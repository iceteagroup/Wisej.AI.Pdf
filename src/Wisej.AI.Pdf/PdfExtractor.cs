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

// One-call helpers for the most common tasks.

using System.Collections.Generic;
using System.IO;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// One-call helpers to convert PDF documents to Markdown, HTML, text or JSON, and to extract tables and images.
	/// </summary>
	/// <remarks>
	/// <code><![CDATA[
	/// // Markdown with images saved next to it.
	/// var content = PdfExtractor.Extract("report.pdf");
	/// File.WriteAllText("report.md", content.ToMarkdown());
	/// foreach (var image in content.Images)
	///     image.Save(Path.Combine("images", image.FileName));
	///
	/// // Plain text for indexing.
	/// var text = PdfExtractor.ToText(File.OpenRead("invoice.pdf"));
	/// ]]></code>
	/// </remarks>
	public static class PdfExtractor
	{
		/// <summary>
		/// Extracts the content of a PDF file.
		/// </summary>
		/// <param name="path">The path of the PDF file.</param>
		/// <param name="options">The extraction options; null for the defaults.</param>
		/// <param name="password">The password of an encrypted document; null for none.</param>
		/// <returns>The extracted content.</returns>
		public static PdfContent Extract(string path, PdfExtractionOptions options = null, string password = null)
		{
			using (var document = PdfDocument.Open(path, password))
				return document.Extract(options);
		}

		/// <summary>
		/// Extracts the content of a PDF stream.
		/// </summary>
		/// <param name="stream">The stream containing the PDF document.</param>
		/// <param name="options">The extraction options; null for the defaults.</param>
		/// <param name="password">The password of an encrypted document; null for none.</param>
		/// <returns>The extracted content.</returns>
		public static PdfContent Extract(Stream stream, PdfExtractionOptions options = null, string password = null)
		{
			using (var document = PdfDocument.Open(stream, password))
				return document.Extract(options);
		}

		/// <summary>
		/// Converts a PDF document to Markdown.
		/// </summary>
		/// <param name="stream">The stream containing the PDF document.</param>
		/// <param name="markdownOptions">The Markdown options; null for the defaults (images are referenced as "images/image-N.png").</param>
		/// <param name="options">The extraction options; null for the defaults.</param>
		/// <returns>The Markdown text.</returns>
		public static string ToMarkdown(Stream stream, PdfMarkdownOptions markdownOptions = null, PdfExtractionOptions options = null)
		{
			return Extract(stream, options).ToMarkdown(markdownOptions);
		}

		/// <summary>
		/// Converts a PDF document to HTML, with the images embedded.
		/// </summary>
		/// <param name="stream">The stream containing the PDF document.</param>
		/// <param name="options">The extraction options; null for the defaults.</param>
		/// <returns>The HTML document.</returns>
		public static string ToHtml(Stream stream, PdfExtractionOptions options = null)
		{
			return Extract(stream, options).ToHtml(embedImages: true);
		}

		/// <summary>
		/// Extracts the plain text of a PDF document in reading order.
		/// </summary>
		/// <param name="stream">The stream containing the PDF document.</param>
		/// <param name="pages">The pages to extract, for example "1-3, 5"; null for all the pages.</param>
		/// <returns>The text.</returns>
		public static string ToText(Stream stream, string pages = null)
		{
			using (var document = PdfDocument.Open(stream))
				return document.ExtractText(pages);
		}

		/// <summary>
		/// Converts a PDF document to JSON.
		/// </summary>
		/// <param name="stream">The stream containing the PDF document.</param>
		/// <param name="options">The extraction options; null for the defaults.</param>
		/// <returns>The JSON text.</returns>
		public static string ToJson(Stream stream, PdfExtractionOptions options = null)
		{
			return Extract(stream, options).ToJson();
		}

		/// <summary>
		/// Extracts the tables of a PDF document.
		/// </summary>
		/// <param name="stream">The stream containing the PDF document.</param>
		/// <param name="pages">The pages to extract, for example "1-3, 5"; null for all the pages.</param>
		/// <returns>The tables.</returns>
		public static IReadOnlyList<PdfTable> ExtractTables(Stream stream, string pages = null)
		{
			using (var document = PdfDocument.Open(stream))
				return document.ExtractTables(pages);
		}

		/// <summary>
		/// Extracts the images and figures of a PDF document.
		/// </summary>
		/// <param name="stream">The stream containing the PDF document.</param>
		/// <param name="options">The image options; null for the defaults.</param>
		/// <returns>The distinct images.</returns>
		public static IReadOnlyList<PdfImage> ExtractImages(Stream stream, PdfImageOptions options = null)
		{
			using (var document = PdfDocument.Open(stream))
				return document.ExtractImages(options);
		}
	}
}
