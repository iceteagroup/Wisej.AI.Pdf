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

// Geometric primitives: points, rectangles and affine matrices.

using System;
using System.Globalization;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// A point in two-dimensional space.
	/// </summary>
	public readonly struct PdfPoint
	{
		/// <summary>
		/// The horizontal coordinate.
		/// </summary>
		public readonly double X;

		/// <summary>
		/// The vertical coordinate.
		/// </summary>
		public readonly double Y;

		/// <summary>
		/// Initializes a new <see cref="PdfPoint"/>.
		/// </summary>
		/// <param name="x">The horizontal coordinate.</param>
		/// <param name="y">The vertical coordinate.</param>
		public PdfPoint(double x, double y)
		{
			this.X = x;
			this.Y = y;
		}

		/// <inheritdoc/>
		public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##})", this.X, this.Y);
	}

	/// <summary>
	/// An axis-aligned rectangle.
	/// </summary>
	/// <remarks>
	/// Rectangles returned by the extraction API use <b>top-left origin page coordinates</b>
	/// in points (1/72 inch): <see cref="Left"/> grows to the right and <see cref="Top"/>
	/// grows downward, as in HTML and image coordinates, already corrected for the page rotation.
	/// </remarks>
	public readonly struct PdfRect : IEquatable<PdfRect>
	{
		/// <summary>
		/// An empty rectangle at the origin.
		/// </summary>
		public static readonly PdfRect Empty = new PdfRect(0, 0, 0, 0);

		/// <summary>
		/// The left edge.
		/// </summary>
		public readonly double Left;

		/// <summary>
		/// The top edge.
		/// </summary>
		public readonly double Top;

		/// <summary>
		/// The right edge.
		/// </summary>
		public readonly double Right;

		/// <summary>
		/// The bottom edge.
		/// </summary>
		public readonly double Bottom;

		/// <summary>
		/// Initializes a new <see cref="PdfRect"/> from its edges. The edges are normalized so that
		/// <see cref="Left"/> &lt;= <see cref="Right"/> and <see cref="Top"/> &lt;= <see cref="Bottom"/>.
		/// </summary>
		/// <param name="left">The left edge.</param>
		/// <param name="top">The top edge.</param>
		/// <param name="right">The right edge.</param>
		/// <param name="bottom">The bottom edge.</param>
		public PdfRect(double left, double top, double right, double bottom)
		{
			this.Left = Math.Min(left, right);
			this.Right = Math.Max(left, right);
			this.Top = Math.Min(top, bottom);
			this.Bottom = Math.Max(top, bottom);
		}

		/// <summary>
		/// The width of the rectangle.
		/// </summary>
		public double Width => this.Right - this.Left;

		/// <summary>
		/// The height of the rectangle.
		/// </summary>
		public double Height => this.Bottom - this.Top;

		/// <summary>
		/// The area of the rectangle.
		/// </summary>
		public double Area => this.Width * this.Height;

		/// <summary>
		/// The horizontal center.
		/// </summary>
		public double CenterX => (this.Left + this.Right) / 2;

		/// <summary>
		/// The vertical center.
		/// </summary>
		public double CenterY => (this.Top + this.Bottom) / 2;

		/// <summary>
		/// True when the rectangle has no area.
		/// </summary>
		public bool IsEmpty => this.Width <= 0 || this.Height <= 0;

		/// <summary>
		/// Returns the smallest rectangle that contains both rectangles.
		/// </summary>
		/// <param name="other">The other rectangle.</param>
		/// <returns>The union.</returns>
		public PdfRect Union(PdfRect other)
		{
			if (this.IsEmpty && this.Left == 0 && this.Top == 0)
				return other;
			if (other.IsEmpty && other.Left == 0 && other.Top == 0)
				return this;

			return new PdfRect(
				Math.Min(this.Left, other.Left), Math.Min(this.Top, other.Top),
				Math.Max(this.Right, other.Right), Math.Max(this.Bottom, other.Bottom));
		}

		/// <summary>
		/// Returns the intersection of both rectangles.
		/// </summary>
		/// <param name="other">The other rectangle.</param>
		/// <returns>The intersection, or <see cref="Empty"/> when they don't overlap.</returns>
		public PdfRect Intersect(PdfRect other)
		{
			var left = Math.Max(this.Left, other.Left);
			var top = Math.Max(this.Top, other.Top);
			var right = Math.Min(this.Right, other.Right);
			var bottom = Math.Min(this.Bottom, other.Bottom);

			return left < right && top < bottom ? new PdfRect(left, top, right, bottom) : Empty;
		}

		/// <summary>
		/// Returns true when the rectangles overlap.
		/// </summary>
		/// <param name="other">The other rectangle.</param>
		/// <returns>True when the intersection has a positive area.</returns>
		public bool IntersectsWith(PdfRect other)
		{
			return other.Left < this.Right && other.Right > this.Left && other.Top < this.Bottom && other.Bottom > this.Top;
		}

		/// <summary>
		/// Returns true when <paramref name="other"/> is entirely inside this rectangle, within <paramref name="tolerance"/>.
		/// </summary>
		/// <param name="other">The rectangle to test.</param>
		/// <param name="tolerance">The tolerance applied to each edge.</param>
		/// <returns>True when contained.</returns>
		public bool Contains(PdfRect other, double tolerance = 0)
		{
			return other.Left >= this.Left - tolerance && other.Right <= this.Right + tolerance
				&& other.Top >= this.Top - tolerance && other.Bottom <= this.Bottom + tolerance;
		}

		/// <summary>
		/// Returns true when the point is inside the rectangle.
		/// </summary>
		/// <param name="x">The horizontal coordinate.</param>
		/// <param name="y">The vertical coordinate.</param>
		/// <returns>True when contained.</returns>
		public bool Contains(double x, double y) => x >= this.Left && x <= this.Right && y >= this.Top && y <= this.Bottom;

		/// <summary>
		/// Returns a rectangle enlarged by <paramref name="amount"/> on every side.
		/// </summary>
		/// <param name="amount">The amount to add to each side; negative values shrink the rectangle.</param>
		/// <returns>The inflated rectangle.</returns>
		public PdfRect Inflate(double amount) => new PdfRect(this.Left - amount, this.Top - amount, this.Right + amount, this.Bottom + amount);

		/// <summary>
		/// Returns the fraction of this rectangle's area covered by <paramref name="other"/>.
		/// </summary>
		/// <param name="other">The covering rectangle.</param>
		/// <returns>A value between 0 and 1.</returns>
		public double CoverageBy(PdfRect other)
		{
			var area = this.Area;
			return area <= 0 ? 0 : Intersect(other).Area / area;
		}

		/// <inheritdoc/>
		public bool Equals(PdfRect other) => this.Left == other.Left && this.Top == other.Top && this.Right == other.Right && this.Bottom == other.Bottom;

		/// <inheritdoc/>
		public override bool Equals(object obj) => obj is PdfRect other && Equals(other);

		/// <inheritdoc/>
		public override int GetHashCode() => (this.Left, this.Top, this.Right, this.Bottom).GetHashCode();

		/// <inheritdoc/>
		public override string ToString() => string.Format(CultureInfo.InvariantCulture, "[{0:0.##}, {1:0.##}, {2:0.##}, {3:0.##}]", this.Left, this.Top, this.Right, this.Bottom);
	}

	/// <summary>
	/// A 3x2 affine transformation matrix <c>[a b c d e f]</c> as defined by PDF.
	/// </summary>
	/// <remarks>
	/// Points are row vectors: <c>x' = a*x + c*y + e</c>, <c>y' = b*x + d*y + f</c>.
	/// </remarks>
	internal readonly struct Matrix
	{
		/// <summary>
		/// The identity matrix.
		/// </summary>
		public static readonly Matrix Identity = new Matrix(1, 0, 0, 1, 0, 0);

		/// <summary>The a element (horizontal scaling).</summary>
		public readonly double A;

		/// <summary>The b element.</summary>
		public readonly double B;

		/// <summary>The c element.</summary>
		public readonly double C;

		/// <summary>The d element (vertical scaling).</summary>
		public readonly double D;

		/// <summary>The e element (horizontal translation).</summary>
		public readonly double E;

		/// <summary>The f element (vertical translation).</summary>
		public readonly double F;

		/// <summary>
		/// Initializes a new <see cref="Matrix"/>.
		/// </summary>
		/// <param name="a">The a element.</param>
		/// <param name="b">The b element.</param>
		/// <param name="c">The c element.</param>
		/// <param name="d">The d element.</param>
		/// <param name="e">The e element.</param>
		/// <param name="f">The f element.</param>
		public Matrix(double a, double b, double c, double d, double e, double f)
		{
			this.A = a;
			this.B = b;
			this.C = c;
			this.D = d;
			this.E = e;
			this.F = f;
		}

		/// <summary>
		/// Creates a matrix from a PDF array of six numbers.
		/// </summary>
		/// <param name="values">The values; when null or shorter than six, the identity matrix is returned.</param>
		/// <returns>The matrix.</returns>
		public static Matrix FromArray(double[] values)
		{
			if (values == null || values.Length < 6)
				return Identity;

			return new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]);
		}

		/// <summary>
		/// Returns <c>this × other</c>: the transformation <c>this</c> followed by <paramref name="other"/>.
		/// </summary>
		/// <param name="other">The matrix applied after this one.</param>
		/// <returns>The product.</returns>
		public Matrix Multiply(Matrix other)
		{
			return new Matrix(
				this.A * other.A + this.B * other.C,
				this.A * other.B + this.B * other.D,
				this.C * other.A + this.D * other.C,
				this.C * other.B + this.D * other.D,
				this.E * other.A + this.F * other.C + other.E,
				this.E * other.B + this.F * other.D + other.F);
		}

		/// <summary>
		/// Transforms the point (<paramref name="x"/>, <paramref name="y"/>).
		/// </summary>
		/// <param name="x">The horizontal coordinate.</param>
		/// <param name="y">The vertical coordinate.</param>
		/// <returns>The transformed point.</returns>
		public PdfPoint Transform(double x, double y) => new PdfPoint(this.A * x + this.C * y + this.E, this.B * x + this.D * y + this.F);

		/// <summary>
		/// Transforms a vector, ignoring the translation.
		/// </summary>
		/// <param name="x">The horizontal component.</param>
		/// <param name="y">The vertical component.</param>
		/// <returns>The transformed vector.</returns>
		public PdfPoint TransformVector(double x, double y) => new PdfPoint(this.A * x + this.C * y, this.B * x + this.D * y);

		/// <summary>
		/// Returns the bounding box of the rectangle (<paramref name="x0"/>, <paramref name="y0"/>)-(<paramref name="x1"/>, <paramref name="y1"/>) after transformation.
		/// </summary>
		/// <param name="x0">The first corner horizontal coordinate.</param>
		/// <param name="y0">The first corner vertical coordinate.</param>
		/// <param name="x1">The opposite corner horizontal coordinate.</param>
		/// <param name="y1">The opposite corner vertical coordinate.</param>
		/// <param name="left">Receives the minimum horizontal coordinate.</param>
		/// <param name="bottom">Receives the minimum vertical coordinate.</param>
		/// <param name="right">Receives the maximum horizontal coordinate.</param>
		/// <param name="top">Receives the maximum vertical coordinate.</param>
		public void TransformBounds(double x0, double y0, double x1, double y1, out double left, out double bottom, out double right, out double top)
		{
			var p0 = Transform(x0, y0);
			var p1 = Transform(x1, y0);
			var p2 = Transform(x1, y1);
			var p3 = Transform(x0, y1);

			left = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
			right = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
			bottom = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
			top = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
		}

		/// <summary>
		/// Returns the inverse matrix.
		/// </summary>
		/// <returns>The inverse, or the identity matrix when this matrix is singular.</returns>
		public Matrix Invert()
		{
			var det = this.A * this.D - this.B * this.C;
			if (Math.Abs(det) < 1e-12)
				return Identity;

			return new Matrix(
				this.D / det, -this.B / det,
				-this.C / det, this.A / det,
				(this.C * this.F - this.D * this.E) / det,
				(this.B * this.E - this.A * this.F) / det);
		}

		/// <summary>
		/// The determinant of the linear part.
		/// </summary>
		public double Determinant => this.A * this.D - this.B * this.C;

		/// <summary>
		/// The length of the transformed horizontal unit vector.
		/// </summary>
		public double ScaleX => Math.Sqrt(this.A * this.A + this.B * this.B);

		/// <summary>
		/// The length of the transformed vertical unit vector.
		/// </summary>
		public double ScaleY => Math.Sqrt(this.C * this.C + this.D * this.D);

		/// <summary>
		/// The angle in degrees of the transformed horizontal unit vector.
		/// </summary>
		public double Angle => Math.Atan2(this.B, this.A) * 180 / Math.PI;

		/// <summary>
		/// Creates a translation matrix.
		/// </summary>
		/// <param name="x">The horizontal translation.</param>
		/// <param name="y">The vertical translation.</param>
		/// <returns>The matrix.</returns>
		public static Matrix Translation(double x, double y) => new Matrix(1, 0, 0, 1, x, y);

		/// <summary>
		/// Creates a scaling matrix.
		/// </summary>
		/// <param name="x">The horizontal scale.</param>
		/// <param name="y">The vertical scale.</param>
		/// <returns>The matrix.</returns>
		public static Matrix Scaling(double x, double y) => new Matrix(x, 0, 0, y, 0, 0);

		/// <inheritdoc/>
		public override string ToString() => string.Format(CultureInfo.InvariantCulture, "[{0:0.###} {1:0.###} {2:0.###} {3:0.###} {4:0.###} {5:0.###}]", this.A, this.B, this.C, this.D, this.E, this.F);
	}
}
