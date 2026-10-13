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

// Anti-aliased scan converter: exact-area coverage computed by signed area
// accumulation, with the non-zero and even-odd fill rules (ISO 32000-2, 8.5.3.3).
//
// The accumulation technique follows the font-rs rasterizer by Raph Levien
// (Apache-2.0); the banding, clipping and fill rules are original.

using System;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// The rule that decides which points are inside a path.
	/// </summary>
	internal enum FillRule
	{
		/// <summary>The non-zero winding number rule.</summary>
		NonZero,

		/// <summary>The even-odd rule.</summary>
		EvenOdd,
	}

	/// <summary>
	/// Receives the coverage of the rows produced by <see cref="Rasterizer.Sweep"/>.
	/// </summary>
	internal interface ICoverageSink
	{
		/// <summary>
		/// Processes the coverage of one row.
		/// </summary>
		/// <param name="y">The row.</param>
		/// <param name="x0">The first column with coverage.</param>
		/// <param name="x1">The column after the last one with coverage.</param>
		/// <param name="coverage">The coverage, 0 to 255, indexed by column; only [x0, x1) is valid.</param>
		void Row(int y, int x0, int x1, byte[] coverage);
	}

	/// <summary>
	/// Converts polygons to anti-aliased coverage, one path at a time.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Edges are collected with <see cref="AddLine"/>, clipped to a window (the current clip bounds),
	/// then <see cref="Sweep"/> computes the exact area of each pixel covered by the polygon. Each edge
	/// deposits its signed area contribution into an accumulation buffer; a running sum along each row
	/// gives the (fractional) winding number of every pixel, to which the fill rule is applied.
	/// </para>
	/// <para>
	/// The accumulation buffer covers a band of rows, so memory stays bounded for any canvas size.
	/// Edges left of the window are clamped to its left side, where they still affect the winding
	/// numbers; edges right of it can't affect any visible pixel and are dropped.
	/// </para>
	/// </remarks>
	internal sealed class Rasterizer
	{
		// the accumulation buffer holds at most this many cells per band.
		private const int MaxBandCells = 1 << 18;

		// the canvas size, the row length of the accumulation buffer and the rows of a band.
		private readonly int _width;
		// the canvas height in pixels.
		private readonly int _height;
		// the row length of the accumulation buffer: the width plus two guard cells.
		private readonly int _stride;
		// the number of rows of a band.
		private readonly int _bandHeight;

		// accumulation cells of the current band: _stride per row.
		private readonly float[] _cells;

		// per band row, one flag for each block of 32 cells: true when an edge deposited area in it.
		private readonly bool[] _touched;
		// the number of 32-cell blocks per band row.
		private readonly int _blocksPerRow;

		// coverage of the row being swept, indexed by column.
		private readonly byte[] _coverage;

		// the collected edges, already clipped: x0, y0, x1, y1 (y0 != y1), with the direction in _dirs.
		private double[] _edges = new double[1024];
		// the number of collected edges.
		private int _edgeCount;

		// the clip window, in pixels.
		private int _wx0, _wy0, _wx1, _wy1;

		// bounds of the collected edges.
		private double _minX, _minY, _maxX, _maxY;

		/// <summary>
		/// Initializes a new rasterizer for a canvas of the given size.
		/// </summary>
		/// <param name="width">The canvas width in pixels.</param>
		/// <param name="height">The canvas height in pixels.</param>
		public Rasterizer(int width, int height)
		{
			_width = Math.Max(1, width);
			_height = Math.Max(1, height);
			_stride = _width + 2;
			_bandHeight = Math.Max(1, Math.Min(_height, MaxBandCells / _stride));
			_cells = new float[_stride * _bandHeight];
			_blocksPerRow = (_stride + 31) >> 5;
			_touched = new bool[_blocksPerRow * _bandHeight];
			_coverage = new byte[_width + 2];
			Reset(0, 0, _width, _height);
		}

		#region Properties

		/// <summary>
		/// True when no edge was collected.
		/// </summary>
		public bool IsEmpty => _edgeCount == 0;

		/// <summary>
		/// The left side of the bounds of the collected edges, in pixels.
		/// </summary>
		public double MinX => _minX;

		/// <summary>
		/// The top side of the bounds of the collected edges, in pixels.
		/// </summary>
		public double MinY => _minY;

		/// <summary>
		/// The right side of the bounds of the collected edges, in pixels.
		/// </summary>
		public double MaxX => _maxX;

		/// <summary>
		/// The bottom side of the bounds of the collected edges, in pixels.
		/// </summary>
		public double MaxY => _maxY;

		#endregion

		#region Methods

		/// <summary>
		/// Discards the collected edges and sets the clip window.
		/// </summary>
		/// <param name="x0">The left side of the window.</param>
		/// <param name="y0">The top side of the window.</param>
		/// <param name="x1">The right side of the window (exclusive).</param>
		/// <param name="y1">The bottom side of the window (exclusive).</param>
		public void Reset(int x0, int y0, int x1, int y1)
		{
			_wx0 = Math.Max(0, Math.Min(_width, x0));
			_wy0 = Math.Max(0, Math.Min(_height, y0));
			_wx1 = Math.Max(_wx0, Math.Min(_width, x1));
			_wy1 = Math.Max(_wy0, Math.Min(_height, y1));
			_edgeCount = 0;
			_minX = _minY = double.MaxValue;
			_maxX = _maxY = double.MinValue;
		}

		/// <summary>
		/// Adds an edge of the polygon, in pixel coordinates.
		/// </summary>
		/// <param name="x0">The x coordinate of the start point.</param>
		/// <param name="y0">The y coordinate of the start point.</param>
		/// <param name="x1">The x coordinate of the end point.</param>
		/// <param name="y1">The y coordinate of the end point.</param>
		public void AddLine(double x0, double y0, double x1, double y1)
		{
			// horizontal edges don't change winding numbers; NaN and infinite values are rejected here too.
			if (!(y0 != y1) || double.IsNaN(x0) || double.IsNaN(x1) || double.IsInfinity(x0) || double.IsInfinity(x1) || double.IsInfinity(y0) || double.IsInfinity(y1))
				return;

			// vertical clipping to the window.
			double top = _wy0, bottom = _wy1;
			if (y0 < y1)
			{
				if (y1 <= top || y0 >= bottom)
					return;
				if (y0 < top)
				{
					x0 += (x1 - x0) * (top - y0) / (y1 - y0);
					y0 = top;
				}
				if (y1 > bottom)
				{
					x1 += (x1 - x0) * (bottom - y1) / (y1 - y0);
					y1 = bottom;
				}
			}
			else
			{
				if (y0 <= top || y1 >= bottom)
					return;
				if (y1 < top)
				{
					x1 += (x0 - x1) * (top - y1) / (y0 - y1);
					y1 = top;
				}
				if (y0 > bottom)
				{
					x0 += (x0 - x1) * (bottom - y0) / (y0 - y1);
					y0 = bottom;
				}
			}

			if (y0 == y1)
				return;

			// horizontal clipping: right of the window nothing is visible, left of it the edge is clamped.
			double left = _wx0, right = _wx1;
			if (x0 >= right && x1 >= right)
			{
				// the winding numbers of the edges on the left extend up to the right side of the window.
				if (right > _maxX)
					_maxX = right;

				return;
			}

			if (x0 > right || x1 > right)
			{
				var yc = y0 + (y1 - y0) * (right - x0) / (x1 - x0);
				if (x0 > right)
				{
					x0 = right;
					y0 = yc;
				}
				else
				{
					x1 = right;
					y1 = yc;
				}
			}

			if (x0 < left || x1 < left)
			{
				if (x0 <= left && x1 <= left)
				{
					Store(left, y0, left, y1);
					return;
				}

				var yc = y0 + (y1 - y0) * (left - x0) / (x1 - x0);
				if (x0 < left)
				{
					Store(left, y0, left, yc);
					Store(left, yc, x1, y1);
				}
				else
				{
					Store(x0, y0, left, yc);
					Store(left, yc, left, y1);
				}

				return;
			}

			Store(x0, y0, x1, y1);
		}

		/// <summary>
		/// Computes the coverage of the collected polygon and sends it row by row to <paramref name="sink"/>.
		/// The edges are discarded afterwards.
		/// </summary>
		/// <param name="rule">The fill rule.</param>
		/// <param name="sink">The receiver of the coverage rows.</param>
		public void Sweep(FillRule rule, ICoverageSink sink)
		{
			if (_edgeCount == 0)
				return;

			var rowStart = Math.Max(_wy0, (int)Math.Floor(_minY));
			var rowEnd = Math.Min(_wy1, (int)Math.Ceiling(_maxY));
			var colStart = Math.Max(_wx0, (int)Math.Floor(_minX));
			var colEnd = Math.Min(_wx1, (int)Math.Ceiling(_maxX) + 1);
			var cells = _cells;
			var coverage = _coverage;
			var touched = _touched;
			var evenOdd = rule == FillRule.EvenOdd;

			for (int bandTop = rowStart; bandTop < rowEnd; bandTop += _bandHeight)
			{
				var bandBottom = Math.Min(rowEnd, bandTop + _bandHeight);

				// deposit the edges crossing the band.
				var edges = _edges;
				for (int i = 0; i < _edgeCount * 4; i += 4)
				{
					var ey0 = edges[i + 1];
					var ey1 = edges[i + 3];
					if (ey0 < ey1)
					{
						if (ey1 > bandTop && ey0 < bandBottom)
							Accumulate(edges[i], ey0, edges[i + 2], ey1, 1, bandTop, bandBottom);
					}
					else if (ey0 > bandTop && ey1 < bandBottom)
					{
						Accumulate(edges[i + 2], ey1, edges[i], ey0, -1, bandTop, bandBottom);
					}
				}

				// running sums along each row give the winding numbers; the coverage only changes
				// where an edge deposited area, so blocks without area repeat the previous value.
				for (int y = bandTop; y < bandBottom; y++)
				{
					var row = (y - bandTop) * _stride;
					var touchedRow = (y - bandTop) * _blocksPerRow;
					float sum = 0;
					byte c = 0;
					int first = -1, last = -1;

					for (int x = colStart; x < colEnd;)
					{
						var block = x >> 5;
						var blockEnd = Math.Min(colEnd, (block + 1) << 5);

						if (!touched[touchedRow + block])
						{
							// constant coverage over the whole block.
							if (c != 0)
							{
								if (first < 0)
									first = x;
								last = blockEnd - 1;
							}

							for (; x < blockEnd; x++)
								coverage[x] = c;

							continue;
						}

						touched[touchedRow + block] = false;
						for (; x < blockEnd; x++)
						{
							var cell = cells[row + x];
							if (cell != 0)
							{
								cells[row + x] = 0;
								sum += cell;

								var value = sum < 0 ? -sum : sum;
								if (evenOdd)
								{
									// fold the winding number: 0, 2, 4... are outside, 1, 3, 5... inside.
									if (value >= 2)
										value %= 2;
									if (value > 1)
										value = 2 - value;
								}
								else if (value > 1)
								{
									value = 1;
								}

								c = (byte)(value * 255 + 0.5f);
							}

							coverage[x] = c;
							if (c != 0)
							{
								if (first < 0)
									first = x;
								last = x;
							}
						}
					}

					// clear the cells right of the visible columns that edges may have touched.
					for (int x = colEnd; x < _stride && x <= colEnd + 1; x++)
						cells[row + x] = 0;

					if (first >= 0)
						sink.Row(y, first, last + 1, coverage);
				}
			}

			_edgeCount = 0;
		}

		#endregion

		#region Implementation

		// stores a clipped edge and updates the bounds.
		private void Store(double x0, double y0, double x1, double y1)
		{
			if (y0 == y1)
				return;

			if ((_edgeCount + 1) * 4 > _edges.Length)
				Array.Resize(ref _edges, _edges.Length * 2);

			var i = _edgeCount * 4;
			_edges[i] = x0;
			_edges[i + 1] = y0;
			_edges[i + 2] = x1;
			_edges[i + 3] = y1;
			_edgeCount++;

			if (x0 < _minX) _minX = x0;
			if (x1 < _minX) _minX = x1;
			if (x0 > _maxX) _maxX = x0;
			if (x1 > _maxX) _maxX = x1;
			if (y0 < _minY) _minY = y0;
			if (y1 < _minY) _minY = y1;
			if (y0 > _maxY) _maxY = y0;
			if (y1 > _maxY) _maxY = y1;
		}

		// deposits the signed area of the edge (x0, y0)-(x1, y1), with y0 < y1, into the rows of the band.
		private void Accumulate(double x0, double y0, double x1, double y1, float direction, int bandTop, int bandBottom)
		{
			var cells = _cells;
			var stride = _stride;
			var dxdy = (x1 - x0) / (y1 - y0);
			var startY = Math.Max(y0, bandTop);
			var endY = Math.Min(y1, bandBottom);
			var x = x0 + (startY - y0) * dxdy;
			var maxIndex = _width + 1;

			for (int y = (int)startY; y < endY; y++)
			{
				var dy = Math.Min(y + 1, endY) - Math.Max(y, startY);
				if (dy <= 0)
					continue;

				var xNext = x + dxdy * dy;
				var d = (float)(dy * direction);
				var row = (y - bandTop) * stride;

				double xa, xb;
				if (x < xNext)
				{
					xa = x;
					xb = xNext;
				}
				else
				{
					xa = xNext;
					xb = x;
				}

				var xaFloor = Math.Floor(xa);
				var xai = (int)xaFloor;
				var xbCeil = Math.Ceiling(xb);
				var xbi = (int)xbCeil;

				if (xai < 0)
					xai = 0;

				if (xbi <= xai + 1)
				{
					// the edge stays within one column: split the area between it and the next one.
					var xmf = (float)(0.5 * (x + xNext) - xaFloor);
					var column = Math.Min(xai, maxIndex - 1);
					var i = row + column;
					cells[i] += d - d * xmf;
					cells[i + 1] += d * xmf;
					Touch(y - bandTop, column, column + 1);
				}
				else
				{
					// the edge crosses several columns: trapezoid areas for the first and last, constant in between.
					var s = 1.0 / (xb - xa);
					var x0f = xa - xaFloor;
					var a0 = 0.5 * s * (1 - x0f) * (1 - x0f);
					var x1f = xb - xbCeil + 1;
					var am = 0.5 * s * x1f * x1f;

					if (xbi > maxIndex)
						xbi = maxIndex;

					cells[row + xai] += (float)(d * a0);
					if (xbi == xai + 2)
					{
						cells[row + xai + 1] += (float)(d * (1 - a0 - am));
					}
					else
					{
						var a1 = s * (1.5 - x0f);
						cells[row + xai + 1] += (float)(d * (a1 - a0));
						var ds = (float)(d * s);
						for (int xi = xai + 2; xi < xbi - 1; xi++)
							cells[row + xi] += ds;

						var a2 = a1 + (xbi - xai - 3) * s;
						cells[row + xbi - 1] += (float)(d * (1 - a2 - am));
					}

					cells[row + xbi] += (float)(d * am);
					Touch(y - bandTop, xai, xbi);
				}

				x = xNext;
			}
		}

		// marks the blocks of 32 cells of a band row that hold area, so the sweep can skip the others.
		private void Touch(int bandRow, int first, int last)
		{
			var row = bandRow * _blocksPerRow;
			for (int block = first >> 5, end = last >> 5; block <= end; block++)
				_touched[row + block] = true;
		}

		#endregion
	}
}
