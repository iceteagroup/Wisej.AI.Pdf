///////////////////////////////////////////////////////////////////////////////
//
// (C) 2026 ICE TEA GROUP LLC - ALL RIGHTS RESERVED
//
// 
//
// ALL INFORMATION CONTAINED HEREIN IS, AND REMAINS
// THE PROPERTY OF ICE TEA GROUP LLC AND ITS SUPPLIERS, IF ANY.
// THE INTELLECTUAL PROPERTY AND TECHNICAL CONCEPTS CONTAINED
// HEREIN ARE PROPRIETARY TO ICE TEA GROUP LLC AND ITS SUPPLIERS
// AND MAY BE COVERED BY U.S. AND FOREIGN PATENTS, PATENT IN PROCESS, AND
// ARE PROTECTED BY TRADE SECRET OR COPYRIGHT LAW.
//
// DISSEMINATION OF THIS INFORMATION OR REPRODUCTION OF THIS MATERIAL
// IS STRICTLY FORBIDDEN UNLESS PRIOR WRITTEN PERMISSION IS OBTAINED
// FROM ICE TEA GROUP LLC.
//
///////////////////////////////////////////////////////////////////////////////

// Orchestrates the extraction: page interpretation and layout analysis in
// parallel, document-level analysis, image extraction and the public model.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wisej.AI.Pdf.Content;
using Wisej.AI.Pdf.Imaging;
using Wisej.AI.Pdf.Layout;

namespace Wisej.AI.Pdf.Extraction
{
	/// <summary>
	/// Runs the extraction pipeline for a document.
	/// </summary>
	internal sealed class ExtractionEngine
	{
		#region Nested types

		/// <summary>
		/// The intermediate results of a page.
		/// </summary>
		private sealed class PageResult
		{
			/// <summary>The page.</summary>
			public PdfPage Page;

			/// <summary>The painted objects.</summary>
			public PageObjects Objects;

			/// <summary>The blocks in reading order.</summary>
			public List<LayoutBlock> Blocks;

			/// <summary>True when the page has no text.</summary>
			public bool IsImageOnly;
		}

		#endregion

		// the document to extract.
		private readonly PdfDocument _document;
		// the extraction options.
		private readonly PdfExtractionOptions _options;

		// the words of the document, used to resolve end-of-line hyphenation.
		private HashSet<string> _vocabulary;

		/// <summary>
		/// Initializes a new instance of <see cref="ExtractionEngine"/>.
		/// </summary>
		/// <param name="document">The document.</param>
		/// <param name="options">The options.</param>
		public ExtractionEngine(PdfDocument document, PdfExtractionOptions options)
		{
			_document = document;
			_options = options ?? new PdfExtractionOptions();
		}

		#region Methods

		/// <summary>
		/// Extracts the content.
		/// </summary>
		/// <returns>The extracted content.</returns>
		public PdfContent Run()
		{
			var numbers = _options.GetPageNumbers(_document.PageCount);
			var results = new PageResult[numbers.Count];
			var completed = 0;
			var token = _options.CancellationToken;

			// pages are independent: interpret and analyze them in parallel.
			var parallel = new ParallelOptions
			{
				MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism),
				CancellationToken = token,
			};

			Parallel.For(0, numbers.Count, parallel, i =>
			{
				results[i] = AnalyzePage(_document.Pages[numbers[i] - 1]);
				_options.Progress?.Invoke(Interlocked.Increment(ref completed), numbers.Count);
			});

			token.ThrowIfCancellationRequested();

			// document-level analysis.
			var pages = new List<(PdfPage, List<LayoutBlock>)>();
			foreach (var result in results)
				pages.Add((result.Page, result.Blocks));

			var settings = new DocumentAnalyzerSettings
			{
				DetectHeadersAndFooters = _options.DetectHeadersAndFooters,
				DetectHeadings = _options.DetectHeadings,
				JoinParagraphs = _options.JoinParagraphs,
				JoinTables = _options.JoinTablesAcrossPages,
			};

