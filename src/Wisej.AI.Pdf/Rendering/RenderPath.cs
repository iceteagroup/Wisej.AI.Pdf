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

// Paths in device space (ISO 32000-2, 8.5.2) and their flattening into
// polylines with adaptive subdivision of the Bézier curves.

using System;
using Wisej.AI.Pdf.Fonts.Programs;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// A path made of subpaths of lines and cubic Bézier curves, in device space (pixels).
	/// </summary>
	/// <remarks>
	/// Instances are reused for every path of a page, so the internal buffers grow once and are not reallocated.
	/// The class also receives glyph outlines through <see cref="IGlyphPathSink"/>.
	/// </remarks>
	internal sealed class RenderPath : IGlyphPathSink
	{
		// path commands.
		private const byte VerbMove = 0;
		// draws a line to the point.
		private const byte VerbLine = 1;
		// draws a cubic Bézier curve through two control points to the end point.
		private const byte VerbCurve = 2;
		// closes the subpath.
		private const byte VerbClose = 3;

		// a path with more points than this is truncated, to bound memory and time.
		private const int MaxPoints = 4_000_000;

		// the path commands.
		private byte[] _verbs = new byte[64];
		// the number of path commands.
		private int _verbCount;
		// the coordinates of the commands, as x, y pairs.
		private double[] _coords = new double[256];
		// the number of coordinates.
		private int _coordCount;

		// start of the current subpath and current point.
		private double _startX, _startY, _currentX, _currentY;
		// true when the path has a current point.
		private bool _hasCurrent;

		#region Properties

		/// <summary>
		/// True when the path has no segments.
		/// </summary>
		public bool IsEmpty => _verbCount == 0;

		/// <summary>
		/// True when the path has a current point.
		/// </summary>
		public bool HasCurrentPoint => _hasCurrent;

		/// <summary>
		/// The x coordinate of the current point.
		/// </summary>
		public double CurrentX => _currentX;

		/// <summary>
		/// The y coordinate of the current point.
		/// </summary>
		public double CurrentY => _currentY;

		#endregion

		#region Methods

		/// <summary>
		/// Removes all the subpaths.
		/// </summary>
		public void Reset()
		{
			_verbCount = 0;
			_coordCount = 0;
			_hasCurrent = false;
		}

		/// <summary>
		/// Starts a new subpath.
		/// </summary>
		/// <param name="x">The x coordinate.</param>
		/// <param name="y">The y coordinate.</param>
		public void MoveTo(double x, double y)
		{
			// consecutive moves: only the last one matters.
			if (_verbCount > 0 && _verbs[_verbCount - 1] == VerbMove)
			{
				_coords[_coordCount - 2] = x;
				_coords[_coordCount - 1] = y;
			}
			else
			{
				AddVerb(VerbMove, x, y);
			}

			_startX = _currentX = x;
			_startY = _currentY = y;
			_hasCurrent = true;
		}

		/// <summary>
		/// Adds a line from the current point.
		/// </summary>
		/// <param name="x">The x coordinate of the end point.</param>
		/// <param name="y">The y coordinate of the end point.</param>
		public void LineTo(double x, double y)
		{
			// a line without a current point starts a subpath (lenient, as viewers do).
			if (!_hasCurrent)
			{
				MoveTo(x, y);
				return;
			}

			AddVerb(VerbLine, x, y);
			_currentX = x;
			_currentY = y;
		}

		/// <summary>
		/// Adds a cubic Bézier curve from the current point.
		/// </summary>
		/// <param name="x1">The x coordinate of the first control point.</param>
		/// <param name="y1">The y coordinate of the first control point.</param>
		/// <param name="x2">The x coordinate of the second control point.</param>
		/// <param name="y2">The y coordinate of the second control point.</param>
		/// <param name="x3">The x coordinate of the end point.</param>
		/// <param name="y3">The y coordinate of the end point.</param>
		public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
		{
			if (!_hasCurrent)
				MoveTo(x1, y1);

			if (_coordCount + 6 > MaxPoints * 2)
				return;

			EnsureVerbs();
			EnsureCoords(6);
			_verbs[_verbCount++] = VerbCurve;
			_coords[_coordCount++] = x1;
			_coords[_coordCount++] = y1;
			_coords[_coordCount++] = x2;
			_coords[_coordCount++] = y2;
			_coords[_coordCount++] = x3;
			_coords[_coordCount++] = y3;
			_currentX = x3;
			_currentY = y3;
		}

		/// <summary>
		/// Closes the current subpath.
		/// </summary>
		public void Close()
		{
			if (!_hasCurrent || _verbCount == 0 || _verbs[_verbCount - 1] == VerbClose)
				return;

			EnsureVerbs();
			_verbs[_verbCount++] = VerbClose;
			_currentX = _startX;
			_currentY = _startY;
		}

		/// <summary>
		/// Adds a closed rectangle given its four corners.
		/// </summary>
		/// <param name="x0">The x coordinate of the first corner.</param>
		/// <param name="y0">The y coordinate of the first corner.</param>
		/// <param name="x1">The x coordinate of the second corner.</param>
		/// <param name="y1">The y coordinate of the second corner.</param>
		/// <param name="x2">The x coordinate of the third corner.</param>
		/// <param name="y2">The y coordinate of the third corner.</param>
		/// <param name="x3">The x coordinate of the fourth corner.</param>
		/// <param name="y3">The y coordinate of the fourth corner.</param>
		public void Rectangle(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
		{
			MoveTo(x0, y0);
			LineTo(x1, y1);
			LineTo(x2, y2);
			LineTo(x3, y3);
			Close();
		}

		/// <summary>
		/// Returns true when the path is a single axis-aligned rectangle, and its bounds.
		/// </summary>
		/// <param name="x0">Receives the left side.</param>
		/// <param name="y0">Receives the top side.</param>
		/// <param name="x1">Receives the right side.</param>
		/// <param name="y1">Receives the bottom side.</param>
		/// <returns>True for a rectangle.</returns>
		public bool IsRectangle(out double x0, out double y0, out double x1, out double y1)
		{
			x0 = y0 = x1 = y1 = 0;

			// m l l l [l back to start] [h]
			var verbs = _verbCount;
			if (verbs > 0 && _verbs[verbs - 1] == VerbClose)
				verbs--;
			if (verbs < 4 || verbs > 5 || _verbs[0] != VerbMove)
				return false;

			for (int i = 1; i < verbs; i++)
			{
				if (_verbs[i] != VerbLine)
					return false;
			}

			var c = _coords;
			if (verbs == 5 && (c[8] != c[0] || c[9] != c[1]))
				return false;

			// every side must be horizontal or vertical.
			for (int i = 0; i < 4; i++)
			{
				var j = (i + 1) % 4;
				var ax = c[i * 2];
				var ay = c[i * 2 + 1];
				var bx = c[j * 2];
				var by = c[j * 2 + 1];
				if (Math.Abs(ax - bx) > 1e-6 && Math.Abs(ay - by) > 1e-6)
					return false;
			}

			// alternating sides: the first side horizontal and the second vertical, or vice versa.
			var firstHorizontal = Math.Abs(c[1] - c[3]) <= 1e-6;
			var secondHorizontal = Math.Abs(c[3] - c[5]) <= 1e-6;
			if (firstHorizontal == secondHorizontal)
				return false;

			x0 = Math.Min(Math.Min(c[0], c[2]), Math.Min(c[4], c[6]));
			x1 = Math.Max(Math.Max(c[0], c[2]), Math.Max(c[4], c[6]));
			y0 = Math.Min(Math.Min(c[1], c[3]), Math.Min(c[5], c[7]));
			y1 = Math.Max(Math.Max(c[1], c[3]), Math.Max(c[5], c[7]));
			return true;
		}

		/// <summary>
		/// Returns the bounds of the control points.
		/// </summary>
		/// <param name="x0">Receives the left side.</param>
		/// <param name="y0">Receives the top side.</param>
		/// <param name="x1">Receives the right side.</param>
		/// <param name="y1">Receives the bottom side.</param>
		/// <returns>False when the path is empty.</returns>
		public bool GetBounds(out double x0, out double y0, out double x1, out double y1)
		{
			x0 = y0 = double.MaxValue;
			x1 = y1 = double.MinValue;
			for (int i = 0; i + 1 < _coordCount; i += 2)
			{
				var x = _coords[i];
				var y = _coords[i + 1];
				if (x < x0) x0 = x;
				if (x > x1) x1 = x;
				if (y < y0) y0 = y;
				if (y > y1) y1 = y;
			}

			return _coordCount > 0;
		}

		/// <summary>
		/// Flattens the path into polylines.
		/// </summary>
		/// <param name="output">Receives the polylines; it is cleared first.</param>
		/// <param name="tolerance">The maximum distance between the curves and the polylines, in pixels.</param>
		public void Flatten(Polylines output, double tolerance)
		{
			output.Clear();

			var c = _coords;
			var k = 0;
			double x = 0, y = 0;
			for (int i = 0; i < _verbCount; i++)
			{
				switch (_verbs[i])
				{
					case VerbMove:
						x = c[k];
						y = c[k + 1];
						k += 2;
						output.MoveTo(x, y);
						break;

					case VerbLine:
						x = c[k];
						y = c[k + 1];
						k += 2;
						output.LineTo(x, y);
						break;

					case VerbCurve:
						FlattenCurve(output, x, y, c[k], c[k + 1], c[k + 2], c[k + 3], c[k + 4], c[k + 5], tolerance);
						x = c[k + 4];
						y = c[k + 5];
						k += 6;
						break;

					case VerbClose:
						output.Close();
						break;
				}
			}

			output.Finish();
		}

		#endregion

		#region IGlyphPathSink

		/// <inheritdoc/>
		void IGlyphPathSink.MoveTo(float x, float y) => MoveTo(x, y);

		/// <inheritdoc/>
		void IGlyphPathSink.LineTo(float x, float y) => LineTo(x, y);

		/// <inheritdoc/>
		void IGlyphPathSink.CurveTo(float x1, float y1, float x2, float y2, float x3, float y3) => CurveTo(x1, y1, x2, y2, x3, y3);

		/// <inheritdoc/>
		void IGlyphPathSink.ClosePath() => Close();

		#endregion

		#region Implementation

		// adds a command with one point, unless the path is full.
		private void AddVerb(byte verb, double x, double y)
		{
			if (_coordCount + 2 > MaxPoints * 2)
				return;

			EnsureVerbs();
			EnsureCoords(2);
			_verbs[_verbCount++] = verb;
			_coords[_coordCount++] = x;
			_coords[_coordCount++] = y;
		}

		// grows the command buffer when it is full.
		private void EnsureVerbs()
		{
			if (_verbCount == _verbs.Length)
				Array.Resize(ref _verbs, _verbs.Length * 2);
		}

		// grows the coordinate buffer to fit count more values.
		private void EnsureCoords(int count)
		{
			if (_coordCount + count > _coords.Length)
				Array.Resize(ref _coords, Math.Max(_coords.Length * 2, _coordCount + count));
		}

		// uniform subdivision with a segment count derived from the second differences of the control polygon.
		private static void FlattenCurve(Polylines output, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double tolerance)
		{
			var ddx0 = x0 - 2 * x1 + x2;
			var ddy0 = y0 - 2 * y1 + y2;
			var ddx1 = x1 - 2 * x2 + x3;
			var ddy1 = y1 - 2 * y2 + y3;
			var dd = Math.Sqrt(Math.Max(ddx0 * ddx0 + ddy0 * ddy0, ddx1 * ddx1 + ddy1 * ddy1));

			// the error of n uniform segments is at most 3/4 * dd / n^2.
			var n = (int)Math.Ceiling(Math.Sqrt(0.75 * dd / tolerance));
			if (!(n >= 1))
				n = 1;
			else if (n > 500)
				n = 500;

			if (n == 1)
			{
				output.LineTo(x3, y3);
				return;
			}

			// polynomial coefficients: p(t) = a t^3 + b t^2 + c t + p0.
			var cx = 3 * (x1 - x0);
			var cy = 3 * (y1 - y0);
			var bx = 3 * (x2 - x1) - cx;
			var by = 3 * (y2 - y1) - cy;
			var ax = x3 - x0 - cx - bx;
			var ay = y3 - y0 - cy - by;

			var step = 1.0 / n;
			for (int i = 1; i < n; i++)
			{
				var t = i * step;
				output.LineTo(((ax * t + bx) * t + cx) * t + x0, ((ay * t + by) * t + cy) * t + y0);
			}

			output.LineTo(x3, y3);
		}

		#endregion
	}

	/// <summary>
	/// A set of polylines (flattened subpaths) in device space.
	/// </summary>
	internal sealed class Polylines
	{
		// the x coordinates of the points.
		private double[] _x = new double[256];
		// the y coordinates of the points.
		private double[] _y = new double[256];
		// the number of points.
		private int _count;

		// the index of the first point of each subpath.
		private int[] _starts = new int[16];
		// true for each subpath that is closed.
		private bool[] _closed = new bool[16];
		// the number of subpaths.
		private int _subpaths;

		// true when the last subpath was implicitly started by Close and has only its first point.
		private bool _closedPending;

		#region Properties

		/// <summary>
		/// The x coordinates of all the points.
		/// </summary>
		public double[] X => _x;

		/// <summary>
		/// The y coordinates of all the points.
		/// </summary>
		public double[] Y => _y;

		/// <summary>
		/// The number of subpaths.
		/// </summary>
		public int SubpathCount => _subpaths;

		#endregion

		#region Methods

		/// <summary>
		/// Removes all the polylines.
		/// </summary>
		public void Clear()
		{
			_count = 0;
			_subpaths = 0;
		}

		/// <summary>
		/// Returns the range of points of a subpath.
		/// </summary>
		/// <param name="index">The subpath index.</param>
		/// <param name="start">Receives the index of the first point.</param>
		/// <param name="end">Receives the index after the last point.</param>
		/// <returns>True when the subpath is closed.</returns>
		public bool GetSubpath(int index, out int start, out int end)
		{
			start = _starts[index];
			end = index + 1 < _subpaths ? _starts[index + 1] : _count;
			return _closed[index];
		}

		/// <summary>
		/// Starts a new polyline.
		/// </summary>
		/// <param name="x">The x coordinate.</param>
		/// <param name="y">The y coordinate.</param>
		public void MoveTo(double x, double y)
		{
			if (_subpaths == _starts.Length)
			{
				Array.Resize(ref _starts, _subpaths * 2);
				Array.Resize(ref _closed, _subpaths * 2);
			}

			_starts[_subpaths] = _count;
			_closed[_subpaths] = false;
			_subpaths++;
			Add(x, y);
		}

		/// <summary>
		/// Adds a point to the current polyline.
		/// </summary>
		/// <param name="x">The x coordinate.</param>
		/// <param name="y">The y coordinate.</param>
		public void LineTo(double x, double y)
		{
			if (_subpaths == 0)
			{
				MoveTo(x, y);
				return;
			}

			Add(x, y);
		}

		/// <summary>
		/// Marks the current polyline as closed. Points added afterwards start a new polyline from the start point.
		/// </summary>
		public void Close()
		{
			if (_subpaths == 0)
				return;

			_closed[_subpaths - 1] = true;

			// drawing after "h" continues from the start of the closed subpath.
			var start = _starts[_subpaths - 1];
			var x = _x[start];
			var y = _y[start];
			MoveTo(x, y);
			_closedPending = true;
		}

		/// <summary>
		/// Removes the implicit subpath started by the last <see cref="Close"/> when nothing was added to it.
		/// </summary>
		public void Finish()
		{
			if (_closedPending && _subpaths > 0 && _count - _starts[_subpaths - 1] == 1)
			{
				_count--;
				_subpaths--;
			}

			_closedPending = false;
		}

		/// <summary>
		/// Adds the edges of all the polylines, implicitly closed, to a rasterizer.
		/// </summary>
		/// <param name="rasterizer">The rasterizer.</param>
		public void Fill(Rasterizer rasterizer)
		{
			for (int s = 0; s < _subpaths; s++)
			{
				GetSubpath(s, out var start, out var end);
				if (end - start < 2)
					continue;

				for (int i = start + 1; i < end; i++)
					rasterizer.AddLine(_x[i - 1], _y[i - 1], _x[i], _y[i]);

				rasterizer.AddLine(_x[end - 1], _y[end - 1], _x[start], _y[start]);
			}
		}

		#endregion

		#region Implementation

		// appends a point, growing the buffers when needed.
		private void Add(double x, double y)
		{
			if (_count == _x.Length)
			{
				Array.Resize(ref _x, _count * 2);
				Array.Resize(ref _y, _count * 2);
			}

			_x[_count] = x;
			_y[_count] = y;
			_count++;
			_closedPending = false;
		}

		#endregion
	}
}
