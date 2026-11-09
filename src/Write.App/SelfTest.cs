using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Write.Core;
using Write.Desktop.Editing;

namespace Write.Desktop;

/// <summary>
/// Runs the real window through an editing session without user input:
///   Write.exe --selftest &lt;input.docx&gt; &lt;output folder&gt;
/// Used by the build to prove that the application starts, edits and saves on Windows.
/// Writes selftest.log, the saved documents and a screenshot to the output folder;
/// the exit code is 0 only when every step succeeded.
/// </summary>
internal static class SelfTest
{
    public static void Run(Application app, string[] args)
    {
        var log = new StringBuilder();
        string outDir = args.Length > 2 ? args[2] : Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        int exit = 1;

        void Finish()
        {
            File.WriteAllText(Path.Combine(outDir, "selftest.log"), log.ToString());
            app.Shutdown(exit);
        }

        app.DispatcherUnhandledException += (_, e) =>
        {
            log.AppendLine("UNHANDLED: " + e.Exception);
            e.Handled = true;
            exit = 1;
            Finish();
        };

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30, Width = 1240, Height = 880,
            WindowState = WindowState.Normal, Topmost = true,
        };
        window.Show();
        window.Activate();

        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                Steps(window, args.Length > 1 ? args[1] : null, outDir, log);
                exit = 0;
                log.AppendLine("RESULT: OK");
            }
            catch (Exception ex)
            {
                log.AppendLine("FAILED: " + ex);
            }
            Finish();
        }));
    }

    private static void Idle(Window window) =>
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private static void Check(bool condition, string what, StringBuilder log)
    {
        log.AppendLine((condition ? "ok   " : "FAIL ") + what);
        if (!condition) throw new InvalidOperationException("Check failed: " + what);
    }

    private static string? StyleAtCaret(MainWindow window) =>
        (window.EditorBox.CaretPosition.Paragraph?.Tag as ParaTag)?.StyleId;

    private static void Type(MainWindow window, string text)
    {
        var editor = window.EditorBox;
        editor.CaretPosition.InsertTextInRun(text);
        editor.CaretPosition = editor.CaretPosition.Paragraph?.ContentEnd ?? editor.Document.ContentEnd;
    }

    /// <summary>Selects the first occurrence of a text, the way Find does.</summary>
    private static void Select(MainWindow window, string text)
    {
        window.EditorBox.CaretPosition = window.EditorBox.Document.ContentStart;
        window.ShowFindBar(text);
        if (!window.FindStep(forward: true)) throw new InvalidOperationException("Text not found: " + text);
        window.HideFindBar();
    }

    private static byte[] SamplePicture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new LinearGradientBrush(Color.FromRgb(15, 118, 110), Color.FromRgb(94, 234, 212), 0);
            dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, 360, 120), 14, 14);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new Point(300, 40), 46, 46);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), null, new Point(70, 95), 60, 60);
        }
        var bitmap = new RenderTargetBitmap(360, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void Steps(MainWindow window, string? input, string outDir, StringBuilder log)
    {
        var editor = window.EditorBox;
        var styles = window.Document.Styles;

        // ---- 1. Write a new document with the application's own commands ----------------------
        Type(window, "Projectplan Write");
        window.ApplyStyle("Title");
        window.PressEnter();
        Check(StyleAtCaret(window) == null, "the paragraph after a title goes back to the default style", log);
        Type(window, "Een tekstverwerker voor Word-documenten");
        window.ApplyStyle("Subtitle");
        window.PressEnter();
        Type(window, "Inleiding");
        window.ApplyStyle("heading 1");
        window.PressEnter();
        Type(window, "Write opent en bewaart Word-documenten. Opmaak zoals vet, cursief en kleur blijft behouden, net als x2 en H2O. Meer informatie op ");
        window.InsertLink(null, "de website", "https://example.com/write");
        Type(window, ".");
        Select(window, "vet,");
        editor.Selection.Select(editor.Selection.Start, editor.Selection.End.GetPositionAtOffset(-1));
        EditingCommands.ToggleBold.Execute(null, editor);
        Select(window, "cursief");
        EditingCommands.ToggleItalic.Execute(null, editor);
        Select(window, "kleur");
        editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, FlowFormat.Brush("C00000"));
        Select(window, "behouden");
        editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, FlowFormat.Brush("FFFF00"));
        Select(window, "x2");
        editor.Selection.Select(editor.Selection.Start.GetPositionAtOffset(1), editor.Selection.End);
        window.ToggleScriptForTest(VertAlign.Superscript);
        Select(window, "H2O");
        editor.Selection.Select(editor.Selection.Start.GetPositionAtOffset(1), editor.Selection.End.GetPositionAtOffset(-1));
        window.ToggleScriptForTest(VertAlign.Subscript);

        editor.CaretPosition = editor.Document.ContentEnd;
        window.PressEnter();
        Type(window, "Kenmerken");
        window.ApplyStyle("heading 2");
        window.PressEnter();
        Type(window, "Stijlen, lettertypen en alinea-opmaak");
        EditingCommands.ToggleBullets.Execute(null, editor);
        window.PressEnter();
        Type(window, "Lijsten en tabellen");
        window.PressEnter();
        Type(window, "Afbeeldingen, koppelingen en pagina-einden");
        window.PressEnter();
        EditingCommands.ToggleBullets.Execute(null, editor);
        Type(window, "Een overzicht:");
        Idle(window);

        window.InsertTable(3, 3);
        var inserted = editor.Document.Blocks.OfType<Table>().First();
        string[,] cells = { { "Onderdeel", "Status", "Opmerking" }, { "Openen", "Gereed", "docx, rtf en txt" }, { "Opslaan", "Gereed", "Behoudt wat Word schreef" } };
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        {
            var cell = inserted.RowGroups[0].Rows[r].Cells[c];
            editor.CaretPosition = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            editor.CaretPosition.InsertTextInRun(cells[r, c]);
            if (r == 0)
            {
                editor.Selection.Select(cell.ContentStart, cell.ContentEnd);
                EditingCommands.ToggleBold.Execute(null, editor);
            }
        }
        editor.CaretPosition = editor.Document.ContentEnd;
        Check(window.InsertImage(SamplePicture(), "png"), "insert picture", log);
        editor.CaretPosition = editor.Document.ContentEnd;
        window.InsertPageBreak();
        Type(window, "Bijlage");
        window.ApplyStyle("heading 1");
        Idle(window);

        double padding = editor.Document.PagePadding.Left;
        Check(Math.Abs(padding - FlowFormat.Dip(window.Document.Page.MarginLeft)) < 0.5, $"page margins are in effect ({padding:0.#})", log);

        var newPath = Path.Combine(outDir, "selftest-new.docx");
        Check(window.SaveTo(newPath), "save new document", log);
        var model = DocxReader.Read(File.ReadAllBytes(newPath));
        var paras = model.Blocks.OfType<Para>().ToList();
        log.AppendLine("     paragraphs: " + string.Join(" | ", paras.Select(p => p.Text)));
        Para Find(string text) => paras.First(p => p.Text.StartsWith(text, StringComparison.Ordinal));
        string? Id(string name) => model.Styles.FindByName(name)?.Id;

        Check(Find("Projectplan").Props.StyleId == Id("Title"), "title style saved", log);
        Check(Find("Een tekstverwerker").Props.StyleId == Id("Subtitle"), "subtitle style saved", log);
        Check(Find("Inleiding").Props.StyleId == Id("heading 1"), "heading 1 saved", log);
        Check(Find("Kenmerken").Props.StyleId == Id("heading 2"), "heading 2 saved", log);
        var body = Find("Write opent");
        Check(body.Props.StyleId == null && body.RawPPr == null, "body paragraph has the default style and no direct formatting", log);
        var runs = body.Inlines.OfType<TextRun>().ToList();
        log.AppendLine("     runs: " + string.Join(" | ", runs.Select(r => r.Text + (r.RawRPr != null ? "*" : ""))));
        var normal = model.Styles.ResolveRun(null);
        Check(runs.First().Props == normal && runs.First().RawRPr == null, "plain text has no direct formatting", log);
        Check(runs.Single(r => r.Text == "vet").Props == normal with { Bold = true }, "bold saved", log);
        Check(runs.Single(r => r.Text == "cursief").Props == normal with { Italic = true }, "italic saved", log);
        Check(runs.Single(r => r.Text == "kleur").Props == normal with { Color = "C00000" }, "colour saved", log);
        Check(runs.Single(r => r.Text == "behouden").Props == normal with { Highlight = "FFFF00" }, "highlight saved", log);
        Check(runs.Count(r => r.Text == "2" && r.Props == normal with { Vert = VertAlign.Superscript }) == 1, "superscript saved at the normal size", log);
        Check(runs.Count(r => r.Text == "2" && r.Props == normal with { Vert = VertAlign.Subscript }) == 1, "subscript saved at the normal size", log);
        var link = runs.Single(r => r.Link != null);
        Check(link.Text == "de website" && link.Link == "https://example.com/write" && link.Props.StyleId == Id2(model, "Hyperlink"), "hyperlink saved with the Hyperlink style", log);
        Check(paras.Count(p => p.List != null && p.List.Format.IsBullet()) == 3, "three bullet items saved", log);
        var table = model.Blocks.OfType<TableBlock>().FirstOrDefault();
        Check(table != null && table.Rows.Count == 3 && table.Rows.All(r => r.Cells.Count == 3), "table 3×3 saved", log);
        Check(table!.Rows[1].Cells[2].Blocks.OfType<Para>().Single().Text == "docx, rtf en txt", "table text saved", log);
        Check(table.Grid.Sum() <= model.Page.ContentWidth + 20, "table fits the text width", log);
        Check(paras.SelectMany(p => p.Inlines).OfType<ImageInline>().Count() == 1, "picture saved", log);
        Check(Find("Bijlage").BreakBefore && Find("Bijlage").Props.StyleId == Id("heading 1"), "page break and heading saved", log);

        editor.CaretPosition = editor.Document.ContentStart;
        window.ScrollToTop();
        Screenshot(window, Path.Combine(outDir, "1-nieuw-document.png"), log);
        window.OpenStyleList(true);
        Screenshot(window, Path.Combine(outDir, "2-stijlen.png"), log);
        window.OpenStyleList(false);

        // Table editing on this document.
        editor.CaretPosition = inserted.RowGroups[0].Rows[1].Cells[1].ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        window.InsertRow(below: true);
        window.InsertColumn(right: true);
        Idle(window);
        Check(inserted.RowGroups[0].Rows.Count == 4 && inserted.RowGroups[0].Rows.All(r => r.Cells.Count == 4), "row and column inserted", log);
        var grown = DocxReader.Read(DocxWriter.Write(window.CurrentModel())).Blocks.OfType<TableBlock>().First();
        Check(grown.Rows.Count == 4 && grown.Grid.Count == 4 && grown.Grid.Sum() <= model.Page.ContentWidth + 20, "grown table saved and still fits", log);

        // Undo and redo keep working after all of this.
        editor.CaretPosition = editor.Document.ContentEnd;
        Type(window, " X");
        Check(editor.CanUndo, "undo is available", log);
        editor.Undo();

        // What a real Enter key press does (informational: the key goes through the input system).
        try
        {
            var heading = FlowWalker.Paragraphs(editor.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text == "Inleiding");
            editor.CaretPosition = heading.ContentEnd;
            editor.Focus();
            var source = PresentationSource.FromVisual(editor);
            if (source != null)
            {
                foreach (var routed in new[] { System.Windows.Input.Keyboard.PreviewKeyDownEvent, System.Windows.Input.Keyboard.KeyDownEvent })
                    editor.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Enter) { RoutedEvent = routed });
                Idle(window);
                var now = editor.CaretPosition.Paragraph;
                log.AppendLine($"     Enter key after a heading: new paragraph={!ReferenceEquals(now, heading)}, style={(now?.Tag as ParaTag)?.StyleId ?? "(default)"}");
                if (!ReferenceEquals(now, heading)) editor.Undo();
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("     Enter key probe skipped: " + ex.Message);
        }

        // ---- 2. An existing document ------------------------------------------------------------
        if (input != null && File.Exists(input))
        {
            Check(window.OpenPath(input), "open " + Path.GetFileName(input), log);
            Idle(window);
            editor = window.EditorBox;
            var original = File.ReadAllBytes(input);
            var direct = Describe(DocxReader.Read(DocxWriter.Write(DocxReader.Read(original))));
            var untouchedPath = Path.Combine(outDir, "selftest-untouched.docx");
            Check(window.SaveTo(untouchedPath), "save untouched document", log);
            var viaEditor = Describe(DocxReader.Read(File.ReadAllBytes(untouchedPath)));
            if (direct != viaEditor)
            {
                File.WriteAllText(Path.Combine(outDir, "expected.txt"), direct);
                File.WriteAllText(Path.Combine(outDir, "actual.txt"), viaEditor);
                log.AppendLine(FirstDifference(direct, viaEditor));
            }
            Check(direct == viaEditor, "untouched document is unchanged after passing through the editor", log);
            window.ScrollToTop();
            Screenshot(window, Path.Combine(outDir, "3-bestaand-document.png"), log);
            window.ShowFindBar("pagina");
            window.FindStep(forward: true);
            Screenshot(window, Path.Combine(outDir, "4-zoeken-en-vervangen.png"), log);
            window.HideFindBar();

            // Split a heading in the middle: both halves stay headings, nothing else changes.
            var source = DocxReader.Read(original);
            var headingStyle = source.Blocks.OfType<Para>().First(p => p.Text == "Hoofdstuk één").Props.StyleId;
            var heading = FlowWalker.Paragraphs(editor.Document.Blocks).First();
            editor.CaretPosition = heading.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetPositionAtOffset(9)!;
            window.PressEnter();
            // Enter at the end of a heading starts a normal paragraph.
            var second = FlowWalker.Paragraphs(editor.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text == "Lijsten");
            editor.CaretPosition = second.ContentEnd;
            window.PressEnter();
            Type(window, "Gewone alinea na een kop");
            Select(window, "Gecentreerd");
            window.ApplyStyle("heading 3");
            int replaced = window.ReplaceAll("punt", "PUNT");
            Check(replaced == 3, $"replace all ({replaced})", log);
            Idle(window);

            var editedPath = Path.Combine(outDir, "selftest-edited.docx");
            Check(window.SaveTo(editedPath), "save edited document", log);
            var edited = DocxReader.Read(File.ReadAllBytes(editedPath));
            var ep = edited.Blocks.OfType<Para>().ToList();
            log.AppendLine("     paragraphs: " + string.Join(" | ", ep.Take(8).Select(p => p.Text)));
            Check(ep[0].Text == "Hoofdstuk" && ep[1].Text == " één" && ep[0].Props.StyleId == headingStyle && ep[1].Props.StyleId == headingStyle,
                "both halves of a split heading keep the heading style", log);
            var after = ep.First(p => p.Text == "Gewone alinea na een kop");
            Check(after.Props.StyleId == null && after.Inlines.OfType<TextRun>().All(r => r.Props == edited.Styles.ResolveRun(null) && r.RawRPr == null),
                "text typed after a heading is plain body text", log);
            Check(ep.First(p => p.Text.StartsWith("Gecentreerd")).Props.StyleId == edited.Styles.FindByName("heading 3")?.Id, "style applied to an existing paragraph", log);
            Check(ep.Count(p => p.Text.Contains("PUNT")) == 3 && ep.Where(p => p.Text.Contains("PUNT")).All(p => p.List != null), "replaced text is still in its lists", log);
            Check(edited.Blocks.OfType<TableBlock>().Count() == 1 && ep.SelectMany(p => p.Inlines).OfType<ImageInline>().Count() == 1 &&
                  ep.SelectMany(p => p.Inlines).OfType<RawInline>().Count(r => r.Kind == RawKind.Field) == 1, "table, picture and field survive editing", log);
            var untouchedParas = DocxReader.Read(File.ReadAllBytes(untouchedPath)).Blocks.OfType<Para>().ToList();
            var keep = untouchedParas.First(p => p.Text.StartsWith("Ingesprongen"));
            var kept = ep.First(p => p.Text.StartsWith("Ingesprongen"));
            Check(kept.RawPPr?.ToString() == keep.RawPPr?.ToString(), "an untouched paragraph is written exactly as before", log);
        }

        window.DiscardChanges();
    }

    private static string? Id2(DocModel model, string name) => model.Styles.FindByName(name, "character")?.Id;

    private static string FirstDifference(string a, string b)
    {
        var x = a.Split('\n');
        var y = b.Split('\n');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var l = i < x.Length ? x[i] : "<end>";
            var r = i < y.Length ? y[i] : "<end>";
            if (l != r) return $"     first difference at line {i + 1}:\n       expected: {l}\n       actual:   {r}";
        }
        return "     no difference";
    }

    /// <summary>A text rendering of everything the document model holds, for comparing two documents.</summary>
    internal static string Describe(DocModel doc)
    {
        var sb = new StringBuilder();
        void Blocks(IEnumerable<Write.Core.Block> blocks, string indent)
        {
            foreach (var block in blocks)
            {
                if (block is Para p)
                {
                    sb.Append(indent).Append("P ").Append(p.Props);
                    if (p.List != null) sb.Append($" list[{p.List.Level},{p.List.Format},{p.List.Ordinal}]");
                    if (p.BreakBefore) sb.Append(" break");
                    sb.Append('\n');
                    foreach (var inline in p.Inlines)
                    {
                        sb.Append(indent).Append("  ");
                        switch (inline)
                        {
                            case TextRun r: sb.Append("T '").Append(r.Text).Append("' ").Append(r.Props); break;
                            case LineBreakInline: sb.Append("BR"); break;
                            case ImageInline img: sb.Append($"IMG {img.Extension} {img.Cx}x{img.Cy} {img.Data.Length}b"); break;
                            case RawInline raw: sb.Append($"RAW {raw.Kind} '{raw.Text}'"); break;
                        }
                        if (inline.Link != null) sb.Append(" -> ").Append(inline.Link);
                        sb.Append('\n');
                    }
                }
                else if (block is TableBlock t)
                {
                    sb.Append(indent).Append("TABLE ").Append(string.Join(",", t.Grid)).Append('\n');
                    foreach (var row in t.Rows)
                    {
                        sb.Append(indent).Append(" ROW\n");
                        foreach (var cell in row.Cells)
                        {
                            sb.Append(indent).Append($"  CELL {cell.ColSpan}x{cell.RowSpan} {cell.Shade}\n");
                            Blocks(cell.Blocks, indent + "    ");
                        }
                    }
                }
            }
        }
        Blocks(doc.Blocks, "");
        return sb.ToString();
    }

    /// <summary>Saves a picture of the window as it appears on screen (render-only when the screen cannot be read).</summary>
    private static void Screenshot(MainWindow window, string path, StringBuilder log)
    {
        // Give layout, rendering and the desktop compositor time to show the current state.
        var until = DateTime.UtcNow.AddMilliseconds(700);
        while (DateTime.UtcNow < until)
        {
            Idle(window);
            System.Threading.Thread.Sleep(40);
        }
        try
        {
            SaveScreen(window, path);
            log.AppendLine("     screenshot " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            log.AppendLine("     screen capture unavailable (" + ex.Message + "), rendering instead");
            SaveRendering(window, path);
        }
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveRendering(Window window, string path)
    {
        if (window.Content is not FrameworkElement root) return;
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        bitmap.Render(visual);
        SavePng(bitmap, path);
    }

    private static void SaveScreen(Window window, string path)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(handle, 9 /* extended frame bounds */, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
            if (!GetWindowRect(handle, out r)) throw new InvalidOperationException("no window rectangle");
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("empty window rectangle");

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000))
                throw new InvalidOperationException("BitBlt failed");
            SelectObject(memory, previous);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            SavePng(source, path);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
