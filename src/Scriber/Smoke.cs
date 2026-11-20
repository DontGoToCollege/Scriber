using System.IO;
using System.Windows;
using System.Windows.Documents;

namespace Scriber;

/// <summary>Self-check behind --smoke: drives the real editing, review and file code (no window shown), then writes
/// PASS or FAIL with the details to smoke.txt in the data folder.</summary>
public partial class MainWindow
{
    const string Fixture2x = """
        <!doctype html><html><body><script id="state" type="application/json">{"version":"2.0.0","title":"Old outline",
        "reviewerName":"Author Name","showRowNumbers":true,"blocks":[
        {"id":"a","level":0,"type":"bullet","html":"<b>Bold</b> and <span style=\"color: rgb(255, 0, 0);\">red<\/span>","rowStyle":{"font-size":"18px"}},
        {"id":"b","level":1,"type":"number","html":"two<br>lines &amp; <font face=\"Georgia\">serif</font>","collapsed":false}],
        "comments":[{"id":"c1","blockId":"a","author":"Pat","text":"Old note","createdAt":"2026-11-01T10:00:00.000Z",
        "updatedAt":"2026-11-02T10:00:00.000Z","resolved":false,"replies":[{"id":"r1","author":"Sam","text":"Agreed","createdAt":"2026-11-03T10:00:00.000Z"}]}]}</script></body></html>
        """;

    const string FixtureWord = """
        <p class=MsoListParagraphCxSpFirst style='mso-list:l0 level1 lfo1'><![if !supportLists]><span style='font-family:Symbol'>·<span>&nbsp;&nbsp;</span></span><![endif]><b>One</b></p>
        <p class=MsoListParagraphCxSpLast style='mso-list:l0 level2 lfo1'><![if !supportLists]><span>1.<span>&nbsp;</span></span><![endif]>Two</p>
        """;

