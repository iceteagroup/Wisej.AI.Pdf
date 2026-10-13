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

// In-memory RGBA raster with the transformations needed by the image
// extraction: orientation (rotation and mirroring), scaling and compositing.

using System;

namespace Wisej.AI.Pdf.Imaging
{
	/// <summary>
	/// An 8-bit RGBA raster, rows top to bottom, without padding.
	/// </summary>
	internal sealed class RasterImage
	{
		/// <summary>
		/// Initializes a new transparent raster.
		/// </summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		public RasterImage(int width, int height)
		{
			this.Width = width;
			this.Height = height;
			this.Pixels = new byte[checked(width * height * 4)];
		}

		/// <summary>
		/// Initializes a new raster over existing RGBA pixels.
		/// </summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		/// <param name="pixels">The RGBA pixels (width × height × 4 bytes).</param>
		public RasterImage(int width, int height, byte[] pixels)
		{
			this.Width = width;
			this.Height = height;
			this.Pixels = pixels;
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
		/// The RGBA pixels.
		/// </summary>
		public byte[] Pixels { get; }

		/// <summary>
		/// True when some pixel is not fully opaque. Computed on demand.
		/// </summary>
		public bool HasTransparency
		{
			get
			{
				var pixels = this.Pixels;
				for (int i = 3; i < pixels.Length; i += 4)
				{
					if (pixels[i] != 255)
						return true;
				}

				return false;
			}
		}

		/// <summary>
		/// True when every pixel is gray (R = G = B). Computed on demand; used to encode smaller PNG files.
		/// </summary>
		public bool IsGrayscale
		{
			get
			{
				var pixels = this.Pixels;
				for (int i = 0; i < pixels.Length; i += 4)
				{
					if (pixels[i] != pixels[i + 1] || pixels[i] != pixels[i + 2])
						return false;
				}

				return true;
			}
		}

		#endregion

		#region Transformations

		/// <summary>
		/// Returns a copy rotated clockwise by a multiple of 90 degrees and optionally mirrored horizontally.
		/// </summary>
		/// <param name="quarterTurns">The number of clockwise quarter turns, 0 to 3.</param>
		/// <param name="mirror">True to mirror horizontally before rotating.</param>
		/// <returns>The transformed raster, or this instance when there is nothing to do.</returns>
		public RasterImage Transform(int quarterTurns, bool mirror)
		{
			quarterTurns &= 3;
			if (quarterTurns == 0 && !mirror)
				return this;

			var w = this.Width;
			var h = this.Height;
			var swap = quarterTurns == 1 || quarterTurns == 3;
			var result = new RasterImage(swap ? h : w, swap ? w : h);
			var src = this.Pixels;
			var dst = result.Pixels;
			var rw = result.Width;

			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					var sx = mirror ? w - 1 - x : x;
					int dx, dy;
					switch (quarterTurns)
					{
						case 1:
							dx = h - 1 - y;
							dy = sx;
							break;

						case 2:
							dx = w - 1 - sx;
							dy = h - 1 - y;
							break;

						case 3:
							dx = y;
							dy = w - 1 - sx;
							break;

						default:
							dx = sx;
							dy = y;
							break;
					}

					Buffer.BlockCopy(src, (y * w + x) * 4, dst, (dy * rw + dx) * 4, 4);
				}
			}

			return result;
		}

		/// <summary>
		/// Returns a copy flipped vertically.
		/// </summary>
		/// <returns>The flipped raster.</returns>
		public RasterImage FlipVertical()
		{
			var result = new RasterImage(this.Width, this.Height);
			var stride = this.Width * 4;
			for (int y = 0; y < this.Height; y++)
				Buffer.BlockCopy(this.Pixels, y * stride, result.Pixels, (this.Height - 1 - y) * stride, stride);

			return result;
		}

		/// <summary>
		/// Returns a copy scaled to the given size, with area averaging when shrinking and bilinear interpolation when enlarging.
		/// </summary>
		/// <param name="width">The target width.</param>
		/// <param name="height">The target height.</param>
		/// <returns>The scaled raster.</returns>
		public RasterImage Resize(int width, int height)
		{
			width = Math.Max(1, width);
			height = Math.Max(1, height);
			if (width == this.Width && height == this.Height)
				return this;

			return width <= this.Width && height <= this.Height ? Shrink(width, height) : Bilinear(width, height);
		}

