using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Scriber;

/// <summary>Rows on the clipboard and by drag and drop: copy, cut, paste over selected rows, duplicate, keyboard row
/// selection, and the note after a paste (Undo, Text only).</summary>
public partial class MainWindow
{
    /// <summary>Scriber's own clipboard format: the rows (and their comments) as a small document, so pasting into
    /// Scriber loses nothing.</summary>
    const string RowsFormat = "Scriber.Rows";

    DataObject? _smokeClip;          // the self-check never touches the real clipboard
    bool _inStep;                    // several changes that undo as one step
    bool _dragOut;                   // rows are being dragged by their handle outside the window
    List<string> _pastedIds = new(); // rows the last paste made (for Text only)
    DispatcherTimer? _noteTimer;

    void WireClipboard()
    {
        _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _noteTimer.Tick += (_, _) => HidePasteNote();
        PasteUndo.Click += (_, _) =>
        {
            HidePasteNote();
            Undo();
        };
        PasteTextOnly.Click += (_, _) =>
        {
            HidePasteNote();
            TextOnly(_pastedIds);
        };

        // drop text, lists or rows from other programs (or from Scriber itself) between rows
        OutlineScroll.AllowDrop = true;
        OutlineScroll.PreviewDragOver += (_, e) =>
        {
            e.Effects = Pasteable(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            if (e.Effects != DragDropEffects.None) ShowDropLine(e.GetPosition(Rows).Y);
            e.Handled = true;
        };
        OutlineScroll.PreviewDragLeave += (_, _) => DropLine.Visibility = Visibility.Collapsed;
        OutlineScroll.PreviewDrop += (_, e) =>
        {
            DropLine.Visibility = Visibility.Collapsed;
            e.Handled = true;
            if (!Pasteable(e.Data)) return;
            int i = RowIndexAt(e.GetPosition(Rows).Y);
            bool after = i < 0 || e.GetPosition(_views[_doc.Blocks[i].Id].Root).Y >= _views[_doc.Blocks[i].Id].Root.ActualHeight / 2;
            int at = i < 0 ? _doc.Blocks.Count : after ? End(i) : i;
            int level = i < 0 ? 0 : _doc.Blocks[i].Level;
            if (_dragOut)
            {
                if (i >= 0) MoveSelected(i, after);
                return;
            }
            InStep(() =>
            {
                var nb = new Block { Level = level };
                _doc.Blocks.Insert(at, nb);
                RenderRows();
                PasteInto(_views[nb.Id], e.Data, false);
                if (nb.Runs.Count == 0 && _doc.Blocks.Contains(nb)) _doc.Blocks.Remove(nb); // nothing landed in it
            });
        };
    }

    void ShowDropLine(double y)
    {
        int i = RowIndexAt(y);
        if (i < 0) return;
        var root = _views[_doc.Blocks[i].Id].Root;
        var top = root.TranslatePoint(new Point(), Rows).Y;
        bool after = y >= top + root.ActualHeight / 2;
        Canvas.SetLeft(DropLine, 48);
        DropLine.Width = Math.Max(0, Rows.ActualWidth - 48);
        Canvas.SetTop(DropLine, (after ? top + root.ActualHeight : top) - 1.5);
        DropLine.Visibility = Visibility.Visible;
    }

    static bool Pasteable(IDataObject? d)
    {
        try
        {
            return d != null && (d.GetDataPresent(RowsFormat) || d.GetDataPresent(DataFormats.UnicodeText) ||
                                 d.GetDataPresent(DataFormats.Text) || d.GetDataPresent(DataFormats.Html));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Changes that undo as one step (Mark is skipped inside).</summary>
    void InStep(Action change)
    {
        Mark();
        _inStep = true;
        try
        {
            change();
        }
        finally
        {
            _inStep = false;
        }
        Commit();
        RenderRows();
    }

    /// <summary>The number each numbered row shows, as in RenderRows.</summary>
    Dictionary<string, int> Numbers()
    {
        var counts = new int[22];
        var map = new Dictionary<string, int>();
        foreach (var b in _doc.Blocks)
        {
            for (int k = b.Level + 1; k < counts.Length; k++) counts[k] = 0;
            if (b.Type == "number") counts[b.Level]++;
            map[b.Id] = Math.Max(1, counts[b.Level]);
        }
        return map;
    }

    /// <summary>The selected rows with everything under them (like Delete), or the row being typed in.</summary>
    List<Block> BranchRows(out List<int> roots)
    {
        roots = SelectedRoots();
        if (roots.Count == 0 && Index(_active) is var a && a >= 0) roots.Add(a);
        return roots.SelectMany(r => _doc.Blocks.GetRange(r, End(r) - r)).ToList();
    }

    DataObject RowsData(List<Block> rows)
    {
        var numbers = Numbers();
        var ids = rows.Select(b => b.Id).ToHashSet();
        var d = new DataObject();
        d.SetData(DataFormats.UnicodeText, Html.ToPlain(rows, numbers));
        d.SetData(DataFormats.Html, Html.ClipboardHtml(Html.ToHtml(rows, numbers)));
        d.SetData(RowsFormat, DocFile.ToJson(new Doc { Blocks = rows, Comments = _doc.Comments.Where(c => ids.Contains(c.BlockId)).ToList() }));
        return d;
    }

    /// <summary>Ctrl+C / Ctrl+X on rows: Scriber's own rows, HTML lists and tabbed text all at once, so each program
    /// takes what it understands best.</summary>
    void CopyRows(bool cut)
    {
        CommitTyping();
        var rows = BranchRows(out _);
        if (rows.Count == 0) return;
        var data = RowsData(rows);
        if (_smoke) _smokeClip = data;
        else if (!SetClipboard(data))
        {
            Toast("Could not copy: another program is using the clipboard");
            return;
        }
        if (cut)
        {
            if (_selected.Count == 0 && _active != null) _selected.Add(_active);
            RemoveSelected(false);
        }
        Toast($"{rows.Count} row{S(rows.Count)} {(cut ? "cut" : "copied")}");
    }

    static bool SetClipboard(DataObject data)
    {
        for (int tries = 0; tries < 5; tries++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (Exception ex)
            {
                PortablePaths.Log("clipboard busy: " + ex.Message);
                System.Threading.Thread.Sleep(60);
            }
        }
        return false;
    }

    /// <summary>Ctrl+V with rows selected: the pasted rows take their place.</summary>
    void PasteOver(IDataObject? data, bool plainOnly)
    {
        if (!Pasteable(data)) return;
        var rows = TargetRows();
        if (rows.Count == 0) return;
        int level = rows[0].Level;
        InStep(() =>
        {
            int first = RemoveCore(false, out _);
            Block nb;
            if (_doc.Blocks.Count == 1 && _doc.Blocks[0].Runs.Count == 0) nb = _doc.Blocks[0];
            else
            {
                first = Math.Clamp(first, 0, _doc.Blocks.Count);
                nb = new Block { Level = Math.Min(level, first > 0 ? _doc.Blocks[first - 1].Level + 1 : 0) };
                _doc.Blocks.Insert(first, nb);
            }
            _selected.Clear();
            _selected.Add(nb.Id);
            _active = nb.Id;
            RenderRows();
            PasteInto(_views[nb.Id], data, plainOnly);
        });
    }

    /// <summary>Rows copied from Scriber (this window or another): new ids, levels kept, and their comments when
    /// this document doesn't have them already (a cut and paste moves them along).</summary>
    (List<Block> rows, List<Comment> comments)? ReadRows(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(RowsFormat) || data.GetData(RowsFormat) is not string json) return null;
            var d = DocFile.FromJson(json);
            var ids = new Dictionary<string, string>();
            foreach (var b in d.Blocks) ids[b.Id] = b.Id = Doc.NewId();
            var have = _doc.Comments.Select(c => c.Id).ToHashSet();
            var comments = d.Comments.Where(c => !have.Contains(c.Id) && ids.ContainsKey(c.BlockId)).ToList();
            foreach (var c in comments) c.BlockId = ids[c.BlockId];
            return d.Blocks.Count == 0 ? null : (d.Blocks, comments);
        }
        catch (Exception ex)
        {
            PortablePaths.Log("Scriber rows unreadable: " + ex.Message);
            return null;
        }
    }

    /// <summary>Ctrl+D: a copy of the selected rows (and what's under them) right below them.</summary>
    void DuplicateRows()
    {
        CommitTyping();
        var rows = BranchRows(out var roots);
        if (rows.Count == 0) return;
        var clones = rows.Select(b => new Block
        {
            Level = b.Level, Type = b.Type, Collapsed = b.Collapsed, Runs = b.Runs.Select(r => r.With(r.Text)).ToList(),
            RowStyle = b.RowStyle == null ? null : new(b.RowStyle),
        }).ToList();
        Mark();
        int at = End(roots[^1]);
        _doc.Blocks.InsertRange(at, clones);
        Commit();
        RenderRows();
        if (clones.Count == 1) FocusRow(clones[0].Id);
        else
        {
            _selected.Clear();
            foreach (var c in clones) _selected.Add(c.Id);
            _active = clones[0].Id;
            _anchor = at;
            if (!_smoke) Keyboard.Focus(OutlineScroll);
            UpdateSelectionVisuals();
        }
        Toast($"{clones.Count} row{S(clones.Count)} duplicated");
    }

    /// <summary>Shift+Up/Down: grow or shrink the row selection from the row where it started.</summary>
    void ExtendRows(int dir)
    {
        int i = Index(_active);
        if (i < 0) return;
        int j = i;
        do j += dir;
        while (j >= 0 && j < _doc.Blocks.Count && _views[_doc.Blocks[j].Id].Root.Visibility != Visibility.Visible);
        if (j < 0 || j >= _doc.Blocks.Count) return;
        ClearTextSelection();
        if (_selected.Count <= 1) _anchor = i;
        _active = _doc.Blocks[j].Id;
        SelectRange(_anchor, j);
        if (!_smoke) Keyboard.Focus(OutlineScroll); // typing must not go into one of several selected rows
        UpdateSelectionVisuals();
        _views[_active].Root.BringIntoView();
    }

    static bool AllTextSelected(RowView v)
    {
        var all = new System.Windows.Documents.TextRange(v.Box.Document.ContentStart, v.Box.Document.ContentEnd).Text.TrimEnd('\r', '\n');
        return all.Length == 0 || v.Box.Selection.Text.TrimEnd('\r', '\n').Length >= all.Length;
    }

    void ShowPasteNote(List<Block> rows)
    {
        _pastedIds = rows.Select(b => b.Id).ToList();
        ToastBorder.Visibility = Visibility.Collapsed;
        PasteNoteText.Text = $"{rows.Count} row{S(rows.Count)} pasted";
        PasteTextOnly.Visibility = rows.Any(b => b.RowStyle != null || b.Runs.Any(r => !r.SameFormat(new TextRun())))
            ? Visibility.Visible : Visibility.Collapsed;
        PasteNote.Visibility = Visibility.Visible;
        _noteTimer?.Stop();
        _noteTimer?.Start();
    }

    void HidePasteNote()
    {
        _noteTimer?.Stop();
        PasteNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>Drops bold, colors, fonts and sizes from the given rows, keeping their text, levels and bullets.</summary>
    void TextOnly(List<string> ids)
    {
        var rows = _doc.Blocks.Where(b => ids.Contains(b.Id)).ToList();
        if (rows.Count == 0) return;
        Mark();
        foreach (var b in rows)
        {
            b.Runs = TextRun.Merge(b.Runs.Select(r => new TextRun { Text = r.Text }));
            b.RowStyle = null;
            if (_views.TryGetValue(b.Id, out var v)) LoadBox(v);
        }
        Commit();
        RenderRows();
        Toast("Kept the text only");
    }
}
