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

// Document-level analysis: running headers and footers, headings and their
// levels, captions, and content that continues across columns and pages.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// Settings of the document-level analysis.
	/// </summary>
	internal sealed class DocumentAnalyzerSettings
	{
		/// <summary>Detect running headers and footers. Default: true.</summary>
		public bool DetectHeadersAndFooters = true;

		/// <summary>Detect headings. Default: true.</summary>
		public bool DetectHeadings = true;

		/// <summary>Join paragraphs split across columns and pages. Default: true.</summary>
		public bool JoinParagraphs = true;

		/// <summary>Join tables continued on the next page. Default: true.</summary>
		public bool JoinTables = true;
	}

	/// <summary>
	/// Refines the blocks of all the pages using information that is only available at the document level.
	/// </summary>
	internal sealed class DocumentAnalyzer
	{
		#region Patterns

		// "Figure 3", "Fig. 2a:", "Table IV." and translations.
		private static readonly Regex CaptionPattern = new Regex(
			@"^(Figure|Fig\.?|Table|Tab\.?|Chart|Exhibit|Diagram|Graph|Image|Photo|Plate|Scheme|Listing|Algorithm|Illustration|Abbildung|Abb\.|Tabelle|Figura|Tabella|Tableau|Tabla|Afbeelding|Tabel)\s*[0-9IVXLivxl]+[A-Za-z]?([.\-][0-9]+)*\s*(?<sep>[.:\-–—)|])?",
			RegexOptions.CultureInvariant);

		// page numbers: "12", "- 12 -", "Page 3 of 10", "iv", "3/10".
		private static readonly Regex PageNumberPattern = new Regex(
			@"^\W*((page|pag\.|p\.|seite|página|pagina)\s*)?(#+|[ivxlcdm]+)(\s*(of|/|di|de|von)\s*#+)?\W*$",
			RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

		// numbered headings: "1", "2.3", "A.1", "IV." followed by the title.
		private static readonly Regex NumberedHeadingPattern = new Regex(
			@"^((\d{1,2}|[A-Z])(\.\d{1,2}){0,4}\.?|[IVX]{1,5}\.)\s+\S",
			RegexOptions.CultureInvariant);

		#endregion

		// the analysis settings.
		private readonly DocumentAnalyzerSettings _settings;
		// the document outline, used to recognize headings.
		private readonly IReadOnlyList<PdfOutline> _outlines;
		// the structure tree of tagged documents, or null.
		private readonly StructureTree _structure;

		/// <summary>
		/// Initializes a new instance of <see cref="DocumentAnalyzer"/>.
		/// </summary>
		/// <param name="settings">The settings.</param>
		/// <param name="outlines">The document outline, used to recognize headings; can be empty.</param>
		/// <param name="structure">The structure tree of tagged documents, or null.</param>
		public DocumentAnalyzer(DocumentAnalyzerSettings settings, IReadOnlyList<PdfOutline> outlines, StructureTree structure = null)
		{
			_structure = structure;
			_settings = settings ?? new DocumentAnalyzerSettings();
			_outlines = outlines ?? new PdfOutline[0];
		}

		#region Methods

		/// <summary>
		/// Analyzes the blocks of all the pages.
		/// </summary>
		/// <param name="pages">The blocks of each page in reading order, with the page.</param>
		/// <returns>The refined blocks of each page.</returns>
		public List<List<LayoutBlock>> Analyze(List<(PdfPage Page, List<LayoutBlock> Blocks)> pages)
		{
			var bodySize = GetBodyFontSize(pages);

			if (_settings.DetectHeadersAndFooters)
				DetectHeadersAndFooters(pages);

			foreach (var page in pages)
				DetectCaptions(page.Blocks);

			foreach (var page in pages)
				MergeCodeBlocks(page.Blocks);

			if (_settings.DetectHeadings)
				DetectHeadings(pages, bodySize);

			if (_settings.JoinParagraphs)
				JoinParagraphs(pages);

			if (_settings.JoinTables)
				JoinTables(pages);

			var result = new List<List<LayoutBlock>>();
			foreach (var page in pages)
				result.Add(page.Blocks);

			return result;
		}

		#endregion

		#region Body size

		// the font size used by most characters of the paragraphs.
		private static double GetBodyFontSize(List<(PdfPage Page, List<LayoutBlock> Blocks)> pages)
		{
			var counts = new Dictionary<int, int>();
			foreach (var page in pages)
			{
				foreach (var block in page.Blocks)
				{
					if (block.Kind != LayoutBlockKind.Paragraph && block.Kind != LayoutBlockKind.ListItem)
						continue;

					foreach (var line in block.Lines)
					{
						var key = (int)Math.Round(line.FontSize * 2);
						counts.TryGetValue(key, out var count);
						counts[key] = count + line.CharCount;
					}
				}
			}

			var best = 20;
			var bestCount = -1;
			foreach (var entry in counts)
			{
				if (entry.Value > bestCount)
				{
					best = entry.Key;
					bestCount = entry.Value;
				}
			}

			return best / 2.0;
		}

		#endregion

		#region Headers and footers

		/// <summary>
		/// Marks the text and logos repeated in the page margins, and the page numbers, as headers and footers.
		/// </summary>
		/// <param name="pages">The pages with their blocks.</param>
		private static void DetectHeadersAndFooters(List<(PdfPage Page, List<LayoutBlock> Blocks)> pages)
		{
			// candidates: text blocks in the top and bottom margins of each page.
			var candidates = new List<(int Page, LayoutBlock Block, bool Top, string Key)>();
			for (int p = 0; p < pages.Count; p++)
			{
				var page = pages[p].Page;
				var band = page.Height * 0.12;
				foreach (var block in pages[p].Blocks)
				{
					var isImage = block.Kind == LayoutBlockKind.Figure && block.Images.Count > 0 && block.Images[0].Stream != null;
					if (!isImage && (block.Lines.Count == 0 || block.Lines.Count > 3 || block.Kind == LayoutBlockKind.Table || block.Kind == LayoutBlockKind.Figure))
						continue;

					var bounds = block.DisplayBounds;
					var top = bounds.Bottom <= band;
					var bottom = bounds.Top >= page.Height - band;
					if (!top && !bottom)
						continue;

					// logos repeated in the margins are identified by their image stream.
					var key = isImage
						? "image:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(block.Images[0].Stream).ToString(CultureInfo.InvariantCulture)
						: NormalizeKey(block.GetText());

					candidates.Add((p, block, top, key));
				}
			}

			// a key repeated on enough pages at the same position is a running header or footer.
			var minimum = pages.Count <= 2 ? 2 : Math.Max(2, (int)Math.Ceiling(pages.Count * 0.3));
			foreach (var candidate in candidates)
			{
				var block = candidate.Block;
				var isPageNumber = PageNumberPattern.IsMatch(candidate.Key);
				var isArtifact = block.Lines.Count > 0 && block.Lines.TrueForAll(l => l.IsArtifact);

				var repeats = 0;
				if (!isPageNumber)
				{
					var seen = new HashSet<int>();
					foreach (var other in candidates)
					{
						if (other.Top == candidate.Top && other.Key == candidate.Key
							&& Math.Abs(other.Block.DisplayBounds.Top - block.DisplayBounds.Top) < 15
							&& seen.Add(other.Page))
						{
							repeats++;
						}
					}
				}

				if ((isPageNumber && pages.Count > 1) || repeats >= minimum || (isArtifact && pages.Count > 1))
					block.Kind = candidate.Top ? LayoutBlockKind.PageHeader : LayoutBlockKind.PageFooter;
			}
		}

		// normalizes text so that running headers with changing page numbers produce the same key.
		private static string NormalizeKey(string text)
		{
			var sb = new StringBuilder(text.Length);
			var lastSpace = false;
			foreach (var c in text)
			{
				if (char.IsDigit(c))
				{
					if (sb.Length == 0 || sb[sb.Length - 1] != '#')
						sb.Append('#');
					lastSpace = false;
				}
				else if (char.IsWhiteSpace(c))
				{
					if (!lastSpace && sb.Length > 0)
						sb.Append(' ');
					lastSpace = true;
				}
				else
				{
					sb.Append(char.ToLowerInvariant(c));
					lastSpace = false;
				}
			}

			return sb.ToString().Trim();
		}

		#endregion

		#region Code

		// consecutive code blocks separated only by blank lines are one listing.
		private static void MergeCodeBlocks(List<LayoutBlock> blocks)
		{
			for (int i = 0; i + 1 < blocks.Count; i++)
			{
				var a = blocks[i];
				var b = blocks[i + 1];
				if (a.Kind != LayoutBlockKind.Code || b.Kind != LayoutBlockKind.Code)
					continue;

				var size = Math.Max(a.FontSize, b.FontSize);
				var gap = b.Bounds.Top - a.Bounds.Bottom;
				var overlap = Math.Min(a.Bounds.Right, b.Bounds.Right) - Math.Max(a.Bounds.Left, b.Bounds.Left);
				if (gap < -1 || gap > size * 3.5 || overlap <= 0)
					continue;

				a.Lines.AddRange(b.Lines);
				a.Bounds = a.Bounds.Union(b.Bounds);
				a.DisplayBounds = a.DisplayBounds.Union(b.DisplayBounds);
				blocks.RemoveAt(i + 1);
				i--;
			}
		}

		#endregion

		#region Captions

		// paragraphs that start like captions and touch a figure or table become their captions.
		private static void DetectCaptions(List<LayoutBlock> blocks)
		{
			// candidate pairs of caption and figure/table, assigned globally by increasing distance so that
			// a caption is never taken by a farther figure (a caption of a code listing near a diagram).
			var pairs = new List<(LayoutBlock Caption, LayoutBlock Target, double Distance)>();
			var captions = new List<(LayoutBlock Block, bool HasSeparator)>();

			foreach (var block in blocks)
			{
				if (block.Kind != LayoutBlockKind.Paragraph || block.Lines.Count == 0 || block.Lines.Count > 12)
					continue;

				var text = block.Lines[0].Text;
				var match = CaptionPattern.Match(text);
				if (!match.Success)
					continue;

				// without punctuation after the number, it may be a sentence ("Table 2 shows ..."): require a nearby short block.
				captions.Add((block, match.Groups["sep"].Success));
				var isTableCaption = text.StartsWith("Tab", StringComparison.Ordinal);

				foreach (var other in blocks)
				{
					if (other.Kind != LayoutBlockKind.Figure && other.Kind != LayoutBlockKind.Table)
						continue;

					var overlap = Math.Min(other.Bounds.Right, block.Bounds.Right) - Math.Max(other.Bounds.Left, block.Bounds.Left);
					if (overlap < Math.Min(other.Bounds.Width, block.Bounds.Width) * 0.3)
						continue;

					var distance = block.Bounds.Top >= other.Bounds.Bottom
						? block.Bounds.Top - other.Bounds.Bottom
						: other.Bounds.Top - block.Bounds.Bottom;

					// prefer the kind matching the caption word.
					if ((other.Kind == LayoutBlockKind.Table) != isTableCaption)
						distance += 20;

					if (distance >= -2 && distance <= block.FontSize * 4 + 20)
						pairs.Add((block, other, distance));
				}
			}

			pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			var assigned = new HashSet<LayoutBlock>();
			foreach (var (caption, target, _) in pairs)
			{
				if (assigned.Contains(caption) || target.Caption != null)
					continue;

				target.Caption = caption;
				assigned.Add(caption);
			}

			foreach (var (block, hasSeparator) in captions)
			{
				if (hasSeparator || (assigned.Contains(block) && block.Lines.Count <= 3))
					block.Kind = LayoutBlockKind.Caption;
				else if (assigned.Contains(block))
				{
					// a sentence that only looked like a caption: detach it.
					foreach (var other in blocks)
					{
						if (other.Caption == block)
							other.Caption = null;
					}
				}
			}
		}

		#endregion

		#region Headings

		/// <summary>
		/// Recognizes the headings and assigns their levels.
		/// </summary>
		/// <param name="pages">The pages with their blocks.</param>
		/// <param name="bodySize">The font size of the body text.</param>
		private void DetectHeadings(List<(PdfPage Page, List<LayoutBlock> Blocks)> pages, double bodySize)
		{
			// tagged documents that mark headings are authoritative: their tags are used instead of the heuristics.
			if (_structure != null && ApplyTaggedHeadings(pages, bodySize))
				return;

			var outlineTitles = CollectOutlineTitles();
			var candidates = new List<LayoutBlock>();

			foreach (var page in pages)
			{
				var blocks = page.Blocks;
				for (int i = 0; i < blocks.Count; i++)
				{
					var block = blocks[i];
					if (block.Kind != LayoutBlockKind.Paragraph || block.Lines.Count == 0 || block.IsMarginal)
						continue;

					var next = NextTextBlock(blocks, i);
					if (IsHeading(block, next, bodySize, outlineTitles))
					{
						block.Kind = LayoutBlockKind.Heading;
						candidates.Add(block);
					}
				}
			}

			AssignLevels(candidates, outlineTitles);
		}

		// headings from the structure tree; returns false when the document doesn't tag any heading.
		private bool ApplyTaggedHeadings(List<(PdfPage Page, List<LayoutBlock> Blocks)> pages, double bodySize)
		{
			var levels = new Dictionary<LayoutBlock, int>();
			foreach (var page in pages)
			{
				foreach (var block in page.Blocks)
				{
					if (block.Kind != LayoutBlockKind.Paragraph || block.Lines.Count == 0 || block.Lines[0].Words.Count == 0)
						continue;

					var glyph = block.Lines[0].Words[0].First;
					var level = _structure.GetHeadingLevel(glyph.StructParent, glyph.Mcid);

					// some producers tag whole paragraphs as headings: long text is never a heading.
					// the heading must also look like one: bold, larger than the body, or in capitals.
					var text = block.GetText();
					var looksLikeHeading = block.IsBold || block.FontSize >= bodySize * 1.05 || (text.Length > 3 && text.ToUpperInvariant() == text);
					if (level != 0 && looksLikeHeading && block.Lines.Count <= 4 && text.Length <= 200)
						levels[block] = level;
				}
			}

			if (levels.Count == 0)
				return false;

			// untagged text larger than every tagged heading is a title (producers often tag it as a paragraph).
			var largest = 0.0;
			foreach (var block in levels.Keys)
				largest = Math.Max(largest, block.FontSize);

			foreach (var page in pages)
			{
				foreach (var block in page.Blocks)
				{
					if (block.Kind == LayoutBlockKind.Paragraph && !levels.ContainsKey(block) && !block.IsMarginal
						&& block.FontSize > largest * 1.05 && block.Lines.Count <= 2 && block.GetText().Length <= 120)
					{
						levels[block] = -1;
					}
				}
			}

			// a document title becomes the level 1 heading and pushes the other levels down.
			var hasTitle = levels.ContainsValue(-1);
			foreach (var entry in levels)
			{
				entry.Key.Kind = LayoutBlockKind.Heading;

				// the generic "H" role has no level.
				var level = entry.Value == -1 ? 1 : entry.Value <= 6 ? entry.Value : 2;
				if (hasTitle && entry.Value != -1)
					level++;

				entry.Key.Level = Math.Min(6, level);
			}

			return true;
		}

		/// <summary>
		/// Returns the next block that is not a header, footer or caption.
		/// </summary>
		/// <param name="blocks">The blocks of the page.</param>
		/// <param name="index">The index of the current block.</param>
		/// <returns>The next block, or null.</returns>
		private static LayoutBlock NextTextBlock(
List<LayoutBlock> blocks, int index)
		{
			for (int i = index + 1; i < blocks.Count; i++)
			{
				var kind = blocks[i].Kind;
				if (kind != LayoutBlockKind.PageHeader && kind != LayoutBlockKind.PageFooter && kind != LayoutBlockKind.Caption)
					return blocks[i];
			}

			return null;
		}

		/// <summary>
		/// Returns true when a paragraph is a heading: in the outline, larger than the body, or bold and standing alone.
		/// </summary>
		/// <param name="block">The candidate block.</param>
		/// <param name="next">The following block, or null.</param>
		/// <param name="bodySize">The font size of the body text.</param>
		/// <param name="outlineTitles">The normalized outline titles.</param>
		/// <returns>True when the block is a heading.</returns>
		private static bool IsHeading(LayoutBlock block, LayoutBlock next, double bodySize, Dictionary<string, int> outlineTitles)
		{
			var text = block.GetText().Trim();
			if (text.Length == 0 || text.Length > 160 || block.Lines.Count > 3)
				return false;

			// the document outline is the most reliable source.
			if (outlineTitles.ContainsKey(NormalizeTitle(text)))
				return true;

			// must contain letters; must not look like a sentence.
			var letters = 0;
			foreach (var c in text)
			{
				if (char.IsLetter(c))
					letters++;
			}

			if (letters < 2)
				return false;

			var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
			if (words > 18)
				return false;

			var endsWithPeriod = text.EndsWith(".", StringComparison.Ordinal) && !NumberedHeadingPattern.IsMatch(text);
			if (endsWithPeriod && words > 6)
				return false;

			if (text.EndsWith(",", StringComparison.Ordinal) || text.EndsWith(";", StringComparison.Ordinal))
				return false;

			var size = block.FontSize;
			var bold = block.IsBold;

			// clearly larger than the body text.
			if (size >= bodySize * 1.15)
				return true;

			// same size but bold (or all caps), standing alone before normal text.
			var allCaps = letters >= 4 && text.ToUpperInvariant() == text;
			if ((bold || allCaps) && size >= bodySize * 0.9 && block.Lines.Count <= 2)
			{
				// a bold block followed by bold text is emphasis, not a heading.
				if (next != null && next.IsBold && next.Lines.Count > 2)
					return false;

				// a weak heading (bold, body size) must introduce a real paragraph, a list, a table or a figure.
				if (next == null || (next.IsBold && next.FontSize >= size * 0.95))
					return false;

				return next.Kind != LayoutBlockKind.Paragraph || next.GetText().Length >= 60;
			}

			return false;
		}

		// levels: rank of the distinct heading styles (size, then bold), refined by the outline.
		private void AssignLevels(List<LayoutBlock> headings, Dictionary<string, int> outlineTitles)
		{
			if (headings.Count == 0)
				return;

			// distinct styles, largest first.
			var styles = new List<(double Size, bool Bold, bool Caps)>();
			foreach (var heading in headings)
			{
				var style = GetStyle(heading);
				if (!styles.Exists(s => SameStyle(s, style)))
					styles.Add(style);
			}

			styles.Sort((a, b) =>
			{
				var c = b.Size.CompareTo(a.Size);
				if (c != 0)
					return c;
				c = b.Bold.CompareTo(a.Bold);
				return c != 0 ? c : b.Caps.CompareTo(a.Caps);
			});

			foreach (var heading in headings)
			{
				var style = GetStyle(heading);
				var level = styles.FindIndex(s => SameStyle(s, style)) + 1;

				// the outline level wins when the heading is in the outline.
				if (outlineTitles.TryGetValue(NormalizeTitle(heading.GetText()), out var outlineLevel))
					level = outlineLevel;

				heading.Level = Math.Max(1, Math.Min(6, level));
			}
		}

		// the style of a heading, used to rank the levels.
		private static (double Size, bool Bold, bool Caps) GetStyle(LayoutBlock block)
		{
			var text = block.GetText();
			var caps = text.Length > 3 && text.ToUpperInvariant() == text;
			return (Math.Round(block.FontSize * 2) / 2, block.IsBold, caps);
		}

		// true when two heading styles are the same level.
		private static bool SameStyle((double Size, bool Bold, bool Caps) a, (double Size, bool Bold, bool Caps) b)
		{
			return Math.Abs(a.Size - b.Size) < 0.6 && a.Bold == b.Bold && a.Caps == b.Caps;
		}

		// outline titles normalized, with their level.
		private Dictionary<string, int> CollectOutlineTitles()
		{
			var titles = new Dictionary<string, int>(StringComparer.Ordinal);
			void Walk(IReadOnlyList<PdfOutline> items)
			{
				foreach (var item in items)
				{
					var key = NormalizeTitle(item.Title);
					if (key.Length > 0 && !titles.ContainsKey(key))
						titles[key] = item.Level;

					Walk(item.Children);
				}
			}

			Walk(_outlines);
			return titles;
		}

		// lower case letters and digits only, so "3.1 Encoder and Decoder Stacks" matches "Encoder and Decoder Stacks" loosely.
		private static string NormalizeTitle(string text)
		{
			var sb = new StringBuilder();
			foreach (var c in text.Normalize(NormalizationForm.FormKC))
			{
				if (char.IsLetter(c))
					sb.Append(char.ToLowerInvariant(c));
			}

			return sb.ToString();
		}

		#endregion

		#region Continuations

		// joins a paragraph split by a column or page break.
		private static void JoinParagraphs(List<(PdfPage Page, List<LayoutBlock> Blocks)> pages)
		{
			LayoutBlock previous = null;

			foreach (var page in pages)
			{
				var blocks = page.Blocks;
				for (int i = 0; i < blocks.Count; i++)
				{
					var block = blocks[i];
					switch (block.Kind)
					{
						case LayoutBlockKind.PageHeader:
						case LayoutBlockKind.PageFooter:
						case LayoutBlockKind.Footnote:
						case LayoutBlockKind.Figure:
						case LayoutBlockKind.Caption:
							// these can sit between the two halves of a paragraph.
							continue;

						case LayoutBlockKind.Paragraph:
							if (previous != null && Continues(previous, block))
							{
								previous.Lines.AddRange(block.Lines);
								previous.Bounds = previous.Bounds.Union(block.Bounds);
								blocks.RemoveAt(i);
								i--;
								continue;
							}

							previous = block;
							break;

						default:
							previous = null;
							break;
					}
				}
			}
		}

		/// <summary>
		/// Returns true when <paramref name="second"/> continues the sentence of <paramref name="first"/>.
		/// </summary>
		/// <param name="first">The paragraph before the break.</param>
		/// <param name="second">The paragraph after the break.</param>
		/// <returns>True when the paragraphs must be joined.</returns>
		private static bool Continues(LayoutBlock first, LayoutBlock second)
		{
			var last = first.Lines[first.Lines.Count - 1];
			var next = second.Lines[0];
			var lastText = last.Text.TrimEnd();
			var nextText = next.Text.TrimStart();
			if (lastText.Length == 0 || nextText.Length == 0)
				return false;

			// similar text style.
			var ratio = Math.Max(first.FontSize, second.FontSize) / Math.Max(0.1, Math.Min(first.FontSize, second.FontSize));
			if (ratio > 1.1)
				return false;

			// the first paragraph stops mid-sentence and the second continues it.
			var hyphenated = TextAssembler.EndsWithHyphenation(last, nextText);
			var openSentence = !PageAnalyzer.EndsSentence(lastText);
			var lowercaseStart = char.IsLower(nextText[0]);

			return hyphenated || (openSentence && lowercaseStart);
		}

		// joins tables continued on the following pages with the same columns.
		private static void JoinTables(List<(PdfPage Page, List<LayoutBlock> Blocks)> pages)
		{
			// the table still open at the end of the previous page, which the next page can continue.
			LayoutBlock open = null;

			foreach (var page in pages)
			{
				var blocks = page.Blocks;
				var first = LastContentBlock(blocks, fromEnd: false);

				if (open != null && first != null && first.Kind == LayoutBlockKind.Table
					&& open.Table.ColumnCount == first.Table.ColumnCount && SimilarColumns(open.Table, first.Table))
				{
					var a = open.Table;
					var b = first.Table;

					// a repeated header row on the continuation is dropped.
					var skip = 0;
					if (b.HeaderRowCount > 0 || SameFirstRow(a, b))
						skip = Math.Max(1, b.HeaderRowCount);

					foreach (var cell in b.Cells)
					{
						if (cell.Row < skip)
							continue;

						cell.Row = cell.Row - skip + a.RowCount;
						cell.IsHeader = false;
						a.Cells.Add(cell);
					}

					a.RowCount += b.RowCount - skip;
					a.ContinuedOn.Add(page.Page.Number);
					blocks.Remove(first);

					// a page holding only the continuation keeps the table open for the next page.
					if (LastContentBlock(blocks, fromEnd: false) == null)
						continue;
				}

				var last = LastContentBlock(blocks, fromEnd: true);
				open = last != null && last.Kind == LayoutBlockKind.Table ? last : null;
			}
		}

		// the first (or last) block that is not a header, footer or footnote.
		private static LayoutBlock LastContentBlock(List<LayoutBlock> blocks, bool fromEnd)
		{
			for (int i = 0; i < blocks.Count; i++)
			{
				var block = blocks[fromEnd ? blocks.Count - 1 - i : i];
				if (block.Kind != LayoutBlockKind.PageHeader && block.Kind != LayoutBlockKind.PageFooter && block.Kind != LayoutBlockKind.Footnote)
					return block;
			}

			return null;
		}

		// true when two tables have the same column boundaries.
		private static bool SimilarColumns(TableModel a, TableModel b)
		{
			if (a.ColumnEdges == null || b.ColumnEdges == null || a.ColumnEdges.Length != b.ColumnEdges.Length)
				return false;

			for (int i = 0; i < a.ColumnEdges.Length; i++)
			{
				if (Math.Abs(a.ColumnEdges[i] - b.ColumnEdges[i]) > 12)
					return false;
			}

			return true;
		}

		// true when two tables start with the same row (a repeated header).
		private static bool SameFirstRow(TableModel a, TableModel b)
		{
			for (int c = 0; c < a.ColumnCount; c++)
			{
				var x = a.GetCell(0, c)?.GetText() ?? string.Empty;
				var y = b.GetCell(0, c)?.GetText() ?? string.Empty;
				if (!string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
					return false;
			}

			return true;
		}

		#endregion
	}
}
