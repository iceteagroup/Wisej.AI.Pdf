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

// Tests of the image codecs used to produce the extracted images: the PNG and
// JPEG encoders, the JPEG decoder and the raster transformations.

using System;
using System.Linq;
using Wisej.AI.Pdf.Imaging;
using Wisej.AI.Pdf.Imaging.Codecs;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Images
{
	/// <summary>
	/// Tests of <see cref="PngEncoder"/>, <see cref="JpegEncoder"/>, <see cref="JpegDecoder"/> and <see cref="RasterImage"/>.
	/// </summary>
	public class ImageCodecTests
	{
		#region PNG encoder

		/// <summary>The PNG encoder picks the smallest colour type and the pixels survive the round trip.</summary>
		[Theory]
		[InlineData(2, false, false, 3, 1)]
		[InlineData(4, false, false, 3, 2)]
		[InlineData(16, false, false, 3, 4)]
		[InlineData(200, false, false, 3, 8)]
		[InlineData(5000, false, false, 2, 8)]
		[InlineData(5000, true, false, 6, 8)]
		[InlineData(5000, false, true, 0, 8)]
		[InlineData(5000, true, true, 4, 8)]
		[InlineData(12, true, false, 3, 4)]
		public void Png_RoundTrip(int colors, bool alpha, bool gray, int expectedColorType, int expectedBitDepth)
		{
			var image = CreateRaster(37, 23, colors, alpha, gray);

			var png = PngEncoder.Encode(image);
			var decoded = PngDecoder.Decode(png);

			// IHDR: bit depth at offset 24, colour type at 25.
			Assert.Equal(expectedBitDepth, png[24]);
			Assert.Equal(expectedColorType, png[25]);
			Assert.Equal(image.Width, decoded.Width);
			Assert.Equal(image.Height, decoded.Height);
			AssertSamePixels(image, decoded);
		}

		/// <summary>A one-pixel image and a wide strip encode correctly.</summary>
		[Theory]
		[InlineData(1, 1)]
		[InlineData(1000, 1)]
		[InlineData(1, 300)]
		public void Png_ExtremeSizes(int width, int height)
		{
			var image = CreateRaster(width, height, 1000, false, false);

			AssertSamePixels(image, PngDecoder.Decode(PngEncoder.Encode(image)));
		}

		#endregion

		#region JPEG encoder and decoder

		/// <summary>An RGB image survives the JPEG round trip with a PSNR above 30 dB.</summary>
		[Theory]
		[InlineData(64, 48)]
		[InlineData(37, 21)]
		public void Jpeg_RoundTrip_Rgb(int width, int height)
		{
			var image = CreateGradient(width, height, gray: false);

			var jpeg = new JpegEncoder(90).Encode(image);
			var decoded = JpegDecoder.Decode(jpeg);

			Assert.NotNull(decoded);
			Assert.True(decoded.IsComplete);
			Assert.Equal(3, decoded.Components);
			Assert.Equal(width, decoded.Width);
			Assert.Equal(height, decoded.Height);

			var psnr = Psnr(image, decoded);
			Assert.True(psnr > 30, $"PSNR {psnr:0.0} dB");
		}

		/// <summary>A gray image is encoded with a single component and decodes with a PSNR above 30 dB.</summary>
		[Fact]
		public void Jpeg_RoundTrip_Gray()
		{
			var image = CreateGradient(40, 40, gray: true);

			var decoded = JpegDecoder.Decode(new JpegEncoder(85).Encode(image));

			Assert.Equal(1, decoded.Components);
			Assert.True(Psnr(image, decoded) > 30);
		}

		/// <summary>Lower quality settings produce smaller files.</summary>
		[Fact]
		public void Jpeg_QualityAffectsSize()
		{
			var image = CreateRaster(64, 64, 5000, false, false);

			Assert.True(new JpegEncoder(30).Encode(image).Length < new JpegEncoder(95).Encode(image).Length);
		}

		/// <summary>The header reader reports the frame parameters without decoding the image.</summary>
		[Fact]
		public void Jpeg_TryReadHeader()
		{
			var jpeg = new JpegEncoder().Encode(CreateGradient(30, 20, gray: false));

			Assert.True(JpegDecoder.TryReadHeader(jpeg, out var width, out var height, out var components, out var bits, out var progressive, out _));
			Assert.Equal((30, 20, 3, 8, false), (width, height, components, bits, progressive));
		}

		/// <summary>A truncated JPEG decodes partially or returns null; it never throws.</summary>
		[Theory]
		[InlineData(0.9)]
		[InlineData(0.5)]
		[InlineData(0.1)]
		public void Jpeg_Truncated_DoesNotThrow(double fraction)
		{
			var jpeg = new JpegEncoder().Encode(CreateGradient(64, 64, gray: false));

			var decoded = JpegDecoder.Decode(jpeg.Take((int)(jpeg.Length * fraction)).ToArray());

			Assert.True(decoded == null || !decoded.IsComplete);
		}

		#endregion

		#region Raster transformations

		/// <summary>Quarter turns rotate clockwise; mirroring flips horizontally before rotating.</summary>
		[Fact]
		public void Raster_TransformRotatesAndMirrors()
		{
			// a 2x1 image: red, green.
			var image = new RasterImage(2, 1, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 });

			var turned = image.Transform(1, false);
			var mirrored = image.Transform(0, true);

			Assert.Equal((1, 2), (turned.Width, turned.Height));
			Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, turned.Pixels);
			Assert.Equal(new byte[] { 0, 255, 0, 255, 255, 0, 0, 255 }, mirrored.Pixels);
		}

		#endregion

		#region Helpers

		/// <summary>
		/// Creates a deterministic image with a limited number of colours.
		/// </summary>
		/// <param name="width">The width.</param>
		/// <param name="height">The height.</param>
		/// <param name="colors">The number of distinct colours to draw from.</param>
		/// <param name="alpha">True to include translucent and transparent pixels.</param>
		/// <param name="gray">True for gray pixels only.</param>
		/// <returns>The image.</returns>
		internal static RasterImage CreateRaster(int width, int height, int colors, bool alpha, bool gray)
		{
			var random = new Random(colors * 31 + width);
			var palette = new byte[colors][];
			for (int i = 0; i < colors; i++)
			{
				var r = (byte)random.Next(256);
				palette[i] = gray
					? new[] { r, r, r, alpha ? (byte)random.Next(256) : (byte)255 }
					: new[] { r, (byte)random.Next(256), (byte)random.Next(256), alpha ? (byte)random.Next(256) : (byte)255 };
			}

			var image = new RasterImage(width, height);
			for (int p = 0; p < width * height; p++)
				Buffer.BlockCopy(palette[random.Next(colors)], 0, image.Pixels, p * 4, 4);

			return image;
		}

		/// <summary>
		/// Creates a smooth gradient, the kind of content JPEG is designed for.
		/// </summary>
		/// <param name="width">The width.</param>
		/// <param name="height">The height.</param>
		/// <param name="gray">True for a gray image.</param>
		/// <returns>The image.</returns>
		internal static RasterImage CreateGradient(int width, int height, bool gray)
		{
			var image = new RasterImage(width, height);
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					var o = (y * width + x) * 4;
					var r = (byte)(x * 255 / Math.Max(1, width - 1));
					var g = (byte)(y * 255 / Math.Max(1, height - 1));
					var b = (byte)(128 + 100 * Math.Sin((x + y) / 9.0));
					image.Pixels[o] = r;
					image.Pixels[o + 1] = gray ? r : g;
					image.Pixels[o + 2] = gray ? r : b;
					image.Pixels[o + 3] = 255;
				}
			}

			return image;
		}

		/// <summary>
		/// Asserts that a decoded PNG has the pixels of the source; fully transparent pixels compare only their alpha.
		/// </summary>
		/// <param name="expected">The source image.</param>
		/// <param name="actual">The decoded image.</param>
		private static void AssertSamePixels(RasterImage expected, PixelImage actual)
		{
			for (int i = 0; i < expected.Pixels.Length; i += 4)
			{
				if (expected.Pixels[i + 3] == 0)
				{
					Assert.Equal(0, actual.Pixels[i + 3]);
					continue;
				}

				if (!expected.Pixels.AsSpan(i, 4).SequenceEqual(actual.Pixels.AsSpan(i, 4)))
					Assert.Fail($"Pixel {i / 4} differs: expected {string.Join(",", expected.Pixels.Skip(i).Take(4))}, actual {string.Join(",", actual.Pixels.Skip(i).Take(4))}.");
			}
		}

		/// <summary>
		/// Computes the peak signal-to-noise ratio between a source image and a decoded JPEG.
		/// </summary>
		/// <param name="expected">The source image.</param>
		/// <param name="actual">The decoded JPEG.</param>
		/// <returns>The PSNR in decibels.</returns>
		private static double Psnr(RasterImage expected, JpegImage actual)
		{
			double sum = 0;
			var count = 0;
			for (int p = 0; p < expected.Width * expected.Height; p++)
			{
				for (int c = 0; c < actual.Components; c++)
				{
					var difference = expected.Pixels[p * 4 + c] - actual.Data[p * actual.Components + c];
					sum += difference * difference;
					count++;
				}
			}

			var mse = sum / count;
			return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / mse);
		}

		#endregion
	}
}
