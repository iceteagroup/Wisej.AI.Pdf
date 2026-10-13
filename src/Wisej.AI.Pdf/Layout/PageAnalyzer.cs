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

// Page segmentation: tables, figures, text blocks, reading order and
// paragraphs.

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.Content;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// Settings of the page analysis.
	/// </summary>
	internal sealed class AnalyzerSettings
	{
		/// <summary>Detect tables. Default: true.</summary>
		public bool DetectTables = true;

		/// <summary>Detect tables without ruling lines. Default: true.</summary>
		public bool DetectBorderlessTables = true;

		/// <summary>Detect figures (images and vector graphics). Default: true.</summary>
		public bool DetectFigures = true;

		/// <summary>Minimum size in points of an image to be reported as a figure. Default: 16.</summary>
		public double MinFigureSize = 16;

		/// <summary>Treat text written right to left (Arabic, Hebrew) when ordering columns. Default: auto-detected.</summary>
		public bool? RightToLeft;
	}

	/// <summary>
	/// Segments a page into blocks and determines their reading order.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Lines are first grouped into <em>text blocks</em>: vertically adjacent lines that overlap horizontally
	/// and have similar size, not separated by rules, tables or figures. Tables and figures are detected
	/// before, and become atomic blocks.
	/// </para>
	/// <para>
	/// The reading order of the blocks is computed with the precedence rules of T. M. Breuel
	/// ("High Performance Document Layout Analysis", 2003): a block precedes another when it's above it
	/// and they overlap horizontally, or when it's entirely to its left and no third block spanning both
	/// lies vertically between them. A topological sort of this relation handles multi-column pages,
	/// titles and figures spanning columns, and sidebars.
	/// </para>
	/// <para>
	/// Finally each text block is split into paragraphs, list items and code blocks.
	/// </para>
	/// </remarks>
	internal sealed class PageAnalyzer
	{
		#region Nested types

		/// <summary>
		/// A horizontal or vertical rule drawn on the page, which separates content.
		/// </summary>
		private struct Rule
		{
			/// <summary>The bounds in the reading frame.</summary>
			public PdfRect Bounds;

			/// <summary>True for horizontal rules.</summary>
			public bool IsHorizontal;
		}

		#endregion

		// the page.
		private readonly PdfPage _page;
		// the analysis settings.
		private readonly AnalyzerSettings _settings;

		/// <summary>
		/// Initializes a new instance of <see cref="PageAnalyzer"/>.
		/// </summary>
		/// <param name="page">The page.</param>
		/// <param name="settings">The settings.</param>
		public PageAnalyzer(PdfPage page, AnalyzerSettings settings)
		{
			_page = page;
			_settings = settings ?? new AnalyzerSettings();
		}

		#region Methods

		/// <summary>
		/// Analyzes the page.
		/// </summary>
		/// <param name="objects">The objects painted on the page.</param>
		/// <param name="lines">The lines built from the glyphs.</param>
		/// <returns>The blocks in reading order.</returns>
		public List<LayoutBlock> Analyze(PageObjects objects, List<TextLine> lines)
		{
			var result = new List<LayoutBlock>();

			// lay out each orientation separately; the dominant one comes first.
			var orientations = GetOrientations(lines);
			for (int o = 0; o < orientations.Count; o++)
			{
				var orientation = orientations[o];
				var group = lines.FindAll(l => l.Orientation == orientation);
				var isDominant = o == 0;
				result.AddRange(AnalyzeOrientation(objects, group, orientation, isDominant));
			}

			// pages without text still have figures.
			if (orientations.Count == 0)
				result.AddRange(AnalyzeOrientation(objects, new List<TextLine>(), 0, true));

			foreach (var block in result)
				block.PageNumber = _page.Number;

			return result;
		}

		#endregion

		#region Orientation

		// the orientations present, by decreasing amount of text.
		private static List<int> GetOrientations(List<TextLine> lines)
		{
			var counts = new Dictionary<int, int>();
			foreach (var line in lines)
			{
				counts.TryGetValue(line.Orientation, out var count);
				counts[line.Orientation] = count + line.CharCount;
			}

			var orientations = new List<int>(counts.Keys);
			orientations.Sort((a, b) => counts[b].CompareTo(counts[a]));
			return orientations;
		}

		/// <summary>
		/// Analyzes the lines of one orientation: tables, figures, text blocks, reading order and paragraphs.
		/// </summary>
		/// <param name="objects">The objects painted on the page.</param>
		/// <param name="lines">The lines of this orientation.</param>
		/// <param name="orientation">The orientation.</param>
		/// <param name="isDominant">True for the main orientation of the page, which owns tables and figures.</param>
		/// <returns>The blocks in reading order.</returns>
		private List<LayoutBlock> AnalyzeOrientation(PageObjects objects, List<TextLine> lines, int orientation, bool isDominant)
		{
			var width = _page.Width;
			var height = _page.Height;

			MergeDetachedMarkers(lines);

			var rules = isDominant ? CollectRules(objects, orientation) : new List<Rule>();
			var blocks = new List<LayoutBlock>();

			// 1. ruled tables.
			var tables = isDominant && _settings.DetectTables ? new TableDetector(objects, orientation, width, height, _settings.DetectBorderlessTables) : null;
			if (tables != null)
				blocks.AddRange(tables.DetectRuled(lines));

			// 2. figures: before the borderless tables, so the labels of charts are not taken for tables.
			if (_settings.DetectFigures && isDominant)
			{
				var detector = new FigureDetector(objects, orientation, width, height, _settings.MinFigureSize);
				foreach (var figure in detector.Detect(lines, blocks))
					blocks.Add(figure);
			}

			// 3. borderless tables, outside tables and figures.
			if (tables != null)
				blocks.AddRange(tables.DetectBorderless(lines, blocks));

			// 4. text blocks from the remaining lines.
			var free = lines.FindAll(l => !l.IsConsumed);
			var textBlocks = GroupLines(free, rules, blocks);

			// 5. reading order of all the blocks.
			var all = new List<LayoutBlock>(blocks);
			all.AddRange(textBlocks);
			var rtl = _settings.RightToLeft ?? IsRightToLeft(free);
			var ordered = Order(all, rtl);

			// 6. paragraphs.
			var result = new List<LayoutBlock>();
			foreach (var block in ordered)
			{
				if (block.Kind == LayoutBlockKind.Paragraph)
					result.AddRange(SplitParagraphs(block));
				else
					result.Add(block);
			}

			foreach (var block in result)
			{
				block.Orientation = orientation;
				block.IsMarginal = !isDominant;
				block.DisplayBounds = ReadingFrame.ToDisplay(orientation, block.Bounds, width, height);
			}
			return result;
		}

		#endregion

		#region Rules

		// long thin strokes and filled rectangles that act as separators.
		private List<Rule> CollectRules(PageObjects objects, int orientation)
		{
			var rules = new List<Rule>();
			foreach (var path in objects.Paths)
			{
				if (path.IsClipped || path.HasCurves)
					continue;

				if (path.IsStroked)
				{
					var segments = path.Segments;
					for (int i = 0; i + 3 < segments.Count; i += 4)
					{
						var rect = new PdfRect(segments[i], segments[i + 1], segments[i + 2], segments[i + 3]).Inflate(path.LineWidth / 2);
						AddRule(rules, ReadingFrame.ToFrame(orientation, rect, _page.Width, _page.Height));
					}
				}

				if (path.IsFilled)
				{
					foreach (var rect in path.Rectangles)
						AddRule(rules, ReadingFrame.ToFrame(orientation, rect, _page.Width, _page.Height));
				}
			}

			return rules;
		}

		// adds a rectangle as a rule when it's long and thin.
		private static void AddRule(List<Rule> rules, PdfRect rect)
		{
			if (rect.Height <= 3 && rect.Width >= 15)
				rules.Add(new Rule { Bounds = rect, IsHorizontal = true });
			else if (rect.Width <= 3 && rect.Height >= 15)
				rules.Add(new Rule { Bounds = rect, IsHorizontal = false });
		}

		#endregion

		#region Text blocks

		// bullets or numbers painted far from their text become separate lines: join them back.
		private static void MergeDetachedMarkers(List<TextLine> lines)
		{
			for (int i = 0; i < lines.Count; i++)
			{
				var marker = lines[i];
				if (marker.Words.Count != 1)
					continue;

				// list markers, and section numbers set in the margin ("100.1", "§ 4", "(a)") which label a paragraph or heading.
				var text = marker.Words[0].Text;
				var isListMarker = ListMarkers.IsMarker(text);
				var isSectionLabel = !isListMarker && ListMarkers.IsSectionLabel(text);
				if (!isListMarker && !isSectionLabel)
					continue;

				TextLine target = null;
				var bestGap = double.MaxValue;
				foreach (var line in lines)
				{
					if (line == marker || line.Orientation != marker.Orientation)
						continue;

					var gap = line.Left - marker.Right;
					var sameRow = Math.Abs(line.Baseline - marker.Baseline) < Math.Max(line.FontSize, marker.FontSize) * 0.4;
					// a section label must introduce real text, not a table cell or a table of contents entry.
					if (isSectionLabel && ((line.Words.Count < 3 && !line.IsBold) || line.Text.Contains("....")))
						continue;

					var maximumGap = Math.Max(line.FontSize, marker.FontSize) * (isSectionLabel ? 12 : 6);
					if (sameRow && gap >= -0.5 && gap < maximumGap && gap < bestGap)
					{
						target = line;
						bestGap = gap;
					}
				}

				if (target == null)
					continue;

				var word = marker.Words[0];
				word.SpaceAfter = true;
				target.Words.Insert(0, word);
				target.Bounds = target.Bounds.Union(marker.Bounds);
				target.DisplayBounds = target.DisplayBounds.Union(marker.DisplayBounds);
				target.Order = Math.Min(target.Order, marker.Order);
				target.InvalidateText();

				lines.RemoveAt(i);
				i--;
			}
		}

		// union-find grouping of vertically adjacent, horizontally overlapping lines.
		private List<LayoutBlock> GroupLines(List<TextLine> lines, List<Rule> rules, List<LayoutBlock> obstacles)
		{
			lines.Sort((a, b) => a.Top.CompareTo(b.Top));

			var parent = new int[lines.Count];
			for (int i = 0; i < parent.Length; i++)
				parent[i] = i;

			int Find(int x)
			{
				while (parent[x] != x)
				{
					parent[x] = parent[parent[x]];
					x = parent[x];
				}

				return x;
			}

			for (int i = 0; i < lines.Count; i++)
			{
				var a = lines[i];

				// the closest line below that overlaps horizontally is the continuation candidate.
				for (int j = i + 1; j < lines.Count; j++)
				{
					var b = lines[j];
					var size = Math.Max(a.FontSize, b.FontSize);
					var gap = b.Top - a.Bottom;
					if (gap > size * 2)
						break;

					if (!CanJoin(a, b, gap, rules, obstacles))
						continue;

					parent[Find(j)] = Find(i);
				}
			}

			// collect the groups.
			var groups = new Dictionary<int, LayoutBlock>();
			var blocks = new List<LayoutBlock>();
			for (int i = 0; i < lines.Count; i++)
			{
				var root = Find(i);
				if (!groups.TryGetValue(root, out var block))
				{
					block = new LayoutBlock { Kind = LayoutBlockKind.Paragraph, Bounds = lines[i].Bounds };
					groups[root] = block;
					blocks.Add(block);
				}

				block.Lines.Add(lines[i]);
				block.Bounds = block.Bounds.Union(lines[i].Bounds);
			}

			return blocks;
		}

		/// <summary>
		/// Returns true when line <paramref name="b"/> continues the text block of line <paramref name="a"/> above it.
		/// </summary>
		/// <param name="a">The upper line.</param>
		/// <param name="b">The lower line.</param>
		/// <param name="gap">The vertical gap between the lines.</param>
		/// <param name="rules">The rules of the page.</param>
		/// <param name="obstacles">Tables and figures, which separate text.</param>
		/// <returns>True when the lines belong to the same block.</returns>
		private static bool CanJoin(TextLine a, TextLine b, double gap, List<Rule> rules, List<LayoutBlock> obstacles)
		{
			var size = Math.Max(a.FontSize, b.FontSize);

			// vertical proximity: up to about one line of blank space.
			if (gap > size * 1.3 || gap < -size * 0.5)
				return false;

			// similar font sizes.
			var ratio = Math.Max(a.FontSize, b.FontSize) / Math.Max(0.1, Math.Min(a.FontSize, b.FontSize));
			if (ratio > 1.4)
				return false;

			// code: indented monospaced lines belong together whatever their horizontal extent.
			var code = a.IsMonospace && b.IsMonospace && gap <= size && Math.Abs(a.Left - b.Left) < size * 20;

			// horizontal overlap: a good part of the narrower line, or aligned left edges.
			var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
			var narrow = Math.Min(a.Bounds.Width, b.Bounds.Width);
			var leftAligned = Math.Abs(a.Left - b.Left) < size * 0.6;
			if (overlap < narrow * 0.4 && !leftAligned && !code)
				return false;

			// lines on the same baseline are separate columns or cells: never join them vertically.
			if (Math.Abs(a.Baseline - b.Baseline) < size * 0.3)
				return false;

			// separators: a rule or another block between the lines.
			var between = new PdfRect(Math.Max(a.Left, b.Left), a.Bottom, Math.Min(a.Right, b.Right), b.Top);
			foreach (var rule in rules)
			{
				if (rule.IsHorizontal && rule.Bounds.Top >= a.Bottom - 1 && rule.Bounds.Bottom <= b.Top + 1
					&& rule.Bounds.Right > between.Left && rule.Bounds.Left < between.Right)
				{
					return false;
				}
			}

			foreach (var obstacle in obstacles)
			{
				if (obstacle.Bounds.Top >= a.Bottom - 1 && obstacle.Bounds.Bottom <= b.Top + 1
					&& obstacle.Bounds.Right > between.Left && obstacle.Bounds.Left < between.Right)
				{
					return false;
				}
			}

			return true;
		}

		#endregion

		#region Reading order

		// orders the blocks with Breuel's precedence rules and a topological sort.
		private static List<LayoutBlock> Order(List<LayoutBlock> blocks, bool rightToLeft)
		{
			var n = blocks.Count;
			if (n <= 1)
				return blocks;

			// precedence[i] = list of blocks that must come after i.
			var after = new List<int>[n];
			var incoming = new int[n];
			for (int i = 0; i < n; i++)
				after[i] = new List<int>();

			for (int i = 0; i < n; i++)
			{
				for (int j = 0; j < n; j++)
				{
					if (i != j && Precedes(blocks, i, j, rightToLeft))
					{
						after[i].Add(j);
						incoming[j]++;
					}
				}
			}

			// Kahn's algorithm, choosing the top-most (then left-most) available block first.
			var result = new List<LayoutBlock>(n);
			var done = new bool[n];
			for (int step = 0; step < n; step++)
			{
				var best = -1;
				for (int i = 0; i < n; i++)
				{
					if (done[i] || incoming[i] > 0)
						continue;

					if (best < 0 || IsBefore(blocks[i], blocks[best], rightToLeft))
						best = i;
				}

				// a cycle (contradictory geometry): break it with the top-most remaining block.
				if (best < 0)
				{
					for (int i = 0; i < n; i++)
					{
						if (!done[i] && (best < 0 || IsBefore(blocks[i], blocks[best], rightToLeft)))
							best = i;
					}
				}

				done[best] = true;
				result.Add(blocks[best]);
				foreach (var j in after[best])
					incoming[j]--;
			}

			return result;
		}

		// the default tie-breaker: top to bottom, then left to right.
		private static bool IsBefore(LayoutBlock a, LayoutBlock b, bool rightToLeft)
		{
			if (Math.Abs(a.Bounds.Top - b.Bounds.Top) > 2)
				return a.Bounds.Top < b.Bounds.Top;

			return rightToLeft ? a.Bounds.Right > b.Bounds.Right : a.Bounds.Left < b.Bounds.Left;
		}

		/// <summary>
		/// Returns true when block <paramref name="i"/> must be read before block <paramref name="j"/> (Breuel's rules).
		/// </summary>
		/// <param name="blocks">The blocks.</param>
		/// <param name="i">The index of the first block.</param>
		/// <param name="j">The index of the second block.</param>
		/// <param name="rightToLeft">True for right-to-left scripts.</param>
		/// <returns>True when i precedes j.</returns>
		private static bool Precedes(List<LayoutBlock> blocks, int i, int j, bool rightToLeft)
		{
			var a = blocks[i].Bounds;
			var b = blocks[j].Bounds;
			const double tolerance = 1.5;

			// rule 1: overlapping horizontally and a is above b.
			var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
			if (overlap > tolerance)
				return a.CenterY < b.CenterY && a.Top < b.Top;

			// rule 2: a is entirely before b horizontally, and no block spanning both lies vertically between them.
			var leftOf = rightToLeft ? a.Left >= b.Right - tolerance : a.Right <= b.Left + tolerance;
			if (!leftOf)
				return false;

			var top = Math.Min(a.Top, b.Top);
			var bottom = Math.Max(a.Bottom, b.Bottom);
			for (int k = 0; k < blocks.Count; k++)
			{
				if (k == i || k == j)
					continue;

				var c = blocks[k].Bounds;
				var spansBoth = c.Left < Math.Max(a.Right, b.Right) && c.Right > Math.Min(a.Left, b.Left)
					&& Math.Min(c.Right, a.Right) - Math.Max(c.Left, a.Left) > tolerance
					&& Math.Min(c.Right, b.Right) - Math.Max(c.Left, b.Left) > tolerance;

				// c separates a and b when it's below the top one and above the bottom one.
				if (spansBoth && c.CenterY > top && c.CenterY < bottom
					&& ((c.Top >= a.Bottom - tolerance && c.Bottom <= b.Top + tolerance) || (c.Top >= b.Bottom - tolerance && c.Bottom <= a.Top + tolerance)))
				{
					return false;
				}
			}

			return true;
		}

		// right-to-left when most of the letters are Hebrew or Arabic.
		private static bool IsRightToLeft(List<TextLine> lines)
		{
			int rtl = 0, ltr = 0;
			foreach (var line in lines)
			{
				foreach (var c in line.Text)
				{
					if (c >= '֐' && c <= 'ࣿ')
						rtl++;
					else if (char.IsLetter(c))
						ltr++;
				}
			}

			return rtl > ltr;
		}

		#endregion

		#region Paragraphs

		// splits a text block into paragraphs, list items and code blocks.
		private static List<LayoutBlock> SplitParagraphs(LayoutBlock block)
		{
			var lines = block.Lines;
			lines.Sort((a, b) =>
			{
				var c = a.Top.CompareTo(b.Top);
				return Math.Abs(a.Top - b.Top) > 1 ? c : a.Left.CompareTo(b.Left);
			});

			var result = new List<LayoutBlock>();
			if (lines.Count == 0)
				return result;

			// block metrics: left edge, right edge and typical line spacing.
			var left = block.Bounds.Left;
			var right = block.Bounds.Right;
			var spacing = TypicalSpacing(lines);

			LayoutBlock current = null;
			for (int i = 0; i < lines.Count; i++)
			{
				var line = lines[i];
				var previous = i > 0 ? lines[i - 1] : null;
				var marker = ListMarkers.GetMarker(line);

				var startNew = current == null
					|| marker != null
					|| IsParagraphBreak(previous, line, current, left, right, spacing);

				if (startNew)
				{
					current = new LayoutBlock
					{
						Kind = marker != null ? LayoutBlockKind.ListItem : line.IsMonospace ? LayoutBlockKind.Code : LayoutBlockKind.Paragraph,
						Bounds = line.Bounds,
					};

					if (marker != null)
					{
						current.ListMarker = marker;
						current.IsOrderedList = ListMarkers.IsOrdered(marker);
					}

					result.Add(current);
				}

				current.Lines.Add(line);
				current.Bounds = current.Bounds.Union(line.Bounds);
			}

			// monospaced text is a code block only when it looks like code; otherwise it stays inline (e-mail addresses, identifiers).
			foreach (var item in result)
			{
				if (item.Kind == LayoutBlockKind.Code && !LooksLikeCode(item.Lines))
					item.Kind = LayoutBlockKind.Paragraph;
			}

			// list nesting by indentation relative to the block.
			foreach (var item in result)
			{
				if (item.Kind == LayoutBlockKind.ListItem)
				{
					var indent = item.Bounds.Left - left;
					var size = Math.Max(1, item.FontSize);
					item.ListDepth = (int)Math.Max(0, Math.Round(indent / (size * 1.8)));
				}
			}

			return result;
		}

		// three or more lines, or code punctuation.
		private static bool LooksLikeCode(List<TextLine> lines)
		{
			if (lines.Count >= 3)
				return true;

			foreach (var line in lines)
			{
				if (line.Text.IndexOfAny(new[] { '{', '}', ';', '=', '(' }) >= 0)
					return true;
			}

			return false;
		}

		/// <summary>
		/// Returns true when <paramref name="line"/> starts a new paragraph.
		/// </summary>
		/// <param name="previous">The previous line.</param>
		/// <param name="line">The line.</param>
		/// <param name="current">The paragraph being built.</param>
		/// <param name="left">The left edge of the text block.</param>
		/// <param name="right">The right edge of the text block.</param>
		/// <param name="spacing">The typical gap between lines of the block.</param>
		/// <returns>True when a new paragraph starts.</returns>
		private static bool IsParagraphBreak(TextLine previous, TextLine line, LayoutBlock current, double left, double right, double spacing)
		{
			var size = Math.Max(previous.FontSize, line.FontSize);
			var gap = line.Top - previous.Bottom;

			// code blocks continue across blank lines.
			if (current.Kind == LayoutBlockKind.Code && line.IsMonospace)
				return gap > size * 2.5;

			// a larger vertical gap than usual.
			if (gap > Math.Max(spacing * 1.6, spacing + size * 0.35))
				return true;

			// font changes: size, or monospaced/proportional.
			var ratio = Math.Max(previous.FontSize, line.FontSize) / Math.Max(0.1, Math.Min(previous.FontSize, line.FontSize));
			if (ratio > 1.15 || previous.IsMonospace != line.IsMonospace)
				return true;

			// a bold line (a run-in heading) followed by regular text, or the opposite.
			if (previous.IsBold != line.IsBold && (current.Lines.Count == 1 || IsShort(previous, left, right)))
				return true;

			// list items continue on lines indented at the text after the marker.
			if (current.Kind == LayoutBlockKind.ListItem)
			{
				var first = current.Lines[0];
				var textStart = first.Words.Count > 1 ? first.Words[1].Bounds.Left : first.Left;
				return line.Left < textStart - size * 1.5 && line.Left < first.Left + size * 0.5 && IsShort(previous, left, right);
			}

			if (current.Kind == LayoutBlockKind.Code)
				return false;

			// the previous line ended early and closed a sentence: end of paragraph.
			var previousShort = IsShort(previous, left, right);
			if (previousShort && EndsSentence(previous.Text))
				return true;

			// hanging indentation: a paragraph that starts with a section label continues on lines aligned with the text after it.
			var opening = current.Lines[0];
			if (opening.Words.Count > 1 && ListMarkers.IsSectionLabel(opening.Words[0].Text) && Math.Abs(line.Left - opening.Words[1].Bounds.Left) < size * 0.8)
				return false;

			// first-line indentation in a left-aligned block.
			var indented = line.Left > left + size * 0.8 && line.Left - previous.Left > size * 0.8;
			if (indented && previous.Left <= left + size * 0.5 && !IsCentered(line, left, right))
				return true;

			return false;
		}

		// true when the line ends well before the right edge of the block.
		private static bool IsShort(TextLine line, double left, double right)
		{
			var width = right - left;
			return width > 0 && line.Right < right - Math.Max(width * 0.15, line.FontSize * 2);
		}

		// true when the line is centered in the block.
		private static bool IsCentered(TextLine line, double left, double right)
		{
			var leftMargin = line.Left - left;
			var rightMargin = right - line.Right;
			return leftMargin > line.FontSize && Math.Abs(leftMargin - rightMargin) < line.FontSize * 1.5;
		}

		/// <summary>
		/// Returns true when <paramref name="text"/> ends a sentence.
		/// </summary>
		/// <param name="text">The text.</param>
		/// <returns>True when the last character is a terminal punctuation mark.</returns>
		public static bool EndsSentence(string text)
		{
			text = text.TrimEnd();
			if (text.Length == 0)
				return false;

			var c = text[text.Length - 1];
			if (c == '"' || c == '”' || c == ')' || c == '\'' || c == '’')
				c = text.Length > 1 ? text[text.Length - 2] : c;

			return c == '.' || c == '!' || c == '?' || c == ':' || c == ';' || c == '。';
		}

		// the most common gap between consecutive lines of a block.
		private static double TypicalSpacing(List<TextLine> lines)
		{
			if (lines.Count < 2)
				return lines.Count == 1 ? lines[0].FontSize * 0.3 : 3;

			var gaps = new List<double>();
			for (int i = 1; i < lines.Count; i++)
			{
				var gap = lines[i].Top - lines[i - 1].Bottom;
				if (gap > -lines[i].FontSize)
					gaps.Add(Math.Max(0, gap));
			}

			return gaps.Count > 0 ? LineBuilder.Median(gaps) : lines[0].FontSize * 0.3;
		}

		#endregion
	}
}
