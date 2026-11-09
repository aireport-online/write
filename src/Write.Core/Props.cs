using System.Xml.Linq;

namespace Write.Core;

public enum Align { Left, Center, Right, Justify }

public enum LineRule { Auto, Exact, AtLeast }

public enum VertAlign { None, Superscript, Subscript }

/// <summary>
/// Effective (fully resolved) character formatting of a run: document defaults, styles
/// and direct formatting combined.
/// </summary>
public sealed record RunProps
{
    /// <summary>Character style id (w:rStyle), if any.</summary>
    public string? StyleId { get; init; }
    public string Font { get; init; } = "Times New Roman";
    /// <summary>Font size in half-points (w:sz).</summary>
    public int Size { get; init; } = 20;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public VertAlign Vert { get; init; }
    /// <summary>Text colour as RRGGBB; automatic colour is reported as black.</summary>
    public string Color { get; init; } = "000000";
    /// <summary>Highlight / character shading colour as RRGGBB, or null.</summary>
    public string? Highlight { get; init; }
}

/// <summary>Effective (fully resolved) paragraph formatting. Lengths are in twips.</summary>
public sealed record ParaProps
{
    /// <summary>Paragraph style id (w:pStyle); null means the default paragraph style.</summary>
    public string? StyleId { get; init; }
    public Align Align { get; init; }
    public int IndLeft { get; init; }
    public int IndRight { get; init; }
    /// <summary>First-line indent; negative for a hanging indent.</summary>
    public int IndFirst { get; init; }
    public int Before { get; init; }
    public int After { get; init; }
    /// <summary>Line spacing: 240ths of a line for <see cref="LineRule.Auto"/>, otherwise twips.</summary>
    public int Line { get; init; } = 240;
    public LineRule LineRule { get; init; }
    public bool PageBreakBefore { get; init; }
}

/// <summary>Character formatting as specified at one level (style or direct); unset values are null.</summary>
internal sealed class RunPartial
{
    public string? StyleId;
    public string? Font;
    public int? Size;
    public bool? Bold, Italic, Underline, Strike;
    public VertAlign? Vert;
    /// <summary>RRGGBB, or "auto".</summary>
    public string? Color;
    /// <summary>RRGGBB, or "" for an explicit "no highlight".</summary>
    public string? Highlight;

    public RunPartial Clone() => (RunPartial)MemberwiseClone();

    /// <summary>Returns a copy of this with every value set in <paramref name="over"/> applied on top.</summary>
    public RunPartial Overlay(RunPartial? over)
    {
        var r = Clone();
        if (over == null) return r;
        r.StyleId = over.StyleId ?? r.StyleId;
        r.Font = over.Font ?? r.Font;
        r.Size = over.Size ?? r.Size;
        r.Bold = over.Bold ?? r.Bold;
        r.Italic = over.Italic ?? r.Italic;
        r.Underline = over.Underline ?? r.Underline;
        r.Strike = over.Strike ?? r.Strike;
        r.Vert = over.Vert ?? r.Vert;
        r.Color = over.Color ?? r.Color;
        r.Highlight = over.Highlight ?? r.Highlight;
        return r;
    }

    public RunProps ToProps(string? styleId) => new()
    {
        StyleId = styleId,
        Font = string.IsNullOrEmpty(Font) ? "Times New Roman" : Font!,
        Size = Size is > 0 ? Size.Value : 20,
        Bold = Bold ?? false,
        Italic = Italic ?? false,
        Underline = Underline ?? false,
        Strike = Strike ?? false,
        Vert = Vert ?? VertAlign.None,
        Color = Color == null || Color == "auto" ? "000000" : Color,
        Highlight = string.IsNullOrEmpty(Highlight) ? null : Highlight,
    };