		// box filter: each target pixel averages the source pixels it covers (premultiplied by alpha).
		private RasterImage Shrink(int width, int height)
		{
			var result = new RasterImage(width, height);
			var src = this.Pixels;
			var dst = result.Pixels;
			var sw = this.Width;
			var sh = this.Height;

			for (int y = 0; y < height; y++)
			{
				var y0 = y * sh / height;
				var y1 = Math.Max(y0 + 1, (y + 1) * sh / height);
				for (int x = 0; x < width; x++)
				{
					var x0 = x * sw / width;
					var x1 = Math.Max(x0 + 1, (x + 1) * sw / width);

					long r = 0, g = 0, b = 0, a = 0;
					var count = 0;
					for (int sy = y0; sy < y1; sy++)
					{
						var row = sy * sw;
						for (int sx = x0; sx < x1; sx++)
						{
							var i = (row + sx) * 4;
							var alpha = src[i + 3];
							r += src[i] * alpha;
							g += src[i + 1] * alpha;
							b += src[i + 2] * alpha;
							a += alpha;
							count++;
						}
					}

					var o = (y * width + x) * 4;
					if (a > 0)
					{
						dst[o] = (byte)(r / a);
						dst[o + 1] = (byte)(g / a);
						dst[o + 2] = (byte)(b / a);
					}

					dst[o + 3] = (byte)(a / count);
				}
			}

			return result;
		}

		/// <summary>
		/// Scales the raster with bilinear interpolation of each channel, sampling at pixel centres.
		/// Used when at least one dimension grows.
		/// </summary>
		/// <param name="width">The target width in pixels.</param>
		/// <param name="height">The target height in pixels.</param>
		/// <returns>The scaled raster.</returns>
		private RasterImage Bilinear(int width, int height)
		{
			var result = new RasterImage(width, height);
			var src = this.Pixels;
			var dst = result.Pixels;
			var sw = this.Width;
			var sh = this.Height;
			var scaleX = (double)sw / width;
			var scaleY = (double)sh / height;

			for (int y = 0; y < height; y++)
			{
				var fy = Math.Max(0, (y + 0.5) * scaleY - 0.5);
				var y0 = Math.Min(sh - 1, (int)fy);
				var y1 = Math.Min(sh - 1, y0 + 1);
				var wy = fy - y0;

				for (int x = 0; x < width; x++)
				{
					var fx = Math.Max(0, (x + 0.5) * scaleX - 0.5);
					var x0 = Math.Min(sw - 1, (int)fx);
					var x1 = Math.Min(sw - 1, x0 + 1);
					var wx = fx - x0;

					var o = (y * width + x) * 4;
					for (int c = 0; c < 4; c++)
					{
						var top = src[(y0 * sw + x0) * 4 + c] * (1 - wx) + src[(y0 * sw + x1) * 4 + c] * wx;
						var bottom = src[(y1 * sw + x0) * 4 + c] * (1 - wx) + src[(y1 * sw + x1) * 4 + c] * wx;
						dst[o + c] = (byte)(top * (1 - wy) + bottom * wy + 0.5);
					}
				}
			}

			return result;
		}

		/// <summary>
		/// Draws <paramref name="image"/> scaled into the rectangle (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>), blending with alpha.
		/// </summary>
		/// <param name="image">The image to draw.</param>
		/// <param name="x">The left edge of the target rectangle.</param>
		/// <param name="y">The top edge of the target rectangle.</param>
		/// <param name="width">The width of the target rectangle.</param>
		/// <param name="height">The height of the target rectangle.</param>
		public void Draw(RasterImage image, int x, int y, int width, int height)
		{
			if (width <= 0 || height <= 0)
				return;

			var scaled = image.Resize(width, height);
			var src = scaled.Pixels;
			var dst = this.Pixels;

			for (int sy = 0; sy < height; sy++)
			{
				var ty = y + sy;
				if (ty < 0 || ty >= this.Height)
					continue;

				for (int sx = 0; sx < width; sx++)
				{
					var tx = x + sx;
					if (tx < 0 || tx >= this.Width)
						continue;

					var s = (sy * width + sx) * 4;
					var d = (ty * this.Width + tx) * 4;
					var alpha = src[s + 3];
					if (alpha == 255)
					{
						Buffer.BlockCopy(src, s, dst, d, 4);
					}
					else if (alpha > 0)
					{
						// source-over compositing.
						var inverse = 255 - alpha;
						var outAlpha = alpha + dst[d + 3] * inverse / 255;
						for (int c = 0; c < 3; c++)
							dst[d + c] = (byte)((src[s + c] * alpha + dst[d + c] * dst[d + 3] * inverse / 255) / Math.Max(1, outAlpha));

						dst[d + 3] = (byte)outAlpha;
					}
				}
			}
		}

		/// <summary>
		/// Fills the whole raster with an opaque colour.
		/// </summary>
		/// <param name="rgb">The colour as 0xRRGGBB.</param>
		public void Clear(int rgb)
		{
			var pixels = this.Pixels;
			for (int i = 0; i < pixels.Length; i += 4)
			{
				pixels[i] = (byte)(rgb >> 16);
				pixels[i + 1] = (byte)(rgb >> 8);
				pixels[i + 2] = (byte)rgb;
				pixels[i + 3] = 255;
			}
		}

		#endregion
	}
}
