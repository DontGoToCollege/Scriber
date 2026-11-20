using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Scriber;

/// <summary>One row on screen: gutter (number, drag handle), fold, marker, the text box and comment badges.</summary>
sealed class RowView
{
    public required Block B;
    public readonly Border Root = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), MinHeight = 40, Margin = new Thickness(0, 0, 0, 1) };
    public readonly Border Main = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 0, 0) };
    public readonly TextBlock Num = new()
    {
        Width = 30, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 11, 0, 0), FontFamily = new FontFamily("Consolas"),
        FontSize = 12, Foreground = MainWindow.Br("#94A3B8"),
    };
    public readonly TextBlock Handle = new()
    {
        Text = "⋮", Width = 18, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.Bold,
        FontSize = 16, Foreground = MainWindow.Br("#94A3B8"), Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
        ToolTip = "Drag selected rows",
    };
    public readonly Button Fold = new() { Width = 24, Height = 30, MinWidth = 24, FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
    public readonly TextBlock Marker = new()
    {
        MinWidth = 22, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 7, 2, 0), FontSize = 16,
        Foreground = MainWindow.Br("#64748B"), VerticalAlignment = VerticalAlignment.Top,
    };
    public readonly RichTextBox Box = new();
    public readonly StackPanel Badges = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 7, 4, 0) };
    public bool Hover;
}

/// <summary>A row's own look (its rowStyle with defaults filled in). Runs only store what differs from this.</summary>
sealed record RowFmt(string Font, double Size, string Color, bool Bold, bool Italic, bool Underline)
{
    public const string DefaultFont = "Segoe UI", Ink = "#172033";
    public const double DefaultSize = 16;

    public static RowFmt Of(Block b)
    {
        string? G(string k) => b.RowStyle != null && b.RowStyle.TryGetValue(k, out var v) ? v.Trim().ToLowerInvariant() : null;
        var weight = G("font-weight");
        return new(
            Html.FirstFamily(b.RowStyle?.GetValueOrDefault("font-family")) ?? DefaultFont,
            Html.ParseSize(G("font-size")) ?? DefaultSize,
            Hex.Parse(G("color")) ?? Ink,
            weight is "bold" or "bolder" || (int.TryParse(weight, out var w) && w >= 600),
            G("font-style") is "italic" or "oblique",
            G("text-decoration")?.Contains("underline") == true);
    }
}

public partial class MainWindow
{
    string? _active;
    readonly HashSet<string> _selected = new();
    int _anchor;
    readonly Dictionary<string, RowView> _views = new();
    bool _loading;                 // filling a text box from the document
    bool _dragSelect, _dragMulti;  // click-drag across rows
    int _dragStart;
    List<int>? _dragRoots;         // rows being moved by the handle
    int _dropIndex = -1;
    bool _dropAfter;

    enum Caret { End, Start, Keep }

    static readonly string[] Rainbow = { "#ef4444", "#f97316", "#eab308", "#22c55e", "#06b6d4", "#3b82f6", "#8b5cf6", "#ec4899" };
    static readonly string[] Neon = { "#ff00a8", "#8b00ff", "#00e5ff", "#00ff66", "#d7ff00", "#ffea00", "#ff6b00", "#ff1493" };

    void WireOutline()
    {
        OutlineScroll.PreviewMouseMove += (_, e) =>
        {
            if (!_dragSelect || e.LeftButton != MouseButtonState.Pressed) return;
            int i = RowIndexAt(e.GetPosition(Rows).Y);
            if (i < 0 || (i == _dragStart && !_dragMulti)) return;
            if (!_dragMulti)
            {
                _dragMulti = true;
                ClearTextSelection();
                Keyboard.Focus(OutlineScroll);
                Mouse.Capture(OutlineScroll);
            }
            _active = _doc.Blocks[i].Id;
            SelectRange(_anchor, i);
            UpdateSelectionVisuals();
        };
        PreviewMouseLeftButtonUp += (_, _) =>
        {
            _dragSelect = false;
            if (_dragMulti && Mouse.Captured == OutlineScroll) Mouse.Capture(null);
            _dragMulti = false;
        };
    }

    int Index(string? id) => id == null ? -1 : _doc.Blocks.FindIndex(b => b.Id == id);
    Block? ActiveBlock() => _doc.Blocks.FirstOrDefault(b => b.Id == _active);

    /// <summary>Index just past row i and everything under it.</summary>
    int End(int i)
    {
        int level = _doc.Blocks[i].Level, e = i + 1;
        while (e < _doc.Blocks.Count && _doc.Blocks[e].Level > level) e++;
        return e;
    }

    bool HasKids(int i) => i + 1 < _doc.Blocks.Count && _doc.Blocks[i + 1].Level > _doc.Blocks[i].Level;

    // ---------- drawing rows ----------

