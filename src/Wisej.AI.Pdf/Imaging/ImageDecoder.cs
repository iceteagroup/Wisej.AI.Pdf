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

// Decodes PDF image XObjects and inline images (ISO 32000-2, 8.9) to RGBA:
// all filters and codecs, colour spaces, bit depths, decode arrays, stencil
// masks, explicit masks, colour-key masks, soft masks and matte.

using System;
using Wisej.AI.Pdf.Filters;
using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Imaging.Codecs;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Imaging
{
	/// <summary>
	/// The result of decoding an image.
	/// </summary>
	internal sealed class DecodedImage
	{
		/// <summary>
		/// The decoded pixels.
		/// </summary>
		public RasterImage Raster;

		/// <summary>
		/// The original JPEG data when the image can be passed through without recompression; otherwise null.
		/// </summary>
		public byte[] Jpeg;
	}

	/// <summary>
	/// Decodes PDF images to <see cref="RasterImage"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The decoder follows the PDF imaging model precisely, which is where most extractors fall short:
	/// </para>
	/// <list type="bullet">
	/// <item>samples of 1, 2, 4, 8 and 16 bits per component, mapped through the <c>/Decode</c> array;</item>
	/// <item>every colour space: device, CIE-based (ICC, Lab), Indexed, Separation and DeviceN;</item>
	/// <item>stencil masks painted with the current fill colour and transparent elsewhere;</item>
	/// <item>explicit masks and soft masks of any size (resampled), colour-key masking, and <c>/Matte</c> un-premultiplication;</item>
	/// <item>JPEG (including CMYK, YCCK and progressive), JPEG 2000 (with embedded alpha: <c>/SMaskInData</c>), JBIG2 and CCITT fax;</item>
	/// <item>JPEG pass-through, without any recompression, whenever the result would be identical.</item>
	/// </list>
	/// </remarks>
	internal static class ImageDecoder
	{
		#region Fields

		// refuse images larger than this many pixels, to bound memory.
		private const long MaxPixels = 200_000_000;

		#endregion

		#region Methods

		/// <summary>
		/// Decodes an image.
		/// </summary>
		/// <param name="dictionary">The image dictionary (XObject stream or inline image dictionary).</param>
		/// <param name="data">The raw image data (encoded, decrypted).</param>
		/// <param name="resources">The resources used to resolve named colour spaces; can be null.</param>
		/// <param name="cache">The resource cache of the document; can be null.</param>
		/// <param name="fillColor">The fill colour for stencil masks, as 0xRRGGBB.</param>
		/// <param name="options">The image options.</param>
		/// <returns>The decoded image, or null when it can't be decoded.</returns>
		public static DecodedImage Decode(PdfDictionary dictionary, byte[] data, PdfDictionary resources, ResourceCache cache, int fillColor, PdfImageOptions options)
		{
			var width = dictionary.GetInt(PdfNames.Width, dictionary.GetInt("W"));
			var height = dictionary.GetInt(PdfNames.Height, dictionary.GetInt("H"));
			if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
				return null;

			var isMask = dictionary.GetBool(PdfNames.ImageMask) || dictionary.GetBool("IM");
			var bitsPerComponent = isMask ? 1 : dictionary.GetInt(PdfNames.BitsPerComponent, dictionary.GetInt("BPC", 8));

			// only 1, 2, 4, 8 and 16 bits are valid; codecs (JPEG, JPX, JBIG2, CCITT) define their own depth.
			if (bitsPerComponent != 1 && bitsPerComponent != 2 && bitsPerComponent != 4 && bitsPerComponent != 8 && bitsPerComponent != 16)
				bitsPerComponent = 8;

			// apply the non-image filters; the image codec, if any, is decoded below.
			var filters = FilterPipeline.GetFilters(dictionary, out var parameters);
			string codec = null;
			PdfDictionary codecParameters = null;
			for (int i = 0; i < filters.Count; i++)
			{
				if (FilterPipeline.IsImageCodec(filters[i]))
				{
					codec = filters[i];
					codecParameters = parameters[i];
					break;
				}

				data = FilterPipeline.Apply(filters[i], data, parameters[i]);
			}

			var colorSpaceObject = dictionary.Resolve(PdfNames.ColorSpace, PdfName.Get("CS"));
			var colorSpace = isMask ? null : colorSpaceObject != null
				? (cache != null ? cache.GetColorSpace(colorSpaceObject, resources) : ColorSpace.Create(colorSpaceObject, resources))
				: null;

			var decode = (dictionary.Resolve(PdfNames.Decode, PdfName.Get("D")) as PdfArray)?.ToNumbers();

			Samples samples;
			switch (codec)
			{
				case "DCTDecode":
					{
						var jpeg = JpegDecoder.Decode(data, codecParameters?.GetInt("ColorTransform", -1) ?? -1);
						if (jpeg == null)
							return null;

						samples = new Samples(jpeg.Width, jpeg.Height, jpeg.Components, 8, jpeg.Data);

						// the colour space of the JPEG wins over a missing or inconsistent PDF colour space.
						if (colorSpace == null || colorSpace.ComponentCount != jpeg.Components && colorSpace.Kind != ColorSpaceKind.Indexed)
							colorSpace = DefaultSpace(jpeg.Components);

						var result = Convert(samples, colorSpace, decode, false, 0, dictionary, resources, cache, options, out var hasMask);

						// pass-through: a complete gray or RGB JPEG without masks, decode array or colour transform override.
						var passThrough = !hasMask && decode == null && filters.Count == 1 && jpeg.IsComplete
							&& (jpeg.Components == 1 || jpeg.Components == 3)
							&& IsDisplayable(colorSpace, jpeg.Components)
							&& (codecParameters == null || !codecParameters.Has("ColorTransform"));

						return new DecodedImage { Raster = result, Jpeg = passThrough ? data : null };
					}

				case "JPXDecode":
					{
						var jpx = JpxDecoder.Decode(data);
						if (jpx == null)
							return null;

						// the JPX header defines the colour space when the PDF doesn't.
						var colorChannels = jpx.AlphaChannel >= 0 ? jpx.Components - 1 : jpx.Components;
						if (colorSpace == null || colorSpace.ComponentCount != colorChannels && colorSpace.Kind != ColorSpaceKind.Indexed)
							colorSpace = jpx.ColorSpace == JpxColorSpace.Cmyk || colorChannels == 4 ? ColorSpace.DeviceCmyk : DefaultSpace(colorChannels);

						samples = new Samples(jpx.Width, jpx.Height, jpx.Components, 8, jpx.Data) { AlphaChannel = jpx.AlphaChannel };

						// /SMaskInData 1 or 2: the alpha channel of the codestream is the soft mask.
						var useAlpha = jpx.AlphaChannel >= 0 && (dictionary.GetInt(PdfNames.SMaskInData) > 0 || !dictionary.Has(PdfNames.SMask));
						var raster = Convert(samples, colorSpace, decode, false, 0, dictionary, resources, cache, options, out _);
						if (useAlpha)
							ApplyChannelAlpha(raster, samples, jpx.AlphaPremultiplied);

						return new DecodedImage { Raster = raster };
					}

				case "JBIG2Decode":
					{
						var globals = codecParameters?.GetStream("JBIG2Globals")?.GetBytes();
						var bits = Jbig2Decoder.Decode(data, globals, out var w, out var h);
						if (bits == null)
							return null;

						samples = new Samples(w, h, 1, 1, bits);
						break;
					}

				case "CCITTFaxDecode":
					{
						var p = new CcittParameters
						{
							K = codecParameters?.GetInt("K") ?? 0,
							EndOfLine = codecParameters?.GetBool("EndOfLine") ?? false,
							EncodedByteAlign = codecParameters?.GetBool("EncodedByteAlign") ?? false,
							Columns = codecParameters?.GetInt("Columns", width) ?? width,
							Rows = codecParameters?.GetInt("Rows", height) ?? height,
							EndOfBlock = codecParameters?.GetBool("EndOfBlock", true) ?? true,
							BlackIs1 = codecParameters?.GetBool("BlackIs1") ?? false,
							DamagedRowsBeforeError = codecParameters?.GetInt("DamagedRowsBeforeError") ?? 0,
						};

						var bits = CcittFaxDecoder.Decode(data, p, out var rows);
						if (bits == null)
							return null;

						samples = new Samples(p.Columns, Math.Max(1, Math.Min(rows > 0 ? rows : height, height)), 1, 1, bits);
						break;
					}

				default:
					{
						var components = isMask ? 1 : colorSpace?.ComponentCount ?? 1;
						if (colorSpace == null && !isMask)
							colorSpace = ColorSpace.DeviceGray;

						samples = new Samples(width, height, Math.Max(1, components), bitsPerComponent, data);

						// data covering less than 1% of the declared size is a corrupt or hostile image: don't allocate its raster.
						var needed = (long)samples.RowLength * samples.Height;
						if (needed > 1 << 20 && data.Length * 100L < needed)
							return null;
						break;
					}
			}

			if (!isMask && colorSpace == null)
				colorSpace = DefaultSpace(samples.Components);

			var raster2 = Convert(samples, colorSpace, decode, isMask, fillColor, dictionary, resources, cache, options, out _);
			return raster2 != null ? new DecodedImage { Raster = raster2 } : null;
		}

		#endregion

		#region Samples

		/// <summary>
		/// Packed image samples.
		/// </summary>
		private sealed class Samples
		{
			/// <summary>The width in pixels.</summary>
			public readonly int Width;

			/// <summary>The height in pixels.</summary>
			public readonly int Height;

			/// <summary>The number of components per pixel.</summary>
			public readonly int Components;

			/// <summary>The bits per component.</summary>
			public readonly int Bits;

			/// <summary>The packed data, rows padded to whole bytes.</summary>
			public readonly byte[] Data;

			/// <summary>The length of a row in bytes.</summary>
			public readonly int RowLength;

			/// <summary>The index of an alpha channel interleaved in the data (JPX), or -1.</summary>
			public int AlphaChannel = -1;

			/// <summary>
			/// Initializes a new instance of <see cref="Samples"/>.
			/// </summary>
			/// <param name="width">The width in pixels.</param>
			/// <param name="height">The height in pixels.</param>
			/// <param name="components">The number of components per pixel, including an interleaved alpha channel.</param>
			/// <param name="bits">The bits per component: 1, 2, 4, 8 or 16.</param>
			/// <param name="data">The packed data; null is treated as empty.</param>
			public Samples(int width, int height, int components, int bits, byte[] data)
			{
				this.Width = width;
				this.Height = height;
				this.Components = components;
				this.Bits = bits;
				this.Data = data ?? Array.Empty<byte>();
				this.RowLength = (width * components * bits + 7) / 8;
			}

			/// <summary>
			/// Returns the raw value of component <paramref name="c"/> of pixel <paramref name="x"/> in row <paramref name="y"/>.
			/// </summary>
			/// <param name="x">The column, 0-based.</param>
			/// <param name="y">The row, 0-based, from the top.</param>
			/// <param name="c">The component index within the pixel.</param>
			/// <returns>The raw sample value, 0 to 2^bits - 1, or 0 when the data is truncated.</returns>
			public int Get(int x, int y, int c)
			{
				var bits = this.Bits;
				var row = y * this.RowLength;
				if (bits == 8)
				{
					var i = row + x * this.Components + c;
					return i < this.Data.Length ? this.Data[i] : 0;
				}

				if (bits == 16)
				{
					var i = row + (x * this.Components + c) * 2;
					return i + 1 < this.Data.Length ? (this.Data[i] << 8) | this.Data[i + 1] : 0;
				}

				// 1, 2 and 4 bits: big-endian packing within each byte.
				var bit = (x * this.Components + c) * bits;
				var index = row + (bit >> 3);
				if (index >= this.Data.Length)
					return 0;

				return (this.Data[index] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
			}
		}

		#endregion

		#region Conversion

		// converts the samples to RGBA, applying decode, colour space and masks.
		private static RasterImage Convert(Samples samples, ColorSpace colorSpace, double[] decode, bool isMask, int fillColor, PdfDictionary dictionary, PdfDictionary resources, ResourceCache cache, PdfImageOptions options, out bool hasMask)
		{
			hasMask = false;
			var width = samples.Width;
			var height = samples.Height;
			var raster = new RasterImage(width, height);
			var pixels = raster.Pixels;

			if (isMask)
			{
				// stencil mask: with the default decode [0 1], 0 samples are painted with the fill colour.
				var paintValue = decode != null && decode.Length >= 2 && decode[0] > decode[1] ? 1 : 0;
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						var o = (y * width + x) * 4;
						if (samples.Get(x, y, 0) == paintValue)
						{
							pixels[o] = (byte)(fillColor >> 16);
							pixels[o + 1] = (byte)(fillColor >> 8);
							pixels[o + 2] = (byte)fillColor;
							pixels[o + 3] = 255;
						}
					}
				}

				hasMask = true;
				return raster;
			}

			var colorChannels = samples.AlphaChannel >= 0 ? samples.Components - 1 : samples.Components;
			var n = Math.Min(colorSpace.ComponentCount, colorChannels);
			var maxValue = (1 << samples.Bits) - 1;

			// the decode array maps raw values to colour space values.
			var defaultDecode = colorSpace.GetDefaultDecode(samples.Bits);
			var map = decode != null && decode.Length >= n * 2 ? decode : defaultDecode;

			// fast paths for the most common images: 8-bit gray, RGB and palette with the default decode.
			if (samples.Bits == 8 && samples.AlphaChannel < 0 && SameDecode(map, defaultDecode) && TryConvertDirect(samples, colorSpace, pixels))
			{
				if (options == null || options.ApplyMasks)
					hasMask = ApplyMasks(raster, samples, dictionary, resources, cache, options);

				return raster;
			}

			// lookup tables per component for bit depths up to 8; direct computation for 16.
			float[][] tables = null;
			if (samples.Bits <= 8)
			{
				tables = new float[n][];
				for (int c = 0; c < n; c++)
				{
					tables[c] = new float[maxValue + 1];
					var dmin = c * 2 < map.Length ? map[c * 2] : 0;
					var dmax = c * 2 + 1 < map.Length ? map[c * 2 + 1] : 1;
					for (int v = 0; v <= maxValue; v++)
						tables[c][v] = (float)(dmin + v * (dmax - dmin) / maxValue);
				}
			}

			var row = new float[width * colorSpace.ComponentCount];
			var rgb = new byte[width * 3];

			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					for (int c = 0; c < colorSpace.ComponentCount; c++)
					{
						// skip the interleaved alpha channel of JPX images.
						var source = samples.AlphaChannel >= 0 && c >= samples.AlphaChannel ? c + 1 : c;
						float value;
						if (c >= n)
						{
							value = 0;
						}
						else if (tables != null)
						{
							value = tables[c][samples.Get(x, y, source)];
						}
						else
						{
							var dmin = map[c * 2];
							var dmax = map[c * 2 + 1];
							value = (float)(dmin + samples.Get(x, y, source) * (dmax - dmin) / maxValue);
						}

						row[x * colorSpace.ComponentCount + c] = value;
					}
				}

				colorSpace.ToRgbRow(row, width, rgb);

				for (int x = 0; x < width; x++)
				{
					var o = (y * width + x) * 4;
					pixels[o] = rgb[x * 3];
					pixels[o + 1] = rgb[x * 3 + 1];
					pixels[o + 2] = rgb[x * 3 + 2];
					pixels[o + 3] = 255;
				}
			}

			if (options == null || options.ApplyMasks)
				hasMask = ApplyMasks(raster, samples, dictionary, resources, cache, options);

			return raster;
		}

		/// <summary>
		/// Determines whether a decode array starts with the same ranges as another (within 1e-6).
		/// </summary>
		/// <param name="a">The decode array in use.</param>
		/// <param name="b">The reference decode array, usually the default of the colour space.</param>
		/// <returns>True when the first <c>b.Length</c> entries of <paramref name="a"/> match <paramref name="b"/>.</returns>
		private static bool SameDecode(double[] a, double[] b)
		{
			if (a.Length < b.Length)
				return false;

			for (int i = 0; i < b.Length; i++)
			{
				if (Math.Abs(a[i] - b[i]) > 1e-6)
					return false;
			}

			return true;
		}

		// converts 8-bit samples of device gray/RGB (or ICC with such alternates) and Indexed spaces without per-pixel calls.
		private static bool TryConvertDirect(Samples samples, ColorSpace colorSpace, byte[] pixels)
		{
			var kind = colorSpace.Kind;
			var n = colorSpace.ComponentCount;
			if (kind == ColorSpaceKind.IccBased && (n == 1 || n == 3))
				kind = n == 1 ? ColorSpaceKind.Gray : ColorSpaceKind.Rgb;

			var width = samples.Width;
			var height = samples.Height;
			var data = samples.Data;
			var rowLength = samples.RowLength;

			switch (kind)
			{
				case ColorSpaceKind.Gray:
					if (samples.Components != 1)
						return false;

					for (int y = 0; y < height; y++)
					{
						var s = y * rowLength;
						var o = y * width * 4;
						for (int x = 0; x < width; x++, o += 4)
						{
							var v = s + x < data.Length ? data[s + x] : (byte)0;
							pixels[o] = v;
							pixels[o + 1] = v;
							pixels[o + 2] = v;
							pixels[o + 3] = 255;
						}
					}
					return true;

				case ColorSpaceKind.Rgb:
					if (samples.Components != 3)
						return false;

					for (int y = 0; y < height; y++)
					{
						var s = y * rowLength;
						var o = y * width * 4;
						for (int x = 0; x < width; x++, s += 3, o += 4)
						{
							if (s + 2 < data.Length)
							{
								pixels[o] = data[s];
								pixels[o + 1] = data[s + 1];
								pixels[o + 2] = data[s + 2];
							}

							pixels[o + 3] = 255;
						}
					}
					return true;

				case ColorSpaceKind.Indexed:
					{
						if (samples.Components != 1)
							return false;

						var indexed = (ColorSpace.IndexedColorSpace)colorSpace;
						var palette = indexed.Palette;
						var hival = indexed.HiVal;
						for (int y = 0; y < height; y++)
						{
							var s = y * rowLength;
							var o = y * width * 4;
							for (int x = 0; x < width; x++, o += 4)
							{
								var index = s + x < data.Length ? data[s + x] : 0;
								var color = palette[index > hival ? hival : index];
								pixels[o] = (byte)(color >> 16);
								pixels[o + 1] = (byte)(color >> 8);
								pixels[o + 2] = (byte)color;
								pixels[o + 3] = 255;
							}
						}
					}
					return true;

				default:
					return false;
			}
		}

		/// <summary>
		/// Returns the device colour space implied by a component count: 1 gray, 4 CMYK, anything else RGB.
		/// </summary>
		/// <param name="components">The number of colour components.</param>
		/// <returns>The device colour space.</returns>
		private static ColorSpace DefaultSpace(int components)
		{
			switch (components)
			{
				case 1: return ColorSpace.DeviceGray;
				case 4: return ColorSpace.DeviceCmyk;
				default: return ColorSpace.DeviceRgb;
			}
		}

		// true when the colour space is gray or RGB as browsers interpret JPEG files.
		private static bool IsDisplayable(ColorSpace colorSpace, int components)
		{
			if (components == 1)
				return colorSpace.Kind == ColorSpaceKind.Gray || (colorSpace.Kind == ColorSpaceKind.IccBased && colorSpace.ComponentCount == 1);

			if (components == 3)
				return colorSpace.Kind == ColorSpaceKind.Rgb || (colorSpace.Kind == ColorSpaceKind.IccBased && colorSpace.ComponentCount == 3);

			return false;
		}

		#endregion

		#region Masks

		// applies /SMask, /Mask (stream or colour key); returns true when the image has transparency.
		private static bool ApplyMasks(RasterImage raster, Samples samples, PdfDictionary dictionary, PdfDictionary resources, ResourceCache cache, PdfImageOptions options)
		{
			var applied = false;

			// colour-key masking: [min0 max0 min1 max1 ...] on raw sample values.
			var mask = dictionary.Resolve(PdfNames.Mask);
			if (mask is PdfArray ranges && ranges.Count >= samples.Components * 2)
			{
				var keys = ranges.ToNumbers();
				var pixels = raster.Pixels;
				for (int y = 0; y < samples.Height; y++)
				{
					for (int x = 0; x < samples.Width; x++)
					{
						var masked = true;
						for (int c = 0; c < samples.Components && masked; c++)
						{
							var value = samples.Get(x, y, c);
							masked = value >= keys[c * 2] && value <= keys[c * 2 + 1];
						}

						if (masked)
							pixels[(y * samples.Width + x) * 4 + 3] = 0;
					}
				}

				applied = true;
			}
			else if (mask is PdfStream stencil)
			{
				// explicit masking: a stencil whose 1 samples (with the default decode) mask the image out.
				var alpha = DecodeMaskAlpha(stencil, raster.Width, raster.Height, isStencil: true, resources, cache);
				if (alpha != null)
				{
					SetAlpha(raster, alpha);
					applied = true;
				}
			}

			// soft mask: a grayscale image used as alpha.
			var softMask = dictionary.GetStream(PdfNames.SMask);
			if (softMask != null)
			{
				var alpha = DecodeMaskAlpha(softMask, raster.Width, raster.Height, isStencil: false, resources, cache);
				if (alpha != null)
				{
					var matte = softMask.GetArray(PdfNames.Matte)?.ToNumbers();
					if (matte != null)
						Unpremultiply(raster, alpha, matte);

					SetAlpha(raster, alpha);
					applied = true;
				}
			}

			return applied;
		}

		// decodes a mask image to an alpha plane of the size of the base image.
		private static byte[] DecodeMaskAlpha(PdfStream maskStream, int width, int height, bool isStencil, PdfDictionary resources, ResourceCache cache)
		{
			DecodedImage decoded;
			try
			{
				// the mask is decoded as a plain gray image: stencils with fill white on a transparent background.
				var maskOptions = new PdfImageOptions { ApplyMasks = false };
				decoded = Decode(maskStream, maskStream.GetRawBytes(), resources, cache, 0xFFFFFF, maskOptions);
			}
			catch (Exception)
			{
				return null;
			}

			var raster = decoded?.Raster;
			if (raster == null)
				return null;

			if (raster.Width != width || raster.Height != height)
				raster = raster.Resize(width, height);

			var isImageMask = maskStream.GetBool(PdfNames.ImageMask);
			var pixels = raster.Pixels;
			var alpha = new byte[width * height];
			for (int i = 0; i < alpha.Length; i++)
			{
				var p = i * 4;
				if (isStencil)
				{
					// the stencil painted white where its samples are 0: those areas show the image.
					// a mask stream without /ImageMask decodes as gray: black (0 samples) shows the image.
					alpha[i] = isImageMask ? pixels[p + 3] : (pixels[p] < 128 ? (byte)255 : (byte)0);
				}
				else
				{
					// soft masks are gray images: the gray level is the opacity.
					alpha[i] = pixels[p];
				}
			}

			return alpha;
		}

		/// <summary>
		/// Multiplies the alpha channel of the raster by an alpha plane, so that masks combine.
		/// </summary>
		/// <param name="raster">The image to update.</param>
		/// <param name="alpha">The alpha plane, one byte per pixel, the size of the raster.</param>
		private static void SetAlpha(RasterImage raster, byte[] alpha)
		{
			var pixels = raster.Pixels;
			for (int i = 0; i < alpha.Length; i++)
			{
				var p = i * 4 + 3;
				pixels[p] = (byte)(pixels[p] * alpha[i] / 255);
			}
		}

		// colours were premultiplied with the matte colour: c = m + a × (c' − m)  =>  c' = m + (c − m) / a.
		private static void Unpremultiply(RasterImage raster, byte[] alpha, double[] matte)
		{
			var pixels = raster.Pixels;
			var mr = matte.Length > 0 ? matte[0] * 255 : 0;
			var mg = matte.Length > 1 ? matte[1] * 255 : mr;
			var mb = matte.Length > 2 ? matte[2] * 255 : mr;

			for (int i = 0; i < alpha.Length; i++)
			{
				var a = alpha[i] / 255.0;
				if (a <= 0)
					continue;

				var p = i * 4;
				pixels[p] = Clamp(mr + (pixels[p] - mr) / a);
				pixels[p + 1] = Clamp(mg + (pixels[p + 1] - mg) / a);
				pixels[p + 2] = Clamp(mb + (pixels[p + 2] - mb) / a);
			}
		}

		// uses the interleaved alpha channel of a JPX image.
		private static void ApplyChannelAlpha(RasterImage raster, Samples samples, bool premultiplied)
		{
			var pixels = raster.Pixels;
			for (int y = 0; y < samples.Height; y++)
			{
				for (int x = 0; x < samples.Width; x++)
				{
					var a = samples.Get(x, y, samples.AlphaChannel);
					var p = (y * samples.Width + x) * 4;
					if (premultiplied && a > 0)
					{
						pixels[p] = Clamp(pixels[p] * 255.0 / a);
						pixels[p + 1] = Clamp(pixels[p + 1] * 255.0 / a);
						pixels[p + 2] = Clamp(pixels[p + 2] * 255.0 / a);
					}

					// combined with the alpha already set by /Mask or /SMask.
					pixels[p + 3] = (byte)(pixels[p + 3] * a / 255);
				}
			}
		}

		/// <summary>
		/// Rounds a value to the nearest byte, clamping it to 0..255.
		/// </summary>
		/// <param name="value">The value.</param>
		/// <returns>The clamped byte.</returns>
		private static byte Clamp(double value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + 0.5);

		#endregion
	}
}
