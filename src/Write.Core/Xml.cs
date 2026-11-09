using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Write.Core;

/// <summary>XML namespaces used by WordprocessingML packages.</summary>
public static class Ns
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    public static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    public static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    public static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    public static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    public static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";

    public const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
}

public static class RelTypes
{
    private const string Base = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    public const string OfficeDocument = Base + "officeDocument";
    public const string Styles = Base + "styles";
    public const string Numbering = Base + "numbering";
    public const string Theme = Base + "theme";
    public const string Image = Base + "image";
    public const string Hyperlink = Base + "hyperlink";
    public const string Settings = Base + "settings";
}

internal static class X
{
    /// <summary>First child element in the w: namespace with the given local name.</summary>
    public static XElement? W(this XElement? e, string name) => e?.Element(Ns.W + name);

    public static IEnumerable<XElement> Ws(this XElement? e, string name) =>
        e?.Elements(Ns.W + name) ?? Enumerable.Empty<XElement>();

    /// <summary>Value of a w:-namespaced attribute.</summary>
    public static string? WA(this XElement? e, string name) => (string?)e?.Attribute(Ns.W + name);

    public static int? WInt(this XElement? e, string name) => ParseInt(e.WA(name));

    public static bool IsW(this XElement e, string name) => e.Name.Namespace == Ns.W && e.Name.LocalName == name;

    public static int? ParseInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
            return (int)Math.Round(d);
        return null;
    }

    public static long? ParseLong(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
            return (long)Math.Round(d);
        return null;
    }

    /// <summary>
    /// Reads a WordprocessingML on/off element: null when the element is absent,
    /// true when present without a value, otherwise the parsed value.
    /// </summary>
    public static bool? OnOff(XElement? e)
    {
        if (e == null) return null;
        var v = e.WA("val");
        if (v == null) return true;
        return v is not ("0" or "false" or "off");
    }

    public static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
    public static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

    public static XDocument Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(ms, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static byte[] Serialize(XDocument doc)
    {
        doc.Declaration = new XDeclaration("1.0", "UTF-8", "yes");
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            NamespaceHandling = NamespaceHandling.OmitDuplicates,
        };
        using (var writer = XmlWriter.Create(ms, settings)) doc.Save(writer);
        return ms.ToArray();
    }

    /// <summary>Removes characters that are not allowed in XML 1.0 text.</summary>
    public static string CleanText(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
            if (!ok && char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb?.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }
            if (ok) sb?.Append(c);
            else sb ??= new StringBuilder(s, 0, i, s.Length);
        }
        return sb?.ToString() ?? s;
    }
}

/// <summary>Unit conversions between WordprocessingML units.</summary>
public static class Units
{
    public const int TwipsPerInch = 1440;
    public const long EmuPerInch = 914400;
    public const long EmuPerTwip = 635;
    public const long EmuPerPixel = 9525;

    public static double TwipsToPoints(int twips) => twips / 20.0;
    public static int PointsToTwips(double pt) => (int)Math.Round(pt * 20.0);
    public static double TwipsToCm(int twips) => twips / 1440.0 * 2.54;
    public static int CmToTwips(double cm) => (int)Math.Round(cm / 2.54 * 1440.0);
}
