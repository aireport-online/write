using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Write.Core;

namespace Write.Desktop.Editing;

/// <summary>Conversions between document formatting (twips, half-points) and WPF text elements.</summary>
internal static class FlowFormat
{
    /// <summary>Font scale used to show superscript and subscript text.</summary>
    public const double ScriptScale = 0.65;

    public static double Dip(int twips) => twips / 15.0;
    public static int Twips(double dip) => double.IsNaN(dip) || double.IsInfinity(dip) ? 0 : (int)Math.Round(dip * 15.0);
    public static double SizeDip(int halfPoints) => halfPoints * 2.0 / 3.0;
    public static int HalfPoints(double dip) => Math.Max(2, (int)Math.Round(dip * 1.5));
    public static double EmuToDip(long emu) => emu / 9525.0;
    public static long DipToEmu(double dip) => (long)Math.Round(dip * 9525.0);

    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new();
    private static readonly Dictionary<string, FontFamily> FontCache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush Brush(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var brush)) return brush;
        byte r = 0, g = 0, b = 0;
        if (hex.Length == 6)
        {
            r = Convert.ToByte(hex.Substring(0, 2), 16);
            g = Convert.ToByte(hex.Substring(2, 2), 16);
            b = Convert.ToByte(hex.Substring(4, 2), 16);
        }
        brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        BrushCache[hex] = brush;
        return brush;
    }

    public static string? Hex(Brush? brush)
    {
        if (brush is not SolidColorBrush solid || solid.Color.A == 0) return null;
        var c = solid.Color;
        return $"{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    public static FontFamily Font(string name)
    {
        if (FontCache.TryGetValue(name, out var family)) return family;
        try { family = new FontFamily(name); }
        catch (ArgumentException) { family = new FontFamily("Segoe UI"); }
        FontCache[name] = family;
        return family;
    }

    /// <summary>The first font name of a WPF font family ("Calibri, Arial" → "Calibri").</summary>
    public static string FontName(FontFamily? family)
    {
        var source = family?.Source ?? "";
        int comma = source.IndexOf(',');
        if (comma >= 0) source = source[..comma];
        int hash = source.LastIndexOf('#');
        if (hash >= 0) source = source[(hash + 1)..];
        source = source.Trim();
        return source.Length == 0 ? "Segoe UI" : source;
    }

    public static double LineSpacingOf(FontFamily family)
    {
        double spacing = family.LineSpacing;
        return spacing > 0.5 && spacing < 3 ? spacing : 1.2;
    }

    public static TextAlignment ToWpf(Align align) => align switch
    {
        Align.Center => TextAlignment.Center,
        Align.Right => TextAlignment.Right,
        Align.Justify => TextAlignment.Justify,
        _ => TextAlignment.Left,
    };

    public static Align FromWpf(TextAlignment align) => align switch
    {
        TextAlignment.Center => Align.Center,
        TextAlignment.Right => Align.Right,
        TextAlignment.Justify => Align.Justify,
        _ => Align.Left,
    };

    public static TextMarkerStyle ToMarker(ListFormat format) => format switch
    {
        ListFormat.Disc => TextMarkerStyle.Disc,
        ListFormat.Circle => TextMarkerStyle.Circle,
        ListFormat.Square => TextMarkerStyle.Square,
        ListFormat.LowerLetter => TextMarkerStyle.LowerLatin,
        ListFormat.UpperLetter => TextMarkerStyle.UpperLatin,
        ListFormat.LowerRoman => TextMarkerStyle.LowerRoman,
        ListFormat.UpperRoman => TextMarkerStyle.UpperRoman,
        ListFormat.None => TextMarkerStyle.None,
        _ => TextMarkerStyle.Decimal,
    };

    public static ListFormat FromMarker(TextMarkerStyle marker) => marker switch
    {
        TextMarkerStyle.Disc => ListFormat.Disc,
        TextMarkerStyle.Circle => ListFormat.Circle,
        TextMarkerStyle.Square or TextMarkerStyle.Box => ListFormat.Square,
        TextMarkerStyle.LowerLatin => ListFormat.LowerLetter,
        TextMarkerStyle.UpperLatin => ListFormat.UpperLetter,
        TextMarkerStyle.LowerRoman => ListFormat.LowerRoman,
        TextMarkerStyle.UpperRoman => ListFormat.UpperRoman,
        TextMarkerStyle.None => ListFormat.None,
        _ => ListFormat.Decimal,
    };

    private static readonly Brush BreakBrush = Brush("9AA0A6");

    /// <summary>Shows or hides the visual marker for "this paragraph starts on a new page".</summary>
    public static void SetPageBreak(Paragraph para, bool on)
    {
        para.BreakPageBefore = on;
        if (on)
        {
            para.BorderBrush = BreakBrush;
            para.BorderThickness = new Thickness(0, 1, 0, 0);
        }
        else
        {
            para.ClearValue(System.Windows.Documents.Block.BorderBrushProperty);
            para.ClearValue(System.Windows.Documents.Block.BorderThicknessProperty);
        }
    }

    /// <summary>Applies paragraph formatting plus the paragraph-level character formatting of its style.</summary>
    public static void ApplyPara(Paragraph para, ParaProps props, RunProps baseRun, bool inList)
    {
        var family = Font(baseRun.Font);
        double size = SizeDip(baseRun.Size);
        para.FontFamily = family;
        para.FontSize = size;
        para.FontWeight = baseRun.Bold ? FontWeights.Bold : FontWeights.Normal;
        para.FontStyle = baseRun.Italic ? FontStyles.Italic : FontStyles.Normal;
        para.Foreground = Brush(baseRun.Color);

        para.TextAlignment = ToWpf(props.Align);
        para.Margin = new Thickness(inList ? 0 : Math.Max(0, Dip(props.IndLeft)), 0, Math.Max(0, Dip(props.IndRight)), Dip(props.After));
        para.Padding = new Thickness(0, Dip(props.Before), 0, 0);
        para.TextIndent = inList ? 0 : Dip(props.IndFirst);

        ApplyLine(para, props.Line, props.LineRule);
    }

    /// <summary>Sets the line spacing of a paragraph from its document values.</summary>
    public static void ApplyLine(Paragraph para, int line, LineRule rule)
    {
        switch (rule)
        {
            case LineRule.Exact:
                para.LineHeight = Math.Clamp(Dip(line), 1, 10000);
                para.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                break;
            case LineRule.AtLeast:
                para.LineHeight = Math.Clamp(Dip(line), 1, 10000);
                para.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                break;
            default:
                para.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                para.LineHeight = line == 240
                    ? double.NaN
                    : Math.Clamp(line / 240.0 * para.FontSize * LineSpacingOf(para.FontFamily), 1, 10000);
                break;
        }
    }

    /// <summary>Reads paragraph formatting back from the editor. StyleId is not known here and stays null.</summary>
    public static ParaProps ReadPara(Paragraph para, bool inList, ParaProps fallback)
    {
        var m = para.Margin;
        var pad = para.Padding;
        static double Nz(double v) => double.IsNaN(v) ? 0 : v;

        int line = 240;
        var rule = LineRule.Auto;
        double lh = para.LineHeight;
        if (!double.IsNaN(lh) && lh > 0)
        {
            if (para.LineStackingStrategy == LineStackingStrategy.BlockLineHeight)
            {
                rule = LineRule.Exact;
                line = Twips(lh);
            }
            else
            {
                double natural = para.FontSize * LineSpacingOf(para.FontFamily);
                line = natural > 0 ? Math.Max(1, (int)Math.Round(lh / natural * 240.0)) : 240;
            }
        }

        return new ParaProps
        {
            Align = FromWpf(para.TextAlignment),
            IndLeft = inList ? 0 : Twips(Nz(m.Left) + Nz(pad.Left)),
            IndRight = Twips(Nz(m.Right) + Nz(pad.Right)),
            IndFirst = inList ? 0 : Twips(para.TextIndent),
            Before = Twips(Nz(pad.Top) + Nz(m.Top)),
            After = double.IsNaN(m.Bottom) ? fallback.After : Twips(m.Bottom + Nz(pad.Bottom)),
            Line = line,
            LineRule = rule,
            PageBreakBefore = para.BreakPageBefore,
        };
    }

    /// <summary>Takes the exact value wherever the editor still shows what it showed when the snapshot was taken.</summary>
    public static ParaProps Merge(ParaProps cur, ParaProps? snapshot, ParaProps precise)
    {
        if (snapshot == null) return cur;
        bool sameLine = cur.Line == snapshot.Line && cur.LineRule == snapshot.LineRule;
        return cur with
        {
            Align = cur.Align == snapshot.Align ? precise.Align : cur.Align,
            IndLeft = cur.IndLeft == snapshot.IndLeft ? precise.IndLeft : cur.IndLeft,
            IndRight = cur.IndRight == snapshot.IndRight ? precise.IndRight : cur.IndRight,
            IndFirst = cur.IndFirst == snapshot.IndFirst ? precise.IndFirst : cur.IndFirst,
            Before = cur.Before == snapshot.Before ? precise.Before : cur.Before,
            After = cur.After == snapshot.After ? precise.After : cur.After,
            Line = sameLine ? precise.Line : cur.Line,
            LineRule = sameLine ? precise.LineRule : cur.LineRule,
        };
    }

    public static TextDecorationCollection? Decorations(bool underline, bool strike)
    {
        if (!underline && !strike) return null;
        var result = new TextDecorationCollection();
        if (underline) result.Add(TextDecorations.Underline);
        if (strike) result.Add(TextDecorations.Strikethrough);
        result.Freeze();
        return result;
    }

    public static bool Has(TextDecorationCollection? decorations, TextDecorationLocation location) =>
        decorations != null && decorations.Any(d => d.Location == location);

    /// <summary>Sets on a run whatever differs from the paragraph-level formatting it inherits.</summary>
    public static void ApplyRun(TextElement run, RunProps props, RunProps baseRun, bool inLink)
    {
        if (!string.Equals(props.Font, baseRun.Font, StringComparison.OrdinalIgnoreCase)) run.FontFamily = Font(props.Font);
        if (props.Size != baseRun.Size || props.Vert != VertAlign.None)
            run.FontSize = SizeDip(props.Size) * (props.Vert != VertAlign.None ? ScriptScale : 1);
        if (props.Bold != baseRun.Bold) run.FontWeight = props.Bold ? FontWeights.Bold : FontWeights.Normal;
        if (props.Italic != baseRun.Italic) run.FontStyle = props.Italic ? FontStyles.Italic : FontStyles.Normal;
        if (props.Color != baseRun.Color || inLink) run.Foreground = Brush(props.Color);
        if (props.Highlight != null) run.Background = Brush(props.Highlight);
        if (run is System.Windows.Documents.Inline inline)
        {
            var decorations = Decorations(props.Underline, props.Strike);
            if (decorations != null) inline.TextDecorations = decorations;
            if (props.Vert == VertAlign.Superscript) inline.BaselineAlignment = BaselineAlignment.Superscript;
            else if (props.Vert == VertAlign.Subscript) inline.BaselineAlignment = BaselineAlignment.Subscript;
        }
    }

    /// <summary>Reads the effective character formatting of a run. StyleId stays null.</summary>
    public static RunProps ReadRun(System.Windows.Documents.Inline run, bool ambientUnderline = false, bool ambientStrike = false)
    {
        var vert = run.BaselineAlignment switch
        {
            BaselineAlignment.Superscript => VertAlign.Superscript,
            BaselineAlignment.Subscript => VertAlign.Subscript,
            _ => VertAlign.None,
        };
        double size = run.FontSize;
        if (vert != VertAlign.None) size /= ScriptScale;
        var decorations = run.TextDecorations;
        return new RunProps
        {
            Font = FontName(run.FontFamily),
            Size = HalfPoints(size),
            Bold = run.FontWeight.ToOpenTypeWeight() >= 600,
            Italic = run.FontStyle != FontStyles.Normal,
            Underline = ambientUnderline || Has(decorations, TextDecorationLocation.Underline),
            Strike = ambientStrike || Has(decorations, TextDecorationLocation.Strikethrough),
            Vert = vert,
            Color = Hex(run.Foreground) ?? "000000",
            Highlight = Hex(run.Background),
        };
    }

    public static RunProps Merge(RunProps cur, RunProps? snapshot, RunProps precise)
    {
        if (snapshot == null) return cur;
        return cur with
        {
            StyleId = precise.StyleId,
            Font = cur.Font == snapshot.Font ? precise.Font : cur.Font,
            Size = cur.Size == snapshot.Size ? precise.Size : cur.Size,
            Bold = cur.Bold == snapshot.Bold ? precise.Bold : cur.Bold,
            Italic = cur.Italic == snapshot.Italic ? precise.Italic : cur.Italic,
            Underline = cur.Underline == snapshot.Underline ? precise.Underline : cur.Underline,
            Strike = cur.Strike == snapshot.Strike ? precise.Strike : cur.Strike,
            Vert = cur.Vert == snapshot.Vert ? precise.Vert : cur.Vert,
            Color = cur.Color == snapshot.Color ? precise.Color : cur.Color,
            Highlight = cur.Highlight == snapshot.Highlight ? precise.Highlight : cur.Highlight,
        };
    }
}
