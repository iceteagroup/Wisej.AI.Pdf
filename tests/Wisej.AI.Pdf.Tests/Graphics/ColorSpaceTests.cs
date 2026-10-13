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

// Tests of the colour spaces (ISO 32000-2, 8.6) and their conversion to sRGB.

using System;
using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Objects;
using Wisej.AI.Pdf.Tests.Helpers;
using Xunit;

namespace Wisej.AI.Pdf.Tests.Graphics
{
	/// <summary>
	/// Tests of <see cref="ColorSpace"/>.
	/// </summary>
	public class ColorSpaceTests
	{
		#region Device spaces

		/// <summary>DeviceGray maps the gray level to equal RGB channels, rounding to the nearest byte.</summary>
		[Theory]
		[InlineData(0.0, 0x000000)]
		[InlineData(0.5, 0x808080)]
		[InlineData(1.0, 0xFFFFFF)]
		[InlineData(1.5, 0xFFFFFF)]
		[InlineData(-0.5, 0x000000)]
		public void DeviceGray_ToRgb(double gray, int expected)
		{
			Assert.Equal(expected, Create("/DeviceGray").ToRgb(new[] { gray }));
		}

		/// <summary>DeviceRGB components map to bytes; abbreviated and calibrated names are aliases.</summary>
		[Theory]
		[InlineData("/DeviceRGB")]
		[InlineData("/RGB")]
		[InlineData("[/CalRGB << /WhitePoint [0.9505 1 1.089] >>]")]
		public void DeviceRgb_ToRgb(string definition)
		{
			var space = Create(definition);

			Assert.Equal(3, space.ComponentCount);
			Assert.Equal(0xFF8000, space.ToRgb(new[] { 1.0, 0.5, 0.0 }));
		}

		/// <summary>DeviceCMYK follows the SWOP polynomial approximation: white, rich black and the process primaries.</summary>
		[Theory]
		[InlineData(0, 0, 0, 0, 0xFFFFFF, 2)]
		[InlineData(0, 0, 0, 1, 0x2C2E35, 3)]
		[InlineData(1, 0, 0, 0, 0x00B9F2, 3)]
		[InlineData(0, 1, 0, 0, 0xFB3199, 3)]
		[InlineData(0, 0, 1, 0, 0xFFEB3D, 3)]
		public void DeviceCmyk_ToRgb_Approximately(double c, double m, double y, double k, int expected, int tolerance)
		{
			var actual = Rgba.FromRgb(Create("/DeviceCMYK").ToRgb(new[] { c, m, y, k }));

			Assert.True(actual.IsCloseTo(Rgba.FromRgb(expected), tolerance), $"CMYK ({c}, {m}, {y}, {k}) gave {actual}, expected about {Rgba.FromRgb(expected)}.");
		}

		/// <summary>The initial colour of DeviceCMYK is black (K = 1).</summary>
		[Fact]
		public void DeviceCmyk_InitialColorIsBlack()
		{
			Assert.Equal(new[] { 0.0, 0, 0, 1 }, Create("/DeviceCMYK").GetInitialColor());
		}

		/// <summary>A named colour space is resolved through the /ColorSpace resources.</summary>
		[Fact]
		public void NamedColorSpace_IsResolvedFromResources()
		{
			var resources = Assert.IsType<PdfDictionary>(TestPdf.ParseObject("<< /ColorSpace << /CS0 [/Indexed /DeviceRGB 0 <00FF00>] >> >>"));

			var space = ColorSpace.Create(PdfName.Get("CS0"), resources);

			Assert.Equal(ColorSpaceKind.Indexed, space.Kind);
			Assert.Equal(0x00FF00, space.ToRgb(new[] { 0.0 }));
		}

		/// <summary>An unknown colour space falls back to DeviceGray instead of failing.</summary>
		[Fact]
		public void UnknownColorSpace_FallsBackToGray()
		{
			Assert.Same(ColorSpace.DeviceGray, ColorSpace.Create(PdfName.Get("NoSuchSpace"), null));
		}

		#endregion

		#region Indexed

		/// <summary>Indexed colours look up the palette; indices beyond hival are clamped.</summary>
		[Theory]
		[InlineData(0, 0xFF0000)]
		[InlineData(1, 0x00FF00)]
		[InlineData(2, 0x0000FF)]
		[InlineData(9, 0x0000FF)]
		public void Indexed_LooksUpPalette(int index, int expected)
		{
			var space = Create("[/Indexed /DeviceRGB 2 <FF0000 00FF00 0000FF>]");

			Assert.Equal(expected, space.ToRgb(new double[] { index }));
		}

		/// <summary>The palette can be a stream and the base space can be CMYK.</summary>
		[Fact]
		public void Indexed_CmykBaseInStream()
		{
			var lookup = TestPdf.ParseStream(string.Empty, new byte[] { 0, 0, 0, 0, 0, 0, 0, 255 });
			var array = new PdfArray { PdfName.Get("Indexed"), PdfName.Get("DeviceCMYK"), 1, lookup };

			var space = ColorSpace.Create(array, null);

			Assert.Equal(0xFFFFFF, space.ToRgb(new[] { 0.0 }));
			Assert.True(Rgba.FromRgb(space.ToRgb(new[] { 1.0 })).R < 70);
		}

		/// <summary>The default decode of an Indexed space maps samples to palette indices.</summary>
		[Fact]
		public void Indexed_DefaultDecodeIsIndexRange()
		{
			Assert.Equal(new[] { 0.0, 15 }, Create("[/Indexed /DeviceGray 15 <00>]").GetDefaultDecode(4));
		}

