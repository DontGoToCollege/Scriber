using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Scriber;

/// <summary>A piece of row text with one look. Null means "as the row is" (the row's own style).</summary>
public sealed class TextRun
{
    public string Text { get; set; } = "";
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    public string? Font { get; set; }
    public double? Size { get; set; }
    public string? Color { get; set; }

    public TextRun With(string text)
    {
        var r = (TextRun)MemberwiseClone();
        r.Text = text;
        return r;
    }

    public bool SameFormat(TextRun o) => Bold == o.Bold && Italic == o.Italic && Underline == o.Underline &&
                                         Font == o.Font && Size == o.Size && Color == o.Color;

    /// <summary>Drops empty runs and joins neighbours that look the same.</summary>
    public static List<TextRun> Merge(IEnumerable<TextRun> runs)
    {
        var list = new List<TextRun>();
        foreach (var r in runs)
        {
            if (string.IsNullOrEmpty(r.Text)) continue;
            if (list.Count > 0 && list[^1].SameFormat(r)) list[^1] = list[^1].With(list[^1].Text + r.Text);
            else list.Add(r);
        }
        return list;
    }

    /// <summary>Splits runs at a character offset in their joined text.</summary>
    public static (List<TextRun> before, List<TextRun> after) Split(List<TextRun> runs, int at)
    {
        var before = new List<TextRun>();
        var after = new List<TextRun>();
        int pos = 0;
        foreach (var r in runs)
        {
            int cut = Math.Clamp(at - pos, 0, r.Text.Length);
            if (cut > 0) before.Add(r.With(r.Text[..cut]));
            if (cut < r.Text.Length) after.Add(r.With(r.Text[cut..]));
            pos += r.Text.Length;
        }
        return (before, after);
    }
}

public sealed class Block
{
    public string Id { get; set; } = Doc.NewId();
    public int Level { get; set; }
    /// <summary>none, bullet or number.</summary>
    public string Type { get; set; } = "none";
    public bool Collapsed { get; set; }
    public List<TextRun> Runs { get; set; } = new();
    /// <summary>Whole-row look as CSS properties, the same shape as 2.x: font-family, font-size ("18px"), color,
    /// font-weight ("700"), font-style ("italic"), text-decoration ("underline").</summary>
    public Dictionary<string, string>? RowStyle { get; set; }
    /// <summary>Only in 2.x files (row text as HTML); turned into Runs when the file is read.</summary>
    public string? Html { get; set; }

    [JsonIgnore] public string Text => string.Concat(Runs.Select(r => r.Text));
}

public class Message
{
    public string Id { get; set; } = Doc.NewId();
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public List<HistoryEntry> History { get; set; } = new();
}

public sealed class HistoryEntry
{
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset ModifiedAt { get; set; }
}

public sealed class Comment : Message
{
    public string BlockId { get; set; } = "";
    public bool Resolved { get; set; }
    public List<Message> Replies { get; set; } = new();
    /// <summary>1.x name for ModifiedAt; only read.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class SortSpec
{
    public string Key { get; set; } = "row";
    public string Dir { get; set; } = "asc";
}

/// <summary>One outline: what a .scriber file holds. The reviewer's name is never stored here.</summary>
public sealed class Doc
{
    public string App { get; set; } = AppInfo.Name;
    public string Version { get; set; } = AppInfo.Version;
    public string Title { get; set; } = "Untitled outline";
    public bool ShowNumbers { get; set; }
    /// <summary>1.x name for ShowNumbers; only read.</summary>
    public bool? ShowRowNumbers { get; set; }
    public bool ColoredRows { get; set; } = true;
    public double RowColorIntensity { get; set; } = 55;
    public string Colorway { get; set; } = "rainbow";
    public string LastTextColor { get; set; } = "#000000";
    public SortSpec Sort { get; set; } = new();
    public List<Block> Blocks { get; set; } = new();
    public List<Comment> Comments { get; set; } = new();

