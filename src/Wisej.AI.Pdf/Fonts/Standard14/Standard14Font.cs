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

// Metrics of one of the 14 standard Type 1 fonts (ISO 32000-2, 9.6.2.2), from Adobe's Core14 AFM files.

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// The metrics of one of the 14 standard fonts: font-wide values, glyph widths and, for Symbol
	/// and ZapfDingbats, the built-in encoding.
	/// </summary>
	/// <remarks>
	/// Instances are shared and must be treated as read-only. Values are in glyph space units (1/1000 em).
	/// </remarks>
	internal sealed class Standard14Font
	{
		#region Fields

		// glyph name -> advance width.
		private readonly Dictionary<string, double> _widths;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes the font with its glyph widths.
		/// </summary>
		/// <param name="widths">The advance width of each glyph name.</param>
		internal Standard14Font(Dictionary<string, double> widths)
		{
			_widths = widths ?? new Dictionary<string, double>(StringComparer.Ordinal);
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the PostScript name of the font, for example <c>Helvetica-Bold</c>.
		/// </summary>
		public string Name { get; internal set; }

		/// <summary>
		/// Returns the family name, for example <c>Helvetica</c>.
		/// </summary>
		public string FamilyName { get; internal set; }

		/// <summary>
		/// Returns true for the bold fonts.
		/// </summary>
		public bool IsBold { get; internal set; }

		/// <summary>
		/// Returns true for the italic and oblique fonts.
		/// </summary>
		public bool IsItalic { get; internal set; }

		/// <summary>
		/// Returns true for the Courier fonts.
		/// </summary>
		public bool IsFixedPitch { get; internal set; }

		/// <summary>
		/// Returns true for the Times fonts.
		/// </summary>
		public bool IsSerif { get; internal set; }

		/// <summary>
		/// Returns true for Symbol and ZapfDingbats, whose glyphs are outside the standard Latin character set.
		/// </summary>
		public bool IsSymbolic { get; internal set; }

		/// <summary>
		/// Returns the ascender height. For Symbol and ZapfDingbats, whose AFM files have no ascender, it is the top of the bounding box.
		/// </summary>
		public double Ascent { get; internal set; }

		/// <summary>
		/// Returns the descender depth (negative). For Symbol and ZapfDingbats it is the bottom of the bounding box.
		/// </summary>
		public double Descent { get; internal set; }

		/// <summary>
		/// Returns the height of the capital letters. For Symbol and ZapfDingbats it is derived from the glyph bounding boxes.
		/// </summary>
		public double CapHeight { get; internal set; }

		/// <summary>
		/// Returns the height of the lowercase letters. For Symbol and ZapfDingbats it is derived from the glyph bounding boxes.
		/// </summary>
		public double XHeight { get; internal set; }

		/// <summary>
		/// Returns the italic angle in degrees, counter-clockwise from vertical (negative for right-leaning fonts).
		/// </summary>
		public double ItalicAngle { get; internal set; }

		/// <summary>
		/// Returns the font bounding box: llx, lly, urx, ury.
		/// </summary>
		public double[] FontBBox { get; internal set; }

		/// <summary>
		/// Returns the built-in encoding (code to glyph name, 256 entries, null for unused codes) of
		/// Symbol and ZapfDingbats; null for the other fonts, which use StandardEncoding.
		/// </summary>
		public string[] BuiltInEncoding { get; internal set; }

		/// <summary>
		/// Returns the width of the space glyph, or 250 when the font has no space.
		/// </summary>
		public double SpaceWidth => _widths.TryGetValue("space", out var width) ? width : 250;

		/// <summary>
		/// Returns the names of the glyphs in the font.
		/// </summary>
		public ICollection<string> GlyphNames => _widths.Keys;

		#endregion

		#region Methods

		/// <summary>
		/// Returns the advance width of a glyph.
		/// </summary>
		/// <param name="glyphName">The glyph name, for example <c>A</c> or <c>eacute</c>.</param>
		/// <returns>The width in 1/1000 em, or -1 when the font has no such glyph.</returns>
		public double GetWidth(string glyphName)
		{
			if (glyphName != null && _widths.TryGetValue(glyphName, out var width))
				return width;

			return -1;
		}

		/// <summary>
		/// Returns true when the font has a glyph with the given name.
		/// </summary>
		/// <param name="glyphName">The glyph name.</param>
		/// <returns>True when the glyph exists.</returns>
		public bool HasGlyph(string glyphName)
		{
			return glyphName != null && _widths.ContainsKey(glyphName);
		}

		/// <summary>
		/// Returns the font name.
		/// </summary>
		/// <returns>The name.</returns>
		public override string ToString()
		{
			return this.Name;
		}

		#endregion
	}
}
