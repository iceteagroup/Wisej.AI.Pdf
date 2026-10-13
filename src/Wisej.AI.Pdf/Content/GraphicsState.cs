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

// The graphics state (ISO 32000-2, 8.4), including the text state parameters
// (9.3) that are saved and restored with it.

using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Content
{
	/// <summary>
	/// The subset of the PDF graphics state that matters for content extraction.
	/// </summary>
	internal sealed class GraphicsState
	{
		#region Graphics

		/// <summary>
		/// The current transformation matrix, mapping user space directly to display space.
		/// </summary>
		public Matrix Ctm = Matrix.Identity;

		/// <summary>
		/// The bounding box of the current clipping path in display space.
		/// </summary>
		public PdfRect Clip;

		/// <summary>
		/// The line width in user space units.
		/// </summary>
		public double LineWidth = 1;

		/// <summary>
		/// The colour space used for filling.
		/// </summary>
		public ColorSpace FillSpace = ColorSpace.DeviceGray;

		/// <summary>
		/// The colour space used for stroking.
		/// </summary>
		public ColorSpace StrokeSpace = ColorSpace.DeviceGray;

		/// <summary>
		/// The fill colour as 0xRRGGBB.
		/// </summary>
		public int FillColor;

		/// <summary>
		/// The stroke colour as 0xRRGGBB.
		/// </summary>
		public int StrokeColor;

		/// <summary>
		/// True when the fill colour is a pattern.
		/// </summary>
		public bool FillIsPattern;

		/// <summary>
		/// True when the stroke colour is a pattern.
		/// </summary>
		public bool StrokeIsPattern;

		/// <summary>
		/// The pattern dictionary when the fill colour is a pattern, or null.
		/// </summary>
		public PdfDictionary FillPattern;

		/// <summary>
		/// The constant opacity for filling (ca).
		/// </summary>
		public double FillAlpha = 1;

		/// <summary>
		/// The constant opacity for stroking (CA).
		/// </summary>
		public double StrokeAlpha = 1;

		/// <summary>
		/// The soft mask dictionary (ExtGState /SMask), or null.
		/// </summary>
		public PdfDictionary SoftMask;

		#endregion

		#region Text state

		/// <summary>
		/// The character spacing (Tc) in unscaled text space units.
		/// </summary>
		public double CharSpacing;

		/// <summary>
		/// The word spacing (Tw) in unscaled text space units.
		/// </summary>
		public double WordSpacing;

		/// <summary>
		/// The horizontal scaling (Tz) as a fraction: 1 is 100%.
		/// </summary>
		public double HorizontalScaling = 1;

		/// <summary>
		/// The leading (TL) in unscaled text space units.
		/// </summary>
		public double Leading;

		/// <summary>
		/// The current font, or null before the first Tf.
		/// </summary>
		public PdfFont Font;

		/// <summary>
		/// The font size (Tf).
		/// </summary>
		public double FontSize;

		/// <summary>
		/// The text rendering mode (Tr).
		/// </summary>
		public int RenderMode;

		/// <summary>
		/// The text rise (Ts) in unscaled text space units.
		/// </summary>
		public double Rise;

		#endregion

		/// <summary>
		/// Returns a copy of this state, for the <c>q</c> operator.
		/// </summary>
		/// <returns>The copy.</returns>
		public GraphicsState Clone() => (GraphicsState)MemberwiseClone();
	}
}
