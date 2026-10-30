using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
namespace VEXYON;

public record Hit(long Id, string Path, string Text);

public class Db
{
    readonly string _cs;
    readonly object _lock = new();
    List<(long id, string path, string text, float[] emb)>? _cache;

    public Db(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _cs = $"Data Source={file};Default Timeout=30";
        using var c = Open();
        Exec(c, null, "PRAGMA journal_mode=WAL;");
        Exec(c, null, "CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY, mtime INTEGER, size INTEGER);");
        Exec(c, null, "CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT, idx INTEGER, text TEXT, emb BLOB);");
        Exec(c, null, "CREATE INDEX IF NOT EXISTS ix_chunks_path ON chunks(path);");
        Exec(c, null, "CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(text, chunk_id UNINDEXED, path UNINDEXED);");
    }

    SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    void Invalidate() { lock (_lock) _cache = null; }

    public bool IsUnchanged(string path, long mtime, long size)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE path=$p AND mtime=$m AND size=$s";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$m", mtime);
        cmd.Parameters.AddWithValue("$s", size);
        return cmd.ExecuteScalar() != null;
    }

    public void MarkFile(string path, long mtime, long size)
    {
        using var c = Open();
        Exec(c, null, "INSERT OR REPLACE INTO files VALUES($p,$m,$s)", ("$p", path), ("$m", mtime), ("$s", size));
    }

    public List<string> KnownPaths()
    {
        var list = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path FROM files";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void RemoveFile(string path)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, tx, "DELETE FROM fts WHERE path=$p", ("$p", path));
        Exec(c, tx, "DELETE FROM chunks WHERE path=$p", ("$p", path));
        Exec(c, tx, "DELETE FROM files WHERE path=$p", ("$p", path));
        tx.Commit();
        Invalidate();
    }

    public void Clear()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, tx, "DELETE FROM fts");
        Exec(c, tx, "DELETE FROM chunks");
        Exec(c, tx, "DELETE FROM files");
        tx.Commit();
        Invalidate();
    }

    public void AddChunks(string path, IList<string> texts, IList<float[]> embs)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        for (int i = 0; i < texts.Count; i++)
        {
            var bytes = new byte[embs[i].Length * 4];
            Buffer.BlockCopy(embs[i], 0, bytes, 0, bytes.Length);
            Exec(c, tx, "INSERT INTO chunks(path,idx,text,emb) VALUES($p,$i,$t,$e)",
                ("$p", path), ("$i", i), ("$t", texts[i]), ("$e", bytes));
            long id;
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT last_insert_rowid()";
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            Exec(c, tx, "INSERT INTO fts(text,chunk_id,path) VALUES($t,$id,$p)",
                ("$t", texts[i]), ("$id", id), ("$p", path));
        }
        tx.Commit();
        Invalidate();
    }

    public (int files, int chunks) Stats()
    {
        using var c = Open();
        using var a = c.CreateCommand();
        a.CommandText = "SELECT COUNT(*) FROM files";
        using var b = c.CreateCommand();
        b.CommandText = "SELECT COUNT(*) FROM chunks";
        return (Convert.ToInt32(a.ExecuteScalar()), Convert.ToInt32(b.ExecuteScalar()));
    }

    List<(long id, string path, string text, float[] emb)> LoadAll()
    {
        var list = new List<(long, string, string, float[])>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,path,text,emb FROM chunks";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var b = r.GetFieldValue<byte[]>(3);
            var f = new float[b.Length / 4];
            Buffer.BlockCopy(b, 0, f, 0, b.Length);
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), f));
        }
        return list;
    }

    public List<Hit> VectorSearch(float[] q, int k)
    {
        List<(long id, string path, string text, float[] emb)> cache;
        lock (_lock) { _cache ??= LoadAll(); cache = _cache; }
        return cache
            .Where(x => x.emb.Length == q.Length)
            .Select(x => (score: Dot(x.emb, q), x))
            .OrderByDescending(t => t.score)
            .Take(k)
            .Select(t => new Hit(t.x.id, t.x.path, t.x.text))
            .ToList();
    }

    static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public List<Hit> KeywordSearch(string q, int k)
    {
        var words = Regex.Matches(q, @"[\p{L}\p{N}_]{2,}").Select(m => m.Value).Distinct().Take(12).ToList();
        var list = new List<Hit>();
        if (words.Count == 0) return list;
        var match = string.Join(" OR ", words.Select(w => "\"" + w + "\""));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT chunk_id, path, text FROM fts WHERE fts MATCH $q ORDER BY rank LIMIT $k";
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$k", k);
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new Hit(Convert.ToInt64(r.GetValue(0)), r.GetString(1), r.GetString(2)));
        }
        catch (SqliteException) { }
        return list;
    }
}

public class Search
{
    readonly Db _db; readonly OllamaClient _ol; readonly Settings _s;
    public Search(Db db, OllamaClient ol, Settings s) { _db = db; _ol = ol; _s = s; }

    public async Task<List<Hit>> Query(string q, int k, CancellationToken ct)
    {
        var vec = new List<Hit>();
        try
        {
            var e = (await _ol.Embed(_s.EmbedModel, new[] { q }, ct))[0];
            vec = await Task.Run(() => _db.VectorSearch(e, 10), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        var kw = await Task.Run(() => _db.KeywordSearch(q, 10), ct);

        var score = new Dictionary<long, double>();
        var byId = new Dictionary<long, Hit>();
        void Add(List<Hit> l)
        {
            for (int r = 0; r < l.Count; r++)
            {
                byId[l[r].Id] = l[r];
                score[l[r].Id] = score.GetValueOrDefault(l[r].Id) + 1.0 / (60 + r);
            }
        }
        Add(vec); Add(kw);
        return score.OrderByDescending(p => p.Value).Take(k).Select(p => byId[p.Key]).ToList();
    }
}
