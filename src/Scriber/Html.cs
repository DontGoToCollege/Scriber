using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Scriber;

/// <summary>Turns HTML into row text: rows from 2.x files, and lists pasted from Word or web pages. HTML is only
/// read here, never shown. Keeps bold, italic, underline, font, size and color; drops everything else.</summary>
public static class Html
{
    public sealed class Item
    {
        public List<TextRun> Runs = new();
        public int Level;
        public string Type = "none";
        public string Text => string.Concat(Runs.Select(r => r.Text));
    }

    sealed record Fmt(bool? Bold, bool? Italic, bool? Underline, string? Font, double? Size, string? Color)
    {
        public static readonly Fmt None = new(null, null, null, null, null, null);
    }

    enum Kind { Open, Close, Text, WordMarker }

    readonly record struct Ev(Kind Kind, string Name, string Attrs, string Text, Fmt Fmt);

    // Word marks each list paragraph's bullet or number with <![if !supportLists]>...<![endif]>.
    static readonly Regex Tok = new(
        @"<!--\[if !supportLists\]-->(?<wl>.*?)<!--\[endif\]-->|<!\[if !supportLists\]>(?<wl>.*?)<!\[endif\]>|<!--.*?-->|<![^>]*>" +
        @"|<(?<close>/?)(?<name>[a-zA-Z][a-zA-Z0-9:]*)(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)>|(?<text>[^<]+)|<",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
        { "br", "img", "hr", "meta", "link", "input", "wbr", "col", "area", "base", "source" };
    static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "style", "script", "head", "title", "xml" };
    static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "tr", "blockquote", "pre" };

    static IEnumerable<Ev> Read(string html)
    {
        var stack = new List<(string name, Fmt fmt)>();
        int hidden = 0;
        Fmt Cur() => stack.Count > 0 ? stack[^1].fmt : Fmt.None;
        foreach (Match m in Tok.Matches(html))
        {
            if (m.Groups["wl"].Success)
            {
                if (hidden == 0) yield return new(Kind.WordMarker, "", "", Decode(Regex.Replace(m.Groups["wl"].Value, "<[^>]*>", "")).Trim(), Cur());
                continue;
            }
            if (m.Groups["text"].Success)
            {
                if (hidden == 0) yield return new(Kind.Text, "", "", Decode(m.Groups["text"].Value), Cur());
                continue;
            }
            if (!m.Groups["name"].Success) continue;
            var name = m.Groups["name"].Value.ToLowerInvariant();
            var attrs = m.Groups["attrs"].Value;
            if (m.Groups["close"].Value == "/")
            {
                if (Hidden.Contains(name))
                {
                    hidden = Math.Max(0, hidden - 1);
                    continue;
                }
                int at = stack.FindLastIndex(f => f.name == name);
                if (at >= 0) stack.RemoveRange(at, stack.Count - at);
                if (hidden == 0) yield return new(Kind.Close, name, "", "", Cur());
                continue;
            }
            bool selfClosing = attrs.TrimEnd().EndsWith('/');
            if (Hidden.Contains(name))
            {
                if (!selfClosing) hidden++;
                continue;
            }
            var fmt = Apply(Cur(), name, attrs);
            if (!Void.Contains(name) && !selfClosing) stack.Add((name, fmt));
            if (hidden == 0) yield return new(Kind.Open, name, attrs, "", fmt);
        }
    }

    static string Decode(string raw) => WebUtility.HtmlDecode(Regex.Replace(raw, @"[\r\n\t ]+", " ")).Replace(' ', ' ');

    static string? Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, @"\b" + name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }

    static readonly double[] LegacySizes = { 10, 10, 13, 16, 18, 24, 32, 48 };

    static Fmt Apply(Fmt f, string name, string attrs)
    {
        switch (name)
        {
            case "b" or "strong": f = f with { Bold = true }; break;
            case "i" or "em": f = f with { Italic = true }; break;
            case "u" or "ins": f = f with { Underline = true }; break;
            case "font":
                if (Attr(attrs, "face") is { } face) f = f with { Font = FirstFamily(face) };
                if (Hex.Parse(Attr(attrs, "color")) is { } c) f = f with { Color = c };
                if (int.TryParse(Attr(attrs, "size"), out var s)) f = f with { Size = LegacySizes[Math.Clamp(s, 1, 7)] };
                break;
        }
        if (Attr(attrs, "style") is not { } style) return f;
        foreach (var decl in style.Split(';'))
        {
            int colon = decl.IndexOf(':');
            if (colon < 0) continue;
            var prop = decl[..colon].Trim().ToLowerInvariant();
            var val = decl[(colon + 1)..].Trim().ToLowerInvariant().Replace("!important", "").Trim();
            switch (prop)
            {
                case "font-weight":
                    f = f with { Bold = val is "bold" or "bolder" || (int.TryParse(val, out var w) && w >= 600) };
                    break;
                case "font-style": f = f with { Italic = val is "italic" or "oblique" }; break;
                case "text-decoration" or "text-decoration-line":
                    if (val.Contains("underline")) f = f with { Underline = true };
                    else if (val.Contains("none")) f = f with { Underline = false };
                    break;
                case "font-family":
                    if (FirstFamily(decl[(colon + 1)..]) is { } fam) f = f with { Font = fam };
                    break;
                case "font-size":
                    if (ParseSize(val) is { } px) f = f with { Size = px };
                    break;
                case "color":
                    if (Hex.Parse(val) is { } col) f = f with { Color = col };
                    break;
            }
        }
        return f;
    }

    public static string? FirstFamily(string? s)
    {
        var first = s?.Split(',')[0].Trim().Trim('"', '\'').Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    /// <summary>"18px", "13.5pt" or a bare number, in pixels (WPF units).</summary>
    public static double? ParseSize(string? s)
    {
        var m = Regex.Match(s ?? "", @"^\s*([\d.]+)\s*(px|pt)?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0) return null;
        if (m.Groups[2].Value.Equals("pt", StringComparison.OrdinalIgnoreCase)) v = v * 96 / 72;
        return Math.Round(v * 2) / 2;
    }

    static TextRun Run(string text, Fmt f) => new()
    {
        Text = text, Bold = f.Bold, Italic = f.Italic, Underline = f.Underline, Font = f.Font, Size = f.Size, Color = f.Color,
    };

    /// <summary>One 2.x row (contenteditable HTML) as runs; line breaks become "\n".</summary>
    public static List<TextRun> ToRuns(string html)
    {
        var runs = new List<TextRun>();
        foreach (var e in Read(html))
        {
            if (e.Kind == Kind.Text) runs.Add(Run(e.Text, e.Fmt));
            else if (e.Kind == Kind.Open && e.Name == "br") runs.Add(new TextRun { Text = "\n" });
            else if (e.Kind == Kind.Open && BlockTags.Contains(e.Name) && runs.Count > 0 && !runs[^1].Text.EndsWith('\n'))
                runs.Add(new TextRun { Text = "\n" });
        }
        return Trim(TextRun.Merge(runs));
    }

    /// <summary>A pasted HTML fragment as rows: list items keep their nesting, Word list paragraphs their level.</summary>
    public static List<Item> ToItems(string html)
    {
        var items = new List<Item>();
        var lists = new List<string>();
        Item? cur = null;
        bool inLi = false;
        void Finish()
        {
            if (cur != null)
            {
                cur.Runs = Trim(TextRun.Merge(cur.Runs));
                if (cur.Text.Trim().Length > 0) items.Add(cur);
            }
            cur = null;
            inLi = false;
        }
        int ListLevel() => Math.Max(0, lists.Count - 1);

        foreach (var e in Read(html))
        {
            switch (e.Kind)
            {
                case Kind.WordMarker:
                    if (cur != null) cur.Type = Regex.IsMatch(e.Text, @"\d|^[a-zA-Z]{1,4}[.)]") ? "number" : "bullet";
                    break;
                case Kind.Text:
                    cur ??= new Item { Level = ListLevel() };
                    cur.Runs.Add(Run(e.Text, e.Fmt));
                    break;
                case Kind.Open when e.Name is "ul" or "ol":
                    if (inLi) Finish();
                    lists.Add(e.Name);
                    break;
                case Kind.Open when e.Name == "li":
                    Finish();
                    cur = new Item { Level = ListLevel(), Type = lists.Count > 0 && lists[^1] == "ol" ? "number" : "bullet" };
                    inLi = true;
                    break;
                case Kind.Open when e.Name == "br":
                    cur?.Runs.Add(new TextRun { Text = "\n" });
                    break;
                case Kind.Open when BlockTags.Contains(e.Name) && !inLi:
                    Finish();
                    cur = new Item { Level = ListLevel() };
                    var lv = Regex.Match(e.Attrs, @"mso-list:\s*l\d+\s+level(\d+)", RegexOptions.IgnoreCase);
                    if (lv.Success) cur.Level = int.Parse(lv.Groups[1].Value) - 1;
                    break;
                case Kind.Close when e.Name is "ul" or "ol":
                    if (inLi) Finish();
                    if (lists.Count > 0) lists.RemoveAt(lists.Count - 1);
                    break;
                case Kind.Close when e.Name == "li":
                    Finish();
                    break;
                case Kind.Close when BlockTags.Contains(e.Name) && !inLi:
                    Finish();
                    break;
            }
        }
        Finish();
        return Normalize(items);
    }

    static readonly Regex Bullet = new(@"^([-•*◦▪·‣]|\d+[.)])\s+(.*)$");

    /// <summary>Plain text as rows. One Tab is one level; leading spaces count too (the smallest space indent in
    /// the text is one level), with or without a bullet.</summary>
    public static List<Item> FromPlain(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var leads = lines.Select(l => l[..(l.Length - l.TrimStart(' ', '\t').Length)]).ToList();
        int unit = leads.Select(s => s.Count(c => c == ' ')).Where(n => n > 0).DefaultIfEmpty(2).Min();
        var items = new List<Item>();
        for (int i = 0; i < lines.Count; i++)
        {
            var body = lines[i].Trim();
            var m = Bullet.Match(body);
            items.Add(new Item
            {
                Level = leads[i].Count(c => c == '\t') + leads[i].Count(c => c == ' ') / unit,
                Type = !m.Success ? "none" : char.IsDigit(m.Groups[1].Value[0]) ? "number" : "bullet",
                Runs = { new TextRun { Text = m.Success ? m.Groups[2].Value : body } },
            });
        }
        return Normalize(items);
    }

    /// <summary>The least indented row is level 0 and no row is more than one level deeper than the row above.</summary>
    static List<Item> Normalize(List<Item> items)
    {
        if (items.Count == 0) return items;
        int min = items.Min(x => x.Level);
        for (int i = 0; i < items.Count; i++)
            items[i].Level = i == 0 ? 0 : Math.Clamp(items[i].Level - min, 0, items[i - 1].Level + 1);
        return items;
    }

    static List<TextRun> Trim(List<TextRun> runs)
    {
        while (runs.Count > 0 && runs[0].Text.TrimStart(' ', '\n').Length == 0) runs.RemoveAt(0);
        while (runs.Count > 0 && runs[^1].Text.TrimEnd(' ', '\n').Length == 0) runs.RemoveAt(runs.Count - 1);
        if (runs.Count > 0)
        {
            runs[0] = runs[0].With(runs[0].Text.TrimStart(' ', '\n'));
            runs[^1] = runs[^1].With(runs[^1].Text.TrimEnd(' ', '\n'));
        }
        return runs;
    }

    /// <summary>The fragment part of the clipboard's "HTML Format".</summary>
    public static string Fragment(string cfHtml)
    {
        int s = cfHtml.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        int e = cfHtml.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (s >= 0 && e > s) return cfHtml[(s + 20)..e];
        int b = cfHtml.IndexOf('<');
        return b >= 0 ? cfHtml[b..] : cfHtml;
    }

    /// <summary>Clipboard HTML is UTF-8 but sometimes arrives read as Latin-1 ("â€™"); undo that when it happened.</summary>
    public static string FixUtf8(string s)
    {
        if (s.Any(c => c > 255)) return s;
        try
        {
            return new UTF8Encoding(false, true).GetString(Encoding.Latin1.GetBytes(s));
        }
        catch
        {
            return s;
        }
    }
}
