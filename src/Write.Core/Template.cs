using System.Text;

namespace Write.Core;

/// <summary>The parts of a new, empty document and the built-in styles.</summary>
public static class Template
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly Lazy<StyleSheet> LazyStyles = new(() =>
        StyleSheet.Parse(X.Parse(Encoding.UTF8.GetBytes(StylesXml)), null));

    /// <summary>The built-in styles, used to add a missing style to an existing document.</summary>
    public static StyleSheet Styles => LazyStyles.Value;

    public static DocxPackage CreatePackage()
    {
        var pkg = new DocxPackage();
        void Add(string path, string xml) => pkg.Set(path, Encoding.UTF8.GetBytes(xml));
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        Add("[Content_Types].xml", Header +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"" + StylesContentType + "\"/>" +
            "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
            "</Types>");

        Add("_rels/.rels", Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
            "</Relationships>");

        Add("word/_rels/document.xml.rels", Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"" + RelTypes.Styles + "\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"" + RelTypes.Settings + "\" Target=\"settings.xml\"/>" +
            "</Relationships>");

        Add("word/document.xml", Header +
            "<w:document " + RootNamespaces + ">" +
            "<w:body><w:p/>" +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
            "<w:pgMar w:top=\"1417\" w:right=\"1417\" w:bottom=\"1417\" w:left=\"1417\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/>" +
            "<w:cols w:space=\"708\"/><w:docGrid w:linePitch=\"360\"/></w:sectPr>" +
            "</w:body></w:document>");

        Add("word/styles.xml", StylesXml);

        Add("word/settings.xml", Header +
            "<w:settings xmlns:w=\"" + W + "\">" +
            "<w:zoom w:percent=\"100\"/><w:defaultTabStop w:val=\"708\"/><w:hyphenationZone w:val=\"425\"/>" +
            "<w:characterSpacingControl w:val=\"doNotCompress\"/>" +
            "<w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat>" +
            "<w:themeFontLang w:val=\"nl-NL\"/><w:decimalSymbol w:val=\",\"/><w:listSeparator w:val=\";\"/>" +
            "</w:settings>");

        Add("docProps/core.xml", Header +
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
            "xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<dcterms:created xsi:type=\"dcterms:W3CDTF\">" + now + "</dcterms:created>" +
            "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + now + "</dcterms:modified>" +
            "</cp:coreProperties>");

        Add("docProps/app.xml", Header +
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
            "<Application>Write</Application></Properties>");

        return pkg;
    }

    public const string StylesContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml";
    public const string NumberingContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml";

    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    internal const string RootNamespaces =
        "xmlns:w=\"" + W + "\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"";

    private static string Heading(int level, int size, string color, int before, bool italic = false, string font = "Calibri Light") =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/>" +
        "<w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/>" +
        (level > 1 ? "<w:unhideWhenUsed/>" : "") + "<w:qFormat/>" +
        $"<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"{before}\" w:after=\"{(level == 1 ? 0 : 0)}\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
        $"<w:rPr><w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:eastAsia=\"{font}\" w:cs=\"{font}\"/>" +
        (italic ? "<w:i/><w:iCs/>" : "") +
        $"<w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";

    /// <summary>word/styles.xml of a new document: the familiar Word defaults.</summary>
    public static readonly string StylesXml = Header +
        "<w:styles xmlns:w=\"" + W + "\">" +
        "<w:docDefaults>" +
        "<w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:eastAsia=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Times New Roman\"/>" +
        "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"nl-NL\" w:eastAsia=\"en-US\" w:bidi=\"ar-SA\"/></w:rPr></w:rPrDefault>" +
        "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"259\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>" +
        "</w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        Heading(1, 32, "2F5496", 240) +
        Heading(2, 26, "2F5496", 40) +
        Heading(3, 24, "1F3763", 40) +
        Heading(4, 22, "2F5496", 40, italic: true) +
        Heading(5, 22, "2F5496", 40) +
        Heading(6, 22, "1F3763", 40) +
        "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/>" +
        "<w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>" +
        "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/>" +
        "<w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>" +
        "<w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/>" +
        "<w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
        "<w:style w:type=\"numbering\" w:default=\"1\" w:styleId=\"NoList\"><w:name w:val=\"No List\"/>" +
        "<w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/>" +
        "<w:uiPriority w:val=\"10\"/><w:qFormat/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/><w:contextualSpacing/></w:pPr>" +
        "<w:rPr><w:rFonts w:ascii=\"Calibri Light\" w:hAnsi=\"Calibri Light\" w:eastAsia=\"Calibri Light\" w:cs=\"Calibri Light\"/>" +
        "<w:spacing w:val=\"-10\"/><w:kern w:val=\"28\"/><w:sz w:val=\"56\"/><w:szCs w:val=\"56\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Subtitle\"><w:name w:val=\"Subtitle\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/>" +
        "<w:uiPriority w:val=\"11\"/><w:qFormat/><w:rPr><w:color w:val=\"5A5A5A\"/><w:spacing w:val=\"15\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/>" +
        "<w:uiPriority w:val=\"29\"/><w:qFormat/><w:pPr><w:spacing w:before=\"200\"/><w:ind w:left=\"864\" w:right=\"864\"/><w:jc w:val=\"center\"/></w:pPr>" +
        "<w:rPr><w:i/><w:iCs/><w:color w:val=\"404040\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"NoSpacing\"><w:name w:val=\"No Spacing\"/><w:uiPriority w:val=\"1\"/><w:qFormat/>" +
        "<w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/>" +
        "<w:uiPriority w:val=\"34\"/><w:qFormat/><w:pPr><w:ind w:left=\"720\"/><w:contextualSpacing/></w:pPr></w:style>" +
        "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/>" +
        "<w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
        "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:basedOn w:val=\"TableNormal\"/>" +
        "<w:uiPriority w:val=\"39\"/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>" +
        "<w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/>" +
        "<w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/>" +
        "<w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/>" +
        "<w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/></w:tblBorders></w:tblPr></w:style>" +
        "</w:styles>";
}
