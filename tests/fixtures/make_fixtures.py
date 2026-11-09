"""Generates the .docx fixtures used by the tests (python-docx, pandoc and LibreOffice)."""
import io, os, struct, subprocess, zlib
from docx import Document
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK
from docx.shared import Pt, RGBColor, Inches, Cm
from docx.oxml import OxmlElement
from docx.oxml.ns import qn

here = os.path.dirname(os.path.abspath(__file__))


def png(width=64, height=40, rgb=(47, 84, 150)):
    raw = b"".join(b"\x00" + bytes(rgb) * width for _ in range(height))
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def add_hyperlink(paragraph, url, text):
    r_id = paragraph.part.relate_to(url, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", is_external=True)
    link = OxmlElement("w:hyperlink")
    link.set(qn("r:id"), r_id)
    run = OxmlElement("w:r")
    rpr = OxmlElement("w:rPr")
    style = OxmlElement("w:rStyle")
    style.set(qn("w:val"), "Hyperlink")
    rpr.append(style)
    run.append(rpr)
    t = OxmlElement("w:t")
    t.text = text
    run.append(t)
    link.append(run)
    paragraph._p.append(link)


def add_field(paragraph, instr, result):
    def run(child):
        r = OxmlElement("w:r")
        r.append(child)
        paragraph._p.append(r)
    def fld(kind):
        f = OxmlElement("w:fldChar")
        f.set(qn("w:fldCharType"), kind)
        return f
    run(fld("begin"))
    it = OxmlElement("w:instrText")
    it.set(qn("xml:space"), "preserve")
    it.text = instr
    run(it)
    run(fld("separate"))
    t = OxmlElement("w:t")
    t.text = result
    run(t)
    run(fld("end"))


def rich():
    d = Document()
    d.core_properties.title = "Fixture"
    d.add_heading("Hoofdstuk één", level=1)
    p = d.add_paragraph("Gewone tekst met ")
    p.add_run("vet").bold = True
    p.add_run(", ")
    p.add_run("cursief").italic = True
    p.add_run(", ")
    p.add_run("onderstreept").underline = True
    p.add_run(" en ")
    r = p.add_run("rood 14pt")
    r.font.color.rgb = RGBColor(0xC0, 0x00, 0x00)
    r.font.size = Pt(14)
    r.font.name = "Arial"
    p.add_run(". H")
    p.add_run("2").font.subscript = True
    p.add_run("O en x")
    p.add_run("2").font.superscript = True
    p.add_run(" ")
    add_hyperlink(p, "https://example.com/pad?a=1&b=2", "een link")
    p.add_run(".")

    d.add_heading("Lijsten", level=2)
    d.add_paragraph("Eerste punt", style="List Bullet")
    d.add_paragraph("Tweede punt", style="List Bullet")
    d.add_paragraph("Genest punt", style="List Bullet 2")
    d.add_paragraph("Stap één", style="List Number")
    d.add_paragraph("Stap twee", style="List Number")

    c = d.add_paragraph("Gecentreerd met afstand")
    c.alignment = WD_ALIGN_PARAGRAPH.CENTER
    c.paragraph_format.space_before = Pt(12)
    c.paragraph_format.space_after = Pt(6)
    c.paragraph_format.line_spacing = 1.5
    i = d.add_paragraph("Ingesprongen alinea met eerste regel.")
    i.paragraph_format.left_indent = Cm(1)
    i.paragraph_format.first_line_indent = Cm(0.5)
    i.paragraph_format.keep_with_next = True

    d.add_heading("Tabel", level=2)
    t = d.add_table(rows=3, cols=3)
    t.style = "Table Grid"
    for r_i in range(3):
        for c_i in range(3):
            t.cell(r_i, c_i).text = f"r{r_i + 1}k{c_i + 1}"
    t.cell(0, 0).merge(t.cell(0, 1))
    t.cell(1, 2).merge(t.cell(2, 2))

    d.add_heading("Afbeelding", level=2)
    d.add_picture(io.BytesIO(png()), width=Inches(1.5))

    pb = d.add_paragraph("Voor het pagina-einde")
    pb.add_run().add_break(WD_BREAK.PAGE)
    pb.add_run("Na het pagina-einde")

    f = d.add_paragraph("Pagina ")
    add_field(f, " PAGE ", "2")
    f.add_run(" van het document. Tab\thier.")

    section = d.sections[0]
    section.header.paragraphs[0].text = "Koptekst van het document"
    section.footer.paragraphs[0].text = "Voettekst"
    d.save(os.path.join(here, "rich.docx"))


def pandoc():
    md = """# Titel uit pandoc

Een alinea met **vet**, *cursief* en een [link](https://pandoc.org).[^1]

[^1]: Dit is een voetnoot.

- punt a
- punt b
    1. genummerd één
    2. genummerd twee
- punt c

> Een citaat.

| Kolom A | Kolom B |
|---------|---------|
| 1       | 2       |
| 3       | 4       |

Formule: $E = mc^2$ in de tekst.
"""
    src = os.path.join(here, "_pandoc.md")
    with open(src, "w", encoding="utf-8") as fh:
        fh.write(md)
    subprocess.run(["pandoc", src, "-o", os.path.join(here, "pandoc.docx")], check=True)
    os.remove(src)


def libreoffice():
    html = """<html><body><h1>LibreOffice-document</h1>
<p>Tekst met <b>vet</b> en <span style="background: yellow">markering</span>.</p>
<ol><li>een</li><li>twee<ul><li>sub</li></ul></li></ol>
<table border="1"><tr><td>a</td><td>b</td></tr><tr><td colspan="2">breed</td></tr></table>
</body></html>"""
    src = os.path.join(here, "libreoffice.html")
    with open(src, "w", encoding="utf-8") as fh:
        fh.write(html)
    subprocess.run(["soffice", "--headless", "--convert-to", "docx:MS Word 2007 XML", "--outdir", here, src],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.remove(src)


def offerte():
    """A realistic business document: the one opened in the demo video."""
    from PIL import Image, ImageDraw
    from docx.enum.table import WD_TABLE_ALIGNMENT
    d = Document()
    for section in d.sections:
        section.page_width, section.page_height = Cm(21), Cm(29.7)
        section.left_margin = section.right_margin = Cm(2.5)
        section.top_margin = section.bottom_margin = Cm(2.5)
    d.styles["Normal"].font.name = "Calibri"
    d.styles["Normal"].font.size = Pt(11)

    header = d.sections[0].header.paragraphs[0]
    header.text = "Bakker Webdesign  ·  Dorpsstraat 12, Utrecht"
    header.alignment = WD_ALIGN_PARAGRAPH.RIGHT
    footer = d.sections[0].footer.paragraphs[0]
    footer.text = "Pagina "
    add_field(footer, " PAGE ", "1")
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER

    img = Image.new("RGB", (900, 220), (15, 118, 110))
    draw = ImageDraw.Draw(img)
    for i in range(900):
        t = i / 899
        draw.line([(i, 0), (i, 220)], fill=(int(15 + 60 * t), int(118 + 90 * t), int(110 + 90 * t)))
    draw.ellipse([640, -60, 900, 200], fill=(140, 225, 210))
    draw.ellipse([60, 110, 300, 350], fill=(40, 150, 140))
    buf = io.BytesIO()
    img.save(buf, "PNG")
    buf.seek(0)

    d.add_heading("Offerte website", level=0)
    p = d.add_paragraph("Offertenummer 2026-118  ·  9 november 2026")
    p.runs[0].font.color.rgb = RGBColor(0x59, 0x59, 0x59)
    d.add_picture(buf, width=Cm(16))

    d.add_heading("Uw aanvraag", level=1)
    p = d.add_paragraph("Hartelijk dank voor uw aanvraag. Hieronder vindt u ons voorstel voor een ")
    p.add_run("nieuwe website").bold = True
    p.add_run(" met webwinkel. De prijzen zijn ")
    p.add_run("exclusief btw").italic = True
    p.add_run(" en dertig dagen geldig. Meer voorbeelden staan op ")
    add_hyperlink(p, "https://example.com/portfolio", "onze portfoliopagina")
    p.add_run(".")

    d.add_heading("Wat u krijgt", level=1)
    for line in ("Ontwerp in uw huisstijl", "Webwinkel met iDEAL-betaling", "Een jaar onderhoud en hosting"):
        d.add_paragraph(line, style="List Bullet")

    d.add_heading("Kosten", level=1)
    rows = [("Onderdeel", "Uren", "Bedrag"), ("Ontwerp", "24", "€ 2.160"), ("Bouw en webwinkel", "60", "€ 5.400"),
            ("Onderhoud (1 jaar)", "", "€ 900"), ("Totaal", "", "€ 8.460")]
    t = d.add_table(rows=len(rows), cols=3)
    t.style = "Table Grid"
    t.alignment = WD_TABLE_ALIGNMENT.LEFT
    for r_i, row in enumerate(rows):
        for c_i, value in enumerate(row):
            cell = t.cell(r_i, c_i)
            cell.text = value
            run = cell.paragraphs[0].runs[0] if cell.paragraphs[0].runs else None
            if c_i > 0:
                cell.paragraphs[0].alignment = WD_ALIGN_PARAGRAPH.RIGHT
            if run is not None and (r_i == 0 or r_i == len(rows) - 1):
                run.bold = True
            if r_i == 0:
                shd = OxmlElement("w:shd")
                shd.set(qn("w:val"), "clear")
                shd.set(qn("w:color"), "auto")
                shd.set(qn("w:fill"), "D9EAD3")
                cell._tc.get_or_add_tcPr().append(shd)

    d.add_heading("Planning", level=1)
    for line in ("Akkoord op de offerte", "Ontwerp ter goedkeuring (week 48)", "Oplevering (week 3)"):
        d.add_paragraph(line, style="List Number")
    d.add_paragraph("Met vriendelijke groet,")
    d.add_paragraph("Sanne Bakker")
    d.save(os.path.join(here, "offerte.docx"))


if __name__ == "__main__":
    rich()
    offerte()
    pandoc()
    libreoffice()
    print("ok")