			var structure = StructureTree.Get(_document);
			var refined = new DocumentAnalyzer(settings, _document.Outlines, structure).Analyze(pages);
			if (structure != null)
				ApplyAltText(results, structure);
			for (int i = 0; i < results.Length; i++)
				results[i].Blocks = refined[i];

			_vocabulary = BuildVocabulary(results);

			// images, then the public model.
			var images = _options.ExtractImages ? ExtractImages(results) : new Dictionary<LayoutBlock, PdfImage>();
			return BuildContent(results, images);
		}

		#endregion

		#region Pages

		/// <summary>
		/// Interprets a page and analyzes its layout. Runs in parallel with the other pages.
		/// </summary>
		/// <param name="page">The page.</param>
		/// <returns>The intermediate results of the page.</returns>
		private PageResult AnalyzePage(PdfPage page)
		{
			var interpreter = new ContentInterpreter(page, new InterpreterOptions
			{
				Annotations = _options.IncludeAnnotations,
				CancellationToken = _options.CancellationToken,
			});

			var objects = interpreter.Run();
			var lines = new LineBuilder(page.Width, page.Height).Build(objects.Glyphs, objects.Links);

			var analyzer = new PageAnalyzer(page, new AnalyzerSettings
			{
				DetectTables = _options.DetectTables,
				DetectBorderlessTables = _options.DetectBorderlessTables,
				DetectFigures = _options.ExtractImages || _options.DetectFigures,
				MinFigureSize = _options.Images?.MinimumSize ?? 24,
			});

			var blocks = analyzer.Analyze(objects, lines);
			var hasText = lines.Exists(l => l.CharCount > 0);

			// scanned pages without text: the page image is the content.
			if (!hasText && _options.ExtractImages && (_options.Images?.IncludePageScans ?? true))
				AddPageScan(page, objects, blocks);

			return new PageResult { Page = page, Objects = objects, Blocks = blocks, IsImageOnly = !hasText };
		}

		// adds the images that cover the page (and were skipped as figures) as a page scan figure.
		private static void AddPageScan(PdfPage page, PageObjects objects, List<LayoutBlock> blocks)
		{
			if (blocks.Exists(b => b.Kind == LayoutBlockKind.Figure))
				return;

			var pageArea = page.Width * page.Height;
			var figure = new LayoutBlock { Kind = LayoutBlockKind.Figure, PageNumber = page.Number };
			foreach (var image in objects.Images)
			{
				if (!image.IsClipped && image.VisibleBounds.Area > pageArea * 0.3)
				{
					figure.Images.Add(image);
					figure.Bounds = figure.Images.Count == 1 ? image.VisibleBounds : figure.Bounds.Union(image.VisibleBounds);
				}
			}

			if (figure.Images.Count > 0)
			{
				figure.DisplayBounds = figure.Bounds;
				blocks.Add(figure);
			}
		}

		// alternate text of tagged figures.
		private static void ApplyAltText(PageResult[] results, StructureTree structure)
		{
			foreach (var result in results)
			{
				foreach (var block in result.Blocks)
				{
					if (block.Kind != LayoutBlockKind.Figure || block.AltText != null)
						continue;

					foreach (var image in block.Images)
					{
						var text = structure.GetFigureAltText(image.StructParent, image.Mcid);
						if (text != null)
						{
							block.AltText = text;
							break;
						}
					}
				}
			}
		}

		#endregion

		#region Images