    public void RunSmoke()
    {
        var log = new List<string>();
        void Check(bool ok, string what)
        {
            log.Add((ok ? "ok   " : "FAIL ") + what);
            if (!ok) throw new Exception("check failed: " + what);
        }
        var report = Path.Combine(PortablePaths.DataDir, "smoke.txt");
        try
        {
            var dir = Path.Combine(PortablePaths.DataDir, "smoke-files");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            // rows: type, split with Enter, indent, bullet, row bold, color on selected text
            LoadDocument(Doc.New(), null, null);
            _doc.Title = "Smoke";
            var v0 = _views[_doc.Blocks[0].Id];
            v0.Box.Document.Blocks.Clear();
            v0.Box.Document.Blocks.Add(new Paragraph(new Run("First row")));
            Check(_doc.Blocks[0].Text == "First row", "typing reaches the document");
            v0.Box.CaretPosition = PointerAt(v0.Box.Document, 5);
            SplitRow(v0);
            Check(_doc.Blocks.Count == 2 && _doc.Blocks[0].Text == "First" && _doc.Blocks[1].Text == " row", "Enter splits the row at the caret");
            var b1 = _doc.Blocks[1];
            FocusRow(b1.Id);
            ChangeIndent(1);
            Check(b1.Level == 1, "Tab indents");
            ChangeIndent(1);
            Check(b1.Level == 1, "no deeper than one level under the row above");
            LineType("bullet");
            Check(b1.Type == "bullet", "bullet row");
            FocusRow(_doc.Blocks[0].Id);
            Exec("bold");
            Check(_doc.Blocks[0].RowStyle?["font-weight"] == "700" && _views[_doc.Blocks[0].Id].Box.Document.FontWeight.ToOpenTypeWeight() >= 600, "whole-row bold");
            FocusRow(b1.Id);
            var v1 = _views[b1.Id];
            v1.Box.Selection.Select(PointerAt(v1.Box.Document, 1), PointerAt(v1.Box.Document, 4));
            ApplyColor("#FF0000", true);
            Check(b1.Runs.Count == 2 && b1.Runs[1].Text == "row" && b1.Runs[1].Color == "#FF0000" && b1.Runs[0].Color == null, "color on selected text only");

            // undo and redo, including structure
            int rows = _doc.Blocks.Count;
            AddRow(b1, b1.Level);
            Check(_doc.Blocks.Count == rows + 1, "add row");
            Undo();
            Check(_doc.Blocks.Count == rows, "undo add row");
            Redo();
            Check(_doc.Blocks.Count == rows + 1, "redo add row");
            Undo();
            Check(_doc.Blocks[1].Runs.Any(r => r.Color == "#FF0000"), "undo keeps earlier formatting");

            // comments and replies (the name lives on this PC only)
            NameBox.Text = "Smoke Author";
            OpenAdd(_doc.Blocks[0].Id);
            NewComment.Text = "Looks good";
            PostComment();
            Check(_doc.Comments.Count == 1 && _doc.Comments[0].Author == "Smoke Author", "comment on a row");
            Check(_views[_doc.Blocks[0].Id].Badges.Children.Count == 1, "row shows a comment badge");
            SendReply(_doc.Comments[0], "Thanks");
            Check(_doc.Comments[0].Replies.Count == 1, "reply");
            SaveEdit(_doc.Comments[0], _doc.Comments[0], "Looks very good");
            Check(_doc.Comments[0].History.Count == 1 && _doc.Comments[0].Text == "Looks very good", "edit keeps history");
            ToggleReview();
            Check(_drafts.Count == 0 || !_review, "Esc hides the review panel");

            // save, reopen
            var file = Path.Combine(dir, "smoke" + AppInfo.FileExtension);
            Check(SaveTo(file), "save");
            Check(!File.ReadAllText(file).Contains("Smoke Author\",\"reviewerName"), "reviewer name not stored in the file");
            LoadDocument(Doc.New(), null, null);
            Check(OpenPath(file) && _path == file, "reopen");
            Check(_doc.Title == "Smoke" && _doc.Blocks.Count == 2 && _doc.Blocks[1].Level == 1 && _doc.Blocks[1].Type == "bullet", "rows survive saving");
            Check(_doc.Blocks[1].Runs.Any(r => r.Color == "#FF0000") && RowFmt.Of(_doc.Blocks[0]).Bold, "formatting survives saving");
            Check(_doc.Comments.Count == 1 && _doc.Comments[0].Replies.Count == 1 && _doc.Comments[0].History.Count == 1, "comments survive saving");

            // merge a reviewer's copy
            var copy = DocFile.Load(file);
            var later = DateTimeOffset.UtcNow.AddMinutes(5);
            copy.Comments[0].Replies.Add(new Message { Author = "Reviewer", Text = "Second look", CreatedAt = later, ModifiedAt = later });
            copy.Comments[0].ModifiedAt = later;
            copy.Comments.Add(new Comment { BlockId = copy.Blocks[1].Id, Author = "Reviewer", Text = "New point", CreatedAt = later, ModifiedAt = later });
            copy.Blocks[0].Runs = new() { new TextRun { Text = "Reviewer's text change" } };
            var copyFile = Path.Combine(dir, "returned" + AppInfo.FileExtension);
            DocFile.Save(copy, copyFile);
            var merged = MergeFile(copyFile);
            Check(merged.Comments == 1 && merged.Replies == 1 && _doc.Comments.Count == 2 && _doc.Comments[0].Replies.Count == 2, "merge adds new comments and replies");
            Check(_doc.Blocks[0].Text == "First", "merge leaves row text alone");
            Check(!MergeFile(copyFile).Any, "merging the same copy again adds nothing");

            // import a 2.x saved page
            var old = DocFile.Parse(Fixture2x);
            var a = old.Blocks[0];
            Check(a.Text == "Bold and red" && a.Runs[0].Bold == true && a.Runs.Any(r => r.Text == "red" && r.Color == "#FF0000"), "2.x HTML row becomes runs");
            Check(RowFmt.Of(a).Size == 18 && old.ShowNumbers, "2.x row style and settings");
            Check(old.Blocks[1].Text == "two\nlines & serif" && old.Blocks[1].Runs.Any(r => r.Font == "Georgia"), "2.x line breaks and font tags");
            Check(old.Comments[0].ModifiedAt.Day == 2 && old.Comments[0].Replies[0].ModifiedAt.Day == 3, "2.x comment dates");
            var oldFile = Path.Combine(dir, "old.html");
            File.WriteAllText(oldFile, Fixture2x);
            Check(OpenPath(oldFile) && _path == null && _doc.Title == "Old outline", "open a 2.x file as a new document");

            // paste
            var plain = Html.FromPlain("Top\n\tChild\n\t\tGrand\n  - Spaced\n1. Numbered");
            Check(string.Join(",", plain.Select(x => x.Level)) == "0,1,2,1,0" && plain[3].Type == "bullet" && plain[4].Type == "number", "plain-text paste levels");
            var web = Html.ToItems("<ul><li>A<ul><li>B</li></ul></li><li><p>C</p></li></ul>");
            Check(string.Join(",", web.Select(x => $"{x.Text}{x.Level}")) == "A0,B1,C0", "web list paste");
            var word = Html.ToItems(FixtureWord);
            Check(word.Count == 2 && word[0].Text == "One" && word[0].Type == "bullet" && word[0].Runs[0].Bold == true &&
                  word[1].Level == 1 && word[1].Type == "number" && word[1].Text == "Two", "Word list paste");
            LoadDocument(Doc.New(), null, null);
            var pv = _views[_doc.Blocks[0].Id];
            pv.Box.Document.Blocks.Clear();
            pv.Box.Document.Blocks.Add(new Paragraph(new Run("Keep me")));
            pv.Box.CaretPosition = pv.Box.Document.ContentEnd;
            PasteRows(pv, Html.FromPlain("x\n\ty"), false);
            Check(_doc.Blocks.Select(b => b.Text).SequenceEqual(new[] { "Keep me", "x", "y" }) && _doc.Blocks[2].Level == 1, "pasting rows keeps the row's text");

            // smarter paste: PDF text, Markdown, letters and roman numerals, Word indents and headings
            string Shape(List<Html.Item> items) => string.Join(",", items.Select(x => $"{x.Text}/{x.Level}/{x.Type}"));
            var pdf = Html.FromPlain("Shopping list for the week ahead, including everything for the trip and the party\r\n" +
                                     "• Fruit and vegetables from the market on Saturday morning before the\r\nrush starts\r\n" +
                                     "o Apples\r\no Pears\r\n Bread\r\n•\r\nMilk");
            Check(Shape(pdf) == "Shopping list for the week ahead, including everything for the trip and the party/0/none," +
                  "Fruit and vegetables from the market on Saturday morning before the rush starts/1/bullet,Apples/2/bullet,Pears/2/bullet," +
                  "Bread/1/bullet,Milk/1/bullet", "PDF paste: wrapped lines joined, levels from the bullets");
            // text as Brave's PDF viewer copies a Word PDF: no indents, Word's bullets, wrapped lines, then a numbered list
            var pdf2 = Html.FromPlain("Trip planning notes for the long weekend away, covering what to book, what to pack and\r\n" +
                                      "what still needs a decision before Friday evening.\r\n• Book the train\r\n" +
                                      "o Window seat if the early train still has some left, otherwise any seat in the\r\nquiet coach near the front\r\n" +
                                      "▪ Check the return time\r\n• Pack bags\r\no Charger\r\n1) First stop\r\n2) Second stop\r\na) Lunch near the harbour");
            Check(string.Join(",", pdf2.Select(x => x.Level)) == "0,1,2,3,1,2,1,1,2" && pdf2.Count == 9 &&
                  pdf2[2].Text.EndsWith("quiet coach near the front") && pdf2[6].Type == "number", "PDF paste (Brave's PDF viewer)");
            var md = Html.FromPlain("# Trip\n- **Book** train\n  - seat *window*\n## Day two\nWalk");
            Check(Shape(md) == "Trip/0/none,Book train/1/bullet,seat window/2/bullet,Day two/1/none,Walk/2/none" &&
                  md[0].Runs[0].Bold == true && md[1].Runs[0].Bold == true && md[1].Runs[1].Bold == null && md[2].Runs[1].Italic == true,
                  "Markdown paste");
            Check(Shape(Html.FromPlain("1. One\na. Sub\ni. Deeper\nb. Sub two\n2. Two\n(3) Three")) ==
                  "One/0/number,Sub/1/number,Deeper/2/number,Sub two/1/number,Two/0/number,Three/0/number", "letters and roman numerals");
            Check(Shape(Html.FromPlain("1 Intro\n1.1 Scope\n1.1.1 Detail\n2 Next\nNeeds 1.5 cups")) ==
                  "1 Intro/0/none,Scope/1/number,Detail/2/number,2 Next/0/none,Needs 1.5 cups/0/none", "outline numbers");
            Check(Shape(Html.FromPlain("Milk\nEggs\nBread")) == "Milk/0/none,Eggs/0/none,Bread/0/none", "short lines stay rows");
            var indent = Html.ToItems("<p style='mso-list:l0 level1 lfo1'>A</p><p style='margin-left:.75in;mso-list:l0 level1 lfo1'>B</p>" +
                                      "<p style='margin-left:1.0in;mso-list:l0 level1 lfo1'>C</p><p style='mso-list:l0 level2 lfo1'>D</p>");
            Check(string.Join(",", indent.Select(x => x.Level)) == "0,1,2,1", "Word Increase Indent keeps levels");
            var heads = Html.ToItems("<h1>Title</h1><p>Intro</p><h2>Part</h2><ul><li>Point</li></ul>");
            Check(Shape(heads) == "Title/0/none,Intro/1/none,Part/1/none,Point/2/bullet" && heads[0].Runs[0].Bold == true, "headings become levels");

            // rows on the clipboard: HTML lists, tabbed text and Scriber's own rows
            LoadDocument(Doc.New(), null, null);
            _doc.Blocks[0].Runs = new() { new TextRun { Text = "Plan", Bold = true } };
            _doc.Blocks[0].Type = "bullet";
            _doc.Blocks.Add(new Block { Level = 1, Type = "number", Runs = { new TextRun { Text = "Step & one ✓" } } });
            _doc.Blocks.Add(new Block { Level = 1, Type = "number", Runs = { new TextRun { Text = "Step two", Color = "#FF0000" } } });
            _doc.Blocks.Add(new Block { Runs = { new TextRun { Text = "Note" } } });
            _doc.Comments.Add(new Comment { BlockId = _doc.Blocks[1].Id, Text = "on step one" });
            RenderRows();
            SelectRange(0, 3);
            _active = _doc.Blocks[0].Id;
            CopyRows(false);
            var clipHtml = (string)_smokeClip!.GetData(DataFormats.Html);
            var back = Html.ToItems(Html.Fragment(clipHtml));
            Check(Shape(back) == "Plan/0/bullet,Step & one ✓/1/number,Step two/1/number,Note/0/none" && back[0].Runs[0].Bold == true &&
                  back[2].Runs[0].Color == "#FF0000", "copied rows read back from HTML");
            int startFrag = int.Parse(System.Text.RegularExpressions.Regex.Match(clipHtml, @"StartFragment:(\d+)").Groups[1].Value);
            Check(System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(clipHtml)[startFrag..]).StartsWith("<p>") ||
                  System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(clipHtml)[startFrag..]).StartsWith("<ul>"), "HTML clipboard offsets");
            var clipText = (string)_smokeClip.GetData(DataFormats.UnicodeText);
            Check(clipText == "• Plan\r\n\t1. Step & one ✓\r\n\t2. Step two\r\nNote", "copied rows as text");
            Check(Shape(Html.FromPlain(clipText)) == "Plan/0/bullet,Step & one ✓/1/number,Step two/1/number,Note/0/none", "copied text reads back");
            var last = _views[_doc.Blocks[3].Id];
            FocusRow(_doc.Blocks[3].Id);
            last.Box.CaretPosition = last.Box.Document.ContentEnd;
            PasteInto(last, _smokeClip, false);
            Check(_doc.Blocks.Count == 8 && _doc.Blocks.Select(b => b.Id).Distinct().Count() == 8 && _doc.Blocks[4].Text == "Plan" &&
                  _doc.Blocks[5].Level == 1 && _doc.Comments.Count == 1, "Scriber rows paste as copies (comments not doubled)");
            SelectRange(0, 0);
            _active = _doc.Blocks[0].Id;
            CopyRows(true);
            Check(_doc.Blocks.Count == 5 && _doc.Comments.Count == 0, "cut takes the row with what's under it");
            var end = _views[_doc.Blocks[^1].Id];
            FocusRow(_doc.Blocks[^1].Id);
            end.Box.CaretPosition = end.Box.Document.ContentEnd;
            PasteInto(end, _smokeClip, false);
            Check(_doc.Blocks.Count == 8 && _doc.Comments.Count == 1 && _doc.Blocks.Any(b => b.Id == _doc.Comments[0].BlockId && b.Text.StartsWith("Step & one")),
                  "cut and paste moves comments along");
            Undo();
            Check(_doc.Blocks.Count == 5, "undo a paste");
            SelectRange(1, 2);
            _active = _doc.Blocks[1].Id;
            var over = new DataObject(DataFormats.UnicodeText, "x\n\ty");
            PasteOver(over, false);
            // the selected "Plan" row goes with what's under it (like Delete); x and y take its place
            Check(_doc.Blocks.Count == 4 && _doc.Blocks[1].Text == "x" && _doc.Blocks[2].Text == "y" && _doc.Blocks[2].Level == _doc.Blocks[1].Level + 1,
                  "paste over selected rows");
            Undo();
            Check(_doc.Blocks.Count == 5 && _doc.Blocks[1].Text != "x", "paste over undoes in one step");
            SelectRange(0, 0);
            _active = _doc.Blocks[0].Id;
            int before = _doc.Blocks.Count;
            DuplicateRows();
            Check(_doc.Blocks.Count == before + 1 && _doc.Blocks[1].Text == _doc.Blocks[0].Text && _doc.Blocks[1].Id != _doc.Blocks[0].Id, "Ctrl+D duplicates");
            FocusRow(_doc.Blocks[0].Id);
            ExtendRows(1);
            ExtendRows(1);
            Check(_selected.Count == 3, "Shift+Down selects rows");
            ExtendRows(-1);
            Check(_selected.Count == 2, "Shift+Up shrinks the selection");
            TextOnly(_doc.Blocks.Select(b => b.Id).ToList());
            Check(_doc.Blocks.All(b => b.RowStyle == null && b.Runs.All(r => r.SameFormat(new TextRun()))), "text only drops formatting");

