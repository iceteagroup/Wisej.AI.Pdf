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

// Table detection: ruled tables from vector graphics, and borderless tables
// from the alignment of text.

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.Content;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// Detects tables on a page.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Ruled tables</b> are found from the drawn grid: stroked lines, thin filled rectangles and the edges of
	/// shaded cells are merged into horizontal and vertical edges, connected edges form a lattice, and the lattice
	/// becomes a grid of rows and columns. A boundary missing between two grid cells means the cells are merged,
	/// which yields row and column spans. Text is assigned to the cells word by word, so a line that crosses a
	/// cell boundary is split correctly.
	/// </para>
	/// <para>
	/// <b>Borderless tables</b> are found from text alignment: runs of rows made of several line segments whose
	/// horizontal extents line up in columns. Paragraphs set in two columns are rejected because their cells
	/// are long wrapped text. Horizontal rules (as in "booktabs" tables) increase confidence and separate the header.
	/// </para>
	/// </remarks>
	internal sealed partial class TableDetector
	{
		#region Nested types

		/// <summary>
		/// A horizontal or vertical edge in the reading frame.
		/// </summary>
		private sealed class Edge
		{
			/// <summary>True for horizontal edges.</summary>
			public bool IsHorizontal;

			/// <summary>The fixed coordinate: Y for horizontal edges, X for vertical edges.</summary>
			public double Position;

			/// <summary>The start of the edge along its direction.</summary>
			public double Start;

			/// <summary>The end of the edge along its direction.</summary>
			public double End;

			/// <inheritdoc/>
			public override string ToString() => $"{(this.IsHorizontal ? "H" : "V")} {this.Position:0.#} [{this.Start:0.#}, {this.End:0.#}]";
		}

		#endregion

		// distance within which edges are considered connected or collinear.
		private const double Tolerance = 2.5;

		// the objects painted on the page.
		private readonly PageObjects _objects;
		// the orientation of the reading frame.
		private readonly int _orientation;
		// the page width in display space.
		private readonly double _width;
		// the page height in display space.
		private readonly double _height;
		// true to detect tables without rules.
		private readonly bool _borderless;

		// long horizontal rules, collected by DetectRuled and used by the borderless detection.
		private List<Edge> _horizontalRules;

		/// <summary>
		/// Initializes a new instance of <see cref="TableDetector"/>.
		/// </summary>
		/// <param name="objects">The objects painted on the page.</param>
		/// <param name="orientation">The orientation of the reading frame.</param>
		/// <param name="width">The page width.</param>
		/// <param name="height">The page height.</param>
		/// <param name="borderless">True to detect borderless tables.</param>
		public TableDetector(PageObjects objects, int orientation, double width, double height, bool borderless)
		{
			_objects = objects;
			_orientation = orientation;
			_width = width;
			_height = height;
			_borderless = borderless;
		}

		#region Methods

		/// <summary>
		/// Detects the tables drawn with ruling lines and marks the lines they contain as consumed.
		/// </summary>
		/// <param name="lines">The lines of the page in this orientation.</param>
		/// <returns>The table blocks.</returns>
		public List<LayoutBlock> DetectRuled(List<TextLine> lines)
		{
			var tables = new List<LayoutBlock>();

			var edges = CollectEdges(out _horizontalRules);
			foreach (var component in FindLattices(edges))
			{
				var table = BuildRuledTable(component, lines);
				if (table != null)
					tables.Add(table);
			}

			return tables;
		}

		/// <summary>
		/// Detects the tables without ruling lines and marks the lines they contain as consumed.
		/// </summary>
		/// <param name="lines">The lines of the page in this orientation.</param>
		/// <param name="exclusions">Blocks (tables, figures) whose areas are not considered.</param>
		/// <returns>The table blocks.</returns>
		public List<LayoutBlock> DetectBorderless(List<TextLine> lines, List<LayoutBlock> exclusions)
		{
			if (!_borderless)
				return new List<LayoutBlock>();

			if (_horizontalRules == null)
				CollectEdges(out _horizontalRules);

			return DetectBorderlessTables(lines, exclusions);
		}

		#endregion

		#region Edges

		// collects the edges drawn on the page, merged when collinear.
		private List<Edge> CollectEdges(out List<Edge> horizontalRules)
		{
			var horizontal = new List<Edge>();
			var vertical = new List<Edge>();
			var shadings = new List<(PdfRect Rect, int Color)>();
			var pageArea = _width * _height;

			foreach (var path in _objects.Paths)
			{
				if (path.IsClipped || path.HasCurves && path.Rectangles.Count == 0)
					continue;

				if (path.IsStroked)
				{
					var s = path.Segments;
					for (int i = 0; i + 3 < s.Count; i += 4)
						AddSegment(horizontal, vertical, s[i], s[i + 1], s[i + 2], s[i + 3]);
				}

				if (path.IsFilled && !path.IsStroked)
				{
					foreach (var displayRect in path.Rectangles)
					{
						var rect = ToFrame(displayRect);

						// thin rectangles are lines.
						if (rect.Height <= 3 && rect.Width > 3)
						{
							horizontal.Add(new Edge { IsHorizontal = true, Position = rect.CenterY, Start = rect.Left, End = rect.Right });
						}
						else if (rect.Width <= 3 && rect.Height > 3)
						{
							vertical.Add(new Edge { IsHorizontal = false, Position = rect.CenterX, Start = rect.Top, End = rect.Bottom });
						}
						else if (rect.Area < pageArea * 0.5 && path.FillColor != 0xFFFFFF && rect.Width > 3 && rect.Height > 3)
						{
							// shaded cells: collected first, merged with their neighbours of the same colour below.
							shadings.Add((rect, path.FillColor));
						}
					}
				}
			}

			// paragraph shading is painted line by line: adjacent rectangles of the same colour form one cell background.
			foreach (var (rect, _) in MergeShadings(shadings))
				AddRectangleEdges(horizontal, vertical, rect);

			horizontal = Merge(horizontal);
			vertical = Merge(vertical);

			// long horizontal rules are also used by the borderless detection.
			horizontalRules = horizontal.FindAll(e => e.End - e.Start > 30);

			var all = new List<Edge>(horizontal);
			all.AddRange(vertical);
			return all;
		}

		// merges touching rectangles of the same colour stacked vertically with the same left and right edges (shading painted line by line).
		// side-by-side rectangles are kept separate: their common edges are the column boundaries of shaded cells.
		private static List<(PdfRect Rect, int Color)> MergeShadings(List<(PdfRect Rect, int Color)> shadings)
		{
			var merged = true;
			while (merged)
			{
				merged = false;
				for (int i = 0; i < shadings.Count && !merged; i++)
				{
					for (int j = i + 1; j < shadings.Count; j++)
					{
						var a = shadings[i].Rect;
						var b = shadings[j].Rect;
						if (shadings[i].Color != shadings[j].Color)
							continue;

						var stacked = Math.Abs(a.Left - b.Left) < 1.5 && Math.Abs(a.Right - b.Right) < 1.5
							&& b.Top <= a.Bottom + 1.5 && a.Top <= b.Bottom + 1.5;
						if (stacked)
						{
							shadings[i] = (a.Union(b), shadings[i].Color);
							shadings.RemoveAt(j);
							merged = true;
							break;
						}
					}
				}
			}

			return shadings;
		}

		/// <summary>
		/// Adds a stroked segment as a horizontal or vertical edge, in the reading frame.
		/// </summary>
		/// <param name="horizontal">The horizontal edges.</param>
		/// <param name="vertical">The vertical edges.</param>
		/// <param name="x0">The start X in display space.</param>
		/// <param name="y0">The start Y in display space.</param>
		/// <param name="x1">The end X in display space.</param>
		/// <param name="y1">The end Y in display space.</param>
		private void AddSegment(
List<Edge> horizontal, List<Edge> vertical, double x0, double y0, double x1, double y1)
		{
			var a = ReadingFrame.ToFrame(_orientation, x0, y0, _width, _height);
			var b = ReadingFrame.ToFrame(_orientation, x1, y1, _width, _height);

			if (Math.Abs(a.Y - b.Y) <= 1 && Math.Abs(a.X - b.X) > 2)
				horizontal.Add(new Edge { IsHorizontal = true, Position = (a.Y + b.Y) / 2, Start = Math.Min(a.X, b.X), End = Math.Max(a.X, b.X) });
			else if (Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) > 2)
				vertical.Add(new Edge { IsHorizontal = false, Position = (a.X + b.X) / 2, Start = Math.Min(a.Y, b.Y), End = Math.Max(a.Y, b.Y) });
		}

		/// <summary>
		/// Adds the four sides of a rectangle as edges.
		/// </summary>
		/// <param name="horizontal">The horizontal edges.</param>
		/// <param name="vertical">The vertical edges.</param>
		/// <param name="rect">The rectangle in the reading frame.</param>
		private static void AddRectangleEdges(List<Edge> horizontal, List<Edge> vertical, PdfRect rect)
		{
			horizontal.Add(new Edge { IsHorizontal = true, Position = rect.Top, Start = rect.Left, End = rect.Right });
			horizontal.Add(new Edge { IsHorizontal = true, Position = rect.Bottom, Start = rect.Left, End = rect.Right });
			vertical.Add(new Edge { IsHorizontal = false, Position = rect.Left, Start = rect.Top, End = rect.Bottom });
			vertical.Add(new Edge { IsHorizontal = false, Position = rect.Right, Start = rect.Top, End = rect.Bottom });
		}

		// converts a display rectangle to the reading frame.
		private PdfRect ToFrame(PdfRect rect) => ReadingFrame.ToFrame(_orientation, rect, _width, _height);

		// joins collinear edges that overlap or are separated by small gaps (dashed lines, cell-by-cell borders).
		private static List<Edge> Merge(List<Edge> edges)
		{
			edges.Sort((a, b) =>
			{
				var c = a.Position.CompareTo(b.Position);
				return c != 0 ? c : a.Start.CompareTo(b.Start);
			});

			var result = new List<Edge>();
			var i = 0;
			while (i < edges.Count)
			{
				// edges at (almost) the same position.
				var group = new List<Edge>();
				var position = edges[i].Position;
				while (i < edges.Count && edges[i].Position - position <= Tolerance)
					group.Add(edges[i++]);

				group.Sort((a, b) => a.Start.CompareTo(b.Start));

				Edge current = null;
				double sum = 0;
				var count = 0;
				foreach (var edge in group)
				{
					if (current != null && edge.Start <= current.End + Tolerance)
					{
						current.End = Math.Max(current.End, edge.End);
						sum += edge.Position;
						count++;
						continue;
					}

					if (current != null)
					{
						current.Position = sum / count;
						result.Add(current);
					}

					current = new Edge { IsHorizontal = edge.IsHorizontal, Position = edge.Position, Start = edge.Start, End = edge.End };
					sum = edge.Position;
					count = 1;
				}

				if (current != null)
				{
					current.Position = sum / count;
					result.Add(current);
				}
			}

			return result;
		}

		#endregion

		#region Ruled tables

		// connected components of horizontal and vertical edges.
		private static List<List<Edge>> FindLattices(List<Edge> edges)
		{
			var parent = new int[edges.Count];
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

			for (int i = 0; i < edges.Count; i++)
			{
				var a = edges[i];
				if (!a.IsHorizontal)
					continue;

				for (int j = 0; j < edges.Count; j++)
				{
					var b = edges[j];
					if (b.IsHorizontal)
						continue;

					// a horizontal and a vertical edge are connected when they touch or cross.
					if (b.Position >= a.Start - Tolerance && b.Position <= a.End + Tolerance
						&& a.Position >= b.Start - Tolerance && a.Position <= b.End + Tolerance)
					{
						parent[Find(i)] = Find(j);
					}
				}
			}

			var groups = new Dictionary<int, List<Edge>>();
			for (int i = 0; i < edges.Count; i++)
			{
				var root = Find(i);
				if (!groups.TryGetValue(root, out var list))
					groups[root] = list = new List<Edge>();

				list.Add(edges[i]);
			}

			var result = new List<List<Edge>>();
			foreach (var group in groups.Values)
			{
				var h = 0;
				var v = 0;
				foreach (var edge in group)
				{
					if (edge.IsHorizontal)
						h++;
					else
						v++;
				}

				if (h >= 2 && v >= 2)
					result.Add(group);
			}

			return result;
		}

		/// <summary>
		/// Builds a table from a lattice of connected edges: grid, merged cells, text, header.
		/// </summary>
		/// <param name="component">The connected edges.</param>
		/// <param name="lines">The lines of the page.</param>
		/// <returns>The table block, or null when the lattice is not a table.</returns>
		private LayoutBlock BuildRuledTable(List<Edge> component, List<TextLine> lines)
		{
			// grid positions from the edges.
			var xs = new List<double>();
			var ys = new List<double>();
			double left = double.MaxValue, right = double.MinValue, top = double.MaxValue, bottom = double.MinValue;
			foreach (var edge in component)
			{
				if (edge.IsHorizontal)
				{
					ys.Add(edge.Position);
					left = Math.Min(left, edge.Start);
					right = Math.Max(right, edge.End);
				}
				else
				{
					xs.Add(edge.Position);
					top = Math.Min(top, edge.Start);
					bottom = Math.Max(bottom, edge.End);
				}
			}

			xs.Add(left);
			xs.Add(right);
			ys.Add(top);
			ys.Add(bottom);

			var columns = Cluster(xs);
			var rows = Cluster(ys);
			if (columns.Count < 2 || rows.Count < 2)
				return null;

			var bounds = new PdfRect(columns[0], rows[0], columns[columns.Count - 1], rows[rows.Count - 1]);

			// a frame around the whole page is decoration, not a table.
			if (bounds.Width > _width * 0.9 && bounds.Height > _height * 0.85 && rows.Count <= 3 && columns.Count <= 3)
				return null;

			// collect the words inside the table.
			var words = CollectWords(lines, bounds.Inflate(1));
			if (words.Count == 0)
				return null;

			var rowCount = rows.Count - 1;
			var columnCount = columns.Count - 1;

			// boundaries present between adjacent grid cells.
			var vertical = component.FindAll(e => !e.IsHorizontal);
			var horizontal = component.FindAll(e => e.IsHorizontal);

			var cellIds = new int[rowCount, columnCount];
			var parent = new int[rowCount * columnCount];
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

			// how often each boundary is drawn: a boundary drawn in most rows (columns) is implied where it's
			// missing when both sides have text and none crosses it (zebra-striped rows, partially ruled grids);
			// with text on one side only, the missing rule is a merged cell (a header spanning columns).
			var columnBoundaryRatio = new double[columnCount + 1];
			for (int c = 1; c < columnCount; c++)
			{
				var drawn = 0;
				for (int r = 0; r < rowCount; r++)
				{
					if (HasEdge(vertical, columns[c], rows[r], rows[r + 1]))
						drawn++;
				}

				columnBoundaryRatio[c] = (double)drawn / rowCount;
			}

			for (int r = 0; r < rowCount; r++)
			{
				for (int c = 0; c < columnCount; c++)
				{
					cellIds[r, c] = r * columnCount + c;

					// merge with the cell on the right when no vertical edge separates them.
					if (c + 1 < columnCount && !HasEdge(vertical, columns[c + 1], rows[r], rows[r + 1])
						&& !(columnBoundaryRatio[c + 1] >= 0.5 && !TextCrosses(words, columns[c + 1], rows[r], rows[r + 1], vertical: true)
							&& KeepsBoundary(words, columns, rows, r, c)))
					{
						parent[Find(r * columnCount + c + 1)] = Find(r * columnCount + c);
					}

					// merge with the cell below when no horizontal edge separates them.
					if (r + 1 < rowCount && !HasEdge(horizontal, rows[r + 1], columns[c], columns[c + 1]))
						parent[Find((r + 1) * columnCount + c)] = Find(r * columnCount + c);
				}
			}

			// build the cells as the bounding grid rectangles of each merged group.
			var groups = new Dictionary<int, TableCellModel>();
			for (int r = 0; r < rowCount; r++)
			{
				for (int c = 0; c < columnCount; c++)
				{
					var root = Find(r * columnCount + c);
					if (!groups.TryGetValue(root, out var cell))
					{
						cell = new TableCellModel { Row = r, Column = c };
						groups[root] = cell;
					}

					cell.RowSpan = Math.Max(cell.RowSpan, r - cell.Row + 1);
					cell.ColumnSpan = Math.Max(cell.ColumnSpan, c - cell.Column + 1);
					cell.Row = Math.Min(cell.Row, r);
					cell.Column = Math.Min(cell.Column, c);
				}
			}

			var model = new TableModel
			{
				RowCount = rowCount,
				ColumnCount = columnCount,
				IsRuled = true,
				Bounds = bounds,
				ColumnEdges = columns.ToArray(),
				RowEdges = rows.ToArray(),
			};

			foreach (var cell in groups.Values)
			{
				cell.Bounds = new PdfRect(columns[cell.Column], rows[cell.Row], columns[cell.Column + cell.ColumnSpan], rows[cell.Row + cell.RowSpan]);
				model.Cells.Add(cell);
			}

			// a merged group can be non-rectangular in broken grids: degenerate tables are rejected.
			if (model.Cells.Count < 2)
				return null;

			AssignWords(model, words);
			SubdivideColumns(model);
			SubdivideRows(model);
			RemoveEmptyRowsAndColumns(model);
			if (model.RowCount < 1 || model.ColumnCount < 1 || CountNonEmpty(model) < 2)
				return null;

			// a single column is a framed box (code sample, note, callout), not a table.
			if (model.ColumnCount == 1)
				return null;

			DetectHeader(model);
			ConsumeLines(lines, bounds);

			return new LayoutBlock { Kind = LayoutBlockKind.Table, Table = model, Bounds = bounds };
		}

		// an undrawn boundary between two cells is kept when both have text; when one side is empty, it's a merged cell
		// only if that side is a real column (with text in other rows): empty spacer columns never absorb their neighbours.
		private static bool KeepsBoundary(List<(TextWord Word, TextLine Line)> words, List<double> columns, List<double> rows, int r, int c)
		{
			var left = HasText(words, columns[c], columns[c + 1], rows[r], rows[r + 1]);
			var right = HasText(words, columns[c + 1], columns[c + 2], rows[r], rows[r + 1]);
			if (left == right)
				return true;

			var emptyColumn = left ? c + 1 : c;
			return !HasText(words, columns[emptyColumn], columns[emptyColumn + 1], rows[0], rows[rows.Count - 1]);
		}

		// true when some word has its center in the cell (left..right, top..bottom).
		private static bool HasText(List<(TextWord Word, TextLine Line)> words, double left, double right, double top, double bottom)
		{
			foreach (var (word, _) in words)
			{
				var b = word.Bounds;
				if (b.CenterX > left && b.CenterX < right && b.CenterY > top && b.CenterY < bottom)
					return true;
			}

			return false;
		}

		// true when a word crosses the boundary at "position" within the span [start, end].
		private static bool TextCrosses(List<(TextWord Word, TextLine Line)> words, double position, double start, double end, bool vertical)
		{
			foreach (var (word, _) in words)
			{
				var b = word.Bounds;
				if (vertical)
				{
					if (b.CenterY > start && b.CenterY < end && b.Left < position - 1 && b.Right > position + 1)
						return true;
				}
				else if (b.CenterX > start && b.CenterX < end && b.Top < position - 1 && b.Bottom > position + 1)
				{
					return true;
				}
			}

			return false;
		}

		// true when an edge at "position" covers most of the span [start, end].
		private static bool HasEdge(List<Edge> edges, double position, double start, double end)
		{
			var length = end - start;
			if (length <= 0)
				return true;

			double covered = 0;
			foreach (var edge in edges)
			{
				if (Math.Abs(edge.Position - position) > Tolerance)
					continue;

				var overlap = Math.Min(edge.End, end) - Math.Max(edge.Start, start);
				if (overlap > 0)
					covered += overlap;
			}

			return covered >= length * 0.5;
		}

		// clusters sorted coordinates that are closer than the tolerance.
		private static List<double> Cluster(List<double> values)
		{
			values.Sort();
			var result = new List<double>();
			var i = 0;
			while (i < values.Count)
			{
				var start = values[i];
				double sum = 0;
				var count = 0;
				while (i < values.Count && values[i] - start <= Tolerance * 1.5)
				{
					sum += values[i++];
					count++;
				}

				result.Add(sum / count);
			}

			return result;
		}

		#endregion

		#region Words and cells

		// the words of the lines whose centers fall inside the bounds.
		private static List<(TextWord Word, TextLine Line)> CollectWords(List<TextLine> lines, PdfRect bounds)
		{
			var words = new List<(TextWord, TextLine)>();
			foreach (var line in lines)
			{
				if (line.IsConsumed || !line.Bounds.IntersectsWith(bounds))
					continue;

				foreach (var word in line.Words)
				{
					if (bounds.Contains(word.Bounds.CenterX, word.Bounds.CenterY))
						words.Add((word, line));
				}
			}

			return words;
		}

		// assigns each word to the cell that contains its center and rebuilds the lines of each cell.
		private static void AssignWords(TableModel model, List<(TextWord Word, TextLine Line)> words)
		{
			var buckets = new Dictionary<TableCellModel, Dictionary<TextLine, TextLine>>();
			foreach (var (word, line) in words)
			{
				var cx = word.Bounds.CenterX;
				var cy = word.Bounds.CenterY;

				TableCellModel target = null;
				foreach (var cell in model.Cells)
				{
					if (cell.Bounds.Contains(cx, cy))
					{
						target = cell;
						break;
					}
				}

				if (target == null)
					continue;

				if (!buckets.TryGetValue(target, out var cellLines))
					buckets[target] = cellLines = new Dictionary<TextLine, TextLine>();

				// one cell line per source line: a source line that crosses cells is split.
				if (!cellLines.TryGetValue(line, out var cellLine))
				{
					cellLine = CloneLineHeader(line);
					cellLines[line] = cellLine;
				}

				cellLine.Words.Add(word);
				cellLine.Bounds = cellLine.Words.Count == 1 ? word.Bounds : cellLine.Bounds.Union(word.Bounds);
			}

			foreach (var entry in buckets)
			{
				var cellLines = new List<TextLine>(entry.Value.Values);
				cellLines.Sort((a, b) => a.Bounds.Top.CompareTo(b.Bounds.Top));
				foreach (var cellLine in cellLines)
					cellLine.InvalidateText();

				entry.Key.Lines = cellLines;
			}
		}

		// a new line with the same style as the source line and no words.
		private static TextLine CloneLineHeader(TextLine source)
		{
			return new TextLine
			{
				Baseline = source.Baseline,
				FontSize = source.FontSize,
				Orientation = source.Orientation,
				Order = source.Order,
				IsBold = source.IsBold,
				IsItalic = source.IsItalic,
				IsMonospace = source.IsMonospace,
				Color = source.Color,
				FontFamily = source.FontFamily,
				DisplayBounds = source.DisplayBounds,
			};
		}

		// the number of cells with text.
		private static int CountNonEmpty(TableModel model)
		{
			var count = 0;
			foreach (var cell in model.Cells)
			{
				if (cell.Lines.Count > 0)
					count++;
			}

			return count;
		}

		// removes rows and columns where every cell is empty (double borders, spacer columns).
		private static void RemoveEmptyRowsAndColumns(TableModel model)
		{
			// a row is empty when none of its single-row cells has text; spanning cells just lose a row.
			for (int r = model.RowCount - 1; r >= 0; r--)
			{
				var empty = true;
				foreach (var cell in model.Cells)
				{
					if (cell.Row == r && cell.RowSpan == 1 && cell.Lines.Count > 0)
					{
						empty = false;
						break;
					}
				}

				if (empty)
					RemoveRow(model, r);
			}

			for (int c = model.ColumnCount - 1; c >= 0; c--)
			{
				var empty = true;
				foreach (var cell in model.Cells)
				{
					if (cell.Column == c && cell.ColumnSpan == 1 && cell.Lines.Count > 0)
					{
						empty = false;
						break;
					}
				}

				if (empty)
					RemoveColumn(model, c);
			}
		}

		/// <summary>
		/// Removes an empty row, shrinking the cells that span it.
		/// </summary>
		/// <param name="model">The table.</param>
		/// <param name="row">The row index.</param>
		private static void RemoveRow(TableModel model, int row)
		{
			var cells = new List<TableCellModel>();
			foreach (var cell in model.Cells)
			{
				if (cell.Row == row && cell.RowSpan == 1)
				{
					// a cell entirely in the removed row disappears (it's empty).
					if (cell.Lines.Count > 0)
						cells.Add(cell);
					continue;
				}

				if (cell.Row <= row && cell.Row + cell.RowSpan > row)
					cell.RowSpan--;
				else if (cell.Row > row)
					cell.Row--;

				cells.Add(cell);
			}

			model.Cells = cells;
			model.RowCount--;

			var edges = new List<double>(model.RowEdges);
			edges.RemoveAt(row + 1);
			model.RowEdges = edges.ToArray();
		}

		/// <summary>
		/// Removes an empty column, shrinking the cells that span it.
		/// </summary>
		/// <param name="model">The table.</param>
		/// <param name="column">The column index.</param>
		private static void RemoveColumn(TableModel model, int column)
		{
			var cells = new List<TableCellModel>();
			foreach (var cell in model.Cells)
			{
				if (cell.Column == column && cell.ColumnSpan == 1)
				{
					if (cell.Lines.Count > 0)
						cells.Add(cell);
					continue;
				}

				if (cell.Column <= column && cell.Column + cell.ColumnSpan > column)
					cell.ColumnSpan--;
				else if (cell.Column > column)
					cell.Column--;

				cells.Add(cell);
			}

			model.Cells = cells;
			model.ColumnCount--;

			var edges = new List<double>(model.ColumnEdges);
			edges.RemoveAt(column + 1);
			model.ColumnEdges = edges.ToArray();
		}

		// the first row is a header when it's bold while the body is not.
		private static void DetectHeader(TableModel model)
		{
			if (model.RowCount < 2)
				return;

			int bold = 0, total = 0, bodyBold = 0, bodyTotal = 0;
			foreach (var cell in model.Cells)
			{
				foreach (var line in cell.Lines)
				{
					if (cell.Row == 0)
					{
						total++;
						if (line.IsBold)
							bold++;
					}
					else
					{
						bodyTotal++;
						if (line.IsBold)
							bodyBold++;
					}
				}
			}

			var headerBold = total > 0 && bold * 2 >= total;
			var bodyMostlyBold = bodyTotal > 0 && bodyBold * 2 >= bodyTotal;
			if (headerBold && !bodyMostlyBold)
			{
				// multi-level headers: the following rows that are entirely bold belong to the header too.
				var count = 1;
				while (count < Math.Min(4, model.RowCount - 1) && IsBoldRow(model, count))
					count++;

				SetHeaderRows(model, count);
			}
		}

		// true when every line of the row is bold.
		private static bool IsBoldRow(TableModel model, int row)
		{
			var lines = 0;
			foreach (var cell in model.Cells)
			{
				if (cell.Row != row)
					continue;

				foreach (var line in cell.Lines)
				{
					if (!line.IsBold)
						return false;

					lines++;
				}
			}

			return lines > 0;
		}

		/// <summary>
		/// Marks the first <paramref name="count"/> rows as header rows.
		/// </summary>
		/// <param name="model">The table.</param>
		/// <param name="count">The number of header rows.</param>
		private static void SetHeaderRows(TableModel model, int count)
		{
			model.HeaderRowCount = count;
			foreach (var cell in model.Cells)
				cell.IsHeader = cell.Row < count;

			// an empty header cell above a header label is part of the label (a label spanning the header rows).
			for (int i = model.Cells.Count - 1; i >= 0; i--)
			{
				var empty = model.Cells[i];
				if (empty.Lines.Count > 0 || !empty.IsHeader)
					continue;

				var below = model.Cells.Find(c => c.Row == empty.Row + empty.RowSpan && c.Column == empty.Column && c.ColumnSpan == empty.ColumnSpan && c.IsHeader && c.Lines.Count > 0);
				if (below == null)
					continue;

				below.RowSpan += below.Row - empty.Row;
				below.Row = empty.Row;
				below.Bounds = below.Bounds.Union(empty.Bounds);
				model.Cells.RemoveAt(i);
			}
		}

		// marks the lines inside the table as consumed.
		private static void ConsumeLines(List<TextLine> lines, PdfRect bounds)
		{
			var area = bounds.Inflate(1);
			foreach (var line in lines)
			{
				if (!line.IsConsumed && area.Contains(line.Bounds.CenterX, line.Bounds.CenterY))
					line.IsConsumed = true;
			}
		}

		#endregion
	}
}
