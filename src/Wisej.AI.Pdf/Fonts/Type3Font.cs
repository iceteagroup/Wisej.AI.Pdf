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

// Type 3 fonts: glyphs defined by content streams (ISO 32000-2, 9.6.4).

using System;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// A Type 3 font, whose glyphs are described by PDF content streams.
	/// </summary>
	/// <remarks>
	/// Type 3 fonts are common in documents produced by TeX (bitmap fonts) and by some
	/// reporting tools. Glyph procedures may paint images, which the image extractor can collect.
	/// </remarks>
	internal sealed class Type3Font : SimpleFont
	{
		#region Fields

		// the /FontMatrix: glyph space (the space of the glyph procedures) to text space.
		private readonly Matrix _fontMatrix;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of <see cref="Type3Font"/>.
		/// </summary>
		/// <param name="dictionary">The font dictionary.</param>
		public Type3Font(PdfDictionary dictionary) : base(dictionary)
		{
			var fontMatrix = dictionary.GetArray(PdfNames.FontMatrix)?.ToNumbers();
			_fontMatrix = fontMatrix != null && fontMatrix.Length >= 6 ? Matrix.FromArray(fontMatrix) : new Matrix(0.001, 0, 0, 0.001, 0, 0);

			// a degenerate matrix would make every glyph invisible: use the conventional 1/1000 scale.
			if (Math.Abs(_fontMatrix.Determinant) < 1e-12)
				_fontMatrix = new Matrix(0.001, 0, 0, 0.001, 0, 0);

			this.CharProcs = dictionary.GetDictionary(PdfNames.CharProcs);
			this.Resources = dictionary.GetDictionary(PdfNames.Resources);

			// the font bounding box in glyph space gives the vertical extent.
			var bbox = dictionary.GetArray(PdfNames.FontBBox)?.ToNumbers();
			if (bbox != null && bbox.Length == 4 && bbox[3] > bbox[1])
			{
				var top = _fontMatrix.Transform(0, bbox[3]).Y;
				var bottom = _fontMatrix.Transform(0, bbox[1]).Y;
				if (top > bottom && top - bottom < 3)
				{
					this.Ascent = Math.Max(0.1, top);
					this.Descent = Math.Min(0, bottom);
				}
			}
		}

		#endregion

		#region Properties

		/// <inheritdoc/>
		public override bool IsType3 => true;

		/// <inheritdoc/>
		public override Matrix FontMatrix => _fontMatrix;

		/// <summary>
		/// Returns the glyph procedures (/CharProcs), keyed by glyph name.
		/// </summary>
		public PdfDictionary CharProcs { get; }

		/// <summary>
		/// Returns the resources used by the glyph procedures, or null to use the resources of the page.
		/// </summary>
		public PdfDictionary Resources { get; }

		#endregion

		#region Methods

		/// <summary>
		/// Returns the glyph procedure of <paramref name="code"/>.
		/// </summary>
		/// <param name="code">The character code.</param>
		/// <returns>The content stream, or null.</returns>
		public PdfStream GetCharProc(int code)
		{
			var name = GetGlyphName(code);
			return name != null ? this.CharProcs?.GetStream(PdfName.Get(name)) : null;
		}

		#endregion
	}
}
