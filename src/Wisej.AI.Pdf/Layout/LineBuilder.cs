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

// Groups glyphs into words and lines.

using System;
using System.Collections.Generic;
using System.Text;
using Wisej.AI.Pdf.Content;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// Builds words and lines from the glyphs of a page.
	/// </summary>
	/// <remarks>
	/// <para>The algorithm works in four steps:</para>
	/// <list type="number">
	/// <item>Clean-up: invisible and clipped glyphs are removed, overprinted duplicates (simulated bold) are merged,
	/// and marked-content sequences with /ActualText are replaced by their text.</item>
	/// <item>Chunks: glyphs are grouped in painting order into runs that continue on the same baseline.
	/// Producers almost always paint the characters of a line in order, which makes this step very reliable.</item>
	/// <item>Rows: chunks on the same baseline are merged left to right, except across <em>gutters</em>:
	/// vertical white strips that continue over several rows above or below, which separate columns and table cells.</item>
	/// <item>Words: glyphs are split at explicit spaces and at gaps wider than a threshold derived from the
	/// font's space width and the gap statistics of the line (robust to letter-spacing and justification).</item>
	/// </list>
	/// </remarks>
	internal sealed class LineBuilder
	{
		#region Nested types

		/// <summary>
		/// A glyph projected into the reading frame of its orientation.
		/// </summary>
		private sealed class FrameGlyph
		{
			/// <summary>The source glyph.</summary>
			public TextGlyph Glyph;

			/// <summary>The text (may differ from the glyph for ActualText replacements).</summary>
			public string Text;

			/// <summary>The left edge in the reading frame.</summary>
			public double X0;

			/// <summary>The right edge in the reading frame.</summary>
			public double X1;

			/// <summary>The top edge in the reading frame.</summary>
			public double Top;

			/// <summary>The bottom edge in the reading frame.</summary>
			public double Bottom;

			/// <summary>The baseline in the reading frame.</summary>
			public double Baseline;

			/// <summary>The font size.</summary>
			public double Size;

			/// <summary>True for whitespace glyphs, which only separate words.</summary>
			public bool IsSpace;

			/// <summary>True when the glyph was overprinted to simulate bold.</summary>
			public bool IsOverprinted;
		}

		/// <summary>
		/// A run of glyphs in painting order on the same baseline.
		/// </summary>
		private sealed class Chunk
		{
			/// <summary>The glyphs of the chunk.</summary>
			public readonly List<FrameGlyph> Glyphs = new List<FrameGlyph>();

			/// <summary>The left edge.</summary>
			public double X0 = double.MaxValue;

			/// <summary>The right edge.</summary>
			public double X1 = double.MinValue;

			/// <summary>The top edge.</summary>
			public double Top = double.MaxValue;

			/// <summary>The bottom edge.</summary>
			public double Bottom = double.MinValue;

			/// <summary>The baseline of the first glyph.</summary>
			public double Baseline;

			/// <summary>The largest font size.</summary>
			public double Size;

			/// <summary>Adds a glyph and grows the bounds.</summary>
			/// <param name="glyph">The glyph to add.</param>
			public void Add(FrameGlyph glyph)
			{
				if (this.Glyphs.Count == 0)
					this.Baseline = glyph.Baseline;

				this.Glyphs.Add(glyph);

				// spaces don't extend the chunk: trailing spaces would hide the gaps between columns.
				if (glyph.IsSpace)
					return;

				this.X0 = Math.Min(this.X0, glyph.X0);
				this.X1 = Math.Max(this.X1, glyph.X1);
				this.Top = Math.Min(this.Top, glyph.Top);
				this.Bottom = Math.Max(this.Bottom, glyph.Bottom);
				this.Size = Math.Max(this.Size, glyph.Size);
			}
		}

		/// <summary>
		/// Chunks that share a baseline.
		/// </summary>
		private sealed class Row
		{
			/// <summary>The chunks of the row, sorted left to right.</summary>
			public readonly List<Chunk> Chunks = new List<Chunk>();

			/// <summary>The baseline of the dominant chunk.</summary>
			public double Baseline;

			/// <summary>The font size of the dominant chunk.</summary>
			public double Size;

			/// <summary>The top edge.</summary>
			public double Top = double.MaxValue;

			/// <summary>The bottom edge.</summary>
			public double Bottom = double.MinValue;
		}

		#endregion

		// the page width in display space.
		private readonly double _width;
		// the page height in display space.
		private readonly double _height;

		/// <summary>
		/// Initializes a new instance of <see cref="LineBuilder"/>.
		/// </summary>
		/// <param name="pageWidth">The width of the page in display space.</param>
		/// <param name="pageHeight">The height of the page in display space.</param>
		public LineBuilder(double pageWidth, double pageHeight)
		{
			_width = pageWidth;
			_height = pageHeight;
		}

		#region Methods

		/// <summary>
		/// Builds the lines of a page.
		/// </summary>
		/// <param name="glyphs">The glyphs of the page in painting order.</param>
		/// <param name="links">The link annotations of the page; can be null.</param>
		/// <returns>The lines, for all the orientations.</returns>
		public List<TextLine> Build(List<TextGlyph> glyphs, List<PageLink> links)
		{
			var lines = new List<TextLine>();

			// group by orientation; each group is laid out in its own reading frame.
			var groups = new Dictionary<int, List<FrameGlyph>>();
			foreach (var frameGlyph in Prepare(glyphs))
			{
				var orientation = ReadingFrame.GetOrientation(frameGlyph.Glyph.Angle);
				if (orientation < 0)
					orientation = (int)(Math.Round(frameGlyph.Glyph.Angle / 90) * 90) % 360;

				if (!groups.TryGetValue(orientation, out var list))
					groups[orientation] = list = new List<FrameGlyph>();

				Project(frameGlyph, orientation);
				list.Add(frameGlyph);
			}

			foreach (var group in groups)
			{
				var chunks = BuildChunks(group.Value);
				var rows = BuildRows(chunks);
				foreach (var row in rows)
					SplitRow(row, rows, lines, group.Key);
			}

			if (links != null && links.Count > 0)
				ApplyLinks(lines, links);

			return lines;
		}

		#endregion

		#region Clean-up

		// removes invisible glyphs, merges overprinted duplicates and applies ActualText replacements.
		private static List<FrameGlyph> Prepare(List<TextGlyph> glyphs)
		{
			var result = new List<FrameGlyph>(glyphs.Count);

			// spatial hash of recent glyphs to detect overprinting (fake bold, shadows).
			var recent = new Dictionary<long, FrameGlyph>();

			for (int i = 0; i < glyphs.Count; i++)
			{
				var glyph = glyphs[i];
				if (glyph.IsClipped)
					continue;

				// replace a whole /ActualText sequence by its text, attached to the first glyph.
				if (glyph.ActualTextId > 0)
				{
					var id = glyph.ActualTextId;
					var bounds = glyph.Bounds;
					var last = glyph;
					while (i + 1 < glyphs.Count && glyphs[i + 1].ActualTextId == id)
					{
						i++;
						bounds = bounds.Union(glyphs[i].Bounds);
						last = glyphs[i];
					}

					if (string.IsNullOrEmpty(glyph.ActualText))
						continue;

					var replacement = new TextGlyph
					{
						Text = glyph.ActualText,
						Font = glyph.Font,
						X = glyph.X,
						Y = glyph.Y,
						EndX = last.EndX,
						EndY = last.EndY,
						FontSize = glyph.FontSize,
						Angle = glyph.Angle,
						SpaceWidth = glyph.SpaceWidth,
						Color = glyph.Color,
						RenderMode = glyph.RenderMode,
						IsBold = glyph.IsBold,
						IsItalic = glyph.IsItalic,
						Bounds = bounds,
						Index = glyph.Index,
						Mcid = glyph.Mcid,
						StructParent = glyph.StructParent,
						IsArtifact = glyph.IsArtifact,
						IsAnnotation = glyph.IsAnnotation,
					};

					result.Add(new FrameGlyph { Glyph = replacement, Text = replacement.Text });
					continue;
				}

				if (glyph.Text.Length == 0 || glyph.FontSize <= 0.1)
					continue;

				var isSpace = string.IsNullOrWhiteSpace(glyph.Text);

				// overprinted duplicates: same text at (almost) the same position.
				if (!isSpace)
				{
					var tolerance = Math.Max(0.5, glyph.FontSize * 0.15);
					var key = SpatialKey(glyph.X, glyph.Y, tolerance);
					if (recent.TryGetValue(key, out var previous)
						&& previous.Text == glyph.Text
						&& Math.Abs(previous.Glyph.X - glyph.X) <= tolerance
						&& Math.Abs(previous.Glyph.Y - glyph.Y) <= tolerance)
					{
						previous.IsOverprinted = true;
						continue;
					}

					var frameGlyph = new FrameGlyph { Glyph = glyph, Text = glyph.Text };
					recent[key] = frameGlyph;
					result.Add(frameGlyph);
				}
				else
				{
					result.Add(new FrameGlyph { Glyph = glyph, Text = " ", IsSpace = true });
				}
			}

			return result;
		}

		// a hash key of the grid cell that contains a position, for the overprint detection.
		private static long SpatialKey(double x, double y, double cell)
		{
			var cx = (long)Math.Floor(x / (cell * 2));
			var cy = (long)Math.Floor(y / (cell * 2));
			return (cx << 32) ^ (cy & 0xFFFFFFFF);
		}

		// projects the glyph into the reading frame of its orientation.
		private void Project(FrameGlyph g, int orientation)
		{
			var glyph = g.Glyph;
			var origin = ReadingFrame.ToFrame(orientation, glyph.X, glyph.Y, _width, _height);
			var end = ReadingFrame.ToFrame(orientation, glyph.EndX, glyph.EndY, _width, _height);
			var box = ReadingFrame.ToFrame(orientation, glyph.Bounds, _width, _height);

			g.X0 = Math.Min(origin.X, end.X);
			g.X1 = Math.Max(origin.X, end.X);
			g.Baseline = origin.Y;
			g.Size = glyph.FontSize;

			// the vertical extent comes from the glyph box; ensure a minimum height for degenerate fonts.
			g.Top = Math.Min(box.Top, g.Baseline - glyph.FontSize * 0.5);
			g.Bottom = Math.Max(box.Bottom, g.Baseline);

			// zero-width glyphs (combining marks, broken widths) still occupy a little space.
			if (g.X1 - g.X0 < 0.01)
				g.X1 = g.X0 + Math.Max(0.01, Math.Min(box.Width, glyph.FontSize * 0.5));
		}

		#endregion

		#region Chunks and rows

		// groups consecutive glyphs (painting order) that continue the same baseline.
		private static List<Chunk> BuildChunks(List<FrameGlyph> glyphs)
		{
			var chunks = new List<Chunk>();
			Chunk current = null;
			FrameGlyph previous = null;
			FrameGlyph lastInk = null;

			foreach (var g in glyphs)
			{
				var continues = false;
				if (current != null && previous != null)
				{
					var size = Math.Max(g.Size, previous.Size);
					var sameBaseline = Math.Abs(g.Baseline - previous.Baseline) < size * 0.25;
					var forward = g.X0 >= previous.X1 - size * 0.6;

					// the distance is measured from the last visible glyph: runs of spaces must not bridge columns.
					var reference = g.IsSpace || lastInk == null ? previous : lastInk;
					var near = g.X0 - reference.X1 < size * 1.2;
					continues = sameBaseline && forward && near;
				}

				if (!continues)
				{
					current = new Chunk();
					chunks.Add(current);
				}

				current.Add(g);
				previous = g;
				if (!g.IsSpace)
					lastInk = g;
			}

			// chunks made only of spaces carry no information.
			chunks.RemoveAll(c => c.Glyphs.TrueForAll(g => g.IsSpace));
			return chunks;
		}

		// groups chunks into rows by baseline; small raised or lowered chunks join the row they overlap.
		private static List<Row> BuildRows(List<Chunk> chunks)
		{
			// largest fonts first, so superscripts attach to existing rows rather than forming their own.
			chunks.Sort((a, b) =>
			{
				var c = b.Size.CompareTo(a.Size);
				return c != 0 ? c : a.Baseline.CompareTo(b.Baseline);
			});

			var rows = new List<Row>();
			foreach (var chunk in chunks)
			{
				Row best = null;
				var bestDistance = double.MaxValue;

				foreach (var row in rows)
				{
					var distance = Math.Abs(row.Baseline - chunk.Baseline);
					var sameBaseline = distance < Math.Min(row.Size, chunk.Size) * 0.3;

					// scripts: a smaller chunk whose box lies mostly within the row band.
					var isScript = false;
					if (!sameBaseline && chunk.Size < row.Size * 0.9)
					{
						var overlap = Math.Min(row.Bottom, chunk.Bottom) - Math.Max(row.Top, chunk.Top);
						var chunkHeight = chunk.Bottom - chunk.Top;
						isScript = chunkHeight > 0 && overlap >= chunkHeight * 0.5 && distance < row.Size * 0.6
							&& !OverlapsHorizontally(row, chunk);
					}

					if ((sameBaseline || isScript) && distance < bestDistance)
					{
						best = row;
						bestDistance = distance;
					}
				}

				if (best == null)
				{
					best = new Row { Baseline = chunk.Baseline, Size = chunk.Size };
					rows.Add(best);
				}

				best.Chunks.Add(chunk);
				best.Top = Math.Min(best.Top, chunk.Top);
				best.Bottom = Math.Max(best.Bottom, chunk.Bottom);
			}

			foreach (var row in rows)
				row.Chunks.Sort((a, b) => a.X0.CompareTo(b.X0));

			rows.Sort((a, b) => a.Baseline.CompareTo(b.Baseline));
			return rows;
		}

		// a script must not sit on top of existing text in the row (that would be a different line).
		private static bool OverlapsHorizontally(Row row, Chunk chunk)
		{
			foreach (var other in row.Chunks)
			{
				var overlap = Math.Min(other.X1, chunk.X1) - Math.Max(other.X0, chunk.X0);
				if (overlap > (chunk.X1 - chunk.X0) * 0.5)
					return true;
			}

			return false;
		}

		// splits a row into lines at gutters, and builds the words of each line.
		private void SplitRow(Row row, List<Row> rows, List<TextLine> lines, int orientation)
		{
			var index = rows.IndexOf(row);
			var segment = new List<Chunk> { row.Chunks[0] };

			for (int i = 1; i < row.Chunks.Count; i++)
			{
				var previous = segment[segment.Count - 1];
				var chunk = row.Chunks[i];
				var size = Math.Max(previous.Size, chunk.Size);
				var gap = chunk.X0 - SegmentRight(segment);

				var split = gap > size * 3
					|| (gap > size * 0.6 && IsGutter(rows, index, SegmentRight(segment), chunk.X0, size));

				if (split)
				{
					lines.Add(CreateLine(segment, orientation));
					segment = new List<Chunk>();
				}

				segment.Add(chunk);
			}

			lines.Add(CreateLine(segment, orientation));
		}

		// the right edge of a run of chunks.
		private static double SegmentRight(List<Chunk> segment)
		{
			var right = double.MinValue;
			foreach (var chunk in segment)
				right = Math.Max(right, chunk.X1);

			return right;
		}

		// a gap is a gutter when the same vertical strip is empty in the neighbouring rows above or below.
		private static bool IsGutter(List<Row> rows, int index, double left, double right, double size)
		{
			// test only the middle of the gap: centered or right-aligned cells in the adjacent rows protrude into it.
			var margin = Math.Max(size * 0.15, (right - left) * 0.3);
			var stripLeft = left + margin;
			var stripRight = right - margin;
			if (stripRight <= stripLeft)
				return false;

			var above = CountEmpty(rows, index, -1, stripLeft, stripRight, size);
			var below = CountEmpty(rows, index, 1, stripLeft, stripRight, size);

			// two rows on one side, or one on each side, is enough evidence of an aligned white strip.
			return above >= 2 || below >= 2 || (above >= 1 && below >= 1);
		}

		/// <summary>
		/// Counts the consecutive rows above or below that leave the strip empty and have text on both sides of it.
		/// </summary>
		/// <param name="rows">The rows.</param>
		/// <param name="index">The index of the current row.</param>
		/// <param name="direction">-1 for the rows above, 1 for the rows below.</param>
		/// <param name="left">The left edge of the strip.</param>
		/// <param name="right">The right edge of the strip.</param>
		/// <param name="size">The font size, which bounds the vertical distance considered.</param>
		/// <returns>The number of rows (at most 3).</returns>
		private static int CountEmpty(List<Row> rows, int index, int direction, double left, double right, double size)
		{
			var count = 0;
			var lastBaseline = rows[index].Baseline;

			for (int i = index + direction; i >= 0 && i < rows.Count && count < 3; i += direction)
			{
				var row = rows[i];

				// only consider rows that are close: a big vertical gap ends the evidence.
				if (Math.Abs(row.Baseline - lastBaseline) > size * 3)
					break;

				// the row must have text on both sides of the strip to count as evidence.
				var hasLeft = false;
				var hasRight = false;
				var crosses = false;
				foreach (var chunk in row.Chunks)
				{
					if (chunk.X1 <= left)
						hasLeft = true;
					else if (chunk.X0 >= right)
						hasRight = true;
					else
						crosses = true;
				}

				if (crosses)
					break;

				if (hasLeft && hasRight)
					count++;

				lastBaseline = row.Baseline;
			}

			return count;
		}

		#endregion

		#region Lines and words

		/// <summary>
		/// Creates a line from chunks: sorts the glyphs, splits the words and computes the metrics and style.
		/// </summary>
		/// <param name="chunks">The chunks of the line.</param>
		/// <param name="orientation">The orientation of the reading frame.</param>
		/// <returns>The line.</returns>
		private TextLine CreateLine(List<Chunk> chunks, int orientation)
		{
			// collect the glyphs left to right.
			var glyphs = new List<FrameGlyph>();
			Chunk main = chunks[0];
			foreach (var chunk in chunks)
			{
				glyphs.AddRange(chunk.Glyphs);
				if (chunk.Glyphs.Count > main.Glyphs.Count)
					main = chunk;
			}

			// stable sort by position: painting order breaks ties (e.g. accents drawn over letters).
			glyphs.Sort((a, b) =>
			{
				var c = a.X0.CompareTo(b.X0);
				return c != 0 ? c : a.Glyph.Index.CompareTo(b.Glyph.Index);
			});

			var line = new TextLine { Orientation = orientation };
			var lineSize = DominantSize(glyphs);
			var lineBaseline = main.Baseline;
			var threshold = GetSpaceThreshold(glyphs, lineSize);

			TextWord word = null;
			FrameGlyph previous = null;
			var sb = new StringBuilder();

			foreach (var g in glyphs)
			{
				if (g.IsSpace)
				{
					if (word != null)
						word.SpaceAfter = true;

					FinishWord(word, sb, line, lineSize, lineBaseline);
					word = null;
					previous = g;
					continue;
				}

				if (word != null && previous != null && !previous.IsSpace)
				{
					var gap = g.X0 - previous.X1;
					var scriptChange = IsScript(g, lineSize, lineBaseline) != IsScript(previous, lineSize, lineBaseline);
					if (gap > threshold || (scriptChange && gap > -0.01))
					{
						word.SpaceAfter = gap > threshold;
						FinishWord(word, sb, line, lineSize, lineBaseline);
						word = null;
					}
				}

				if (word == null)
					word = new TextWord();

				word.Glyphs.Add(g.Glyph);
				sb.Append(g.Text);
				if (g.IsOverprinted)
					g.Glyph.IsBold = true;

				previous = g;
			}

			FinishWord(word, sb, line, lineSize, lineBaseline);

			// line metrics.
			var left = double.MaxValue;
			var top = double.MaxValue;
			var right = double.MinValue;
			var bottom = double.MinValue;
			foreach (var g in glyphs)
			{
				if (g.IsSpace)
					continue;

				left = Math.Min(left, g.X0);
				right = Math.Max(right, g.X1);
				top = Math.Min(top, g.Top);
				bottom = Math.Max(bottom, g.Bottom);
			}

			line.Bounds = new PdfRect(left, top, right, bottom);
			line.DisplayBounds = ReadingFrame.ToDisplay(orientation, line.Bounds, _width, _height);
			line.Baseline = lineBaseline;
			line.FontSize = lineSize;
			line.Order = int.MaxValue;
			foreach (var g in glyphs)
				line.Order = Math.Min(line.Order, g.Glyph.Index);

			ApplyBidi(line);
			SetLineStyle(line);
			return line;
		}

		/// <summary>
		/// Converts a right-to-left line from visual to logical order.
		/// </summary>
		/// <param name="line">The line, whose words are in visual order (left to right on the page).</param>
		/// <remarks>
		/// A simplified version of the Unicode bidirectional algorithm, sufficient for text extraction:
		/// in lines where Hebrew or Arabic letters dominate, the order of the words is reversed and the
		/// characters of right-to-left words are reversed; numbers and Latin words keep their order.
		/// </remarks>
		private static void ApplyBidi(TextLine line)
		{
			int rtl = 0, ltr = 0;
			foreach (var word in line.Words)
			{
				foreach (var c in word.Text)
				{
					if (IsRightToLeft(c))
						rtl++;
					else if (char.IsLetter(c))
						ltr++;
				}
			}

			if (rtl == 0 || rtl <= ltr)
				return;

			line.Words.Reverse();

			// the space after each word moves to the word that now precedes the gap.
			for (int i = 0; i < line.Words.Count - 1; i++)
				line.Words[i].SpaceAfter = line.Words[i + 1].SpaceAfter;

			if (line.Words.Count > 0)
				line.Words[line.Words.Count - 1].SpaceAfter = false;

			foreach (var word in line.Words)
			{
				var hasRtl = false;
				foreach (var c in word.Text)
				{
					if (IsRightToLeft(c))
					{
						hasRtl = true;
						break;
					}
				}

				if (!hasRtl)
					continue;

				// reverse glyph by glyph: a ligature glyph maps to several letters that are already in logical order.
				var sb = new StringBuilder(word.Text.Length);
				for (int g = word.Glyphs.Count - 1; g >= 0; g--)
					sb.Append(word.Glyphs[g].Text);

				var reversed = sb.ToString();
				if (reversed.Length != word.Text.Length)
				{
					// the text doesn't come from the glyphs (ActualText): reverse the characters.
					var chars = word.Text.ToCharArray();
					Array.Reverse(chars);
					reversed = new string(chars);
				}

				word.Text = MirrorBrackets(reversed);
			}

			line.InvalidateText();
		}

		// Hebrew, Arabic, Syriac, Thaana and N'Ko letters, and the Arabic presentation forms.
		private static bool IsRightToLeft(char c)
		{
			return (c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF');
		}

		// brackets reversed with the text must be mirrored: "(" in visual order is ")" in logical order.
		private static string MirrorBrackets(string text)
		{
			var chars = text.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
			{
				switch (chars[i])
				{
					case '(': chars[i] = ')'; break;
					case ')': chars[i] = '('; break;
					case '[': chars[i] = ']'; break;
					case ']': chars[i] = '['; break;
					case '<': chars[i] = '>'; break;
					case '>': chars[i] = '<'; break;
				}
			}

			return new string(chars);
		}

		// the word gap threshold: half a space, bounded by the font size, adapted to letter-spaced lines.
		private static double GetSpaceThreshold(List<FrameGlyph> glyphs, double size)
		{
			var spaceWidths = new List<double>();
			var gaps = new List<double>();
			for (int i = 0; i < glyphs.Count; i++)
			{
				var g = glyphs[i];
				if (g.IsSpace)
					continue;

				if (g.Glyph.SpaceWidth > 0)
					spaceWidths.Add(g.Glyph.SpaceWidth);

				if (i > 0 && !glyphs[i - 1].IsSpace)
					gaps.Add(g.X0 - glyphs[i - 1].X1);
			}

			var spaceWidth = spaceWidths.Count > 0 ? Median(spaceWidths) : size * 0.25;
			var threshold = Math.Max(size * 0.12, Math.Min(spaceWidth * 0.5, size * 0.3));

			// letter-spaced text: most gaps are above the threshold and similar to each other.
			if (gaps.Count >= 6)
			{
				var median = Median(gaps);
				if (median > threshold * 0.8)
					threshold = Math.Max(threshold, median + Math.Max(spaceWidth * 0.4, size * 0.1));
			}

			return threshold;
		}

		// true when the glyph is a superscript or subscript relative to the line.
		private static bool IsScript(FrameGlyph g, double lineSize, double baseline)
		{
			return g.Size < lineSize * 0.85 && Math.Abs(g.Baseline - baseline) > lineSize * 0.12;
		}

		/// <summary>
		/// Completes a word: text, bounds, style and script position, and adds it to the line.
		/// </summary>
		/// <param name="word">The word, or null.</param>
		/// <param name="sb">The text of the word; cleared.</param>
		/// <param name="line">The line.</param>
		/// <param name="lineSize">The dominant font size of the line.</param>
		/// <param name="lineBaseline">The baseline of the line.</param>
		private void FinishWord(TextWord word, StringBuilder sb, TextLine line, double lineSize, double lineBaseline)
		{
			if (word == null || word.Glyphs.Count == 0)
			{
				sb.Clear();
				return;
			}

			word.Text = sb.ToString();
			sb.Clear();

			var first = word.Glyphs[0];
			word.FontSize = first.FontSize;
			word.Baseline = BaselineOf(word, line);

			// word bounds in the reading frame.
			var bounds = ReadingFrame.ToFrame(line.Orientation, first.Bounds, _width, _height);
			for (int i = 1; i < word.Glyphs.Count; i++)
				bounds = bounds.Union(ReadingFrame.ToFrame(line.Orientation, word.Glyphs[i].Bounds, _width, _height));
			word.Bounds = bounds;

			var bold = 0;
			var italic = 0;
			var mono = 0;
			foreach (var glyph in word.Glyphs)
			{
				// invisible text (the OCR layer of scans) uses placeholder fonts: its style means nothing.
				if (glyph.IsInvisible)
					continue;

				if (glyph.IsBold)
					bold++;
				if (glyph.IsItalic)
					italic++;
				if (glyph.Font != null && glyph.Font.IsFixedPitch)
					mono++;
			}

			word.IsBold = bold * 2 > word.Glyphs.Count;
			word.IsItalic = italic * 2 > word.Glyphs.Count;
			word.IsMonospace = mono * 2 > word.Glyphs.Count;

			// superscripts and subscripts: smaller and shifted from the line baseline.
			if (word.FontSize < lineSize * 0.85)
			{
				var shift = lineBaseline - BaselineOf(word, line);
				if (shift > lineSize * 0.12)
					word.IsSuperscript = true;
				else if (shift < -lineSize * 0.08)
					word.IsSubscript = true;
			}
			else if (first.Rise > lineSize * 0.15)
			{
				word.IsSuperscript = true;
			}
			else if (first.Rise < -lineSize * 0.1)
			{
				word.IsSubscript = true;
			}

			line.Words.Add(word);
		}

		// the baseline of a word in the reading frame of the line.
		private double BaselineOf(TextWord word, TextLine line)
		{
			var glyph = word.Glyphs[0];
			return ReadingFrame.ToFrame(line.Orientation, glyph.X, glyph.Y, _width, _height).Y;
		}

		// the font size used by most glyphs, rounded to half points.
		private static double DominantSize(List<FrameGlyph> glyphs)
		{
			// the size used by most characters, rounded to half points.
			var counts = new Dictionary<int, int>();
			var best = 0;
			var bestCount = 0;
			foreach (var g in glyphs)
			{
				if (g.IsSpace)
					continue;

				var key = (int)Math.Round(g.Size * 2);
				counts.TryGetValue(key, out var count);
				counts[key] = ++count;
				if (count > bestCount || (count == bestCount && key > best))
				{
					best = key;
					bestCount = count;
				}
			}

			return best > 0 ? best / 2.0 : 10;
		}

		// sets the bold, italic, monospace, artifact, colour and family of the line from its words.
		private static void SetLineStyle(TextLine line)
		{
			int total = 0, bold = 0, italic = 0, mono = 0, artifacts = 0;
			var colors = new Dictionary<int, int>();
			var families = new Dictionary<string, int>();

			foreach (var word in line.Words)
			{
				var length = word.Text.Length;
				total += length;
				if (word.IsBold)
					bold += length;
				if (word.IsItalic)
					italic += length;
				if (word.IsMonospace)
					mono += length;

				foreach (var glyph in word.Glyphs)
				{
					if (glyph.IsArtifact)
						artifacts++;

					colors.TryGetValue(glyph.Color, out var c);
					colors[glyph.Color] = c + 1;

					var family = glyph.Font?.FamilyName ?? string.Empty;
					families.TryGetValue(family, out var f);
					families[family] = f + 1;
				}
			}

			line.IsBold = total > 0 && bold * 10 >= total * 6;
			line.IsItalic = total > 0 && italic * 10 >= total * 6;
			line.IsMonospace = total > 0 && mono * 10 >= total * 8;

			var glyphCount = 0;
			foreach (var word in line.Words)
				glyphCount += word.Glyphs.Count;

			line.IsArtifact = glyphCount > 0 && artifacts == glyphCount;
			line.Color = MaxKey(colors);
			line.FontFamily = MaxKey(families);
		}

		// the key with the highest count.
		private static T MaxKey<T>(Dictionary<T, int> counts)
		{
			var best = default(T);
			var bestCount = -1;
			foreach (var entry in counts)
			{
				if (entry.Value > bestCount)
				{
					best = entry.Key;
					bestCount = entry.Value;
				}
			}

			return best;
		}

		/// <summary>
		/// Returns the median of a list of values (the list is sorted in place).
		/// </summary>
		/// <param name="values">The values; must not be empty.</param>
		/// <returns>The median.</returns>
		public static double Median(List<double> values)
		{
			values.Sort();
			var middle = values.Count / 2;
			return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
		}

		#endregion

		#region Links

		// assigns link targets to the words covered by link annotations.
		private static void ApplyLinks(List<TextLine> lines, List<PageLink> links)
		{
			foreach (var line in lines)
			{
				foreach (var word in line.Words)
				{
					var bounds = word.Glyphs[0].Bounds;
					for (int i = 1; i < word.Glyphs.Count; i++)
						bounds = bounds.Union(word.Glyphs[i].Bounds);

					foreach (var link in links)
					{
						if (bounds.CoverageBy(link.Bounds.Inflate(1)) > 0.5)
						{
							word.LinkUri = link.Uri;
							word.LinkPage = link.PageNumber;
							break;
						}
					}
				}
			}
		}

		#endregion
	}
}