		/// <summary>
		/// Extracts the images of all the figures, in parallel, and de-duplicates them.
		/// </summary>
		/// <param name="results">The analyzed pages.</param>
		/// <returns>The image of each figure block.</returns>
		private Dictionary<LayoutBlock, PdfImage> ExtractImages(PageResult[] results)
		{
			var map = new Dictionary<LayoutBlock, PdfImage>();
			var extractor = new FigureImageExtractor(_document, _options.Images ?? new PdfImageOptions());

			// figures in reading order; the work is done in parallel and the ids assigned in order.
			var figures = new List<(PageResult Page, LayoutBlock Block)>();
			foreach (var result in results)
			{
				foreach (var block in result.Blocks)
				{
					if (block.Kind == LayoutBlockKind.Figure)
						figures.Add((result, block));
				}
			}

			var images = new ExtractedImage[figures.Count];
			var parallel = new ParallelOptions
			{
				MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism),
				CancellationToken = _options.CancellationToken,
			};

			Parallel.For(0, figures.Count, parallel, i =>
			{
				var (page, block) = figures[i];
				try
				{
					images[i] = extractor.Extract(page.Page, page.Objects, block, page.IsImageOnly);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception)
				{
					// an image that can't be decoded doesn't stop the extraction.
					images[i] = null;
				}
			});

			// de-duplicate by content and assign stable ids in order of appearance.
			var byHash = new Dictionary<string, ExtractedImage>();
			var counter = 0;
			for (int i = 0; i < figures.Count; i++)
			{
				var image = images[i];
				if (image == null)
					continue;

				var block = figures[i].Block;
				if (_options.Images?.Deduplicate != false && byHash.TryGetValue(image.Hash, out var existing))
				{
					((List<int>)existing.Image.PageNumbers).Add(block.PageNumber);
					map[block] = existing.Image;
					continue;
				}

				image.Image.Id = "image-" + (++counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
				image.Image.PageNumbers = new List<int> { block.PageNumber };
				byHash[image.Hash] = image;
				map[block] = image.Image;
			}

			return map;
		}

		#endregion

		#region Public model

		/// <summary>
		/// Converts the layout blocks to the public content model.
		/// </summary>
		/// <param name="results">The analyzed pages.</param>
		/// <param name="images">The image of each figure block.</param>
		/// <returns>The content.</returns>
		private PdfContent BuildContent(PageResult[] results, Dictionary<LayoutBlock, PdfImage> images)
		{
			var pages = new List<PdfContentPage>();
			var allBlocks = new List<PdfBlock>();
			var tables = new List<PdfTable>();
			var allImages = new List<PdfImage>();
			var seenImages = new HashSet<PdfImage>();

			foreach (var result in results)
			{
				var page = result.Page;
				var blocks = new List<PdfBlock>();
				var layout = result.Blocks;

				for (int i = 0; i < layout.Count; i++)
				{
					var block = layout[i];

					// captions attached to a figure or table are emitted with it.
					if (block.Kind == LayoutBlockKind.Caption && IsAttachedCaption(layout, block))
						continue;

					switch (block.Kind)
					{
						case LayoutBlockKind.ListItem:
							{
								// consecutive items form a list.
								var items = new List<LayoutBlock> { block };
								while (i + 1 < layout.Count && layout[i + 1].Kind == LayoutBlockKind.ListItem)
									items.Add(layout[++i]);

								blocks.Add(BuildList(page, items));
							}
							break;

						case LayoutBlockKind.Table:
							{
								var table = BuildTable(page, block);
								blocks.Add(table);
								tables.Add(table);
							}
							break;

						case LayoutBlockKind.Figure:
							{
								images.TryGetValue(block, out var image);
								if (image != null)
								{
									image.Caption = image.Caption ?? block.Caption?.GetText();
									image.AltText = image.AltText ?? block.AltText;
									if (seenImages.Add(image))
										allImages.Add(image);
								}

								// figures without an image are kept only when they carry information.
								if (image != null || block.Caption != null || block.AltText != null || block.FigureText.Count > 0)
								{
									blocks.Add(new PdfFigure(page.Number, block.DisplayBounds, image, block.Caption?.GetText(), block.AltText,
										block.FigureText.Count > 0 ? JoinLabels(block.FigureText) : null, block.IsVectorFigure));
								}
							}
							break;

						default:
							blocks.Add(BuildText(page, block));
							break;
					}
				}

				pages.Add(new PdfContentPage
				{
					Number = page.Number,
					Label = page.Label,
					Width = page.Width,
					Height = page.Height,
					Blocks = blocks,
					IsImageOnly = result.IsImageOnly,
				});

				allBlocks.AddRange(blocks);
			}

			return new PdfContent
			{
				Info = _document.Info,
				PageCount = _document.PageCount,
				Pages = pages,
				Blocks = allBlocks,
				Tables = tables,
				Images = allImages,
			};
		}

