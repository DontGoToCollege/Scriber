# Scriber

Outline notepad with review comments (rows, indents, rich text, comments with replies, resolve and edit history).
Called Outline Review Notepad up to 2.0.0. 1.5.7 was a single HTML file; 2.x was a WPF + WebView2 shell around a web
page. **3.0.0 is a native WPF rewrite with no HTML anywhere** (decided with the owner on 2026-11-20, built the same day).
Code map: `Model.cs` (document, file format, import, merge), `Html.cs` (reads old rows and pasted HTML), `MainWindow.*`
(files, undo, toolbar, keys), `Outline.cs` (rows: one RichTextBox per row), `Review.cs`, `Export.cs`, `Smoke.cs`.

## 3.0 design (native rewrite)

Decisions the owner made:
- Native WPF, .NET 8, self-contained single-file `Scriber.exe` like the owner's other apps. No WebView2, no web page.
- **Document files**, like Word: each outline is a `.scriber` file (JSON) you open and save, with a recent-files list
  and autosave. The file is also what gets sent to reviewers, who need Scriber.exe to comment.
- People without Scriber get a **read-only export**: plain text, and PDF through the Windows print dialog
  ("Microsoft Print to PDF", no PDF library).
- The first release named Scriber is 3.0.0. 2.1.0 (the rename) was never released.

Design choices (Claude's; change them if they turn out wrong):
- Row text is stored as runs, not HTML: `[{text, bold, italic, underline, font, size, color}]`. The rest of the
  document keeps the 2.x shape: `title, blocks[{id, level, type: none|bullet|number, collapsed, rowStyle}],
  comments[{id, blockId, author, text, createdAt, modifiedAt, resolved, replies[], history[]}]`, plus the view
  settings (showNumbers, coloredRows, rowColorIntensity, colorway, lastTextColor).
- The reviewer's name lives in `Scriber-Data\settings.json` on each PC, never in the document (in 2.x it was saved
  into shared files, so reviewers posted under the author's name).
- Unsaved new documents autosave to `Scriber-Data\Unsaved\` so nothing is lost on a crash or close.
- **Merge review**: open a returned copy and add its new comments and replies to the open document (match by id;
  for an edited message the newer modifiedAt wins; resolved follows the newer thread). Opening a file never wipes
  the current one: it opens in its place only after a save prompt.
- **Import** old files: `.notepack` (JSON) and 2.x/1.x `.html` (read the `<script id="state">` JSON and turn
  each row's HTML into runs: b/strong, i/em, u, span style, font tags; drop everything else). This is the only HTML
  left, and it is only parsed, never shown.
- 2.x keeps its outline in WebView2 storage, which a native app can't read reliably. Upgrade path: in 2.x press
  Save backup, then open that `.notepack` in 3.0. Say so in the README and changelog.

Feature parity with 2.x (all of these stay):
- Rows: Enter adds, Tab/Shift+Tab and Alt+arrows indent, Alt+Enter adds a row one level up, bullets and numbers,
  folding (Alt+click folds a level, Alt+Shift+click folds everything, expand/collapse all), drag rows by the handle,
  multi-select (click-drag, Ctrl, Shift, Ctrl+Shift+A), row numbers, colored rows (Rainbow/Neon with intensity).
- Text: bold, italic, underline, font, size and color for selected text or whole rows; Ctrl+Alt+C then 1 (black) or 2
  (last color); paste lists from Word, web pages and plain text as rows with the same indents.
- Review panel (Esc): comment on a row (double-click or button), reply, resolve or reopen, edit with history,
  delete thread, open/resolved/all filter, sort by row or created/modified date, per-row comment badges.
- Undo/redo (Ctrl+Z/Ctrl+Y), including structural changes and comments.

New in 3.0 (approved): Ctrl+N/O/S/Shift+S, Ctrl+F find in the outline, window title shows the document name.

Bugs found in 2.x that the rewrite must not repeat:
- Pasting a multi-line list replaced the current row (its text was lost); insert below it instead, or split at the caret.
- Plain-text paste: one Tab is one level, and leading spaces count even without a bullet.
- Delete and Ctrl+Backspace act on text while typing; they remove rows only for an empty row or several selected rows.
- Esc in a comment box must not throw the draft away.
- Dropping rows onto their own child does nothing (2.x moved them to the end).
- Show a clear warning when a save fails (2.x quietly showed "Ready to save/share").

Build and check:
- `publish.ps1` keeps the archive refusal, smoke before keep, and close-and-restart of a running copy. The version is
  set in the csproj only (`AppInfo.Version` reads it).
- `--smoke` drives the real code without showing the window: rows, formatting, undo, comments, save, reopen, merge,
  2.x HTML import, paste parsing (plain, web, Word), export, find. It writes PASS/FAIL to `Scriber-Data\smoke.txt` and
  draws the window to `smoke.png`. Run it on a Debug build first.
- Look at the UI through `smoke.png`, not screenshots: PrintWindow on an off-screen WPF window captures only a blank
  client area (even with software rendering). Never real input (see the global rules).
- WPF gotchas met here: a row's TextDecorations can't be cancelled by a child run, so row underline is set per run;
  the RichTextBox's own scroller eats the mouse wheel (forwarded to the outline); its undo is off (document undo).
- Copy/paste and drag (Clip.cs): rows go on the clipboard as Scriber.Rows (JSON), HTML lists and tabbed text; Ctrl+V
  with rows selected pastes over them in one undo step (InStep). Paste readers in Html.cs: FromPlain (PDF, Notepad,
  Markdown) and ToItems (Word, web). The self-check never touches the real clipboard (_smokeClip).
- Real drag tests: Word and WordPad don't start text drags from simulated mouse input, so drops into Scriber are tested
  with a small WinForms DoDragDrop source in another process. Brave's PDF viewer ignores CDP keys; a real click into
  the page and then Ctrl+A/C copies its text.
- Setting WindowState = Maximized before the window exists maximizes on the main screen; it is set in SourceInitialized.
- Real paste tests: copy from Word through COM (Selection.Copy), and from Brave through its debug port (Runtime.evaluate
  with userGesture: selectAll + copy). Brave ignored keybd_event Ctrl+A/C. Chromium's copy sets every run to black and
  the page's font; that's kept (no dark theme, and paste keeps fonts by design).
