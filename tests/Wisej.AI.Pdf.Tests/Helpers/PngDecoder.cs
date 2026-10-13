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

// Test helper: a minimal PNG decoder (ISO/IEC 15948) used to verify the pixels
// of extracted images. It reuses the library's FlateFilter and Predictor.

using System;
using System.IO;
using System.Text;
using Wisej.AI.Pdf.Filters;
using Wisej.AI.Pdf.Imaging.Codecs;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// An RGBA colour with a readable <see cref="ToString"/> for assertion messages.
	/// </summary>
	internal readonly struct Rgba : IEquatable<Rgba>
	{
		/// <summary>Red.</summary>
		public readonly byte R;

		/// <summary>Green.</summary>
		public readonly byte G;

		/// <summary>Blue.</summary>
		public readonly byte B;

		/// <summary>Alpha: 0 transparent, 255 opaque.</summary>
		public readonly byte A;

		/// <summary>
		/// Initializes a new colour.
		/// </summary>
		/// <param name="r">Red.</param>
		/// <param name="g">Green.</param>
		/// <param name="b">Blue.</param>
		/// <param name="a">Alpha.</param>
		public Rgba(byte r, byte g, byte b, byte a = 255)
		{
			this.R = r;
			this.G = g;
			this.B = b;
			this.A = a;
		}

		/// <summary>
		/// Creates an opaque colour from 0xRRGGBB.
		/// </summary>
		/// <param name="rgb">The colour.</param>
		/// <returns>The opaque colour.</returns>
		public static Rgba FromRgb(int rgb) => new Rgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

		/// <summary>
		/// Returns true when every channel is within <paramref name="tolerance"/> of <paramref name="other"/>.
		/// </summary>
		/// <param name="other">The expected colour.</param>
		/// <param name="tolerance">The maximum difference per channel.</param>
		/// <returns>True when the colours are close.</returns>
		public bool IsCloseTo(Rgba other, int tolerance)
		{
			return Math.Abs(this.R - other.R) <= tolerance && Math.Abs(this.G - other.G) <= tolerance
				&& Math.Abs(this.B - other.B) <= tolerance && Math.Abs(this.A - other.A) <= tolerance;
		}

		/// <inheritdoc/>
		public bool Equals(Rgba other) => this.R == other.R && this.G == other.G && this.B == other.B && this.A == other.A;

		/// <inheritdoc/>
		public override bool Equals(object obj) => obj is Rgba other && Equals(other);

		/// <inheritdoc/>
		public override int GetHashCode() => (this.R << 24) | (this.G << 16) | (this.B << 8) | this.A;

		/// <inheritdoc/>
		public override string ToString() => $"rgba({this.R}, {this.G}, {this.B}, {this.A})";
	}

	/// <summary>
	/// A decoded image: RGBA pixels, row-major, top row first.
	/// </summary>
	internal sealed class PixelImage
	{
		/// <summary>
		/// Initializes a new decoded image.
		/// </summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		/// <param name="pixels">The RGBA pixels.</param>
		public PixelImage(int width, int height, byte[] pixels)
		{
			this.Width = width;
			this.Height = height;
			this.Pixels = pixels;
		}

		/// <summary>The width in pixels.</summary>
		public int Width { get; }

		/// <summary>The height in pixels.</summary>
		public int Height { get; }

		/// <summary>The RGBA pixels.</summary>
		public byte[] Pixels { get; }

		/// <summary>
		/// Returns the colour of a pixel.
		/// </summary>
		/// <param name="x">The column.</param>
		/// <param name="y">The row, 0 at the top.</param>
		/// <returns>The colour.</returns>
		public Rgba this[int x, int y]
		{
			get
			{
				var i = (y * this.Width + x) * 4;
				return new Rgba(this.Pixels[i], this.Pixels[i + 1], this.Pixels[i + 2], this.Pixels[i + 3]);
			}
		}

		/// <summary>
		/// Decodes the data of an extracted image, PNG or JPEG.
		/// </summary>
		/// <param name="image">The extracted image.</param>
		/// <returns>The pixels.</returns>
		public static PixelImage From(PdfImage image)
		{
			if (image.MimeType == "image/png")
				return PngDecoder.Decode(image.Data);

			var jpeg = JpegDecoder.Decode(image.Data) ?? throw new InvalidDataException("The JPEG data cannot be decoded.");
			var pixels = new byte[jpeg.Width * jpeg.Height * 4];
			for (int i = 0; i < jpeg.Width * jpeg.Height; i++)
			{
				for (int c = 0; c < 3; c++)
					pixels[i * 4 + c] = jpeg.Data[i * jpeg.Components + (jpeg.Components >= 3 ? c : 0)];

				pixels[i * 4 + 3] = 255;
			}

			return new PixelImage(jpeg.Width, jpeg.Height, pixels);
		}
	}

	/// <summary>
	/// Decodes non-interlaced PNG files of every colour type and bit depth that the library writes.
	/// </summary>
	internal static class PngDecoder
	{
		// the 8-byte signature at the start of every PNG file.
		private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

		/// <summary>
		/// Decodes a PNG file.
		/// </summary>
		/// <param name="png">The PNG file.</param>
		/// <returns>The RGBA pixels.</returns>
		/// <exception cref="InvalidDataException">The data is not a supported PNG file.</exception>
		public static PixelImage Decode(byte[] png)
		{
			if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
				throw new InvalidDataException("Not a PNG file.");

			int width = 0, height = 0, bitDepth = 0, colorType = 0;
			byte[] palette = null, transparency = null;
			var idat = new MemoryStream();

			// chunks: length, type, data, CRC.
			for (int p = 8; p + 8 <= png.Length;)
			{
				var length = ReadInt(png, p);
				var type = Encoding.ASCII.GetString(png, p + 4, 4);
				var data = png.AsSpan(p + 8, length).ToArray();
				p += 12 + length;

				switch (type)
				{
					case "IHDR":
						width = ReadInt(data, 0);
						height = ReadInt(data, 4);
						bitDepth = data[8];
						colorType = data[9];
						if (data[12] != 0)
							throw new InvalidDataException("Interlaced PNG files are not supported.");
						break;

					case "PLTE":
						palette = data;
						break;

					case "tRNS":
						transparency = data;
						break;

					case "IDAT":
						idat.Write(data, 0, data.Length);
						break;
				}
			}

			var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"Colour type {colorType}.") };

			// the PNG filters are the PNG predictors of the PDF FlateDecode filter.
			var parameters = new PdfDictionary
			{
				[PdfName.Get("Predictor")] = 15,
				[PdfName.Get("Colors")] = channels,
				[PdfName.Get("BitsPerComponent")] = bitDepth,
				[PdfName.Get("Columns")] = width,
			};

			var raw = Predictor.Apply(FlateFilter.Decode(idat.ToArray()), parameters);
			var rowLength = (width * channels * bitDepth + 7) / 8;
			if (raw.Length < rowLength * height)
				throw new InvalidDataException("The image data is truncated.");

			var pixels = new byte[width * height * 4];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					var o = (y * width + x) * 4;
					var row = y * rowLength;
					switch (colorType)
					{
						case 0:
							{
								var gray = Sample(raw, row, x, bitDepth) * 255 / ((1 << bitDepth) - 1);
								pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)gray;
								pixels[o + 3] = 255;
							}
							break;

						case 2:
							Buffer.BlockCopy(raw, row + x * 3, pixels, o, 3);
							pixels[o + 3] = 255;
							break;

						case 3:
							{
								var index = Sample(raw, row, x, bitDepth);
								Buffer.BlockCopy(palette, index * 3, pixels, o, 3);
								pixels[o + 3] = transparency != null && index < transparency.Length ? transparency[index] : (byte)255;
							}
							break;

						case 4:
							pixels[o] = pixels[o + 1] = pixels[o + 2] = raw[row + x * 2];
							pixels[o + 3] = raw[row + x * 2 + 1];
							break;

						default:
							Buffer.BlockCopy(raw, row + x * 4, pixels, o, 4);
							break;
					}
				}
			}

			return new PixelImage(width, height, pixels);
		}

		// reads a sample of 1, 2, 4 or 8 bits, packed most significant bit first.
		private static int Sample(byte[] data, int row, int x, int bits)
		{
			if (bits == 8)
				return data[row + x];

			var bit = x * bits;
			return (data[row + bit / 8] >> (8 - bits - bit % 8)) & ((1 << bits) - 1);
		}

		// reads a big-endian 32-bit integer.
		private static int ReadInt(byte[] data, int offset)
		{
			return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
		}
	}
}
