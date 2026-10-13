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

// Options of the content extraction.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Options that control what <see cref="PdfDocument.Extract(PdfExtractionOptions)"/> extracts and how.
	/// </summary>
	/// <remarks>
	/// The defaults produce the most complete result: text with formatting, headings, lists, tables,
	/// images and vector figures, with running headers and footers removed from the text flow.
	/// </remarks>
	public sealed class PdfExtractionOptions
	{
		/// <summary>
		/// The default options.
		/// </summary>
		public static PdfExtractionOptions Default => new PdfExtractionOptions();

		#region Pages

		/// <summary>
		/// The pages to extract, as a list of 1-based numbers and ranges: "1-3, 5, 8-". Null or empty for all the pages.
		/// </summary>
		public string Pages { get; set; }

		#endregion

		#region Structure

		/// <summary>
		/// Detect tables drawn with ruling lines. Default: true.
		/// </summary>
		public bool DetectTables { get; set; } = true;

		/// <summary>
		/// Detect tables without ruling lines, from the alignment of the text. Default: true.
		/// </summary>
		public bool DetectBorderlessTables { get; set; } = true;

		/// <summary>
		/// Join tables that continue on the next page into a single table. Default: true.
		/// </summary>
		public bool JoinTablesAcrossPages { get; set; } = true;

		/// <summary>
		/// Detect headings and their levels. Default: true.
		/// </summary>
		public bool DetectHeadings { get; set; } = true;

		/// <summary>
		/// Recognize running headers, footers and page numbers. Default: true.
		/// </summary>
		/// <remarks>
		/// Recognized headers and footers are reported as <see cref="PdfBlockType.PageHeader"/> and
		/// <see cref="PdfBlockType.PageFooter"/> blocks, which the text writers omit unless
		/// <see cref="PdfMarkdownOptions.IncludePageHeadersAndFooters"/> is set.
		/// </remarks>
		public bool DetectHeadersAndFooters { get; set; } = true;

		/// <summary>
		/// Join paragraphs split by column and page breaks. Default: true.
		/// </summary>
		public bool JoinParagraphs { get; set; } = true;

		/// <summary>
		/// Include the content of annotations, such as filled form fields, stamps and text boxes. Default: true.
		/// </summary>
		public bool IncludeAnnotations { get; set; } = true;

		#endregion

		#region Images

		/// <summary>
		/// Extract images and figures. Default: true.
		/// </summary>
		public bool ExtractImages { get; set; } = true;

		/// <summary>
		/// Detect figures (pictures, charts and diagrams) even when <see cref="ExtractImages"/> is false. Default: true.
		/// </summary>
		/// <remarks>
		/// Detected figures keep their text labels out of the text flow and are reported as <see cref="PdfFigure"/> blocks
		/// with their caption and alternate text, without decoding any image data.
		/// </remarks>
		public bool DetectFigures { get; set; } = true;

		/// <summary>
		/// The options of the image extraction.
		/// </summary>
		public PdfImageOptions Images { get; set; } = new PdfImageOptions();

		#endregion

		#region Execution

		/// <summary>
		/// The maximum number of pages processed in parallel. Default: the number of processors.
		/// </summary>
		public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

		/// <summary>
		/// Token used to cancel the extraction.
		/// </summary>
		public CancellationToken CancellationToken { get; set; }

		/// <summary>
		/// Called after each page is processed with the number of pages completed and the total.
		/// </summary>
		/// <remarks>
		/// The callback can be invoked from multiple threads.
		/// </remarks>
		public Action<int, int> Progress { get; set; }

		#endregion

		#region Methods

		/// <summary>
		/// Returns the 1-based numbers of the pages selected by <see cref="Pages"/>.
		/// </summary>
		/// <param name="pageCount">The number of pages of the document.</param>
		/// <returns>The page numbers in ascending order, without duplicates.</returns>
		/// <exception cref="FormatException"><see cref="Pages"/> is not a valid page list.</exception>
		public IReadOnlyList<int> GetPageNumbers(int pageCount)
		{
			var result = new SortedSet<int>();
			if (string.IsNullOrWhiteSpace(this.Pages))
			{
				for (int i = 1; i <= pageCount; i++)
					result.Add(i);

				return new List<int>(result);
			}

			foreach (var part in this.Pages.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var item = part.Trim();
				var dash = item.IndexOf('-');
				int first, last;
				if (dash < 0)
				{
					first = last = ParseNumber(item, pageCount);
				}
				else
				{
					var start = item.Substring(0, dash).Trim();
					var end = item.Substring(dash + 1).Trim();
					first = start.Length == 0 ? 1 : ParseNumber(start, pageCount);
					last = end.Length == 0 ? pageCount : ParseNumber(end, pageCount);
				}

				for (int n = Math.Max(1, first); n <= Math.Min(pageCount, last); n++)
					result.Add(n);
			}

			return new List<int>(result);
		}

		/// <summary>
		/// Parses a page number of a page list.
		/// </summary>
		/// <param name="text">The page number, or <c>last</c> for the last page.</param>
		/// <param name="pageCount">The number of pages of the document.</param>
		/// <returns>The 1-based page number; not validated against <paramref name="pageCount"/>.</returns>
		/// <exception cref="FormatException"><paramref name="text"/> is not an integer or <c>last</c>.</exception>
		private static int ParseNumber(string text, int pageCount)
		{
			// "last" is accepted as an alias of the last page.
			if (string.Equals(text, "last", StringComparison.OrdinalIgnoreCase))
				return pageCount;

			if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
				throw new FormatException($"Invalid page number: '{text}'.");

			return value;
		}

		#endregion
	}

	/// <summary>
	/// Options of the image extraction.
	/// </summary>
	public sealed class PdfImageOptions
	{
		/// <summary>
		/// The minimum displayed width and height of an image, in points, to be extracted. Smaller images
		/// (icons, bullets, decorations) are skipped. Default: 24.
		/// </summary>
		public double MinimumSize { get; set; } = 24;

		/// <summary>
		/// Render figures drawn with vector graphics (charts, diagrams) to images. Default: true.
		/// </summary>
		public bool RenderVectorFigures { get; set; } = true;

		/// <summary>
		/// The resolution used to render vector figures, in dots per inch. Default: 150.
		/// </summary>
		public int RenderResolution { get; set; } = 150;

		/// <summary>
		/// The output format of the images. Default: <see cref="PdfImageFormat.Auto"/>: JPEG images are kept
		/// as JPEG without recompression when possible, everything else is encoded as PNG.
		/// </summary>
		public PdfImageFormat Format { get; set; } = PdfImageFormat.Auto;

		/// <summary>
		/// Apply soft masks and masks to produce transparent images. Default: true.
		/// </summary>
		public bool ApplyMasks { get; set; } = true;

		/// <summary>
		/// Return each distinct image only once, even when it's placed several times. Default: true.
		/// </summary>
		public bool Deduplicate { get; set; } = true;

		/// <summary>
		/// Rotate and flip images as they appear on the page. Default: true.
		/// </summary>
		public bool ApplyOrientation { get; set; } = true;

		/// <summary>
		/// The maximum number of pixels (width × height) of an extracted image; larger images are scaled down. Default: 40 megapixels.
		/// </summary>
		public long MaxPixels { get; set; } = 40_000_000;

		/// <summary>
		/// Include images of scanned pages (images covering a whole page). Default: true when the page has no text, false otherwise.
		/// </summary>
		public bool? IncludePageScans { get; set; }
	}

	/// <summary>
	/// The encoding of extracted images.
	/// </summary>
	public enum PdfImageFormat
	{
		/// <summary>Keep JPEG images as they are; encode the others as PNG.</summary>
		Auto,

		/// <summary>Encode all the images as PNG (lossless, supports transparency).</summary>
		Png,

		/// <summary>Encode all the images as JPEG (smaller, no transparency).</summary>
		Jpeg,
	}
}
