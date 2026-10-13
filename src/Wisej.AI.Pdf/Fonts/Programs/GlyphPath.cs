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

// Compact, immutable glyph outline in glyph space, and the builder used by the font parsers.

using System;

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// The drawing commands stored in a <see cref="GlyphPath"/>.
	/// </summary>
	internal enum GlyphPathVerb : byte
	{
		/// <summary>
		/// Starts a new contour at (x, y).
		/// </summary>
		MoveTo,

		/// <summary>
		/// Draws a straight line to (x, y).
		/// </summary>
		LineTo,

		/// <summary>
		/// Draws a cubic Bézier curve with the control points (x1, y1), (x2, y2) to (x3, y3).
		/// </summary>
		CurveTo,

		/// <summary>
		/// Closes the current contour.
		/// </summary>
		ClosePath
	}

	/// <summary>
	/// Receives the commands of a <see cref="GlyphPath"/> in order.
	/// </summary>
	internal interface IGlyphPathSink
	{
		/// <summary>
		/// Starts a new contour.
		/// </summary>
		/// <param name="x">The x coordinate.</param>
		/// <param name="y">The y coordinate.</param>
		void MoveTo(float x, float y);

		/// <summary>
		/// Draws a straight line from the current point.
		/// </summary>
		/// <param name="x">The x coordinate of the end point.</param>
		/// <param name="y">The y coordinate of the end point.</param>
		void LineTo(float x, float y);

		/// <summary>
		/// Draws a cubic Bézier curve from the current point.
		/// </summary>
		/// <param name="x1">The x coordinate of the first control point.</param>
		/// <param name="y1">The y coordinate of the first control point.</param>
		/// <param name="x2">The x coordinate of the second control point.</param>
		/// <param name="y2">The y coordinate of the second control point.</param>
		/// <param name="x3">The x coordinate of the end point.</param>
		/// <param name="y3">The y coordinate of the end point.</param>
		void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3);

		/// <summary>
		/// Closes the current contour.
		/// </summary>
		void ClosePath();
	}

	/// <summary>
	/// An immutable glyph outline in glyph space, made of move, line, cubic curve and close commands.
	/// </summary>
	/// <remarks>
	/// The outline is stored as an array of verbs plus an array of coordinates: a move or line uses
	/// two coordinates, a curve six and a close none. Every contour is explicitly closed.
	/// Instances are immutable and safe to share between threads.
	/// </remarks>
	internal sealed class GlyphPath
	{
		#region Nested types

		/// <summary>
		/// A single command of a <see cref="GlyphPath"/>, returned by the enumerator.
		/// </summary>
		public struct Segment
		{
			/// <summary>
			/// The command.
			/// </summary>
			public GlyphPathVerb Verb;

			/// <summary>
			/// The x coordinate of the end point, or of the first control point of a curve.
			/// </summary>
			public float X1;

			/// <summary>
			/// The y coordinate of the end point, or of the first control point of a curve.
			/// </summary>
			public float Y1;

			/// <summary>
			/// The x coordinate of the second control point of a curve.
			/// </summary>
			public float X2;

			/// <summary>
			/// The y coordinate of the second control point of a curve.
			/// </summary>
			public float Y2;

			/// <summary>
			/// The x coordinate of the end point of a curve.
			/// </summary>
			public float X3;

			/// <summary>
			/// The y coordinate of the end point of a curve.
			/// </summary>
			public float Y3;
		}

		/// <summary>
		/// Enumerates the commands of a <see cref="GlyphPath"/> without allocating.
		/// </summary>
		public struct Enumerator
		{
			// the path being enumerated.
			private readonly GlyphPath _path;

			// index of the current verb.
			private int _verb;

			// index of the first coordinate of the next verb.
			private int _coord;

			// the current command.
			private Segment _current;

			/// <summary>
			/// Initializes a new enumerator.
			/// </summary>
			/// <param name="path">The path to enumerate.</param>
			internal Enumerator(GlyphPath path)
			{
				_path = path;
				_verb = -1;
				_coord = 0;
				_current = default(Segment);
			}

			/// <summary>
			/// Returns the current command.
			/// </summary>
			public Segment Current => _current;

			/// <summary>
			/// Advances to the next command.
			/// </summary>
			/// <returns>True when a command is available.</returns>
			public bool MoveNext()
			{
				if (_path == null || ++_verb >= _path._verbs.Length)
					return false;

				var c = _path._coords;
				var verb = (GlyphPathVerb)_path._verbs[_verb];
				_current.Verb = verb;
				switch (verb)
				{
					case GlyphPathVerb.MoveTo:
					case GlyphPathVerb.LineTo:
						_current.X1 = c[_coord];
						_current.Y1 = c[_coord + 1];
						_coord += 2;
						break;

					case GlyphPathVerb.CurveTo:
						_current.X1 = c[_coord];
						_current.Y1 = c[_coord + 1];
						_current.X2 = c[_coord + 2];
						_current.Y2 = c[_coord + 3];
						_current.X3 = c[_coord + 4];
						_current.Y3 = c[_coord + 5];
						_coord += 6;
						break;
				}

				return true;
			}
		}

		#endregion

		#region Fields

		/// <summary>
		/// The shared empty outline.
		/// </summary>
		public static readonly GlyphPath Empty = new GlyphPath(Array.Empty<byte>(), Array.Empty<float>());

		// the commands, one GlyphPathVerb per entry.
		private readonly byte[] _verbs;

		// the coordinates consumed by the commands, in order.
		private readonly float[] _coords;

		// the tight bounding box in glyph space, computed on first use.
		private float _xMin, _yMin, _xMax, _yMax;

		// set (with release semantics) after the four bounds have been written.
		private volatile bool _boundsReady;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new outline from its encoded commands.
		/// </summary>
		/// <param name="verbs">The commands.</param>
		/// <param name="coords">The coordinates, as described in the remarks of the class.</param>
		internal GlyphPath(byte[] verbs, float[] coords)
		{
			_verbs = verbs;
			_coords = coords;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns true when the outline has no drawing commands.
		/// </summary>
		public bool IsEmpty => _verbs.Length == 0;

		/// <summary>
		/// Returns the number of commands.
		/// </summary>
		public int Count => _verbs.Length;

		/// <summary>
		/// Returns the raw command array. Callers must not modify it.
		/// </summary>
		internal byte[] Verbs => _verbs;

		/// <summary>
		/// Returns the raw coordinate array. Callers must not modify it.
		/// </summary>
		internal float[] Coordinates => _coords;

		#endregion

		#region Methods

		/// <summary>
		/// Returns an enumerator over the commands.
		/// </summary>
		/// <returns>The enumerator.</returns>
		public Enumerator GetEnumerator() => new Enumerator(this);

		/// <summary>
		/// Sends every command to <paramref name="sink"/>, in glyph space.
		/// </summary>
		/// <param name="sink">The receiver of the commands.</param>
		public void Visit(IGlyphPathSink sink)
		{
			Visit(sink, 1, 0, 0, 1, 0, 0);
		}

		/// <summary>
		/// Sends every command to <paramref name="sink"/>, transforming the points by the affine matrix
		/// [<paramref name="a"/> <paramref name="b"/> <paramref name="c"/> <paramref name="d"/>
		/// <paramref name="e"/> <paramref name="f"/>] (x' = a*x + c*y + e, y' = b*x + d*y + f).
		/// </summary>
		/// <param name="sink">The receiver of the commands.</param>
		/// <param name="a">The a component of the matrix.</param>
		/// <param name="b">The b component of the matrix.</param>
		/// <param name="c">The c component of the matrix.</param>
		/// <param name="d">The d component of the matrix.</param>
		/// <param name="e">The e component (x translation) of the matrix.</param>
		/// <param name="f">The f component (y translation) of the matrix.</param>
		public void Visit(IGlyphPathSink sink, double a, double b, double c, double d, double e, double f)
		{
			if (sink == null)
				return;

			var p = _coords;
			var k = 0;
			for (int i = 0; i < _verbs.Length; i++)
			{
				switch ((GlyphPathVerb)_verbs[i])
				{
					case GlyphPathVerb.MoveTo:
						sink.MoveTo(
							(float)(a * p[k] + c * p[k + 1] + e),
							(float)(b * p[k] + d * p[k + 1] + f));
						k += 2;
						break;

					case GlyphPathVerb.LineTo:
						sink.LineTo(
							(float)(a * p[k] + c * p[k + 1] + e),
							(float)(b * p[k] + d * p[k + 1] + f));
						k += 2;
						break;

					case GlyphPathVerb.CurveTo:
						sink.CurveTo(
							(float)(a * p[k] + c * p[k + 1] + e),
							(float)(b * p[k] + d * p[k + 1] + f),
							(float)(a * p[k + 2] + c * p[k + 3] + e),
							(float)(b * p[k + 2] + d * p[k + 3] + f),
							(float)(a * p[k + 4] + c * p[k + 5] + e),
							(float)(b * p[k + 4] + d * p[k + 5] + f));
						k += 6;
						break;

					case GlyphPathVerb.ClosePath:
						sink.ClosePath();
						break;
				}
			}
		}

		/// <summary>
		/// Returns the tight bounding box of the outline, including the extrema of the curves.
		/// All values are zero when the outline is empty.
		/// </summary>
		/// <param name="xMin">The minimum x coordinate.</param>
		/// <param name="yMin">The minimum y coordinate.</param>
		/// <param name="xMax">The maximum x coordinate.</param>
		/// <param name="yMax">The maximum y coordinate.</param>
		public void GetBounds(out float xMin, out float yMin, out float xMax, out float yMax)
		{
			if (!_boundsReady)
			{
				// computing twice on a race is harmless: both threads write the same values.
				ComputeBounds();
				_boundsReady = true;
			}

			xMin = _xMin;
			yMin = _yMin;
			xMax = _xMax;
			yMax = _yMax;
		}

		/// <summary>
		/// Returns a new outline with every point transformed by the affine matrix [a b c d e f].
		/// </summary>
		/// <param name="a">The a component of the matrix.</param>
		/// <param name="b">The b component of the matrix.</param>
		/// <param name="c">The c component of the matrix.</param>
		/// <param name="d">The d component of the matrix.</param>
		/// <param name="e">The e component (x translation) of the matrix.</param>
		/// <param name="f">The f component (y translation) of the matrix.</param>
		/// <returns>The transformed outline.</returns>
		public GlyphPath Transform(double a, double b, double c, double d, double e, double f)
		{
			if (this.IsEmpty)
				return this;

			var coords = new float[_coords.Length];
			for (int i = 0; i + 1 < coords.Length; i += 2)
			{
				var x = _coords[i];
				var y = _coords[i + 1];
				coords[i] = (float)(a * x + c * y + e);
				coords[i + 1] = (float)(b * x + d * y + f);
			}

			return new GlyphPath(_verbs, coords);
		}

		/// <summary>
		/// Returns a short description of the outline, for debugging.
		/// </summary>
		/// <returns>The description.</returns>
		public override string ToString() => $"GlyphPath ({_verbs.Length} commands)";

		#endregion

		#region Implementation

		// computes the bounding box from the on-curve points and the curve extrema.
		private void ComputeBounds()
		{
			if (_coords.Length < 2)
			{
				_xMin = _yMin = _xMax = _yMax = 0;
				return;
			}

			var bounds = new float[] { float.MaxValue, float.MaxValue, float.MinValue, float.MinValue };
			float cx = 0, cy = 0;
			var k = 0;
			for (int i = 0; i < _verbs.Length; i++)
			{
				switch ((GlyphPathVerb)_verbs[i])
				{
					case GlyphPathVerb.MoveTo:
					case GlyphPathVerb.LineTo:
						cx = _coords[k];
						cy = _coords[k + 1];
						Include(bounds, cx, cy);
						k += 2;
						break;

					case GlyphPathVerb.CurveTo:
						var x3 = _coords[k + 4];
						var y3 = _coords[k + 5];
						Include(bounds, x3, y3);

						// the control points only matter when they lie outside the box so far.
						IncludeCurveExtrema(bounds, 0, cx, _coords[k], _coords[k + 2], x3);
						IncludeCurveExtrema(bounds, 1, cy, _coords[k + 1], _coords[k + 3], y3);
						cx = x3;
						cy = y3;
						k += 6;
						break;
				}
			}

			_xMin = bounds[0];
			_yMin = bounds[1];
			_xMax = bounds[2];
			_yMax = bounds[3];
		}

		// extends the bounds with a point.
		private static void Include(float[] bounds, float x, float y)
		{
			if (x < bounds[0])
				bounds[0] = x;

			if (y < bounds[1])
				bounds[1] = y;

			if (x > bounds[2])
				bounds[2] = x;

			if (y > bounds[3])
				bounds[3] = y;
		}

		// extends one axis of the bounds with the extrema of the cubic p0..p3 on that axis,
		// found where the derivative is zero.
		private static void IncludeCurveExtrema(float[] bounds, int axis, double p0, double p1, double p2, double p3)
		{
			var min = bounds[axis];
			var max = bounds[axis + 2];
			if (p1 >= min && p1 <= max && p2 >= min && p2 <= max)
				return;

			// derivative: 3 * (a t^2 + b t + c).
			var a = -p0 + 3 * p1 - 3 * p2 + p3;
			var b = 2 * (p0 - 2 * p1 + p2);
			var c = p1 - p0;

			if (Math.Abs(a) < 1e-12)
			{
				if (Math.Abs(b) > 1e-12)
					IncludeAt(bounds, axis, -c / b, p0, p1, p2, p3);

				return;
			}

			var disc = b * b - 4 * a * c;
			if (disc < 0)
				return;

			var root = Math.Sqrt(disc);
			IncludeAt(bounds, axis, (-b + root) / (2 * a), p0, p1, p2, p3);
			IncludeAt(bounds, axis, (-b - root) / (2 * a), p0, p1, p2, p3);
		}

		// extends one axis of the bounds with the value of the cubic at t, when t is inside (0, 1).
		private static void IncludeAt(float[] bounds, int axis, double t, double p0, double p1, double p2, double p3)
		{
			if (!(t > 0 && t < 1))
				return;

			var mt = 1 - t;
			var v = (float)(mt * mt * mt * p0 + 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t * p3);
			if (v < bounds[axis])
				bounds[axis] = v;

			if (v > bounds[axis + 2])
				bounds[axis + 2] = v;
		}

		#endregion
	}

	/// <summary>
	/// Accumulates outline commands and produces an immutable <see cref="GlyphPath"/>.
	/// </summary>
	/// <remarks>
	/// The builder closes every open contour when a new one starts or when the path is built,
	/// and drops contours that consist only of a move.
	/// </remarks>
	internal sealed class GlyphPathBuilder : IGlyphPathSink
	{
		#region Fields

		// upper bound on the number of commands, to stop runaway charstrings from allocating without limit.
		private const int MaxVerbs = 1 << 20;

		// the commands collected so far; only the first _verbCount entries are used.
		private byte[] _verbs = new byte[64];

		// the number of commands in _verbs.
		private int _verbCount;

		// the coordinates collected so far; only the first _coordCount entries are used.
		private float[] _coords = new float[256];

		// the number of coordinates in _coords.
		private int _coordCount;

		// index of the pending MoveTo verb of the current contour, or -1 when no contour is open.
		private int _openMove = -1;

		// true when the current contour has at least one drawing command after its MoveTo.
		private bool _hasSegments;

		// the start point (_startX, _startY) and the current point (_x, _y) of the current contour.
		private float _startX, _startY, _x, _y;

		#endregion

		#region Properties

		/// <summary>
		/// Returns the x coordinate of the current point.
		/// </summary>
		public float CurrentX => _x;

		/// <summary>
		/// Returns the y coordinate of the current point.
		/// </summary>
		public float CurrentY => _y;

		/// <summary>
		/// Returns true when no command has been added.
		/// </summary>
		public bool IsEmpty => _verbCount == 0;

		#endregion

		#region Methods

		/// <summary>
		/// Discards all the commands so the builder can be reused.
		/// </summary>
		public void Clear()
		{
			_verbCount = 0;
			_coordCount = 0;
			_openMove = -1;
			_hasSegments = false;
			_startX = _startY = _x = _y = 0;
		}

		/// <summary>
		/// Starts a new contour, closing the current one.
		/// </summary>
		/// <param name="x">The x coordinate.</param>
		/// <param name="y">The y coordinate.</param>
		public void MoveTo(float x, float y)
		{
			if (_openMove >= 0 && !_hasSegments)
			{
				// a move right after a move replaces it.
				_coords[_coordCount - 2] = x;
				_coords[_coordCount - 1] = y;
			}
			else
			{
				ClosePath();
				if (!AddVerb(GlyphPathVerb.MoveTo, 2))
					return;

				_openMove = _verbCount - 1;
				_coords[_coordCount++] = x;
				_coords[_coordCount++] = y;
			}

			_hasSegments = false;
			_startX = _x = x;
			_startY = _y = y;
		}

		/// <summary>
		/// Adds a straight line from the current point.
		/// </summary>
		/// <param name="x">The x coordinate of the end point.</param>
		/// <param name="y">The y coordinate of the end point.</param>
		public void LineTo(float x, float y)
		{
			EnsureContour();
			if (!AddVerb(GlyphPathVerb.LineTo, 2))
				return;

			_coords[_coordCount++] = x;
			_coords[_coordCount++] = y;
			_hasSegments = true;
			_x = x;
			_y = y;
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
		public void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3)
		{
			EnsureContour();
			if (!AddVerb(GlyphPathVerb.CurveTo, 6))
				return;

			_coords[_coordCount++] = x1;
			_coords[_coordCount++] = y1;
			_coords[_coordCount++] = x2;
			_coords[_coordCount++] = y2;
			_coords[_coordCount++] = x3;
			_coords[_coordCount++] = y3;
			_hasSegments = true;
			_x = x3;
			_y = y3;
		}

		/// <summary>
		/// Adds a quadratic Bézier curve from the current point, converted to a cubic.
		/// </summary>
		/// <param name="cx">The x coordinate of the control point.</param>
		/// <param name="cy">The y coordinate of the control point.</param>
		/// <param name="x">The x coordinate of the end point.</param>
		/// <param name="y">The y coordinate of the end point.</param>
		public void QuadTo(float cx, float cy, float x, float y)
		{
			// the cubic control points sit two thirds of the way from each end point to the quadratic control point.
			var x0 = _x;
			var y0 = _y;
			CurveTo(
				x0 + 2f / 3f * (cx - x0), y0 + 2f / 3f * (cy - y0),
				x + 2f / 3f * (cx - x), y + 2f / 3f * (cy - y),
				x, y);
		}

		/// <summary>
		/// Closes the current contour, if one is open.
		/// </summary>
		public void ClosePath()
		{
			if (_openMove < 0)
				return;

			if (!_hasSegments)
			{
				// drop a contour made of a single move.
				_verbCount = _openMove;
				_coordCount -= 2;
			}
			else
			{
				AddVerb(GlyphPathVerb.ClosePath, 0);
			}

			_openMove = -1;
			_hasSegments = false;
			_x = _startX;
			_y = _startY;
		}

		/// <summary>
		/// Appends all the commands of <paramref name="path"/>, offset by (<paramref name="dx"/>, <paramref name="dy"/>).
		/// </summary>
		/// <param name="path">The outline to append.</param>
		/// <param name="dx">The horizontal offset.</param>
		/// <param name="dy">The vertical offset.</param>
		public void Append(GlyphPath path, float dx, float dy)
		{
			if (path == null || path.IsEmpty)
				return;

			ClosePath();
			path.Visit(this, 1, 0, 0, 1, dx, dy);
			ClosePath();
		}

		/// <summary>
		/// Creates the immutable outline from the commands added so far.
		/// </summary>
		/// <returns>The outline; <see cref="GlyphPath.Empty"/> when there are no commands.</returns>
		public GlyphPath ToPath()
		{
			ClosePath();
			if (_verbCount == 0)
				return GlyphPath.Empty;

			var verbs = new byte[_verbCount];
			Buffer.BlockCopy(_verbs, 0, verbs, 0, _verbCount);
			var coords = new float[_coordCount];
			Array.Copy(_coords, coords, _coordCount);
			return new GlyphPath(verbs, coords);
		}

		#endregion

		#region Implementation

		// opens an implicit contour at the current point when a segment is drawn without a move.
		private void EnsureContour()
		{
			if (_openMove < 0)
				MoveTo(_x, _y);
		}

		// appends a verb and makes room for its coordinates; returns false when the size cap is reached.
		private bool AddVerb(GlyphPathVerb verb, int coordinates)
		{
			if (_verbCount >= MaxVerbs)
				return false;

			if (_verbCount == _verbs.Length)
				Array.Resize(ref _verbs, _verbs.Length * 2);

			if (_coordCount + coordinates > _coords.Length)
				Array.Resize(ref _coords, Math.Max(_coords.Length * 2, _coordCount + coordinates));

			_verbs[_verbCount++] = (byte)verb;
			return true;
		}

		#endregion
	}
}
