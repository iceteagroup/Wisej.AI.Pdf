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

// Refinement of ruled tables with the alignment of their text: rows and
// columns that are not separated by rules ("booktabs" style, grouped rows).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Layout
{
	/// <content>
	/// Refinement of ruled tables with the alignment of their text.
	/// </content>
	internal sealed partial class TableDetector
	{
		#region Columns

		/// <summary>
		/// Splits ruled columns that contain several text columns aligned across rows.
		/// </summary>
		/// <param name="model">The table to refine.</param>
		/// <remarks>
		/// Many tables draw rules only around groups of columns. Within a ruled column, line segments
		/// separated by a gap on the same baseline, and whose positions line up across rows, are separate columns.
		/// </remarks>
		private static void SubdivideColumns(TableModel model)
		{
			for (int c = model.ColumnCount - 1; c >= 0; c--)
			{
				var cells = model.Cells.FindAll(cell => cell.Column == c && cell.ColumnSpan == 1 && cell.Lines.Count > 0);
				if (cells.Count < 2)
					continue;

				// evidence: rows with two or more segments on the same baseline.
				var multiple = 0;
				var intervals = new List<(double Left, double Right)>();
				foreach (var cell in cells)
				{
					if (HasSegmentsOnSameBaseline(cell.Lines))
						multiple++;

					foreach (var line in cell.Lines)
						intervals.Add((line.Bounds.Left, line.Bounds.Right));
				}

				if (multiple < 2)
					continue;

				var subColumns = MergeIntervals(intervals, 1);
				if (subColumns.Count < 2)
					continue;

				SplitColumn(model, c, subColumns);
			}
		}

		// true when two of the lines share a baseline: the cell holds side-by-side values.
		private static bool HasSegmentsOnSameBaseline(List<TextLine> lines)
		{
			for (int i = 0; i < lines.Count; i++)
			{
				for (int j = i + 1; j < lines.Count; j++)
				{
					if (Math.Abs(lines[i].Baseline - lines[j].Baseline) < Math.Min(lines[i].FontSize, lines[j].FontSize) * 0.4)
						return true;
				}
			}

			return false;
		}

		// merges overlapping intervals (with a tolerance); returns them sorted.
		private static List<(double Left, double Right)> MergeIntervals(List<(double Left, double Right)> intervals, double tolerance)
		{
			intervals.Sort((a, b) => a.Left.CompareTo(b.Left));
			var merged = new List<(double Left, double Right)>();
			foreach (var interval in intervals)
			{
				if (merged.Count > 0 && interval.Left <= merged[merged.Count - 1].Right + tolerance)
				{
					var last = merged[merged.Count - 1];
					merged[merged.Count - 1] = (last.Left, Math.Max(last.Right, interval.Right));
				}
				else
				{
					merged.Add(interval);
				}
			}

			return merged;
		}

		/// <summary>
		/// Splits a column into several columns at the gaps between its text columns.
		/// </summary>
		/// <param name="model">The table.</param>
		/// <param name="column">The column to split.</param>
		/// <param name="subColumns">The horizontal extents of the text columns.</param>
		private static void SplitColumn(TableModel model, int column, List<(double Left, double Right)> subColumns)
		{
			var m = subColumns.Count;

			// new edges: midpoints of the gaps between the text columns.
			var edges = new List<double>(model.ColumnEdges);
			var inserted = new double[m - 1];
			for (int i = 0; i < m - 1; i++)
				inserted[i] = (subColumns[i].Right + subColumns[i + 1].Left) / 2;
			edges.InsertRange(column + 1, inserted);

			var cells = new List<TableCellModel>();
			foreach (var cell in model.Cells)
			{
				if (cell.Column == column && cell.ColumnSpan == 1)
				{
					cells.AddRange(SplitCellHorizontally(cell, subColumns));
				}
				else if (cell.Column <= column && cell.Column + cell.ColumnSpan > column)
				{
					cell.ColumnSpan += m - 1;
					cells.Add(cell);
				}
				else
				{
					if (cell.Column > column)
						cell.Column += m - 1;

					cells.Add(cell);
				}
			}

			model.Cells = cells;
			model.ColumnCount += m - 1;
			model.ColumnEdges = edges.ToArray();
		}

		// distributes the lines of a cell over the sub-columns; a line covering several sub-columns makes a spanning cell.
		private static List<TableCellModel> SplitCellHorizontally(TableCellModel cell, List<(double Left, double Right)> subColumns)
		{
			var m = subColumns.Count;
			var owner = new int[m];
			for (int i = 0; i < m; i++)
				owner[i] = i;

			// the sub-column range of each line; overlapping ranges are merged into one group.
			var ranges = new List<(int First, int Last, TextLine Line)>();
			foreach (var line in cell.Lines)
			{
				var first = -1;
				var last = -1;
				for (int i = 0; i < m; i++)
				{
					var overlap = Math.Min(line.Bounds.Right, subColumns[i].Right) - Math.Max(line.Bounds.Left, subColumns[i].Left);
					if (overlap > 0)
					{
						if (first < 0)
							first = i;
						last = i;
					}
				}

				if (first < 0)
					first = last = NearestInterval(subColumns, line.Bounds.CenterX);

				ranges.Add((first, last, line));
				for (int i = first + 1; i <= last; i++)
					owner[i] = owner[first];
			}

			// one cell per group of sub-columns.
			var result = new List<TableCellModel>();
			for (int i = 0; i < m;)
			{
				var j = i;
				while (j + 1 < m && owner[j + 1] == owner[i])
					j++;

				var part = new TableCellModel
				{
					Row = cell.Row,
					RowSpan = cell.RowSpan,
					Column = cell.Column + i,
					ColumnSpan = j - i + 1,
					IsHeader = cell.IsHeader,
					Bounds = new PdfRect(i == 0 ? cell.Bounds.Left : subColumns[i].Left, cell.Bounds.Top, j == m - 1 ? cell.Bounds.Right : subColumns[j].Right, cell.Bounds.Bottom),
				};

				foreach (var range in ranges)
				{
					if (range.First >= i && range.First <= j)
						part.Lines.Add(range.Line);
				}

				result.Add(part);
				i = j + 1;
			}

			return result;
		}

		// the index of the interval closest to x.
		private static int NearestInterval(List<(double Left, double Right)> intervals, double x)
		{
			var best = 0;
			var bestDistance = double.MaxValue;
			for (int i = 0; i < intervals.Count; i++)
			{
				var distance = x < intervals[i].Left ? intervals[i].Left - x : x > intervals[i].Right ? x - intervals[i].Right : 0;
				if (distance < bestDistance)
				{
					best = i;
					bestDistance = distance;
				}
			}

			return best;
		}

		#endregion

		#region Rows

		/// <summary>
		/// Splits ruled rows that contain several text rows aligned across cells.
		/// </summary>
		/// <param name="model">The table to refine.</param>
		/// <remarks>
		/// When two or more cells of a row hold the same number of lines on shared baselines, each baseline is
		/// a row of its own (rules were drawn only around groups of rows). Single-line cells of the row become
		/// cells spanning the new rows, which is how group labels are laid out.
		/// </remarks>
		private static void SubdivideRows(TableModel model)
		{
			for (int r = model.RowCount - 1; r >= 0; r--)
			{
				var cells = model.Cells.FindAll(cell => cell.Row == r && cell.RowSpan == 1 && cell.Lines.Count > 0);
				if (cells.Count < 2)
					continue;

				// bold rows are headers: their extra lines are wrapped text, not separate rows.
				var boldCells = cells.FindAll(cell => cell.Lines.TrueForAll(line => line.IsBold));
				if (boldCells.Count * 2 >= cells.Count)
					continue;

				// the baselines of the row, clustered.
				var baselines = new List<(double Baseline, double Size)>();
				foreach (var cell in cells)
				{
					foreach (var line in cell.Lines)
						baselines.Add((line.Baseline, line.FontSize));
				}

				var groups = ClusterBaselines(baselines);
				if (groups.Count < 2)
					continue;

				// at least two cells must have one line on each of several baselines.
				var multiple = 0;
				foreach (var cell in cells)
				{
					if (CountGroups(cell.Lines, groups) >= 2)
						multiple++;
				}

				if (multiple < 2 || multiple * 2 < cells.Count)
					continue;

				SplitRow(model, r, groups);
			}
		}

		// clusters close baselines; returns one baseline per cluster, top to bottom.
		private static List<double> ClusterBaselines(List<(double Baseline, double Size)> baselines)
		{
			baselines.Sort((a, b) => a.Baseline.CompareTo(b.Baseline));
			var groups = new List<double>();
			var lastBaseline = double.MinValue;
			foreach (var (baseline, size) in baselines)
			{
				if (groups.Count == 0 || baseline - lastBaseline > size * 0.5)
					groups.Add(baseline);

				lastBaseline = baseline;
			}

			return groups;
		}

		// the index of the baseline cluster closest to the baseline.
		private static int GroupOf(List<double> groups, double baseline)
		{
			var best = 0;
			for (int i = 1; i < groups.Count; i++)
			{
				if (Math.Abs(groups[i] - baseline) < Math.Abs(groups[best] - baseline))
					best = i;
			}

			return best;
		}

		// the number of distinct baseline clusters used by the lines.
		private static int CountGroups(List<TextLine> lines, List<double> groups)
		{
			var seen = new HashSet<int>();
			foreach (var line in lines)
				seen.Add(GroupOf(groups, line.Baseline));

			return seen.Count;
		}

		/// <summary>
		/// Splits a row into one row per baseline cluster; single-line cells span the new rows.
		/// </summary>
		/// <param name="model">The table.</param>
		/// <param name="row">The row to split.</param>
		/// <param name="groups">The baseline clusters of the row.</param>
		private static void SplitRow(TableModel model, int row, List<double> groups)
		{
			var k = groups.Count;

			// new row edges: between consecutive baselines, above the ascent of the next row.
			var edges = new List<double>(model.RowEdges);
			var inserted = new double[k - 1];
			for (int i = 0; i < k - 1; i++)
				inserted[i] = (groups[i] + groups[i + 1]) / 2 + (groups[i + 1] - groups[i]) * 0.15;
			edges.InsertRange(row + 1, inserted);

			var cells = new List<TableCellModel>();
			foreach (var cell in model.Cells)
			{
				if (cell.Row == row && cell.RowSpan == 1)
				{
					if (cell.Lines.Count > 0 && CountGroups(cell.Lines, groups) >= 2)
					{
						// one cell per baseline group.
						for (int g = 0; g < k; g++)
						{
							var part = new TableCellModel
							{
								Row = row + g,
								Column = cell.Column,
								ColumnSpan = cell.ColumnSpan,
								IsHeader = cell.IsHeader,
								Bounds = new PdfRect(cell.Bounds.Left, edges[row + g], cell.Bounds.Right, edges[row + g + 1]),
							};

							foreach (var line in cell.Lines)
							{
								if (GroupOf(groups, line.Baseline) == g)
									part.Lines.Add(line);
							}

							cells.Add(part);
						}
					}
					else
					{
						// a single label for the whole group of rows.
						cell.RowSpan = k;
						cells.Add(cell);
					}
				}
				else if (cell.Row <= row && cell.Row + cell.RowSpan > row)
				{
					cell.RowSpan += k - 1;
					cells.Add(cell);
				}
				else
				{
					if (cell.Row > row)
						cell.Row += k - 1;

					cells.Add(cell);
				}
			}

			model.Cells = cells;
			model.RowCount += k - 1;
			model.RowEdges = edges.ToArray();
		}

		#endregion
	}
}
