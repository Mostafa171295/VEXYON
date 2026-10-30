using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
namespace VEXYON;

/// <summary>قراءة شيت إكسل كجدول منظّم (أسماء الشيتات، محاذاة الأعمدة، التواريخ).</summary>
public static class ExcelReader
{
    public static string Read(string path, string? sheetName, int maxRows, int maxCols = 20)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = SpreadsheetDocument.Open(fs, false);
        var wb = doc.WorkbookPart ?? throw new Exception("ملف الإكسل بايظ أو فاضي");
        var sheets = wb.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();
        if (sheets.Count == 0) return "مفيش شيتات في الملف.";

        var sst = wb.SharedStringTablePart?.SharedStringTable;
        var dateStyles = DateStyles(wb);

        var info = new List<string>();
        foreach (var sh in sheets)
        {
            var dim = "";
            try { dim = ((WorksheetPart)wb.GetPartById(sh.Id!.Value!)).Worksheet.SheetDimension?.Reference?.Value ?? ""; } catch { }
            info.Add(sh.Name?.Value + (dim.Length > 0 ? $" ({dim})" : ""));
        }

        var target = string.IsNullOrWhiteSpace(sheetName)
            ? sheets[0]
            : sheets.FirstOrDefault(x => string.Equals(x.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase));
        if (target == null) return "الشيت مش موجود. الشيتات المتاحة: " + string.Join("، ", info);

        var part = (WorksheetPart)wb.GetPartById(target.Id!.Value!);
        var table = new List<string[]>();
        int width = 0;
        foreach (var row in part.Worksheet.Descendants<Row>())
        {
            if (table.Count >= maxRows) break;
            var cells = new Dictionary<int, string>();
            int seq = 0;
            foreach (var c in row.Elements<Cell>())
            {
                int col = ColIndex(c.CellReference?.Value);
                if (col < 0) col = seq;
                seq = col + 1;
                if (col >= maxCols) continue;
                cells[col] = CellText(c, sst, dateStyles);
            }
            if (cells.Count == 0 || cells.Values.All(string.IsNullOrWhiteSpace)) continue;
            int w = cells.Keys.Max() + 1;
            width = Math.Max(width, w);
            var arr = new string[w];
            for (int i = 0; i < w; i++) arr[i] = cells.TryGetValue(i, out var v) ? v : "";
            table.Add(arr);
        }

        var sb = new StringBuilder();
        sb.AppendLine("الشيتات: " + string.Join("، ", info));
        sb.AppendLine($"الشيت المعروض: {target.Name?.Value} (أول {table.Count} صف)");
        sb.AppendLine();
        if (table.Count == 0) { sb.AppendLine("(الشيت فاضي)"); return sb.ToString(); }

        string At(string[] r, int i) => i < r.Length ? Clean(r[i]) : "";
        sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => At(table[0], i))) + " |");
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", width)));
        foreach (var r in table.Skip(1))
            sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => At(r, i))) + " |");
        return sb.ToString();
    }

    static string Clean(string s)
    {
        s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    static int ColIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;
        int n = 0;
        foreach (var ch in cellRef)
        {
            if (ch >= 'A' && ch <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else if (ch >= 'a' && ch <= 'z') n = n * 26 + (ch - 'a' + 1);
            else break;
        }
        return n - 1;
    }

    static string CellText(Cell c, SharedStringTable? sst, HashSet<uint> dateStyles)
    {
        var v = c.CellValue?.InnerText ?? "";
        if (c.DataType?.Value == CellValues.SharedString && sst != null && int.TryParse(v, out var i))
            return sst.ElementAt(i).InnerText;
        if (c.DataType?.Value == CellValues.InlineString) return c.InlineString?.InnerText ?? "";
        if (c.DataType?.Value == CellValues.Boolean) return v == "1" ? "TRUE" : "FALSE";
        if (c.DataType?.Value == CellValues.Error) return "#ERR";
        if (c.StyleIndex?.Value is uint si && dateStyles.Contains(si) &&
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            try { return DateTime.FromOADate(d).ToString("yyyy-MM-dd"); } catch { }
        }
        return v;
    }

    static HashSet<uint> DateStyles(WorkbookPart wb)
    {
        var set = new HashSet<uint>();
        var ss = wb.WorkbookStylesPart?.Stylesheet;
        if (ss?.CellFormats == null) return set;
        var custom = new Dictionary<uint, string>();
        if (ss.NumberingFormats != null)
            foreach (var nf in ss.NumberingFormats.Elements<NumberingFormat>())
                if (nf.NumberFormatId?.Value is uint id) custom[id] = nf.FormatCode?.Value ?? "";
        uint idx = 0;
        foreach (var xf in ss.CellFormats.Elements<CellFormat>())
        {
            uint id = xf.NumberFormatId?.Value ?? 0;
            bool date = (id >= 14 && id <= 22) || (id >= 27 && id <= 36) || (id >= 45 && id <= 47) || (id >= 50 && id <= 58);
            if (!date && custom.TryGetValue(id, out var code))
            {
                var stripped = Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");
                date = Regex.IsMatch(stripped, "[dmyh]", RegexOptions.IgnoreCase) && !stripped.Contains('0') && !stripped.Contains('#');
            }
            if (date) set.Add(idx);
            idx++;
        }
        return set;
    }
}
