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

// Accumulates the current path in display space (ISO 32000-2, 8.5.2).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Content
{
	/// <summary>
	/// Builds the current path, keeping what layout analysis needs: straight segments,
	/// axis-aligned rectangles, bounds and complexity.
	/// </summary>
	internal sealed class PathBuilder
	{
		// tolerance in points to consider a segment horizontal or vertical.
		private const double AxisTolerance = 0.5;

		// straight segments of the path (x0, y0, x1, y1).
		private List<double> _segments = new List<double>();
		// axis-aligned rectangles recognized in the path.
		private List<PdfRect> _rectangles = new List<PdfRect>();

		// points of the current subpath, used to recognize rectangles drawn with m/l/l/l/h.
		private readonly List<PdfPoint> _subpath = new List<PdfPoint>(8);
		// true when the current subpath contains curves.
		private bool _subpathHasCurves;

		// the start point of the current subpath (target of h).
		private PdfPoint _start;
		// the current point.
		private PdfPoint _current;
		// true when there is a current point.
		private bool _hasCurrent;

		// the bounds of all the points added to the path.
		private double _left, _top, _right, _bottom;

		/// <summary>
		/// Initializes a new instance of <see cref="PathBuilder"/>.
		/// </summary>
		public PathBuilder()
		{
			Reset();
		}

		#region Properties

		/// <summary>
		/// True when the path has no points.
		/// </summary>
		public bool IsEmpty => this.PointCount == 0;

		/// <summary>
		/// The current point, or the origin when there is none.
		/// </summary>
		public PdfPoint CurrentPoint => _current;

		/// <summary>
		/// The bounds of all the points of the path, including curve control points.
		/// </summary>
		public PdfRect Bounds => this.PointCount == 0 ? PdfRect.Empty : new PdfRect(_left, _top, _right, _bottom);

		/// <summary>
		/// The straight segments as (x0, y0, x1, y1) quadruples.
		/// </summary>
		public List<double> Segments => _segments;

		/// <summary>
		/// The axis-aligned rectangles of the path.
		/// </summary>
		public List<PdfRect> Rectangles => _rectangles;

		/// <summary>
		/// True when the path contains curves.
		/// </summary>
		public bool HasCurves { get; private set; }

		/// <summary>
		/// The number of points added to the path.
		/// </summary>
		public int PointCount { get; private set; }

		#endregion

		#region Methods

		/// <summary>
		/// Starts a new subpath at <paramref name="point"/>.
		/// </summary>
		/// <param name="point">The start point.</param>
		public void MoveTo(PdfPoint point)
		{
			FinishSubpath();

			_start = point;
			_current = point;
			_hasCurrent = true;
			_subpath.Add(point);
			AddPoint(point);
		}

		/// <summary>
		/// Adds a straight segment from the current point to <paramref name="point"/>.
		/// </summary>
		/// <param name="point">The end point.</param>
		public void LineTo(PdfPoint point)
		{
			if (!_hasCurrent)
			{
				// a line without a current point starts a subpath (lenient).
				MoveTo(point);
				return;
			}

			AddSegment(_current, point);
			_current = point;
			_subpath.Add(point);
			AddPoint(point);
		}

		/// <summary>
		/// Adds a cubic Bézier curve from the current point.
		/// </summary>
		/// <param name="control1">The first control point.</param>
		/// <param name="control2">The second control point.</param>
		/// <param name="end">The end point.</param>
		public void CurveTo(PdfPoint control1, PdfPoint control2, PdfPoint end)
		{
			if (!_hasCurrent)
				MoveTo(control1);

			this.HasCurves = true;
			_subpathHasCurves = true;

			AddPoint(control1);
			AddPoint(control2);
			AddPoint(end);

			_current = end;
			_subpath.Add(end);
		}

		/// <summary>
		/// Closes the current subpath with a segment back to its start.
		/// </summary>
		public void ClosePath()
		{
			if (!_hasCurrent)
				return;

			if (_current.X != _start.X || _current.Y != _start.Y)
				AddSegment(_current, _start);

			_current = _start;
			_subpath.Add(_start);
			FinishSubpath();
		}

		/// <summary>
		/// Adds a closed rectangle subpath (the <c>re</c> operator), given its four transformed corners.
		/// </summary>
		/// <param name="p0">The corner at (x, y).</param>
		/// <param name="p1">The corner at (x + w, y).</param>
		/// <param name="p2">The corner at (x + w, y + h).</param>
		/// <param name="p3">The corner at (x, y + h).</param>
		public void Rectangle(PdfPoint p0, PdfPoint p1, PdfPoint p2, PdfPoint p3)
		{
			MoveTo(p0);
			LineTo(p1);
			LineTo(p2);
			LineTo(p3);
			ClosePath();
		}

		/// <summary>
		/// Completes the current subpath so its rectangle, if any, is recognized. Called before the path is painted.
		/// </summary>
		public void EndPath() => FinishSubpath();

		/// <summary>
		/// Clears the path. The segment and rectangle lists are replaced so the previous ones can be kept by the caller.
		/// </summary>
		public void Reset()
		{
			_segments = new List<double>();
			_rectangles = new List<PdfRect>();
			_subpath.Clear();
			_subpathHasCurves = false;
			_hasCurrent = false;
			_current = default;
			_start = default;

			this.HasCurves = false;
			this.PointCount = 0;
			_left = _top = double.MaxValue;
			_right = _bottom = double.MinValue;
		}

		#endregion

		#region Implementation

		/// <summary>
		/// Counts a point and grows the bounds.
		/// </summary>
		/// <param name="point">The point.</param>
		private void AddPoint(PdfPoint point)
		{
			this.PointCount++;
			if (point.X < _left)
				_left = point.X;
			if (point.X > _right)
				_right = point.X;
			if (point.Y < _top)
				_top = point.Y;
			if (point.Y > _bottom)
				_bottom = point.Y;
		}

		/// <summary>
		/// Records a straight segment.
		/// </summary>
		/// <param name="from">The start point.</param>
		/// <param name="to">The end point.</param>
		private void AddSegment(PdfPoint from, PdfPoint to)
		{
			_segments.Add(from.X);
			_segments.Add(from.Y);
			_segments.Add(to.X);
			_segments.Add(to.Y);
		}

		// recognizes axis-aligned rectangles: 4 corners (5 points when explicitly closed), straight edges only.
		private void FinishSubpath()
		{
			var count = _subpath.Count;
			if (!_subpathHasCurves && (count == 5 || count == 4))
			{
				var p0 = _subpath[0];
				var p1 = _subpath[1];
				var p2 = _subpath[2];
				var p3 = _subpath[3];

				var closed = count == 4 || (Math.Abs(_subpath[4].X - p0.X) < AxisTolerance && Math.Abs(_subpath[4].Y - p0.Y) < AxisTolerance);
				if (closed && IsAxisAligned(p0, p1) && IsAxisAligned(p1, p2) && IsAxisAligned(p2, p3) && IsAxisAligned(p3, p0)
					&& !IsSamePoint(p0, p2))
				{
					var left = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
					var right = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
					var top = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
					var bottom = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
					_rectangles.Add(new PdfRect(left, top, right, bottom));
				}
			}

			_subpath.Clear();
			_subpathHasCurves = false;
		}

		// true when the segment is horizontal or vertical within the tolerance.
		private static bool IsAxisAligned(PdfPoint a, PdfPoint b) => Math.Abs(a.X - b.X) < AxisTolerance || Math.Abs(a.Y - b.Y) < AxisTolerance;

		// true when the points coincide within the tolerance.
		private static bool IsSamePoint(PdfPoint a, PdfPoint b) => Math.Abs(a.X - b.X) < AxisTolerance && Math.Abs(a.Y - b.Y) < AxisTolerance;

		#endregion
	}
}
