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

// The graphics state of the renderer (ISO 32000-2, 8.4) and the context
// shared by the renderers of a page.

using System.Collections.Generic;
using Wisej.AI.Pdf.Content;
using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Graphics;
using Wisej.AI.Pdf.Imaging;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Rendering
{
	/// <summary>
	/// The graphics state used while rendering.
	/// </summary>
	internal sealed class RenderState
	{
		/// <summary>The current transformation matrix: user space to device pixels.</summary>
		public Matrix Ctm = Matrix.Identity;

		/// <summary>The clipping region.</summary>
		public ClipRegion Clip;

		/// <summary>The line width in user space units.</summary>
		public double LineWidth = 1;

		/// <summary>The line cap style.</summary>
		public int LineCap;

		/// <summary>The line join style.</summary>
		public int LineJoin;

		/// <summary>The miter limit.</summary>
		public double MiterLimit = 10;

		/// <summary>The dash array, or null for solid lines.</summary>
		public double[] Dash;

		/// <summary>The dash phase.</summary>
		public double DashPhase;

		/// <summary>The fill colour space.</summary>
		public ColorSpace FillSpace = ColorSpace.DeviceGray;

		/// <summary>The stroke colour space.</summary>
		public ColorSpace StrokeSpace = ColorSpace.DeviceGray;

		/// <summary>The fill colour as 0xRRGGBB (for uncoloured patterns, the colour of the pattern).</summary>
		public int FillColor;

		/// <summary>The stroke colour as 0xRRGGBB.</summary>
		public int StrokeColor;

		/// <summary>The blend mode.</summary>
		public BlendMode BlendMode;

		/// <summary>The soft mask (SMask in the graphics state) rendered to device pixels, or null.</summary>
		public ClipMask SoftMask;

		/// <summary>The fill pattern dictionary or stream, or null.</summary>
		public PdfDictionary FillPattern;

		/// <summary>The stroke pattern dictionary or stream, or null.</summary>
		public PdfDictionary StrokePattern;

		/// <summary>The constant alpha of fills (ca).</summary>
		public double FillAlpha = 1;

		/// <summary>The constant alpha of strokes (CA).</summary>
		public double StrokeAlpha = 1;

		/// <summary>The character spacing (Tc).</summary>
		public double CharSpacing;

		/// <summary>The word spacing (Tw).</summary>
		public double WordSpacing;

		/// <summary>The horizontal scaling (Tz / 100).</summary>
		public double HorizontalScaling = 1;

		/// <summary>The leading (TL).</summary>
		public double Leading;

		/// <summary>The font (Tf).</summary>
		public PdfFont Font;

		/// <summary>The font size (Tf).</summary>
		public double FontSize;

		/// <summary>The text rendering mode (Tr).</summary>
		public int RenderMode;

		/// <summary>The text rise (Ts).</summary>
		public double Rise;

		/// <summary>
		/// Returns a copy of the state.
		/// </summary>
		/// <returns>The copy.</returns>
		public RenderState Clone() => (RenderState)MemberwiseClone();

		/// <summary>
		/// Returns the stroke parameters.
		/// </summary>
		/// <returns>The stroke style.</returns>
		public StrokeStyle GetStrokeStyle()
		{
			return new StrokeStyle
			{
				Width = this.LineWidth,
				Cap = this.LineCap,
				Join = this.LineJoin,
				MiterLimit = this.MiterLimit,
				Dash = this.Dash,
				DashPhase = this.DashPhase,
			};
		}
	}

	/// <summary>
	/// The state shared by all the renderers of a page: the main one and those of the pattern cells.
	/// </summary>
	internal sealed class RenderContext
	{
		/// <summary>The number of operators that can still be executed.</summary>
		public int OperatorBudget;

		/// <summary>
		/// Initializes a new instance of <see cref="RenderContext"/>.
		/// </summary>
		/// <param name="page">The page being rendered.</param>
		/// <param name="maxOperators">The maximum number of operators executed.</param>
		public RenderContext(PdfPage page, int maxOperators)
		{
			this.Page = page;
			this.Document = page.Document;
			this.Cache = page.Document.ResourceCache;
			this.OptionalContent = OptionalContent.Get(page.Document);
			this.OperatorBudget = maxOperators;
		}

		/// <summary>The page being rendered.</summary>
		public PdfPage Page { get; }

		/// <summary>The document.</summary>
		public PdfDocument Document { get; }

		/// <summary>The resource cache of the document.</summary>
		public ResourceCache Cache { get; }

		/// <summary>The optional content configuration, or null.</summary>
		public OptionalContent OptionalContent { get; }

		/// <summary>The image decoding options.</summary>
		public PdfImageOptions ImageOptions { get; } = new PdfImageOptions();

		/// <summary>The decoded images, by stream and fill colour (stencil masks depend on it).</summary>
		public Dictionary<(PdfStream, int), CachedImage> Images { get; } = new Dictionary<(PdfStream, int), CachedImage>();

		/// <summary>The rendered tiling pattern cells, by pattern, matrix and colour.</summary>
		public Dictionary<(PdfDictionary, Matrix, int), Paint> Tiles { get; } = new Dictionary<(PdfDictionary, Matrix, int), Paint>();

		/// <summary>The rendered soft masks, by mask dictionary, matrix and clipping window.</summary>
		public Dictionary<(PdfDictionary, Matrix, int, int, int, int), ClipMask> SoftMasks { get; } = new Dictionary<(PdfDictionary, Matrix, int, int, int, int), ClipMask>();
	}

	/// <summary>
	/// A decoded image and its last resampled version.
	/// </summary>
	internal sealed class CachedImage
	{
		/// <summary>The decoded image, or null when it can't be decoded.</summary>
		public RasterImage Raster;

		/// <summary>The premultiplied bitmap last used for drawing.</summary>
		public Bitmap Bitmap;
	}
}
