using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Write.Core;
using Write.Desktop.Editing;

namespace Write.Desktop;

public partial class MainWindow : Window
{
    private DocModel _doc = null!;
    private PageSetup _page = new();
    private string? _path;
    private bool _dirty;
    private bool _loading;
    private bool _syncing;
    private bool _enterPending;
    private double _zoom = 1.0;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();

        var settings = Settings.Current;
        Width = Math.Clamp(settings.WindowWidth, 640, 4000);
        Height = Math.Clamp(settings.WindowHeight, 420, 3000);
        if (settings.Maximized) WindowState = WindowState.Maximized;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); UpdateStatus(); };

        InitFormatControls();
        Editor.AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler(Editor_RequestNavigate));
        PreviewKeyDown += Window_PreviewKeyDown;
        PreviewDragOver += Window_PreviewDragOver;
        PreviewDrop += Window_PreviewDrop;

        LoadModel(DocModel.CreateBlank(), null);
        SetZoom(Math.Clamp(settings.Zoom, 0.25, 3.0));
        SpellMenu.IsChecked = settings.SpellCheck;
        Editor.SpellCheck.IsEnabled = settings.SpellCheck;
    }

    internal RichTextBox EditorBox => Editor;
    internal DocModel Document => _doc;
    internal bool IsPristine => _path == null && !_dirty;

    // ---- document ↔ editor -------------------------------------------------------------------

    internal void LoadModel(DocModel doc, string? path)
    {
        _loading = true;
        try
        {
            _doc = doc;
            _page = doc.Page;
            _path = path;
            var flow = new FlowBuilder(doc).Build();
            try { flow.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag); }
            catch (ArgumentException) { }
            Editor.Document = flow;
            ApplyPageLayout();
            FlowReader.TakeSnapshots(flow, doc);
            // Start with an empty undo history for the new document.
            Editor.IsUndoEnabled = false;
            Editor.IsUndoEnabled = true;
            PopulateStyles();
            _dirty = false;
            Editor.CaretPosition = flow.ContentStart;
        }
        finally
        {
            _loading = false;
        }
        UpdateTitle();
        UpdateToolbarState();
        UpdateStatus();
        Scroller.ScrollToHome();
        Editor.Focus();
    }

    /// <summary>The document as it currently stands in the editor.</summary>
    internal DocModel CurrentModel() => new FlowReader(_doc).Read(Editor.Document, _page);

    private void ApplyPageLayout()
    {
        Editor.Width = FlowFormat.Dip(_page.Width);
        Editor.MinHeight = FlowFormat.Dip(_page.Height);
        FlowBuilder.ApplyPage(Editor.Document, _page, forPrint: false);
        StatusInfo.Text = DescribePage(_page);
    }

    private static string DescribePage(PageSetup page)
    {
        var (w, h) = page.Landscape ? (page.Height, page.Width) : (page.Width, page.Height);
        string name = (w, h) switch
        {
            (>= 11896 and <= 11916, >= 16828 and <= 16848) => "A4",
            (>= 8380 and <= 8400, >= 11896 and <= 11916) => "A5",
            (>= 16828 and <= 16848, >= 23801 and <= 23821) => "A3",
            (>= 12230 and <= 12250, >= 15830 and <= 15850) => "Letter",
            (>= 12230 and <= 12250, >= 20150 and <= 20170) => "Legal",
            _ => $"{Units.TwipsToCm(page.Width):0.#} × {Units.TwipsToCm(page.Height):0.#} cm",
        };
        return name + (page.Landscape ? ", liggend" : ", staand");
    }

    private void MarkDirty()
    {
        if (_loading || _dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _path != null ? System.IO.Path.GetFileName(_path) : "Naamloos document";
        Title = (_dirty ? "● " : "") + name + " – Write";
    }

    private void UpdateStatus()
    {
        var text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
        int words = 0;
        bool inWord = false;
        int chars = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else
            {
                chars++;
                if (!inWord) { words++; inWord = true; }
            }
        }
        StatusWords.Text = $"{words:N0} {(words == 1 ? "woord" : "woorden")}  ·  {chars:N0} tekens";
    }

    // ---- editor events -----------------------------------------------------------------------

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        MarkDirty();
        _statusTimer.Stop();
        _statusTimer.Start();
        if (_enterPending)
        {
            _enterPending = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FixAfterParagraphSplit));
        }
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateToolbarState();
    }

    private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetZoom(_zoom + (e.Delta > 0 ? 0.1 : -0.1));
            return;
        }
        // The editor never scrolls by itself; hand the wheel to the page scroller.
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender };
        Scroller.RaiseEvent(args);
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Enter && mods == ModifierKeys.None)
        {
            _enterPending = true;
            return;
        }

        if (e.Key == Key.Back && mods == ModifierKeys.None && Editor.Selection.IsEmpty)
        {
            // Backspace at the start of a paragraph that begins a new page removes the page break.
            var para = Editor.CaretPosition.Paragraph;
            if (para != null && para.BreakPageBefore &&
                Editor.CaretPosition.CompareTo(para.ContentStart.GetInsertionPosition(LogicalDirection.Forward)) <= 0)
            {
                FlowFormat.SetPageBreak(para, false);
                MarkDirty();
                e.Handled = true;
            }
            return;
        }

        if (e.Key is Key.PageDown or Key.PageUp && (mods & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            MoveByPage(e.Key == Key.PageDown, (mods & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
    }

    private void MoveByPage(bool down, bool extend)
    {
        var rect = Editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
        double delta = Math.Max(40, Scroller.ViewportHeight / _zoom * 0.9) * (down ? 1 : -1);
        var point = new Point(rect.X, rect.Y + rect.Height / 2 + delta);
        point.Y = Math.Clamp(point.Y, 1, Math.Max(1, Editor.ActualHeight - 1));
        var target = Editor.GetPositionFromPoint(point, true);
        if (target != null)
        {
            if (extend)
            {
                if (down) Editor.Selection.Select(Editor.Selection.Start, target);
                else Editor.Selection.Select(target, Editor.Selection.End);
            }
            else Editor.CaretPosition = target;
        }
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + delta * _zoom);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = mods == ModifierKeys.Control;
        bool ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            CloseFindBar();
            e.Handled = true;
            return;
        }
        if (!ctrl && !ctrlShift) return;

        bool handled = true;
        if (ctrl)
        {
            switch (key)
            {
                case Key.N: New_Click(this, e); break;
                case Key.O: Open_Click(this, e); break;
                case Key.S: Save_Click(this, e); break;
                case Key.P: Print_Click(this, e); break;
                case Key.F:
                case Key.H: Find_Click(this, e); break;
                case Key.K: InsertLink_Click(this, e); break;
                case Key.U: Underline_Click(this, e); break;
                case Key.Enter: PageBreak_Click(this, e); break;
                case Key.Space: ClearFormatting_Click(this, e); break;
                case Key.OemPlus:
                case Key.Add: SetZoom(_zoom + 0.1); break;
                case Key.OemMinus:
                case Key.Subtract: SetZoom(_zoom - 0.1); break;
                case Key.D0:
                case Key.NumPad0: SetZoom(1.0); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (key)
            {
                case Key.S: SaveAs_Click(this, e); break;
                case Key.V: PastePlain_Click(this, e); break;
                default: handled = false; break;
            }
        }
        if (handled) e.Handled = true;
    }

    // ---- paragraph bookkeeping ---------------------------------------------------------------

    private static Paragraph? PreviousParagraph(Paragraph p)
    {
        if (p.PreviousBlock is Paragraph prev) return prev;
        if (p.PreviousBlock == null && p.Parent is ListItem item && item.PreviousListItem is { } before)
            return before.Blocks.LastBlock as Paragraph;
        return null;
    }

    /// <summary>
    /// After Enter splits a paragraph, both halves must know where they came from, and a paragraph
    /// that follows a heading goes back to the style that heading names as its successor.
    /// </summary>
    private void FixAfterParagraphSplit()
    {
        var para = Editor.CaretPosition.Paragraph;
        if (para == null) return;
        var prev = PreviousParagraph(para);
        if (prev == null) return;

        var tag = para.Tag as ParaTag;
        var prevTag = prev.Tag as ParaTag;
        var origin = prevTag ?? tag;
        if (origin == null) return;
        if (tag != null && prevTag != null && !ReferenceEquals(tag, prevTag)) return; // not a split of one paragraph

        var first = origin.Clone();
        first.DropSection = true; // a section break stays with the end of the paragraph
        var second = origin.Clone();
        second.DropBookmarks = true;
        second.BreakBr = false;
        if (second.Snapshot != null) second.Snapshot = second.Snapshot with { PageBreakBefore = false };
        second.Precise = second.Precise with { PageBreakBefore = false };
        prev.Tag = first;
        para.Tag = second;

        _loading = true; // these adjustments are bookkeeping, not edits
        try
        {
            if (para.BreakPageBefore) FlowFormat.SetPageBreak(para, false);
            bool empty = new TextRange(para.ContentStart, para.ContentEnd).IsEmpty;
            var next = origin.StyleId != null ? _doc.Styles.Get(origin.StyleId)?.Next : null;
            if (empty && next != null && next != origin.StyleId && _doc.Styles.Get(next) != null)
            {
                ApplyParagraphStyle(para, next == _doc.Styles.DefaultParagraphStyleId ? null : next);
                // Text typed next must not inherit the heading's character formatting.
                Editor.Selection.ClearAllProperties();
            }
        }
        finally
        {
            _loading = false;
        }
        UpdateToolbarState();
    }

    /// <summary>Does what pressing Enter does, including the bookkeeping that follows it.</summary>
    internal void PressEnter()
    {
        EditingCommands.EnterParagraphBreak.Execute(null, Editor);
        FixAfterParagraphSplit();
    }

    private string? TableStyleOf(DependencyObject element)
    {
        var table = FlowWalker.Ancestor<Table>(element);
        if (table == null) return null;
        var id = table.Tag is TableTag tag ? tag.Source.StyleId : _doc.Styles.Get("TableGrid")?.Id;
        return id != null && _doc.Styles.Get(id) != null ? id : null;
    }

    /// <summary>Gives a paragraph a style: its formatting becomes that of the style.</summary>
    private void ApplyParagraphStyle(Paragraph para, string? styleId)
    {
        var tableStyle = TableStyleOf(para);
        var resolved = _doc.Styles.ResolvePara(styleId, tableStyle);
        var baseRun = _doc.Styles.ResolveRun(styleId, null, tableStyle);
        bool inList = para.Parent is ListItem;
        bool hadBreak = para.BreakPageBefore;
        var old = para.Tag as ParaTag;

        FlowFormat.ApplyPara(para, resolved, baseRun, inList);
        FlowFormat.SetPageBreak(para, hadBreak || resolved.PageBreakBefore);

        var tag = new ParaTag
        {
            Source = old?.Source,
            StyleId = styleId,
            StyleApplied = true,
            Precise = resolved,
            BreakBr = hadBreak && !resolved.PageBreakBefore,
            DropSection = old?.DropSection ?? false,
            DropBookmarks = old?.DropBookmarks ?? false,
        };
        tag.Snapshot = FlowFormat.ReadPara(para, inList, _doc.Styles.ResolvePara(null));
        para.Tag = tag;
    }

    // ---- zoom --------------------------------------------------------------------------------

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(Math.Round(zoom * 20) / 20, 0.25, 3.0);
        PageBorder.LayoutTransform = Math.Abs(_zoom - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(_zoom, _zoom);
        ZoomText.Text = $"{_zoom * 100:0}%";
        _syncing = true;
        ZoomSlider.Value = _zoom * 100;
        _syncing = false;
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsInitialized || ZoomText == null) return;
        SetZoom(e.NewValue / 100.0);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom + 0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom - 0.1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void Spell_Click(object sender, RoutedEventArgs e)
    {
        Editor.SpellCheck.IsEnabled = SpellMenu.IsChecked;
        Settings.Current.SpellCheck = SpellMenu.IsChecked;
        Settings.Current.Save();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        MessageBox.Show(this,
            $"Write {version?.ToString(3)}\n\nTekstverwerker voor Word-documenten (.docx).\n" +
            "Onderdelen die Write niet kan bewerken – kop- en voetteksten, voetnoten, velden, vormen – blijven bij het opslaan behouden.",
            "Over Write", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- closing -----------------------------------------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        var settings = Settings.Current;
        settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
        }
        settings.Zoom = _zoom;
        settings.Save();
    }
}