    RowView CreateRow(Block b)
    {
        var v = new RowView { B = b };
        var box = v.Box;
        box.Tag = v;
        box.BorderThickness = new Thickness(0);
        box.Background = Brushes.Transparent;
        box.Padding = new Thickness(0, 7, 4, 7);
        box.AcceptsTab = false;
        box.IsUndoEnabled = false; // undo is the document's (structure and comments too)
        box.IsInactiveSelectionHighlightEnabled = true; // drops are the outline's (Clip.cs), not the box's
        box.FocusVisualStyle = null;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        box.Document = new FlowDocument { PagePadding = new Thickness(0) };
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            v.B.Runs = ReadBox(v);
            Touch();
        };
        box.SelectionChanged += (_, _) =>
        {
            if (!_loading && v.B.Id == _active) RefreshFormatControls();
        };
        box.GotKeyboardFocus += (_, _) =>
        {
            SpellCheck.SetIsEnabled(box, true); // only on the row being typed in: a speller per row is slow
            if (_active == v.B.Id && _selected.Contains(v.B.Id)) return;
            _active = v.B.Id;
            if (!_selected.Contains(_active))
            {
                _selected.Clear();
                _selected.Add(_active);
                _anchor = Index(_active);
            }
            UpdateSelectionVisuals();
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            SpellCheck.SetIsEnabled(box, false);
            CommitTyping();
        };
        // the box's own scroller would eat the wheel, and the outline would not scroll
        box.PreviewMouseWheel += (_, e) =>
        {
            OutlineScroll.ScrollToVerticalOffset(OutlineScroll.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            e.CancelCommand();
            PasteInto(v, e.DataObject, false);
        });