		// collects the words of all the text, in lower case without punctuation.
		private static HashSet<string> BuildVocabulary(PageResult[] results)
		{
			var vocabulary = new HashSet<string>(StringComparer.Ordinal);

			void Add(List<TextLine> lines)
			{
				foreach (var line in lines)
				{
					// the last word of a line may be a hyphenated fragment: it doesn't count.
					for (int i = 0; i < line.Words.Count - 1; i++)
						vocabulary.Add(TextAssembler.NormalizeWord(line.Words[i].Text));

					var last = line.Words.Count > 0 ? line.Words[line.Words.Count - 1].Text : string.Empty;
					if (!last.EndsWith("-", StringComparison.Ordinal))
						vocabulary.Add(TextAssembler.NormalizeWord(last));
				}
			}

			foreach (var result in results)
			{
				foreach (var block in result.Blocks)
				{
					Add(block.Lines);
					if (block.Table != null)
					{
						foreach (var cell in block.Table.Cells)
							Add(cell.Lines);
					}
				}
			}

			return vocabulary;
		}

		/// <summary>
		/// Returns true when <paramref name="caption"/> is the caption of a figure or table of the page.
		/// </summary>
		/// <param name="blocks">The blocks of the page.</param>
		/// <param name="caption">The caption block.</param>
		/// <returns>True when the caption is attached.</returns>
		private static bool IsAttachedCaption(
List<LayoutBlock> blocks, LayoutBlock caption)
		{
			foreach (var block in blocks)
			{
				if (block.Caption == caption)
					return true;
			}

			return false;
		}

		/// <summary>
		/// Creates a text block: paragraph, heading, caption, code, header or footer.
		/// </summary>
		/// <param name="page">The page.</param>
		/// <param name="block">The layout block.</param>
		/// <returns>The text block.</returns>
		private PdfTextBlock BuildText(PdfPage page, LayoutBlock block)
		{
			PdfBlockType type;
			switch (block.Kind)
			{
				case LayoutBlockKind.Heading: type = PdfBlockType.Heading; break;
				case LayoutBlockKind.Caption: type = PdfBlockType.Caption; break;
				case LayoutBlockKind.Code: type = PdfBlockType.Code; break;
				case LayoutBlockKind.PageHeader: type = PdfBlockType.PageHeader; break;
				case LayoutBlockKind.PageFooter: type = PdfBlockType.PageFooter; break;
				default: type = PdfBlockType.Paragraph; break;
			}

			var runs = ToRuns(TextAssembler.GetRuns(block.Lines, block.Kind == LayoutBlockKind.Code, _vocabulary));
			return new PdfTextBlock(type, page.Number, block.DisplayBounds, runs, block.Kind == LayoutBlockKind.Heading ? block.Level : 0);
		}

