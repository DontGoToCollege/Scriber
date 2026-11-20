# Scriber

A notepad for outlines that other people can review. Write in indented rows, and anyone can attach comments to a row,
reply, resolve them and see how each comment was edited. One `Scriber.exe` with no install and no internet.

## Download

Download `Scriber.exe` from the [latest release](https://github.com/DontGoToCollege/Scriber/releases/latest). It's a
single program: no installer, just run it.

## Use it

- **Write**: each line is a row. `Enter` adds a row (text after the caret moves down with it), `Tab` and `Shift+Tab`
  indent and outdent. Rows can be bullets or numbered, folded, dragged to a new place by their `⋮` handle, and
  selected several at a time.
- **Format**: bold, italic, underline, font, size and text color, for the selected text or for whole rows. Rows can be
  tinted by level (Rainbow or Neon, with adjustable strength), and row numbers can be shown.
- **Paste** a list from Word, a web page or plain text and it becomes rows with the same indents. The row you paste
  into keeps its text.
- **Review**: double-click a row (or press ▣) to comment on it. Open the Review panel with `Esc` to see every thread,
  reply, resolve or reopen, edit, and look at a comment's history. Filter by open or resolved and sort by row or date.
  Your name is kept on your PC, not in the outline.
- **Find** with `Ctrl+F`.
- **Undo and redo** with `Ctrl+Z` and `Ctrl+Y`.

### Files

Each outline is a `.scriber` file, like a Word document: `Ctrl+N` new, `Ctrl+O` open, `Ctrl+S` save,
`Ctrl+Shift+S` save as, and **File > Open recent**. Once an outline is a file, every change is saved into it by
itself. A new outline that hasn't been saved yet is kept as a draft in `Scriber-Data\Unsaved` beside the exe, and comes
back the next time you start Scriber. Opening another outline never throws away the one on screen without asking.

### Sharing for review

- Send the `.scriber` file. Reviewers open it in their own copy of Scriber, add comments and replies, and send it back.
- **File > Merge a review copy** adds their new comments and replies to your outline. Edited comments keep the newer
  version; changes to the rows themselves are not merged (reviewers comment, you write).
- For people without Scriber: **File > Export as text** makes a plain `.txt` with the outline and its comments, and
  **File > Print or save as PDF** prints it (pick "Microsoft Print to PDF" for a PDF).

### Shortcuts

| Keys | What it does |
|---|---|
| Click-drag, `Ctrl`+click, `Shift`+click | Select rows |
| `Ctrl+Shift+A` | Select all rows |
| `Alt`+click | Fold or unfold every row at that level |
| `Alt+Shift`+click | Fold or unfold everything |
| `Alt+←` / `Alt+→` | Outdent / indent |
| `Alt+Enter` | New row one level up |
| `Shift+Enter` | Line break inside a row |
| `Ctrl+Alt+C` then `1` | Black text |
| `Ctrl+Alt+C` then `2` | Last used text color |
| `Ctrl+Shift+V` | Paste as plain text |
| Double-click | Comment on the row |
| `Delete` (on an empty row or several rows) | Remove the rows and everything under them |
| `Backspace` (on an empty row or several rows) | Remove the rows but keep what is under them |
| `Esc` | Show or hide Review (anything typed in a comment box stays there) |

## Coming from version 2 (Outline Review Notepad)

Version 2 kept its outline inside its own browser storage, which Scriber 3 can't read. To bring it over: open version
2, press **Save backup** (◫), then open that `.notepack` file in Scriber 3 (`Ctrl+O`) and save it as a `.scriber`
file. Pages saved with **Save HTML** from version 1 or 2 open the same way.

## History

Scriber was called Outline Review Notepad until version 2. It began as a single HTML file opened in a browser (up to
1.5.7), became a desktop program wrapping that page in 2.0.0, and from 3.0.0 is a native Windows program with
document files. Every version is on the [releases page](https://github.com/DontGoToCollege/Scriber/releases). See
[CHANGELOG.md](CHANGELOG.md).

## Build

```powershell
.\publish.ps1          # -> Scriber.exe (self-contained .NET 8 WPF)
.\publish.ps1 -Run
```

Needs the .NET 8 SDK. The version is set only in `src\Scriber\Scriber.csproj`. The script refuses to rebuild a version
that is already in `Versions\Archive`, and only keeps a build that passes its own self-check: `Scriber.exe --smoke`
makes an outline, edits, comments, saves, reopens, merges a reviewer's copy, imports a 2.x file, pastes and exports,
then writes `PASS` or `FAIL` to `Scriber-Data\smoke.txt` and a picture of the window to `smoke.png`.

- `src\Scriber\Model.cs`: the document and its file format, import and merge. `Html.cs` reads old rows and pasted
  HTML (only reads it; nothing is shown as HTML).
- `MainWindow.*`: files, undo, toolbar and keys; `Outline.cs` the rows; `Review.cs` the review panel; `Export.cs`
  text and print; `Smoke.cs` the self-check.
- `tools\make-icon.ps1`: redraws `src\Scriber\app.ico`.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
