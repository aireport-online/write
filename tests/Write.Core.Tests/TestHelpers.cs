using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace Write.Core.Tests;

internal static class TestHelpers
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    /// <summary>Schema validation as Word would apply it; returns one line per problem.</summary>
    public static List<string> Validate(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Microsoft365);
        return validator.Validate(doc)
            .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
            .ToList();
    }

    /// <summary>Problems in <paramref name="after"/> that the original file did not already have.</summary>
    public static List<string> NewProblems(byte[] before, byte[] after)
    {
        string Key(string s) => s[(s.IndexOf(": ", StringComparison.Ordinal) + 2)..];
        var known = Validate(before).Select(Key).ToHashSet();
        return Validate(after).Where(e => !known.Contains(Key(e))).ToList();
    }

    public static string Part(byte[] docx, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var entry = zip.GetEntry(path) ?? throw new InvalidOperationException("Part not found: " + path);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static bool HasPart(byte[] docx, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        return zip.GetEntry(path) != null;
    }

    public static XElement Body(byte[] docx) =>
        XDocument.Parse(Part(docx, "word/document.xml"), LoadOptions.PreserveWhitespace).Root!.Element(W + "body")!;

    public static IEnumerable<Para> Paragraphs(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is Para p) yield return p;
            else if (b is TableBlock t)
                foreach (var row in t.Rows)
                foreach (var cell in row.Cells)
                foreach (var inner in Paragraphs(cell.Blocks))
                    yield return inner;
        }
    }

    /// <summary>A compact, comparable description of the document content and formatting.</summary>
    public static string Describe(DocModel doc)
    {
        var sb = new StringBuilder();
        void Blocks(IEnumerable<Block> blocks, string indent)
        {
            foreach (var b in blocks)
            {
                if (b is Para p)
                {
                    sb.Append(indent).Append("P ").Append(p.Props);
                    if (p.List != null) sb.Append($" list[{p.List.Level},{p.List.Format},{p.List.Ordinal}]");
                    if (p.BreakBefore) sb.Append(" break");
                    sb.AppendLine();
                    foreach (var i in p.Inlines)
                    {
                        sb.Append(indent).Append("  ");
                        switch (i)
                        {
                            case TextRun r: sb.Append("T '").Append(r.Text).Append("' ").Append(r.Props); break;
                            case LineBreakInline: sb.Append("BR"); break;
                            case ImageInline img: sb.Append($"IMG {img.Extension} {img.Cx}x{img.Cy} {img.Data.Length}b"); break;
                            case RawInline raw: sb.Append($"RAW {raw.Kind} '{raw.Text}'"); break;
                        }
                        if (i.Link != null) sb.Append(" -> ").Append(i.Link);
                        sb.AppendLine();
                    }
                }
                else if (b is TableBlock t)
                {
                    sb.Append(indent).AppendLine("TABLE " + string.Join(",", t.Grid));
                    foreach (var row in t.Rows)
                    {
                        sb.Append(indent).AppendLine(" ROW");
                        foreach (var cell in row.Cells)
                        {
                            sb.Append(indent).AppendLine($"  CELL {cell.ColSpan}x{cell.RowSpan} {cell.Shade} {cell.Top.Visible}{cell.Left.Visible}{cell.Bottom.Visible}{cell.Right.Visible}");
                            Blocks(cell.Blocks, indent + "    ");
                        }
                    }
                }
            }
        }
        Blocks(doc.Blocks, "");
        return sb.ToString();
    }
}
