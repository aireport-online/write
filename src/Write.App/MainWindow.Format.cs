using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Write.Core;
using Write.Desktop.Editing;

namespace Write.Desktop;

public partial class MainWindow
{
    private sealed record StyleItem(string? Id, string Name, string Display, bool IsDefault);

    private static readonly double[] FontSizes = { 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72 };

    private static readonly Dictionary<string, string> StyleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = "Standaard", ["No Spacing"] = "Geen afstand", ["Title"] = "Titel", ["Subtitle"] = "Ondertitel",
        ["Quote"] = "Citaat", ["Intense Quote"] = "Duidelijk citaat", ["List Paragraph"] = "Lijstalinea",
        ["caption"] = "Bijschrift", ["Body Text"] = "Platte tekst", ["header"] = "Koptekst", ["footer"] = "Voettekst",
    };

    private static readonly string[] BuiltInStyles = { "No Spacing", "heading 1", "heading 2", "heading 3", "Title", "Subtitle", "Quote" };

    private static readonly string[] TextColors =
    {
        "000000", "434343", "666666", "999999", "C00000", "FF0000", "ED7D31", "FFC000",
        "70AD47", "00B050", "0F766E", "00B0F0", "0563C1", "1F3763", "7030A0", "C55A9E",
    };

    private static readonly string[] HighlightColors =
    {
        "FFFF00", "00FF00", "00FFFF", "FF00FF", "FFD966", "F4B183", "C5E0B3", "BDD7EE",
    };

    private string _fontColor = "C00000";
    private string _highlightColor = "FFFF00";
    private Popup? _palette;

    private void InitFormatControls()
    {
        var fonts = Fonts.SystemFontFamilies.Select(f => FlowFormat.FontName(f)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        FontBox.ItemsSource = fonts;
        SizeBox.ItemsSource = FontSizes.Select(FormatSize).ToList();
    }

    private static string FormatSize(double points) => points.ToString("0.#", CultureInfo.CurrentCulture);

    private static string DisplayName(string name)
    {
        if (StyleNames.TryGetValue(name, out var nl)) return nl;
        if (name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(8), out int level)) return "Kop " + level;
        return name;
    }

    private void PopulateStyles()
    {
        var styles = _doc.Styles;
        var items = new List<StyleItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var normal = styles.Get(styles.DefaultParagraphStyleId);
        items.Add(new StyleItem(null, normal?.Name ?? "Normal", DisplayName(normal?.Name ?? "Normal"), true));
        seen.Add(normal?.Name ?? "Normal");

        foreach (var name in BuiltInStyles)
        {
            var existing = styles.FindByName(name);
            items.Add(new StyleItem(existing?.Id, name, DisplayName(name), false));
            seen.Add(name);
        }
        foreach (var style in styles.ParagraphStylesForUi().Where(s => s.QuickFormat && !s.IsDefault).Take(40))
            if (seen.Add(style.Name))
                items.Add(new StyleItem(style.Id, style.Name, DisplayName(style.Name), false));

        _syncing = true;
        StyleBox.ItemsSource = items;
        _syncing = false;
    }

    // ---- toolbar state -----------------------------------------------------------------------

    private void UpdateToolbarState()
    {
        if (_doc == null) return;
        _syncing = true;
        try
        {
            var selection = Editor.Selection;

            var weight = selection.GetPropertyValue(TextElement.FontWeightProperty);
            BoldButton.IsChecked = weight is FontWeight w && w.ToOpenTypeWeight() >= 600;
            var fontStyle = selection.GetPropertyValue(TextElement.FontStyleProperty);
            ItalicButton.IsChecked = fontStyle is FontStyle fs && fs != FontStyles.Normal;
            var decorations = selection.GetPropertyValue(System.Windows.Documents.Inline.TextDecorationsProperty) as TextDecorationCollection;
            UnderlineButton.IsChecked = FlowFormat.Has(decorations, TextDecorationLocation.Underline);
            StrikeButton.IsChecked = FlowFormat.Has(decorations, TextDecorationLocation.Strikethrough);
            var baseline = selection.GetPropertyValue(System.Windows.Documents.Inline.BaselineAlignmentProperty);
            var script = FlowFormat.ScriptOf(baseline);
            SubButton.IsChecked = script == VertAlign.Subscript;
            SuperButton.IsChecked = script == VertAlign.Superscript;

            var family = selection.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily;
            FontBox.Text = family != null ? FlowFormat.FontName(family) : "";

            if (selection.GetPropertyValue(TextElement.FontSizeProperty) is double size)
            {
                if (script != VertAlign.None) size /= FlowFormat.ScriptScale;
                SizeBox.Text = FormatSize(Math.Round(size * 72 / 96 * 2) / 2);
            }
            else SizeBox.Text = "";

            var align = selection.GetPropertyValue(Paragraph.TextAlignmentProperty);
            AlignLeftButton.IsChecked = align is TextAlignment.Left;
            AlignCenterButton.IsChecked = align is TextAlignment.Center;
            AlignRightButton.IsChecked = align is TextAlignment.Right;
            JustifyButton.IsChecked = align is TextAlignment.Justify;

            var para = selection.Start.Paragraph;
            var list = (para?.Parent as ListItem)?.List;
            bool bullet = list != null && FlowFormat.FromMarker(list.MarkerStyle).IsBullet();
            BulletsButton.IsChecked = list != null && bullet;
            NumberingButton.IsChecked = list != null && !bullet;

            var styleId = (para?.Tag as ParaTag)?.StyleId;
            var items = StyleBox.ItemsSource as List<StyleItem>;
            StyleItem? match = null;
            if (items != null)
            {
                match = styleId == null ? items.FirstOrDefault(i => i.IsDefault) : items.FirstOrDefault(i => i.Id == styleId);
                if (match == null && styleId != null && _doc.Styles.Get(styleId) is { } def)
                {
                    // A style that is in use but not offered by default: add it to the list.
                    match = new StyleItem(def.Id, def.Name, DisplayName(def.Name), false);
                    items = items.Append(match).ToList();
                    StyleBox.ItemsSource = items;
                }
            }
            StyleBox.SelectedItem = match;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Combo_DropDownClosed(object? sender, EventArgs e) => Editor.Focus();

    // ---- character formatting ----------------------------------------------------------------

    private void Apply(DependencyProperty property, object? value)
    {
        Editor.Selection.ApplyPropertyValue(property, value);
        MarkDirty();
        UpdateToolbarState();
        Editor.Focus();
    }

    private void Bold_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleBold.Execute(null, Editor);
        UpdateToolbarState();
    }

    private void Italic_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleItalic.Execute(null, Editor);
        UpdateToolbarState();
    }

    private void Underline_Click(object sender, RoutedEventArgs e) => ToggleDecoration(TextDecorationLocation.Underline);

    private void Strike_Click(object sender, RoutedEventArgs e) => ToggleDecoration(TextDecorationLocation.Strikethrough);

    private void ToggleDecoration(TextDecorationLocation location)
    {
        var current = Editor.Selection.GetPropertyValue(System.Windows.Documents.Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool underline = FlowFormat.Has(current, TextDecorationLocation.Underline);
        bool strike = FlowFormat.Has(current, TextDecorationLocation.Strikethrough);
        if (location == TextDecorationLocation.Underline) underline = !underline; else strike = !strike;
        Apply(System.Windows.Documents.Inline.TextDecorationsProperty, FlowFormat.Decorations(underline, strike) ?? new TextDecorationCollection());
    }

    private void Subscript_Click(object sender, RoutedEventArgs e) => ToggleScript(VertAlign.Subscript);

    private void Superscript_Click(object sender, RoutedEventArgs e) => ToggleScript(VertAlign.Superscript);

    internal void ToggleScriptForTest(VertAlign wanted) => ToggleScript(wanted);

    internal void OpenStyleList(bool open) => StyleBox.IsDropDownOpen = open;

    private void ToggleScript(VertAlign wanted)
    {
        var selection = Editor.Selection;
        var current = FlowFormat.ScriptOf(selection.GetPropertyValue(System.Windows.Documents.Inline.BaselineAlignmentProperty));
        bool wasScript = current != VertAlign.None;
        bool turnOff = current == wanted;
        var target = wanted == VertAlign.Superscript ? FlowFormat.SuperAlignment : FlowFormat.SubAlignment;
        double? size = selection.GetPropertyValue(TextElement.FontSizeProperty) is double s ? s : null;

        Editor.BeginChange();
        try
        {
            selection.ApplyPropertyValue(System.Windows.Documents.Inline.BaselineAlignmentProperty, turnOff ? BaselineAlignment.Baseline : target);
            if (size != null)
            {
                if (turnOff) selection.ApplyPropertyValue(TextElement.FontSizeProperty, size.Value / FlowFormat.ScriptScale);
                else if (!wasScript) selection.ApplyPropertyValue(TextElement.FontSizeProperty, size.Value * FlowFormat.ScriptScale);
            }
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        UpdateToolbarState();
        Editor.Focus();
    }

    private void ClearFormatting_Click(object sender, RoutedEventArgs e)
    {
        Editor.Selection.ClearAllProperties();
        MarkDirty();
        UpdateToolbarState();
        Editor.Focus();
    }

    private void FontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || FontBox.SelectedItem is not string name) return;
        Apply(TextElement.FontFamilyProperty, FlowFormat.Font(name));
    }

    private void FontBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var name = FontBox.Text.Trim();
        if (name.Length > 0) Apply(TextElement.FontFamilyProperty, FlowFormat.Font(name));
    }

    private void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SizeBox.SelectedItem is not string text) return;
        ApplyFontSize(text);
    }

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyFontSize(SizeBox.Text);
    }

    private void ApplyFontSize(string text)
    {
        if (!FormDialog.TryNumber(text, out double points) || points < 1 || points > 400) return;
        points = Math.Round(points * 2) / 2;
        double dip = points * 96 / 72;
        var baseline = Editor.Selection.GetPropertyValue(System.Windows.Documents.Inline.BaselineAlignmentProperty);
        if (FlowFormat.ScriptOf(baseline) != VertAlign.None) dip *= FlowFormat.ScriptScale;
        Apply(TextElement.FontSizeProperty, dip);
    }

    // ---- colours -----------------------------------------------------------------------------

    private void FontColor_Click(object sender, RoutedEventArgs e) =>
        Apply(TextElement.ForegroundProperty, FlowFormat.Brush(_fontColor));

    private void Highlight_Click(object sender, RoutedEventArgs e) =>
        Apply(TextElement.BackgroundProperty, FlowFormat.Brush(_highlightColor));

    private void FontColorMore_Click(object sender, RoutedEventArgs e) =>
        ShowPalette(FontColorButton, TextColors, "Automatisch", color =>
        {
            if (color == null) Apply(TextElement.ForegroundProperty, FlowFormat.Brush("000000"));
            else
            {
                _fontColor = color;
                FontColorBar.Fill = FlowFormat.Brush(color);
                Apply(TextElement.ForegroundProperty, FlowFormat.Brush(color));
            }
        });

    private void HighlightMore_Click(object sender, RoutedEventArgs e) =>
        ShowPalette(HighlightButton, HighlightColors, "Geen markering", color =>
        {
            if (color == null) Apply(TextElement.BackgroundProperty, null);
            else
            {
                _highlightColor = color;
                HighlightBar.Fill = FlowFormat.Brush(color);
                Apply(TextElement.BackgroundProperty, FlowFormat.Brush(color));
            }
        });

    private void ShowPalette(UIElement anchor, string[] colors, string noneLabel, Action<string?> choose)
    {
        if (_palette != null) _palette.IsOpen = false;
        var grid = new UniformGrid { Columns = 8 };
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        foreach (var color in colors)
        {
            var swatch = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(2), Focusable = false, ToolTip = "#" + color,
                Style = (Style)FindResource("ToolButton"),
                Content = new Rectangle { Width = 18, Height = 18, RadiusX = 3, RadiusY = 3, Fill = FlowFormat.Brush(color), Stroke = FlowFormat.Brush("C8CCD0"), StrokeThickness = 1 },
            };
            swatch.Click += (_, _) => { popup.IsOpen = false; choose(color); };
            grid.Children.Add(swatch);
        }
        var none = new Button { Content = noneLabel, Margin = new Thickness(2, 6, 2, 0), Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        none.Click += (_, _) => { popup.IsOpen = false; choose(null); };
        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(none);
        popup.Child = new Border
        {
            Background = Brushes.White, BorderBrush = FlowFormat.Brush("D0D4D8"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 2, 8, 8), Child = panel,
        };
        _palette = popup;
        popup.IsOpen = true;
    }

    // ---- paragraph formatting ----------------------------------------------------------------

    private void AlignLeft_Click(object sender, RoutedEventArgs e) => RunEditing(EditingCommands.AlignLeft);
    private void AlignCenter_Click(object sender, RoutedEventArgs e) => RunEditing(EditingCommands.AlignCenter);
    private void AlignRight_Click(object sender, RoutedEventArgs e) => RunEditing(EditingCommands.AlignRight);
    private void Justify_Click(object sender, RoutedEventArgs e) => RunEditing(EditingCommands.AlignJustify);

    private void RunEditing(RoutedUICommand command)
    {
        command.Execute(null, Editor);
        MarkDirty();
        UpdateToolbarState();
        Editor.Focus();
    }

    private void Bullets_Click(object sender, RoutedEventArgs e)
    {
        RunEditing(EditingCommands.ToggleBullets);
        TidyListsAtSelection();
    }

    private void Numbering_Click(object sender, RoutedEventArgs e)
    {
        RunEditing(EditingCommands.ToggleNumbering);
        TidyListsAtSelection();
    }

    /// <summary>
    /// After a list command: lists made by the editor get the same indentation as lists that come
    /// from a document, and paragraphs that left a list get their normal spacing back.
    /// </summary>
    private void TidyListsAtSelection()
    {
        bool wasLoading = _loading;
        _loading = true; // bookkeeping, not an edit
        try
        {
            foreach (var para in FlowWalker.SelectedParagraphs(Editor.Selection))
            {
                for (var list = FlowWalker.Ancestor<List>(para); list != null; list = FlowWalker.Ancestor<List>(list.Parent))
                {
                    if (list.Tag is ListTag) continue;
                    if (double.IsNaN(list.Margin.Top) || double.IsNaN(list.Margin.Bottom) || double.IsNaN(list.Margin.Left))
                        list.Margin = new Thickness(0);
                    if (double.IsNaN(list.Padding.Left)) list.Padding = new Thickness(48, 0, 0, 0);
                }
                NormalizeLooseParagraph(para);
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>
    /// A paragraph outside a list whose spacing is left to the editor ("auto") would be shown with a
    /// blank line above and below. Give it the spacing of its style, which is also what gets saved.
    /// </summary>
    private void NormalizeLooseParagraph(Paragraph para)
    {
        if (para.Parent is ListItem) return;
        var m = para.Margin;
        if (!double.IsNaN(m.Top) && !double.IsNaN(m.Bottom) && !double.IsNaN(m.Left) && !double.IsNaN(m.Right)) return;
        var props = (para.Tag as ParaTag)?.Precise ?? _doc.Styles.ResolvePara(null);
        para.Margin = new Thickness(double.IsNaN(m.Left) ? 0 : m.Left, 0, double.IsNaN(m.Right) ? 0 : m.Right, FlowFormat.Dip(props.After));
        var pad = para.Padding;
        if (double.IsNaN(pad.Top) || pad.Top == 0) para.Padding = new Thickness(0, FlowFormat.Dip(props.Before), 0, 0);
    }

    private void Editor_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        // List commands also arrive from the keyboard (Tab in a list, Ctrl+Shift+L); tidy up afterwards.
        if (e.Command == EditingCommands.ToggleBullets || e.Command == EditingCommands.ToggleNumbering ||
            e.Command == EditingCommands.IncreaseIndentation || e.Command == EditingCommands.DecreaseIndentation)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(TidyListsAtSelection));
    }

    private void Indent_Click(object sender, RoutedEventArgs e) => ChangeIndent(+1);
    private void Outdent_Click(object sender, RoutedEventArgs e) => ChangeIndent(-1);

    private void ChangeIndent(int direction)
    {
        var paragraphs = FlowWalker.SelectedParagraphs(Editor.Selection);
        if (paragraphs.Count == 0) return;
        if (paragraphs.Any(p => p.Parent is ListItem))
        {
            RunEditing(direction > 0 ? EditingCommands.IncreaseIndentation : EditingCommands.DecreaseIndentation);
            TidyListsAtSelection();
            return;
        }
        const double step = 48; // half an inch
        Editor.BeginChange();
        try
        {
            foreach (var p in paragraphs)
            {
                var m = p.Margin;
                double left = Math.Max(0, (double.IsNaN(m.Left) ? 0 : m.Left) + direction * step);
                p.Margin = new Thickness(left, double.IsNaN(m.Top) ? 0 : m.Top, double.IsNaN(m.Right) ? 0 : m.Right, m.Bottom);
            }
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        Editor.Focus();
    }

    private void Spacing_Click(object sender, RoutedEventArgs e)
    {
        SpacingMenu.PlacementTarget = SpacingButton;
        SpacingMenu.Placement = PlacementMode.Bottom;
        SpacingMenu.IsOpen = true;
    }

    private void LineSpacing_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out int line)) return;
        ForSelectedParagraphs(p => FlowFormat.ApplyLine(p, line, LineRule.Auto));
    }

    private void ParaSpacing_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        var parts = tag.Split(':');
        double dip = FlowFormat.Dip(int.Parse(parts[1]));
        ForSelectedParagraphs(p =>
        {
            if (parts[0] == "before") p.Padding = new Thickness(0, dip, 0, 0);
            else
            {
                var m = p.Margin;
                p.Margin = new Thickness(double.IsNaN(m.Left) ? 0 : m.Left, 0, double.IsNaN(m.Right) ? 0 : m.Right, dip);
            }
        });
    }

    private void ForSelectedParagraphs(Action<Paragraph> action)
    {
        var paragraphs = FlowWalker.SelectedParagraphs(Editor.Selection);
        if (paragraphs.Count == 0) return;
        Editor.BeginChange();
        try
        {
            foreach (var p in paragraphs) action(p);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        Editor.Focus();
    }

    private void StyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || StyleBox.SelectedItem is not StyleItem item) return;
        ApplyStyle(item.IsDefault ? null : item.Name);
    }

    /// <summary>
    /// Applies a paragraph style, given by its invariant name ("heading 1"), to the selected
    /// paragraphs; null means the default style. A built-in style the document lacks is added.
    /// </summary>
    internal void ApplyStyle(string? name)
    {
        string? id = null;
        if (name != null)
        {
            bool existed = _doc.Styles.FindByName(name) != null;
            id = _doc.Styles.EnsureBuiltIn(name);
            if (id == null) return;
            if (!existed) PopulateStyles();
        }
        ForSelectedParagraphs(p => ApplyParagraphStyle(p, id));
        UpdateToolbarState();
    }
}
