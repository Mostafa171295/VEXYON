using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace VEXYON;

public static class Extractor
{
    public static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".ini", ".yaml", ".yml",
        ".cs", ".py", ".cpp", ".c", ".h", ".js", ".ts", ".xaml", ".sql", ".bat", ".ps1", ".lsp",
        ".docx", ".xlsx", ".pdf"
    };

    static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public static string Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".pdf": return Pdf(path);
            case ".docx": return Docx(path);
            case ".xlsx": return Xlsx(path);
            default:
                using (var fs = OpenShared(path))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                    return sr.ReadToEnd();
        }
    }

    static string Pdf(string path)
    {
        var sb = new StringBuilder();
        using var fs = OpenShared(path);
        using var doc = PdfDocument.Open(fs);
        for (int i = 1; i <= doc.NumberOfPages; i++)
        {
            try { sb.AppendLine(doc.GetPage(i).Text); }
            catch { /* صفحة بايظة أو خط ناقص: نتخطاها ونكمّل */ }
            if (sb.Length > 1_200_000) break; // كتاب ضخم: كفاية أول جزء (الفهرسة ليها حد أجزاء أصلاً)
        }
        return sb.ToString();
    }

    static string Docx(string path)
    {
        var sb = new StringBuilder();
        using var fs = OpenShared(path);
        using var d = WordprocessingDocument.Open(fs, false);
        var body = d.MainDocumentPart?.Document?.Body;
        if (body == null) return "";
        foreach (var p in body.Descendants<Paragraph>()) sb.AppendLine(p.InnerText);
        return sb.ToString();
    }

    static string Xlsx(string path)
    {
        var sb = new StringBuilder();
        using var fs = OpenShared(path);
        using var d = SpreadsheetDocument.Open(fs, false);
        var wb = d.WorkbookPart;
        if (wb == null) return "";
        var sst = wb.SharedStringTablePart?.SharedStringTable;
        foreach (var ws in wb.WorksheetParts)
        {
            foreach (var row in ws.Worksheet.Descendants<Row>())
            {
                var vals = new List<string>();
                foreach (var c in row.Elements<Cell>())
                {
                    string v = c.CellValue?.InnerText ?? c.InnerText;
                    if (c.DataType?.Value == CellValues.SharedString && sst != null &&
                        int.TryParse(v, out var i))
                        v = sst.ElementAt(i).InnerText;
                    if (!string.IsNullOrWhiteSpace(v)) vals.Add(v);
                }
                if (vals.Count > 0) sb.AppendLine(string.Join(" | ", vals));
            }
        }
        return sb.ToString();
    }
}

public static class Chunker
{
    public static List<string> Split(string text, int size = 1000, int overlap = 150)
    {
        var list = new List<string>();
        text = text.Trim();
        int pos = 0;
        while (pos < text.Length)
        {
            int end = Math.Min(pos + size, text.Length);
            if (end < text.Length)
            {
                int br = text.LastIndexOfAny(new[] { '\n', '.', '؟', ' ' }, end - 1, Math.Min(200, end - pos));
                if (br > pos) end = br + 1;
            }
            var piece = text.Substring(pos, end - pos).Trim();
            if (piece.Length > 20) list.Add(piece);
            if (end >= text.Length) break;
            pos = Math.Max(end - overlap, pos + 1);
        }
        return list;
    }
}