    public static string NewId() => Guid.NewGuid().ToString();
    public static Doc New() => new() { Blocks = { new Block() } };
}

/// <summary>Reading, writing, importing and merging outline files.</summary>
public static class DocFile
{
    static readonly JsonSerializerOptions Pretty = Options(true), Compact = Options(false);

    static JsonSerializerOptions Options(bool indent) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indent,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new LenientDate() },
    };

    public static string ToJson(Doc d, bool pretty = false) => JsonSerializer.Serialize(d, pretty ? Pretty : Compact);

    public static Doc Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads a .scriber or .notepack (JSON), or a 1.x/2.x saved page (.html with the outline in
    /// &lt;script id="state"&gt;). The HTML is only searched for that JSON, never shown.</summary>
    public static Doc Parse(string text)
    {
        var json = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!json.StartsWith('{'))
        {
            var m = Regex.Match(text, @"<script[^>]*\bid\s*=\s*[""']?(?:state|embeddedState)\b[^>]*>(.*?)</script>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!m.Success) throw new InvalidDataException("There is no outline in this file.");
            json = m.Groups[1].Value.Trim();
        }
        Doc? d;
        try
        {
            d = JsonSerializer.Deserialize<Doc>(json, Compact);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This is not a Scriber outline (" + ex.Message + ").");
        }
        if (d?.Blocks == null) throw new InvalidDataException("There is no outline in this file.");
        return Normalize(d);
    }

    /// <summary>Writes beside the target first, so a failed save never leaves half a file.</summary>
    public static void Save(Doc d, string path)
    {
        var tmp = path + ".saving";
        File.WriteAllText(tmp, ToJson(d, true));
        File.Move(tmp, path, true);
    }

    public static Doc FromJson(string json) => Normalize(JsonSerializer.Deserialize<Doc>(json, Compact)!);

    static readonly string[] StyleKeys = { "font-family", "font-size", "color", "font-weight", "font-style", "text-decoration" };

    static Doc Normalize(Doc d)
    {
        d.App = AppInfo.Name;
        d.Version = AppInfo.Version;
        d.Title ??= "Untitled outline";
        if (d.ShowRowNumbers == true) d.ShowNumbers = true;
        d.ShowRowNumbers = null;
        if (d.Colorway is not ("rainbow" or "neon")) d.Colorway = "rainbow";
        d.RowColorIntensity = double.IsFinite(d.RowColorIntensity) ? Math.Clamp(d.RowColorIntensity, 15, 100) : 55;
        d.LastTextColor = Hex.Parse(d.LastTextColor) ?? "#000000";
        d.Sort ??= new();
        if (d.Sort.Key is not ("row" or "modified" or "created")) d.Sort.Key = "row";
        if (d.Sort.Dir is not ("asc" or "desc")) d.Sort.Dir = "asc";

        var ids = new HashSet<string>();
        d.Blocks = (d.Blocks ?? new()).Where(b => b != null).ToList();
        foreach (var b in d.Blocks)
        {
            if (string.IsNullOrEmpty(b.Id) || !ids.Add(b.Id)) ids.Add(b.Id = Doc.NewId());
            b.Level = Math.Clamp(b.Level, 0, 20);
            if (b.Type is not ("bullet" or "number")) b.Type = "none";
            b.Runs = b.Html != null && (b.Runs == null || b.Runs.Count == 0) ? Html.ToRuns(b.Html) : TextRun.Merge(b.Runs ?? new());
            b.Html = null;
            if (b.RowStyle != null)
            {
                b.RowStyle = b.RowStyle.Where(kv => StyleKeys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                if (b.RowStyle.Count == 0) b.RowStyle = null;
            }
        }
        if (d.Blocks.Count == 0) d.Blocks.Add(new Block());

        d.Comments = (d.Comments ?? new()).Where(c => c != null).ToList();
        foreach (var c in d.Comments)
        {
            if (c.ModifiedAt == default && c.UpdatedAt is { } u) c.ModifiedAt = u;
            c.UpdatedAt = null;
            c.BlockId ??= "";
            Fix(c);
            c.Replies = (c.Replies ?? new()).Where(r => r != null).ToList();
            c.Replies.ForEach(Fix);
        }
        return d;

        static void Fix(Message m)
        {
            if (string.IsNullOrEmpty(m.Id)) m.Id = Doc.NewId();
            m.Author ??= "";
            m.Text ??= "";
            if (m.ModifiedAt == default) m.ModifiedAt = m.CreatedAt;
            m.History = (m.History ?? new()).Where(h => h != null).ToList();
            foreach (var h in m.History)
            {
                h.Author ??= "";
                h.Text ??= "";
            }
        }
    }

    public readonly record struct MergeResult(int Comments, int Replies, int Updates)
    {
        public bool Any => Comments + Replies + Updates > 0;
    }

    /// <summary>Adds a returned review copy's new comments and replies to <paramref name="into"/>. Matched by id; an
    /// edited message keeps the newer version (or the one whose history holds the other's text); resolved follows
    /// the newer thread. Row text in the copy is ignored: reviewers comment, the author writes.
    /// shortcut: no record of deleted threads, so a thread deleted here comes back if the copy still has it.</summary>
    public static MergeResult Merge(Doc into, Doc from)
    {
        int comments = 0, replies = 0, updates = 0;
        foreach (var theirs in from.Comments)
        {
            var mine = into.Comments.FirstOrDefault(c => c.Id == theirs.Id);
            if (mine == null)
            {
                into.Comments.Add(theirs);
                comments++;
                continue;
            }
            bool theirsNewer = theirs.ModifiedAt > mine.ModifiedAt;
            if (TakeNewer(mine, theirs)) updates++;
            foreach (var r in theirs.Replies)
            {
                var mr = mine.Replies.FirstOrDefault(x => x.Id == r.Id);
                if (mr == null)
                {
                    mine.Replies.Add(r);
                    replies++;
                }
                else if (TakeNewer(mr, r)) updates++;
            }
            mine.Replies = mine.Replies.OrderBy(r => r.CreatedAt).ToList();
            if (theirsNewer)
            {
                if (mine.Resolved != theirs.Resolved) updates++;
                mine.Resolved = theirs.Resolved;
                mine.ModifiedAt = theirs.ModifiedAt;
            }
        }
        return new(comments, replies, updates);
    }

    static bool TakeNewer(Message mine, Message theirs)
    {
        var history = mine.History.Concat(theirs.History)
            .GroupBy(h => (h.ModifiedAt, h.Text)).Select(g => g.First()).OrderBy(h => h.ModifiedAt).ToList();
        bool differ = mine.Text != theirs.Text;
        bool take = differ && (theirs.History.Any(h => h.Text == mine.Text) ? true
            : mine.History.Any(h => h.Text == theirs.Text) ? false
            : theirs.ModifiedAt > mine.ModifiedAt);
        if (take)
        {
            if (!history.Any(h => h.Text == mine.Text))
                history.Add(new HistoryEntry { Author = mine.Author, Text = mine.Text, ModifiedAt = mine.ModifiedAt });
            mine.Author = theirs.Author;
            mine.Text = theirs.Text;
            if (theirs.ModifiedAt > mine.ModifiedAt) mine.ModifiedAt = theirs.ModifiedAt;
        }
        mine.History = history.OrderBy(h => h.ModifiedAt).ToList();
        return take;
    }

    /// <summary>Dates from older files can be missing, null, numbers or odd strings; none of that should stop a file opening.</summary>
    sealed class LenientDate : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            if (reader.TokenType == JsonTokenType.String && DateTimeOffset.TryParse(reader.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
                return d;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            reader.Skip();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions o) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }
}

/// <summary>Colors as "#RRGGBB".</summary>
public static class Hex
{
    public static string? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var m = Regex.Match(s, @"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
        if (m.Success)
            return "#" + string.Concat(Enumerable.Range(1, 3).Select(i => Math.Min(255, int.Parse(m.Groups[i].Value)).ToString("X2")));
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s);
            return Of(c);
        }
        catch
        {
            return null;
        }
    }

    public static string Of(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
