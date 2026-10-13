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

// Words and lines: the first level of text layout analysis.

using System;
using System.Collections.Generic;
using System.Text;
using Wisej.AI.Pdf.Content;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// A word: a run of glyphs without spaces, with uniform direction.
	/// </summary>
	/// <remarks>
	/// Coordinates are in the <em>reading frame</em> of the line: a coordinate system rotated so the
	/// text runs left to right along the X axis. For horizontal text it is the display space.
	/// </remarks>
	internal sealed class TextWord
	{
		/// <summary>
		/// The glyphs of the word, in reading order.
		/// </summary>
		public readonly List<TextGlyph> Glyphs = new List<TextGlyph>();

		/// <summary>
		/// The text of the word.
		/// </summary>
		public string Text;

		/// <summary>
		/// The bounds of the word in the reading frame.
		/// </summary>
		public PdfRect Bounds;

		/// <summary>
		/// The baseline position in the reading frame.
		/// </summary>
		public double Baseline;

		/// <summary>
		/// The font size of the word (of its first glyph).
		/// </summary>
		public double FontSize;

		/// <summary>
		/// True when the word is a superscript relative to its line.
		/// </summary>
		public bool IsSuperscript;

		/// <summary>
		/// True when the word is a subscript relative to its line.
		/// </summary>
		public bool IsSubscript;

		/// <summary>
		/// True when the word is followed by a space in the line.
		/// </summary>
		public bool SpaceAfter;

		/// <summary>
		/// The first glyph of the word, which carries the style.
		/// </summary>
		public TextGlyph First => this.Glyphs[0];

		/// <summary>
		/// True when most of the word is bold.
		/// </summary>
		public bool IsBold;

		/// <summary>
		/// True when most of the word is italic.
		/// </summary>
		public bool IsItalic;

		/// <summary>
		/// True when the word uses a monospaced font.
		/// </summary>
		public bool IsMonospace;

		/// <summary>
		/// The link target URI of the word, or null.
		/// </summary>
		public string LinkUri;

		/// <summary>
		/// The internal link target page, or 0.
		/// </summary>
		public int LinkPage;

		/// <inheritdoc/>
		public override string ToString() => this.Text;
	}

	/// <summary>
	/// A line of text: words that share a baseline and are close enough to be read together.
	/// </summary>
	/// <remarks>
	/// Two runs of text on the same baseline separated by a wide gap (columns, table cells) are
	/// separate lines: the segmentation decides later how they combine.
	/// </remarks>
	internal sealed class TextLine
	{
		/// <summary>
		/// The words of the line, left to right in the reading frame.
		/// </summary>
		public readonly List<TextWord> Words = new List<TextWord>();

		/// <summary>
		/// The bounds of the line in the reading frame.
		/// </summary>
		public PdfRect Bounds;

		/// <summary>
		/// The bounds of the line in display space.
		/// </summary>
		public PdfRect DisplayBounds;

		/// <summary>
		/// The baseline position in the reading frame.
		/// </summary>
		public double Baseline;

		/// <summary>
		/// The dominant font size of the line.
		/// </summary>
		public double FontSize;

		/// <summary>
		/// The orientation of the text in degrees: 0, 90, 180 or 270, clockwise.
		/// </summary>
		public int Orientation;

		/// <summary>
		/// The position of the first glyph in the painting order: used as a tie breaker for reading order.
		/// </summary>
		public int Order;

		/// <summary>
		/// True when most of the characters are bold.
		/// </summary>
		public bool IsBold;

		/// <summary>
		/// True when most of the characters are italic.
		/// </summary>
		public bool IsItalic;

		/// <summary>
		/// True when most of the characters use a monospaced font.
		/// </summary>
		public bool IsMonospace;

		/// <summary>
		/// True when all the glyphs are marked as artifacts.
		/// </summary>
		public bool IsArtifact;

		/// <summary>
		/// The dominant fill colour as 0xRRGGBB.
		/// </summary>
		public int Color;

		/// <summary>
		/// The dominant font family.
		/// </summary>
		public string FontFamily;

		/// <summary>
		/// Set by the segmentation: the line is part of a table, a figure or a header/footer and must not form paragraphs.
		/// </summary>
		public bool IsConsumed;

		// cached text.
		private string _text;

		/// <summary>
		/// The text of the line, with single spaces between words.
		/// </summary>
		public string Text
		{
			get
			{
				if (_text == null)
				{
					var sb = new StringBuilder();
					for (int i = 0; i < this.Words.Count; i++)
					{
						if (i > 0 && this.Words[i - 1].SpaceAfter)
							sb.Append(' ');

						sb.Append(this.Words[i].Text);
					}

					_text = sb.ToString();
				}

				return _text;
			}
		}

		/// <summary>
		/// The number of characters in the line.
		/// </summary>
		public int CharCount
		{
			get
			{
				var count = 0;
				foreach (var word in this.Words)
					count += word.Text.Length;

				return count;
			}
		}

		/// <summary>
		/// Invalidates the cached text after the words were modified.
		/// </summary>
		public void InvalidateText() => _text = null;

		/// <summary>
		/// The left edge in the reading frame.
		/// </summary>
		public double Left => this.Bounds.Left;

		/// <summary>
		/// The right edge in the reading frame.
		/// </summary>
		public double Right => this.Bounds.Right;

		/// <summary>
		/// The top edge in the reading frame.
		/// </summary>
		public double Top => this.Bounds.Top;

		/// <summary>
		/// The bottom edge in the reading frame.
		/// </summary>
		public double Bottom => this.Bounds.Bottom;

		/// <inheritdoc/>
		public override string ToString() => $"{this.Bounds} {this.FontSize:0.#}pt: {this.Text}";
	}

	/// <summary>
	/// Maps between display space and the reading frame of an orientation.
	/// </summary>
	internal static class ReadingFrame
	{
		/// <summary>
		/// Converts a display point to the reading frame of <paramref name="orientation"/>.
		/// </summary>
		/// <param name="orientation">0, 90, 180 or 270 degrees.</param>
		/// <param name="x">The display X.</param>
		/// <param name="y">The display Y.</param>
		/// <param name="width">The page width.</param>
		/// <param name="height">The page height.</param>
		/// <returns>The point in the reading frame.</returns>
		public static PdfPoint ToFrame(int orientation, double x, double y, double width, double height)
		{
			// rotate the page counter-clockwise by the orientation so the text becomes horizontal.
			switch (orientation)
			{
				case 90: return new PdfPoint(y, width - x);
				case 180: return new PdfPoint(width - x, height - y);
				case 270: return new PdfPoint(height - y, x);
				default: return new PdfPoint(x, y);
			}
		}

		/// <summary>
		/// Converts a rectangle in the reading frame back to display space.
		/// </summary>
		/// <param name="orientation">0, 90, 180 or 270 degrees.</param>
		/// <param name="rect">The rectangle in the reading frame.</param>
		/// <param name="width">The page width.</param>
		/// <param name="height">The page height.</param>
		/// <returns>The rectangle in display space.</returns>
		public static PdfRect ToDisplay(int orientation, PdfRect rect, double width, double height)
		{
			switch (orientation)
			{
				case 90: return new PdfRect(width - rect.Top, rect.Left, width - rect.Bottom, rect.Right);
				case 180: return new PdfRect(width - rect.Right, height - rect.Bottom, width - rect.Left, height - rect.Top);
				case 270: return new PdfRect(rect.Top, height - rect.Right, rect.Bottom, height - rect.Left);
				default: return rect;
			}
		}

		/// <summary>
		/// Converts a display rectangle to the reading frame.
		/// </summary>
		/// <param name="orientation">0, 90, 180 or 270 degrees.</param>
		/// <param name="rect">The rectangle in display space.</param>
		/// <param name="width">The page width.</param>
		/// <param name="height">The page height.</param>
		/// <returns>The rectangle in the reading frame.</returns>
		public static PdfRect ToFrame(int orientation, PdfRect rect, double width, double height)
		{
			var a = ToFrame(orientation, rect.Left, rect.Top, width, height);
			var b = ToFrame(orientation, rect.Right, rect.Bottom, width, height);
			return new PdfRect(a.X, a.Y, b.X, b.Y);
		}

		/// <summary>
		/// Returns the orientation bucket (0, 90, 180, 270) of a baseline angle, or -1 for skewed text.
		/// </summary>
		/// <param name="angle">The baseline angle in degrees, clockwise.</param>
		/// <returns>The orientation, or -1.</returns>
		public static int GetOrientation(double angle)
		{
			for (int o = 0; o <= 360; o += 90)
			{
				if (Math.Abs(angle - o) <= 8)
					return o % 360;
			}

			return -1;
		}
	}
}
