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

// Integration tests of the image extraction (ISO 32000-2, 8.9): every colour
// space and bit depth, decode arrays, masks, orientation, tiles, JPEG pass-
// through and de-duplication. The pixels of the output are decoded and checked.

using System;
using System.Collections.Generic;
using System.Linq;
using Wisej.AI.Pdf.Imaging;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Images
{
	/// <summary>
	/// Tests of the images returned by <see cref="PdfDocument.Extract"/>.
	/// </summary>
	public class ImageExtractionTests
	{
		// opaque red.
		private static readonly Rgba Red = new Rgba(255, 0, 0);

		// opaque green.
		private static readonly Rgba Green = new Rgba(0, 255, 0);

		// opaque blue.
		private static readonly Rgba Blue = new Rgba(0, 0, 255);

		// opaque white.
		private static readonly Rgba White = new Rgba(255, 255, 255);

		// opaque black.
		private static readonly Rgba Black = new Rgba(0, 0, 0);

		// fully transparent black.
		private static readonly Rgba Transparent = new Rgba(0, 0, 0, 0);

		#region Colour spaces and bit depths

		/// <summary>An 8-bit RGB image keeps its exact pixels.</summary>
		[Fact]
		public void Rgb8_ExactPixels()
		{
			var builder = new PdfBuilder();
			var samples = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30, 200, 100, 50, 255, 255, 255 };
			var image = ExtractSingle(builder, builder.Image(3, 2, "/DeviceRGB", 8, samples));

			Assert.Equal("image/png", image.MimeType);
			Assert.Equal((3, 2), (image.Width, image.Height));
			Assert.False(image.HasTransparency);
			AssertPixels(image, new[,] { { Red, Green, Blue }, { new Rgba(10, 20, 30), new Rgba(200, 100, 50), White } });
		}

		/// <summary>An 8-bit gray image keeps its exact levels.</summary>
		[Fact]
		public void Gray8_ExactPixels()
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(4, 1, "/DeviceGray", 8, new byte[] { 0, 85, 170, 255 }));

			AssertPixels(image, new[,] { { Gray(0), Gray(85), Gray(170), Gray(255) } });
		}

		/// <summary>1, 2 and 4-bit gray samples scale to the full 8-bit range; rows are padded to whole bytes.</summary>
		[Theory]
		[InlineData(1, new byte[] { 0b1010_0000, 0b0101_0000 }, new[] { 255, 0, 255, 0, 255, 0 })]
		[InlineData(2, new byte[] { 0b0001_1011, 0b0000_0000, 0b1110_0100, 0b1100_0000 }, new[] { 0, 85, 170, 255, 0, 255, 170, 85, 0, 255 })]
		[InlineData(4, new byte[] { 0x0F, 0x58, 0xF0, 0x70, 0xA0, 0xF0 }, new[] { 0, 255, 85, 136, 255, 0, 119, 0, 170, 0, 255, 0 })]
		public void LowBitDepthGray_ScalesLevels(int bits, byte[] samples, int[] expected)
		{
			var width = expected.Length / 2;
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(width, 2, "/DeviceGray", bits, samples));

			var pixels = PixelImage.From(image);
			for (int i = 0; i < expected.Length; i++)
				Assert.Equal(Gray((byte)expected[i]), pixels[i % width, i / width]);
		}

		/// <summary>16-bit samples use their high byte (rounded): 0xFFFF is 255, 0x8000 is 128.</summary>
		[Fact]
		public void Rgb16_UsesFullPrecision()
		{
			var builder = new PdfBuilder();
			var samples = new byte[] { 0xFF, 0xFF, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x40, 0x00 };
			var image = ExtractSingle(builder, builder.Image(2, 1, "/DeviceRGB", 16, samples));

			var pixels = PixelImage.From(image);
			Assert.True(pixels[0, 0].IsCloseTo(new Rgba(255, 128, 0), 1), pixels[0, 0].ToString());
			Assert.True(pixels[1, 0].IsCloseTo(new Rgba(0, 0, 64), 1), pixels[1, 0].ToString());
		}

		/// <summary>An Indexed image looks up its palette, at 8 bits and at 2 bits per index.</summary>
		[Theory]
		[InlineData(8, new byte[] { 0, 1, 2, 1 })]
		[InlineData(2, new byte[] { 0b0001_1001 })]
		public void Indexed_PaletteColors(int bits, byte[] samples)
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(4, 1, "[/Indexed /DeviceRGB 2 <FF0000 00FF00 0000FF>]", bits, samples));

			AssertPixels(image, new[,] { { Red, Green, Blue, Green } });
		}

		/// <summary>A 1-bit Indexed image with a two-colour palette.</summary>
		[Fact]
		public void Indexed_OneBit()
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(3, 1, "[/Indexed /DeviceRGB 1 <FFFF00 800080>]", 1, new byte[] { 0b0100_0000 }));

			AssertPixels(image, new[,] { { new Rgba(255, 255, 0), new Rgba(128, 0, 128), new Rgba(255, 255, 0) } });
		}

		/// <summary>CMYK samples are converted to RGB (approximately).</summary>
		[Fact]
		public void Cmyk_IsConverted()
		{
			var builder = new PdfBuilder();
			var samples = new byte[] { 0, 0, 0, 0, 255, 0, 0, 0, 0, 0, 0, 255 };
			var image = ExtractSingle(builder, builder.Image(3, 1, "/DeviceCMYK", 8, samples));

			var pixels = PixelImage.From(image);
			Assert.True(pixels[0, 0].IsCloseTo(White, 2), pixels[0, 0].ToString());
			Assert.True(pixels[1, 0].R < 30 && pixels[1, 0].G > 150 && pixels[1, 0].B > 220, "cyan: " + pixels[1, 0]);
			Assert.True(pixels[2, 0].R < 70 && pixels[2, 0].G < 70 && pixels[2, 0].B < 70, "black: " + pixels[2, 0]);
		}

		/// <summary>An image in a Separation space goes through the tint transform.</summary>
		[Fact]
		public void Separation_TintTransform()
		{
			var builder = new PdfBuilder();
			var space = "[/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 1] /N 1 >>]";
			var image = ExtractSingle(builder, builder.Image(2, 1, space, 8, new byte[] { 0, 255 }));

			AssertPixels(image, new[,] { { White, Blue } });
		}

		/// <summary>An ICCBased image with three components uses the RGB alternate.</summary>
		[Fact]
		public void IccBased_Rgb()
		{
			var builder = new PdfBuilder();
			var profile = builder.AddStream("/N 3", new byte[] { 0, 1, 2, 3 });
			var image = ExtractSingle(builder, builder.Image(2, 1, $"[/ICCBased {PdfText.Reference(profile)}]", 8, new byte[] { 255, 0, 0, 0, 0, 255 }));

			AssertPixels(image, new[,] { { Red, Blue } });
		}

		/// <summary>Image data with a PNG predictor in /DecodeParms is decoded before the colour conversion.</summary>
		[Fact]
		public void FlateWithPredictor()
		{
			var raw = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255, 255, 0, 0, 0, 255, 0 };
			var data = PdfBuilder.Deflate(Encoders.PngFilter(raw, 9, 3, 4));
			var builder = new PdfBuilder();
			var resource = builder.Register("XObject", builder.AddStream(
				"/Type /XObject /Subtype /Image /Width 3 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors 3 /Columns 3 >>",
				data));

			AssertPixels(ExtractSingle(builder, resource), new[,] { { Red, Green, Blue }, { Blue, Red, Green } });
		}

		#endregion

		#region Decode arrays

		/// <summary>/Decode [1 0] inverts a gray image.</summary>
		[Fact]
		public void Decode_InvertsGray()
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(3, 1, "/DeviceGray", 8, new byte[] { 0, 64, 255 }, "/Decode [1 0]"));

			AssertPixels(image, new[,] { { Gray(255), Gray(191), Gray(0) } });
		}

		/// <summary>/Decode [1 0] inverts a 1-bit image, as written by scanners for black-is-1 data.</summary>
		[Fact]
		public void Decode_InvertsOneBit()
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(4, 1, "/DeviceGray", 1, new byte[] { 0b1100_0000 }, "/Decode [1 0]"));

			AssertPixels(image, new[,] { { Black, Black, White, White } });
		}

		/// <summary>A decode array on an RGB image inverts each channel.</summary>
		[Fact]
		public void Decode_InvertsRgb()
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(1, 1, "/DeviceRGB", 8, new byte[] { 255, 0, 0 }, "/Decode [1 0 1 0 1 0]"));

			AssertPixels(image, new[,] { { new Rgba(0, 255, 255) } });
		}

		#endregion

		#region Masks

		/// <summary>A soft mask becomes the alpha channel.</summary>
		[Fact]
		public void SoftMask_IsAlpha()
		{
			var builder = new PdfBuilder();
			var mask = builder.Image(3, 1, "/DeviceGray", 8, new byte[] { 255, 128, 0 });
			var image = ExtractSingle(builder, builder.Image(3, 1, "/DeviceRGB", 8, new byte[] { 255, 0, 0, 255, 0, 0, 255, 0, 0 }, "/SMask " + mask.Reference));

			Assert.True(image.HasTransparency);
			Assert.Equal("image/png", image.MimeType);

			var pixels = PixelImage.From(image);
			Assert.Equal(new Rgba(255, 0, 0, 255), pixels[0, 0]);
			Assert.Equal(new Rgba(255, 0, 0, 128), pixels[1, 0]);
			Assert.Equal(0, pixels[2, 0].A);
		}

		/// <summary>A soft mask of a different size is scaled to the image.</summary>
		[Fact]
		public void SoftMask_DifferentSizeIsScaled()
		{
			var builder = new PdfBuilder();
			var mask = builder.Image(1, 1, "/DeviceGray", 8, new byte[] { 0 });
			var image = ExtractSingle(builder, builder.Image(2, 2, "/DeviceGray", 8, new byte[] { 10, 20, 30, 40 }, "/SMask " + mask.Reference));

			var pixels = PixelImage.From(image);
			Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(0, pixels[i % 2, i / 2].A));
		}

		/// <summary>/Matte: colours premultiplied with the matte colour are restored.</summary>
		[Fact]
		public void SoftMask_MatteIsUnpremultiplied()
		{
			// the true colour (200, 40, 0) at alpha 0.5 premultiplied with a white matte: c = m + a × (c' − m).
			var alpha = 128 / 255.0;
			byte Premultiply(int value) => (byte)Math.Round(255 + alpha * (value - 255));

			var builder = new PdfBuilder();
			var mask = builder.Image(1, 1, "/DeviceGray", 8, new byte[] { 128 }, "/Matte [1 1 1]");
			var image = ExtractSingle(builder, builder.Image(1, 1, "/DeviceRGB", 8, new[] { Premultiply(200), Premultiply(40), Premultiply(0) }, "/SMask " + mask.Reference));

			var pixel = PixelImage.From(image)[0, 0];
			Assert.True(pixel.IsCloseTo(new Rgba(200, 40, 0, 128), 2), pixel.ToString());
		}

		/// <summary>A colour-key mask makes the samples within the ranges transparent.</summary>
		[Fact]
		public void ColorKeyMask_MakesKeyTransparent()
		{
			var builder = new PdfBuilder();
			var samples = new byte[] { 255, 0, 0, 0, 255, 0, 250, 5, 0, 0, 0, 255 };
			var image = ExtractSingle(builder, builder.Image(4, 1, "/DeviceRGB", 8, samples, "/Mask [240 255 0 10 0 10]"));

			var pixels = PixelImage.From(image);
			Assert.Equal(0, pixels[0, 0].A);
			Assert.Equal(Green, pixels[1, 0]);
			Assert.Equal(0, pixels[2, 0].A);
			Assert.Equal(Blue, pixels[3, 0]);
		}

		/// <summary>A colour-key mask on an Indexed image compares palette indices.</summary>
		[Fact]
		public void ColorKeyMask_Indexed()
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(3, 1, "[/Indexed /DeviceRGB 2 <FF0000 00FF00 0000FF>]", 8, new byte[] { 0, 1, 2 }, "/Mask [1 1]"));

			var pixels = PixelImage.From(image);
			Assert.Equal(Red, pixels[0, 0]);
			Assert.Equal(0, pixels[1, 0].A);
			Assert.Equal(Blue, pixels[2, 0]);
		}

		/// <summary>An explicit stencil /Mask hides the image where its samples are 1.</summary>
		[Fact]
		public void ExplicitMask_HidesWhereOne()
		{
			var builder = new PdfBuilder();
			var mask = builder.StencilMask(4, 1, new byte[] { 0b0101_0000 });
			var image = ExtractSingle(builder, builder.Image(4, 1, "/DeviceGray", 8, new byte[] { 50, 100, 150, 200 }, "/Mask " + mask.Reference));

			var pixels = PixelImage.From(image);
			Assert.Equal(Gray(50), pixels[0, 0]);
			Assert.Equal(0, pixels[1, 0].A);
			Assert.Equal(Gray(150), pixels[2, 0]);
			Assert.Equal(0, pixels[3, 0].A);
		}

		/// <summary>A stencil mask (/ImageMask) is painted with the current fill colour on a transparent background.</summary>
		[Fact]
		public void ImageMask_PaintedWithFillColor()
		{
			var builder = new PdfBuilder();
			var stencil = builder.StencilMask(4, 1, new byte[] { 0b0110_0000 });
			var page = AddPageWithText(builder);
			page.Use(stencil).Content($"q 1 0 0 rg 100 0 0 100 72 400 cm /{stencil.Name} Do Q");

			var image = Assert.Single(TestPdf.Extract(builder).Images);

			var pixels = PixelImage.From(image);
			Assert.Equal(Red, pixels[0, 0]);
			Assert.Equal(0, pixels[1, 0].A);
			Assert.Equal(0, pixels[2, 0].A);
			Assert.Equal(Red, pixels[3, 0]);
		}

		/// <summary>A stencil mask with /Decode [1 0] paints the 1 samples instead.</summary>
		[Fact]
		public void ImageMask_DecodeInverts()
		{
			var builder = new PdfBuilder();
			var stencil = builder.StencilMask(2, 1, new byte[] { 0b0100_0000 }, "/Decode [1 0]");
			var page = AddPageWithText(builder);
			page.Use(stencil).Content($"q 0 0 1 rg 100 0 0 100 72 400 cm /{stencil.Name} Do Q");

			var pixels = PixelImage.From(Assert.Single(TestPdf.Extract(builder).Images));

			Assert.Equal(0, pixels[0, 0].A);
			Assert.Equal(Blue, pixels[1, 0]);
		}

		/// <summary>A soft mask with /Decode [1 0] inverts the opacity.</summary>
		[Fact]
		public void SoftMask_DecodeInvertsAlpha()
		{
			var builder = new PdfBuilder();
			var mask = builder.Image(2, 1, "/DeviceGray", 8, new byte[] { 0, 255 }, "/Decode [1 0]");
			var image = ExtractSingle(builder, builder.Image(2, 1, "/DeviceGray", 8, new byte[] { 60, 60 }, "/SMask " + mask.Reference));

			var pixels = PixelImage.From(image);
			Assert.Equal(255, pixels[0, 0].A);
			Assert.Equal(0, pixels[1, 0].A);
		}

		/// <summary>A 16-bit image with an 8-bit soft mask.</summary>
		[Fact]
		public void SoftMask_On16BitImage()
		{
			var builder = new PdfBuilder();
			var mask = builder.Image(2, 1, "/DeviceGray", 8, new byte[] { 255, 64 });
			var image = ExtractSingle(builder, builder.Image(2, 1, "/DeviceGray", 16, new byte[] { 0xFF, 0xFF, 0x00, 0x00 }, "/SMask " + mask.Reference));

			var pixels = PixelImage.From(image);
			Assert.Equal(new Rgba(255, 255, 255, 255), pixels[0, 0]);
			Assert.Equal(new Rgba(0, 0, 0, 64), pixels[1, 0]);
		}

		/// <summary>An explicit mask with a higher resolution than the image is applied at the image resolution.</summary>
		[Fact]
		public void ExplicitMask_HigherResolution()
		{
			// the mask is 4 × 2: the left half shows the image, the right half hides it.
			var builder = new PdfBuilder();
			var mask = builder.StencilMask(4, 2, new byte[] { 0b0011_0000, 0b0011_0000 });
			var image = ExtractSingle(builder, builder.Image(2, 1, "/DeviceRGB", 8, new byte[] { 255, 0, 0, 0, 0, 255 }, "/Mask " + mask.Reference));

			var pixels = PixelImage.From(image);
			Assert.Equal(Red, pixels[0, 0]);
			Assert.Equal(0, pixels[1, 0].A);
		}

		/// <summary>An inline stencil mask (/IM true) is painted with the fill colour.</summary>
		[Fact]
		public void InlineImageMask_PaintedWithFillColor()
		{
			var builder = new PdfBuilder();
			var data = PdfText.FromBytes(new byte[] { 0b0000_1111 });
			AddPageWithText(builder).Content($"q 0 1 0 rg 100 0 0 100 72 400 cm BI /W 8 /H 1 /IM true ID {data}\nEI Q");

			var pixels = PixelImage.From(Assert.Single(TestPdf.Extract(builder).Images));

			Assert.Equal(Green, pixels[0, 0]);
			Assert.Equal(Green, pixels[3, 0]);
			Assert.Equal(0, pixels[4, 0].A);
			Assert.Equal(0, pixels[7, 0].A);
		}

		/// <summary>/Decode on an Indexed image maps the samples to reversed palette indices.</summary>
		[Fact]
		public void Decode_IndexedReversed()
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(4, 1, "[/Indexed /DeviceRGB 3 <FF0000 00FF00 0000FF FFFFFF>]", 2, new byte[] { 0b0001_1011 }, "/Decode [3 0]"));

			AssertPixels(image, new[,] { { White, Blue, Green, Red } });
		}

		/// <summary>An 8-bit Lab image uses the default decode [0 100 amin amax bmin bmax].</summary>
		[Fact]
		public void Lab_Image()
		{
			var builder = new PdfBuilder();
			var space = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]";
			var image = ExtractSingle(builder, builder.Image(2, 1, space, 8, new byte[] { 255, 128, 128, 0, 128, 128 }));

			var pixels = PixelImage.From(image);
			Assert.True(pixels[0, 0].IsCloseTo(White, 3), pixels[0, 0].ToString());
			Assert.True(pixels[1, 0].IsCloseTo(Black, 3), pixels[1, 0].ToString());
		}

		/// <summary>A JPEG with a decode array is not passed through: the decoded, inverted pixels are re-encoded.</summary>
		[Fact]
		public void Jpeg_DecodeArray_IsApplied()
		{
			var source = new RasterImage(16, 16);
			for (int i = 0; i < source.Pixels.Length; i += 4)
			{
				source.Pixels[i] = 230;
				source.Pixels[i + 1] = 20;
				source.Pixels[i + 2] = 20;
				source.Pixels[i + 3] = 255;
			}

			var jpeg = new JpegEncoder(95).Encode(source);
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.JpegImage(jpeg, 16, 16, "/DeviceRGB", "/Decode [1 0 1 0 1 0]"));

			Assert.NotEqual(jpeg, image.Data);
			Assert.True(PixelImage.From(image)[8, 8].IsCloseTo(new Rgba(25, 235, 235), 8), PixelImage.From(image)[8, 8].ToString());
		}

		/// <summary>With ApplyMasks off, the soft mask is ignored and the image is opaque.</summary>
		[Fact]
		public void ApplyMasksDisabled_IgnoresSoftMask()
		{
			var builder = new PdfBuilder();
			var mask = builder.Image(1, 1, "/DeviceGray", 8, new byte[] { 0 });
			var image = ExtractSingle(builder, builder.Image(1, 1, "/DeviceRGB", 8, new byte[] { 0, 0, 255 }, "/SMask " + mask.Reference), new PdfImageOptions { ApplyMasks = false });

			Assert.False(image.HasTransparency);
			AssertPixels(image, new[,] { { Blue } });
		}

		#endregion

		#region JPEG

		/// <summary>A JPEG image without masks is returned as the original bytes.</summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void Jpeg_PassThrough(bool gray)
		{
			var jpeg = new JpegEncoder(80).Encode(ImageCodecTests.CreateGradient(32, 24, gray));
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.JpegImage(jpeg, 32, 24, gray ? "/DeviceGray" : "/DeviceRGB"));

			Assert.Equal("image/jpeg", image.MimeType);
			Assert.Equal(".jpg", image.Extension);
			Assert.Equal(jpeg, image.Data);
		}

		/// <summary>A JPEG image with a soft mask is decoded and re-encoded as a transparent PNG.</summary>
		[Fact]
		public void Jpeg_WithSoftMask_BecomesPng()
		{
			var jpeg = new JpegEncoder(95).Encode(ImageCodecTests.CreateGradient(16, 16, false));
			var builder = new PdfBuilder();
			var mask = builder.Image(16, 16, "/DeviceGray", 8, Enumerable.Repeat((byte)100, 256).ToArray());
			var image = ExtractSingle(builder, builder.JpegImage(jpeg, 16, 16, "/DeviceRGB", "/SMask " + mask.Reference));

			Assert.Equal("image/png", image.MimeType);
			Assert.True(image.HasTransparency);
			Assert.Equal(100, PixelImage.From(image)[5, 5].A);
		}

		/// <summary>The PNG format option re-encodes JPEG images losslessly from their decoded pixels.</summary>
		[Fact]
		public void Jpeg_FormatPngOption()
		{
			var source = ImageCodecTests.CreateGradient(16, 16, false);
			var jpeg = new JpegEncoder(95).Encode(source);
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.JpegImage(jpeg, 16, 16, "/DeviceRGB"), new PdfImageOptions { Format = PdfImageFormat.Png });

			Assert.Equal("image/png", image.MimeType);

			var pixels = PixelImage.From(image);
			var expected = new Rgba(source.Pixels[(8 * 16 + 8) * 4], source.Pixels[(8 * 16 + 8) * 4 + 1], source.Pixels[(8 * 16 + 8) * 4 + 2]);
			Assert.True(pixels[8, 8].IsCloseTo(expected, 12), $"{pixels[8, 8]} vs {expected}");
		}

		/// <summary>The JPEG format option encodes lossless sources as JPEG.</summary>
		[Fact]
		public void FormatJpegOption()
		{
			var builder = new PdfBuilder();
			var image = ExtractSingle(builder, builder.Image(8, 8, "/DeviceRGB", 8, Enumerable.Repeat((byte)200, 192).ToArray()), new PdfImageOptions { Format = PdfImageFormat.Jpeg });

			Assert.Equal("image/jpeg", image.MimeType);
			Assert.True(PixelImage.From(image)[4, 4].IsCloseTo(new Rgba(200, 200, 200), 3));
		}

		#endregion

		#region Placement

		/// <summary>An image drawn rotated or mirrored is returned as it appears on the page.</summary>
		[Theory]
		[InlineData("identity", 90, 0, 0, 60, 72, 400)]
		[InlineData("flipped vertically", 90, 0, 0, -60, 72, 460)]
		[InlineData("mirrored horizontally", -90, 0, 0, 60, 162, 400)]
		[InlineData("rotated 90 counterclockwise", 0, 90, -60, 0, 132, 400)]
		[InlineData("rotated 90 clockwise", 0, -90, 60, 0, 72, 490)]
		[InlineData("rotated 180", -90, 0, 0, -60, 162, 460)]
		public void Orientation_MatchesPage(string description, double a, double b, double c, double d, double e, double f)
		{
			AssertOrientation(description, new[] { a, b, c, d, e, f }, 0);
		}

		/// <summary>On a page with /Rotate, an upright image is turned as the page is displayed.</summary>
		[Theory]
		[InlineData(90)]
		[InlineData(180)]
		[InlineData(270)]
		public void Orientation_FollowsPageRotation(int rotation)
		{
			AssertOrientation($"page rotated {rotation}", new double[] { 90, 0, 0, 60, 200, 300 }, rotation);
		}

		/// <summary>Two images touching each other are stitched into one figure image.</summary>
		[Fact]
		public void Tiles_AreStitched()
		{
			var builder = new PdfBuilder();
			var left = builder.Image(10, 20, "/DeviceRGB", 8, Fill(200, Red));
			var right = builder.Image(10, 20, "/DeviceRGB", 8, Fill(200, Blue));
			AddPageWithText(builder).Draw(left, 72, 400, 50, 100).Draw(right, 122, 400, 50, 100);

			var content = TestPdf.Extract(builder);
			var image = Assert.Single(content.Images);
			var pixels = PixelImage.From(image);

			Assert.Single(content.Blocks.OfType<PdfFigure>());
			Assert.Equal((20, 20), (image.Width, image.Height));
			Assert.Equal(Red, pixels[2, 10]);
			Assert.Equal(Red, pixels[9, 10]);
			Assert.Equal(Blue, pixels[10, 10]);
			Assert.Equal(Blue, pixels[17, 10]);
		}

		/// <summary>Images far apart are separate figures.</summary>
		[Fact]
		public void SeparateImages_AreNotStitched()
		{
			var builder = new PdfBuilder();
			var first = builder.Image(2, 2, "/DeviceRGB", 8, Fill(4, Red));
			var second = builder.Image(2, 2, "/DeviceRGB", 8, Fill(4, Blue));
			AddPageWithText(builder).Draw(first, 72, 400, 100, 100).Draw(second, 300, 200, 100, 100);

			var images = TestPdf.Extract(builder).Images;

			Assert.Equal(2, images.Count);
			Assert.Equal(new[] { "image-1", "image-2" }, images.Select(i => i.Id));
		}

		/// <summary>The same image XObject on two pages is returned once, with both page numbers.</summary>
		[Fact]
		public void SameImageOnTwoPages_IsDeduplicated()
		{
			var builder = new PdfBuilder();
			var logo = builder.Image(2, 2, "/DeviceRGB", 8, Fill(4, Green));
			AddPageWithText(builder).Draw(logo, 200, 400, 80, 80);
			AddPageWithText(builder).Draw(logo, 200, 400, 80, 80);

			var content = TestPdf.Extract(builder);
			var image = Assert.Single(content.Images);

			Assert.Equal(new[] { 1, 2 }, image.PageNumbers);
			Assert.Equal(1, image.PageNumber);
			Assert.Same(image, content.Pages[1].Blocks.OfType<PdfFigure>().Single().Image);
		}

		/// <summary>With de-duplication disabled, each placement produces its own image.</summary>
		[Fact]
		public void DeduplicationDisabled()
		{
			var builder = new PdfBuilder();
			var logo = builder.Image(2, 2, "/DeviceRGB", 8, Fill(4, Green));
			AddPageWithText(builder).Draw(logo, 200, 400, 80, 80);
			AddPageWithText(builder).Draw(logo, 200, 400, 80, 80);

			var content = TestPdf.Extract(builder, new PdfExtractionOptions { Images = new PdfImageOptions { Deduplicate = false } });

			Assert.Equal(2, content.Images.Count);
		}

		/// <summary>Images displayed smaller than MinimumSize are skipped.</summary>
		[Theory]
		[InlineData(24, 0)]
		[InlineData(5, 1)]
		public void MinimumSize_FiltersSmallImages(double minimumSize, int expected)
		{
			var builder = new PdfBuilder();
			var icon = builder.Image(2, 2, "/DeviceRGB", 8, Fill(4, Red));
			AddPageWithText(builder).Draw(icon, 72, 400, 10, 10);

			var content = TestPdf.Extract(builder, new PdfExtractionOptions { Images = new PdfImageOptions { MinimumSize = minimumSize } });

			Assert.Equal(expected, content.Images.Count);
		}

		/// <summary>An inline image (BI ... ID ... EI) is extracted.</summary>
		[Fact]
		public void InlineImage_IsExtracted()
		{
			var builder = new PdfBuilder();
			var data = PdfText.FromBytes(new byte[] { 255, 0, 0, 0, 0, 255, 0, 255, 0, 255, 255, 255 });
			AddPageWithText(builder).Content($"q 100 0 0 100 72 400 cm BI /W 2 /H 2 /CS /RGB /BPC 8 ID {data}\nEI Q");

			var image = Assert.Single(TestPdf.Extract(builder).Images);

			AssertPixels(image, new[,] { { Red, Blue }, { Green, White } });
			Assert.Null(image.ResourceName);
		}

		/// <summary>An inline image with abbreviated filter and indexed colour space names.</summary>
		[Fact]
		public void InlineImage_AbbreviatedFilterAndIndexed()
		{
			var builder = new PdfBuilder();
			var data = Encoders.Ascii85(new byte[] { 0, 1, 1, 0 });
			AddPageWithText(builder).Content($"q 100 0 0 100 72 400 cm BI /W 2 /H 2 /CS [/I /RGB 1 <00FF00 FF00FF>] /BPC 8 /F /A85 ID {data}\nEI Q");

			var image = Assert.Single(TestPdf.Extract(builder).Images);

			AssertPixels(image, new[,] { { Green, new Rgba(255, 0, 255) }, { new Rgba(255, 0, 255), Green } });
		}

		/// <summary>An image drawn inside a form XObject is extracted with the form's transformation.</summary>
		[Fact]
		public void ImageInFormXObject_IsExtracted()
		{
			var builder = new PdfBuilder();
			var picture = builder.Image(2, 1, "/DeviceRGB", 8, new byte[] { 255, 0, 0, 0, 0, 255 });
			var form = builder.Form($"q 1 0 0 1 0 0 cm /{picture.Name} Do Q", new double[] { 0, 0, 1, 1 }, new double[] { 120, 0, 0, 60, 0, 0 }, picture);
			AddPageWithText(builder).Draw(form, 1, 0, 0, 1, 72, 400);

			var image = Assert.Single(TestPdf.Extract(builder).Images);

			Assert.Equal(120, image.Bounds.Width, 1);
			Assert.Equal(60, image.Bounds.Height, 1);
			AssertPixels(image, new[,] { { Red, Blue } });
		}

		/// <summary>The image metadata: bounds in page coordinates from the top-left, resolution and resource name.</summary>
		[Fact]
		public void Metadata()
		{
			var builder = new PdfBuilder();
			var resource = builder.Image(144, 72, "/DeviceGray", 8, new byte[144 * 72]);
			AddPageWithText(builder).Draw(resource, 72, 492, 144, 72);

			var image = Assert.Single(TestPdf.Extract(builder).Images);

			Assert.Equal("image-1", image.Id);
			Assert.Equal("image-1.png", image.FileName);
			Assert.Equal(resource.Name, image.ResourceName);
			Assert.Equal(PdfImageKind.Picture, image.Kind);
			Assert.Equal(72, image.Bounds.Left, 1);
			Assert.Equal(792 - 564, image.Bounds.Top, 1);
			Assert.Equal(72, image.Dpi, 1);
			Assert.StartsWith("data:image/png;base64,", image.ToDataUri());
		}

		/// <summary>An image covering a page without text is reported as a page scan.</summary>
		[Fact]
		public void PageScan_Kind()
		{
			var builder = new PdfBuilder();
			var scan = builder.Image(4, 4, "/DeviceGray", 8, new byte[16]);
			builder.AddPage().Draw(scan, 0, 0, 612, 792);

			var content = TestPdf.Extract(builder);

			Assert.True(content.Pages[0].IsImageOnly);
			Assert.Equal(PdfImageKind.PageScan, Assert.Single(content.Images).Kind);
		}

		/// <summary>With ExtractImages off, figures are detected but no image data is decoded.</summary>
		[Fact]
		public void ExtractImagesDisabled()
		{
			var builder = new PdfBuilder();
			var resource = builder.Image(2, 2, "/DeviceRGB", 8, Fill(4, Red));
			AddPageWithText(builder).Draw(resource, 72, 400, 100, 100).Text(builder.Font("Helvetica"), 10, 72, 385, "Figure 3: Nothing to decode.");

			var content = TestPdf.Extract(builder, new PdfExtractionOptions { ExtractImages = false });

			Assert.Empty(content.Images);
			Assert.Equal("Figure 3: Nothing to decode.", Assert.Single(content.Blocks.OfType<PdfFigure>()).Caption);
		}

		#endregion

		#region Helpers

		/// <summary>
		/// Places an image at 100 × 100 points on a page with a line of text, and extracts the single resulting image.
		/// </summary>
		/// <param name="builder">The document, which already contains the image.</param>
		/// <param name="resource">The image XObject.</param>
		/// <param name="options">The image options; null for the defaults.</param>
		/// <returns>The extracted image.</returns>
		private static PdfImage ExtractSingle(PdfBuilder builder, PdfResource resource, PdfImageOptions options = null)
		{
			AddPageWithText(builder).Draw(resource, 72, 400, 100, 100);

			var content = TestPdf.Extract(builder, new PdfExtractionOptions { Images = options ?? new PdfImageOptions() });
			return Assert.Single(content.Images);
		}

		/// <summary>
		/// Adds a page with a line of text, so that images are pictures rather than page scans.
		/// </summary>
		/// <param name="builder">The document.</param>
		/// <returns>The page.</returns>
		private static PdfPageBuilder AddPageWithText(PdfBuilder builder)
		{
			return builder.AddPage().Text(builder.Font("Helvetica"), 11, 72, 720, "A page that shows a picture.");
		}

		/// <summary>
		/// Draws a 3 × 2 image with six distinct colours and asserts that every source pixel appears in the extracted
		/// image where it appears on the displayed page.
		/// </summary>
		/// <param name="description">The case, for the assertion messages.</param>
		/// <param name="matrix">The transformation matrix of the image: a, b, c, d, e, f.</param>
		/// <param name="rotation">The /Rotate value of the page.</param>
		private static void AssertOrientation(string description, double[] matrix, int rotation)
		{
			var colors = new[,] { { Red, Green, Blue }, { White, Black, new Rgba(255, 255, 0) } };
			var samples = new List<byte>();
			foreach (var color in colors)
				samples.AddRange(new[] { color.R, color.G, color.B });

			var builder = new PdfBuilder();
			var resource = builder.Image(3, 2, "/DeviceRGB", 8, samples.ToArray());
			var page = AddPageWithText(builder).Draw(resource, matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]);
			page.Rotation = rotation;

			var pixels = PixelImage.From(Assert.Single(TestPdf.Extract(builder).Images));

			// image space (unit square, row 0 at the top) -> user space -> display space (top-left origin, page rotated clockwise).
			(double X, double Y) ToDisplay(double u, double v)
			{
				var x = matrix[0] * u + matrix[2] * v + matrix[4];
				var y = matrix[1] * u + matrix[3] * v + matrix[5];
				switch (rotation)
				{
					case 90: return (y, x);
					case 180: return (page.Width - x, y);
					case 270: return (page.Height - y, page.Width - x);
					default: return (x, page.Height - y);
				}
			}

			var corners = new[] { ToDisplay(0, 0), ToDisplay(1, 0), ToDisplay(0, 1), ToDisplay(1, 1) };
			double left = corners.Min(p => p.X), right = corners.Max(p => p.X), top = corners.Min(p => p.Y), bottom = corners.Max(p => p.Y);

			for (int row = 0; row < 2; row++)
			{
				for (int column = 0; column < 3; column++)
				{
					var (x, y) = ToDisplay((column + 0.5) / 3, 1 - (row + 0.5) / 2);
					var outX = (int)((x - left) / (right - left) * pixels.Width);
					var outY = (int)((y - top) / (bottom - top) * pixels.Height);
					Assert.True(colors[row, column].Equals(pixels[outX, outY]), $"{description}: source ({column}, {row}) expected {colors[row, column]} at ({outX}, {outY}), found {pixels[outX, outY]}.");
				}
			}
		}

		/// <summary>
		/// Asserts the exact pixels of an extracted image.
		/// </summary>
		/// <param name="image">The image.</param>
		/// <param name="expected">The expected colours, by row and column.</param>
		private static void AssertPixels(PdfImage image, Rgba[,] expected)
		{
			var pixels = PixelImage.From(image);
			Assert.Equal(expected.GetLength(1), pixels.Width);
			Assert.Equal(expected.GetLength(0), pixels.Height);

			for (int y = 0; y < pixels.Height; y++)
			{
				for (int x = 0; x < pixels.Width; x++)
					Assert.True(expected[y, x].Equals(pixels[x, y]), $"Pixel ({x}, {y}): expected {expected[y, x]}, found {pixels[x, y]}.");
			}
		}

		/// <summary>
		/// Returns an opaque gray colour.
		/// </summary>
		/// <param name="level">The gray level.</param>
		/// <returns>The colour.</returns>
		private static Rgba Gray(byte level) => new Rgba(level, level, level);

		/// <summary>
		/// Returns RGB samples of a single colour.
		/// </summary>
		/// <param name="pixels">The number of pixels.</param>
		/// <param name="color">The colour.</param>
		/// <returns>The samples.</returns>
		private static byte[] Fill(int pixels, Rgba color)
		{
			var samples = new byte[pixels * 3];
			for (int i = 0; i < pixels; i++)
			{
				samples[i * 3] = color.R;
				samples[i * 3 + 1] = color.G;
				samples[i * 3 + 2] = color.B;
			}

			return samples;
		}

		#endregion
	}
}
