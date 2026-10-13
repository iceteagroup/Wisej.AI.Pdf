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

// Stroking (ISO 32000-2, 8.5.3.2): converts polylines to the outline of the
// stroke, with line caps, line joins, the miter limit and dash patterns.

using System;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// The parameters of a stroke, in user space.
	/// </summary>
	internal struct StrokeStyle
	{
		/// <summary>The line width in user space units.</summary>
		public double Width;

		/// <summary>The line cap: 0 butt, 1 round, 2 projecting square.</summary>
		public int Cap;

		/// <summary>The line join: 0 miter, 1 round, 2 bevel.</summary>
		public int Join;

		/// <summary>The miter limit.</summary>
		public double MiterLimit;

		/// <summary>The dash array, or null for solid lines.</summary>
		public double[] Dash;

		/// <summary>The dash phase.</summary>
		public double DashPhase;
	}

	/// <summary>
	/// Converts flattened paths to stroke outlines and adds them to a <see cref="Rasterizer"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The stroke is computed in user space, where the pen is a circle of the line width, and then
	/// mapped to device space; this gives the elliptic pens of non-uniform transformations for free.
	/// </para>
	/// <para>
	/// The outline is the union of simple pieces: one quadrilateral per segment plus the joins and caps.
	/// Every piece is emitted with the same orientation, so the non-zero rule merges them seamlessly.
	/// </para>
	/// </remarks>
	internal sealed class Stroker
	{
		// the thinnest stroke painted, in device pixels, as viewers do for hairlines.
		private const double MinDeviceWidth = 1.0;

		// above this many dashes per stroke the pattern is invisible anyway: stroke solid.
		private const int MaxDashes = 100_000;

		// user space -> device space (linear part) and its inverse.
		private double _a, _b, _c, _d;
		// the inverse, from device space to user space.
		private double _ia, _ib, _ic, _id;

		// the stroke parameters: half the line width in user space, and the number of segments of a full circle.
		private double _halfWidth;
		// the line cap and line join styles.
		private int _cap, _join;
		// the miter limit.
		private double _miterLimit;
		// the number of segments of a full circle, for round joins and caps.
		private int _arcSegments;

		// the rasterizer receiving the outline during Stroke.
		private Rasterizer _rasterizer;

		// the current polyline in user space.
		private double[] _px = new double[256];
		// the y coordinates of the current polyline.
		private double[] _py = new double[256];
		// the number of points of the current polyline.
		private int _count;

		// dash output.
		private double[] _dx = new double[256];
		// the y coordinates of the current dash.
		private double[] _dy = new double[256];

		// one piece of the outline.
		private readonly double[] _pieceX = new double[512];
		// the y coordinates of the piece.
		private readonly double[] _pieceY = new double[512];
		// the number of points of the piece.
		private int _pieceCount;

		#region Methods

		/// <summary>
		/// Strokes the polylines.
		/// </summary>
		/// <param name="lines">The flattened path in device space.</param>
		/// <param name="ctm">The transformation from user space to device space; only its linear part is used.</param>
		/// <param name="style">The stroke parameters.</param>
		/// <param name="rasterizer">Receives the outline.</param>
		public void Stroke(Polylines lines, Matrix ctm, StrokeStyle style, Rasterizer rasterizer)
		{
			_rasterizer = rasterizer;
			SetupTransform(ctm, style.Width);

			_cap = style.Cap;
			_join = style.Join;
			_miterLimit = style.MiterLimit >= 1 ? style.MiterLimit : 10;

			// arc segments for round joins and caps, from the device radius.
			var deviceRadius = _halfWidth * Math.Sqrt(Math.Max(_a * _a + _b * _b, _c * _c + _d * _d));
			_arcSegments = deviceRadius <= 1 ? 8 : (int)Math.Min(128, Math.Max(8, Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - 0.2 / deviceRadius)))));

			var dash = NormalizeDash(style.Dash, out var dashLength);

			for (int s = 0; s < lines.SubpathCount; s++)
			{
				var closed = lines.GetSubpath(s, out var start, out var end);
				if (end - start < 2)
					continue;

				LoadPolyline(lines, start, end, ref closed);

				if (_count == 1)
				{
					// a degenerate subpath is painted as a dot with round caps only.
					if (_cap == 1)
						EmitCircle(_px[0], _py[0]);
					continue;
				}

				if (dash != null)
					StrokeDashed(closed, dash, dashLength, style.DashPhase);
				else
					StrokePolyline(_px, _py, _count, closed);
			}

			_rasterizer = null;
		}

		#endregion

		#region Implementation

		// computes the user <-> device transformation and the half width in user space.
		private void SetupTransform(Matrix ctm, double width)
		{
			_a = ctm.A;
			_b = ctm.B;
			_c = ctm.C;
			_d = ctm.D;

			// singular values of the linear part: the smallest gives the thinnest device width of the pen.
			var det = _a * _d - _b * _c;
			var e = (_a * _a + _b * _b + _c * _c + _d * _d) / 2;
			var root = Math.Sqrt(Math.Max(0, e * e - det * det));
			var sMax = Math.Sqrt(e + root);
			var sMin = Math.Sqrt(Math.Max(0, e - root));

			if (!(Math.Abs(det) > 1e-12) || sMin < sMax * 1e-3 || double.IsNaN(det))
			{
				// degenerate or extreme transformations: stroke in device space with a conformal width.
				var scale = Math.Sqrt(Math.Abs(det));
				if (!(scale > 0) || double.IsNaN(scale))
					scale = Math.Max(sMax, 1e-9);

				_a = _d = 1;
				_b = _c = 0;
				_ia = _id = 1;
				_ib = _ic = 0;
				_halfWidth = Math.Max(MinDeviceWidth, Math.Abs(width) * scale) / 2;
				return;
			}

			_ia = _d / det;
			_ib = -_b / det;
			_ic = -_c / det;
			_id = _a / det;

			// hairlines and thin lines are widened to one device pixel.
			var w = Math.Abs(width);
			if (w * sMin < MinDeviceWidth)
				w = MinDeviceWidth / sMin;

			_halfWidth = w / 2;
		}

		// maps a subpath to user space, removing repeated points.
		private void LoadPolyline(Polylines lines, int start, int end, ref bool closed)
		{
			var n = end - start;
			if (_px.Length < n + 1)
			{
				_px = new double[n + 1];
				_py = new double[n + 1];
			}

			var xs = lines.X;
			var ys = lines.Y;
			var epsilon = _halfWidth * 1e-6;
			_count = 0;
			for (int i = start; i < end; i++)
			{
				var x = xs[i] * _ia + ys[i] * _ic;
				var y = xs[i] * _ib + ys[i] * _id;
				if (_count > 0 && Math.Abs(x - _px[_count - 1]) <= epsilon && Math.Abs(y - _py[_count - 1]) <= epsilon)
					continue;

				_px[_count] = x;
				_py[_count] = y;
				_count++;
			}

			// a closed polyline ending on its start point: the closing segment is implicit.
			if (closed && _count > 1 && Math.Abs(_px[_count - 1] - _px[0]) <= epsilon && Math.Abs(_py[_count - 1] - _py[0]) <= epsilon)
				_count--;

			if (_count == 2 && closed)
			{
				// a closed two-point path is a line drawn back and forth: stroke it as open with joins at both ends.
				_px[2] = _px[0];
				_py[2] = _py[0];
				_count = 3;
				closed = false;
			}
		}

		// returns the dash array when it has a positive length, and its total length.
		private static double[] NormalizeDash(double[] dash, out double length)
		{
			length = 0;
			if (dash == null || dash.Length == 0)
				return null;

			for (int i = 0; i < dash.Length; i++)
			{
				if (dash[i] < 0 || double.IsNaN(dash[i]))
					return null;

				length += dash[i];
			}

			// an odd count repeats the array, so on and off alternate correctly.
			if (dash.Length % 2 == 1)
				length *= 2;

			return length > 0 ? dash : null;
		}

		// splits the polyline in dashes and strokes each one as an open polyline.
		private void StrokeDashed(bool closed, double[] dash, double patternLength, double phase)
		{
			var n = closed ? _count + 1 : _count;
			if (closed)
			{
				_px[_count] = _px[0];
				_py[_count] = _py[0];
			}

			// too many dashes: they would blend into a solid line.
			double total = 0;
			for (int i = 1; i < n; i++)
				total += Distance(_px[i - 1], _py[i - 1], _px[i], _py[i]);

			if (total / patternLength * dash.Length > MaxDashes)
			{
				StrokePolyline(_px, _py, _count, closed);
				return;
			}

			// find the dash and the offset in it where the phase starts.
			var count = dash.Length % 2 == 1 ? dash.Length * 2 : dash.Length;
			phase %= patternLength;
			if (phase < 0)
				phase += patternLength;

			// the phase is shorter than the pattern, so this ends within one pass.
			var index = 0;
			for (int guard = 0; guard < count; guard++)
			{
				var length = dash[index % dash.Length];

				// a phase at the start of a dash selects it, even when it has zero length (a dot).
				if (phase < length || phase <= 0)
					break;

				phase -= length;
				index = (index + 1) % count;
			}

			var remaining = dash[index % dash.Length] - phase;
			var on = index % 2 == 0;
			var dashCount = 0;
			var dot = false;

			if (on)
			{
				AddDashPoint(ref dashCount, _px[0], _py[0]);

				// a zero-length dash at the start is a dot: give it a direction.
				if (remaining <= 0)
				{
					var length = Distance(_px[0], _py[0], _px[1], _py[1]);
					AddDashPoint(ref dashCount, _px[0] + (_px[1] - _px[0]) / length * 1e-6, _py[0] + (_py[1] - _py[0]) / length * 1e-6);
					dot = true;
				}
			}

			for (int i = 1; i < n; i++)
			{
				double x0 = _px[i - 1], y0 = _py[i - 1], x1 = _px[i], y1 = _py[i];
				var length = Distance(x0, y0, x1, y1);
				var position = 0.0;

				while (length - position > remaining)
				{
					position += remaining;
					var t = position / length;
					var x = x0 + (x1 - x0) * t;
					var y = y0 + (y1 - y0) * t;

					if (on)
					{
						// the end of a dot is already the point added to give it a direction.
						if (!dot)
							AddDashPoint(ref dashCount, x, y);

						EmitDash(dashCount);
						dashCount = 0;
					}
					else
					{
						dashCount = 0;
						AddDashPoint(ref dashCount, x, y);
					}

					on = !on;
					dot = false;
					index = (index + 1) % count;
					remaining = dash[index % dash.Length];

					// zero-length dashes are dots: they need at least two points to have a direction.
					if (remaining <= 0 && on)
					{
						AddDashPoint(ref dashCount, x + (x1 - x0) / length * 1e-6, y + (y1 - y0) / length * 1e-6);
						dot = true;
					}
				}

				remaining -= length - position;
				if (on && !dot)
					AddDashPoint(ref dashCount, x1, y1);
			}

			if (on && dashCount > 1)
				EmitDash(dashCount);
		}

		// appends a point to the current dash, growing the buffers when needed.
		private void AddDashPoint(ref int count, double x, double y)
		{
			if (count == _dx.Length)
			{
				Array.Resize(ref _dx, count * 2);
				Array.Resize(ref _dy, count * 2);
			}

			_dx[count] = x;
			_dy[count] = y;
			count++;
		}

		// strokes the current dash when it has at least one segment.
		private void EmitDash(int count)
		{
			if (count >= 2)
				StrokePolyline(_dx, _dy, count, false);
		}

		// strokes one polyline in user space.
		private void StrokePolyline(double[] xs, double[] ys, int count, bool closed)
		{
			if (count < 2)
				return;

			var hw = _halfWidth;
			var segments = closed ? count : count - 1;

			for (int i = 0; i < segments; i++)
			{
				var j = (i + 1) % count;
				double x0 = xs[i], y0 = ys[i], x1 = xs[j], y1 = ys[j];
				var length = Distance(x0, y0, x1, y1);
				if (length <= 0)
					continue;

				// the segment body.
				var nx = -(y1 - y0) / length * hw;
				var ny = (x1 - x0) / length * hw;
				Begin();
				Add(x0 + nx, y0 + ny);
				Add(x1 + nx, y1 + ny);
				Add(x1 - nx, y1 - ny);
				Add(x0 - nx, y0 - ny);
				EmitPiece();

				// the join with the next segment.
				if (closed || j < count - 1)
				{
					var k = (j + 1) % count;
					EmitJoin(xs[i], ys[i], xs[j], ys[j], xs[k], ys[k]);
				}
			}

			if (!closed)
			{
				EmitCap(xs[0], ys[0], xs[1], ys[1]);
				EmitCap(xs[count - 1], ys[count - 1], xs[count - 2], ys[count - 2]);
			}
		}

		// the join at (x1, y1) between the segments from (x0, y0) and to (x2, y2).
		private void EmitJoin(double x0, double y0, double x1, double y1, double x2, double y2)
		{
			var l0 = Distance(x0, y0, x1, y1);
			var l1 = Distance(x1, y1, x2, y2);
			if (l0 <= 0 || l1 <= 0)
				return;

			var d0x = (x1 - x0) / l0;
			var d0y = (y1 - y0) / l0;
			var d1x = (x2 - x1) / l1;
			var d1y = (y2 - y1) / l1;
			var cross = d0x * d1y - d0y * d1x;
			var dot = d0x * d1x + d0y * d1y;

			// straight continuation: nothing to fill.
			if (Math.Abs(cross) < 1e-9 && dot > 0)
				return;

			var hw = _halfWidth;

			// the outer side of the turn is opposite to the turning direction.
			var sign = cross > 0 ? -1 : 1;
			var n0x = -d0y * sign;
			var n0y = d0x * sign;
			var n1x = -d1y * sign;
			var n1y = d1x * sign;

			var join = _join;

			// tiny angles (flattened curves): a miter is indistinguishable and cheaper.
			if (dot > 0.995)
				join = 0;

			if (join == 0)
			{
				// miter length / line width = 1 / sin(phi / 2), phi being the angle between the segments.
				var cosHalf = Math.Sqrt(Math.Max(0, (1 + dot) / 2));
				if (cosHalf > 1e-9 && 1 / cosHalf <= _miterLimit)
				{
					var mx = x1 + (n0x + n1x) * hw / (1 + dot);
					var my = y1 + (n0y + n1y) * hw / (1 + dot);
					Begin();
					Add(x1, y1);
					Add(x1 + n0x * hw, y1 + n0y * hw);
					Add(mx, my);
					Add(x1 + n1x * hw, y1 + n1y * hw);
					EmitPiece();
					return;
				}

				join = 2;
			}

			if (join == 1)
			{
				// a pie slice from the end of the first segment to the start of the second.
				var a0 = Math.Atan2(n0y, n0x);
				var a1 = Math.Atan2(n1y, n1x);
				var sweep = a1 - a0;
				while (sweep > Math.PI)
					sweep -= 2 * Math.PI;
				while (sweep < -Math.PI)
					sweep += 2 * Math.PI;

				var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (2 * Math.PI) * _arcSegments));
				Begin();
				Add(x1, y1);
				for (int i = 0; i <= steps; i++)
				{
					var angle = a0 + sweep * i / steps;
					Add(x1 + Math.Cos(angle) * hw, y1 + Math.Sin(angle) * hw);
				}

				EmitPiece();
				return;
			}

			// bevel.
			Begin();
			Add(x1, y1);
			Add(x1 + n0x * hw, y1 + n0y * hw);
			Add(x1 + n1x * hw, y1 + n1y * hw);
			EmitPiece();
		}

		// the cap at (x0, y0), at the end of a segment coming from (x1, y1).
		private void EmitCap(double x0, double y0, double x1, double y1)
		{
			if (_cap == 0)
				return;

			var length = Distance(x0, y0, x1, y1);
			if (length <= 0)
				return;

			var hw = _halfWidth;
			var dx = (x0 - x1) / length * hw;
			var dy = (y0 - y1) / length * hw;

			if (_cap == 2)
			{
				// projecting square: a rectangle extending half the width beyond the end.
				Begin();
				Add(x0 - dy, y0 + dx);
				Add(x0 - dy + dx, y0 + dx + dy);
				Add(x0 + dy + dx, y0 - dx + dy);
				Add(x0 + dy, y0 - dx);
				EmitPiece();
				return;
			}

			// round: a half disc.
			var start = Math.Atan2(dx, -dy);
			var steps = Math.Max(2, _arcSegments / 2);
			Begin();
			for (int i = 0; i <= steps; i++)
			{
				var angle = start - Math.PI * i / steps;
				Add(x0 + Math.Cos(angle) * hw, y0 + Math.Sin(angle) * hw);
			}

			EmitPiece();
		}

		// emits a full disc at (x, y), the round dot of a degenerate subpath.
		private void EmitCircle(double x, double y)
		{
			var hw = _halfWidth;
			Begin();
			for (int i = 0; i < _arcSegments; i++)
			{
				var angle = 2 * Math.PI * i / _arcSegments;
				Add(x + Math.Cos(angle) * hw, y + Math.Sin(angle) * hw);
			}

			EmitPiece();
		}

		#region Pieces

		// starts a new piece.
		private void Begin() => _pieceCount = 0;

		// appends a point to the piece; points beyond its capacity are dropped.
		private void Add(double x, double y)
		{
			if (_pieceCount < _pieceX.Length)
			{
				_pieceX[_pieceCount] = x;
				_pieceY[_pieceCount] = y;
				_pieceCount++;
			}
		}

		// maps the piece to device space and adds its edges, always with a positive orientation in user space.
		private void EmitPiece()
		{
			var n = _pieceCount;
			if (n < 3)
				return;

			var xs = _pieceX;
			var ys = _pieceY;
			double area = 0;
			for (int i = 0, j = n - 1; i < n; j = i++)
				area += (xs[j] - xs[i]) * (ys[j] + ys[i]);

			var reverse = area < 0;
			var r = _rasterizer;
			for (int i = 0; i < n; i++)
			{
				var j = i + 1 == n ? 0 : i + 1;
				var ax = xs[i] * _a + ys[i] * _c;
				var ay = xs[i] * _b + ys[i] * _d;
				var bx = xs[j] * _a + ys[j] * _c;
				var by = xs[j] * _b + ys[j] * _d;
				if (reverse)
					r.AddLine(bx, by, ax, ay);
				else
					r.AddLine(ax, ay, bx, by);
			}
		}

		#endregion

		// the distance between two points.
		private static double Distance(double x0, double y0, double x1, double y1)
		{
			var dx = x1 - x0;
			var dy = y1 - y0;
			return Math.Sqrt(dx * dx + dy * dy);
		}

		#endregion
	}
}
