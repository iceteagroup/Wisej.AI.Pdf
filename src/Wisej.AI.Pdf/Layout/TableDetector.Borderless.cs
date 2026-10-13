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

// Borderless table detection from the alignment of text, optionally bounded
// by horizontal rules ("booktabs" tables).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Layout
{
	/// <content>
	/// Borderless table detection from the alignment of text.
	/// </content>
	internal sealed partial class TableDetector
	{
		#region Nested types

		/// <summary>
		/// Line segments that share a baseline: a candidate row of a borderless table.
		/// </summary>
		private sealed class TextRow
		{
			/// <summary>The segments, left to right.</summary>
			public readonly List<TextLine> Segments = new List<TextLine>();

			/// <summary>The baseline.</summary>
			public double Baseline;

			/// <summary>The top edge.</summary>
			public double Top = double.MaxValue;

			/// <summary>The bottom edge.</summary>
			public double Bottom = double.MinValue;

			/// <summary>The font size.</summary>
			public double Size;

			/// <summary>The left edge of the first segment.</summary>
			public double Left => this.Segments[0].Left;

			/// <summary>The right edge of the last segment.</summary>
			public double Right => this.Segments[this.Segments.Count - 1].Right;
		}

		#endregion

		#region Detection

		/// <summary>
		/// Detects borderless tables in rule-bounded regions and in runs of aligned rows.
		/// </summary>
		/// <param name="lines">The lines of the page.</param>
		/// <param name="exclusions">Areas already taken by tables and figures.</param>
		/// <returns>The table blocks.</returns>
		private List<LayoutBlock> DetectBorderlessTables(List<TextLine> lines, List<LayoutBlock> exclusions)
		{
			var result = new List<LayoutBlock>();
			var rows = BuildTextRows(lines, exclusions);
			if (rows.Count < 2)
				return result;

			var used = new bool[rows.Count];

			// 1. regions bounded by horizontal rules of the same width (booktabs: top, middle and bottom rules).
			foreach (var (top, bottom, left, right) in FindRuleRegions())
			{
				var region = new List<TextRow>();
				var indexes = new List<int>();
				for (int i = 0; i < rows.Count; i++)
				{
					var row = rows[i];
					if (!used[i] && row.Top >= top - 2 && row.Bottom <= bottom + 2 && row.Left >= left - 5 && row.Right <= right + 5)
					{
						region.Add(row);
						indexes.Add(i);
					}
				}

				var table = BuildBorderlessTable(region, bounded: true);
				if (table != null)
				{
					table.Bounds = table.Bounds.Union(new PdfRect(left, top, right, bottom));
					table.Table.Bounds = table.Bounds;
					result.Add(table);
					foreach (var i in indexes)
						used[i] = true;
				}
			}

			// 2. runs of rows with several aligned segments.
			for (int i = 0; i < rows.Count;)
			{
				if (used[i] || rows[i].Segments.Count < 2)
				{
					i++;
					continue;
				}

				var run = new List<TextRow> { rows[i] };
				var j = i + 1;
				while (j < rows.Count && !used[j])
				{
					var previous = run[run.Count - 1];
					var row = rows[j];
					var size = Math.Max(previous.Size, row.Size);

					// rows of a table are close to each other.
					if (row.Top - previous.Bottom > size * 2.2)
						break;

					// single-segment rows are accepted only inside the horizontal extent of the run.
					if (row.Segments.Count < 2 && !FitsRun(run, row))
						break;

					run.Add(row);
					j++;
				}

				// trailing single-segment rows belong to the following text, not to the table.
				var end = j;
				while (run.Count > 0 && run[run.Count - 1].Segments.Count < 2)
				{
					run.RemoveAt(run.Count - 1);
					end--;
				}

				var table = BuildBorderlessTable(run, bounded: false);
				if (table != null)
				{
					result.Add(table);
					for (int k = i; k < end; k++)
						used[k] = true;

					i = Math.Max(end, i + 1);
				}
				else
				{
					i++;
				}
			}

			return result;
		}

		// groups the free lines by baseline, outside the excluded areas.
		private static List<TextRow> BuildTextRows(List<TextLine> lines, List<LayoutBlock> exclusions)
		{
			var free = lines.FindAll(l =>
			{
				if (l.IsConsumed)
					return false;

				foreach (var block in exclusions)
				{
					if (block.Bounds.Inflate(1).Contains(l.Bounds.CenterX, l.Bounds.CenterY))
						return false;
				}

				return true;
			});

			free.Sort((a, b) => a.Baseline.CompareTo(b.Baseline));

			var rows = new List<TextRow>();
			foreach (var line in free)
			{
				TextRow row = null;
				if (rows.Count > 0)
				{
					var last = rows[rows.Count - 1];
					if (Math.Abs(last.Baseline - line.Baseline) < Math.Min(last.Size, line.FontSize) * 0.4)
						row = last;
				}

				if (row == null)
				{
					row = new TextRow { Baseline = line.Baseline, Size = line.FontSize };
					rows.Add(row);
				}

				row.Segments.Add(line);
				row.Top = Math.Min(row.Top, line.Top);
				row.Bottom = Math.Max(row.Bottom, line.Bottom);
				row.Size = Math.Max(row.Size, line.FontSize);
			}

			foreach (var row in rows)
				row.Segments.Sort((a, b) => a.Left.CompareTo(b.Left));

			return rows;
		}

		// regions between the first and last of a stack of horizontal rules with the same extent.
		private List<(double Top, double Bottom, double Left, double Right)> FindRuleRegions()
		{
			var regions = new List<(double, double, double, double)>();
			var rules = _horizontalRules ?? new List<Edge>();
			rules.Sort((a, b) => a.Position.CompareTo(b.Position));

			var done = new bool[rules.Count];
			for (int i = 0; i < rules.Count; i++)
			{
				if (done[i])
					continue;

				var first = rules[i];
				var last = first;
				var count = 1;
				for (int j = i + 1; j < rules.Count; j++)
				{
					var rule = rules[j];
					if (Math.Abs(rule.Start - first.Start) < 6 && Math.Abs(rule.End - first.End) < 6 && rule.Position - last.Position < _height * 0.4)
					{
						last = rule;
						done[j] = true;
						count++;
					}
				}

				if (count >= 2 && last.Position - first.Position > 10)
					regions.Add((first.Position, last.Position, first.Start, first.End));
			}

			return regions;
		}

		// a single-segment row can join a table run when it lies within the horizontal extent of the run.
		private static bool FitsRun(List<TextRow> run, TextRow row)
		{
			double left = double.MaxValue, right = double.MinValue;
			foreach (var r in run)
			{
				left = Math.Min(left, r.Left);
				right = Math.Max(right, r.Right);
			}

			var segment = row.Segments[0];
			return segment.Left >= left - segment.FontSize && segment.Right <= right + segment.FontSize;
		}

		#endregion

		#region Model

		/// <summary>
		/// Validates a candidate region and builds its table.
		/// </summary>
		/// <param name="rows">The rows of the region.</param>
		/// <param name="bounded">True when the region is bounded by rules, which relaxes the evidence required.</param>
		/// <returns>The table block, or null when the rows don't form a table.</returns>
		private LayoutBlock BuildBorderlessTable(List<TextRow> rows, bool bounded)
		{
			if (rows.Count < 2)
				return null;

			var columns = FindColumns(rows);
			if (columns.Count < 2)
				return null;

			// a column of prose beside a short table (a second text column of the page) is not part of it.
			if (DropProseColumn(rows, columns, out rows))
			{
				if (columns.Count < 2 || rows.Count < 2)
					return null;
			}

			// evidence: rows with several cells, short cells (not two columns of prose).
			int multiRows = 0, cellCount = 0, wordCount = 0, longCells = 0;
			foreach (var row in rows)
			{
				if (row.Segments.Count >= 2)
					multiRows++;

				foreach (var segment in row.Segments)
				{
					cellCount++;
					wordCount += segment.Words.Count;
					if (segment.Words.Count > 8)
						longCells++;
				}
			}

			var averageWords = (double)wordCount / Math.Max(1, cellCount);
			if (!bounded)
			{
				if (averageWords > 6 && columns.Count < 4)
					return null;
				if (longCells * 3 > cellCount)
					return null;

				var minimumRows = columns.Count >= 3 ? 2 : 3;
				if (multiRows < minimumRows || multiRows * 10 < rows.Count * 6)
					return null;
			}
			else if (multiRows < 2 || longCells * 2 > cellCount)
			{
				return null;
			}

			var model = new TableModel { ColumnCount = columns.Count, IsRuled = false };
			var columnEdges = new double[columns.Count + 1];
			columnEdges[0] = columns[0].Left;
			for (int c = 1; c < columns.Count; c++)
				columnEdges[c] = (columns[c - 1].Right + columns[c].Left) / 2;
			columnEdges[columns.Count] = columns[columns.Count - 1].Right;
			model.ColumnEdges = columnEdges;

			var rowEdges = new List<double>();
			TableCellModel[] previousCells = null;
			TextRow previousRow = null;

			foreach (var row in rows)
			{
				// a row without text in the first column, tight under the previous row, continues its cells.
				var firstColumn = ColumnRange(columns, row.Segments[0]).First;
				var continuation = previousCells != null && firstColumn > 0 && row.Segments.Count < CountNonEmpty(previousCells)
					&& row.Top - previousRow.Bottom < row.Size * 0.5;

				TableCellModel[] cells;
				if (continuation)
				{
					cells = previousCells;
				}
				else
				{
					cells = new TableCellModel[columns.Count];
					rowEdges.Add(row.Top);
					model.RowCount++;
				}

				foreach (var segment in SplitByColumns(row.Segments, columns, Math.Max(3, row.Size * 0.5)))
				{
					var (first, last) = ColumnRange(columns, segment);
					var cell = cells[first];
					if (cell == null)
					{
						cell = new TableCellModel { Row = model.RowCount - 1, Column = first, ColumnSpan = last - first + 1 };
						cells[first] = cell;
						model.Cells.Add(cell);
					}
					else if (last - first + 1 > cell.ColumnSpan)
					{
						cell.ColumnSpan = last - first + 1;
					}

					cell.Lines.Add(segment);
				}

				foreach (var segment in row.Segments)
					segment.IsConsumed = true;

				previousCells = cells;
				previousRow = row;
			}

			rowEdges.Add(rows[rows.Count - 1].Bottom);
			model.RowEdges = rowEdges.ToArray();

			// cells hidden under a spanning cell are removed; the remaining positions stay empty.
			RemoveCoveredCells(model);
			MergeSparseHeaderRows(model);
			ApplyRuleSpans(model);

			var bounds = new PdfRect(columns[0].Left, rows[0].Top, columns[columns.Count - 1].Right, rows[rows.Count - 1].Bottom);
			model.Bounds = bounds;
			foreach (var cell in model.Cells)
			{
				cell.Bounds = new PdfRect(columnEdges[cell.Column], model.RowEdges[cell.Row],
					columnEdges[Math.Min(columns.Count, cell.Column + cell.ColumnSpan)], model.RowEdges[cell.Row + cell.RowSpan]);
				cell.Lines.Sort((a, b) => Math.Abs(a.Top - b.Top) > 1 ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));
			}

			DetectHeader(model);
			if (model.HeaderRowCount == 0 && bounded)
				DetectHeaderFromRules(model);

			return new LayoutBlock { Kind = LayoutBlockKind.Table, Table = model, Bounds = bounds };
		}

		// the number of cells created in a row.
		// splits segments that span several columns into one line per column, word by word.
		private static List<TextLine> SplitByColumns(List<TextLine> segments, List<(double Left, double Right)> columns, double minimumGap)
		{
			var result = new List<TextLine>();
			foreach (var segment in segments)
			{
				TextLine current = null;
				var currentColumn = -1;
				foreach (var word in segment.Words)
				{
						// a word wider than a column keeps the column of its start.
					var column = ColumnOfWord(columns, word.Bounds);

					// words separated by a normal space stay together: a header spanning columns is one cell.
					var gap = current == null ? double.MaxValue : word.Bounds.Left - current.Bounds.Right;
					if (current == null || (column != currentColumn && gap >= minimumGap * 0.8))
					{
						current = CloneLineHeader(segment);
						current.Bounds = word.Bounds;
						currentColumn = column;
						result.Add(current);
					}

					current.Words.Add(word);
					current.Bounds = current.Bounds.Union(word.Bounds);
				}
			}

			foreach (var line in result)
				line.InvalidateText();

			return result;
		}

		// the column that contains the start of a word.
		private static int ColumnOfWord(List<(double Left, double Right)> columns, PdfRect word)
		{
			for (int c = columns.Count - 1; c >= 0; c--)
			{
				if (word.Left >= columns[c].Left - 1)
					return c;
			}

			return 0;
		}

		/// <summary>
		/// Widens header cells over the columns covered by the partial rule drawn under them (booktabs "cmidrule").
		/// </summary>
		/// <param name="model">The table.</param>
		private void ApplyRuleSpans(TableModel model)
		{
			if (_horizontalRules == null || model.RowCount < 2)
				return;

			var edges = model.ColumnEdges;
			for (int r = 0; r < Math.Min(3, model.RowCount - 1); r++)
			{
				foreach (var cell in model.Cells.FindAll(c => c.Row == r && c.Lines.Count > 0))
				{
					var textBottom = double.MinValue;
					var center = 0.0;
					foreach (var line in cell.Lines)
					{
						textBottom = Math.Max(textBottom, line.Bottom);
						center += line.Bounds.CenterX;
					}

					center /= cell.Lines.Count;

					foreach (var rule in _horizontalRules)
					{
						// a rule right under the text, before the next row, centered on the cell.
						if (rule.Position < textBottom - 1 || rule.Position > model.RowEdges[r + 1] + 2 || center < rule.Start || center > rule.End)
							continue;

						// the columns whose centers lie under the rule.
						int first = -1, last = -1;
						for (int c = 0; c < model.ColumnCount; c++)
						{
							var middle = (edges[c] + edges[c + 1]) / 2;
							if (middle >= rule.Start - 2 && middle <= rule.End + 2)
							{
								if (first < 0)
									first = c;
								last = c;
							}
						}

						// a rule spanning the whole table is a header separator, not a group rule.
						if (first < 0 || last - first < 1 || (first == 0 && last == model.ColumnCount - 1))
							continue;

						// the other positions covered must be empty in this row.
						var free = true;
						for (int c = first; c <= last && free; c++)
						{
							var other = model.GetCell(r, c);
							free = other == null || other == cell || other.Lines.Count == 0;
						}

						if (!free || first > cell.Column || last < cell.Column + cell.ColumnSpan - 1)
							continue;

						model.Cells.RemoveAll(c => c != cell && c.Row == r && c.Column >= first && c.Column <= last && c.Lines.Count == 0);
						cell.Column = first;
						cell.ColumnSpan = last - first + 1;
						cell.Bounds = new PdfRect(edges[first], cell.Bounds.Top, edges[last + 1], cell.Bounds.Bottom);
						break;
					}
				}
			}
		}

		// a label centred between two header rows forms an almost empty row: it joins the row above.
		private static void MergeSparseHeaderRows(TableModel model)
		{
			for (int r = Math.Min(3, model.RowCount - 2); r >= 1; r--)
			{
				var cells = model.Cells.FindAll(c => c.Row == r);
				if (cells.Count != 1 || cells[0].ColumnSpan != 1)
					continue;

				// the position above must be free, and the row above must not be data (several numbers).
				var label = cells[0];
				if (model.GetCell(r - 1, label.Column) != null)
					continue;

				var above = model.Cells.FindAll(c => c.Row == r - 1);
				if (above.Count == 0 || above.Exists(c => IsNumeric(c.GetText())))
					continue;

				label.Row = r - 1;
				foreach (var cell in model.Cells)
				{
					if (cell.Row > r)
						cell.Row--;
				}

				var edges = new List<double>(model.RowEdges);
				edges.RemoveAt(r);
				model.RowEdges = edges.ToArray();
				model.RowCount--;
			}
		}

		// true when the text is a number (with separators, signs, units or percent).
		private static bool IsNumeric(string text)
		{
			var digits = 0;
			foreach (var c in text)
			{
				if (char.IsDigit(c))
					digits++;
				else if (char.IsLetter(c))
					return false;
			}

			return digits > 0;
		}

		/// <summary>
		/// Removes the first or last column when it holds long text while the others hold short values.
		/// </summary>
		/// <param name="rows">The rows of the region.</param>
		/// <param name="columns">The columns; the prose column is removed from the list.</param>
		/// <param name="remaining">Receives the rows without the segments of the removed column.</param>
		/// <returns>True when a column was removed.</returns>
		private static bool DropProseColumn(List<TextRow> rows, List<(double Left, double Right)> columns, out List<TextRow> remaining)
		{
			remaining = rows;

			// average number of words per segment in each column.
			var words = new double[columns.Count];
			var counts = new int[columns.Count];
			foreach (var row in rows)
			{
				foreach (var segment in row.Segments)
				{
					var column = ColumnRange(columns, segment).First;
					words[column] += segment.Words.Count;
					counts[column]++;
				}
			}

			int prose = -1;
			foreach (var candidate in new[] { 0, columns.Count - 1 })
			{
				var average = counts[candidate] > 0 ? words[candidate] / counts[candidate] : 0;
				if (average <= 6)
					continue;

				// the other columns must be short values.
				var othersShort = true;
				for (int c = 0; c < columns.Count; c++)
				{
					if (c != candidate && counts[c] > 0 && words[c] / counts[c] > 4)
						othersShort = false;
				}

				if (othersShort)
				{
					prose = candidate;
					break;
				}
			}

			if (prose < 0)
				return false;

			// rebuild the rows without the segments of the prose column.
			var dropped = columns[prose];
			columns.RemoveAt(prose);
			remaining = new List<TextRow>();
			foreach (var row in rows)
			{
				var copy = new TextRow { Baseline = row.Baseline, Size = row.Size };
				foreach (var segment in row.Segments)
				{
					var overlap = Math.Min(segment.Right, dropped.Right) - Math.Max(segment.Left, dropped.Left);
					if (overlap > segment.Bounds.Width * 0.5)
						continue;

					copy.Segments.Add(segment);
					copy.Top = Math.Min(copy.Top, segment.Top);
					copy.Bottom = Math.Max(copy.Bottom, segment.Bottom);
				}

				if (copy.Segments.Count > 0)
					remaining.Add(copy);
			}

			return true;
		}

		// the number of cells that are not null.
		private static int CountNonEmpty(TableCellModel[] cells)
		{
			var count = 0;
			foreach (var cell in cells)
			{
				if (cell != null)
					count++;
			}

			return count;
		}

		// columns from the whitespace projection: x positions not covered by (almost) any row separate columns.
		private static List<(double Left, double Right)> FindColumns(List<TextRow> rows)
		{
			double left = double.MaxValue, right = double.MinValue, size = 0;
			foreach (var row in rows)
			{
				left = Math.Min(left, row.Left);
				right = Math.Max(right, row.Right);
				size = Math.Max(size, row.Size);
			}

			var width = (int)Math.Ceiling(right - left) + 1;
			if (width <= 1 || width > 20000)
				return new List<(double, double)>();

			// coverage: number of rows with text at each x position (1 point resolution). Words are used rather than
			// segments: the gaps between columns line up across rows, the spaces between words don't.
			var coverage = new int[width];
			foreach (var row in rows)
			{
				var covered = new bool[width];
				foreach (var segment in row.Segments)
				{
					foreach (var word in segment.Words)
					{
						var x0 = Math.Max(0, (int)Math.Floor(word.Bounds.Left - left));
						var x1 = Math.Min(width - 1, (int)Math.Ceiling(word.Bounds.Right - left));
						for (int x = x0; x <= x1; x++)
							covered[x] = true;
					}
				}

				for (int x = 0; x < width; x++)
				{
					if (covered[x])
						coverage[x]++;
				}
			}

			// spanning cells (group headers) cross the gaps of a few rows: tolerate them.
			var tolerance = rows.Count >= 8 ? rows.Count / 4 : rows.Count >= 3 ? 1 : 0;
			var minimumGap = Math.Max(3, size * 0.5);

			var columns = new List<(double Left, double Right)>();
			var start = -1;
			var gapStart = -1;
			for (int x = 0; x <= width; x++)
			{
				var isText = x < width && coverage[x] > tolerance;
				if (isText)
				{
					if (start < 0)
					{
						start = x;
					}
					else if (gapStart >= 0 && x - gapStart < minimumGap)
					{
						// a narrow gap: still the same column.
					}
					else if (gapStart >= 0)
					{
						columns.Add((left + start, left + gapStart));
						start = x;
					}

					gapStart = -1;
				}
				else if (start >= 0 && gapStart < 0)
				{
					gapStart = x;
				}
			}

			if (start >= 0)
				columns.Add((left + start, left + (gapStart >= 0 ? gapStart : width)));

			// a real column has cells that start in it; otherwise the "gap" is just ragged text (short and long values).
			for (int c = columns.Count - 1; c > 0; c--)
			{
				var starts = 0;
				foreach (var row in rows)
				{
					foreach (var segment in row.Segments)
					{
						// a cell starts with the first word of a segment, or with a word after a wide gap.
						for (int w = 0; w < segment.Words.Count; w++)
						{
							var word = segment.Words[w].Bounds;
							var startsCell = w == 0 || word.Left - segment.Words[w - 1].Bounds.Right >= minimumGap * 0.8;
							if (startsCell && word.Left >= columns[c].Left - 1 && word.Left < columns[c].Right)
								starts++;
						}
					}
				}

				if (starts < 2)
				{
					columns[c - 1] = (columns[c - 1].Left, columns[c].Right);
					columns.RemoveAt(c);
				}
			}

			return columns;
		}

		// the range of columns a segment covers.
		private static (int First, int Last) ColumnRange(List<(double Left, double Right)> columns, TextLine segment)
		{
			var first = -1;
			var last = -1;
			for (int c = 0; c < columns.Count; c++)
			{
				var overlap = Math.Min(segment.Right, columns[c].Right) - Math.Max(segment.Left, columns[c].Left);
				var columnWidth = columns[c].Right - columns[c].Left;
				if (overlap > Math.Min(columnWidth * 0.3, segment.Bounds.Width * 0.25))
				{
					if (first < 0)
						first = c;
					last = c;
				}
			}

			if (first < 0)
			{
				first = last = NearestInterval(columns, segment.Bounds.CenterX);
			}

			return (first, last);
		}

		// removes the cells hidden by a cell spanning several columns, moving their text into it.
		private static void RemoveCoveredCells(TableModel model)
		{
			var cells = new List<TableCellModel>();
			foreach (var cell in model.Cells)
			{
				var covered = false;
				foreach (var other in model.Cells)
				{
					if (other != cell && other.Row == cell.Row && other.Column < cell.Column && other.Column + other.ColumnSpan > cell.Column)
					{
						// merge the covered cell's text into the spanning cell.
						other.Lines.AddRange(cell.Lines);
						covered = true;
						break;
					}
				}

				if (!covered)
					cells.Add(cell);
			}

			model.Cells = cells;
		}

		// the header ends at the first rule below the first row.
		private void DetectHeaderFromRules(TableModel model)
		{
			if (model.RowCount < 2 || _horizontalRules == null)
				return;

			var left = model.ColumnEdges[0];
			var right = model.ColumnEdges[model.ColumnCount];
			foreach (var rule in _horizontalRules)
			{
				if (rule.Start > left + 8 || rule.End < right - 8)
					continue;

				// a rule strictly inside the table separates the header rows from the body.
				for (int r = 1; r < model.RowCount; r++)
				{
					if (r <= 3 && rule.Position > model.RowEdges[r] - 12 && rule.Position < model.RowEdges[r] + 3)
					{
						SetHeaderRows(model, r);
						return;
					}
				}
			}
		}

		#endregion
	}
}
