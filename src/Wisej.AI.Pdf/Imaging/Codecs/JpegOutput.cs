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

// Converts decoded JPEG components into interleaved pixels: inverse DCT, chroma upsampling
// and colour conversion (ITU-T T.81, A.3; JFIF 1.02 and Adobe Technical Note 5116).

using System;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Renders the components of a decoded frame into interleaved 8-bit samples.
	/// </summary>
	internal static class JpegOutput
	{
		#region Nested types

		/// <summary>
		/// The colour conversion applied when interleaving the components.
		/// </summary>
		public enum ColorConversion
		{
			/// <summary>The samples are copied as they are.</summary>
			None,

			/// <summary>Three components are converted from YCbCr to RGB.</summary>
			YCbCrToRgb,

			/// <summary>Four components are converted from YCCK to CMYK.</summary>
			YcckToCmyk,
		}

		// how one component is resampled to the full image resolution.
		private enum UpsampleMode
		{
			// the component has full resolution.
			Copy,

			// libjpeg-style triangle filter, 2x horizontally.
			Fancy2x1,

			// libjpeg-style triangle filter, 2x vertically.
			Fancy1x2,

			// libjpeg-style triangle filter, 2x in both directions.
			Fancy2x2,

			// pixel replication for any other ratio.
			Replicate,
		}

		// the upsampling state of one component.
		private sealed class Upsampler
		{
			// the component.
			public JpegComponent Component;

			// the resampling method.
			public UpsampleMode Mode;

			// one full-resolution output row.
			public byte[] Row;

			// the vertically filtered sums of two input rows (Fancy2x2).
			public int[] ColumnSums;

			// for Replicate: the source column of each output column.
			public int[] ColumnMap;
		}

		#endregion

		#region Fields

		// fixed-point fraction bits of the colour conversion tables.
		private const int ScaleBits = 16;

		// Cr contribution to R: 1.402 * (Cr - 128).
		private static readonly int[] CrToR = new int[256];

		// Cb contribution to B: 1.772 * (Cb - 128).
		private static readonly int[] CbToB = new int[256];

		// Cr contribution to G, scaled: -0.714136 * (Cr - 128).
		private static readonly int[] CrToG = new int[256];

		// Cb contribution to G, scaled, including the rounding term: -0.344136 * (Cb - 128).
		private static readonly int[] CbToG = new int[256];

		#endregion

		#region Constructors

		/// <summary>
		/// Builds the colour conversion tables.
		/// </summary>
		static JpegOutput()
		{
			const int one = 1 << ScaleBits;
			const int half = 1 << (ScaleBits - 1);
			for (int i = 0; i < 256; i++)
			{
				var x = i - 128;
				CrToR[i] = (int)((1.40200 * one) * x + half) >> ScaleBits;
				CbToB[i] = (int)((1.77200 * one) * x + half) >> ScaleBits;
				CrToG[i] = -(int)(0.71414 * one) * x;
				CbToG[i] = -(int)(0.34414 * one) * x + half;
			}
		}

		#endregion

		#region Methods

		/// <summary>
		/// Renders the frame into interleaved samples.
		/// </summary>
		/// <param name="frame">The decoded frame.</param>
		/// <param name="tables">The coding tables (for quantization tables that were never latched).</param>
		/// <param name="conversion">The colour conversion to apply.</param>
		/// <returns>The samples, <c>Width * Height * Components</c> bytes.</returns>
		public static byte[] Render(JpegFrame frame, JpegTables tables, ColorConversion conversion)
		{
			foreach (var component in frame.Components)
				RenderPlane(frame, tables, component);

			var width = frame.Width;
			var height = frame.Height;
			var count = frame.Components.Length;
			var output = new byte[(long)width * height * count];

			var upsamplers = new Upsampler[count];
			for (int i = 0; i < count; i++)
				upsamplers[i] = CreateUpsampler(frame, frame.Components[i]);

			if (count != 3 && conversion == ColorConversion.YCbCrToRgb)
				conversion = ColorConversion.None;

			if (count != 4 && conversion == ColorConversion.YcckToCmyk)
				conversion = ColorConversion.None;

			// rows lost to truncation repeat the last decoded row.
			var validRows = frame.ValidRows > 0 ? Math.Min(height, frame.ValidRows) : height;

			var rowLength = width * count;
			for (int y = 0; y < validRows; y++)
			{
				for (int i = 0; i < count; i++)
					Upsample(frame, upsamplers[i], y);

				var target = y * rowLength;
				switch (conversion)
				{
					case ColorConversion.YCbCrToRgb:
						ConvertYCbCr(upsamplers[0].Row, upsamplers[1].Row, upsamplers[2].Row, output, target, width, 3, false);
						break;

					case ColorConversion.YcckToCmyk:
						ConvertYCbCr(upsamplers[0].Row, upsamplers[1].Row, upsamplers[2].Row, output, target, width, 4, true);
						Interleave(upsamplers[3].Row, output, target + 3, width, 4);
						break;

					default:
						if (count == 1)
						{
							Buffer.BlockCopy(upsamplers[0].Row, 0, output, target, width);
						}
						else
						{
							for (int i = 0; i < count; i++)
								Interleave(upsamplers[i].Row, output, target + i, width, count);
						}

						break;
				}
			}

			for (int y = validRows; y < height; y++)
				Buffer.BlockCopy(output, (validRows - 1) * rowLength, output, y * rowLength, rowLength);

			return output;
		}

		#endregion

		#region Implementation

		// produces the 8-bit plane of a component: inverse DCT of every block, or the scaled lossless samples.
		private static void RenderPlane(JpegFrame frame, JpegTables tables, JpegComponent component)
		{
			if (frame.IsLossless)
			{
				RenderLosslessPlane(frame, component);
				return;
			}

			var stride = component.BlocksPerLine * 8;
			var plane = new byte[(long)stride * component.BlocksPerColumn * 8];
			var quant = component.Quant ?? tables.QuantTables[component.QuantIndex & 3]
				?? JpegTables.CreateDefaultQuantTable(Array.IndexOf(frame.Components, component) > 0);

			var coefficients = component.Coefficients;
			var workspace = new int[64];
			for (int blockRow = 0; blockRow < component.BlocksPerColumn; blockRow++)
			{
				var source = blockRow * component.BlocksPerLineAlloc * 64;
				var target = blockRow * 8 * stride;
				for (int blockColumn = 0; blockColumn < component.BlocksPerLine; blockColumn++)
				{
					JpegIdct.Transform(coefficients, source, quant, workspace, plane, target, stride, frame.Precision);
					source += 64;
					target += 8;
				}
			}

			// the coefficients are no longer needed: release them before the output is allocated.
			component.Coefficients = null;
			component.Plane = plane;
			component.PlaneStride = stride;
		}

		// scales the lossless samples (after the point transform) to 8 bits.
		private static void RenderLosslessPlane(JpegFrame frame, JpegComponent component)
		{
			var stride = component.BlocksPerLineAlloc;
			var samples = component.Samples;
			var plane = new byte[samples.Length];
			var precision = Math.Max(2, Math.Min(16, frame.Precision));
			var max = (1 << precision) - 1;
			for (int i = 0; i < samples.Length; i++)
			{
				var value = samples[i] & max;
				plane[i] = (byte)(precision == 8 ? value : (value * 255 + (max >> 1)) / max);
			}

			component.Samples = null;
			component.Plane = plane;
			component.PlaneStride = stride;
		}

		// chooses the resampling method of a component.
		private static Upsampler CreateUpsampler(JpegFrame frame, JpegComponent component)
		{
			var upsampler = new Upsampler
			{
				Component = component,
				Row = new byte[frame.Width + 1],
			};

			var h = component.H;
			var v = component.V;
			if (h == frame.MaxH && v == frame.MaxV)
				upsampler.Mode = UpsampleMode.Copy;
			else if (h * 2 == frame.MaxH && v == frame.MaxV)
				upsampler.Mode = UpsampleMode.Fancy2x1;
			else if (h == frame.MaxH && v * 2 == frame.MaxV)
				upsampler.Mode = UpsampleMode.Fancy1x2;
			else if (h * 2 == frame.MaxH && v * 2 == frame.MaxV)
				upsampler.Mode = UpsampleMode.Fancy2x2;
			else
				upsampler.Mode = UpsampleMode.Replicate;

			// the triangle filter needs at least two samples in the filtered direction.
			if ((upsampler.Mode == UpsampleMode.Fancy2x1 || upsampler.Mode == UpsampleMode.Fancy2x2) && component.Width < 2)
				upsampler.Mode = UpsampleMode.Replicate;

			if (upsampler.Mode == UpsampleMode.Fancy2x2)
				upsampler.ColumnSums = new int[component.Width];

			if (upsampler.Mode == UpsampleMode.Replicate)
			{
				upsampler.ColumnMap = new int[frame.Width];
				for (int x = 0; x < frame.Width; x++)
					upsampler.ColumnMap[x] = Math.Min(component.Width - 1, (int)((long)x * h / frame.MaxH));
			}

			return upsampler;
		}

		// fills the row buffer of a component with output row y.
		private static void Upsample(JpegFrame frame, Upsampler upsampler, int y)
		{
			var component = upsampler.Component;
			var plane = component.Plane;
			var stride = component.PlaneStride;
			var row = upsampler.Row;
			var lastRow = component.Height - 1;
			var width = frame.Width;

			switch (upsampler.Mode)
			{
				case UpsampleMode.Copy:
					Buffer.BlockCopy(plane, Math.Min(y, lastRow) * stride, row, 0, width);
					break;

				case UpsampleMode.Fancy2x1:
					UpsampleFancy2x1(plane, Math.Min(y, lastRow) * stride, component.Width, row, width);
					break;

				case UpsampleMode.Fancy1x2:
				{
					// each output row is 3/4 of the nearer input row plus 1/4 of the farther one.
					var near = Math.Min(y >> 1, lastRow);
					var far = Clamp((y & 1) == 0 ? near - 1 : near + 1, lastRow);
					var nearOffset = near * stride;
					var farOffset = far * stride;
					var bias = (y & 1) == 0 ? 1 : 2;
					for (int x = 0; x < width; x++)
						row[x] = (byte)((plane[nearOffset + x] * 3 + plane[farOffset + x] + bias) >> 2);

					break;
				}

				case UpsampleMode.Fancy2x2:
				{
					var near = Math.Min(y >> 1, lastRow);
					var far = Clamp((y & 1) == 0 ? near - 1 : near + 1, lastRow);
					var nearOffset = near * stride;
					var farOffset = far * stride;
					var sums = upsampler.ColumnSums;
					var columns = component.Width;
					for (int x = 0; x < columns; x++)
						sums[x] = plane[nearOffset + x] * 3 + plane[farOffset + x];

					UpsampleFancy2x2(sums, columns, row, width);
					break;
				}

				default:
				{
					var sourceRow = Math.Min((int)((long)y * component.V / frame.MaxV), lastRow);
					var offset = sourceRow * stride;
					var map = upsampler.ColumnMap;
					for (int x = 0; x < width; x++)
						row[x] = plane[offset + map[x]];

					break;
				}
			}
		}

		// horizontal triangle filter: each output sample is 3/4 of the nearer input plus 1/4 of the other neighbour.
		private static void UpsampleFancy2x1(byte[] plane, int offset, int columns, byte[] row, int width)
		{
			var last = columns - 1;
			row[0] = plane[offset];
			row[1] = (byte)((plane[offset] * 3 + plane[offset + 1] + 2) >> 2);
			for (int x = 1; x < last; x++)
			{
				var current = plane[offset + x] * 3;
				row[2 * x] = (byte)((current + plane[offset + x - 1] + 1) >> 2);
				row[2 * x + 1] = (byte)((current + plane[offset + x + 1] + 2) >> 2);
			}

			// the last input sample: the output row may be one sample shorter than 2 * columns.
			var end = 2 * last;
			if (end < width)
				row[end] = (byte)((plane[offset + last] * 3 + plane[offset + last - 1] + 1) >> 2);

			if (end + 1 < width)
				row[end + 1] = plane[offset + last];
		}

		// horizontal pass of the 2x2 triangle filter over the vertically weighted sums (total weight 16).
		private static void UpsampleFancy2x2(int[] sums, int columns, byte[] row, int width)
		{
			var last = columns - 1;
			row[0] = (byte)((sums[0] * 4 + 8) >> 4);
			row[1] = (byte)((sums[0] * 3 + sums[1] + 7) >> 4);
			for (int x = 1; x < last; x++)
			{
				var current = sums[x] * 3;
				row[2 * x] = (byte)((current + sums[x - 1] + 8) >> 4);
				row[2 * x + 1] = (byte)((current + sums[x + 1] + 7) >> 4);
			}

			var end = 2 * last;
			if (end < width)
				row[end] = (byte)((sums[last] * 3 + sums[last - 1] + 8) >> 4);

			if (end + 1 < width)
				row[end + 1] = (byte)((sums[last] * 4 + 7) >> 4);
		}

		// converts YCbCr (or the YCC part of YCCK) to RGB (or inverted RGB = CMY) into the interleaved output.
		private static void ConvertYCbCr(byte[] yRow, byte[] cbRow, byte[] crRow, byte[] output, int target, int width, int pixelSize, bool invert)
		{
			var mask = invert ? 255 : 0;
			for (int x = 0; x < width; x++)
			{
				int luma = yRow[x];
				int cb = cbRow[x];
				int cr = crRow[x];

				var r = luma + CrToR[cr];
				var g = luma + ((CbToG[cb] + CrToG[cr]) >> ScaleBits);
				var b = luma + CbToB[cb];

				output[target] = (byte)(ClampByte(r) ^ mask);
				output[target + 1] = (byte)(ClampByte(g) ^ mask);
				output[target + 2] = (byte)(ClampByte(b) ^ mask);
				target += pixelSize;
			}
		}

		// copies one component row into every pixelSize-th byte of the output.
		private static void Interleave(byte[] row, byte[] output, int target, int width, int pixelSize)
		{
			for (int x = 0; x < width; x++)
			{
				output[target] = row[x];
				target += pixelSize;
			}
		}

		// clamps a row index to 0..last.
		private static int Clamp(int value, int last)
		{
			return value < 0 ? 0 : (value > last ? last : value);
		}

		// clamps a sample to 0..255.
		private static int ClampByte(int value)
		{
			return (uint)value > 255 ? (value < 0 ? 0 : 255) : value;
		}

		#endregion
	}
}