            // export, print layout, find
            LoadDocument(DocFile.Load(file), file, null);
            var txt = Path.Combine(dir, "export.txt");
            File.WriteAllText(txt, ExportText());
            var exported = File.ReadAllText(txt);
            Check(exported.Contains("First") && exported.Contains("    •  row") && exported.Contains("Looks very good"), "text export");
            Check(PrintDocument().Blocks.Count >= 4, "print layout");
            FindBox.Text = "ROW";
            Check(_matches.Count == 1 && _views[_doc.Blocks[1].Id].Box.Selection.Text == "row", "find");

            // lay the window out and draw it to smoke.png (a look at the build without putting it on screen)
            _doc.ShowNumbers = true;
            OpenScope(_doc.Blocks[0].Id, "all");
            _expandedThreads.Add(_doc.Comments[0].Id);
            RenderRows();
            RenderReview();
            var root = (System.Windows.FrameworkElement)Content;
            root.Measure(new System.Windows.Size(1300, 760));
            root.Arrange(new System.Windows.Rect(0, 0, 1300, 760));
            root.UpdateLayout();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(1300, 760, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Media.DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(Br(AppInfo.Background), null, new System.Windows.Rect(0, 0, 1300, 760));
            bmp.Render(bg);
            bmp.Render(root);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(PortablePaths.DataDir, "smoke.png"))) png.Save(fs);
            Check(_views[_doc.Blocks[0].Id].Root.ActualHeight >= 40, "rows lay out");

            File.WriteAllText(report, $"PASS Scriber {AppInfo.Version} self-check\n" + string.Join("\n", log) + "\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, $"FAIL Scriber {AppInfo.Version} self-check\n" + string.Join("\n", log) + "\n" + ex + "\n");
        }
    }
}