    public static RunPartial Parse(XElement? rPr, Theme? theme)
    {
        var r = new RunPartial();
        if (rPr == null) return r;

        r.StyleId = rPr.W("rStyle").WA("val");

        var fonts = rPr.W("rFonts");
        if (fonts != null)
        {
            string? themed = fonts.WA("asciiTheme") ?? fonts.WA("hAnsiTheme");
            string? name = null;
            if (themed != null && theme != null)
                name = themed.StartsWith("major", StringComparison.OrdinalIgnoreCase) ? theme.MajorFont : theme.MinorFont;
            name ??= fonts.WA("ascii") ?? fonts.WA("hAnsi");
            if (name == null && themed == null)
            {
                // Only East Asian / complex script fonts given: not the font we display.
            }
            if (!string.IsNullOrEmpty(name)) r.Font = name;
        }

        r.Bold = X.OnOff(rPr.W("b"));
        r.Italic = X.OnOff(rPr.W("i"));

        var u = rPr.W("u");
        if (u != null)
        {
            var v = u.WA("val");
            r.Underline = v != "none";
        }

        var strike = X.OnOff(rPr.W("strike"));
        var dstrike = X.OnOff(rPr.W("dstrike"));
        if (strike != null || dstrike != null) r.Strike = (strike ?? false) || (dstrike ?? false);

        var vert = rPr.W("vertAlign").WA("val");
        if (vert != null)
            r.Vert = vert switch
            {
                "superscript" => VertAlign.Superscript,
                "subscript" => VertAlign.Subscript,
                _ => VertAlign.None,
            };

        var color = rPr.W("color");
        if (color != null)
        {
            var v = color.WA("val");
            var hex = Colors.NormalizeHex(v);
            if (hex != null) r.Color = hex;
            else if (color.WA("themeColor") is { } tc && theme?.Color(tc) is { } themed) r.Color = themed;
            else if (v == "auto") r.Color = "auto";
        }

        r.Size = rPr.W("sz").WInt("val");

        var hl = rPr.W("highlight").WA("val");
        if (hl != null) r.Highlight = Colors.HighlightToHex(hl) ?? "";
        if (string.IsNullOrEmpty(r.Highlight))
        {
            var shd = rPr.W("shd");
            if (shd != null)
            {
                var fill = Colors.NormalizeHex(shd.WA("fill"));
                if (fill != null) r.Highlight = fill;
                else if (hl == null && shd.WA("val") is "clear" or "nil") r.Highlight = "";
            }
        }
        return r;
    }
}

/// <summary>Paragraph formatting as specified at one level; unset values are null.</summary>
internal sealed class ParaPartial
{
    public string? StyleId;
    public Align? Align;
    public int? IndLeft, IndRight, IndFirst;
    public int? Before, After, Line;
    public LineRule? LineRule;
    public bool? PageBreakBefore;
    public string? NumId;
    public int? Ilvl;
    public int? OutlineLvl;
    public bool? ContextualSpacing;

    public ParaPartial Clone() => (ParaPartial)MemberwiseClone();

    public ParaPartial Overlay(ParaPartial? over)
    {
        var r = Clone();
        if (over == null) return r;
        r.StyleId = over.StyleId ?? r.StyleId;
        r.Align = over.Align ?? r.Align;
        r.IndLeft = over.IndLeft ?? r.IndLeft;
        r.IndRight = over.IndRight ?? r.IndRight;
        r.IndFirst = over.IndFirst ?? r.IndFirst;
        r.Before = over.Before ?? r.Before;
        r.After = over.After ?? r.After;
        r.Line = over.Line ?? r.Line;
        r.LineRule = over.LineRule ?? r.LineRule;
        r.PageBreakBefore = over.PageBreakBefore ?? r.PageBreakBefore;
        r.NumId = over.NumId ?? r.NumId;
        r.Ilvl = over.Ilvl ?? r.Ilvl;
        r.OutlineLvl = over.OutlineLvl ?? r.OutlineLvl;
        r.ContextualSpacing = over.ContextualSpacing ?? r.ContextualSpacing;
        return r;
    }

    public ParaProps ToProps(string? styleId)
    {
        var rule = LineRule ?? Write.Core.LineRule.Auto;
        int line = Line is > 0 ? Line.Value : 240;
        return new ParaProps
        {
            StyleId = styleId,
            Align = Align ?? Write.Core.Align.Left,
            IndLeft = IndLeft ?? 0,
            IndRight = IndRight ?? 0,
            IndFirst = IndFirst ?? 0,
            Before = Math.Max(0, Before ?? 0),
            After = Math.Max(0, After ?? 0),
            Line = line,
            LineRule = rule,
            PageBreakBefore = PageBreakBefore ?? false,
        };
    }