		#endregion

		#region CIE-based

		/// <summary>Lab with a D65 white point: white, black and an sRGB primary.</summary>
		[Theory]
		[InlineData(100, 0, 0, 0xFFFFFF, 3)]
		[InlineData(0, 0, 0, 0x000000, 3)]
		[InlineData(53.24, 80.09, 67.2, 0xFF0000, 6)]
		[InlineData(87.73, -86.18, 83.18, 0x00FF00, 6)]
		public void Lab_D65_ToRgb(double l, double a, double b, int expected, int tolerance)
		{
			var space = Create("[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]");

			var actual = Rgba.FromRgb(space.ToRgb(new[] { l, a, b }));

			Assert.True(actual.IsCloseTo(Rgba.FromRgb(expected), tolerance), $"Lab ({l}, {a}, {b}) gave {actual}.");
		}

		/// <summary>Lab with a D50 white point is chromatically adapted: L 100 is still white.</summary>
		[Fact]
		public void Lab_D50_WhiteIsWhite()
		{
			var space = Create("[/Lab << /WhitePoint [0.9642 1 0.8249] >>]");

			var actual = Rgba.FromRgb(space.ToRgb(new[] { 100.0, 0, 0 }));

			Assert.True(actual.IsCloseTo(new Rgba(255, 255, 255), 3), $"D50 white gave {actual}.");
		}

		/// <summary>Lab components outside /Range are clipped.</summary>
		[Fact]
		public void Lab_ComponentsAreClippedToRange()
		{
			var space = Create("[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-10 10 -10 10] >>]");

			Assert.Equal(space.ToRgb(new[] { 50.0, 10, -10 }), space.ToRgb(new[] { 50.0, 90, -90 }));
		}

		/// <summary>ICCBased spaces use their alternate (or the space implied by /N).</summary>
		[Fact]
		public void IccBased_UsesAlternate()
		{
			var profile = TestPdf.ParseStream("/N 3 /Alternate /DeviceRGB", new byte[] { 1, 2, 3 });
			var array = new PdfArray { PdfName.Get("ICCBased"), profile };

			var space = ColorSpace.Create(array, null);

			Assert.Equal(ColorSpaceKind.IccBased, space.Kind);
			Assert.Equal(3, space.ComponentCount);
			Assert.Equal(0x0080FF, space.ToRgb(new[] { 0.0, 0.5, 1.0 }));
			Assert.Equal(new byte[] { 1, 2, 3 }, space.IccProfile);
		}

		#endregion

		#region Special spaces

		/// <summary>Separation with an exponential tint transform to RGB: tint 0 is white, tint 1 the full colorant.</summary>
		[Theory]
		[InlineData(0.0, 0xFFFFFF)]
		[InlineData(0.5, 0xFF8080)]
		[InlineData(1.0, 0xFF0000)]
		public void Separation_ExponentialTint(double tint, int expected)
		{
			var space = Create("[/Separation /SpotRed /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>]");

			var actual = Rgba.FromRgb(space.ToRgb(new[] { tint }));

			Assert.True(actual.IsCloseTo(Rgba.FromRgb(expected), 1), $"tint {tint} gave {actual}.");
		}

		/// <summary>Separation through a CMYK alternate, with a non-linear exponent.</summary>
		[Fact]
		public void Separation_CmykAlternateWithExponent()
		{
			var space = Create("[/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 2 >>]");

			var half = Rgba.FromRgb(space.ToRgb(new[] { 0.5 }));
			var full = Rgba.FromRgb(space.ToRgb(new[] { 1.0 }));

			// K = 0.25 at half tint: much lighter than full black.
			Assert.True(half.R > 170 && full.R < 70, $"half {half}, full {full}");
		}

		/// <summary>The special colorant "None" never marks the page; "All" paints in gray.</summary>
		[Fact]
		public void Separation_NoneAndAll()
		{
			var none = Create("[/Separation /None /DeviceGray << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>]");
			var all = Create("[/Separation /All /DeviceGray << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>]");

			Assert.Equal(0xFFFFFF, none.ToRgb(new[] { 1.0 }));
			Assert.Equal(0x000000, all.ToRgb(new[] { 1.0 }));
		}

		/// <summary>DeviceN with a PostScript tint transform that mixes two inks into RGB.</summary>
		[Fact]
		public void DeviceN_PostScriptTint()
		{
			var tint = TestPdf.ParseStream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ exch 1 exch sub exch 1 exch sub 1 }");
			var array = new PdfArray
			{
				PdfName.Get("DeviceN"),
				new PdfArray { PdfName.Get("Cyanish"), PdfName.Get("Magentaish") },
				PdfName.Get("DeviceRGB"),
				tint,
			};

			var space = ColorSpace.Create(array, null);

			Assert.Equal(2, space.ComponentCount);
			Assert.Equal(0x00FFFF, space.ToRgb(new[] { 1.0, 0 }));
			Assert.Equal(0xFF00FF, space.ToRgb(new[] { 0.0, 1 }));
		}

		#endregion

		/// <summary>
		/// Creates a colour space from its definition in PDF syntax.
		/// </summary>
		/// <param name="definition">A name or an array.</param>
		/// <returns>The colour space.</returns>
		private static ColorSpace Create(string definition)
		{
			var space = ColorSpace.TryCreate(TestPdf.ParseObject(definition), null);
			Assert.NotNull(space);
			return space;
		}
	}
}
