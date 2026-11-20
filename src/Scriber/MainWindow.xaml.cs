using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Scriber;

/// <summary>The document, its file, undo, the toolbar and keyboard. Rows are in Outline.cs, the review panel in
/// Review.cs, export in Export.cs and the self-check in Smoke.cs.</summary>
public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    readonly bool _smoke;
    readonly Settings _settings = Settings.Load();
    Doc _doc = Doc.New();
    string? _path;   // the .scriber file, or null while the document has never been saved as a file
    string? _draft;  // where an unsaved document autosaves (Scriber-Data\Unsaved)
    bool _dirty, _warnedSaveFailure;
    readonly DispatcherTimer _idle, _toastTimer, _chordTimer;

    // Undo keeps whole-document snapshots (JSON), like 2.x. _committed is the state at the last undo step.
    string _committed = "";
    readonly List<string> _undo = new(), _redo = new();
    bool _syncing; // controls are being set from the document, not by the user
    bool _chord;   // Alt+C was pressed; 1 or 2 picks a color

    static readonly string[] Fonts = { "Segoe UI", "Arial", "Calibri", "Times New Roman", "Georgia", "Consolas" };
    static readonly int[] Sizes = { 12, 14, 16, 18, 20, 24, 28, 32 };
    static readonly string[] TextColors =
    {
        "#000000", "#404040", "#7F7F7F", "#BFBFBF", "#FFFFFF", "#C00000", "#FF0000", "#F4B183", "#ED7D31",
        "#FFC000", "#FFFF00", "#70AD47", "#00B050", "#00B0F0", "#4472C4", "#002060", "#7030A0", "#A64D79",
    };

    public MainWindow(bool smoke, string? file)
    {
        _smoke = smoke;
        Settings.ReadOnly = smoke;
        InitializeComponent();
        VersionText.Text = "Version " + AppInfo.Version;
        if (!smoke) ApplyPlacement();

        _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            CommitTyping();
            AutoSave();
        };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        _chordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _chordTimer.Tick += (_, _) => EndChord();

        SourceInitialized += (_, _) =>
        {
            SetTitleBarColors();
            // The shortcuts lean on Alt (Alt+click, Alt+C then 1). Letting go of Alt on its own would put Windows in
            // "menu mode", which swallows the next key press. Alt+Space still works.
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(
                (IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == 0x0112 /* WM_SYSCOMMAND */ && (wParam.ToInt64() & 0xFFF0) == 0xF100 /* SC_KEYMENU */ && lParam == IntPtr.Zero)
                        handled = true;
                    return IntPtr.Zero;
                });
        };
        WireToolbar();
        WireOutline();
        WireReview();
        WireFind();
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        if (!smoke) Loaded += (_, _) => StartWith(file);
    }

    // ---------- documents and files ----------

    void StartWith(string? file)
    {
        var last = _settings.LastDocument;
        if (file != null && OpenPath(file)) return;
        if (file == null && last != null && File.Exists(last) && OpenPath(last)) return;
        LoadDocument(Doc.New(), null, null);
    }

    void LoadDocument(Doc d, string? path, string? draft)
    {
        _idle.Stop();
        _doc = d;
        _path = path;
        _draft = draft;
        _dirty = false;
        _warnedSaveFailure = false;
        _undo.Clear();
        _redo.Clear();
        _committed = Core();
        _active = d.Blocks[0].Id;
        _selected.Clear();
        _selected.Add(_active);
        _anchor = 0;
        ResetReviewState();
        RenderRows();
        RenderReview();
        SyncViewControls();
        UpdateHistoryButtons();
        UpdateTitle();
        SetStatus(path != null ? "Saved" : draft != null ? "Draft (not saved as a file yet)" : "", false);
        if (path != null) _settings.AddRecent(path);
        _settings.LastDocument = path ?? draft;
        _settings.Save();
        if (FindBar.Visibility == Visibility.Visible) UpdateFind();
    }

    static bool IsEmpty(Doc d) => d.Comments.Count == 0 && d.Blocks.All(b => b.Text.Trim().Length == 0);

    void NewDocument()
    {
        if (ConfirmLeave()) LoadDocument(Doc.New(), null, null);
    }

    const string OpenFilter = "Outlines (*.scriber, *.notepack, *.html)|*.scriber;*.notepack;*.json;*.html;*.htm|" +
                              "Scriber outline (*.scriber)|*.scriber|All files (*.*)|*.*";

    void OpenDialog()
    {
        var dlg = new OpenFileDialog { Title = "Open outline", Filter = OpenFilter };
        if (_path != null) dlg.InitialDirectory = Path.GetDirectoryName(_path);
        if (dlg.ShowDialog(this) != true || !ConfirmLeave()) return;
        OpenPath(dlg.FileName);
    }

    /// <summary>Opens a .scriber file in place of the current one (call ConfirmLeave first). Older files (.notepack,
    /// 1.x/2.x .html) are imported as a new unsaved document, so they are never overwritten with the new format.</summary>
    bool OpenPath(string file)
    {
        Doc d;
        try
        {
            d = DocFile.Load(file);
        }
        catch (Exception ex)
        {
            if (_smoke) throw;
            Ask($"Could not open {Path.GetFileName(file)}:\n\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (string.Equals(dir, Path.GetFullPath(PortablePaths.UnsavedDir), StringComparison.OrdinalIgnoreCase))
            LoadDocument(d, null, file);
        else if (Path.GetExtension(file).Equals(AppInfo.FileExtension, StringComparison.OrdinalIgnoreCase))
            LoadDocument(d, file, null);
        else
        {
            LoadDocument(d, null, null);
            Touch();
            Toast($"Imported {Path.GetFileName(file)}. Save it to keep it as a Scriber file.");
        }
        return true;
    }

    /// <summary>Before another document takes this one's place: nothing may be lost without asking.</summary>
    bool ConfirmLeave()
    {
        CommitTyping();
        AutoSave();
        if (_path == null && !IsEmpty(_doc))
        {
            var r = Ask("Save this outline as a file first?\n\nNo throws it away.", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return SaveAs();
            DeleteDraft();
            return true;
        }
        if (_dirty)
            return Ask("The latest changes could not be saved. Close this outline anyway and lose them?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        return true;
    }

    /// <summary>Saves to the open file (or the unsaved draft) when there are changes. Says clearly when it fails.</summary>
    bool AutoSave()
    {
        if (!_dirty) return true;
        var target = _path;
        if (target == null)
        {
            if (_draft == null)
            {
                Directory.CreateDirectory(PortablePaths.UnsavedDir);
                _draft = Path.Combine(PortablePaths.UnsavedDir, $"Untitled {DateTime.Now:yyyy-MM-dd HHmmss}{AppInfo.FileExtension}");
                _settings.AddRecent(_draft);
                _settings.LastDocument = _draft;
                _settings.Save();
            }
            target = _draft;
        }
        try
        {
            DocFile.Save(_doc, target);
            _dirty = false;
            _warnedSaveFailure = false;
            SetStatus(_path != null ? "Saved" : "Draft (not saved as a file yet)", false);
            return true;
        }
        catch (Exception ex)
        {
            if (_smoke) throw;
            PortablePaths.Log("save failed: " + ex);
            SetStatus("⚠ Not saved: " + ex.Message, true);
            if (!_warnedSaveFailure)
            {
                _warnedSaveFailure = true;
                Ask($"Scriber could not save {Path.GetFileName(target)}:\n\n{ex.Message}\n\nYour changes are still on screen. " +
                    "Use File > Save as to save them somewhere else.", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }
    }

    bool SaveNow()
    {
        CommitTyping();
        if (_path == null) return SaveAs();
        _dirty = true;
        _warnedSaveFailure = false;
        return AutoSave();
    }

    bool SaveAs()
    {
        CommitTyping();
        var dlg = new SaveFileDialog
        {
            Title = "Save outline", Filter = "Scriber outline (*.scriber)|*.scriber", DefaultExt = AppInfo.FileExtension,
            AddExtension = true, FileName = SafeName(_doc.Title) + AppInfo.FileExtension,
        };
        if (_path != null) dlg.InitialDirectory = Path.GetDirectoryName(_path);
        return dlg.ShowDialog(this) == true && SaveTo(dlg.FileName);
    }

    bool SaveTo(string file)
    {
        try
        {
            DocFile.Save(_doc, file);
        }
        catch (Exception ex)
        {
            if (_smoke) throw;
            SetStatus("⚠ Not saved: " + ex.Message, true);
            Ask($"Could not save {Path.GetFileName(file)}:\n\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        DeleteDraft();
        _path = file;
        _dirty = false;
        _settings.AddRecent(file);
        _settings.LastDocument = file;
        _settings.Save();
        UpdateTitle();
        SetStatus("Saved", false);
        return true;
    }

    void DeleteDraft()
    {
        if (_draft == null) return;
        try
        {
            File.Delete(_draft);
        }
        catch (Exception ex)
        {
            PortablePaths.Log("draft not deleted: " + ex.Message);
        }
        _settings.Recent.Remove(_draft);
        if (_settings.LastDocument == _draft) _settings.LastDocument = null;
        _draft = null;
    }

    static string SafeName(string title)
    {
        var s = Regex.Replace(title ?? "", @"[\\/:*?""<>|]+", "-").Trim().Trim('.');
        return s.Length == 0 ? "outline" : s;
    }

    void MergeDialog()
    {
        var dlg = new OpenFileDialog { Title = "Merge a review copy", Filter = OpenFilter };
        if (_path != null) dlg.InitialDirectory = Path.GetDirectoryName(_path);
        if (dlg.ShowDialog(this) == true) MergeFile(dlg.FileName);
    }

    /// <summary>Adds the new comments and replies from a reviewer's copy to the open outline.</summary>
    DocFile.MergeResult MergeFile(string file)
    {
        Doc d;
        try
        {
            d = DocFile.Load(file);
        }
        catch (Exception ex)
        {
            if (_smoke) throw;
            Ask($"Could not open {Path.GetFileName(file)}:\n\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Warning);
            return default;
        }
        Mark();
        var r = DocFile.Merge(_doc, d);
        Commit();
        RenderRows();
        _review = true;
        _addMode = _scope = null;
        RenderReview();
        Toast(r.Any
            ? $"Merged: {r.Comments} new comment{S(r.Comments)}, {r.Replies} new repl{(r.Replies == 1 ? "y" : "ies")}, {r.Updates} update{S(r.Updates)}"
            : "Nothing new in that copy");
        return r;
    }

    static string S(int n) => n == 1 ? "" : "s";

    void ShowFileMenu()
    {
        var m = new ContextMenu { PlacementTarget = FileButton, Placement = PlacementMode.Bottom };
        MenuItem Item(string text, string? keys, Action click)
        {
            var mi = new MenuItem { Header = text, InputGestureText = keys ?? "" };
            mi.Click += (_, _) => click();
            return mi;
        }
        m.Items.Add(Item("New", "Ctrl+N", NewDocument));
        m.Items.Add(Item("Open…", "Ctrl+O", OpenDialog));
        var recent = new MenuItem { Header = "Open recent" };
        foreach (var p in _settings.Recent.Where(File.Exists))
        {
            var path = p;
            bool draft = string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), Path.GetFullPath(PortablePaths.UnsavedDir),
                StringComparison.OrdinalIgnoreCase);
            var label = draft ? $"Unsaved draft from {File.GetLastWriteTime(p):g}" : $"{Path.GetFileName(p)}   ({Path.GetDirectoryName(p)})";
            recent.Items.Add(Item(label, null, () =>
            {
                if (ConfirmLeave()) OpenPath(path);
            }));
        }
        recent.IsEnabled = recent.Items.Count > 0;
        m.Items.Add(recent);
        m.Items.Add(Item("Save", "Ctrl+S", () => SaveNow()));
        m.Items.Add(Item("Save as…", "Ctrl+Shift+S", () => SaveAs()));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Merge a review copy…", null, MergeDialog));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Export as text…", null, ExportTextDialog));
        m.Items.Add(Item("Print or save as PDF…", null, PrintOutline));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Exit", "Alt+F4", Close));
        m.IsOpen = true;
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_smoke) return;
        CommitTyping();
        if (!AutoSave() && Ask("The latest changes could not be saved. Close anyway and lose them?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        SavePlacement();
    }

    MessageBoxResult Ask(string text, MessageBoxButton buttons, MessageBoxImage icon)
    {
        if (_smoke) throw new InvalidOperationException("A dialog came up during the self-check: " + text);
        return IsVisible ? MessageBox.Show(this, text, AppInfo.Name, buttons, icon) : MessageBox.Show(text, AppInfo.Name, buttons, icon);
    }

    void UpdateTitle() => Title = $"{(_path != null ? Path.GetFileNameWithoutExtension(_path) : "Untitled")} - {AppInfo.Name}";

    void SetStatus(string text, bool error)
    {
        Status.Text = text;
        Status.Foreground = error ? Brushes.Firebrick : Br("#64748B");
        Status.FontWeight = error ? FontWeights.SemiBold : FontWeights.Normal;
    }

    void Toast(string text)
    {
        ToastText.Text = text;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------- undo ----------

    string Core() => DocFile.ToJson(_doc);

    /// <summary>Something changed: autosave soon (and make typing an undo step when it pauses).</summary>
    void Touch()
    {
        _dirty = true;
        if (Status.Foreground != Brushes.Firebrick) SetStatus("Saving…", false);
        _idle.Stop();
        _idle.Start();
    }

    /// <summary>Typing since the last undo step becomes an undo step of its own.</summary>
    void CommitTyping()
    {
        var now = Core();
        if (now == _committed) return;
        PushUndo(_committed);
        _committed = now;
    }

    /// <summary>Call before a change; Commit() after it.</summary>
    void Mark()
    {
        CommitTyping();
        PushUndo(_committed);
    }

    void Commit()
    {
        _committed = Core();
        Touch();
        UpdateHistoryButtons();
    }

    void PushUndo(string snapshot)
    {
        if (_undo.Count == 0 || _undo[^1] != snapshot) _undo.Add(snapshot);
        if (_undo.Count > 150) _undo.RemoveAt(0);
        _redo.Clear();
        UpdateHistoryButtons();
    }

    void Undo()
    {
        CommitTyping();
        var cur = Core();
        while (_undo.Count > 0 && _undo[^1] == cur) _undo.RemoveAt(_undo.Count - 1);
        if (_undo.Count == 0) return;
        _redo.Add(cur);
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(s);
    }

    void Redo()
    {
        CommitTyping();
        if (_redo.Count == 0) return;
        _undo.Add(Core());
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(s);
    }

    void Restore(string json)
    {
        _doc = DocFile.FromJson(json);
        _committed = Core();
        _active = _doc.Blocks[Math.Clamp(_anchor, 0, _doc.Blocks.Count - 1)].Id;
        _selected.Clear();
        _selected.Add(_active);
        RenderRows();
        RenderReview();
        SyncViewControls();
        UpdateHistoryButtons();
        Touch();
        FocusRow(_active);
    }

    void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0 || Core() != _committed;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    // ---------- toolbar ----------

    void WireToolbar()
    {
        FileButton.Click += (_, _) => ShowFileMenu();
        SaveButton.Click += (_, _) => SaveNow();
        ReviewToggle.Click += (_, _) => ToggleReview();
        UndoButton.Click += (_, _) => Undo();
        RedoButton.Click += (_, _) => Redo();
        BoldButton.Click += (_, _) => Exec("bold");
        ItalicButton.Click += (_, _) => Exec("italic");
        UnderlineButton.Click += (_, _) => Exec("underline");
        FontBox.ItemsSource = Fonts;
        FontBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && FontBox.SelectedItem is string f) ApplyFont(f);
        };
        SizeBox.ItemsSource = Sizes;
        SizeBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && SizeBox.SelectedItem is int px) ApplySize(px);
        };
        ColorApply.Click += (_, _) => ApplyColor(_doc.LastTextColor, true);
        ColorMenu.Click += (_, _) => ColorPopup.IsOpen = !ColorPopup.IsOpen;
        foreach (var c in TextColors)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Tool"), Width = 24, Height = 24, MinWidth = 24, Margin = new Thickness(2),
                Background = Br(c), BorderBrush = Br("#CBD5E1"), BorderThickness = new Thickness(1), ToolTip = c,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, c); // a name for screen readers and UI Automation
            b.Click += (_, _) =>
            {
                ColorPopup.IsOpen = false;
                ApplyColor(c, true);
            };
            ColorGrid.Children.Add(b);
        }
        BulletButton.Click += (_, _) => LineType("bullet");
        NumberButton.Click += (_, _) => LineType("number");
        OutdentButton.Click += (_, _) => ChangeIndent(-1);
        IndentButton.Click += (_, _) => ChangeIndent(1);
        AddButton.Click += (_, _) =>
        {
            if (ActiveBlock() is { } b) AddRow(b, b.Level);
        };
        CommentButton.Click += (_, _) =>
        {
            if (_active != null) OpenAdd(_active);
        };
        ExpandButton.Click += (_, _) => SetAllCollapsed(false);
        CollapseButton.Click += (_, _) => SetAllCollapsed(true);
        NumbersCheck.Click += (_, _) => ChangeView(() => _doc.ShowNumbers = NumbersCheck.IsChecked == true);
        ColoredCheck.Click += (_, _) => ChangeView(() => _doc.ColoredRows = ColoredCheck.IsChecked == true);
        IntensitySlider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _doc.RowColorIntensity = IntensitySlider.Value;
            Touch(); // becomes one undo step once the slider stops moving
            RenderRows();
        };
        ColorwayBox.ItemsSource = new[] { "Rainbow", "Neon" };
        ColorwayBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing) ChangeView(() => _doc.Colorway = ColorwayBox.SelectedIndex == 1 ? "neon" : "rainbow");
        };
        TitleBox.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _doc.Title = TitleBox.Text;
            Touch();
        };
        TitleBox.LostKeyboardFocus += (_, _) => CommitTyping();
    }

    void ChangeView(Action change)
    {
        Mark();
        change();
        Commit();
        RenderRows();
    }

    /// <summary>Puts the document's view settings into the toolbar.</summary>
    void SyncViewControls()
    {
        _syncing = true;
        NumbersCheck.IsChecked = _doc.ShowNumbers;
        ColoredCheck.IsChecked = _doc.ColoredRows;
        IntensitySlider.Value = _doc.RowColorIntensity;
        ColorwayBox.SelectedIndex = _doc.Colorway == "neon" ? 1 : 0;
        if (TitleBox.Text != _doc.Title) TitleBox.Text = _doc.Title;
        _syncing = false;
        RefreshFormatControls();
    }

    // ---------- keyboard ----------

    void StartChord()
    {
        _chord = true;
        _chordTimer.Stop();
        _chordTimer.Start();
        Toast("Color hotkey: press 1 for black or 2 for the last color");
    }

    void EndChord()
    {
        _chord = false;
        _chordTimer.Stop();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        var focus = Keyboard.FocusedElement;
        var row = (focus as RichTextBox)?.Tag as RowView;
        bool inTextBox = focus is TextBox;

        if (alt && !ctrl && key == Key.C)
        {
            StartChord();
            e.Handled = true;
            return;
        }
        if (_chord && key is Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2)
        {
            EndChord();
            bool black = key is Key.D1 or Key.NumPad1;
            ApplyColor(black ? "#000000" : _doc.LastTextColor, false);
            e.Handled = true;
            return;
        }
        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.N:
                    NewDocument();
                    e.Handled = true;
                    return;
                case Key.O:
                    OpenDialog();
                    e.Handled = true;
                    return;
                case Key.S:
                    if (shift) SaveAs();
                    else SaveNow();
                    e.Handled = true;
                    return;
                case Key.F:
                    ShowFind();
                    e.Handled = true;
                    return;
                case Key.A when shift && !inTextBox:
                    SelectAllRows();
                    e.Handled = true;
                    return;
            }
        }
        if (focus == FindBox)
        {
            if (key == Key.Enter) StepFind(shift ? -1 : 1);
            else if (key == Key.Escape) CloseFind();
            else return;
            e.Handled = true;
            return;
        }
        if (inTextBox)
        {
            // Esc hides the panel; whatever was typed in a comment box stays there
            if (key == Key.Escape && focus != TitleBox)
            {
                ToggleReview();
                e.Handled = true;
            }
            return;
        }

        if (ctrl && key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }
        if (ctrl && key == Key.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }
        if (key == Key.Escape)
        {
            ToggleReview();
            e.Handled = true;
            return;
        }
        if (alt && key is Key.Left or Key.Right)
        {
            ChangeIndent(key == Key.Right ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (alt && key == Key.Enter && row != null)
        {
            AddRow(row.B, Math.Max(0, row.B.Level - 1));
            e.Handled = true;
            return;
        }
        if (key == Key.Tab && !ctrl && !alt)
        {
            ChangeIndent(shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        // Delete and Backspace edit text while typing; they remove rows only for an empty row or several selected rows.
        bool many = _selected.Count > 1;
        bool rowsFocused = row == null && _selected.Count > 0;
        if (key == Key.Delete && !ctrl && !alt)
        {
            if (many || rowsFocused || (row != null && row.B.Runs.Count == 0))
            {
                RemoveSelected(false);
                e.Handled = true;
            }
            return;
        }
        if (key == Key.Back && !alt)
        {
            if (ctrl && many) ClearSelectedText();
            else if (many || rowsFocused || (row != null && row.B.Runs.Count == 0)) RemoveSelected(true);
            else return;
            e.Handled = true;
            return;
        }
        if (row == null) return;
        if (ctrl && key is Key.B or Key.I or Key.U)
        {
            Exec(key == Key.B ? "bold" : key == Key.I ? "italic" : "underline");
            e.Handled = true;
            return;
        }
        if (ctrl && shift && key == Key.V)
        {
            PasteInto(row, SafeClipboard(), true);
            e.Handled = true;
            return;
        }
        if (key == Key.Enter && !shift && !ctrl && !alt)
        {
            SplitRow(row);
            e.Handled = true;
            return;
        }
        if (key is Key.Up or Key.Down && mods == ModifierKeys.None && OnEdgeLine(row.Box, key == Key.Up ? -1 : 1))
            e.Handled = MoveToRow(key == Key.Up ? -1 : 1);
    }

    static IDataObject? SafeClipboard()
    {
        try
        {
            return Clipboard.GetDataObject();
        }
        catch
        {
            return null;
        }
    }

    // ---------- window ----------

    static readonly Dictionary<string, SolidColorBrush> Brushes_ = new();

    internal static SolidColorBrush Br(string hex)
    {
        if (Brushes_.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return Brushes_[hex] = b;
    }

    /// <summary>Title bar in the app's own colors (Windows 11; older Windows keeps its default bar).</summary>
    void SetTitleBarColors()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var bg = (Color)ColorConverter.ConvertFromString(AppInfo.Background);
        var text = (Color)ColorConverter.ConvertFromString(AppInfo.Text);
        int dark = 0;
        int caption = bg.R | (bg.G << 8) | (bg.B << 16);
        int captionText = text.R | (text.G << 8) | (text.B << 16);
        DwmSetWindowAttribute(hwnd, 20 /* dark mode */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35 /* caption color */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* caption text */, ref captionText, sizeof(int));
    }

    void ApplyPlacement()
    {
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top))
        {
            // only restore the position if it is still on a visible screen
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vr = vl + SystemParameters.VirtualScreenWidth;
            var vb = vt + SystemParameters.VirtualScreenHeight;
            if (_settings.Left > vl - 50 && _settings.Left < vr - 100 && _settings.Top > vt - 10 && _settings.Top < vb - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.Left;
                Top = _settings.Top;
            }
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    void SavePlacement()
    {
        var r = WindowState != WindowState.Normal ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (!r.IsEmpty)
        {
            _settings.Left = r.Left;
            _settings.Top = r.Top;
            _settings.Width = r.Width;
            _settings.Height = r.Height;
        }
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.ReviewerName = NameBox.Text.Trim();
        _settings.Save();
    }
}