    public static ParaPartial Parse(XElement? pPr)
    {
        var p = new ParaPartial();
        if (pPr == null) return p;

        p.StyleId = pPr.W("pStyle").WA("val");

        var jc = pPr.W("jc").WA("val");
        if (jc != null)
            p.Align = jc switch
            {
                "center" => Write.Core.Align.Center,
                "right" or "end" => Write.Core.Align.Right,
                "both" or "distribute" => Write.Core.Align.Justify,
                _ => Write.Core.Align.Left,
            };

        var ind = pPr.W("ind");
        if (ind != null)
        {
            p.IndLeft = ind.WInt("left") ?? ind.WInt("start");
            p.IndRight = ind.WInt("right") ?? ind.WInt("end");
            var hanging = ind.WInt("hanging");
            var first = ind.WInt("firstLine");
            if (hanging != null) p.IndFirst = -hanging.Value;
            else if (first != null) p.IndFirst = first.Value;
        }

        var sp = pPr.W("spacing");
        if (sp != null)
        {
            p.Before = sp.WInt("before");
            p.After = sp.WInt("after");
            p.Line = sp.WInt("line");
            var rule = sp.WA("lineRule");
            if (rule != null)
                p.LineRule = rule switch
                {
                    "exact" => Write.Core.LineRule.Exact,
                    "atLeast" => Write.Core.LineRule.AtLeast,
                    _ => Write.Core.LineRule.Auto,
                };
            else if (p.Line != null) p.LineRule = Write.Core.LineRule.Auto;
        }

        p.PageBreakBefore = X.OnOff(pPr.W("pageBreakBefore"));

        var numPr = pPr.W("numPr");
        if (numPr != null)
        {
            p.NumId = numPr.W("numId").WA("val");
            p.Ilvl = numPr.W("ilvl").WInt("val");
        }

        p.OutlineLvl = pPr.W("outlineLvl").WInt("val");
        p.ContextualSpacing = X.OnOff(pPr.W("contextualSpacing"));
        return p;
    }
}

public static class Colors
{
    private static readonly Dictionary<string, string> Highlights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yellow"] = "FFFF00", ["green"] = "00FF00", ["cyan"] = "00FFFF", ["magenta"] = "FF00FF",
        ["blue"] = "0000FF", ["red"] = "FF0000", ["darkBlue"] = "000080", ["darkCyan"] = "008080",
        ["darkGreen"] = "008000", ["darkMagenta"] = "800080", ["darkRed"] = "800000", ["darkYellow"] = "808000",
        ["darkGray"] = "808080", ["lightGray"] = "C0C0C0", ["black"] = "000000", ["white"] = "FFFFFF",
    };

    /// <summary>Returns RRGGBB (upper case) when <paramref name="value"/> is a six-digit hex colour.</summary>
    public static string? NormalizeHex(string? value)
    {
        if (value == null) return null;
        value = value.Trim().TrimStart('#');
        if (value.Length != 6) return null;
        foreach (char c in value)
            if (!Uri.IsHexDigit(c)) return null;
        return value.ToUpperInvariant();
    }

    public static string? HighlightToHex(string name) => Highlights.TryGetValue(name, out var hex) ? hex : null;

    /// <summary>The w:highlight name for a colour, when it is one of the sixteen highlight colours.</summary>
    public static string? HexToHighlight(string hex)
    {
        foreach (var kv in Highlights)
            if (kv.Value == hex) return kv.Key;
        return null;
    }
}

/// <summary>The fonts and colours of the document theme that styles can refer to.</summary>
public sealed class Theme
{
    public string? MajorFont { get; private set; }
    public string? MinorFont { get; private set; }
    private readonly Dictionary<string, string> _colors = new(StringComparer.OrdinalIgnoreCase);

    public string? Color(string name)
    {
        name = name switch
        {
            "text1" or "dark1" => "dk1",
            "text2" or "dark2" => "dk2",
            "background1" or "light1" => "lt1",
            "background2" or "light2" => "lt2",
            "hyperlink" => "hlink",
            "followedHyperlink" => "folHlink",
            _ => name,
        };
        return _colors.TryGetValue(name, out var c) ? c : null;
    }

    public static Theme? Parse(XDocument? doc)
    {
        var elements = doc?.Root?.Element(Ns.A + "themeElements");
        if (elements == null) return null;
        var theme = new Theme();
        var fonts = elements.Element(Ns.A + "fontScheme");
        theme.MajorFont = NullIfEmpty((string?)fonts?.Element(Ns.A + "majorFont")?.Element(Ns.A + "latin")?.Attribute("typeface"));
        theme.MinorFont = NullIfEmpty((string?)fonts?.Element(Ns.A + "minorFont")?.Element(Ns.A + "latin")?.Attribute("typeface"));
        var scheme = elements.Element(Ns.A + "clrScheme");
        if (scheme != null)
        {
            foreach (var e in scheme.Elements())
            {
                var srgb = (string?)e.Element(Ns.A + "srgbClr")?.Attribute("val");
                var sys = (string?)e.Element(Ns.A + "sysClr")?.Attribute("lastClr");
                var hex = Colors.NormalizeHex(srgb ?? sys);
                if (hex != null) theme._colors[e.Name.LocalName] = hex;
            }
        }
        return theme;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
