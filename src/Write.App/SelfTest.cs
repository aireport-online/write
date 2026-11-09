using System.IO;
using System.Text;
using System.Windows;
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

    private static void Steps(MainWindow window, string? input, string outDir, StringBuilder log)
    {
        var editor = window.EditorBox;

        // 1. A new document: type, format, add a list, a table and a page break, then save and read back.
        editor.CaretPosition.InsertTextInRun("Zelftest van Write");
        editor.SelectAll();
        EditingCommands.ToggleBold.Execute(null, editor);
        editor.CaretPosition = editor.Document.ContentEnd;
        window.PressEnter();
        editor.CaretPosition.InsertTextInRun("Eerste punt");
        EditingCommands.ToggleBullets.Execute(null, editor);
        window.PressEnter();
        editor.CaretPosition.InsertTextInRun("Tweede punt");
        window.PressEnter();
        EditingCommands.ToggleBullets.Execute(null, editor);
        editor.CaretPosition.InsertTextInRun("Na de lijst");
        Idle(window);
        window.InsertTable(2, 3);
        editor.CaretPosition.InsertTextInRun("cel");
        window.InsertRow(below: true);
        window.InsertColumn(right: true);
        editor.CaretPosition = editor.Document.ContentEnd;
        window.InsertPageBreak();
        editor.CaretPosition.InsertTextInRun("Nieuwe pagina");
        Idle(window);

        var newPath = Path.Combine(outDir, "selftest-new.docx");
        Check(window.SaveTo(newPath), "save new document", log);
        var model = DocxReader.Read(File.ReadAllBytes(newPath));
        var paras = model.Blocks.OfType<Para>().ToList();
        log.AppendLine("     paragraphs: " + string.Join(" | ", paras.Select(p => p.Text)));
        Check(paras.Any(p => p.Text == "Zelftest van Write" && p.Inlines.OfType<TextRun>().All(r => r.Props.Bold)), "bold heading text saved", log);
        Check(paras.Count(p => p.List != null && p.List.Format.IsBullet()) == 2, "two bullet items saved", log);
        var table = model.Blocks.OfType<TableBlock>().FirstOrDefault();
        Check(table != null && table.Rows.Count == 3 && table.Rows.All(r => r.Cells.Count == 4), "table 3×4 saved", log);
        Check(paras.Any(p => p.Text == "Nieuwe pagina" && p.BreakBefore), "page break saved", log);

        window.EditorBox.CaretPosition = window.EditorBox.Document.ContentStart;
        Screenshot(window, Path.Combine(outDir, "1-nieuw-document.png"), log);
        SaveRendering(window, Path.Combine(outDir, "1-nieuw-document-render.png"));

        // 2. How paragraph splitting treats the bookkeeping tags (informational).
        var probe = editor.Document.Blocks.OfType<Paragraph>().First();
        log.AppendLine($"     first paragraph tag: {probe.Tag?.GetType().Name ?? "none"}");

        // 3. An existing document: open, save untouched, compare with a round trip that never saw the editor.
        if (input != null && File.Exists(input))
        {
            Check(window.OpenPath(input), "open " + Path.GetFileName(input), log);
            Idle(window);
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
            Screenshot(window, Path.Combine(outDir, "2-bestaand-document.png"), log);
            window.ShowFindBar("pagina");
            window.FindStep(forward: true);
            Screenshot(window, Path.Combine(outDir, "3-zoeken-en-vervangen.png"), log);
            window.HideFindBar();

            // 4. Edit it: apply a heading style, press Enter in the middle of a paragraph, replace text.
            var first = FlowWalker.Paragraphs(editor.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Length > 12);
            editor.CaretPosition = first.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetPositionAtOffset(5) ?? first.ContentEnd;
            window.PressEnter();
            Idle(window);
            int replaced = window.ReplaceAll("e", "E");
            log.AppendLine($"     replaced {replaced} occurrences");
            var editedPath = Path.Combine(outDir, "selftest-edited.docx");
            Check(window.SaveTo(editedPath), "save edited document", log);
            var edited = DocxReader.Read(File.ReadAllBytes(editedPath));
            Check(edited.Blocks.Count >= model.Blocks.Count / 2, "edited document can be read back", log);
            editor.CaretPosition = editor.Document.ContentStart;
            Idle(window);
        }

        window.DiscardChanges();
    }

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
