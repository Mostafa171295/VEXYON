using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
namespace VEXYON;

public class ChatMsg
{
    public string Role { get; set; } = "";   // "user" | "assistant"
    public string Text { get; set; } = "";
}

public class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<ChatMsg> Messages { get; set; } = new();
}

/// <summary>حفظ المحادثات في %AppData%\VEXYON\chats (ملف JSON لكل محادثة).</summary>
public static class ChatStore
{
    public static string Dir => Path.Combine(Settings.DataDir, "chats");
    static string PathOf(string id) => Path.Combine(Dir, id + ".json");

    static readonly JsonSerializerOptions Opt = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static List<Conversation> All()
    {
        var list = new List<Conversation>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    var c = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(f, Encoding.UTF8), Opt);
                    if (c != null && c.Messages.Count > 0) list.Add(c);
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(c => c.Updated).ToList();
    }

    public static void Save(Conversation c)
    {
        if (c.Messages.Count == 0) return;
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathOf(c.Id), JsonSerializer.Serialize(c, Opt), new UTF8Encoding(false));
    }

    public static void Delete(string id)
    {
        try { File.Delete(PathOf(id)); } catch { }
    }
}
