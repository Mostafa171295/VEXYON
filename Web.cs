using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace VEXYON;

/// <summary>بحث وقراءة صفحات من الإنترنت (اختياري، بيشتغل بس لما المستخدم يفعّله). من غير مفتاح API.</summary>
public static class Web
{
    static readonly HttpClient Http = Make();

    static HttpClient Make()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar,en;q=0.8");
        return h;
    }

    static string Clean(string html)
    {
        var t = Regex.Replace(html, @"<(script|style|noscript|svg|head)\b.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<(br|/p|/div|/li|/h\d|/tr)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, @"[ \t\u00A0]+", " ");
        t = Regex.Replace(t, @"\s*\n\s*", "\n");
        return t.Trim();
    }

    public static async Task<string> Search(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return "الاستعلام فاضي.";
        try
        {
            var html = await Http.GetStringAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), ct);
            var links = Regex.Matches(html, "<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            var snips = Regex.Matches(html, "<a[^>]*class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < links.Count && n < 6; i++)
            {
                var href = WebUtility.HtmlDecode(links[i].Groups[1].Value);
                var m = Regex.Match(href, @"uddg=([^&]+)");
                var url = m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : href;
                if (url.StartsWith("//")) url = "https:" + url;
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (url.Contains("duckduckgo.com/y.js")) continue; // إعلان
                var title = Clean(links[i].Groups[2].Value);
                var snip = i < snips.Count ? Clean(snips[i].Groups[1].Value) : "";
                sb.AppendLine($"{++n}. {title}\n   {url}\n   {snip}\n");
            }
            return n == 0 ? "مفيش نتايج من البحث (يمكن النت واقف أو المحرك رفض الطلب)." : sb.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return "فشل البحث (تأكد إن النت شغال): " + ex.Message; }
    }

    static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254) ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static async Task<string> Fetch(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            return "الرابط مش صالح (لازم يبدأ بـ http أو https).";
        try
        {
            // حماية: ماينفعش يقرا حاجة من الشبكة الداخلية/الجهاز نفسه
            var ips = await Dns.GetHostAddressesAsync(u.Host, ct);
            if (ips.Any(IsPrivate)) return "ممنوع فتح عناوين داخلية.";
            using var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return $"الموقع رجّع خطأ {(int)resp.StatusCode}.";
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!(type.StartsWith("text/") || type.Contains("json") || type.Contains("xml")))
                return "نوع المحتوى مش نصي (" + type + ").";
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[1_500_000];
            int len = 0, r;
            while (len < buf.Length && (r = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len), ct)) > 0) len += r;
            var html = Encoding.UTF8.GetString(buf, 0, len);
            var text = type.Contains("html") ? Clean(html) : html;
            return text.Length > 4000 ? text[..4000] + "\n...[اتقطع]" : text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return "فشل فتح الصفحة: " + ex.Message; }
    }
}
