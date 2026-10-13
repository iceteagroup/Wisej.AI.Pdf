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

// Paints that sample pixel sources: images (ISO 32000-2, 8.9), tiling
// pattern cells (8.7.3) and pre-rendered layers.

using System;
using Wisej.AI.Pdf.Imaging;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// A premultiplied RGBA bitmap used as a paint source.
	/// </summary>
	internal sealed class Bitmap
	{
		/// <summary>
		/// Initializes a new instance of <see cref="Bitmap"/>.
		/// </summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		/// <param name="pixels">The premultiplied RGBA pixels.</param>
		public Bitmap(int width, int height, byte[] pixels)
		{
			this.Width = width;
			this.Height = height;
			this.Pixels = pixels;
		}

		/// <summary>The width in pixels.</summary>
		public int Width { get; }

		/// <summary>The height in pixels.</summary>
		public int Height { get; }

		/// <summary>The premultiplied RGBA pixels.</summary>
		public byte[] Pixels { get; }

		/// <summary>
		/// Creates a premultiplied bitmap from a straight-alpha raster.
		/// </summary>
		/// <param name="image">The raster.</param>
		/// <returns>The bitmap.</returns>
		public static Bitmap FromRaster(RasterImage image)
		{
			var source = image.Pixels;
			var pixels = new byte[source.Length];
			for (int i = 0; i < source.Length; i += 4)
			{
				int a = source[i + 3];
				if (a == 255)
				{
					pixels[i] = source[i];
					pixels[i + 1] = source[i + 1];
					pixels[i + 2] = source[i + 2];
				}
				else if (a > 0)
				{
					pixels[i] = (byte)((source[i] * a + 127) / 255);
					pixels[i + 1] = (byte)((source[i + 1] * a + 127) / 255);
					pixels[i + 2] = (byte)((source[i + 2] * a + 127) / 255);
				}

				pixels[i + 3] = (byte)a;
			}

			return new Bitmap(image.Width, image.Height, pixels);
		}

		/// <summary>
		/// Creates a premultiplied bitmap from a straight-alpha raster, reduced with area averaging.
		/// </summary>
		/// <param name="image">The raster.</param>
		/// <param name="width">The target width, at most the raster width.</param>
		/// <param name="height">The target height, at most the raster height.</param>
		/// <returns>The bitmap.</returns>
		/// <remarks>
		/// Each target pixel averages the source pixels whose index maps to it, weighted by alpha.
		/// The source is read once, row by row, so the cost is proportional to the source size.
		/// </remarks>
		public static Bitmap FromRaster(RasterImage image, int width, int height)
		{
			var sw = image.Width;
			var sh = image.Height;
			width = Math.Max(1, Math.Min(width, sw));
			height = Math.Max(1, Math.Min(height, sh));
			if (width == sw && height == sh)
				return FromRaster(image);

			// the target column of each source column.
			var columns = new int[sw];
			for (int x = 0; x < sw; x++)
				columns[x] = (int)((long)x * width / sw);

			var source = image.Pixels;
			var pixels = new byte[width * height * 4];
			var sums = new long[width * 4];
			var counts = new int[width];

			for (int ty = 0, sy = 0; ty < height; ty++)
			{
				var end = (int)((long)(ty + 1) * sh / height);
				Array.Clear(sums, 0, sums.Length);
				Array.Clear(counts, 0, counts.Length);

				for (; sy < end; sy++)
				{
					var p = sy * sw * 4;
					for (int x = 0; x < sw; x++, p += 4)
					{
						var t = columns[x];
						int a = source[p + 3];
						counts[t]++;
						if (a == 0)
							continue;

						var s = t * 4;
						sums[s] += source[p] * a;
						sums[s + 1] += source[p + 1] * a;
						sums[s + 2] += source[p + 2] * a;
						sums[s + 3] += a;
					}
				}

				// premultiplied averages: sum(c * a) / (255 * count) and sum(a) / count.
				var o = ty * width * 4;
				for (int t = 0; t < width; t++, o += 4)
				{
					var count = counts[t];
					if (count == 0)
						continue;

					var s = t * 4;
					var divisor = 255L * count;
					pixels[o] = (byte)((sums[s] + divisor / 2) / divisor);
					pixels[o + 1] = (byte)((sums[s + 1] + divisor / 2) / divisor);
					pixels[o + 2] = (byte)((sums[s + 2] + divisor / 2) / divisor);
					pixels[o + 3] = (byte)((sums[s + 3] + count / 2) / count);
				}
			}

			return new Bitmap(width, height, pixels);
		}
	}

	/// <summary>
	/// Paints an image mapped through an affine transformation, with bilinear interpolation.
	/// </summary>
	internal sealed class ImagePaint : Paint
	{
		// the image pixels.
		private readonly Bitmap _bitmap;

		// device space -> image pixel space.
		private readonly Matrix _inverse;

		/// <summary>
		/// Initializes a new instance of <see cref="ImagePaint"/>.
		/// </summary>
		/// <param name="bitmap">The image pixels.</param>
		/// <param name="deviceToImage">Maps device space to image pixel space (the top-left pixel covers [0, 1] x [0, 1]).</param>
		public ImagePaint(Bitmap bitmap, Matrix deviceToImage)
		{
			_bitmap = bitmap;
			_inverse = deviceToImage;
		}

		/// <inheritdoc/>
		public override void Shade(int y, int x0, int x1, byte[] span)
		{
			var m = _inverse;
			var w = _bitmap.Width;
			var h = _bitmap.Height;
			var src = _bitmap.Pixels;

			// sample positions relative to the pixel centres of the image.
			var u = m.A * (x0 + 0.5) + m.C * (y + 0.5) + m.E - 0.5;
			var v = m.B * (x0 + 0.5) + m.D * (y + 0.5) + m.F - 0.5;
			var du = m.A;
			var dv = m.B;
			var maxX = w - 1;
			var maxY = h - 1;

			for (int x = x0, s = 0; x < x1; x++, s += 4, u += du, v += dv)
			{
				// clamp to the edges: the coverage of the image parallelogram already limits the area.
				var fu = u < 0 ? 0 : u > maxX ? maxX : u;
				var fv = v < 0 ? 0 : v > maxY ? maxY : v;
				var ix = (int)fu;
				var iy = (int)fv;
				var wx = (int)((fu - ix) * 256);
				var wy = (int)((fv - iy) * 256);
				var ix1 = ix < maxX ? ix + 1 : ix;
				var iy1 = iy < maxY ? iy + 1 : iy;

				var p00 = (iy * w + ix) * 4;
				var p01 = (iy * w + ix1) * 4;
				var p10 = (iy1 * w + ix) * 4;
				var p11 = (iy1 * w + ix1) * 4;

				if (wx == 0 && wy == 0)
				{
					span[s] = src[p00];
					span[s + 1] = src[p00 + 1];
					span[s + 2] = src[p00 + 2];
					span[s + 3] = src[p00 + 3];
					continue;
				}

				var iwx = 256 - wx;
				var iwy = 256 - wy;
				for (int c = 0; c < 4; c++)
				{
					var top = src[p00 + c] * iwx + src[p01 + c] * wx;
					var bottom = src[p10 + c] * iwx + src[p11 + c] * wx;
					span[s + c] = (byte)((top * iwy + bottom * wy + 32768) >> 16);
				}
			}
		}
	}

	/// <summary>
	/// Paints a tiling pattern: a pre-rendered cell repeated in both directions.
	/// </summary>
	internal sealed class TilePaint : Paint
	{
		// the rendered cell.
		private readonly Bitmap _tile;

		// device space -> tile pixel space (periodic).
		private readonly Matrix _inverse;

		/// <summary>
		/// Initializes a new instance of <see cref="TilePaint"/>.
		/// </summary>
		/// <param name="tile">The rendered cell.</param>
		/// <param name="deviceToTile">Maps device space to tile pixel space.</param>
		public TilePaint(Bitmap tile, Matrix deviceToTile)
		{
			_tile = tile;
			_inverse = deviceToTile;
		}

		/// <inheritdoc/>
		public override void Shade(int y, int x0, int x1, byte[] span)
		{
			var m = _inverse;
			var w = _tile.Width;
			var h = _tile.Height;
			var src = _tile.Pixels;
			var u = m.A * (x0 + 0.5) + m.C * (y + 0.5) + m.E;
			var v = m.B * (x0 + 0.5) + m.D * (y + 0.5) + m.F;

			for (int x = x0, s = 0; x < x1; x++, s += 4, u += m.A, v += m.B)
			{
				// wrap around in floating point: the coordinates can be far from the origin.
				var ix = (int)(u - Math.Floor(u / w) * w);
				var iy = (int)(v - Math.Floor(v / h) * h);
				if ((uint)ix >= (uint)w)
					ix = 0;
				if ((uint)iy >= (uint)h)
					iy = 0;

				var p = (iy * w + ix) * 4;
				span[s] = src[p];
				span[s + 1] = src[p + 1];
				span[s + 2] = src[p + 2];
				span[s + 3] = src[p + 3];
			}
		}
	}

	/// <summary>
	/// Paints a bitmap aligned with the device pixels, transparent outside it.
	/// </summary>
	internal sealed class LayerPaint : Paint
	{
		// the layer and the device position of its top-left pixel.
		private readonly Bitmap _layer;
		// the device column of the first layer column.
		private readonly int _left;
		// the device row of the first layer row.
		private readonly int _top;

		/// <summary>
		/// Initializes a new instance of <see cref="LayerPaint"/>.
		/// </summary>
		/// <param name="layer">The layer pixels.</param>
		/// <param name="left">The device column of the first layer column.</param>
		/// <param name="top">The device row of the first layer row.</param>
		public LayerPaint(Bitmap layer, int left, int top)
		{
			_layer = layer;
			_left = left;
			_top = top;
		}

		/// <inheritdoc/>
		public override void Shade(int y, int x0, int x1, byte[] span)
		{
			Array.Clear(span, 0, (x1 - x0) * 4);

			var ly = y - _top;
			if (ly < 0 || ly >= _layer.Height)
				return;

			var start = Math.Max(x0, _left);
			var end = Math.Min(x1, _left + _layer.Width);
			if (start >= end)
				return;

			Buffer.BlockCopy(_layer.Pixels, (ly * _layer.Width + start - _left) * 4, span, (start - x0) * 4, (end - start) * 4);
		}
	}
}
