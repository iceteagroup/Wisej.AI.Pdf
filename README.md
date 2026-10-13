# Wisej.AI.Pdf

**Fast, dependency-free PDF content extraction for .NET.**

Wisej.AI.Pdf reads a PDF and returns its *content*, not just its characters:
- text in reading order, with formatting;
- headings, lists, captions and code;
- tables with merged cells;
- pictures and charts as high-fidelity images.

Output can be Markdown, HTML, JSON or plain text, ready for search indexing, RAG pipelines and large language models.

It ships with [Wisej.AI](https://wisej.com) and also works standalone.

```csharp
using Wisej.AI.Pdf;

var content = PdfExtractor.Extract("report.pdf");

File.WriteAllText("report.md", content.ToMarkdown());
foreach (var image in content.Images)
    image.Save(Path.Combine("images", image.FileName));
```

## Highlights

**Zero dependencies.** One managed assembly for .NET Standard 2.0 and .NET 8, so it runs on .NET Framework 4.8, .NET 8+, Windows, Linux, macOS, Android and iOS. No native code, no System.Drawing.

**Text in the right order.** Multi-column pages, titles and figures spanning columns, sidebars and rotated text are ordered with a topological reading-order algorithm (Breuel's precedence rules). Paragraphs are rebuilt, end-of-line hyphenation is removed using the document's own vocabulary, and paragraphs split by column or page breaks are joined.

**Structure.** Headings come from tagged PDFs, the outline, or typography (size, weight, numbering), with levels. Bulleted and numbered lists keep their nesting. The tool also recognizes:
- captions, attached to their figure or table;
- code blocks, with indentation;
- footnotes and superscripts;
- links;
- running headers, footers and page numbers, which are removed from the text flow.

**Tables.**
- *Ruled tables* come from the drawn lattice, with row and column spans. Zebra-striped rows and partially ruled grids are handled.
- *Booktabs tables* (horizontal rules only) are detected too.
- *Borderless tables* are found from text alignment, with multi-level headers.
- Grouped rows, wrapped cells, and tables continued on the next page are handled.

**Images, done right.** Every image is decoded following the PDF imaging model:
- all filters and codecs: JPEG (baseline, progressive, arithmetic, 12-bit, CMYK/YCCK), JPEG 2000, JBIG2 and CCITT fax;
- all colour spaces: device, ICC-based, Lab, Indexed, Separation and DeviceN with PostScript tint transforms;
- 1, 2, 4, 8 and 16 bits per component, with `/Decode` arrays;
- soft masks with `/Matte`, explicit and colour-key masks, and stencil masks painted with their colour;
- images inside forms, patterns and annotations;
- the orientation on the page (rotation and mirroring).

Tiles are reassembled into one picture, and repeated images are de-duplicated. JPEG images are returned without recompression whenever possible.

**Vector figures.** Charts and diagrams drawn with vector graphics are detected and rendered to images. Their labels are kept as text.

**Robust and fast.** Damaged files are repaired transparently. The extractor handles every encryption revision, from RC4 40-bit to AES-256 R6. Pages are processed in parallel. The full pdf.js test suite (990 files) extracts in about 25 seconds, figures rendered included, with a single unrecoverable file, a fuzzing test with no catalog.

## Usage

### One call

```csharp
string markdown = PdfExtractor.ToMarkdown(stream);
string html     = PdfExtractor.ToHtml(stream);            // images embedded as data URIs
string text     = PdfExtractor.ToText(stream);
string json     = PdfExtractor.ToJson(stream);
IReadOnlyList<PdfTable> tables = PdfExtractor.ExtractTables(stream);
IReadOnlyList<PdfImage> images = PdfExtractor.ExtractImages(stream);
```

### Full control

```csharp
using (var document = PdfDocument.Open(stream, password: null))
{
    Console.WriteLine($"{document.Info.Title} - {document.PageCount} pages, tagged: {document.IsTagged}");

    var content = document.Extract(new PdfExtractionOptions
    {
        Pages = "1-10, 15",
        DetectBorderlessTables = true,
        Images = new PdfImageOptions { MinimumSize = 32, RenderResolution = 200, Format = PdfImageFormat.Auto },
    });

    foreach (var block in content.Blocks)
    {
        switch (block)
        {
            case PdfTextBlock text when text.Type == PdfBlockType.Heading:
                Console.WriteLine($"H{text.Level}: {text.Text}");
                break;

            case PdfTable table:
                Console.WriteLine(table.ToCsv());
                break;

            case PdfFigure figure:
                figure.Image?.Save($"figure-p{figure.PageNumber}");
                break;
        }
    }

    // Markdown for an LLM, with page markers for citations and images inline.
    var markdown = content.ToMarkdown(new PdfMarkdownOptions
    {
        IncludePageMarkers = true,
        ImageMode = PdfMarkdownImageMode.Embed,
    });
}
```

### The content model

| Type | Content |
|---|---|
| `PdfContent` | `Pages`, `Blocks` (reading order), `Tables`, `Images`, `Info`; `ToMarkdown()`, `ToHtml()`, `ToText()`, `ToJson()` |
| `PdfTextBlock` | paragraphs, headings (`Level`), captions, code, page headers and footers, made of formatted `Runs` (bold, italic, monospace, superscript, subscript, link, font, size, colour) |
| `PdfList` | `Items` with `Marker`, `Depth` and formatted runs |
| `PdfTable` | `Cells` with `RowSpan`/`ColumnSpan`/`IsHeader`, an indexer, `HeaderRowCount`, `Caption`; `ToArray()`, `ToCsv()`, `ToMarkdown()`, `ToHtml()` |
| `PdfFigure` | `Image`, `Caption`, `AltText`, `Labels` (text inside charts), `IsVector` |
| `PdfImage` | `Data` (PNG or JPEG), `Width`, `Height`, `Dpi`, `Bounds`, `PageNumbers`, `Kind`, `AltText`, `Caption`; `Save()`, `ToDataUri()` |

All bounds are in points from the top-left corner of the page, as displayed (page rotation applied).

## Architecture

```
IO/           lexer, parser, cross-reference (tables, streams, hybrid), repair, object streams
Security/     standard security handler: RC4, AES-128, AES-256 (R2-R6, SASLprep)
Filters/      Flate, LZW, ASCII85, ASCIIHex, RunLength, PNG/TIFF predictors
Imaging/      image decoding pipeline, PNG and JPEG encoders, figure images
  Codecs/     JPEG, JPEG 2000, JBIG2, CCITT fax
Fonts/        simple, Type0, Type3 fonts; encodings; ToUnicode; Adobe glyph list
  CMaps/      embedded and all predefined CMaps, CID to Unicode for CJK collections
  Programs/   TrueType, OpenType, CFF and Type1 parsers with glyph outlines
  Standard14/ metrics of the standard 14 fonts
Graphics/     colour spaces and PDF functions (including the PostScript calculator)
Content/      content stream interpreter: glyphs, paths, images, marked content, layers, annotations
Rendering/    managed rasterizer used for vector figures
Layout/       lines and words, reading order, paragraphs, lists, tables, figures, headings
Extraction/   the parallel extraction pipeline
Model/        the public content model
Output/       Markdown, HTML, JSON and text writers
```

## Building and testing

```
dotnet build Wisej.AI.Pdf.sln
dotnet test tests/Wisej.AI.Pdf.Tests
```

The coding conventions are in [docs/CONVENTIONS.md](docs/CONVENTIONS.md).

## License

Copyright © Ice Tea Group LLC. Third-party data and notices are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
