using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Scriber;

/// <summary>The review panel: comments on rows, replies, resolve, edit with history, filters and sorting.</summary>
public partial class MainWindow
{
    bool _review;
    string _filter = "open";
    string? _scope, _addMode;                // one row's comments / adding comments to one row
    readonly List<string> _session = new(); // comments added since "Add comments" opened
    readonly HashSet<string> _expandedRows = new(), _expandedThreads = new(), _openBoxes = new();
    readonly Dictionary<string, string> _drafts = new(); // unsent text, kept when the panel hides or redraws
    string? _focusAfterRender;
    bool _settingComposer;

    void WireReview()
    {
        NameBox.Text = _settings.ReviewerName;
        NameBox.TextChanged += (_, _) =>
        {
            _settings.ReviewerName = NameBox.Text.Trim();
            NameBox.BorderBrush = Br("#DBE2EA");
        };
        NameBox.LostKeyboardFocus += (_, _) => _settings.Save();
        FilterOpen.Click += (_, _) => SetFilter("open");
        FilterResolved.Click += (_, _) => SetFilter("resolved");
        FilterAll.Click += (_, _) => SetFilter("all");
        SortKey.ItemsSource = new[] { "Row number", "Modified date", "Created date" };
        SortKey.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _doc.Sort.Key = SortKey.SelectedIndex switch { 1 => "modified", 2 => "created", _ => "row" };
            _doc.Sort.Dir = _doc.Sort.Key == "row" ? "asc" : "desc";
            Touch();
            RenderReview();
        };
        SortDir.Click += (_, _) =>
        {
            _doc.Sort.Dir = _doc.Sort.Dir == "asc" ? "desc" : "asc";
            Touch();
            RenderReview();
        };
        NewComment.TextChanged += (_, _) =>
        {
            if (!_settingComposer && _addMode != null) _drafts["new:" + _addMode] = NewComment.Text;
        };
        PostButton.Click += (_, _) => PostComment();
    }

    void SetFilter(string f)
    {
        _filter = f;
        RenderReview();
    }

    void ResetReviewState()
    {
        _scope = _addMode = null;
        _session.Clear();
        _expandedRows.Clear();
        _expandedThreads.Clear();
        _openBoxes.Clear();
        _drafts.Clear();
    }

    void ToggleReview()
    {
        bool hadFocus = ReviewPane.IsKeyboardFocusWithin;
        _review = !_review;
        if (!_review)
        {
            _scope = null;
            // keep "Add comments" (and its draft) for next time only if something was typed
            if (_addMode != null && string.IsNullOrWhiteSpace(_drafts.GetValueOrDefault("new:" + _addMode)))
            {
                _addMode = null;
                _session.Clear();
            }
        }
        RenderReview();
        if (!_review && hadFocus && _active != null) FocusRow(_active, Caret.Keep, true);
    }

    void OpenAdd(string id)
    {
        _review = true;
        if (_addMode != id) _session.Clear();
        _addMode = id;
        _scope = null;
        RenderReview();
        if (!_smoke)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                NewComment.Focus();
                NewComment.CaretIndex = NewComment.Text.Length;
            });
    }

    void OpenScope(string id, string filter)
    {
        _review = true;
        _scope = id;
        _addMode = null;
        _filter = filter;
        _expandedRows.Add(id);
        RenderReview();
    }

    string RowPreview(string blockId)
    {
        int i = Index(blockId);
        if (i < 0) return "Deleted row";
        var t = _doc.Blocks[i].Text.Replace('\n', ' ').Trim();
        return $"{i + 1} | {(t.Length > 90 ? t[..90] + "…" : t)}";
    }

    void RenderReview()
    {
        ReviewPane.Visibility = _review ? Visibility.Visible : Visibility.Collapsed;
        ReviewColumn.Width = new GridLength(_review ? 400 : 0);
        ReviewToggle.Background = Br(_review ? "#DBEAFE" : "#FFFFFF");
        ReviewToggle.BorderBrush = Br(_review ? "#93C5FD" : "#DBE2EA");
        if (!_review) return;

        if (NameBox.Text.Trim() != _settings.ReviewerName) NameBox.Text = _settings.ReviewerName;
        OpenCount.Text = $"{_doc.Comments.Count(c => !c.Resolved)} open";
        foreach (var (b, f) in new[] { (FilterOpen, "open"), (FilterResolved, "resolved"), (FilterAll, "all") })
        {
            b.Background = Br(_filter == f ? "#2563EB" : "#FFFFFF");
            b.Foreground = Br(_filter == f ? "#FFFFFF" : "#172033");
        }
        ScopePanel.Children.Clear();
        CommentsList.Children.Clear();

        if (_addMode != null)
        {
            ReviewTitle.Text = "Add comments";
            ScopePanel.Children.Add(ScopeLine(RowPreview(_addMode), SmallButton("Done", () =>
            {
                _addMode = null;
                _session.Clear();
                _review = false;
                RenderReview();
                if (_active != null) FocusRow(_active, Caret.Keep, true);
            }), buttonFirst: false));
            SortBar.Visibility = Visibility.Collapsed;
            Composer.Visibility = Visibility.Visible;
            SetComposer(_drafts.GetValueOrDefault("new:" + _addMode, ""));
            var mine = _doc.Comments.Where(c => _session.Contains(c.Id)).ToList();
            RenderGroups(mine, true);
            if (mine.Count == 0) CommentsList.Children.Add(Muted("Only comments added in this session appear here."));
            return;
        }

        Composer.Visibility = Visibility.Collapsed;
        SortBar.Visibility = Visibility.Visible;
        ReviewTitle.Text = _scope != null ? "Row reviews" : "Review";
        if (_scope != null)
            ScopePanel.Children.Add(ScopeLine(RowPreview(_scope), SmallButton("All rows", () =>
            {
                _scope = null;
                RenderReview();
            }), buttonFirst: true));
        _syncing = true;
        SortKey.SelectedIndex = _doc.Sort.Key switch { "modified" => 1, "created" => 2, _ => 0 };
        _syncing = false;
        SortDir.Content = _doc.Sort.Dir == "asc" ? "↑" : "↓";
        var list = Filtered();
        RenderGroups(list, false);
        if (list.Count == 0) CommentsList.Children.Add(Muted("No matching review threads."));
    }

    void SetComposer(string text)
    {
        _settingComposer = true;
        if (NewComment.Text != text) NewComment.Text = text;
        _settingComposer = false;
    }

    List<Comment> Filtered()
    {
        var list = _doc.Comments.Where(c => (_scope == null || c.BlockId == _scope) &&
                                            (_filter == "all" || (_filter == "open" ? !c.Resolved : c.Resolved)));
        Func<Comment, IComparable> key = _doc.Sort.Key switch
        {
            "created" => c => c.CreatedAt,
            "modified" => c => c.ModifiedAt,
            _ => c => Index(c.BlockId) + 1,
        };
        return (_doc.Sort.Dir == "asc" ? list.OrderBy(key) : list.OrderByDescending(key)).ToList();
    }

    void RenderGroups(List<Comment> list, bool sessionOnly)
    {
        var groups = list.GroupBy(c => c.BlockId).ToList();
        if (_doc.Sort.Key == "row")
            groups = (_doc.Sort.Dir == "asc" ? groups.OrderBy(g => Index(g.Key)) : groups.OrderByDescending(g => Index(g.Key))).ToList();
        foreach (var g in groups) CommentsList.Children.Add(Group(g.Key, g.ToList(), sessionOnly));
    }

    UIElement Group(string blockId, List<Comment> items, bool sessionOnly)
    {
        bool open = _expandedRows.Contains(blockId);
        var head = HeaderButton(open, RowPreview(blockId), $"{items.Count} thread{S(items.Count)}", () =>
        {
            if (!_expandedRows.Remove(blockId)) _expandedRows.Add(blockId);
            RenderReview();
        });
        var panel = new StackPanel();
        panel.Children.Add(head);
        if (open)
        {
            var body = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
            foreach (var c in items) body.Children.Add(Thread(c, sessionOnly));
            panel.Children.Add(body);
        }
        return new Border
        {
            BorderBrush = Br("#DBE2EA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 8), Child = panel,
        };
    }

    UIElement Thread(Comment c, bool sessionOnly)
    {
        bool open = _expandedThreads.Contains(c.Id);
        var all = new List<Message> { c };
        all.AddRange(c.Replies);
        int edits = all.Sum(m => m.History.Count);
        int people = all.Select(m => m.Author).Where(a => a.Length > 0).Distinct().Count();
        var head = HeaderButton(open, c.Text.Replace('\n', ' '), $"💬 {all.Count}   ✎ {edits}   👤 {people}", () =>
        {
            if (!_expandedThreads.Remove(c.Id)) _expandedThreads.Add(c.Id);
            RenderReview();
        });
        var panel = new StackPanel();
        panel.Children.Add(head);
        if (open)
        {
            var body = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
            body.Children.Add(MessageNode(c, c, false, sessionOnly));
            foreach (var r in c.Replies) body.Children.Add(MessageNode(r, c, true, sessionOnly));
            if (!sessionOnly)
            {
                var replyKey = "reply:" + c.Id;
                if (_openBoxes.Contains(replyKey)) body.Children.Add(EditorBox(replyKey, "", "Respond", text => SendReply(c, text)));
                body.Children.Add(ButtonRow(
                    SmallButton("Respond", () => Respond(c)),
                    SmallButton(c.Resolved ? "Reopen" : "Resolve", () => ToggleResolved(c)),
                    SmallButton("Delete thread", () => DeleteThread(c))));
            }
            panel.Children.Add(body);
        }
        return new Border
        {
            Background = Br(c.Resolved ? "#F0FDF4" : "#F8FAFC"), BorderBrush = Br("#DBE2EA"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 0), Child = panel,
        };
    }

    UIElement MessageNode(Message m, Comment thread, bool isReply, bool sessionOnly)
    {
        var p = new StackPanel();
        var meta = new TextBlock { Margin = new Thickness(0, 0, 0, 3) };
        meta.Inlines.Add(new System.Windows.Documents.Run(m.Author.Length > 0 ? m.Author : "Unknown") { FontWeight = FontWeights.SemiBold });
        meta.Inlines.Add(new System.Windows.Documents.Run("   " + m.ModifiedAt.ToLocalTime().ToString("g")) { Foreground = Br("#64748B"), FontSize = 11.5 });
        p.Children.Add(meta);
        var editKey = "edit:" + m.Id;
        if (!sessionOnly && _openBoxes.Contains(editKey)) p.Children.Add(EditorBox(editKey, m.Text, "Save edit", text => SaveEdit(m, thread, text)));
        else p.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap });
        var histKey = "hist:" + m.Id;
        if (_openBoxes.Contains(histKey))
            foreach (var h in Enumerable.Reverse(m.History))
            {
                var item = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Br("#475569"), FontSize = 12, Margin = new Thickness(8, 4, 0, 0) };
                item.Inlines.Add(new System.Windows.Documents.Run(h.Author) { FontWeight = FontWeights.SemiBold });
                item.Inlines.Add(new System.Windows.Documents.Run($"   {h.ModifiedAt.ToLocalTime():g}\n") { Foreground = Br("#64748B") });
                item.Inlines.Add(new System.Windows.Documents.Run(h.Text));
                p.Children.Add(new Border { BorderBrush = Br("#E2E8F0"), BorderThickness = new Thickness(2, 0, 0, 0), Child = item, Margin = new Thickness(0, 2, 0, 0) });
            }
        var actions = new List<UIElement>();
        if (!sessionOnly) actions.Add(SmallButton("Edit", () => StartEdit(m)));
        if (m.History.Count > 0)
            actions.Add(SmallButton($"History ({m.History.Count})", () =>
            {
                if (!_openBoxes.Remove(histKey)) _openBoxes.Add(histKey);
                RenderReview();
            }));
        if (actions.Count > 0) p.Children.Add(ButtonRow(actions.ToArray()));
        return new Border
        {
            Padding = new Thickness(8, 6, 4, 6), Margin = new Thickness(isReply ? 14 : 0, 2, 0, 0),
            BorderBrush = Br("#CBD5E1"), BorderThickness = new Thickness(isReply ? 2 : 0, 0, 0, 0), Child = p,
        };
    }

    UIElement EditorBox(string key, string initial, string sendLabel, Action<string> send)
    {
        var tb = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 200, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = _drafts.TryGetValue(key, out var d) ? d : initial, Margin = new Thickness(0, 4, 0, 0),
        };
        tb.TextChanged += (_, _) => _drafts[key] = tb.Text;
        if (_focusAfterRender == key)
        {
            _focusAfterRender = null;
            if (!_smoke)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    tb.Focus();
                    tb.CaretIndex = tb.Text.Length;
                });
        }
        var p = new StackPanel();
        p.Children.Add(tb);
        var row = ButtonRow(SmallButton("Cancel", () =>
        {
            _openBoxes.Remove(key);
            _drafts.Remove(key);
            RenderReview();
        }), SmallButton(sendLabel, () => send(tb.Text.Trim()), primary: true));
        ((FrameworkElement)row).HorizontalAlignment = HorizontalAlignment.Right;
        p.Children.Add(row);
        return p;
    }

    // ---------- actions ----------

    string? RequireName()
    {
        var n = NameBox.Text.Trim();
        _settings.ReviewerName = n;
        if (n.Length > 0)
        {
            NameBox.BorderBrush = Br("#DBE2EA");
            return n;
        }
        NameBox.BorderBrush = Br("#EF4444");
        if (!_smoke) NameBox.Focus();
        Toast("Type your name first");
        return null;
    }

    void PostComment()
    {
        var author = RequireName();
        var text = NewComment.Text.Trim();
        if (author == null || text.Length == 0 || _addMode == null) return;
        Mark();
        var now = DateTimeOffset.UtcNow;
        var c = new Comment { BlockId = _addMode, Author = author, Text = text, CreatedAt = now, ModifiedAt = now };
        _doc.Comments.Add(c);
        _session.Add(c.Id);
        _expandedRows.Add(_addMode);
        _expandedThreads.Add(c.Id);
        _drafts.Remove("new:" + _addMode);
        SetComposer("");
        Commit();
        RenderRows();
        RenderReview();
        if (!_smoke) NewComment.Focus();
    }

    void Respond(Comment c)
    {
        if (RequireName() == null) return;
        _openBoxes.Add("reply:" + c.Id);
        _focusAfterRender = "reply:" + c.Id;
        RenderReview();
    }

    void SendReply(Comment c, string text)
    {
        var author = RequireName();
        if (author == null || text.Length == 0) return;
        Mark();
        var now = DateTimeOffset.UtcNow;
        c.Replies.Add(new Message { Author = author, Text = text, CreatedAt = now, ModifiedAt = now });
        c.ModifiedAt = now;
        _expandedThreads.Add(c.Id);
        _openBoxes.Remove("reply:" + c.Id);
        _drafts.Remove("reply:" + c.Id);
        Commit();
        RenderRows();
        RenderReview();
    }

    void StartEdit(Message m)
    {
        if (RequireName() == null) return;
        _openBoxes.Add("edit:" + m.Id);
        _focusAfterRender = "edit:" + m.Id;
        RenderReview();
    }

    void SaveEdit(Message m, Comment thread, string text)
    {
        var author = RequireName();
        if (author == null || text.Length == 0) return;
        _openBoxes.Remove("edit:" + m.Id);
        _drafts.Remove("edit:" + m.Id);
        if (text != m.Text)
        {
            Mark();
            m.History.Add(new HistoryEntry { Author = m.Author, Text = m.Text, ModifiedAt = m.ModifiedAt });
            m.Author = author;
            m.Text = text;
            m.ModifiedAt = DateTimeOffset.UtcNow;
            thread.ModifiedAt = m.ModifiedAt;
            _expandedThreads.Add(thread.Id);
            Commit();
        }
        RenderReview();
    }

    void ToggleResolved(Comment c)
    {
        Mark();
        c.Resolved = !c.Resolved;
        c.ModifiedAt = DateTimeOffset.UtcNow;
        Commit();
        RenderRows();
        RenderReview();
    }

    void DeleteThread(Comment c)
    {
        Mark();
        _doc.Comments.Remove(c);
        Commit();
        RenderRows();
        RenderReview();
    }

    // ---------- small builders ----------

    Button SmallButton(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(primary ? "SmallPrimary" : "Small") };
        b.Click += (_, _) => click();
        return b;
    }

    static UIElement ButtonRow(params UIElement[] buttons)
    {
        var p = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    static TextBlock Muted(string text) => new() { Text = text, Foreground = Br("#64748B"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0) };

    static UIElement ScopeLine(string text, Button button, bool buttonFirst)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(button, buttonFirst ? Dock.Left : Dock.Right);
        button.Margin = buttonFirst ? new Thickness(0, 0, 8, 0) : new Thickness(8, 0, 0, 0);
        dock.Children.Add(button);
        dock.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Background = Br("#F1F5F9"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8), Child = dock };
    }

    Button HeaderButton(bool open, string text, string right, Action click)
    {
        var dock = new DockPanel();
        var r = new TextBlock { Text = right, Foreground = Br("#64748B"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(r, Dock.Right);
        dock.Children.Add(r);
        dock.Children.Add(new TextBlock { Text = (open ? "▾  " : "▸  ") + text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Template = (ControlTemplate)FindResource("Flat"), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = dock, Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.Click += (_, _) => click();
        return b;
    }
}
