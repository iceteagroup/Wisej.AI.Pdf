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

// Per-document cache of parsed resources (fonts and colour spaces).

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Caches the fonts and colour spaces of a document so they are parsed only once,
	/// even when shared by many pages.
	/// </summary>
	/// <remarks>
	/// Keys are the resource dictionaries themselves (compared by reference): the same indirect
	/// object always resolves to the same instance, because <see cref="PdfFile"/> caches objects.
	/// All the members are thread-safe.
	/// </remarks>
	internal sealed class ResourceCache
	{
		/// <summary>The parsed fonts, keyed by font dictionary; also the lock for its own access.</summary>
		private readonly Dictionary<object, PdfFont> _fonts = new Dictionary<object, PdfFont>(ReferenceEqualityComparer.Instance);

		/// <summary>The parsed colour spaces, keyed by definition array; also the lock for its own access.</summary>
		private readonly Dictionary<object, ColorSpace> _colorSpaces = new Dictionary<object, ColorSpace>(ReferenceEqualityComparer.Instance);

		// font used when a Tf operator names a font that doesn't exist.
		private PdfFont _defaultFont;

		/// <summary>
		/// Returns the font for <paramref name="dictionary"/>, creating it on first use.
		/// </summary>
		/// <param name="dictionary">The font dictionary.</param>
		/// <returns>The font; never null (a default font is returned for invalid dictionaries).</returns>
		public PdfFont GetFont(PdfDictionary dictionary)
		{
			if (dictionary == null)
				return GetDefaultFont();

			lock (_fonts)
			{
				if (_fonts.TryGetValue(dictionary, out var cached))
					return cached;
			}

			PdfFont font;
			try
			{
				font = CreateFont(dictionary);
			}
			catch (Exception)
			{
				font = new SimpleFont(new PdfDictionary());
			}

			lock (_fonts)
			{
				// another thread may have created it meanwhile: keep the first instance.
				if (_fonts.TryGetValue(dictionary, out var cached))
					return cached;

				_fonts[dictionary] = font;
			}

			return font;
		}

		/// <summary>
		/// Returns the colour space for <paramref name="definition"/>, creating it on first use.
		/// </summary>
		/// <param name="definition">The colour space definition (name or array).</param>
		/// <param name="resources">The resources used to resolve named colour spaces.</param>
		/// <returns>The colour space; never null.</returns>
		public ColorSpace GetColorSpace(object definition, PdfDictionary resources)
		{
			definition = PdfFile.Resolve(resources?.File, definition);

			// device names are shared singletons; only arrays are worth caching.
			if (!(definition is PdfArray))
				return ColorSpace.Create(definition, resources);

			lock (_colorSpaces)
			{
				if (_colorSpaces.TryGetValue(definition, out var cached))
					return cached;
			}

			var colorSpace = ColorSpace.Create(definition, resources);

			lock (_colorSpaces)
				_colorSpaces[definition] = colorSpace;

			return colorSpace;
		}

		/// <summary>
		/// Returns the fallback font (Helvetica, WinAnsiEncoding) used when a <c>Tf</c> operator names a font that doesn't exist.
		/// </summary>
		/// <returns>The default font, created on first use.</returns>
		private PdfFont GetDefaultFont()
		{
			if (_defaultFont == null)
			{
				var dictionary = new PdfDictionary
				{
					[PdfNames.Type] = PdfNames.Font,
					[PdfNames.Subtype] = PdfName.Get("Type1"),
					[PdfNames.BaseFont] = PdfName.Get("Helvetica"),
					[PdfNames.Encoding] = PdfName.Get("WinAnsiEncoding"),
				};

				_defaultFont = new SimpleFont(dictionary);
			}

			return _defaultFont;
		}

		/// <summary>
		/// Creates the font object matching the <c>/Subtype</c> of a font dictionary.
		/// </summary>
		/// <param name="dictionary">The font dictionary.</param>
		/// <returns>The font.</returns>
		/// <remarks>Parsing errors propagate; <see cref="GetFont"/> replaces the font with an empty simple font.</remarks>
		private static PdfFont CreateFont(PdfDictionary dictionary)
		{
			switch (dictionary.GetNameValue(PdfNames.Subtype))
			{
				case "Type0":
					return new Type0Font(dictionary);

				case "Type3":
					return new Type3Font(dictionary);

				default:
					// Type1, MMType1, TrueType, and unknown subtypes treated as simple fonts.
					return new SimpleFont(dictionary);
			}
		}
	}
}
