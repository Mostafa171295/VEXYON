using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace VEXYON;

public class Indexer
{
    readonly Db _db; readonly OllamaClient _ol; readonly Settings _s;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly List<FileSystemWatcher> _ws = new();
    System.Threading.Timer? _debounce;
    volatile bool _pending;
    public event Action<string>? Log;
    public event Action<int, int, string>? Progress;
    public bool Busy => _gate.CurrentCount == 0;

    public Indexer(Db db, OllamaClient ol, Settings s) { _db = db; _ol = ol; _s = s; }

    HashSet<string> SkipSet() =>
        new(_s.SkipNames.Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);

    // ---------- watching ----------
    public void StartWatching()
    {
        foreach (var w in _ws) { try { w.Dispose(); } catch { } }
        _ws.Clear();

        _debounce ??= new System.Threading.Timer(async _ =>
        {
            try { await Scan(CancellationToken.None); }
            catch (Exception ex) { Log?.Invoke("خطأ فهرسة: " + ex.Message); }
        }, null, Timeout.Infinite, Timeout.Infinite);

        foreach (var root in _s.IndexFolders.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                w.Created += (_, ev) => OnFs(ev.FullPath);
                w.Changed += (_, ev) => OnFs(ev.FullPath);
                w.Deleted += (_, ev) => OnFs(ev.FullPath);
                w.Renamed += (_, ev) => { OnFs(ev.OldFullPath); OnFs(ev.FullPath); };
                w.Error += (_, _) => Kick();
                _ws.Add(w);
            }
            catch (Exception ex) { Log?.Invoke($"تعذّرت المراقبة على {root}: {ex.Message}"); }
        }
    }

    bool Relevant(string path)
    {
        try
        {
            var ext = Path.GetExtension(path);
            if (ext.Length > 0 && !Extractor.Exts.Contains(ext)) return false;
            var skip = SkipSet();
            foreach (var seg in path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                if (skip.Contains(seg)) return false;
            return true;
        }
        catch { return false; }
    }

    void OnFs(string path) { if (Relevant(path)) Kick(); }
    void Kick() { _pending = true; _debounce?.Change(3000, Timeout.Infinite); }

    // ---------- scanning ----------
    public async Task Scan(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            do { _pending = false; await ScanOnce(ct); }
            while (_pending && !ct.IsCancellationRequested);
        }
        finally { _gate.Release(); }
    }

    bool InOfflineRoot(string path) =>
        _s.IndexFolders.Any(r => !Directory.Exists(r) &&
            path.StartsWith(r.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    static List<string> Collect(IEnumerable<string> roots, HashSet<string> skip, CancellationToken ct)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            foreach (var f in Walk(root, skip, ct))
                if (seen.Add(f)) result.Add(f);
        return result;
    }

    /// <summary>مشي آمن على الفولدرات: بيتخطى المحمي والمخفي والـ junctions وأي خطأ صلاحيات.</summary>
    static IEnumerable<string> Walk(string root, HashSet<string> skip, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = Array.Empty<string>(); }
            foreach (var f in files)
                if (Extractor.Exts.Contains(Path.GetExtension(f))) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var d in subs)
            {
                if (skip.Contains(Path.GetFileName(d))) continue;
                try
                {
                    var a = File.GetAttributes(d);
                    if ((a & FileAttributes.ReparsePoint) != 0) continue;
                    if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                }
                catch { continue; }
                stack.Push(d);
            }
        }
    }

    async Task ScanOnce(CancellationToken ct)
    {
        var roots = _s.IndexFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_s.IndexFolders.Count == 0)
        {
            foreach (var p in _db.KnownPaths()) _db.RemoveFile(p);
            Log?.Invoke("مفيش فولدرات للفهرسة - ضيف فولدر من تبويب المكتبة");
            return;
        }
        if (roots.Count == 0) { Log?.Invoke("الفولدرات المحددة مش موجودة دلوقتي (درايف مفصول؟)"); return; }
        if (!await _ol.IsUp()) { Log?.Invoke("Ollama مش شغال - الفهرسة اتأجلت"); return; }
        var installed = await _ol.ListModels();
        if (!OllamaClient.HasModel(installed, _s.EmbedModel))
        {
            Log?.Invoke("نموذج الفهرسة مش منزّل - اضغط تجهيز النماذج من الإعدادات");
            return;
        }

        Log?.Invoke("بدأ مسح الفولدرات...");
        Progress?.Invoke(0, 1, "جاري مسح الفولدرات...");
        var skip = SkipSet();
        var files = await Task.Run(() => Collect(roots, skip, ct), ct);
        Log?.Invoke($"لقيت {files.Count} ملف مدعوم");

        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _db.KnownPaths())
            if (!present.Contains(gone) && !InOfflineRoot(gone)) _db.RemoveFile(gone);

        int i = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            Progress?.Invoke(i, files.Count, Path.GetFileName(f));
            FileInfo fi;
            try { fi = new FileInfo(f); if (fi.Length > 100_000_000) continue; }
            catch { continue; }
            if (_db.IsUnchanged(f, fi.LastWriteTimeUtc.Ticks, fi.Length)) continue;
            await IndexFile(f, fi, ct);
        }
        Log?.Invoke("اكتملت الفهرسة");
    }

    async Task IndexFile(string f, FileInfo fi, CancellationToken ct)
    {
        try
        {
            var text = await Task.Run(() => Extractor.Extract(f), ct);
            _db.RemoveFile(f);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var name = Path.GetFileName(f);
                var all = Chunker.Split(text);
                int max = Math.Max(20, _s.MaxChunksPerFile);
                if (all.Count > max)
                {
                    Log?.Invoke($"{name}: ملف كبير - اتفهرس أول {max} جزء بس");
                    all = all.Take(max).ToList();
                }
                var chunks = all.Select(c => $"[{name}] {c}").ToList();
                var embs = new List<float[]>();
                for (int b = 0; b < chunks.Count; b += 8)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = chunks.Skip(b).Take(8).ToList();
                    embs.AddRange(await _ol.Embed(_s.EmbedModel, batch, ct));
                }
                if (chunks.Count > 0) _db.AddChunks(f, chunks, embs);
            }
            _db.MarkFile(f, fi.LastWriteTimeUtc.Ticks, fi.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log?.Invoke($"تعذّرت فهرسة {Path.GetFileName(f)}: {ex.Message}"); }
    }
}
