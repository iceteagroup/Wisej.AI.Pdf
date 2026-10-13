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

// The result of the content extraction.

using System.Collections.Generic;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// A page of extracted content.
	/// </summary>
	public sealed class PdfContentPage
	{
		/// <summary>
		/// Initializes an empty page; the extraction engine sets the properties.
		/// </summary>
		internal PdfContentPage()
		{
		}

		/// <summary>The 1-based page number.</summary>
		public int Number { get; internal set; }

		/// <summary>The page label (e.g. "iv"), or the page number as text.</summary>
		public string Label { get; internal set; }

		/// <summary>The width of the page in points.</summary>
		public double Width { get; internal set; }

		/// <summary>The height of the page in points.</summary>
		public double Height { get; internal set; }

		/// <summary>The blocks that start on this page, in reading order.</summary>
		public IReadOnlyList<PdfBlock> Blocks { get; internal set; }

		/// <summary>
		/// True when the page has no extractable text (for example a scanned page without OCR).
		/// </summary>
		public bool IsImageOnly { get; internal set; }

		/// <summary>
		/// Converts the content of this page to Markdown.
		/// </summary>
		/// <param name="options">The Markdown options; null for the defaults.</param>
		/// <returns>The Markdown text.</returns>
		public string ToMarkdown(PdfMarkdownOptions options = null) => new MarkdownWriter(options ?? new PdfMarkdownOptions()).Write(this);

		/// <inheritdoc/>
		public override string ToString() => $"Page {this.Number}: {this.Blocks.Count} blocks";
	}

	/// <summary>
	/// The content extracted from a PDF document: text with formatting and structure, tables and images.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The content can be navigated through <see cref="Blocks"/> (reading order across all pages),
	/// <see cref="Pages"/>, <see cref="Tables"/> and <see cref="Images"/>, or converted to
	/// Markdown, HTML, plain text or JSON.
	/// </para>
	/// <code><![CDATA[
	/// using (var document = PdfDocument.Open("report.pdf"))
	/// {
	///     var content = document.Extract();
	///     File.WriteAllText("report.md", content.ToMarkdown());
	///
	///     foreach (var image in content.Images)
	///         image.Save(Path.Combine("images", image.FileName));
	/// }
	/// ]]></code>
	/// </remarks>
	public sealed class PdfContent
	{
		/// <summary>
		/// Initializes empty content; the extraction engine sets the properties.
		/// </summary>
		internal PdfContent()
		{
		}

		#region Properties

		/// <summary>
		/// The document information (title, author, ...).
		/// </summary>
		public PdfDocumentInfo Info { get; internal set; }

		/// <summary>
		/// The number of pages of the document (including pages that were not extracted).
		/// </summary>
		public int PageCount { get; internal set; }

		/// <summary>
		/// The extracted pages.
		/// </summary>
		public IReadOnlyList<PdfContentPage> Pages { get; internal set; }

		/// <summary>
		/// All the blocks of the extracted pages, in reading order.
		/// </summary>
		public IReadOnlyList<PdfBlock> Blocks { get; internal set; }

		/// <summary>
		/// All the tables, in reading order. Tables continued across pages are reported once.
		/// </summary>
		public IReadOnlyList<PdfTable> Tables { get; internal set; }

		/// <summary>
		/// All the distinct images, in order of appearance.
		/// </summary>
		public IReadOnlyList<PdfImage> Images { get; internal set; }

		#endregion

		#region Methods

		/// <summary>
		/// Converts the content to Markdown (GitHub-flavored).
		/// </summary>
		/// <param name="options">The Markdown options; null for the defaults.</param>
		/// <returns>The Markdown text.</returns>
		public string ToMarkdown(PdfMarkdownOptions options = null) => new MarkdownWriter(options ?? new PdfMarkdownOptions()).Write(this);

		/// <summary>
		/// Converts the content to an HTML document.
		/// </summary>
		/// <param name="embedImages">True to embed the images as data URIs; false to reference them by <see cref="PdfImage.FileName"/>.</param>
		/// <returns>The HTML text.</returns>
		public string ToHtml(bool embedImages = true) => new HtmlWriter(embedImages).Write(this);

		/// <summary>
		/// Converts the content to plain text, with paragraphs separated by blank lines and tables as tab-separated rows.
		/// </summary>
		/// <param name="includePageBreaks">True to separate pages with a form feed character.</param>
		/// <returns>The plain text.</returns>
		public string ToText(bool includePageBreaks = false) => PlainTextWriter.Write(this, includePageBreaks);

		/// <summary>
		/// Converts the content to JSON, with blocks, bounds, formatting, tables and image references.
		/// </summary>
		/// <param name="indented">True to indent the output.</param>
		/// <returns>The JSON text.</returns>
		public string ToJson(bool indented = true) => JsonWriter.Write(this, indented);

		#endregion
	}
}
