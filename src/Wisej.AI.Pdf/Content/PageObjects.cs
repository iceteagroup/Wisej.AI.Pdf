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

// The positioned objects produced by interpreting a page: glyphs, vector
// paths and image placements, in display coordinates.

using System.Collections.Generic;
using Wisej.AI.Pdf.Fonts;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Content
{
	/// <summary>
	/// Properties shared by all the objects painted on a page.
	/// </summary>
	internal abstract class PageObject
	{
		/// <summary>
		/// The position of the object in the painting order of the page.
		/// </summary>
		public int Index;

		/// <summary>
		/// The axis-aligned bounds in display space (top-left origin, points).
		/// </summary>
		public PdfRect Bounds;

		/// <summary>
		/// The marked-content identifier that links the object to the structure tree, or -1.
		/// </summary>
		public int Mcid = -1;

		/// <summary>
		/// The structure parent key of the content stream that painted the object (page, form or annotation), or -1.
		/// </summary>
		public int StructParent = -1;

		/// <summary>
		/// True when the object is marked as an artifact (headers, footers, decorations) by the producer.
		/// </summary>
		public bool IsArtifact;

		/// <summary>
		/// True when the object belongs to an annotation appearance (for example a filled form field).
		/// </summary>
		public bool IsAnnotation;

		/// <summary>
		/// True when the object is clipped away or outside the page and therefore not visible.
		/// </summary>
		public bool IsClipped;
	}

	/// <summary>
	/// A glyph painted by a text-showing operator.
	/// </summary>
	internal sealed class TextGlyph : PageObject
	{
		/// <summary>
		/// The Unicode text of the glyph (usually one character, more for ligatures). Never null; empty when unmapped.
		/// </summary>
		public string Text;

		/// <summary>
		/// True when the font could not map the glyph to Unicode.
		/// </summary>
		public bool IsUnmapped;

		/// <summary>
		/// The character code.
		/// </summary>
		public int Code;

		/// <summary>
		/// The font.
		/// </summary>
		public PdfFont Font;

		/// <summary>
		/// The horizontal coordinate of the glyph origin on the baseline.
		/// </summary>
		public double X;

		/// <summary>
		/// The vertical coordinate of the glyph origin on the baseline.
		/// </summary>
		public double Y;

		/// <summary>
		/// The horizontal coordinate of the glyph origin advanced by the glyph width.
		/// </summary>
		public double EndX;

		/// <summary>
		/// The vertical coordinate of the glyph origin advanced by the glyph width.
		/// </summary>
		public double EndY;

		/// <summary>
		/// The effective font size in points: the height of the em square on the page.
		/// </summary>
		public double FontSize;

		/// <summary>
		/// The direction of the baseline in degrees, clockwise from the positive X axis of the display (0 = left to right).
		/// </summary>
		public double Angle;

		/// <summary>
		/// The width of a space in this font and size, in points; used to detect word breaks.
		/// </summary>
		public double SpaceWidth;

		/// <summary>
		/// The fill colour as 0xRRGGBB.
		/// </summary>
		public int Color;

		/// <summary>
		/// The text rendering mode (0 fill, 1 stroke, 2 fill and stroke, 3 invisible, 4-7 with clipping).
		/// </summary>
		public int RenderMode;

		/// <summary>
		/// True when the glyph is drawn as bold: bold font, or fill-and-stroke used to simulate bold.
		/// </summary>
		public bool IsBold;

		/// <summary>
		/// True when the glyph is italic: italic font, or a skewed text matrix used to simulate italic.
		/// </summary>
		public bool IsItalic;

		/// <summary>
		/// The baseline shift applied by the text rise (Ts), in points; positive for superscripts.
		/// </summary>
		public double Rise;

		/// <summary>
		/// The replacement text declared with /ActualText for the marked-content sequence that contains the glyph, or null.
		/// </summary>
		public string ActualText;

		/// <summary>
		/// Identifies the /ActualText sequence so it's emitted once, or 0.
		/// </summary>
		public int ActualTextId;

		/// <summary>
		/// True when the glyph is not painted (invisible rendering mode or fully transparent).
		/// </summary>
		public bool IsInvisible => this.RenderMode == 3 || this.RenderMode == 7;

		/// <inheritdoc/>
		public override string ToString() => $"'{this.Text}' at ({this.X:0.#}, {this.Y:0.#}) {this.FontSize:0.#}pt {this.Font?.BaseFont}";
	}

	/// <summary>
	/// A path painted by a path-painting operator.
	/// </summary>
	internal sealed class PathShape : PageObject
	{
		/// <summary>
		/// True when the path is stroked.
		/// </summary>
		public bool IsStroked;

		/// <summary>
		/// True when the path is filled.
		/// </summary>
		public bool IsFilled;

		/// <summary>
		/// The stroke width in points.
		/// </summary>
		public double LineWidth;

		/// <summary>
		/// The stroke colour as 0xRRGGBB.
		/// </summary>
		public int StrokeColor;

		/// <summary>
		/// The fill colour as 0xRRGGBB.
		/// </summary>
		public int FillColor;

		/// <summary>
		/// True when the fill colour comes from a pattern or shading rather than a solid colour.
		/// </summary>
		public bool IsPatternFill;

		/// <summary>
		/// The straight segments of the path in display space, as (x0, y0, x1, y1) quadruples. Curves are not included.
		/// </summary>
		public List<double> Segments = new List<double>();

		/// <summary>
		/// The axis-aligned rectangles of the path (from <c>re</c> operators or closed 4-point subpaths).
		/// </summary>
		public List<PdfRect> Rectangles = new List<PdfRect>();

		/// <summary>
		/// True when the path contains curves.
		/// </summary>
		public bool HasCurves;

		/// <summary>
		/// The number of path construction points; used to estimate the visual complexity of figures.
		/// </summary>
		public int PointCount;

		/// <inheritdoc/>
		public override string ToString() => $"path {(this.IsStroked ? "S" : "")}{(this.IsFilled ? "F" : "")} {this.Bounds} segs={this.Segments.Count / 4} rects={this.Rectangles.Count}";
	}

	/// <summary>
	/// The placement of an image on the page.
	/// </summary>
	internal sealed class ImagePlacement : PageObject
	{
		/// <summary>
		/// The image XObject stream, or null for inline images.
		/// </summary>
		public PdfStream Stream;

		/// <summary>
		/// The inline image, or null for image XObjects.
		/// </summary>
		public InlineImage Inline;

		/// <summary>
		/// The resources in effect, used to resolve named colour spaces of inline images.
		/// </summary>
		public PdfDictionary Resources;

		/// <summary>
		/// The transformation from the image unit square to display space.
		/// </summary>
		public Matrix Transform;

		/// <summary>
		/// The visible part of <see cref="PageObject.Bounds"/> after clipping.
		/// </summary>
		public PdfRect VisibleBounds;

		/// <summary>
		/// The fill colour used to paint stencil masks (ImageMask images), as 0xRRGGBB.
		/// </summary>
		public int FillColor;

		/// <summary>
		/// The constant opacity (ca) in effect, 0..1.
		/// </summary>
		public double Alpha = 1;

		/// <summary>
		/// The soft mask dictionary of the graphics state in effect (ExtGState /SMask), or null.
		/// </summary>
		public PdfDictionary SoftMask;

		/// <summary>
		/// The name of the image in the resources (XObject name), or null for inline images.
		/// </summary>
		public string Name;

		/// <summary>
		/// The dictionary of the image: the XObject stream or the inline image dictionary.
		/// </summary>
		public PdfDictionary Dictionary => (PdfDictionary)this.Stream ?? this.Inline?.Dictionary;

		/// <inheritdoc/>
		public override string ToString() => $"image {this.Name} {this.Bounds}";
	}

	/// <summary>
	/// An area painted by a shading operator (<c>sh</c>) or a shading pattern fill.
	/// </summary>
	internal sealed class ShadingArea : PageObject
	{
		/// <inheritdoc/>
		public override string ToString() => $"shading {this.Bounds}";
	}

	/// <summary>
	/// A link annotation on the page.
	/// </summary>
	internal sealed class PageLink
	{
		/// <summary>
		/// The active area in display space.
		/// </summary>
		public PdfRect Bounds;

		/// <summary>
		/// The target URI, or null for internal links.
		/// </summary>
		public string Uri;

		/// <summary>
		/// The target page number for internal links, or 0.
		/// </summary>
		public int PageNumber;
	}

	/// <summary>
	/// All the objects painted on a page.
	/// </summary>
	internal sealed class PageObjects
	{
		/// <summary>
		/// The page.
		/// </summary>
		public PdfPage Page;

		/// <summary>
		/// The glyphs in painting order.
		/// </summary>
		public List<TextGlyph> Glyphs = new List<TextGlyph>();

		/// <summary>
		/// The paths in painting order.
		/// </summary>
		public List<PathShape> Paths = new List<PathShape>();

		/// <summary>
		/// The image placements in painting order.
		/// </summary>
		public List<ImagePlacement> Images = new List<ImagePlacement>();

		/// <summary>
		/// The shaded areas in painting order.
		/// </summary>
		public List<ShadingArea> Shadings = new List<ShadingArea>();

		/// <summary>
		/// The link annotations.
		/// </summary>
		public List<PageLink> Links = new List<PageLink>();
	}
}
