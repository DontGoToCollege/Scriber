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
        internal string? WordList;   // Word list paragraph: list id and level ("l0 level1")
        internal double? WordMargin; // and its left margin in points, when it has one
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
        bool inLi = false, heading = false;
        int under = 0; // rows after a heading go one level under it
        void Finish()
        {
            if (cur != null)
            {
                cur.Runs = Trim(TextRun.Merge(cur.Runs));
                if (heading) foreach (var r in cur.Runs) r.Bold ??= true;
                if (cur.Text.Trim().Length > 0) items.Add(cur);
            }
            cur = null;
            inLi = false;
            heading = false;
        }
        int ListLevel() => Math.Max(0, lists.Count - 1) + under;

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
                case Kind.Open when e.Name.Length == 2 && e.Name[0] == 'h' && char.IsDigit(e.Name[1]) && !inLi:
                    Finish();
                    under = e.Name[1] - '0';
                    cur = new Item { Level = under - 1 };
                    heading = true;
                    break;
                case Kind.Open when BlockTags.Contains(e.Name) && !inLi:
                    Finish();
                    cur = new Item { Level = ListLevel() };
                    var lv = Regex.Match(e.Attrs, @"mso-list:\s*(l\d+\s+level(\d+))", RegexOptions.IgnoreCase);
                    if (lv.Success)
                    {
                        cur.Level = int.Parse(lv.Groups[2].Value) - 1 + under;
                        cur.WordList = lv.Groups[1].Value.ToLowerInvariant();
                        cur.WordMargin = Points(Regex.Match(e.Attrs, @"margin-left:\s*(-?[\d.]+)\s*(in|cm|mm|pt|px)", RegexOptions.IgnoreCase));
                    }
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
        // Word's Increase Indent moves a list paragraph right without changing its list level: each 18pt is a level
        // (no margin written: Word's list paragraph default, half an inch)
        foreach (var g in items.Where(x => x.WordList != null).GroupBy(x => x.WordList))
        {
            if (g.All(x => x.WordMargin == null)) continue;
            double min = g.Min(x => x.WordMargin ?? 36);
            foreach (var x in g) x.Level += Math.Max(0, (int)Math.Round(((x.WordMargin ?? 36) - min) / 18));
        }
        return Normalize(items);
    }

    static double? Points(Match m)
    {
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
        return m.Groups[2].Value.ToLowerInvariant() switch { "in" => v * 72, "cm" => v * 72 / 2.54, "mm" => v * 72 / 25.4, "px" => v * 0.75, _ => v };
    }

    // List markers: bullets (with the odd characters PDFs and Word's plain text use), numbers, (1), letters, roman
    // numerals and outline numbers like 1.2.3. A marker alone on its line belongs to the next line.
    static readonly Regex Marker = new(
        @"^(?<m>[-•*+◦▪·‣–►➢✓■□●○§]|o(?=\s)|\(?\d{1,3}[.)]|(?<outline>\d{1,2}(?:\.\d{1,2})+)\.?|\(?[a-zA-Z][.)]|\(?[ivxlcIVXLC]{1,5}[.)])(?:\s+|$)(?<t>.*)$");

    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$");

    /// <summary>Plain text as rows (Notepad, PDFs, Markdown). Tabs and leading spaces set levels (the smallest space
    /// indent in the text is one level). Text without any indents (PDFs, Word's plain text) gets its levels from the
    /// markers instead: each new kind of marker (•, o, ▪ or 1., a., i.) is one level deeper. Lines a PDF wrapped are
    /// joined again. Markdown: # headings, - and 1. lists, **bold** and *italic*.</summary>
    public static List<Item> FromPlain(string text)
    {
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool outlineOk = raw.Count(l => Marker.Match(l.Trim()) is { Success: true } m && m.Groups["outline"].Success) >= 2;
        Match? Mark(string body)
        {
            var m = Marker.Match(body);
            return !m.Success || (m.Groups["outline"].Success && !outlineOk) ? null : m;
        }
        int maxLen = raw.Select(l => l.Trim().Length).DefaultIfEmpty(0).Max();

        // join wrapped lines: a long line, then a line without a marker that carries on its sentence
        var lines = new List<string>();
        string? lastPhysical = null;
        foreach (var l in raw)
        {
            var t = l.Trim();
            if (t.Length == 0)
            {
                lastPhysical = null;
                continue;
            }
            if (lastPhysical != null && lines.Count > 0 && maxLen >= 50 && lastPhysical.Length >= maxLen * 0.7 &&
                Mark(t) == null && !Heading.IsMatch(t) &&
                (!".!?:;".Contains(lastPhysical[^1]) || char.IsLower(t[0])))
            {
                lines[^1] = lines[^1].EndsWith('-') && char.IsLower(t[0]) ? lines[^1] + t : lines[^1] + " " + t;
                lastPhysical = t;
                continue;
            }
            // a marker on a line of its own goes with the next line
            if (lines.Count > 0 && Mark(lines[^1].Trim()) is { } prev && prev.Groups["t"].Value.Length == 0 && Mark(t) == null)
            {
                lines[^1] = lines[^1].TrimEnd() + " " + t;
                lastPhysical = t;
                continue;
            }
            lines.Add(l.TrimEnd());
            lastPhysical = t;
        }

        var leads = lines.Select(l => l[..(l.Length - l.TrimStart(' ', '\t').Length)]).ToList();
        bool indented = leads.Any(s => s.Length > 0);
        int unit = leads.Select(s => s.Count(c => c == ' ')).Where(n => n > 0).DefaultIfEmpty(2).Min();
        var kinds = new List<string>();  // other marker kinds in the order they first appear (levels when there are no indents)
        char lastLetter = '\0';
        int under = 0;                   // after a Markdown heading, rows go under it
        bool textBefore = false;         // a line without a marker came first: lists go under it
        var items = new List<Item>();
        foreach (var (line, lead) in lines.Zip(leads))
        {
            var body = line.Trim();
            if (Heading.Match(body) is { Success: true } h)
            {
                under = h.Groups[1].Length;
                var hi = new Item { Level = under - 1, Runs = Inline(h.Groups[2].Value) };
                foreach (var r in hi.Runs) r.Bold ??= true;
                items.Add(hi);
                textBefore = false;
                continue;
            }
            var m = Mark(body);
            var item = new Item { Runs = Inline(m != null ? m.Groups["t"].Value : body) };
            int level = lead.Count(c => c == '\t') + lead.Count(c => c == ' ') / unit;
            if (m != null)
            {
                var mk = m.Groups["m"].Value.Trim('(', ')', '.');
                string kind;
                if (m.Groups["outline"].Success) kind = "1.1";
                else if (mk.Length > 0 && char.IsDigit(mk[0])) kind = "1";
                else if (mk.Length > 0 && char.IsLetter(mk[0]) && mk != "o")
                {
                    // i, v, x... are roman numerals unless they carry on a list of letters (h, i)
                    bool roman = mk.All(c => "ivxlcIVXLC".Contains(c)) && !(mk.Length == 1 && lastLetter != '\0' && mk[0] == lastLetter + 1);
                    kind = roman ? (char.IsUpper(mk[0]) ? "I" : "i") : (char.IsUpper(mk[0]) ? "A" : "a");
                    if (!roman) lastLetter = mk[0];
                }
                else kind = mk is "" or "·" or "●" ? "•" : mk;
                item.Type = kind is "1" or "1.1" or "a" or "A" or "i" or "I" ? "number" : "bullet";
                if (m.Groups["outline"].Success) level = m.Groups["outline"].Value.Count(c => c == '.');
                else if (!indented)
                {
                    // Word's own sequences (• o ▪ and 1 a i) are fixed levels; other markers go by first appearance
                    int? known = kind switch { "•" or "1" => 0, "o" or "◦" or "a" or "A" => 1, "▪" or "§" or "" or "■" or "i" or "I" => 2, _ => null };
                    if (known == null && !kinds.Contains(kind)) kinds.Add(kind);
                    level = (known ?? kinds.IndexOf(kind)) + (textBefore ? 1 : 0);
                }
            }
            else if (!indented) textBefore = true;
            item.Level = under + level;
            items.Add(item);
        }
        return Normalize(items);
    }

    static readonly Regex Emphasis = new(@"\*\*(?<b>.+?)\*\*|__(?<b>.+?)__|(?<![\w*])\*(?!\s)(?<i>.+?)(?<!\s)\*(?![\w*])|(?<!\w)_(?!\s)(?<i>.+?)(?<!\s)_(?!\w)");

    /// <summary>Markdown **bold** and *italic* as runs; everything else stays as typed.</summary>
    static List<TextRun> Inline(string s)
    {
        var runs = new List<TextRun>();
        int at = 0;
        foreach (Match m in Emphasis.Matches(s))
        {
            if (m.Index > at) runs.Add(new TextRun { Text = s[at..m.Index] });
            runs.Add(m.Groups["b"].Success ? new TextRun { Text = m.Groups["b"].Value, Bold = true } : new TextRun { Text = m.Groups["i"].Value, Italic = true });
            at = m.Index + m.Length;
        }
        if (at < s.Length) runs.Add(new TextRun { Text = s[at..] });
        return TextRun.Merge(runs);
    }

    // ---------- writing: rows on the clipboard for other programs ----------

    /// <summary>Rows as nested HTML lists (Word, Outlook, Google Docs, OneNote, web editors). Rows without a bullet
    /// at the top level are paragraphs; deeper ones are list items without a marker, so they keep their indent.
    /// numbers: the number each numbered row shows in Scriber.</summary>
    public static string ToHtml(IList<Block> rows, IReadOnlyDictionary<string, int> numbers)
    {
        var sb = new StringBuilder();
        var open = new List<(string tag, bool li)>();
        int min = rows.Count > 0 ? rows.Min(b => b.Level) : 0;
        void CloseTo(int depth)
        {
            while (open.Count > depth)
            {
                if (open[^1].li) sb.Append("</li>");
                sb.Append("</").Append(open[^1].tag).Append('>');
                open.RemoveAt(open.Count - 1);
            }
        }
        foreach (var b in rows)
        {
            int level = b.Level - min;
            string content = RowHtml(b);
            if (level == 0 && b.Type == "none")
            {
                CloseTo(0);
                sb.Append("<p>").Append(content).Append("</p>");
                continue;
            }
            string tag = b.Type == "number" ? "ol" : "ul";
            CloseTo(level + 1);
            if (open.Count == level + 1 && open[^1].tag != tag) CloseTo(level); // a numbered list after a bulleted one
            while (open.Count < level + 1)
            {
                if (open.Count > 0 && !open[^1].li)
                {
                    sb.Append("<li style=\"list-style-type:none\">");
                    open[^1] = (open[^1].tag, true);
                }
                bool last = open.Count == level;
                string t = last ? tag : "ul";
                sb.Append('<').Append(t);
                if (last && t == "ol" && numbers.TryGetValue(b.Id, out var n) && n > 1) sb.Append(" start=\"").Append(n).Append('"');
                if (!last) sb.Append(" style=\"list-style-type:none\"");
                sb.Append('>');
                open.Add((t, false));
            }
            if (open[^1].li) sb.Append("</li>");
            sb.Append(b.Type == "none" ? "<li style=\"list-style-type:none\">" : "<li>").Append(content);
            open[^1] = (open[^1].tag, true);
        }
        CloseTo(0);
        return sb.ToString();
    }

    static string RowHtml(Block b)
    {
        var sb = new StringBuilder();
        var rowCss = b.RowStyle is { Count: > 0 } rs ? string.Join(";", rs.Select(kv => kv.Key + ":" + kv.Value)) : null;
        if (rowCss != null) sb.Append("<span style=\"").Append(WebUtility.HtmlEncode(rowCss)).Append("\">");
        foreach (var r in b.Runs)
        {
            var css = new List<string>();
            if (r.Font != null) css.Add("font-family:'" + r.Font + "'");
            if (r.Size is double s) css.Add("font-size:" + (s * 0.75).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "pt");
            if (r.Color != null) css.Add("color:" + r.Color);
            if (r.Bold == false) css.Add("font-weight:normal");
            if (r.Italic == false) css.Add("font-style:normal");
            var text = WebUtility.HtmlEncode(r.Text).Replace("\n", "<br>");
            if (r.Bold == true) text = "<b>" + text + "</b>";
            if (r.Italic == true) text = "<i>" + text + "</i>";
            if (r.Underline == true) text = "<u>" + text + "</u>";
            sb.Append(css.Count > 0 ? "<span style=\"" + WebUtility.HtmlEncode(string.Join(";", css)) + "\">" + text + "</span>" : text);
        }
        if (rowCss != null) sb.Append("</span>");
        return sb.ToString();
    }

    /// <summary>Rows as text for Notepad, chats and plain email: one Tab per level, then • or the row's number.</summary>
    public static string ToPlain(IList<Block> rows, IReadOnlyDictionary<string, int> numbers)
    {
        int min = rows.Count > 0 ? rows.Min(b => b.Level) : 0;
        return string.Join("\r\n", rows.Select(b => new string('\t', b.Level - min) +
            (b.Type == "bullet" ? "• " : b.Type == "number" ? numbers.GetValueOrDefault(b.Id, 1) + ". " : "") +
            b.Text.Replace("\n", " ")));
    }

    /// <summary>The clipboard's "HTML Format": a header with byte offsets around the fragment.</summary>
    public static string ClipboardHtml(string fragment)
    {
        const string head = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><body>\r\n<!--StartFragment-->", post = "<!--EndFragment-->\r\n</body></html>";
        int headLen = string.Format(head, 0, 0, 0, 0).Length;
        int startFrag = headLen + Encoding.UTF8.GetByteCount(pre);
        int endFrag = startFrag + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFrag + Encoding.UTF8.GetByteCount(post);
        return string.Format(head, headLen, endHtml, startFrag, endFrag) + pre + fragment + post;
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
