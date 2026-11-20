# Changelog

## 3.1.0 (2026-11-20)

Copying and pasting between Scriber and other programs.

- **Copy and cut rows** with `Ctrl+C` / `Ctrl+X` (several rows selected, or a row with no text selected). They
  paste into Word, Outlook, Google Docs and web editors as real nested bulleted and numbered lists with their bold,
  italics and colors; into Notepad and chats as text with one Tab per level; and into Scriber with nothing lost
  (comments come along when you cut and paste).
- **Paste in place of selected rows** with `Ctrl+V`, **duplicate** with `Ctrl+D`, select rows with
  `Shift+↑` / `Shift+↓`, and `Ctrl+A` twice selects every row. `Ctrl+B` / `I` / `U` work on several selected
  rows.
- **Smarter paste**: text copied from a PDF keeps its structure (wrapped lines are joined, and Word's bullets and
  numbering styles set the levels). Markdown (`- lists`, `# headings`, `**bold**`, `*italic*`), letters,
  roman numerals, `(1)` and `1.2.3` numbering are understood. Word headings become outline levels, and Word rows
  moved with Increase Indent keep their level.
- After a paste, a note offers **Undo** and **Text only** (keep the text and levels, drop the formatting).
- **Drag and drop**: drag rows by their handle into Word or another program, and drag text in from other programs.
- Down or Up on the first or last row ends a multi-row selection.

## 3.0.2 (2026-11-20)

- A window closed on a screen with a different scale (say 100% next to 125%) reopens where you left it. It used to
  creep further away each time, and could end up off the screen.

## 3.0.1 (2026-11-20)

- The text color shortcut is now `Ctrl+Alt+C`, then `1` (black) or `2` (last color). `Alt+C` is often taken by
  other tools (AutoHotkey scripts, for one), so it never reached Scriber.
- A window left maximized on a second screen opens maximized there again (it used to jump to the main screen).
- Opening an outline puts the caret in its first row, so you can type or paste right away without clicking.

## 3.0.0 (2026-11-20)

The first release named Scriber, and a rewrite: a native Windows program with no web page inside.

- **Document files.** Each outline is a `.scriber` file you open and save (`Ctrl+N`, `Ctrl+O`, `Ctrl+S`,
  `Ctrl+Shift+S`, Open recent). Saved outlines save themselves as you work; new ones are kept as a draft until you
  save them. The window title shows the document's name. Opening another outline never wipes the current one without
  asking.
- **Reviewing by file.** Send the `.scriber` file; reviewers comment in their own Scriber. **Merge a review copy**
  adds their new comments and replies to yours.
- **For people without Scriber:** export as plain text, or print / save as PDF.
- **Find** with `Ctrl+F`.
- Your reviewer name is kept on your PC only. (Version 2 saved it into shared files, so reviewers could end up
  posting under the author's name.)
- No longer needs the WebView2 runtime.
- Fixes from version 2: pasting a multi-line list no longer replaces the row's text; plain-text paste counts one Tab
  as one level and leading spaces even without bullets; `Delete` and `Ctrl+Backspace` edit text while typing (they
  remove rows only on an empty row or several selected rows); `Esc` in a comment box keeps what you typed; dropping
  rows onto their own child does nothing; a failed save shows a clear warning.
- `Enter` in the middle of a row moves the text after the caret to the new row. `Up` and `Down` move between lines of
  a long row before moving to the next row.
- **Upgrading from 2.x:** version 2 kept its outline in browser storage that version 3 can't read. In version 2 press
  **Save backup**, then open that `.notepack` in Scriber 3 and save it as a `.scriber` file. Saved `.html` pages from
  versions 1 and 2 open too.

## 2.1.0 (never released; part of 3.0.0)

- Renamed from Outline Review Notepad to Scriber. The exe is now `Scriber.exe` and its data folder `Scriber-Data`.
  An existing `OutlineReviewNotepad-Data` folder beside the exe is renamed on first run, so the outline carries over.

## 2.0.0 (2026-11-11)

- Outline Review Notepad is now a desktop program: one portable `OutlineReviewNotepad.exe` instead of an HTML file in
  a browser.
- The outline is kept in `OutlineReviewNotepad-Data` beside the exe. It no longer depends on which browser opened the
  file or where the file was.
- Save HTML and Save backup use normal Windows Save dialogs. The saved HTML still opens in any browser for reviewers.
- Window size and position are remembered.
- The version is shown from one place, and saved files are named after it.

## 1.5.7

- The last single-file HTML version. It labels itself 1.5.6 inside.
