using System.Windows;
using System.Windows.Controls;

namespace Write.Desktop;

/// <summary>A small form dialog: labelled text fields and choices with OK / Annuleren.</summary>
internal sealed class FormDialog : Window
{
    private readonly Grid _grid;
    private readonly List<Func<string>> _values = new();
    private FrameworkElement? _first;

    public FormDialog(Window owner, string title, string? intro = null)
    {
        Owner = owner;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        MinWidth = 360;

        var root = new StackPanel { Margin = new Thickness(20) };
        if (intro != null)
            root.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 12) });

        _grid = new Grid();
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        root.Children.Add(_grid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Annuleren", IsCancel = true, MinWidth = 88 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _first?.Focus();
            if (_first is TextBox box) box.SelectAll();
        };
    }

    private void AddRow(string label, FrameworkElement editor)
    {
        int row = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 14, 4) };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        editor.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        _grid.Children.Add(text);
        _grid.Children.Add(editor);
        _first ??= editor;
    }

    /// <summary>Adds a text field and returns its index in <see cref="Values"/>.</summary>
    public int AddText(string label, string value = "")
    {
        var box = new TextBox { Text = value };
        AddRow(label, box);
        _values.Add(() => box.Text);
        return _values.Count - 1;
    }

    public int AddChoice(string label, IReadOnlyList<string> options, int selected = 0)
    {
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = selected };
        AddRow(label, combo);
        _values.Add(() => combo.SelectedIndex.ToString());
        return _values.Count - 1;
    }

    public string[] Values => _values.Select(v => v()).ToArray();

    public bool Run() => ShowDialog() == true;

    /// <summary>Parses a number the way a Dutch or English user may type it ("2,5" or "2.5").</summary>
    public static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
}
