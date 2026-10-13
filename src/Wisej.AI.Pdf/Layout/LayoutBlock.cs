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

// Internal layout model: the blocks of a page in reading order.

using System.Collections.Generic;
using Wisej.AI.Pdf.Content;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// The kind of a <see cref="LayoutBlock"/>.
	/// </summary>
	internal enum LayoutBlockKind
	{
		/// <summary>A paragraph of body text.</summary>
		Paragraph,

		/// <summary>A heading.</summary>
		Heading,

		/// <summary>A list item.</summary>
		ListItem,

		/// <summary>A caption of a figure or table.</summary>
		Caption,

		/// <summary>Preformatted (monospaced) text.</summary>
		Code,

		/// <summary>A table.</summary>
		Table,

		/// <summary>An image or a vector figure.</summary>
		Figure,

		/// <summary>A running header repeated across pages.</summary>
		PageHeader,

		/// <summary>A running footer or page number repeated across pages.</summary>
		PageFooter,

		/// <summary>A footnote.</summary>
		Footnote,
	}

	/// <summary>
	/// A block of content on a page.
	/// </summary>
	internal sealed class LayoutBlock
	{
		/// <summary>
		/// The kind of block.
		/// </summary>
		public LayoutBlockKind Kind;

		/// <summary>
		/// The lines of text of the block, top to bottom (empty for tables and figures).
		/// </summary>
		public List<TextLine> Lines = new List<TextLine>();

		/// <summary>
		/// The bounds of the block in the reading frame.
		/// </summary>
		public PdfRect Bounds;

		/// <summary>
		/// The bounds of the block in display space.
		/// </summary>
		public PdfRect DisplayBounds;

		/// <summary>
		/// The orientation of the reading frame.
		/// </summary>
		public int Orientation;

		/// <summary>
		/// The heading level (1-6) for headings.
		/// </summary>
		public int Level;

		/// <summary>
		/// The list marker for list items (for example "•" or "1."), or null.
		/// </summary>
		public string ListMarker;

		/// <summary>
		/// True when the list item is numbered.
		/// </summary>
		public bool IsOrderedList;

		/// <summary>
		/// The nesting depth of a list item, 0 for top-level items.
		/// </summary>
		public int ListDepth;

		/// <summary>
		/// The detected table, for <see cref="LayoutBlockKind.Table"/> blocks.
		/// </summary>
		public TableModel Table;

		/// <summary>
		/// The images that compose a figure, for <see cref="LayoutBlockKind.Figure"/> blocks.
		/// </summary>
		public List<ImagePlacement> Images = new List<ImagePlacement>();

		/// <summary>
		/// True for figures drawn with vector graphics, which must be rendered to obtain an image.
		/// </summary>
		public bool IsVectorFigure;

		/// <summary>
		/// The caption block attached to a figure or table, or null.
		/// </summary>
		public LayoutBlock Caption;

		/// <summary>
		/// The alternate text from the structure tree, or null.
		/// </summary>
		public string AltText;

		/// <summary>
		/// The text lines that fall inside a figure (chart labels, diagram text).
		/// </summary>
		public List<TextLine> FigureText = new List<TextLine>();

		/// <summary>
		/// True for text in a secondary orientation (for example a rotated margin note), which is never a heading.
		/// </summary>
		public bool IsMarginal;

		/// <summary>
		/// The 1-based page number.
		/// </summary>
		public int PageNumber;

		/// <summary>
		/// The dominant font size of the text, or 0.
		/// </summary>
		public double FontSize
		{
			get
			{
				if (this.Lines.Count == 0)
					return 0;

				// the size of the most characters.
				var best = this.Lines[0].FontSize;
				var bestCount = 0;
				var counts = new Dictionary<double, int>();
				foreach (var line in this.Lines)
				{
					counts.TryGetValue(line.FontSize, out var count);
					count += line.CharCount;
					counts[line.FontSize] = count;
					if (count > bestCount)
					{
						best = line.FontSize;
						bestCount = count;
					}
				}

				return best;
			}
		}

		/// <summary>
		/// True when most of the text is bold.
		/// </summary>
		public bool IsBold
		{
			get
			{
				int bold = 0, total = 0;
				foreach (var line in this.Lines)
				{
					var count = line.CharCount;
					total += count;
					if (line.IsBold)
						bold += count;
				}

				return total > 0 && bold * 10 >= total * 7;
			}
		}

		/// <summary>
		/// Returns the plain text of the block, joining lines with spaces and removing end-of-line hyphenation.
		/// </summary>
		/// <returns>The text.</returns>
		public string GetText() => TextAssembler.JoinLines(this.Lines);

		/// <inheritdoc/>
		public override string ToString() => $"{this.Kind} {this.Bounds}: {GetText()}";
	}
}
