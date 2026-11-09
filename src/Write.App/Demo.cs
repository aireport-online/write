using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Write.Desktop.Editing;

namespace Write.Desktop;

/// <summary>
/// Plays a guided tour of the application for a screen recording:
///   Write.exe --demo &lt;output folder&gt; [document to open]
/// The tour is driven with real mouse and keyboard input, so what the recording shows is what a
/// user would do. Where input cannot be delivered, the same action is performed directly.
/// The window's screen rectangle is written to region.txt; the tour starts once go.txt appears.
/// </summary>
internal static class Demo
{
    public static void Run(Application app, string[] args)
    {
        string outDir = args[1];
        string? sample = args.Length > 2 && File.Exists(args[2]) ? args[2] : null;
        Directory.CreateDirectory(outDir);

        MainWindow.CascadeOffset = 0;
        Settings.Current.WindowWidth = 1240;
        Settings.Current.WindowHeight = 880;
        Settings.Current.Maximized = false;
        Settings.Current.Zoom = 1.0;

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30, Width = 1240, Height = 880,
            WindowState = WindowState.Normal, Topmost = true,
        };
        window.Show();
        window.Activate();

        var driver = new Driver(app, window, outDir, sample);
        var thread = new Thread(driver.Play) { IsBackground = true, Name = "Demo" };
        thread.Start();
    }

    private sealed class Driver
    {
        private readonly Application _app;
        private MainWindow _window;
        private readonly string _outDir;
        private readonly string? _sample;
        private readonly StringBuilder _log = new();
        private readonly Random _random = new(7);
        private TextBlock? _caption;
        private Border? _captionBox;
        private bool _direct; // input cannot be delivered: perform actions directly

        public Driver(Application app, MainWindow window, string outDir, string? sample)
        {
            _app = app;
            _window = window;
            _outDir = outDir;
            _sample = sample;
        }

        private void Log(string line)
        {
            lock (_log) _log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }

        private T Ui<T>(Func<T> f) => _window.Dispatcher.Invoke(f);
        private void Ui(Action a) => _window.Dispatcher.Invoke(a);
        private static void Wait(int ms) => Thread.Sleep(ms);

        public void Play()
        {
            int exit = 0;
            try
            {
                Wait(1200);
                WriteRegion();
                var go = Path.Combine(_outDir, "go.txt");
                for (int i = 0; i < 300 && !File.Exists(go); i++) Wait(100);
                Wait(600);
                Tour();
                Log("tour finished");
            }
            catch (Exception ex)
            {
                exit = 1;
                Log("FAILED: " + ex);
            }
            finally
            {
                try { File.WriteAllText(Path.Combine(_outDir, "demo.log"), _log.ToString()); } catch (IOException) { }
                _app.Dispatcher.Invoke(() =>
                {
                    foreach (var w in _app.Windows.OfType<MainWindow>().ToList()) w.DiscardChanges();
                    _app.Shutdown(exit);
                });
            }
        }

        private void WriteRegion()
        {
            var handle = Ui(() => new WindowInteropHelper(_window).Handle);
            if (DwmGetWindowAttribute(handle, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(handle, out r);
            int width = (r.Right - r.Left) & ~1, height = (r.Bottom - r.Top) & ~1;
            File.WriteAllText(Path.Combine(_outDir, "region.txt"), $"{r.Left} {r.Top} {width} {height}");
            Log($"window at {r.Left},{r.Top} size {width}x{height}");
        }

        // ---- the tour ------------------------------------------------------------------------

        private void Tour()
        {
            var editor = Ui(() => _window.EditorBox);

            Caption("Write: een tekstverwerker voor Word-documenten");
            MoveTo(Ui(() => _window.PointToScreen(new Point(620, 420))));
            Wait(2600);

            // Click in the page and find out whether typed input arrives.
            Click(() => TextPoint(editor.Document.ContentStart, 40, 0), null);
            SendText("N");
            Wait(350);
            _direct = Ui(() => new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim()) != "N";
            Log(_direct ? "input is not delivered: performing actions directly" : "input is delivered: driving with mouse and keyboard");
            if (_direct) Ui(() => { editor.Focus(); editor.CaretPosition.InsertTextInRun("N"); editor.CaretPosition = editor.Document.ContentEnd; });

            Caption("Typ een titel en kies een stijl");
            Type("otulen teamoverleg");
            Wait(500);
            PickStyle("Titel");
            Enter();
            Type("Maandag 9 november 2026");
            PickStyle("Ondertitel");
            Enter();
            Type("Aanwezig");
            PickStyle("Kop 1");
            Enter();

            Caption("Een lijst maak je met één klik");
            ClickElement(_window.BulletsButton, () => EditingCommands.ToggleBullets.Execute(null, editor), () => InList(editor));
            Type("Anna de Vries");
            Enter();
            Type("Bram Jansen");
            Enter();
            Type("Chris Bakker");
            Enter();
            ClickElement(_window.BulletsButton, () => EditingCommands.ToggleBullets.Execute(null, editor), () => !InList(editor));
            Wait(300);

            Type("Besluiten");
            PickStyle("Kop 1");
            Enter();
            Type("Het project start op 1 december. Het budget is goedgekeurd.");
            Wait(500);

            Caption("Selecteer tekst en geef die opmaak");
            SelectWord("goedgekeurd");
            ClickElement(_window.BoldButton, () => EditingCommands.ToggleBold.Execute(null, editor),
                () => editor.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600);
            ClickElement(_window.FontColorButton, () => editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, FlowFormat.Brush("C00000")), null);
            Wait(400);
            SelectWord("december");
            ClickElement(_window.HighlightButton, () => editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, FlowFormat.Brush("FFFF00")), null);
            Wait(500);
            Click(() => TextPoint(editor.Document.ContentEnd, 0, 0), () => editor.CaretPosition = editor.Document.ContentEnd);
            Key(VK_END, ctrl: true);
            if (_direct) Ui(() => editor.CaretPosition = editor.Document.ContentEnd);
            Enter();

            Caption("Een tabel invoegen");
            InsertTable(editor);
            string[,] cells = { { "Actie", "Wie", "Wanneer" }, { "Offerte opvragen", "Anna", "12 december" } };
            for (int r = 0; r < 2; r++)
            for (int c = 0; c < 3; c++)
            {
                int row = r, col = c;
                Click(() => CellPoint(editor, row, col), () => editor.CaretPosition = Cell(editor, row, col)!.ContentStart.GetInsertionPosition(LogicalDirection.Forward));
                Type(cells[r, c]);
                Wait(150);
            }
            Wait(600);

            Caption("Zoeken en vervangen met Ctrl+F");
            Key('F', ctrl: true);
            Wait(500);
            if (Ui(() => _window.FindBar.Visibility != Visibility.Visible)) Ui(() => { _window.ShowFindBar(""); _window.FindText.Focus(); });
            TypeInto(_window.FindText, "Anna");
            ClickElement(_window.ReplaceText, () => _window.ReplaceText.Focus(), null);
            TypeInto(_window.ReplaceText, "Annelies");
            var replaceAll = Ui(() => Find<Button>(_window.FindBar, b => b.Content as string == "Alles vervangen"));
            ClickElement(replaceAll!, () => _window.ReplaceAll("Anna", "Annelies"), () => !DocumentText(editor).Contains("Anna "));
            Wait(1800);
            Key(VK_ESCAPE);
            Wait(300);
            Ui(() => _window.HideFindBar());

            Caption("Opslaan als Word-document (.docx) met Ctrl+S");
            Save();
            Wait(1500);

            if (_sample != null)
            {
                Caption("Een bestaand Word-document openen met Ctrl+O");
                Open();
                Caption("Opmaak, lijsten, tabellen en afbeeldingen uit Word blijven behouden");
                MoveTo(Ui(() => _window.PointToScreen(new Point(980, 470))));
                Wait(1800);
                for (int i = 0; i < 9; i++)
                {
                    Scroll(-1);
                    Wait(420);
                }
                Wait(1500);
                for (int i = 0; i < 9; i++)
                {
                    Scroll(+1);
                    Wait(160);
                }
            }

            Caption("Write: native op Windows, en uw documenten blijven gewoon Word-documenten");
            Wait(3500);

            // Keep the document made in the tour next to the recording.
            var saved = Ui(() => _app.Windows.OfType<MainWindow>().Select(w => w.CurrentPath).FirstOrDefault(p => p != null && p.Contains("Notulen")));
            if (saved != null && File.Exists(saved))
            {
                var copy = Path.Combine(_outDir, Path.GetFileName(saved));
                if (!string.Equals(Path.GetFullPath(copy), Path.GetFullPath(saved), StringComparison.OrdinalIgnoreCase)) File.Copy(saved, copy, true);
            }
        }

        // ---- steps ---------------------------------------------------------------------------

        private static bool InList(RichTextBox editor) => editor.CaretPosition.Paragraph?.Parent is ListItem;

        private static string DocumentText(RichTextBox editor) =>
            new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text;

        private void PickStyle(string display)
        {
            ClickElement(_window.StyleBox, null, null);
            Wait(450);
            var entry = Ui(() =>
            {
                var e = _window.StyleListEntry(display);
                e?.BringIntoView();
                return e;
            });
            if (entry != null && !_direct)
            {
                Wait(250);
                Click(() => Center(entry), null);
                Wait(350);
            }
            if (Ui(() => _window.CurrentStyleDisplay) != display)
            {
                Log("style picked directly: " + display);
                Ui(() => _window.ApplyStyleByDisplay(display));
            }
            Ui(() => { _window.OpenStyleList(false); _window.EditorBox.Focus(); });
            Wait(450);
        }

        private void SelectWord(string word)
        {
            var editor = Ui(() => _window.EditorBox);
            var (start, end) = Ui(() =>
            {
                foreach (var para in FlowWalker.Paragraphs(editor.Document.Blocks))
                foreach (var run in FlowWalker.Runs(para.Inlines))
                {
                    int at = run.Text.IndexOf(word, StringComparison.Ordinal);
                    if (at >= 0)
                        return (run.ContentStart.GetPositionAtOffset(at)!, run.ContentStart.GetPositionAtOffset(at + word.Length)!);
                }
                throw new InvalidOperationException("Word not found: " + word);
            });
            DoubleClick(() =>
            {
                var a = start.GetCharacterRect(LogicalDirection.Forward);
                var b = end.GetCharacterRect(LogicalDirection.Backward);
                return editor.PointToScreen(new Point((a.X + b.X) / 2, a.Y + a.Height / 2));
            });
            Wait(300);
            if (Ui(() => editor.Selection.Text.Trim()) != word)
            {
                Log("word selected directly: " + word);
                Ui(() => editor.Selection.Select(start, end));
            }
            Wait(350);
        }

        private static TableCell? Cell(RichTextBox editor, int row, int col)
        {
            var table = editor.Document.Blocks.OfType<Table>().FirstOrDefault();
            if (table == null || table.RowGroups.Count == 0 || row >= table.RowGroups[0].Rows.Count) return null;
            var cells = table.RowGroups[0].Rows[row].Cells;
            return col < cells.Count ? cells[col] : null;
        }

        private static Point CellPoint(RichTextBox editor, int row, int col)
        {
            var cell = Cell(editor, row, col) ?? throw new InvalidOperationException("No table cell");
            var rect = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
            return editor.PointToScreen(new Point(rect.X + 30, rect.Y + rect.Height / 2));
        }

        private void InsertTable(RichTextBox editor)
        {
            var button = Ui(() => Find<ButtonBase>(_window, b => b.ToolTip as string == "Tabel invoegen"));
            var main = Ui(() => new WindowInteropHelper(_window).Handle);
            if (!_direct && button != null)
            {
                Click(() => Center(button), null);
                if (WaitForOtherWindow(main, 4000))
                {
                    Wait(700);
                    SendText("2");
                    Wait(350);
                    Key(VK_TAB);
                    Wait(250);
                    SendText("3");
                    Wait(600);
                    Key(VK_RETURN);
                    Wait(700);
                }
            }
            if (Ui(() => Cell(editor, 0, 0)) == null)
            {
                Log("table inserted directly");
                // A dialog that is still open belongs to the failed attempt.
                Ui(() => { foreach (var d in _app.Windows.OfType<FormDialog>().ToList()) d.Close(); });
                Ui(() => _window.InsertTable(2, 3));
            }
            Wait(500);
        }

        private void Save()
        {
            var main = Ui(() => new WindowInteropHelper(_window).Handle);
            if (!_direct)
            {
                Key('S', ctrl: true);
                if (WaitForOtherWindow(main, 6000))
                {
                    Wait(1500);
                    SendText("Notulen teamoverleg");
                    Wait(900);
                    Key(VK_RETURN);
                    for (int i = 0; i < 80 && Ui(() => _window.CurrentPath) == null; i++) Wait(100);
                }
            }
            if (Ui(() => _window.CurrentPath) == null)
            {
                Log("saved directly");
                Ui(() => _window.SaveTo(Path.Combine(_outDir, "Notulen teamoverleg.docx")));
            }
            Log("saved as " + Ui(() => _window.CurrentPath));
        }

        private void Open()
        {
            var folder = Path.GetDirectoryName(Ui(() => _window.CurrentPath)) ?? _outDir;
            var target = Path.Combine(folder, "Offerte.docx");
            File.Copy(_sample!, target, true);

            MainWindow? Opened() => _app.Windows.OfType<MainWindow>().FirstOrDefault(w => string.Equals(w.CurrentPath, target, StringComparison.OrdinalIgnoreCase));
            var main = Ui(() => new WindowInteropHelper(_window).Handle);
            if (!_direct)
            {
                Key('O', ctrl: true);
                if (WaitForOtherWindow(main, 6000))
                {
                    Wait(1500);
                    SendText("Offerte.docx");
                    Wait(900);
                    Key(VK_RETURN);
                    for (int i = 0; i < 80 && Ui(Opened) == null; i++) Wait(100);
                }
            }
            if (Ui(Opened) == null)
            {
                Log("opened directly");
                Ui(() => _window.OpenInBestWindow(target));
            }
            var opened = Ui(Opened) ?? throw new InvalidOperationException("The sample document did not open");
            _window = opened;
            _caption = null;
            _captionBox = null;
            Ui(() => { opened.Topmost = true; opened.Activate(); });
            Wait(900);
            Log("opened " + target);
        }

        // ---- caption -------------------------------------------------------------------------

        private void Caption(string text)
        {
            Log("caption: " + text);
            Ui(() =>
            {
                if (_caption == null)
                {
                    _caption = new TextBlock { Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") };
                    _captionBox = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(235, 24, 28, 32)),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(20, 10, 20, 11),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 22),
                        IsHitTestVisible = false,
                        Child = _caption,
                    };
                    Panel.SetZIndex(_captionBox, 1000);
                    _window.Workspace.Children.Add(_captionBox);
                }
                _caption.Text = text;
            });
            Wait(900);
        }

        // ---- input ---------------------------------------------------------------------------

        private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed && match(typed)) return typed;
                var found = Find(child, match);
                if (found != null) return found;
            }
            return null;
        }

        private static Point Center(FrameworkElement element) =>
            element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));

        /// <summary>Screen position of a place in the text, offset by a few pixels.</summary>
        private Point TextPoint(TextPointer pointer, double dx, double dy)
        {
            var editor = _window.EditorBox;
            var rect = pointer.GetInsertionPosition(LogicalDirection.Backward).GetCharacterRect(LogicalDirection.Forward);
            return editor.PointToScreen(new Point(rect.X + dx, rect.Y + rect.Height / 2 + dy));
        }

        private void MoveTo(Point target)
        {
            GetCursorPos(out POINT from);
            double distance = Math.Sqrt(Math.Pow(target.X - from.X, 2) + Math.Pow(target.Y - from.Y, 2));
            int steps = (int)Math.Clamp(distance / 22, 10, 34);
            for (int i = 1; i <= steps; i++)
            {
                double t = i / (double)steps;
                double eased = t * t * (3 - 2 * t);
                SetCursorPos((int)Math.Round(from.X + (target.X - from.X) * eased), (int)Math.Round(from.Y + (target.Y - from.Y) * eased));
                Wait(14);
            }
        }

        private void Mouse(uint flags, int data = 0)
        {
            var input = new INPUT { type = 0 };
            input.U.mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data) };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        /// <summary>Moves to a point and clicks; when input is not delivered, <paramref name="direct"/> does the work.</summary>
        private void Click(Func<Point> where, Action? direct)
        {
            MoveTo(Ui(where));
            Wait(160);
            if (!_direct)
            {
                Mouse(MOUSEEVENTF_LEFTDOWN);
                Wait(60);
                Mouse(MOUSEEVENTF_LEFTUP);
            }
            else if (direct != null) Ui(direct);
            Wait(260);
        }

        private void DoubleClick(Func<Point> where)
        {
            MoveTo(Ui(where));
            Wait(160);
            if (_direct) return;
            for (int i = 0; i < 2; i++)
            {
                Mouse(MOUSEEVENTF_LEFTDOWN);
                Wait(35);
                Mouse(MOUSEEVENTF_LEFTUP);
                Wait(70);
            }
        }

        /// <summary>Clicks a control; if the expected result does not follow, performs the action directly.</summary>
        private void ClickElement(FrameworkElement element, Action? direct, Func<bool>? worked)
        {
            Click(() => Center(element), direct);
            if (!_direct && direct != null && worked != null && !Ui(worked))
            {
                Log("performed directly: " + Ui(() => string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name));
                Ui(direct);
                Wait(200);
            }
        }

        private void Scroll(int notches)
        {
            if (!_direct) Mouse(MOUSEEVENTF_WHEEL, notches * 120);
            else Ui(() => _window.ScrollBy(-notches * 48));
        }

        private void KeyEvent(ushort vk, bool up)
        {
            var input = new INPUT { type = 1 };
            input.U.ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private void Key(ushort vk, bool ctrl = false)
        {
            if (_direct) return;
            if (ctrl) KeyEvent(VK_CONTROL, false);
            KeyEvent(vk, false);
            Wait(40);
            KeyEvent(vk, true);
            if (ctrl) KeyEvent(VK_CONTROL, true);
            Wait(120);
        }

        /// <summary>Types text through the keyboard, whatever has the focus.</summary>
        private void SendText(string text)
        {
            foreach (char c in text)
            {
                foreach (bool up in new[] { false, true })
                {
                    var input = new INPUT { type = 1 };
                    input.U.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) };
                    SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
                }
                Wait(_random.Next(24, 58));
            }
        }

        /// <summary>Types into the document.</summary>
        private void Type(string text)
        {
            if (!_direct)
            {
                SendText(text);
                Wait(250);
                return;
            }
            var editor = Ui(() => _window.EditorBox);
            foreach (char c in text)
            {
                Ui(() =>
                {
                    editor.CaretPosition.InsertTextInRun(c.ToString());
                    editor.CaretPosition = editor.CaretPosition.GetNextInsertionPosition(LogicalDirection.Forward) ?? editor.CaretPosition;
                });
                Wait(_random.Next(24, 58));
            }
            Wait(250);
        }

        private void TypeInto(TextBox box, string text)
        {
            if (!_direct)
            {
                SendText(text);
                Wait(300);
                if (Ui(() => box.Text) == text) return;
                Log("text field filled directly: " + text);
            }
            Ui(() => box.Text = "");
            foreach (char c in text)
            {
                Ui(() => { box.Text += c; box.CaretIndex = box.Text.Length; });
                Wait(_random.Next(24, 58));
            }
            Wait(300);
        }

        private void Enter()
        {
            if (!_direct) Key(VK_RETURN);
            else Ui(() => _window.PressEnter());
            Wait(300);
        }

        private bool WaitForOtherWindow(IntPtr main, int timeoutMs)
        {
            for (int waited = 0; waited < timeoutMs; waited += 100)
            {
                var foreground = GetForegroundWindow();
                if (foreground != IntPtr.Zero && foreground != main) return true;
                Wait(100);
            }
            Log("no dialog appeared");
            return false;
        }

        // ---- Win32 ---------------------------------------------------------------------------

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_WHEEL = 0x0800;
        private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
        private const ushort VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_ESCAPE = 0x1B, VK_END = 0x23, VK_TAB = 0x09;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    }
}
