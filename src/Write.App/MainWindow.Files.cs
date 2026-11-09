using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using Write.Core;
using Write.Desktop.Editing;

namespace Write.Desktop;

public partial class MainWindow
{
    private const string OpenFilter =
        "Documenten (*.docx;*.rtf;*.txt)|*.docx;*.docm;*.dotx;*.rtf;*.txt|Word-document (*.docx)|*.docx;*.docm;*.dotx|Rich Text (*.rtf)|*.rtf|Tekst (*.txt)|*.txt|Alle bestanden (*.*)|*.*";

    private const string SaveFilter = "Word-document (*.docx)|*.docx|Rich Text (*.rtf)|*.rtf|Tekst zonder opmaak (*.txt)|*.txt";

    // ---- new / open --------------------------------------------------------------------------

    /// <summary>How far a new window is placed from the one it was opened from.</summary>
    internal static double CascadeOffset = 28;

    internal string? CurrentPath => _path;

    private MainWindow NewCascadedWindow() => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = Left + CascadeOffset,
        Top = Top + CascadeOffset,
        Topmost = Topmost,
    };

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (IsPristine) return;
        NewCascadedWindow().Show();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = OpenFilter, Title = "Openen" };
        if (_path != null) dialog.InitialDirectory = Path.GetDirectoryName(_path);
        if (dialog.ShowDialog(this) == true) OpenInBestWindow(dialog.FileName);
    }

    /// <summary>Opens a file here when this window holds nothing yet, otherwise in a new window.</summary>
    internal void OpenInBestWindow(string path)
    {
        if (IsPristine)
        {
            OpenPath(path);
            return;
        }
        var window = NewCascadedWindow();
        window.Show();
        if (!window.OpenPath(path)) window.Close();
    }

    /// <summary>Loads a file into this window. Returns false (after telling the user) when that fails.</summary>
    internal bool OpenPath(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".txt")
            {
                LoadModel(FromPlainText(ReadTextFile(path)), path);
            }
            else if (ext == ".rtf")
            {
                LoadModel(DocModel.CreateBlank(), path);
                _loading = true;
                try
                {
                    using var stream = File.OpenRead(path);
                    new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Load(stream, DataFormats.Rtf);
                    Editor.IsUndoEnabled = false;
                    Editor.IsUndoEnabled = true;
                    Editor.CaretPosition = Editor.Document.ContentStart;
                }
                finally { _loading = false; }
                UpdateStatus();
            }
            else
            {
                LoadModel(DocxReader.Read(File.ReadAllBytes(path)), path);
            }
            Settings.Current.AddRecent(path);
            return true;
        }
        catch (DocxFormatException ex)
        {
            MessageBox.Show(this, $"'{Path.GetFileName(path)}' kan niet worden geopend.\n\n{ex.Message}", "Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            MessageBox.Show(this, $"'{Path.GetFileName(path)}' kan niet worden geopend.\n\n{ex.Message}", "Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return false;
    }

    private static string ReadTextFile(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static DocModel FromPlainText(string text)
    {
        var doc = DocModel.CreateBlank();
        var para = doc.Styles.ResolvePara(null);
        var run = doc.Styles.ResolveRun(null);
        var blocks = new List<Write.Core.Block>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var p = new Para { Props = para };
            if (line.Length > 0) p.Inlines.Add(new TextRun { Text = line, Props = run });
            blocks.Add(p);
        }
        doc.Blocks = blocks;
        return doc;
    }

    private void FileMenu_Opened(object sender, RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        var recent = Settings.Current.Recent.Where(File.Exists).Take(10).ToList();
        RecentMenu.IsEnabled = recent.Count > 0;
        int n = 1;
        foreach (var path in recent)
        {
            var item = new MenuItem { Header = $"_{n++ % 10}  {Path.GetFileName(path).Replace("_", "__")}", ToolTip = path };
            item.Click += (_, _) => OpenInBestWindow(path);
            RecentMenu.Items.Add(item);
        }
    }

    // ---- save --------------------------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    internal bool Save()
    {
        if (_path == null) return SaveAs();
        var ext = Path.GetExtension(_path).ToLowerInvariant();
        if (ext is not (".docx" or ".rtf" or ".txt")) return SaveAs(); // templates and macro documents are saved as a copy
        return SaveTo(_path);
    }

    internal bool SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = SaveFilter,
            Title = "Opslaan als",
            AddExtension = true,
            DefaultExt = ".docx",
            FileName = _path != null ? Path.GetFileNameWithoutExtension(_path) : SuggestName(),
        };
        if (_path != null)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_path);
            var ext = Path.GetExtension(_path).ToLowerInvariant();
            dialog.FilterIndex = ext == ".rtf" ? 2 : ext == ".txt" ? 3 : 1;
        }
        if (dialog.ShowDialog(this) != true) return false;
        return SaveTo(dialog.FileName);
    }

    private string SuggestName()
    {
        var first = FlowWalker.Paragraphs(Editor.Document.Blocks)
            .Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim())
            .FirstOrDefault(t => t.Length > 0) ?? "";
        var clean = new string(first.Take(60).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim(' ', '.');
        return clean.Length > 0 ? clean : "Document";
    }

    internal bool SaveTo(string path)
    {
        try
        {
            byte[] data;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".txt")
            {
                data = new UTF8Encoding(true).GetPreamble()
                    .Concat(Encoding.UTF8.GetBytes(new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text)).ToArray();
            }
            else if (ext == ".rtf")
            {
                using var ms = new MemoryStream();
                new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Save(ms, DataFormats.Rtf);
                data = ms.ToArray();
            }
            else
            {
                data = DocxWriter.Write(CurrentModel());
            }

            // Write next to the target first, so a failure never leaves a half-written document.
            var temp = path + ".~write";
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception) { /* nothing left to clean up */ }
                throw;
            }

            _path = Path.GetFullPath(path);
            _dirty = false;
            UpdateTitle();
            Settings.Current.AddRecent(_path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            MessageBox.Show(this, $"Het document kan niet worden opgeslagen.\n\n{ex.Message}", "Write", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>Asks what to do with unsaved changes. Returns false when the user cancels.</summary>
    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var name = _path != null ? Path.GetFileName(_path) : "dit document";
        var answer = MessageBox.Show(this, $"Wilt u de wijzigingen in {name} opslaan?", "Write",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Forgets unsaved changes so the window can close without asking (self-test only).</summary>
    internal void DiscardChanges() => _dirty = false;

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var known = files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".docx" or ".docm" or ".dotx" or ".rtf" or ".txt").ToList();
            // Let the drag-and-drop operation finish before a dialog or new window appears.
            Dispatcher.BeginInvoke(new Action(() => { foreach (var f in known) OpenInBestWindow(f); }));
        }
    }

    // ---- page setup, print -------------------------------------------------------------------

    private static readonly (string Name, int W, int H)[] PaperSizes =
    {
        ("A4 (21 × 29,7 cm)", 11906, 16838), ("A5 (14,8 × 21 cm)", 8391, 11906), ("A3 (29,7 × 42 cm)", 16838, 23811),
        ("Letter (8,5 × 11 inch)", 12240, 15840), ("Legal (8,5 × 14 inch)", 12240, 20160),
    };

    private void PageSetup_Click(object sender, RoutedEventArgs e)
    {
        var (w, h) = _page.Landscape ? (_page.Height, _page.Width) : (_page.Width, _page.Height);
        int current = Array.FindIndex(PaperSizes, s => Math.Abs(s.W - w) <= 12 && Math.Abs(s.H - h) <= 12);
        var names = PaperSizes.Select(s => s.Name).ToList();
        if (current < 0)
        {
            names.Add($"Aangepast ({Units.TwipsToCm(w):0.#} × {Units.TwipsToCm(h):0.#} cm)");
            current = names.Count - 1;
        }

        var dialog = new FormDialog(this, "Pagina-instelling");
        int size = dialog.AddChoice("Papierformaat", names, current);
        int orientation = dialog.AddChoice("Afdrukstand", new[] { "Staand", "Liggend" }, _page.Landscape ? 1 : 0);
        int top = dialog.AddText("Marge boven (cm)", $"{Units.TwipsToCm(_page.MarginTop):0.##}");
        int bottom = dialog.AddText("Marge onder (cm)", $"{Units.TwipsToCm(_page.MarginBottom):0.##}");
        int left = dialog.AddText("Marge links (cm)", $"{Units.TwipsToCm(_page.MarginLeft):0.##}");
        int right = dialog.AddText("Marge rechts (cm)", $"{Units.TwipsToCm(_page.MarginRight):0.##}");
        if (!dialog.Run()) return;

        var v = dialog.Values;
        int index = int.Parse(v[size]);
        if (index >= 0 && index < PaperSizes.Length) (w, h) = (PaperSizes[index].W, PaperSizes[index].H);
        bool landscape = v[orientation] == "1";

        int Margin(int field, int fallback) =>
            FormDialog.TryNumber(v[field], out var cm) && cm >= 0 && cm < 20 ? Units.CmToTwips(cm) : fallback;

        var page = _page with
        {
            Width = landscape ? h : w,
            Height = landscape ? w : h,
            MarginTop = Margin(top, _page.MarginTop),
            MarginBottom = Margin(bottom, _page.MarginBottom),
            MarginLeft = Margin(left, _page.MarginLeft),
            MarginRight = Margin(right, _page.MarginRight),
        };
        if (page.MarginLeft + page.MarginRight > page.Width - 1440 || page.MarginTop + page.MarginBottom > page.Height - 1440)
        {
            MessageBox.Show(this, "De marges laten te weinig ruimte over voor tekst.", "Pagina-instelling", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (page == _page) return;
        _page = page;
        ApplyPageLayout();
        MarkDirty();
    }

    private void Print_Click(object sender, RoutedEventArgs e) => Print(toPdf: false);

    private void ExportPdf_Click(object sender, RoutedEventArgs e) => Print(toPdf: true);

    private void Print(bool toPdf)
    {
        try
        {
            var dialog = new PrintDialog();
            if (toPdf)
            {
                try
                {
                    using var server = new LocalPrintServer();
                    dialog.PrintQueue = server.GetPrintQueue("Microsoft Print to PDF");
                }
                catch (Exception)
                {
                    MessageBox.Show(this, "De printer 'Microsoft Print to PDF' is op deze computer niet beschikbaar. Kies in het afdrukvenster een PDF-printer.",
                        "Exporteren als PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                    toPdf = false;
                }
            }
            if (!toPdf && dialog.ShowDialog() != true) return;

            var paginator = CreatePaginator();
            var title = _path != null ? Path.GetFileNameWithoutExtension(_path) : "Document";
            dialog.PrintDocument(paginator, title);
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or IOException)
        {
            MessageBox.Show(this, $"Afdrukken is niet gelukt.\n\n{ex.Message}", "Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Lays the current document out on real pages, from a fresh copy; the editor's own document stays as it is.</summary>
    internal DocumentPaginator CreatePaginator()
    {
        var flow = new FlowBuilder(CurrentModel(), forPrint: true).Build();
        var paginator = ((IDocumentPaginatorSource)flow).DocumentPaginator;
        paginator.PageSize = new Size(FlowFormat.Dip(_page.Width), FlowFormat.Dip(_page.Height));
        return paginator;
    }

    private void Editor_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        OpenLink(e.Uri);
    }

    private static void OpenLink(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No handler for the link: nothing useful to do.
        }
    }
}
