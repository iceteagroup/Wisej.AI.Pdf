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

// The drawing surface of the renderer: a premultiplied RGBA pixel buffer,
// the clipping region (ISO 32000-2, 8.5.4) and source-over compositing.

using System;
using Wisej.AI.Pdf.Imaging;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// The separable blend modes (ISO 32000-2, 11.3.5.2). The non-separable modes are painted as Normal.
	/// </summary>
	internal enum BlendMode
	{
		/// <summary>The source replaces the backdrop (Normal and Compatible).</summary>
		Normal,

		/// <summary>The colours are multiplied.</summary>
		Multiply,

		/// <summary>The complements of the colours are multiplied.</summary>
		Screen,

		/// <summary>Multiply or screen depending on the backdrop.</summary>
		Overlay,

		/// <summary>The darker of the colours.</summary>
		Darken,

		/// <summary>The lighter of the colours.</summary>
		Lighten,

		/// <summary>The backdrop is brightened to reflect the source.</summary>
		ColorDodge,

		/// <summary>The backdrop is darkened to reflect the source.</summary>
		ColorBurn,

		/// <summary>Multiply or screen depending on the source.</summary>
		HardLight,

		/// <summary>Darken or lighten depending on the source.</summary>
		SoftLight,

		/// <summary>The difference of the colours.</summary>
		Difference,

		/// <summary>Like Difference, with lower contrast.</summary>
		Exclusion,
	}

	/// <summary>
	/// A premultiplied RGBA pixel buffer, rows top to bottom.
	/// </summary>
	internal sealed class Canvas
	{
		// true after Clear: source-over compositing keeps every pixel opaque.
		private bool _isOpaque;

		/// <summary>
		/// Initializes a new transparent canvas.
		/// </summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		public Canvas(int width, int height)
		{
			this.Width = Math.Max(1, width);
			this.Height = Math.Max(1, height);
			this.Pixels = new byte[checked(this.Width * this.Height * 4)];
		}

		#region Properties

		/// <summary>
		/// The width in pixels.
		/// </summary>
		public int Width { get; }

		/// <summary>
		/// The height in pixels.
		/// </summary>
		public int Height { get; }

		/// <summary>
		/// The premultiplied RGBA pixels.
		/// </summary>
		public byte[] Pixels { get; }

		#endregion

		#region Methods

		/// <summary>
		/// Fills the whole canvas with an opaque colour.
		/// </summary>
		/// <param name="rgb">The colour as 0xRRGGBB.</param>
		public void Clear(int rgb)
		{
			var pixels = this.Pixels;
			pixels[0] = (byte)(rgb >> 16);
			pixels[1] = (byte)(rgb >> 8);
			pixels[2] = (byte)rgb;
			pixels[3] = 255;

			// fill by doubling the initialized part: a few large block copies.
			for (int filled = 4; filled < pixels.Length; filled *= 2)
				Buffer.BlockCopy(pixels, 0, pixels, filled, Math.Min(filled, pixels.Length - filled));

			_isOpaque = true;
		}

		/// <summary>
		/// Converts the canvas to a straight-alpha <see cref="RasterImage"/>. The canvas must not be used afterwards.
		/// </summary>
		/// <returns>The image.</returns>
		public RasterImage ToRasterImage()
		{
			var source = this.Pixels;

			// an opaque canvas stays opaque: premultiplied and straight alpha are the same, the pixels are reused.
			if (_isOpaque)
				return new RasterImage(this.Width, this.Height, source);

			var pixels = new byte[source.Length];
			for (int i = 0; i < source.Length; i += 4)
			{
				var a = source[i + 3];
				if (a == 255)
				{
					pixels[i] = source[i];
					pixels[i + 1] = source[i + 1];
					pixels[i + 2] = source[i + 2];
				}
				else if (a > 0)
				{
					// un-premultiply.
					pixels[i] = (byte)Math.Min(255, (source[i] * 255 + a / 2) / a);
					pixels[i + 1] = (byte)Math.Min(255, (source[i + 1] * 255 + a / 2) / a);
					pixels[i + 2] = (byte)Math.Min(255, (source[i + 2] * 255 + a / 2) / a);
				}

				pixels[i + 3] = a;
			}

			return new RasterImage(this.Width, this.Height, pixels);
		}

		#endregion
	}

	/// <summary>
	/// A soft clipping mask: the coverage of a clipping path over a rectangular area of the canvas.
	/// </summary>
	/// <remarks>Pixels outside the bounds of the mask are fully clipped.</remarks>
	internal sealed class ClipMask
	{
		/// <summary>
		/// Initializes a new empty mask over the given bounds.
		/// </summary>
		/// <param name="x0">The left side.</param>
		/// <param name="y0">The top side.</param>
		/// <param name="x1">The right side (exclusive).</param>
		/// <param name="y1">The bottom side (exclusive).</param>
		public ClipMask(int x0, int y0, int x1, int y1)
		{
			this.X0 = x0;
			this.Y0 = y0;
			this.X1 = Math.Max(x0, x1);
			this.Y1 = Math.Max(y0, y1);
			this.Data = new byte[(this.X1 - this.X0) * (this.Y1 - this.Y0)];
		}

		/// <summary>The left side of the mask.</summary>
		public int X0 { get; }

		/// <summary>The top side of the mask.</summary>
		public int Y0 { get; }

		/// <summary>The right side of the mask (exclusive).</summary>
		public int X1 { get; }

		/// <summary>The bottom side of the mask (exclusive).</summary>
		public int Y1 { get; }

		/// <summary>The coverage values, 0 to 255, row by row.</summary>
		public byte[] Data { get; }

		/// <summary>
		/// The number of bytes per row of <see cref="Data"/>.
		/// </summary>
		public int Stride => this.X1 - this.X0;

		/// <summary>
		/// Returns the coverage at a pixel.
		/// </summary>
		/// <param name="x">The column.</param>
		/// <param name="y">The row.</param>
		/// <returns>The coverage, 0 outside the bounds.</returns>
		public int Get(int x, int y)
		{
			if (x < this.X0 || x >= this.X1 || y < this.Y0 || y >= this.Y1)
				return 0;

			return this.Data[(y - this.Y0) * this.Stride + x - this.X0];
		}
	}

	/// <summary>
	/// The current clipping region: an axis-aligned rectangle with fractional edges, optionally intersected with a soft mask.
	/// </summary>
	/// <remarks>Instances are immutable, so graphics states can share them.</remarks>
	internal sealed class ClipRegion
	{
		/// <summary>
		/// Initializes a new clipping region.
		/// </summary>
		/// <param name="x0">The left side of the rectangle.</param>
		/// <param name="y0">The top side of the rectangle.</param>
		/// <param name="x1">The right side of the rectangle.</param>
		/// <param name="y1">The bottom side of the rectangle.</param>
		/// <param name="mask">The soft mask, or null.</param>
		public ClipRegion(double x0, double y0, double x1, double y1, ClipMask mask)
		{
			this.X0 = x0;
			this.Y0 = y0;
			this.X1 = Math.Max(x0, x1);
			this.Y1 = Math.Max(y0, y1);
			this.Mask = mask;

			var left = (int)Math.Floor(this.X0);
			var top = (int)Math.Floor(this.Y0);
			var right = (int)Math.Ceiling(this.X1);
			var bottom = (int)Math.Ceiling(this.Y1);
			if (mask != null)
			{
				left = Math.Max(left, mask.X0);
				top = Math.Max(top, mask.Y0);
				right = Math.Min(right, mask.X1);
				bottom = Math.Min(bottom, mask.Y1);
			}

			this.Left = left;
			this.Top = top;
			this.Right = Math.Max(left, right);
			this.Bottom = Math.Max(top, bottom);
		}

		#region Properties

		/// <summary>The left side of the clipping rectangle.</summary>
		public double X0 { get; }

		/// <summary>The top side of the clipping rectangle.</summary>
		public double Y0 { get; }

		/// <summary>The right side of the clipping rectangle.</summary>
		public double X1 { get; }

		/// <summary>The bottom side of the clipping rectangle.</summary>
		public double Y1 { get; }

		/// <summary>The soft mask, or null when the region is only the rectangle.</summary>
		public ClipMask Mask { get; }

		/// <summary>The first visible column.</summary>
		public int Left { get; }

		/// <summary>The first visible row.</summary>
		public int Top { get; }

		/// <summary>The column after the last visible one.</summary>
		public int Right { get; }

		/// <summary>The row after the last visible one.</summary>
		public int Bottom { get; }

		/// <summary>
		/// True when nothing is visible.
		/// </summary>
		public bool IsEmpty => this.Right <= this.Left || this.Bottom <= this.Top || this.X1 - this.X0 < 1e-6 || this.Y1 - this.Y0 < 1e-6;

		#endregion

		#region Methods

		/// <summary>
		/// Returns the intersection with a rectangle.
		/// </summary>
		/// <param name="x0">The left side.</param>
		/// <param name="y0">The top side.</param>
		/// <param name="x1">The right side.</param>
		/// <param name="y1">The bottom side.</param>
		/// <returns>The new region.</returns>
		public ClipRegion Intersect(double x0, double y0, double x1, double y1)
		{
			return new ClipRegion(Math.Max(this.X0, x0), Math.Max(this.Y0, y0), Math.Min(this.X1, x1), Math.Min(this.Y1, y1), this.Mask);
		}

		/// <summary>
		/// Returns a region with the same rectangle and a different mask.
		/// </summary>
		/// <param name="mask">The new mask, which must already include the current one.</param>
		/// <returns>The new region.</returns>
		public ClipRegion WithMask(ClipMask mask) => new ClipRegion(this.X0, this.Y0, this.X1, this.Y1, mask);

		/// <summary>
		/// Returns the coverage factor of the rectangle for a row, 0 to 256.
		/// </summary>
		/// <param name="y">The row.</param>
		/// <returns>The factor.</returns>
		public int RowFactor(int y)
		{
			var overlap = Math.Min(y + 1, this.Y1) - Math.Max(y, this.Y0);
			return overlap >= 1 ? 256 : overlap <= 0 ? 0 : (int)(overlap * 256 + 0.5);
		}

		/// <summary>
		/// Returns the coverage factor of the rectangle for a column, 0 to 256.
		/// </summary>
		/// <param name="x">The column.</param>
		/// <returns>The factor.</returns>
		public int ColumnFactor(int x)
		{
			var overlap = Math.Min(x + 1, this.X1) - Math.Max(x, this.X0);
			return overlap >= 1 ? 256 : overlap <= 0 ? 0 : (int)(overlap * 256 + 0.5);
		}

		#endregion
	}

	/// <summary>
	/// The source colours of a painting operation.
	/// </summary>
	internal abstract class Paint
	{
		/// <summary>
		/// Computes the premultiplied RGBA colours of the pixels [<paramref name="x0"/>, <paramref name="x1"/>) of row <paramref name="y"/>,
		/// sampled at the pixel centres.
		/// </summary>
		/// <param name="y">The row.</param>
		/// <param name="x0">The first column.</param>
		/// <param name="x1">The column after the last one.</param>
		/// <param name="span">Receives 4 bytes per pixel, starting at index 0 for <paramref name="x0"/>.</param>
		public abstract void Shade(int y, int x0, int x1, byte[] span);
	}

	/// <summary>
	/// A uniform opaque colour.
	/// </summary>
	internal sealed class SolidPaint : Paint
	{
		/// <summary>
		/// Initializes a new instance of <see cref="SolidPaint"/>.
		/// </summary>
		/// <param name="rgb">The colour as 0xRRGGBB.</param>
		public SolidPaint(int rgb)
		{
			this.Rgb = rgb & 0xFFFFFF;
		}

		/// <summary>
		/// The colour as 0xRRGGBB.
		/// </summary>
		public int Rgb { get; }

		/// <inheritdoc/>
		public override void Shade(int y, int x0, int x1, byte[] span)
		{
			var r = (byte)(this.Rgb >> 16);
			var g = (byte)(this.Rgb >> 8);
			var b = (byte)this.Rgb;
			for (int i = 0, n = (x1 - x0) * 4; i < n; i += 4)
			{
				span[i] = r;
				span[i + 1] = g;
				span[i + 2] = b;
				span[i + 3] = 255;
			}
		}
	}

	/// <summary>
	/// Composites coverage rows onto a <see cref="Canvas"/> with a paint, the clipping region and a constant alpha.
	/// </summary>
	internal sealed class CompositeSink : ICoverageSink
	{
		/// <summary>
		/// The paint of the operation.
		/// </summary>
		public Paint Paint;

		/// <summary>
		/// The clipping region.
		/// </summary>
		public ClipRegion Clip;

		/// <summary>
		/// The constant alpha, 0 to 256.
		/// </summary>
		public int Alpha = 256;

		/// <summary>
		/// The soft mask of the graphics state, or null.
		/// </summary>
		public ClipMask SoftMask;

		/// <summary>
		/// The soft mask of the enclosing transparency groups, or null.
		/// </summary>
		public ClipMask GroupMask;

		/// <summary>
		/// The blend mode.
		/// </summary>
		public BlendMode BlendMode;

		// the target canvas.
		private readonly Canvas _canvas;

		// the source colours of the paints that are not uniform.
		private byte[] _span;

		/// <summary>
		/// Initializes a new instance of <see cref="CompositeSink"/>.
		/// </summary>
		/// <param name="canvas">The target canvas.</param>
		public CompositeSink(Canvas canvas)
		{
			_canvas = canvas;
			_span = new byte[(canvas.Width + 2) * 4];
		}

		/// <inheritdoc/>
		public void Row(int y, int x0, int x1, byte[] coverage)
		{
			var clip = this.Clip;
			var factor = clip.RowFactor(y) * this.Alpha >> 8;
			if (factor <= 0)
				return;

			x0 = Math.Max(x0, clip.Left);
			x1 = Math.Min(x1, clip.Right);
			if (x0 >= x1)
				return;

			var mask = clip.Mask;
			var softMask = this.SoftMask;
			var groupMask = this.GroupMask;
			var maskRow = 0;
			if (mask != null)
			{
				if (y < mask.Y0 || y >= mask.Y1)
					return;

				maskRow = (y - mask.Y0) * mask.Stride - mask.X0;
			}

			// the partially covered columns at the sides of the clip rectangle.
			var leftEdge = (int)Math.Floor(clip.X0);
			var rightEdge = (int)Math.Floor(clip.X1);
			var leftFactor = leftEdge >= x0 && leftEdge < x1 ? clip.ColumnFactor(leftEdge) : 256;
			var rightFactor = rightEdge >= x0 && rightEdge < x1 ? clip.ColumnFactor(rightEdge) : 256;

			// combine the coverage with the alpha, the soft mask and the clip rectangle edges; the common
			// case of an opaque paint inside a rectangular clip needs no change.
			if (factor < 256 || mask != null || softMask != null || groupMask != null)
			{
				for (int x = x0; x < x1; x++)
				{
					int c = coverage[x];
					if (c == 0)
						continue;

					c = (c + (c >> 7)) * factor >> 8;
					if (mask != null)
					{
						int m = mask.Data[maskRow + x];
						c = c * (m + (m >> 7)) >> 8;
					}

					if (softMask != null)
					{
						var m = softMask.Get(x, y);
						c = c * (m + (m >> 7)) >> 8;
					}

					if (groupMask != null)
					{
						var m = groupMask.Get(x, y);
						c = c * (m + (m >> 7)) >> 8;
					}

					coverage[x] = (byte)(c > 255 ? 255 : c);
				}
			}

			if (leftFactor < 256)
				coverage[leftEdge] = (byte)(coverage[leftEdge] * leftFactor >> 8);
			if (rightFactor < 256 && rightEdge != leftEdge)
				coverage[rightEdge] = (byte)(coverage[rightEdge] * rightFactor >> 8);

			Blend(y, x0, x1, coverage);
		}

		// source-over compositing of the paint, weighted by the coverage, on premultiplied pixels.
		private void Blend(int y, int x0, int x1, byte[] coverage)
		{
			var pixels = _canvas.Pixels;
			var offset = (y * _canvas.Width + x0) * 4;

			if (this.BlendMode != BlendMode.Normal)
			{
				BlendSeparable(y, x0, x1, coverage, pixels, offset);
				return;
			}

			if (this.Paint is SolidPaint solid)
			{
				BlendSolid(pixels, offset, x0, x1, coverage, solid.Rgb);
				return;
			}

			if (_span.Length < (x1 - x0) * 4)
				_span = new byte[(x1 - x0) * 4];

			var span = _span;
			this.Paint.Shade(y, x0, x1, span);
			for (int x = x0, s = 0; x < x1; x++, offset += 4, s += 4)
			{
				int c = coverage[x];
				int sa = span[s + 3];
				if (c == 0 || sa == 0)
					continue;

				if (c == 255 && sa == 255)
				{
					pixels[offset] = span[s];
					pixels[offset + 1] = span[s + 1];
					pixels[offset + 2] = span[s + 2];
					pixels[offset + 3] = 255;
					continue;
				}

				var t = c + (c >> 7);
				var inverse = 256 - (sa * t >> 8);
				pixels[offset] = (byte)Math.Min(255, (span[s] * t + pixels[offset] * inverse) >> 8);
				pixels[offset + 1] = (byte)Math.Min(255, (span[s + 1] * t + pixels[offset + 1] * inverse) >> 8);
				pixels[offset + 2] = (byte)Math.Min(255, (span[s + 2] * t + pixels[offset + 2] * inverse) >> 8);
				pixels[offset + 3] = (byte)Math.Min(255, (sa * t + pixels[offset + 3] * inverse) >> 8);
			}
		}

		// compositing with a separable blend mode (11.3.5.2): the result is (1 - as) * Cb + as * ((1 - ab) * Cs + ab * B(Cb, Cs)).
		private void BlendSeparable(int y, int x0, int x1, byte[] coverage, byte[] pixels, int offset)
		{
			if (_span.Length < (x1 - x0) * 4)
				_span = new byte[(x1 - x0) * 4];

			var span = _span;
			this.Paint.Shade(y, x0, x1, span);
			var mode = this.BlendMode;

			for (int x = x0, s = 0; x < x1; x++, offset += 4, s += 4)
			{
				int c = coverage[x];
				int sa = span[s + 3];
				if (c == 0 || sa == 0)
					continue;

				// source alpha with the coverage, and the backdrop alpha, both 0..255.
				var alpha = sa * (c + (c >> 7)) >> 8;
				int ab = pixels[offset + 3];

				for (int k = 0; k < 3; k++)
				{
					// straight (unpremultiplied) colours of the source and of the backdrop.
					var cs = span[s + k] * 255 / sa;
					var cbPremultiplied = pixels[offset + k];
					var cb = ab == 0 ? 0 : Math.Min(255, cbPremultiplied * 255 / ab);
					var blended = BlendChannel(mode, cb, cs);

					// the source colour where the backdrop is transparent, the blended colour where it is opaque.
					var mixed = ((255 - ab) * cs + ab * blended) / 255;
					var result = (cbPremultiplied * (255 - alpha) + mixed * alpha) / 255;
					pixels[offset + k] = (byte)(result > 255 ? 255 : result);
				}

				var outAlpha = alpha + ab * (255 - alpha) / 255;
				pixels[offset + 3] = (byte)(outAlpha > 255 ? 255 : outAlpha);
			}
		}

		// the blend function of a separable mode on 0..255 colour values.
		private static int BlendChannel(BlendMode mode, int cb, int cs)
		{
			switch (mode)
			{
				case BlendMode.Multiply:
					return cb * cs / 255;

				case BlendMode.Screen:
					return cb + cs - cb * cs / 255;

				case BlendMode.Overlay:
					return BlendChannel(BlendMode.HardLight, cs, cb);

				case BlendMode.Darken:
					return Math.Min(cb, cs);

				case BlendMode.Lighten:
					return Math.Max(cb, cs);

				case BlendMode.ColorDodge:
					if (cb == 0)
						return 0;
					return cs >= 255 ? 255 : Math.Min(255, cb * 255 / (255 - cs));

				case BlendMode.ColorBurn:
					if (cb >= 255)
						return 255;
					return cs <= 0 ? 0 : 255 - Math.Min(255, (255 - cb) * 255 / cs);

				case BlendMode.HardLight:
					return cs <= 127 ? cb * 2 * cs / 255 : BlendChannel(BlendMode.Screen, cb, 2 * cs - 255);

				case BlendMode.SoftLight:
					{
						double b = cb / 255.0, s = cs / 255.0;
						double d = b <= 0.25 ? ((16 * b - 12) * b + 4) * b : Math.Sqrt(b);
						var r = s <= 0.5 ? b - (1 - 2 * s) * b * (1 - b) : b + (2 * s - 1) * (d - b);
						return (int)(r * 255 + 0.5);
					}

				case BlendMode.Difference:
					return Math.Abs(cb - cs);

				case BlendMode.Exclusion:
					return cb + cs - 2 * cb * cs / 255;

				default:
					return cs;
			}
		}

		// source-over compositing of an opaque colour.
		private static unsafe void BlendSolid(byte[] pixels, int offset, int x0, int x1, byte[] coverage, int rgb)
		{
			int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;

			// the colour as one 32-bit RGBA value in memory order, for the fully covered runs.
			var packed = BitConverter.IsLittleEndian
				? (uint)(r | (g << 8) | (b << 16) | (255 << 24))
				: (uint)((r << 24) | (g << 16) | (b << 8) | 255);

			// unsafe: large fills write millions of pixels; one 32-bit store per pixel is several times faster.
			fixed (byte* start = pixels)
			{
				var p = start + offset;
				for (int x = x0; x < x1; x++, p += 4)
				{
					int c = coverage[x];
					if (c == 0)
						continue;

					if (c == 255)
					{
						*(uint*)p = packed;
						continue;
					}

					var t = c + (c >> 7);
					var inverse = 256 - t;
					p[0] = (byte)((r * t + p[0] * inverse) >> 8);
					p[1] = (byte)((g * t + p[1] * inverse) >> 8);
					p[2] = (byte)((b * t + p[2] * inverse) >> 8);
					p[3] = (byte)((255 * t + p[3] * inverse) >> 8);
				}
			}
		}
	}

	/// <summary>
	/// Writes coverage rows into a new <see cref="ClipMask"/>, combined with the previous mask.
	/// </summary>
	internal sealed class MaskSink : ICoverageSink
	{
		/// <summary>
		/// The mask being built.
		/// </summary>
		public ClipMask Target;

		/// <summary>
		/// The previous mask, or null.
		/// </summary>
		public ClipMask Previous;

		/// <inheritdoc/>
		public void Row(int y, int x0, int x1, byte[] coverage)
		{
			var target = this.Target;
			if (y < target.Y0 || y >= target.Y1)
				return;

			x0 = Math.Max(x0, target.X0);
			x1 = Math.Min(x1, target.X1);
			var row = (y - target.Y0) * target.Stride - target.X0;
			var previous = this.Previous;
			for (int x = x0; x < x1; x++)
			{
				int c = coverage[x];
				if (c != 0 && previous != null)
				{
					var m = previous.Get(x, y);
					c = c * (m + (m >> 7)) >> 8;
				}

				target.Data[row + x] = (byte)c;
			}
		}
	}
}