		/// <summary>
		/// Creates a list from consecutive list items, removing the markers from the text.
		/// </summary>
		/// <param name="page">The page.</param>
		/// <param name="items">The list item blocks.</param>
		/// <returns>The list.</returns>
		private PdfList BuildList(PdfPage page, List<LayoutBlock> items)
		{
			var listItems = new List<PdfListItem>();
			var bounds = items[0].DisplayBounds;
			var ordered = items[0].IsOrderedList;

			foreach (var item in items)
			{
				bounds = bounds.Union(item.DisplayBounds);

				// the marker is the first word of the first line: remove it from the text.
				var lines = new List<TextLine>(item.Lines);
				if (lines.Count > 0 && lines[0].Words.Count > 0)
				{
					var first = lines[0];
					var copy = new TextLine
					{
						Bounds = first.Bounds,
						Baseline = first.Baseline,
						FontSize = first.FontSize,
						Orientation = first.Orientation,
						IsBold = first.IsBold,
						IsItalic = first.IsItalic,
						IsMonospace = first.IsMonospace,
					};

					for (int w = 1; w < first.Words.Count; w++)
						copy.Words.Add(first.Words[w]);

					lines[0] = copy;
				}

				listItems.Add(new PdfListItem(item.ListMarker, item.ListDepth, ToRuns(TextAssembler.GetRuns(lines, false, _vocabulary))));
			}

			// depths: the rank of each distinct indentation (within one em), so stray offsets don't create deep nesting.
			var lefts = new List<double>();
			foreach (var item in items)
			{
				var left = item.Bounds.Left;
				if (!lefts.Exists(l => Math.Abs(l - left) < Math.Max(4, item.FontSize)))
					lefts.Add(left);
			}

			lefts.Sort();
			for (int i = 0; i < listItems.Count; i++)
			{
				var left = items[i].Bounds.Left;
				var depth = lefts.FindIndex(l => Math.Abs(l - left) < Math.Max(4, items[i].FontSize));
				listItems[i] = new PdfListItem(listItems[i].Marker, Math.Min(4, Math.Max(0, depth)), listItems[i].Runs);
			}

			return new PdfList(page.Number, bounds, ordered, listItems);
		}

		/// <summary>
		/// Creates a table, converting the cell bounds to display space.
		/// </summary>
		/// <param name="page">The page.</param>
		/// <param name="block">The table block.</param>
		/// <returns>The table.</returns>
		private PdfTable BuildTable(PdfPage page, LayoutBlock block)
		{
			var model = block.Table;
			var cells = new List<PdfTableCell>();
			foreach (var cell in model.Cells)
			{
				var bounds = ReadingFrame.ToDisplay(block.Orientation, cell.Bounds, page.Width, page.Height);
				var runs = ToRuns(TextAssembler.GetRuns(cell.Lines, false, _vocabulary));
				cells.Add(new PdfTableCell(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan, cell.IsHeader, bounds, runs));
			}

			cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));

			var pageNumbers = new List<int> { page.Number };
			pageNumbers.AddRange(model.ContinuedOn);
			return new PdfTable(page.Number, block.DisplayBounds, model.RowCount, model.ColumnCount, model.HeaderRowCount, model.IsRuled,
				cells, block.Caption?.GetText(), pageNumbers);
		}

		/// <summary>
		/// Converts styled runs to public text runs.
		/// </summary>
		/// <param name="runs">The styled runs.</param>
		/// <returns>The text runs.</returns>
		private static List<PdfTextRun> ToRuns(List<StyledRun> runs)
		{
			var result = new List<PdfTextRun>(runs.Count);
			foreach (var run in runs)
			{
				result.Add(new PdfTextRun
				{
					Text = run.Text,
					IsBold = run.Bold,
					IsItalic = run.Italic,
					IsMonospace = run.Monospace,
					IsSuperscript = run.Superscript,
					IsSubscript = run.Subscript,
					LinkUri = run.LinkUri,
					LinkPage = run.LinkPage,
					FontName = run.FontName,
					FontSize = run.FontSize,
					Color = run.Color,
				});
			}

			return result;
		}

		/// <summary>
		/// Joins the text drawn inside a figure, top to bottom and left to right, one line per label.
		/// </summary>
		/// <param name="lines">The lines inside the figure.</param>
		/// <returns>The labels.</returns>
		private static string JoinLabels(List<TextLine> lines)
		{
			lines.Sort((a, b) => Math.Abs(a.Top - b.Top) > 2 ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));
			var texts = new List<string>();
			foreach (var line in lines)
				texts.Add(line.Text);

			return string.Join("\n", texts);
		}

		#endregion
	}
}
