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

// Colour spaces (ISO 32000-2, 8.6) and their conversion to sRGB.
//
// The DeviceCMYK conversion uses the polynomial approximation of the
// US Web Coated (SWOP) profile published by Mozilla pdf.js (Apache-2.0).

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Graphics
{
	/// <summary>
	/// Identifies the family of a colour space.
	/// </summary>
	internal enum ColorSpaceKind
	{
		/// <summary>DeviceGray or CalGray.</summary>
		Gray,

		/// <summary>DeviceRGB or CalRGB.</summary>
		Rgb,

		/// <summary>DeviceCMYK.</summary>
		Cmyk,

		/// <summary>CIE L*a*b*.</summary>
		Lab,

		/// <summary>ICC profile based.</summary>
		IccBased,

		/// <summary>Indexed (palette).</summary>
		Indexed,

		/// <summary>Separation (single colorant).</summary>
		Separation,

		/// <summary>DeviceN (multiple colorants).</summary>
		DeviceN,

		/// <summary>Pattern.</summary>
		Pattern,
	}

	/// <summary>
	/// Converts colours from a PDF colour space to 8-bit sRGB.
	/// </summary>
	/// <remarks>
	/// Components are passed as values in the colour space range: 0..1 for device spaces,
	/// 0..hival for Indexed, and the declared ranges for Lab.
	/// </remarks>
	internal abstract class ColorSpace
	{
		/// <summary>
		/// The shared DeviceGray instance.
		/// </summary>
		public static readonly ColorSpace DeviceGray = new GrayColorSpace();

		/// <summary>
		/// The shared DeviceRGB instance.
		/// </summary>
		public static readonly ColorSpace DeviceRgb = new RgbColorSpace();

		/// <summary>
		/// The shared DeviceCMYK instance.
		/// </summary>
		public static readonly ColorSpace DeviceCmyk = new CmykColorSpace();

		#region Properties

		/// <summary>
		/// The family of the colour space.
		/// </summary>
		public abstract ColorSpaceKind Kind { get; }

		/// <summary>
		/// The number of colour components.
		/// </summary>
		public abstract int ComponentCount { get; }

		/// <summary>
		/// The ICC profile bytes, when the colour space is ICC based and the profile is available.
		/// </summary>
		public virtual byte[] IccProfile => null;

		#endregion

		#region Methods

		/// <summary>
		/// Converts a colour to sRGB.
		/// </summary>
		/// <param name="components">The colour components; missing components are treated as 0.</param>
		/// <returns>The colour packed as 0xRRGGBB.</returns>
		public abstract int ToRgb(double[] components);

		/// <summary>
		/// Returns the default <c>/Decode</c> array for images with <paramref name="bitsPerComponent"/> bits per component.
		/// </summary>
		/// <param name="bitsPerComponent">The number of bits per component.</param>
		/// <returns>Pairs of minimum and maximum values, one pair per component.</returns>
		public virtual double[] GetDefaultDecode(int bitsPerComponent)
		{
			var decode = new double[this.ComponentCount * 2];
			for (int i = 0; i < this.ComponentCount; i++)
				decode[i * 2 + 1] = 1;

			return decode;
		}

		/// <summary>
		/// Returns the initial colour set when the colour space is selected with <c>CS</c>/<c>cs</c>.
		/// </summary>
		/// <returns>The initial components.</returns>
		public virtual double[] GetInitialColor()
		{
			// black for most spaces; overridden where the specification says otherwise.
			return new double[this.ComponentCount];
		}

		/// <summary>
		/// Converts a whole row of image samples to RGB.
		/// </summary>
		/// <param name="samples">Decoded component values (already mapped through the Decode array), <see cref="ComponentCount"/> per pixel.</param>
		/// <param name="pixelCount">The number of pixels.</param>
		/// <param name="rgb">Receives 3 bytes per pixel.</param>
		/// <remarks>
		/// The default implementation caches the conversion of repeated colours, which makes
		/// expensive spaces (DeviceN, Separation with PostScript functions, Lab) fast on typical images.
		/// </remarks>
		public virtual void ToRgbRow(float[] samples, int pixelCount, byte[] rgb)
		{
			var n = this.ComponentCount;
			var components = new double[n];
			var cache = new Dictionary<long, int>();

			for (int p = 0; p < pixelCount; p++)
			{
				// quantize to 8 bits per component to build a cache key (up to 8 components).
				long key = 0;
				for (int c = 0; c < n && c < 8; c++)
					key = (key << 8) | (uint)Math.Max(0, Math.Min(255, (int)(samples[p * n + c] * 255 + 0.5)));

				if (n > 8 || !cache.TryGetValue(key, out var color))
				{
					for (int c = 0; c < n; c++)
						components[c] = samples[p * n + c];

					color = ToRgb(components);
					if (n <= 8 && cache.Count < 65536)
						cache[key] = color;
				}

				rgb[p * 3] = (byte)(color >> 16);
				rgb[p * 3 + 1] = (byte)(color >> 8);
				rgb[p * 3 + 2] = (byte)color;
			}
		}

		/// <summary>
		/// Packs 0..1 RGB components into 0xRRGGBB.
		/// </summary>
		/// <param name="r">Red, 0..1.</param>
		/// <param name="g">Green, 0..1.</param>
		/// <param name="b">Blue, 0..1.</param>
		/// <returns>The packed colour.</returns>
		protected static int Pack(double r, double g, double b) => (ToByte(r) << 16) | (ToByte(g) << 8) | ToByte(b);

		/// <summary>
		/// Converts a 0..1 value to a byte, clamping out-of-range values.
		/// </summary>
		/// <param name="value">The value.</param>
		/// <returns>The byte value.</returns>
		protected static int ToByte(double value)
		{
			if (!(value > 0))
				return 0;

			return value >= 1 ? 255 : (int)(value * 255 + 0.5);
		}

		/// <summary>
		/// Returns the component at <paramref name="index"/>, or 0 when missing.
		/// </summary>
		/// <param name="components">The components.</param>
		/// <param name="index">The index.</param>
		/// <returns>The component value.</returns>
		protected static double Get(double[] components, int index) => index < components.Length ? components[index] : 0;

		/// <summary>
		/// Converts CMYK (0..1) to packed sRGB using a polynomial fit of the SWOP profile.
		/// </summary>
		/// <param name="c">Cyan.</param>
		/// <param name="m">Magenta.</param>
		/// <param name="y">Yellow.</param>
		/// <param name="k">Black.</param>
		/// <returns>The packed colour.</returns>
		public static int CmykToRgb(double c, double m, double y, double k)
		{
			c = Clamp01(c);
			m = Clamp01(m);
			y = Clamp01(y);
			k = Clamp01(k);

			var r = 255
				+ c * (-4.387332384609988 * c + 54.48615194189176 * m + 18.82290502165302 * y + 212.25662451639585 * k - 285.2331026137004)
				+ m * (1.7149763477362134 * m - 5.6096736904047315 * y - 17.873870861415444 * k - 5.497006427196366)
				+ y * (-2.5217340131683033 * y - 21.248923337353073 * k + 17.5119270841813)
				+ k * (-21.86122147463605 * k - 189.48180835922747);

			var g = 255
				+ c * (8.841041422036149 * c + 60.118027045597366 * m + 6.871425592049007 * y + 31.159100130055922 * k - 79.2970844816548)
				+ m * (-15.310361306967817 * m + 17.575251261109482 * y + 131.35250912493976 * k - 190.9453302588951)
				+ y * (4.444339102852739 * y + 9.8632861493405 * k - 24.86741582555878)
				+ k * (-20.737325471181034 * k - 187.80453709719578);

			var b = 255
				+ c * (0.8842522430003296 * c + 8.078677503112928 * m + 30.89978309703729 * y - 0.23883238689178934 * k - 14.183576799673286)
				+ m * (10.49593273432072 * m + 63.02378494754052 * y + 50.606957656360734 * k - 112.23884253719248)
				+ y * (0.03296041114873217 * y + 115.60384449646641 * k - 193.58209356861505)
				+ k * (-22.33816807309886 * k - 180.12613974708367);

			return (ClampByte(r) << 16) | (ClampByte(g) << 8) | ClampByte(b);
		}

		/// <summary>
		/// Clamps <paramref name="value"/> to the 0..1 range.
		/// </summary>
		/// <param name="value">The value to clamp.</param>
		/// <returns>The clamped value.</returns>
		private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

		/// <summary>
		/// Rounds a 0..255 channel value to the nearest integer, clamping out-of-range values.
		/// </summary>
		/// <param name="value">The channel value, nominally 0..255.</param>
		/// <returns>The channel value as an integer in 0..255.</returns>
		private static int ClampByte(double value) => value <= 0 ? 0 : value >= 255 ? 255 : (int)(value + 0.5);

		#endregion

		#region Factory

		/// <summary>
		/// Creates the colour space described by <paramref name="definition"/>.
		/// </summary>
		/// <param name="definition">A colour space name or array, possibly a reference.</param>
		/// <param name="resources">The resources used to look up named colour spaces; can be null.</param>
		/// <returns>The colour space; DeviceGray when the definition is invalid.</returns>
		public static ColorSpace Create(object definition, PdfDictionary resources)
		{
			return Create(definition, resources, 0) ?? DeviceGray;
		}

		/// <summary>
		/// Creates the colour space described by <paramref name="definition"/>, or null when invalid.
		/// </summary>
		/// <param name="definition">A colour space name or array, possibly a reference.</param>
		/// <param name="resources">The resources used to look up named colour spaces; can be null.</param>
		/// <returns>The colour space, or null.</returns>
		public static ColorSpace TryCreate(object definition, PdfDictionary resources)
		{
			return Create(definition, resources, 0);
		}

		/// <summary>
		/// Creates the colour space described by <paramref name="definition"/>, guarding against
		/// circular or excessively deep definitions (named spaces, base and alternate spaces).
		/// </summary>
		/// <param name="definition">A colour space name or array, possibly a reference.</param>
		/// <param name="resources">The resources used to look up named colour spaces; can be null.</param>
		/// <param name="depth">The current nesting depth; definitions deeper than 8 levels are rejected.</param>
		/// <returns>The colour space, or null when the definition is invalid or unsupported.</returns>
		private static ColorSpace Create(object definition, PdfDictionary resources, int depth)
		{
			if (depth > 8)
				return null;

			var file = resources?.File;
			definition = PdfFile.Resolve(file, definition);

			if (definition is PdfName name)
			{
				switch (name.Value)
				{
					case "DeviceGray":
					case "G":
					case "CalGray":
						return DeviceGray;

					case "DeviceRGB":
					case "RGB":
					case "CalRGB":
						return DeviceRgb;

					case "DeviceCMYK":
					case "CMYK":
						return DeviceCmyk;

					case "Pattern":
						return new PatternColorSpace(null);

					default:
						// a named colour space in the resources.
						var named = resources?.GetDictionary(PdfNames.ColorSpace)?.Resolve(name);
						return named != null ? Create(named, resources, depth + 1) : null;
				}
			}

			if (!(definition is PdfArray array) || array.Count == 0)
				return null;

			var family = array.GetName(0)?.Value;
			switch (family)
			{
				case "DeviceGray":
				case "G":
				case "CalGray":
					return DeviceGray;

				case "DeviceRGB":
				case "RGB":
				case "CalRGB":
					return DeviceRgb;

				case "DeviceCMYK":
				case "CMYK":
					return DeviceCmyk;

				case "Lab":
					return new LabColorSpace(array.GetDictionary(1));

				case "ICCBased":
					return CreateIccBased(array.GetStream(1), resources, depth);

				case "Indexed":
				case "I":
					return IndexedColorSpace.Create(array, Create(array.Resolve(1), resources, depth + 1));

				case "Separation":
					return SeparationColorSpace.Create(array, Create(array.Resolve(2), resources, depth + 1));

				case "DeviceN":
					return DeviceNColorSpace.Create(array, Create(array.Resolve(2), resources, depth + 1));

				case "Pattern":
					return new PatternColorSpace(array.Count > 1 ? Create(array.Resolve(1), resources, depth + 1) : null);

				default:
					return null;
			}
		}

		/// <summary>
		/// Creates an ICCBased colour space (ISO 32000-2, 8.6.5.5) from its profile stream.
		/// </summary>
		/// <param name="stream">The ICC profile stream; null yields DeviceRGB.</param>
		/// <param name="resources">The resources used to resolve the <c>/Alternate</c> space; can be null.</param>
		/// <param name="depth">The current nesting depth.</param>
		/// <returns>The colour space. Colours are converted through the alternate space, or through
		/// the device space matching <c>/N</c> when the alternate is missing or inconsistent.</returns>
		private static ColorSpace CreateIccBased(PdfStream stream, PdfDictionary resources, int depth)
		{
			if (stream == null)
				return DeviceRgb;

			var n = stream.GetInt(PdfNames.N, 0);
			var alternate = stream.Resolve("Alternate");
			var alternateSpace = alternate != null ? Create(alternate, resources, depth + 1) : null;

			// /N is required, but some files omit it: infer it from the alternate.
			if (n == 0)
				n = alternateSpace?.ComponentCount ?? 3;

			if (alternateSpace == null || alternateSpace.ComponentCount != n)
				alternateSpace = n == 1 ? DeviceGray : n == 4 ? DeviceCmyk : DeviceRgb;

			byte[] profile = null;
			try
			{
				profile = stream.GetBytes();
			}
			catch (Exception)
			{
				// the profile is optional for our purposes: the alternate is used.
			}

			return new IccBasedColorSpace(n, alternateSpace, profile);
		}

		#endregion

		#region Device spaces

		/// <summary>
		/// DeviceGray (and CalGray, treated as device gray).
		/// </summary>
		private sealed class GrayColorSpace : ColorSpace
		{
			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.Gray;

			/// <inheritdoc/>
			public override int ComponentCount => 1;

			/// <inheritdoc/>
			public override int ToRgb(double[] components)
			{
				var g = ToByte(Get(components, 0));
				return (g << 16) | (g << 8) | g;
			}

			/// <inheritdoc/>
			public override void ToRgbRow(float[] samples, int pixelCount, byte[] rgb)
			{
				for (int p = 0; p < pixelCount; p++)
				{
					var g = (byte)ToByte(samples[p]);
					rgb[p * 3] = g;
					rgb[p * 3 + 1] = g;
					rgb[p * 3 + 2] = g;
				}
			}
		}

		/// <summary>
		/// DeviceRGB (and CalRGB, treated as device RGB).
		/// </summary>
		private sealed class RgbColorSpace : ColorSpace
		{
			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.Rgb;

			/// <inheritdoc/>
			public override int ComponentCount => 3;

			/// <inheritdoc/>
			public override int ToRgb(double[] components) => Pack(Get(components, 0), Get(components, 1), Get(components, 2));

			/// <inheritdoc/>
			public override void ToRgbRow(float[] samples, int pixelCount, byte[] rgb)
			{
				var count = pixelCount * 3;
				for (int i = 0; i < count; i++)
					rgb[i] = (byte)ToByte(samples[i]);
			}
		}

		/// <summary>
		/// DeviceCMYK.
		/// </summary>
		private sealed class CmykColorSpace : ColorSpace
		{
			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.Cmyk;

			/// <inheritdoc/>
			public override int ComponentCount => 4;

			/// <inheritdoc/>
			public override double[] GetInitialColor() => new double[] { 0, 0, 0, 1 };

			/// <inheritdoc/>
			public override int ToRgb(double[] components) => CmykToRgb(Get(components, 0), Get(components, 1), Get(components, 2), Get(components, 3));
		}

		#endregion

		#region CIE-based spaces

		/// <summary>
		/// CIE L*a*b* converted to sRGB through XYZ with Bradford chromatic adaptation to D65.
		/// </summary>
		private sealed class LabColorSpace : ColorSpace
		{
			// the /WhitePoint in CIE XYZ (defaults to D65).
			private readonly double _xw, _yw, _zw;

			// the /Range of the a* and b* components (defaults to -100..100).
			private readonly double _aMin, _aMax, _bMin, _bMax;

			/// <summary>
			/// Initializes a Lab colour space from its parameter dictionary.
			/// </summary>
			/// <param name="dictionary">The dictionary with <c>/WhitePoint</c> and <c>/Range</c>; can be null.</param>
			public LabColorSpace(PdfDictionary dictionary)
			{
				var white = dictionary?.GetArray("WhitePoint")?.ToNumbers();
				_xw = white != null && white.Length > 0 ? white[0] : 0.9505;
				_yw = white != null && white.Length > 1 ? white[1] : 1.0;
				_zw = white != null && white.Length > 2 ? white[2] : 1.089;

				var range = dictionary?.GetArray(PdfNames.Range)?.ToNumbers();
				_aMin = range != null && range.Length > 0 ? range[0] : -100;
				_aMax = range != null && range.Length > 1 ? range[1] : 100;
				_bMin = range != null && range.Length > 2 ? range[2] : -100;
				_bMax = range != null && range.Length > 3 ? range[3] : 100;
			}

			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.Lab;

			/// <inheritdoc/>
			public override int ComponentCount => 3;

			/// <inheritdoc/>
			public override double[] GetDefaultDecode(int bitsPerComponent) => new[] { 0, 100, _aMin, _aMax, _bMin, _bMax };

			/// <inheritdoc/>
			public override double[] GetInitialColor() => new double[] { 0, Math.Max(0, _aMin), Math.Max(0, _bMin) };

			/// <inheritdoc/>
			public override int ToRgb(double[] components)
			{
				var l = Math.Max(0, Math.Min(100, Get(components, 0)));
				var a = Math.Max(_aMin, Math.Min(_aMax, Get(components, 1)));
				var b = Math.Max(_bMin, Math.Min(_bMax, Get(components, 2)));

				// L*a*b* -> XYZ relative to the declared white point.
				var fy = (l + 16) / 116;
				var fx = fy + a / 500;
				var fz = fy - b / 200;
				var x = _xw * InverseF(fx);
				var y = _yw * InverseF(fy);
				var z = _zw * InverseF(fz);

				// adapt from the source white to D65 with the Bradford transform when the white point is not D65.
				if (Math.Abs(_xw - 0.9505) > 0.01 || Math.Abs(_zw - 1.089) > 0.01)
					Adapt(ref x, ref y, ref z);

				// XYZ (D65) -> linear sRGB.
				var rl = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
				var gl = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
				var bl = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;

				return Pack(Gamma(rl), Gamma(gl), Gamma(bl));
			}

			/// <summary>
			/// Converts a row of L*a*b* samples, caching repeated colours quantized to half units.
			/// </summary>
			/// <param name="samples">Three values per pixel in the L*a*b* ranges.</param>
			/// <param name="pixelCount">The number of pixels.</param>
			/// <param name="rgb">Receives 3 bytes per pixel.</param>
			public override void ToRgbRow(float[] samples, int pixelCount, byte[] rgb)
			{
				// the base implementation quantizes to 0..1, which would merge distinct L*a*b* values.
				var cache = new Dictionary<long, int>();
				var components = new double[3];
				for (int p = 0; p < pixelCount; p++)
				{
					var l = (long)Math.Round(samples[p * 3] * 2);
					var a = (long)Math.Round(samples[p * 3 + 1] * 2) + 1024;
					var b = (long)Math.Round(samples[p * 3 + 2] * 2) + 1024;
					var key = (l << 32) | (a << 16) | b;

					if (!cache.TryGetValue(key, out var color))
					{
						components[0] = samples[p * 3];
						components[1] = samples[p * 3 + 1];
						components[2] = samples[p * 3 + 2];
						color = ToRgb(components);
						if (cache.Count < 65536)
							cache[key] = color;
					}

					rgb[p * 3] = (byte)(color >> 16);
					rgb[p * 3 + 1] = (byte)(color >> 8);
					rgb[p * 3 + 2] = (byte)color;
				}
			}

			/// <summary>
			/// The inverse of the CIE L*a*b* companding function f(t).
			/// </summary>
			/// <param name="t">The companded value.</param>
			/// <returns>The linear ratio to the white point component.</returns>
			private static double InverseF(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);

			/// <summary>
			/// Applies the sRGB transfer curve to a linear component.
			/// </summary>
			/// <param name="v">The linear component, nominally 0..1.</param>
			/// <returns>The gamma-encoded component; negative input yields 0.</returns>
			private static double Gamma(double v)
			{
				if (v <= 0)
					return 0;

				return v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
			}

			/// <summary>
			/// Adapts an XYZ colour from the declared white point to D65 with the Bradford transform.
			/// </summary>
			/// <param name="x">The X component, adapted in place.</param>
			/// <param name="y">The Y component, adapted in place.</param>
			/// <param name="z">The Z component, adapted in place.</param>
			private void Adapt(ref double x, ref double y, ref double z)
			{
				// Bradford cone response domain.
				double Rs(double X, double Y, double Z) => 0.8951 * X + 0.2664 * Y - 0.1614 * Z;
				double Gs(double X, double Y, double Z) => -0.7502 * X + 1.7135 * Y + 0.0367 * Z;
				double Bs(double X, double Y, double Z) => 0.0389 * X - 0.0685 * Y + 1.0296 * Z;

				var rsw = Rs(_xw, _yw, _zw);
				var gsw = Gs(_xw, _yw, _zw);
				var bsw = Bs(_xw, _yw, _zw);
				var rdw = Rs(0.9505, 1.0, 1.089);
				var gdw = Gs(0.9505, 1.0, 1.089);
				var bdw = Bs(0.9505, 1.0, 1.089);

				var r = Rs(x, y, z) * rdw / rsw;
				var g = Gs(x, y, z) * gdw / gsw;
				var b = Bs(x, y, z) * bdw / bsw;

				// inverse Bradford.
				x = 0.9869929 * r - 0.1470543 * g + 0.1599627 * b;
				y = 0.4323053 * r + 0.5183603 * g + 0.0492912 * b;
				z = -0.0085287 * r + 0.0400428 * g + 0.9684867 * b;
			}
		}

		/// <summary>
		/// ICC-based colour space. Colours are converted through the alternate device space.
		/// </summary>
		private sealed class IccBasedColorSpace : ColorSpace
		{
			/// <summary>The number of components (<c>/N</c>).</summary>
			private readonly int _n;

			/// <summary>The device space used to convert colours.</summary>
			private readonly ColorSpace _alternate;

			/// <summary>The raw ICC profile bytes, or null when unreadable.</summary>
			private readonly byte[] _profile;

			/// <summary>
			/// Initializes an ICC-based colour space.
			/// </summary>
			/// <param name="n">The number of components.</param>
			/// <param name="alternate">The space used to convert colours; must have <paramref name="n"/> components.</param>
			/// <param name="profile">The ICC profile bytes; can be null.</param>
			public IccBasedColorSpace(int n, ColorSpace alternate, byte[] profile)
			{
				_n = n;
				_alternate = alternate;
				_profile = profile;
			}

			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.IccBased;

			/// <inheritdoc/>
			public override int ComponentCount => _n;

			/// <inheritdoc/>
			public override byte[] IccProfile => _profile;

			/// <summary>
			/// The alternate space used for conversion.
			/// </summary>
			public ColorSpace Alternate => _alternate;

			/// <inheritdoc/>
			public override double[] GetInitialColor() => _alternate.GetInitialColor();

			/// <inheritdoc/>
			public override int ToRgb(double[] components) => _alternate.ToRgb(components);

			/// <inheritdoc/>
			public override void ToRgbRow(float[] samples, int pixelCount, byte[] rgb) => _alternate.ToRgbRow(samples, pixelCount, rgb);
		}

		#endregion

		#region Special spaces

		/// <summary>
		/// Indexed colour space: each sample is an index in a palette of base-space colours.
		/// </summary>
		internal sealed class IndexedColorSpace : ColorSpace
		{
			/// <summary>The base colour space of the palette entries.</summary>
			private ColorSpace _base;

			/// <summary>The highest valid index, 0..255.</summary>
			private int _hival;

			/// <summary>The palette converted to packed 0xRRGGBB, <c>_hival + 1</c> entries.</summary>
			private int[] _palette;

			/// <summary>
			/// Creates an Indexed colour space from its definition array.
			/// </summary>
			/// <param name="array">The definition <c>[/Indexed base hival lookup]</c>.</param>
			/// <param name="baseSpace">The base colour space.</param>
			/// <returns>The colour space, or null when invalid.</returns>
			public static IndexedColorSpace Create(PdfArray array, ColorSpace baseSpace)
			{
				if (baseSpace == null || array.Count < 4)
					return null;

				var hival = Math.Max(0, Math.Min(255, array.GetInt(2)));
				var lookupObject = array.Resolve(3);
				byte[] lookup;
				if (lookupObject is PdfStream stream)
					lookup = stream.GetBytes();
				else if (lookupObject is PdfString s)
					lookup = s.Bytes;
				else
					return null;

				// precompute the palette in RGB.
				var n = baseSpace.ComponentCount;
				var palette = new int[hival + 1];
				var components = new double[n];
				var decode = baseSpace.GetDefaultDecode(8);
				for (int i = 0; i <= hival; i++)
				{
					for (int c = 0; c < n; c++)
					{
						var index = i * n + c;
						var value = index < lookup.Length ? lookup[index] / 255.0 : 0;
						components[c] = decode[c * 2] + value * (decode[c * 2 + 1] - decode[c * 2]);
					}

					palette[i] = baseSpace.ToRgb(components);
				}

				return new IndexedColorSpace { _base = baseSpace, _hival = hival, _palette = palette };
			}

			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.Indexed;

			/// <inheritdoc/>
			public override int ComponentCount => 1;

			/// <summary>
			/// The base colour space.
			/// </summary>
			public ColorSpace Base => _base;

			/// <summary>
			/// The highest valid index.
			/// </summary>
			public int HiVal => _hival;

			/// <summary>
			/// The palette converted to packed RGB.
			/// </summary>
			public int[] Palette => _palette;

			/// <inheritdoc/>
			public override double[] GetDefaultDecode(int bitsPerComponent) => new double[] { 0, (1 << bitsPerComponent) - 1 };

			/// <inheritdoc/>
			public override int ToRgb(double[] components)
			{
				var index = (int)Math.Round(Get(components, 0));
				index = index < 0 ? 0 : index > _hival ? _hival : index;
				return _palette[index];
			}

			/// <inheritdoc/>
			public override void ToRgbRow(float[] samples, int pixelCount, byte[] rgb)
			{
				for (int p = 0; p < pixelCount; p++)
				{
					var index = (int)(samples[p] + 0.5f);
					index = index < 0 ? 0 : index > _hival ? _hival : index;
					var color = _palette[index];
					rgb[p * 3] = (byte)(color >> 16);
					rgb[p * 3 + 1] = (byte)(color >> 8);
					rgb[p * 3 + 2] = (byte)color;
				}
			}
		}

		/// <summary>
		/// Separation colour space: a single colorant converted through a tint transform.
		/// </summary>
		private sealed class SeparationColorSpace : ColorSpace
		{
			/// <summary>The alternate space that receives the tint transform output.</summary>
			private ColorSpace _alternate;

			/// <summary>The tint transform, or null when missing or invalid.</summary>
			private PdfFunction _tint;

			/// <summary>The colorant name (e.g. <c>All</c>, <c>None</c> or a spot colour).</summary>
			private string _colorant;

			/// <summary>The packed RGB colour of 256 evenly spaced tint levels.</summary>
			private int[] _lut;

			/// <summary>
			/// Creates a Separation colour space from its definition array.
			/// </summary>
			/// <param name="array">The definition <c>[/Separation name alternateSpace tintTransform]</c>.</param>
			/// <param name="alternate">The alternate colour space; DeviceGray when null.</param>
			/// <returns>The colour space, or null when the array is too short.</returns>
			public static SeparationColorSpace Create(PdfArray array, ColorSpace alternate)
			{
				if (array.Count < 4)
					return null;

				var space = new SeparationColorSpace
				{
					_colorant = array.GetName(1)?.Value ?? "None",
					_alternate = alternate ?? DeviceGray,
					_tint = PdfFunction.Create(array.Resolve(3)),
				};

				// precompute 256 tint levels: separations are almost always used with 8-bit or smaller samples.
				var lut = new int[256];
				for (int i = 0; i < 256; i++)
					lut[i] = space.Convert(i / 255.0);

				space._lut = lut;
				return space;
			}

			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.Separation;

			/// <inheritdoc/>
			public override int ComponentCount => 1;

			/// <inheritdoc/>
			public override double[] GetInitialColor() => new double[] { 1 };

			/// <inheritdoc/>
			public override int ToRgb(double[] components) => _lut[ToByte(Get(components, 0))];

			/// <inheritdoc/>
			public override void ToRgbRow(float[] samples, int pixelCount, byte[] rgb)
			{
				for (int p = 0; p < pixelCount; p++)
				{
					var color = _lut[ToByte(samples[p])];
					rgb[p * 3] = (byte)(color >> 16);
					rgb[p * 3 + 1] = (byte)(color >> 8);
					rgb[p * 3 + 2] = (byte)color;
				}
			}

			/// <summary>
			/// Converts a tint to packed sRGB through the tint transform.
			/// </summary>
			/// <param name="tint">The tint, 0..1.</param>
			/// <returns>The packed colour.</returns>
			private int Convert(double tint)
			{
				// "All" marks every separation: rendered as gray. "None" never marks the page.
				if (_colorant == "None")
					return 0xFFFFFF;

				if (_colorant == "All" || _tint == null)
				{
					var g = ToByte(1 - tint);
					return (g << 16) | (g << 8) | g;
				}

				var output = new double[Math.Max(_alternate.ComponentCount, _tint.OutputCount)];
				_tint.Evaluate(new[] { tint }, output);
				return _alternate.ToRgb(output);
			}
		}

		/// <summary>
		/// DeviceN colour space: several colorants converted through a tint transform.
		/// </summary>
		private sealed class DeviceNColorSpace : ColorSpace
		{
			/// <summary>The alternate space that receives the tint transform output.</summary>
			private ColorSpace _alternate;

			/// <summary>The tint transform, or null when missing or invalid.</summary>
			private PdfFunction _tint;

			/// <summary>The number of colorants.</summary>
			private int _n;

			/// <summary>
			/// Creates a DeviceN colour space from its definition array.
			/// </summary>
			/// <param name="array">The definition <c>[/DeviceN names alternateSpace tintTransform attributes]</c>.</param>
			/// <param name="alternate">The alternate colour space; DeviceCMYK when null.</param>
			/// <returns>The colour space, or null when the array is invalid.</returns>
			public static DeviceNColorSpace Create(PdfArray array, ColorSpace alternate)
			{
				var names = array.GetArray(1);
				if (names == null || names.Count == 0 || array.Count < 4)
					return null;

				return new DeviceNColorSpace
				{
					_n = names.Count,
					_alternate = alternate ?? DeviceCmyk,
					_tint = PdfFunction.Create(array.Resolve(3)),
				};
			}

			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.DeviceN;

			/// <inheritdoc/>
			public override int ComponentCount => _n;

			/// <inheritdoc/>
			public override double[] GetInitialColor()
			{
				var initial = new double[_n];
				for (int i = 0; i < _n; i++)
					initial[i] = 1;

				return initial;
			}

			/// <inheritdoc/>
			public override int ToRgb(double[] components)
			{
				if (_tint == null)
				{
					// no usable transform: approximate with the average tint as gray.
					double sum = 0;
					for (int i = 0; i < _n; i++)
						sum += Get(components, i);

					var g = ToByte(1 - sum / _n);
					return (g << 16) | (g << 8) | g;
				}

				var output = new double[Math.Max(_alternate.ComponentCount, _tint.OutputCount)];
				_tint.Evaluate(components, output);
				return _alternate.ToRgb(output);
			}
		}

		/// <summary>
		/// Pattern colour space. The colour of a pattern can't be expressed as a single value:
		/// uncoloured patterns use the underlying space, coloured ones are reported as mid gray.
		/// </summary>
		private sealed class PatternColorSpace : ColorSpace
		{
			/// <summary>The underlying space of uncoloured patterns, or null for coloured patterns.</summary>
			private readonly ColorSpace _underlying;

			/// <summary>
			/// Initializes a Pattern colour space.
			/// </summary>
			/// <param name="underlying">The underlying colour space for uncoloured (PaintType 2) patterns; can be null.</param>
			public PatternColorSpace(ColorSpace underlying)
			{
				_underlying = underlying;
			}

			/// <inheritdoc/>
			public override ColorSpaceKind Kind => ColorSpaceKind.Pattern;

			/// <inheritdoc/>
			public override int ComponentCount => _underlying?.ComponentCount ?? 0;

			/// <inheritdoc/>
			public override int ToRgb(double[] components)
			{
				return _underlying != null ? _underlying.ToRgb(components) : 0x808080;
			}
		}

		#endregion
	}
}
