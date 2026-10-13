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

// Tests of the PNG and TIFF predictors (ISO 32000-2, 7.4.4.4) at 8 and 16 bits
// per component, standalone and through the FlateDecode filter.

using System;
using Wisej.AI.Pdf.Filters;
using Wisej.AI.Pdf.Objects;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Filters
{
	/// <summary>
	/// Tests of <see cref="Predictor"/>.
	/// </summary>
	public class PredictorTests
	{
		/// <summary>Every PNG filter type reverses to the raw rows, for 8-bit RGB samples.</summary>
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		public void Png_EachFilterType_8BitRgb(int filter)
		{
			const int Columns = 7, Colors = 3, Rows = 5;
			var raw = FilterTests.CreateData(Columns * Colors * Rows);

			var encoded = Encoders.PngFilter(raw, Columns * Colors, Colors, filter);

			Assert.Equal(raw, Predictor.Apply(encoded, Parameters(10 + filter, Colors, 8, Columns)));
		}

		/// <summary>Every PNG filter type reverses to the raw rows, for 16-bit gray samples (two bytes per pixel).</summary>
		[Theory]
		[InlineData(1)]
		[InlineData(3)]
		[InlineData(4)]
		public void Png_EachFilterType_16BitGray(int filter)
		{
			const int Columns = 9, Rows = 4;
			var raw = FilterTests.CreateData(Columns * 2 * Rows);

			var encoded = Encoders.PngFilter(raw, Columns * 2, 2, filter);

			Assert.Equal(raw, Predictor.Apply(encoded, Parameters(15, 1, 16, Columns)));
		}

		/// <summary>The filter type is read from each row, so mixed filters decode (the /Predictor value is only a hint).</summary>
		[Fact]
		public void Png_MixedFiltersPerRow()
		{
			const int Columns = 4, Colors = 1;
			var raw = FilterTests.CreateData(Columns * 5);

			// filter each row with a different type.
			var encoded = new byte[5 * (Columns + 1)];
			for (int row = 0; row < 5; row++)
			{
				var rowFiltered = Encoders.PngFilter(raw, Columns, Colors, row);
				Buffer.BlockCopy(rowFiltered, row * (Columns + 1), encoded, row * (Columns + 1), Columns + 1);
			}

			Assert.Equal(raw, Predictor.Apply(encoded, Parameters(10, Colors, 8, Columns)));
		}

		/// <summary>TIFF predictor 2 reverses horizontal differencing for 8-bit samples.</summary>
		[Fact]
		public void Tiff_8BitCmyk()
		{
			const int Columns = 6, Colors = 4;
			var raw = FilterTests.CreateData(Columns * Colors * 3);

			var encoded = Encoders.TiffPredictor(raw, Columns, Colors, 8);

			Assert.Equal(raw, Predictor.Apply(encoded, Parameters(2, Colors, 8, Columns)));
		}

		/// <summary>TIFF predictor 2 reverses horizontal differencing for 16-bit samples, with carries between bytes.</summary>
		[Fact]
		public void Tiff_16BitRgb()
		{
			const int Columns = 5, Colors = 3;
			var raw = FilterTests.CreateData(Columns * Colors * 2 * 3);

			var encoded = Encoders.TiffPredictor(raw, Columns, Colors, 16);

			Assert.Equal(raw, Predictor.Apply(encoded, Parameters(2, Colors, 16, Columns)));
		}

		/// <summary>TIFF predictor 2 on 1-bit samples: each bit is the running XOR of the differences.</summary>
		[Fact]
		public void Tiff_1Bit()
		{
			// the differences 1,0,0,0,1,0,0,0 give the samples 1,1,1,1,0,0,0,0.
			var encoded = new byte[] { 0b1000_1000 };

			Assert.Equal(new byte[] { 0b1111_0000 }, Predictor.Apply(encoded, Parameters(2, 1, 1, 8)));
		}

		/// <summary>Through the FlateDecode filter, the /DecodeParms predictor is applied after inflating.</summary>
		[Fact]
		public void Flate_WithPngPredictor_ThroughStream()
		{
			const int Columns = 10;
			var raw = FilterTests.CreateData(Columns * 3 * 8);
			var data = PdfBuilder.Deflate(Encoders.PngFilter(raw, Columns * 3, 3, 4));

			var stream = TestPdf.ParseStream($"/Filter /FlateDecode /DecodeParms << /Predictor 12 /Colors 3 /Columns {Columns} >>", data);

			Assert.Equal(raw, stream.GetBytes());
		}

		/// <summary>A truncated last row is kept as far as it goes.</summary>
		[Fact]
		public void Png_TruncatedLastRow_KeepsAvailableBytes()
		{
			var raw = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
			var encoded = Encoders.PngFilter(raw, 4, 1, 1);

			var decoded = Predictor.Apply(encoded.AsSpan(0, encoded.Length - 2).ToArray(), Parameters(11, 1, 8, 4));

			Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, decoded);
		}

		/// <summary>
		/// Creates predictor decode parameters.
		/// </summary>
		/// <param name="predictor">The /Predictor value.</param>
		/// <param name="colors">The /Colors value.</param>
		/// <param name="bitsPerComponent">The /BitsPerComponent value.</param>
		/// <param name="columns">The /Columns value.</param>
		/// <returns>The parameters dictionary.</returns>
		private static PdfDictionary Parameters(int predictor, int colors, int bitsPerComponent, int columns)
		{
			return new PdfDictionary
			{
				[PdfName.Get("Predictor")] = predictor,
				[PdfName.Get("Colors")] = colors,
				[PdfName.Get("BitsPerComponent")] = bitsPerComponent,
				[PdfName.Get("Columns")] = columns,
			};
		}
	}
}