        v.Fold.Style = (Style)FindResource("Tool");
        v.Fold.Foreground = Br("#64748B");
        v.Fold.Click += (_, _) =>
        {
            Mark();
            v.B.Collapsed = !v.B.Collapsed;
            Commit();
            RenderRows();
        };
        v.Root.PreviewMouseLeftButtonDown += (_, e) => RowMouseDown(v, e);
        v.Root.MouseEnter += (_, _) =>
        {
            v.Hover = true;
            Paint(v);
        };
        v.Root.MouseLeave += (_, _) =>
        {
            v.Hover = false;
            Paint(v);
        };
        v.Handle.MouseLeftButtonDown += (_, e) => StartRowDrag(v, e);
        v.Handle.MouseMove += (_, e) => RowDragMove(e);
        v.Handle.MouseLeftButtonUp += (_, _) => EndRowDrag(true);
        v.Handle.LostMouseCapture += (_, _) => EndRowDrag(false);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(v.Marker, 1);
        Grid.SetColumn(box, 2);
        Grid.SetColumn(v.Badges, 3);
        inner.Children.Add(v.Fold);
        inner.Children.Add(v.Marker);
        inner.Children.Add(box);
        inner.Children.Add(v.Badges);
        v.Main.Child = inner;

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gutter = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        gutter.Children.Add(v.Num);
        gutter.Children.Add(v.Handle);
        Grid.SetColumn(v.Main, 1);
        outer.Children.Add(gutter);
        outer.Children.Add(v.Main);
        v.Root.Child = outer;
        LoadBox(v);
        return v;
    }

    /// <summary>Brings the rows on screen in line with the document, reusing each row's view (and text box).</summary>
    void RenderRows()
    {
        var blocks = _doc.Blocks;
        var open = new Dictionary<string, int>();
        var done = new Dictionary<string, int>();
        foreach (var c in _doc.Comments)
        {
            var d = c.Resolved ? done : open;
            d[c.BlockId] = d.GetValueOrDefault(c.BlockId) + 1;
        }
        var counts = new int[22];
        int? cut = null; // level of the folded row whose children are hidden
        var list = new List<UIElement>(blocks.Count);
        var live = new HashSet<string>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            bool hidden = cut is int c && b.Level > c;
            if (!hidden) cut = b.Collapsed ? b.Level : null;
            for (int k = b.Level + 1; k < counts.Length; k++) counts[k] = 0;
            if (b.Type == "number") counts[b.Level]++;
            if (!_views.TryGetValue(b.Id, out var v) || v.B != b) _views[b.Id] = v = CreateRow(b);
            live.Add(b.Id);
            UpdateRow(v, i, hidden, Math.Max(1, counts[b.Level]), HasKids(i), open.GetValueOrDefault(b.Id), done.GetValueOrDefault(b.Id));
            list.Add(v.Root);
        }
        foreach (var id in _views.Keys.Where(k => !live.Contains(k)).ToList()) _views.Remove(id);

        // move only what changed, so untouched rows keep their layout
        var keep = list.ToHashSet();
        for (int k = Rows.Children.Count - 1; k >= 0; k--)
            if (!keep.Contains(Rows.Children[k])) Rows.Children.RemoveAt(k);
        for (int k = 0; k < list.Count; k++)
        {
            if (k < Rows.Children.Count && Rows.Children[k] == list[k]) continue;
            Rows.Children.Remove(list[k]);
            Rows.Children.Insert(k, list[k]);
        }
        UpdateSelectionVisuals();
    }

    void UpdateRow(RowView v, int i, bool hidden, int number, bool kids, int open, int done)
    {
        var b = v.B;
        v.Root.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        v.Num.Text = (i + 1).ToString();
        v.Num.Visibility = _doc.ShowNumbers ? Visibility.Visible : Visibility.Hidden;
        v.Main.Margin = new Thickness(b.Level * 24, 0, 0, 0);
        v.Fold.Content = b.Collapsed ? "▸" : "▾";
        v.Fold.Visibility = kids ? Visibility.Visible : Visibility.Hidden;
        v.Marker.Text = b.Type == "bullet" ? "•" : b.Type == "number" ? number + "." : "";
        v.Badges.Children.Clear();
        if (open > 0) v.Badges.Children.Add(Badge($"● {open}", "#FEF3C7", "#92400E", () => OpenScope(b.Id, "open")));
        if (done > 0) v.Badges.Children.Add(Badge($"✓ {done}", "#DCFCE7", "#166534", () => OpenScope(b.Id, "resolved")));
    }

    Button Badge(string text, string bg, string fg, Action click)
    {
        var b = new Button
        {
            Content = text, Style = (Style)FindResource("Small"), Background = Br(bg), Foreground = Br(fg), BorderThickness = new Thickness(0),
            FontSize = 11, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(2, 0, 0, 0), ToolTip = "Show this row's comments",
        };
        b.Click += (_, _) => click();
        return b;
    }

    void Paint(RowView v)
    {
        bool sel = _selected.Contains(v.B.Id);
        var palette = _doc.Colorway == "neon" ? Neon : Rainbow;
        var accent = (Color)ColorConverter.ConvertFromString(palette[v.B.Level % palette.Length]);
        double intensity = Math.Clamp(_doc.RowColorIntensity / 100, 0.15, 1);
        v.Root.Background = sel ? Br("#DBEAFE")
            : _doc.ColoredRows ? new SolidColorBrush(Color.FromArgb((byte)(255 * (v.Hover ? .42 : .28) * intensity), accent.R, accent.G, accent.B))
            : v.Hover ? Br("#F8FAFC") : Brushes.Transparent;
        v.Root.BorderBrush = sel ? Br("#93C5FD") : Brushes.Transparent;
        v.Main.BorderBrush = new SolidColorBrush(accent);
        v.Main.BorderThickness = new Thickness(_doc.ColoredRows ? 5 : 0, 0, 0, 0);
    }

    void UpdateSelectionVisuals()
    {
        foreach (var v in _views.Values) Paint(v);
        SelInfo.Text = _selected.Count > 1 ? $"{_selected.Count} rows selected" : "";
        RefreshFormatControls();
    }

    // ---------- row text <-> text box ----------

    /// <summary>Fills a row's text box from its runs. The row's look goes on the document so new typing gets it.</summary>
    void LoadBox(RowView v)
    {
        _loading = true;
        var st = RowFmt.Of(v.B);
        var doc = v.Box.Document;
        doc.FontFamily = new FontFamily(st.Font);
        doc.FontSize = st.Size;
        doc.FontWeight = st.Bold ? FontWeights.Bold : FontWeights.Normal;
        doc.FontStyle = st.Italic ? FontStyles.Italic : FontStyles.Normal;
        doc.Foreground = Br(st.Color);
        var p = new Paragraph { Margin = new Thickness(0) };
        p.Inlines.AddRange(Inlines(v.B, st));
        if (p.Inlines.Count == 0 && st.Underline) p.Inlines.Add(new Run("") { TextDecorations = TextDecorations.Underline });
        doc.Blocks.Clear();
        doc.Blocks.Add(p);
        _loading = false;
    }

    /// <summary>A row's runs as WPF inlines ("\n" becomes a line break). Underline can't be inherited, so the row's
    /// underline is set on each run.</summary>
    static IEnumerable<Inline> Inlines(Block b, RowFmt st)
    {
        foreach (var r in b.Runs)
        {
            var parts = r.Text.Split('\n');
            for (int k = 0; k < parts.Length; k++)
            {
                if (k > 0) yield return new LineBreak();
                if (parts[k].Length == 0) continue;
                var run = new Run(parts[k]);
                if (r.Bold is bool bold) run.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
                if (r.Italic is bool it) run.FontStyle = it ? FontStyles.Italic : FontStyles.Normal;
                if (r.Font != null) run.FontFamily = new FontFamily(r.Font);
                if (r.Size is double s) run.FontSize = s;
                if (r.Color != null) run.Foreground = Br(r.Color);
                if (r.Underline ?? st.Underline) run.TextDecorations = TextDecorations.Underline;
                yield return run;
            }
        }
    }

    /// <summary>Reads a row's text box back into runs, keeping only what differs from the row's own look.</summary>
    static List<TextRun> ReadBox(RowView v)
    {
        var st = RowFmt.Of(v.B);
        var list = new List<TextRun>();
        bool first = true;
        foreach (var block in v.Box.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            if (!first) list.Add(new TextRun { Text = "\n" });
            first = false;
            Walk(p.Inlines);
        }
        return TextRun.Merge(list);

        void Walk(InlineCollection inlines)
        {
            foreach (var x in inlines)
            {
                if (x is Run r) list.Add(Read(r));
                else if (x is LineBreak) list.Add(new TextRun { Text = "\n" });
                else if (x is Span s) Walk(s.Inlines);
            }
        }

        TextRun Read(Run r)
        {
            bool bold = r.FontWeight.ToOpenTypeWeight() >= 600;
            bool italic = r.FontStyle != FontStyles.Normal;
            var font = Html.FirstFamily(r.FontFamily.Source) ?? st.Font;
            double size = Math.Round(r.FontSize * 2) / 2;
            var color = r.Foreground is SolidColorBrush b ? Hex.Of(b.Color) : st.Color;
            bool underline = false;
            for (DependencyObject? d = r; d is TextElement t; d = t.Parent)
                if ((t is Inline i ? i.TextDecorations : (t as Paragraph)?.TextDecorations) is { } deco &&
                    deco.Any(x => x.Location == TextDecorationLocation.Underline)) underline = true;
            return new TextRun
            {
                Text = r.Text,
                Bold = bold != st.Bold ? bold : null,
                Italic = italic != st.Italic ? italic : null,
                Underline = underline != st.Underline ? underline : null,
                Font = !string.Equals(font, st.Font, StringComparison.OrdinalIgnoreCase) ? font : null,
                Size = Math.Abs(size - st.Size) > 0.1 ? size : null,
                Color = color != st.Color ? color : null,
            };
        }
    }

    /// <summary>Character offset of a position in a row, counting a line break as one character (like Block.Text).</summary>
    static int CharOffset(FlowDocument doc, TextPointer target)
    {
        int n = 0;
        bool first = true;
        for (var p = doc.ContentStart; p != null && p.CompareTo(target) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            var ctx = p.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                int len = p.GetTextRunLength(LogicalDirection.Forward);
                if (p.GetPositionAtOffset(len).CompareTo(target) > 0) return n + p.GetOffsetToPosition(target);
                n += len;
            }
            else if (ctx == TextPointerContext.ElementStart)
            {
                var el = p.GetAdjacentElement(LogicalDirection.Forward);
                if (el is LineBreak || (el is Paragraph && !first)) n++;
                if (el is Paragraph) first = false;
            }
        }
        return n;
    }

    static TextPointer PointerAt(FlowDocument doc, int offset)
    {
        bool first = true;
        for (var p = doc.ContentStart; p != null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            var ctx = p.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                int len = p.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= len) return p.GetPositionAtOffset(offset);
                offset -= len;
            }
            else if (ctx == TextPointerContext.ElementStart)
            {
                var el = p.GetAdjacentElement(LogicalDirection.Forward);
                if (el is LineBreak || (el is Paragraph && !first))
                {
                    if (offset == 0) return p;
                    offset--;
                }
                if (el is Paragraph) first = false;
            }
            else if (ctx == TextPointerContext.None) return p;
        }
        return doc.ContentEnd;
    }

    static bool OnEdgeLine(RichTextBox box, int dir)
    {
        box.CaretPosition.GetLineStartPosition(dir, out int moved);
        return moved == 0;
    }

    // ---------- selection and focus ----------

    void FocusRow(string id, Caret where = Caret.End, bool keepSelection = false)
    {
        _active = id;
        if (!keepSelection)
        {
            _selected.Clear();
            _selected.Add(id);
        }
        _anchor = Math.Max(0, Index(id));
        UpdateSelectionVisuals();
        if (_smoke || !_views.TryGetValue(id, out var v)) return;
        // at once, not queued: keys typed right after Enter or Tab must land in the new row
        if (!v.Box.IsVisible) Rows.UpdateLayout();
        v.Box.Focus();
        if (where == Caret.End) v.Box.CaretPosition = v.Box.Document.ContentEnd;
        else if (where == Caret.Start) v.Box.CaretPosition = v.Box.Document.ContentStart;
        v.Root.BringIntoView();
    }

    bool MoveToRow(int dir)
    {
        int i = Index(_active);
        do i += dir;
        while (i >= 0 && i < _doc.Blocks.Count && _views[_doc.Blocks[i].Id].Root.Visibility != Visibility.Visible);
        if (i < 0 || i >= _doc.Blocks.Count) return false;
        FocusRow(_doc.Blocks[i].Id);
        return true;
    }

    void SelectRange(int a, int b)
    {
        _selected.Clear();
        for (int i = Math.Max(0, Math.Min(a, b)); i <= Math.Min(_doc.Blocks.Count - 1, Math.Max(a, b)); i++) _selected.Add(_doc.Blocks[i].Id);
    }

    void SelectAllRows()
    {
        ClearTextSelection();
        _selected.Clear();
        foreach (var b in _doc.Blocks) _selected.Add(b.Id);
        _active = _doc.Blocks[0].Id;
        _anchor = 0;
        if (!_smoke) Keyboard.Focus(OutlineScroll);
        UpdateSelectionVisuals();
        Toast($"{_doc.Blocks.Count} rows selected");
    }

    void ClearTextSelection()
    {
        foreach (var v in _views.Values)
            if (!v.Box.Selection.IsEmpty) v.Box.Selection.Select(v.Box.Selection.End, v.Box.Selection.End);
    }

    /// <summary>Selected rows that aren't inside another selected row, in order.</summary>
    List<int> SelectedRoots()
    {
        var idx = _selected.Select(Index).Where(i => i >= 0).OrderBy(i => i).ToList();
        var roots = new List<int>();
        foreach (var i in idx)
            if (!roots.Any(r => i < End(r))) roots.Add(i);
        return roots;
    }

    List<Block> TargetRows()
    {
        var rows = _doc.Blocks.Where(b => _selected.Contains(b.Id)).ToList();
        if (rows.Count == 0 && ActiveBlock() is { } a) rows.Add(a);
        return rows;
    }

    static bool Within(DependencyObject? d, DependencyObject ancestor)
    {
        while (d != null)
        {
            if (d == ancestor || (ancestor is RichTextBox rtb && d == rtb.Document)) return true;
            d = d switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(d),
                FrameworkContentElement fce => fce.Parent,
                _ => LogicalTreeHelper.GetParent(d),
            };
        }
        return false;
    }

    int RowIndexAt(double y)
    {
        int last = -1;
        for (int i = 0; i < _doc.Blocks.Count; i++)
        {
            if (!_views.TryGetValue(_doc.Blocks[i].Id, out var v) || v.Root.Visibility != Visibility.Visible) continue;
            last = i;
            if (y < v.Root.TranslatePoint(new Point(), Rows).Y + v.Root.ActualHeight) return i;
        }
        return last;
    }

    // ---------- mouse ----------

    void RowMouseDown(RowView v, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        var mods = Keyboard.Modifiers;
        int i = Index(v.B.Id);
        if (mods.HasFlag(ModifierKeys.Alt))
        {
            if (Within(src, v.Handle) || Within(src, v.Badges)) return;
            e.Handled = true;
            if (mods.HasFlag(ModifierKeys.Shift)) ToggleAll();
            else ToggleLevel(v.B.Level);
            return;
        }
        if (Within(src, v.Fold) || Within(src, v.Handle) || Within(src, v.Badges)) return;
        bool inBox = Within(src, v.Box);
        if (inBox && e.ClickCount == 2)
        {
            e.Handled = true;
            OpenAdd(v.B.Id);
            return;
        }
        if (mods.HasFlag(ModifierKeys.Shift) || mods.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            ClearTextSelection();
            _active = v.B.Id;
            if (mods.HasFlag(ModifierKeys.Shift)) SelectRange(_anchor, i);
            else
            {
                if (!_selected.Remove(v.B.Id)) _selected.Add(v.B.Id);
                _anchor = i;
            }
            Keyboard.Focus(OutlineScroll); // typing must not go into one of several selected rows
            UpdateSelectionVisuals();
            return;
        }
        _selected.Clear();
        _selected.Add(v.B.Id);
        _active = v.B.Id;
        _anchor = i;
        _dragSelect = true;
        _dragMulti = false;
        _dragStart = i;
        UpdateSelectionVisuals();
        if (inBox) return;
        e.Handled = true;
        if (e.GetPosition(v.Root).X < 48) Keyboard.Focus(OutlineScroll);
        else FocusRow(v.B.Id);
    }

    void StartRowDrag(RowView v, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        if (!_selected.Contains(v.B.Id))
        {
            _selected.Clear();
            _selected.Add(v.B.Id);
            _active = v.B.Id;
            _anchor = Index(v.B.Id);
            UpdateSelectionVisuals();
        }
        _dragRoots = SelectedRoots();
        foreach (var r in _dragRoots)
            for (int j = r; j < End(r); j++) _views[_doc.Blocks[j].Id].Root.Opacity = 0.38;
        _dropIndex = -1;
        v.Handle.CaptureMouse();
        e.Handled = true;
    }

    void RowDragMove(MouseEventArgs e)
    {
        if (_dragRoots == null) return;
        var p = e.GetPosition(this);
        if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
        {
            // dragged out of the window: hand a copy of the rows to whatever program they're dropped on
            var rows = _dragRoots.SelectMany(r => _doc.Blocks.GetRange(r, End(r) - r)).ToList();
            EndRowDrag(false);
            _dragOut = true; // brought back and dropped in Scriber, it's a move again (Clip.cs)
            try
            {
                DragDrop.DoDragDrop(this, RowsData(rows), DragDropEffects.Copy);
            }
            finally
            {
                _dragOut = false;
            }
            return;
        }
        var y = e.GetPosition(Rows).Y;
        int i = RowIndexAt(y);
        if (i < 0) return;
        var root = _views[_doc.Blocks[i].Id].Root;
        var top = root.TranslatePoint(new Point(), Rows).Y;
        _dropIndex = i;
        _dropAfter = y >= top + root.ActualHeight / 2;
        Canvas.SetLeft(DropLine, 48);
        DropLine.Width = Math.Max(0, Rows.ActualWidth - 48);
        Canvas.SetTop(DropLine, (_dropAfter ? top + root.ActualHeight : top) - 1.5);
        DropLine.Visibility = Visibility.Visible;
    }

    void EndRowDrag(bool drop)
    {
        if (_dragRoots == null) return;
        _dragRoots = null;
        Mouse.Capture(null);
        DropLine.Visibility = Visibility.Collapsed;
        foreach (var v in _views.Values) v.Root.Opacity = 1;
        if (drop && _dropIndex >= 0) MoveSelected(_dropIndex, _dropAfter);
        _dropIndex = -1;
    }

    // ---------- editing ----------

    void AddRow(Block after, int level)
    {
        Mark();
        var nb = new Block { Level = level, Type = after.Type };
        _doc.Blocks.Insert(Index(after.Id) + 1, nb);
        Commit();
        RenderRows();
        FocusRow(nb.Id);
    }

    /// <summary>Enter: the text after the caret moves to a new row below.</summary>
    void SplitRow(RowView v)
    {
        Mark();
        var box = v.Box;
        if (!box.Selection.IsEmpty) box.Selection.Text = "";
        v.B.Runs = ReadBox(v);
        var (before, after) = TextRun.Split(v.B.Runs, CharOffset(box.Document, box.CaretPosition));
        v.B.Runs = TextRun.Merge(before);
        LoadBox(v);
        var rest = TextRun.Merge(after);
        var nb = new Block
        {
            Level = v.B.Level, Type = v.B.Type, Runs = rest,
            RowStyle = rest.Count > 0 && v.B.RowStyle != null ? new(v.B.RowStyle) : null,
        };
        _doc.Blocks.Insert(Index(v.B.Id) + 1, nb);
        Commit();
        RenderRows();
        FocusRow(nb.Id, Caret.Start);
    }

    /// <summary>Delete (preserveChildren false) removes rows with everything under them; Backspace keeps what is
    /// under them, one level up.</summary>
    void RemoveSelected(bool preserveChildren)
    {
        if (_selected.Count == 0) return;
        Mark();
        RemoveCore(preserveChildren, out var target);
        Commit();
        RenderRows();
        FocusRow(target);
    }

    /// <summary>Takes the selected rows out (no undo step, no redraw). Returns where the first one was.</summary>
    int RemoveCore(bool preserveChildren, out string target)
    {
        var old = _doc.Blocks;
        var sel = new HashSet<string>(_selected);
        var removed = new HashSet<string>(sel);
        var output = new List<Block>();
        int first = int.MaxValue;
        string? focusChild = null;
        for (int i = 0; i < old.Count; i++)
        {
            var b = old[i];
            if (sel.Contains(b.Id))
            {
                first = Math.Min(first, i);
                int j = i + 1;
                for (; j < old.Count && old[j].Level > b.Level; j++)
                {
                    if (preserveChildren && !sel.Contains(old[j].Id))
                    {
                        old[j].Level = Math.Max(b.Level, old[j].Level - 1);
                        output.Add(old[j]);
                        focusChild ??= old[j].Id;
                    }
                    else removed.Add(old[j].Id);
                }
                i = j - 1;
                continue;
            }
            if (!removed.Contains(b.Id)) output.Add(b);
        }
        if (output.Count == 0) output.Add(new Block());
        _doc.Blocks = output;
        _doc.Comments.RemoveAll(c => removed.Contains(c.BlockId));
        // Backspace goes to the row above, like in text; Delete to the row that took the removed row's place
        int at = Math.Clamp(preserveChildren ? first - 1 : first, 0, output.Count - 1);
        target = preserveChildren && first > 0 ? output[at].Id : focusChild ?? output[at].Id;
        return first;
    }

    void ClearSelectedText()
    {
        var rows = TargetRows();
        if (rows.Count == 0) return;
        Mark();
        foreach (var b in rows)
        {
            b.Runs = new();
            if (_views.TryGetValue(b.Id, out var v)) LoadBox(v);
        }
        Commit();
        FocusRow(rows[0].Id);
    }

    void ChangeIndent(int d)
    {
        var changes = new List<(Block b, int level)>();
        foreach (var i in _selected.Select(Index).Where(i => i >= 0).OrderBy(i => i))
        {
            var b = _doc.Blocks[i];
            int max = i > 0 ? _doc.Blocks[i - 1].Level + 1 : 0;
            int n = Math.Max(0, Math.Min(Math.Min(20, max), b.Level + d));
            if (n != b.Level) changes.Add((b, n));
        }
        if (changes.Count > 0)
        {
            Mark();
            foreach (var (b, n) in changes) b.Level = n;
            Commit();
            RenderRows();
        }
        if (_active != null) FocusRow(_active, Caret.Keep, true);
    }

    void LineType(string t)
    {
        var rows = TargetRows();
        if (rows.Count == 0) return;
        Mark();
        foreach (var b in rows) b.Type = b.Type == t ? "none" : t;
        Commit();
        RenderRows();
        if (_active != null) FocusRow(_active, Caret.Keep, true);
    }

    void SetAllCollapsed(bool collapse)
    {
        var targets = _doc.Blocks.Where((b, i) => HasKids(i)).ToList();
        if (targets.Count == 0) return;
        Mark();
        foreach (var b in targets) b.Collapsed = collapse;
        Commit();
        RenderRows();
        Toast(collapse ? "All rows collapsed" : "All rows expanded");
    }

    void ToggleAll()
    {
        var targets = _doc.Blocks.Where((b, i) => HasKids(i)).ToList();
        if (targets.Count > 0) SetAllCollapsed(targets.Any(b => !b.Collapsed));
    }

    void ToggleLevel(int level)
    {
        var targets = _doc.Blocks.Where((b, i) => b.Level == level && HasKids(i)).ToList();
        if (targets.Count == 0) return;
        Mark();
        bool collapse = targets.Any(b => !b.Collapsed);
        foreach (var b in targets) b.Collapsed = collapse;
        Commit();
        RenderRows();
    }

    void MoveSelected(int target, bool after)
    {
        var roots = SelectedRoots();
        if (roots.Count == 0) return;
        if (roots.Any(r => target >= r && target < End(r))) return; // onto itself or its own child: nothing to do
        Mark();
        var targetId = _doc.Blocks[target].Id;
        var moved = roots.SelectMany(r => _doc.Blocks.GetRange(r, End(r) - r)).ToList();
        var movedSet = moved.ToHashSet();
        _doc.Blocks.RemoveAll(movedSet.Contains);
        int ti = Index(targetId);
        _doc.Blocks.InsertRange(after ? End(ti) : ti, moved);
        _selected.Clear();
        foreach (var b in moved) _selected.Add(b.Id);
        _active = moved[0].Id;
        Commit();
        RenderRows();
        Toast("Rows moved");
    }

    // ---------- formatting ----------

    bool HasTextSelection(out RowView v)
    {
        v = null!;
        return _selected.Count == 1 && _active != null && _selected.Contains(_active) && _views.TryGetValue(_active, out v!) &&
               !v.Box.Selection.IsEmpty;
    }

    /// <summary>Bold, italic or underline: the selected text if there is some, otherwise whole rows (toggled).</summary>
    void Exec(string cmd)
    {
        if (HasTextSelection(out var v))
        {
            var sel = v.Box.Selection;
            switch (cmd)
            {
                case "bold":
                    FormatSelection(TextElement.FontWeightProperty,
                        sel.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600 ? FontWeights.Normal : FontWeights.Bold);
                    break;
                case "italic":
                    FormatSelection(TextElement.FontStyleProperty,
                        sel.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle s && s != FontStyles.Normal ? FontStyles.Normal : FontStyles.Italic);
                    break;
                default:
                    FormatSelection(Inline.TextDecorationsProperty,
                        sel.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection d &&
                        d.Any(x => x.Location == TextDecorationLocation.Underline) ? new TextDecorationCollection() : TextDecorations.Underline);
                    break;
            }
            return;
        }
        var (prop, on) = cmd switch { "bold" => ("font-weight", "700"), "italic" => ("font-style", "italic"), _ => ("text-decoration", "underline") };
        var rows = TargetRows();
        bool off = rows.Count > 0 && rows.All(b => b.RowStyle?.GetValueOrDefault(prop) == on);
        Action<TextRun> strip = cmd switch
        {
            "bold" => r => r.Bold = null,
            "italic" => r => r.Italic = null,
            _ => r => r.Underline = null,
        };
        FormatRows(prop, off ? null : on, strip, off ? "unformatted" : "formatted");
    }

    void ApplyFont(string font)
    {
        if (!FormatSelection(TextElement.FontFamilyProperty, new FontFamily(font))) FormatRows("font-family", font, r => r.Font = null);
    }

    void ApplySize(int px)
    {
        if (!FormatSelection(TextElement.FontSizeProperty, (double)px)) FormatRows("font-size", px + "px", r => r.Size = null);
    }

    void ApplyColor(string hex, bool remember)
    {
        if (remember) _doc.LastTextColor = hex;
        if (!FormatSelection(TextElement.ForegroundProperty, Br(hex))) FormatRows("color", hex, r => r.Color = null);
        if (!remember) Toast(hex == "#000000" ? "Black applied" : "Last used color applied");
    }

    bool FormatSelection(DependencyProperty dp, object value)
    {
        if (!HasTextSelection(out var v)) return false;
        Mark();
        v.Box.Selection.ApplyPropertyValue(dp, value);
        v.B.Runs = ReadBox(v);
        Commit();
        RefreshFormatControls();
        Toast("Selected text formatted");
        if (!_smoke) v.Box.Focus();
        return true;
    }

    /// <summary>Sets (or with null, removes) a whole-row style and drops the same property from the row's runs.</summary>
    void FormatRows(string prop, string? css, Action<TextRun> strip, string done = "formatted")
    {
        var rows = TargetRows();
        if (rows.Count == 0) return;
        Mark();
        foreach (var b in rows)
        {
            if (css == null) b.RowStyle?.Remove(prop);
            else (b.RowStyle ??= new())[prop] = css;
            if (b.RowStyle?.Count == 0) b.RowStyle = null;
            b.Runs = TextRun.Merge(b.Runs.Select(r =>
            {
                var c = r.With(r.Text);
                strip(c);
                return c;
            }));
            if (_views.TryGetValue(b.Id, out var v)) LoadBox(v);
        }
        Commit();
        RefreshFormatControls();
        Toast($"{rows.Count} row{S(rows.Count)} {done}");
        if (_active != null) FocusRow(_active, Caret.Keep, true);
    }

    /// <summary>Shows the font and size of the selected text, or what the selected rows have in common.</summary>
    void RefreshFormatControls()
    {
        string? font = null;
        double? size = null;
        if (HasTextSelection(out var v))
        {
            font = v.Box.Selection.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f ? Html.FirstFamily(f.Source) : null;
            size = v.Box.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double d ? d : null;
        }
        else
        {
            var fmts = TargetRows().Select(RowFmt.Of).ToList();
            if (fmts.Count > 0 && fmts.All(x => x.Font == fmts[0].Font)) font = fmts[0].Font;
            if (fmts.Count > 0 && fmts.All(x => x.Size == fmts[0].Size)) size = fmts[0].Size;
        }
        _syncing = true;
        FontBox.SelectedItem = Fonts.FirstOrDefault(x => string.Equals(x, font, StringComparison.OrdinalIgnoreCase));
        SizeBox.SelectedItem = size is double s && Sizes.Contains((int)s) && s % 1 == 0 ? (int)s : null;
        _syncing = false;
        ColorSwatch.Background = Br(_doc.LastTextColor);
    }

    // ---------- paste ----------

    void PasteInto(RowView v, IDataObject? data, bool plainOnly)
    {
        if (data == null) return;
        if (!plainOnly && ReadRows(data) is { } clip)
        {
            var (rows, comments) = clip;
            int min = rows.Min(b => b.Level);
            foreach (var r in rows) r.Level = Math.Min(20, v.B.Level + r.Level - min);
            InsertPasted(v, rows, comments);
            return;
        }
        string plain = "";
        var items = new List<Html.Item>();
        try
        {
            plain = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string ?? "";
            if (!plainOnly && data.GetData(DataFormats.Html) is string cf) items = Html.ToItems(Html.Fragment(Html.FixUtf8(cf)));
        }
        catch (Exception ex)
        {
            PortablePaths.Log("clipboard unreadable: " + ex.Message);
        }
        if (items.Count <= 1 && Html.FromPlain(plain) is { Count: > 1 } fromPlain) items = fromPlain;
        if (items.Count > 1)
        {
            PasteRows(v, items, plainOnly);
            return;
        }
        // one line: plain text where the caret is
        var text = (plain.Length > 0 ? plain : items.Count == 1 ? items[0].Text : "").Replace("\r", "").Trim('\n').Replace('\n', ' ');
        if (text.Length == 0) return;
        Mark();
        v.Box.Selection.Text = text;
        v.Box.Selection.Select(v.Box.Selection.End, v.Box.Selection.End);
        v.B.Runs = ReadBox(v);
        Commit();
        if (plainOnly) Toast("Pasted as plain text");
    }

    /// <summary>Several pasted rows never replace the row's text: they go below it (split at the caret), or above it
    /// when the caret is at the start. An empty row is replaced.</summary>
    void PasteRows(RowView v, List<Html.Item> items, bool plainOnly) =>
        InsertPasted(v, items.Select(x => new Block
        {
            Level = Math.Min(20, v.B.Level + x.Level), Type = plainOnly && x.Type == "none" ? v.B.Type : x.Type, Runs = TextRun.Merge(x.Runs),
        }).ToList(), null);

    void InsertPasted(RowView v, List<Block> created, List<Comment>? comments)
    {
        Mark();
        var box = v.Box;
        if (!box.Selection.IsEmpty) box.Selection.Text = "";
        var b = v.B;
        b.Runs = ReadBox(v);
        var (before, after) = TextRun.Split(b.Runs, CharOffset(box.Document, box.CaretPosition));
        int i = Index(b.Id);
        int pasted = created.Count;
        var pastedRows = created.ToList();
        if (comments != null) _doc.Comments.AddRange(comments);
        bool hasBefore = before.Any(r => r.Text.Trim().Length > 0), hasAfter = after.Any(r => r.Text.Trim().Length > 0);
        if (!hasBefore && !hasAfter)
        {
            _doc.Blocks.RemoveAt(i);
            _doc.Blocks.InsertRange(i, created);
            foreach (var c in _doc.Comments.Where(c => c.BlockId == b.Id)) c.BlockId = created[0].Id;
        }
        else if (!hasBefore) _doc.Blocks.InsertRange(i, created);
        else
        {
            b.Runs = TextRun.Merge(before);
            LoadBox(v);
            if (hasAfter)
                created.Add(new Block { Level = b.Level, Type = b.Type, Runs = TextRun.Merge(after), RowStyle = b.RowStyle == null ? null : new(b.RowStyle) });
            _doc.Blocks.InsertRange(i + 1, created);
        }
        _selected.Clear();
        foreach (var c in created.Take(pasted)) _selected.Add(c.Id);
        Commit();
        RenderRows();
        if (comments is { Count: > 0 }) RenderReview();
        FocusRow(created[pasted - 1].Id, Caret.End, true);
        ShowPasteNote(pastedRows);
    }

    // ---------- find (Ctrl+F) ----------

    readonly List<(int row, int at)> _matches = new();
    int _match = -1;

    void WireFind()
    {
        FindBox.TextChanged += (_, _) => UpdateFind();
        FindNext.Click += (_, _) => StepFind(1);
        FindPrev.Click += (_, _) => StepFind(-1);
        FindClose.Click += (_, _) => CloseFind();
    }

    void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        if (HasTextSelection(out var v) && v.Box.Selection.Text is { Length: > 0 and < 80 } t && !t.Contains('\n')) FindBox.Text = t;
        if (!_smoke)
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }
        UpdateFind();
    }

    void CollectMatches()
    {
        _matches.Clear();
        var q = FindBox.Text;
        if (q.Length == 0) return;
        for (int i = 0; i < _doc.Blocks.Count; i++)
        {
            var t = _doc.Blocks[i].Text;
            for (int k = t.IndexOf(q, StringComparison.OrdinalIgnoreCase); k >= 0; k = t.IndexOf(q, k + q.Length, StringComparison.OrdinalIgnoreCase))
                _matches.Add((i, k));
        }
    }

    void UpdateFind()
    {
        CollectMatches();
        int from = Math.Max(0, Index(_active));
        _match = _matches.FindIndex(m => m.row >= from);
        if (_match < 0 && _matches.Count > 0) _match = 0;
        ShowMatch();
    }

    void StepFind(int d)
    {
        var cur = _match >= 0 && _match < _matches.Count ? _matches[_match] : (row: Math.Max(0, Index(_active)), at: -1);
        CollectMatches(); // the text may have changed since
        if (_matches.Count == 0)
        {
            ShowMatch();
            return;
        }
        int Cmp((int row, int at) m) => m.row != cur.row ? m.row.CompareTo(cur.row) : m.at.CompareTo(cur.at);
        _match = d > 0 ? _matches.FindIndex(m => Cmp(m) > 0) : _matches.FindLastIndex(m => Cmp(m) < 0);
        if (_match < 0) _match = d > 0 ? 0 : _matches.Count - 1;
        ShowMatch();
    }

    void ShowMatch()
    {
        FindCount.Text = _matches.Count == 0 ? (FindBox.Text.Length > 0 ? "No matches" : "") : $"{_match + 1} of {_matches.Count}";
        if (_match < 0 || _match >= _matches.Count) return;
        var (row, at) = _matches[_match];
        if (Reveal(row))
        {
            Touch();
            RenderRows();
        }
        var b = _doc.Blocks[row];
        var v = _views[b.Id];
        _active = b.Id;
        _selected.Clear();
        _selected.Add(b.Id);
        _anchor = row;
        v.Box.Selection.Select(PointerAt(v.Box.Document, at), PointerAt(v.Box.Document, at + FindBox.Text.Length));
        UpdateSelectionVisuals();
        v.Root.BringIntoView();
    }

    /// <summary>Unfolds the rows above a row so it can be seen.</summary>
    bool Reveal(int row)
    {
        bool changed = false;
        int min = _doc.Blocks[row].Level;
        for (int j = row - 1; j >= 0 && min > 0; j--)
        {
            if (_doc.Blocks[j].Level >= min) continue;
            min = _doc.Blocks[j].Level;
            if (_doc.Blocks[j].Collapsed)
            {
                _doc.Blocks[j].Collapsed = false;
                changed = true;
            }
        }
        return changed;
    }

    void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        if (!_smoke && _active != null && _views.TryGetValue(_active, out var v)) v.Box.Focus();
    }
}
