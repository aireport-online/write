using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;
using static Write.Core.Tests.TestHelpers;

namespace Write.Core.Tests;

/// <summary>Changes made in the editor must end up in the file as clean, valid markup.</summary>
public class EditingTests
{
    private readonly ITestOutputHelper _out;
    public EditingTests(ITestOutputHelper output) => _out = output;

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private byte[] WriteValid(DocModel doc, byte[]? original = null)
    {
        var bytes = DocxWriter.Write(doc);
        var problems = original != null ? NewProblems(original, bytes) : Validate(bytes);
        foreach (var p in problems) _out.WriteLine(p);
        Assert.Empty(problems);
        return bytes;
    }

    private static TextRun NewRun(DocModel doc, string text, string? paraStyle = null, Func<RunProps, RunProps>? change = null)
    {
        var props = doc.Styles.ResolveRun(paraStyle);
        return new TextRun { Text = text, Props = change != null ? change(props) : props };
    }

    [Fact]
    public void NewDocumentWithEverything()
    {
        var doc = DocModel.CreateBlank();
        var h1 = doc.Styles.EnsureBuiltIn("heading 1")!;
        var normal = doc.Styles.ResolvePara(null);
        var blocks = new List<Block>
        {
            new Para { Props = doc.Styles.ResolvePara(h1), Inlines = { NewRun(doc, "Titel", h1) } },
            new Para
            {
                Props = normal with { Align = Align.Justify, IndLeft = 720, IndFirst = -360, Before = 120 },
                Inlines =
                {
                    NewRun(doc, "gewoon "),
                    NewRun(doc, "vet", null, p => p with { Bold = true, Color = "FF0000", Size = 28, Font = "Arial" }),
                    NewRun(doc, " geel", null, p => p with { Highlight = "FFFF00", Underline = true }),
                    NewRun(doc, " oranje", null, p => p with { Highlight = "FFA500", Strike = true, Vert = VertAlign.Superscript }),
                    new LineBreakInline(),
                    NewRun(doc, "tab\thier"),
                },
            },
        };

        var bullets = new object();
        blocks.Add(new Para { Props = normal, List = new ListRef { Level = 0, Format = ListFormat.Disc, Group = bullets }, Inlines = { NewRun(doc, "punt") } });
        blocks.Add(new Para { Props = normal, List = new ListRef { Level = 1, Format = ListFormat.Circle, Group = bullets }, Inlines = { NewRun(doc, "subpunt") } });
        var first = new object();
        blocks.Add(new Para { Props = normal, List = new ListRef { Level = 0, Format = ListFormat.Decimal, Group = first }, Inlines = { NewRun(doc, "een") } });
        blocks.Add(new Para { Props = normal, List = new ListRef { Level = 0, Format = ListFormat.Decimal, Group = first, Continuation = true }, Inlines = { NewRun(doc, "vervolg") } });
        blocks.Add(new Para { Props = normal, Inlines = { NewRun(doc, "tussen") } });
        var second = new object();
        blocks.Add(new Para { Props = normal, List = new ListRef { Level = 0, Format = ListFormat.LowerLetter, Group = second, Ordinal = 3 }, Inlines = { NewRun(doc, "c") } });

        var cellProps = doc.Styles.ResolvePara(null, "TableGrid");
        TableCellModel Cell(string text, int colSpan = 1, int rowSpan = 1) => new()
        {
            ColSpan = colSpan, RowSpan = rowSpan,
            Blocks = { new Para { Props = cellProps, Inlines = { new TextRun { Text = text, Props = doc.Styles.ResolveRun(null, null, "TableGrid") } } } },
        };
        blocks.Add(new TableBlock
        {
            Grid = { 3000, 3000, 3000 },
            Rows =
            {
                new TableRowModel { Cells = { Cell("a", colSpan: 2), Cell("b", rowSpan: 2) } },
                new TableRowModel { Cells = { Cell("c"), Cell("d") } },
            },
        });

        blocks.Add(new Para
        {
            Props = normal, BreakBefore = true, Bookmarks = { "doel" },
            Inlines =
            {
                new ImageInline { Data = Png(), Extension = "png", Cx = 952500, Cy = 952500 },
                new ImageInline { Data = Png(), Extension = "png", Cx = 476250, Cy = 476250 },
                new TextRun { Text = "extern", Props = doc.Styles.ResolveRun(null, doc.Styles.EnsureBuiltIn("Hyperlink", "character")), Link = "https://example.com/?q=1&r=2" },
                new TextRun { Text = "intern", Props = doc.Styles.ResolveRun(null), Link = "#doel" },
            },
        });
        blocks.Add(new Para { Props = normal });

        doc.Blocks = blocks;
        doc.Page = doc.Page with { Width = 16838, Height = 11906, MarginLeft = 1000 };
        var bytes = WriteValid(doc);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "write-test-everything.docx"), bytes);

        var again = DocxReader.Read(bytes);
        _out.WriteLine(Describe(again));
        Assert.True(again.Page.Landscape);
        Assert.Equal(1000, again.Page.MarginLeft);
        var paras = Paragraphs(again.Blocks).ToList();

        Assert.Equal(1, again.Styles.HeadingLevel(paras[0].Props.StyleId));
        var p1 = paras[1];
        Assert.Equal((Align.Justify, 720, -360, 120), (p1.Props.Align, p1.Props.IndLeft, p1.Props.IndFirst, p1.Props.Before));
        var bold = p1.Inlines.OfType<TextRun>().Single(r => r.Text == "vet");
        Assert.Equal((true, "FF0000", 28, "Arial"), (bold.Props.Bold, bold.Props.Color, bold.Props.Size, bold.Props.Font));
        Assert.Equal("FFFF00", p1.Inlines.OfType<TextRun>().Single(r => r.Text == " geel").Props.Highlight);
        var orange = p1.Inlines.OfType<TextRun>().Single(r => r.Text == " oranje");
        Assert.Equal(("FFA500", true, VertAlign.Superscript), (orange.Props.Highlight, orange.Props.Strike, orange.Props.Vert));
        Assert.Contains(p1.Inlines, i => i is LineBreakInline);
        Assert.Contains(p1.Inlines, i => i is TextRun { Text: "tab\thier" });

        var lists = paras.Where(p => p.List != null).ToList();
        Assert.Equal(new[] { ListFormat.Disc, ListFormat.Circle, ListFormat.Decimal, ListFormat.LowerLetter }, lists.Select(p => p.List!.Format));
        Assert.Equal(new[] { 0, 1, 0, 0 }, lists.Select(p => p.List!.Level));
        Assert.Equal(3, lists[3].List!.Ordinal);
        Assert.NotEqual(lists[2].List!.NumId, lists[3].List!.NumId);
        var continuation = paras.Single(p => p.Text == "vervolg");
        Assert.Null(continuation.List);
        Assert.Equal(720, continuation.Props.IndLeft);

        var table = again.Blocks.OfType<TableBlock>().Single();
        Assert.Equal((2, 1), (table.Rows[0].Cells[0].ColSpan, table.Rows[0].Cells[0].RowSpan));
        Assert.Equal(2, table.Rows[0].Cells[1].RowSpan);
        Assert.Equal(2, table.Rows[1].Cells.Count);
        Assert.True(table.Rows[1].Cells[0].Left.Visible);

        var last = paras.First(p => p.BreakBefore);
        Assert.Equal(new[] { "doel" }, last.Bookmarks);
        var images = last.Inlines.OfType<ImageInline>().ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal(952500, images[0].Cx);
        Assert.Equal(images[0].RelId, images[1].RelId); // identical image data is stored once
        Assert.Equal("https://example.com/?q=1&r=2", last.Inlines.OfType<TextRun>().Single(r => r.Text == "extern").Link);
        Assert.Equal("0563C1", last.Inlines.OfType<TextRun>().Single(r => r.Text == "extern").Props.Color);
        Assert.Equal("#doel", last.Inlines.OfType<TextRun>().Single(r => r.Text == "intern").Link);
    }

    [Fact]
    public void ChangingOnePropertyKeepsTheRest()
    {
        var original = Fixture("rich.docx");
        var doc = DocxReader.Read(original);
        var para = Paragraphs(doc.Blocks).First(p => p.Text.StartsWith("Gewone tekst"));
        var red = para.Inlines.OfType<TextRun>().Single(r => r.Text == "rood 14pt");
        red.Props = red.Props with { Bold = true };
        var plain = para.Inlines.OfType<TextRun>().First();
        plain.Props = plain.Props with { Italic = true };
        var boldRun = para.Inlines.OfType<TextRun>().Single(r => r.Text == "vet");
        boldRun.Props = boldRun.Props with { Bold = false };

        var bytes = WriteValid(doc, original);
        var runs = Body(bytes).Descendants(W + "r").ToList();
        XElement Run(string text) => runs.Single(r => (string?)r.Element(W + "t") == text);

        var redPr = Run("rood 14pt").Element(W + "rPr")!;
        Assert.NotNull(redPr.Element(W + "b"));
        Assert.Equal("C00000", (string?)redPr.Element(W + "color")!.Attribute(W + "val"));
        Assert.Equal("28", (string?)redPr.Element(W + "sz")!.Attribute(W + "val"));
        Assert.Equal("Arial", (string?)redPr.Element(W + "rFonts")!.Attribute(W + "ascii"));
        // Children stay in schema order.
        var names = redPr.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(names.IndexOf("rFonts") < names.IndexOf("b") && names.IndexOf("b") < names.IndexOf("color") && names.IndexOf("color") < names.IndexOf("sz"));

        Assert.NotNull(Run("Gewone tekst met ").Element(W + "rPr")!.Element(W + "i"));
        Assert.Null(Run("vet").Element(W + "rPr")?.Element(W + "b"));

        var again = DocxReader.Read(bytes);
        var p2 = Paragraphs(again.Blocks).First(p => p.Text.StartsWith("Gewone tekst"));
        Assert.True(p2.Inlines.OfType<TextRun>().Single(r => r.Text == "rood 14pt").Props.Bold);
        Assert.False(p2.Inlines.OfType<TextRun>().Single(r => r.Text == "vet").Props.Bold);
    }

    [Fact]
    public void ChangingParagraphStyleWritesOnlyTheStyle()
    {
        var original = Fixture("rich.docx");
        var doc = DocxReader.Read(original);
        var para = Paragraphs(doc.Blocks).First(p => p.Text == "Gecentreerd met afstand");
        var h2 = doc.Styles.EnsureBuiltIn("heading 2")!;
        // What the editor does when a style is applied: paragraph and runs take the style's formatting.
        para.Props = doc.Styles.ResolvePara(h2);
        para.Loaded = null;
        para.RawPPr = null;
        foreach (var run in para.Inlines.OfType<TextRun>()) run.Props = doc.Styles.ResolveRun(h2);

        var bytes = WriteValid(doc, original);
        var p = Body(bytes).Elements(W + "p").Single(e => e.Value == "Gecentreerd met afstand");
        var pPr = p.Element(W + "pPr")!;
        Assert.Equal(h2, (string?)pPr.Element(W + "pStyle")!.Attribute(W + "val"));
        Assert.Single(pPr.Elements());
        Assert.Null(p.Element(W + "r")!.Element(W + "rPr"));
    }

    [Fact]
    public void RemovingDirectFormattingFallsBackToStyle()
    {
        var original = Fixture("rich.docx");
        var doc = DocxReader.Read(original);
        var para = Paragraphs(doc.Blocks).First(p => p.Text == "Gecentreerd met afstand");
        var style = doc.Styles.ResolvePara(para.Props.StyleId);
        para.Props = para.Props with { Align = style.Align };

        var bytes = WriteValid(doc, original);
        var pPr = Body(bytes).Elements(W + "p").Single(e => e.Value == "Gecentreerd met afstand").Element(W + "pPr")!;
        Assert.Null(pPr.Element(W + "jc"));
        Assert.NotNull(pPr.Element(W + "spacing"));
    }

    [Fact]
    public void ResizedImageReusesTheStoredPicture()
    {
        var original = Fixture("rich.docx");
        var doc = DocxReader.Read(original);
        var image = Paragraphs(doc.Blocks).SelectMany(p => p.Inlines).OfType<ImageInline>().Single();
        image.Cx /= 2;
        image.Cy /= 2;
        var bytes = WriteValid(doc, original);
        var again = DocxReader.Read(bytes);
        var resized = Paragraphs(again.Blocks).SelectMany(p => p.Inlines).OfType<ImageInline>().Single();
        Assert.Equal(image.Cx, resized.Cx);
        Assert.Equal(image.RelId, resized.RelId);
        Assert.Equal(image.Data, resized.Data);
    }

    [Fact]
    public void ListsCreatedInAnExistingDocumentGetTheirOwnNumbering()
    {
        var original = Fixture("rich.docx");
        var doc = DocxReader.Read(original);
        var group = new object();
        var normal = doc.Styles.ResolvePara(null);
        doc.Blocks.Add(new Para { Props = normal, List = new ListRef { Format = ListFormat.UpperRoman, Group = group }, Inlines = { NewRun(doc, "nieuw I") } });
        doc.Blocks.Add(new Para { Props = normal, List = new ListRef { Format = ListFormat.UpperRoman, Group = group }, Inlines = { NewRun(doc, "nieuw II") } });

        var bytes = WriteValid(doc, original);
        var again = DocxReader.Read(bytes);
        var paras = Paragraphs(again.Blocks).ToList();
        var added = paras.Where(p => p.Text.StartsWith("nieuw")).ToList();
        Assert.All(added, p => Assert.Equal(ListFormat.UpperRoman, p.List!.Format));
        Assert.Equal(new[] { 1, 2 }, added.Select(p => p.List!.Ordinal));
        // The lists that were already there are untouched.
        Assert.Equal(3, paras.Count(p => p.List != null && p.List.Format.IsBullet()));
        Assert.Equal(2, paras.Count(p => p.List is { Format: ListFormat.Decimal }));
    }

    [Fact]
    public void DeletedContentIsGone()
    {
        var original = Fixture("pandoc.docx");
        var doc = DocxReader.Read(original);
        doc.Blocks.RemoveAll(b => b is TableBlock);
        foreach (var p in Paragraphs(doc.Blocks)) p.Inlines.RemoveAll(i => i is RawInline { Kind: RawKind.Footnote });
        var bytes = WriteValid(doc, original);
        Assert.Empty(Body(bytes).Descendants(W + "tbl"));
        Assert.Empty(Body(bytes).Descendants(W + "footnoteReference"));
    }

    [Fact]
    public void TextWithSpecialCharactersIsEscaped()
    {
        var doc = DocModel.CreateBlank();
        const string text = "  <a & b> \"quotes\" 'apos' \u0001 emoji 😀 é  ";
        doc.Blocks = new List<Block> { new Para { Props = doc.Styles.ResolvePara(null), Inlines = { NewRun(doc, text) } } };
        var again = DocxReader.Read(WriteValid(doc));
        Assert.Equal(text.Replace("\u0001", ""), Paragraphs(again.Blocks).Single().Text);
    }

    [Theory]
    [InlineData("HYPERLINK \"https://a.nl/x y\"", "https://a.nl/x y")]
    [InlineData(" HYPERLINK \\l \"_Toc123\" ", "#_Toc123")]
    [InlineData("HYPERLINK \"https://a.nl\" \\l \"deel\" \\o \"tip\"", "https://a.nl#deel")]
    [InlineData("PAGE", null)]
    public void HyperlinkFieldInstructions(string instruction, string? expected)
    {
        Assert.Equal(expected, DocxReader_ParseHyperlink(instruction));
    }

    private static string? DocxReader_ParseHyperlink(string instruction) =>
        (string?)typeof(DocxReader).GetNestedType("Context", System.Reflection.BindingFlags.NonPublic)!
            .GetMethod("ParseHyperlinkInstruction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { instruction });

    [Fact]
    public void NotADocxGivesAClearError()
    {
        Assert.Throws<DocxFormatException>(() => DocxReader.Read(new byte[] { 1, 2, 3, 4 }));
    }
}
