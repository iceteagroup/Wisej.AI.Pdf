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

// An image extracted from a PDF document.

using System;
using System.Collections.Generic;
using System.IO;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// The origin of an extracted image.
	/// </summary>
	public enum PdfImageKind
	{
		/// <summary>A raster image embedded in the document (photo, screenshot, logo).</summary>
		Picture,

		/// <summary>A figure drawn with vector graphics (chart, diagram), rendered to an image.</summary>
		VectorFigure,

		/// <summary>The image of a scanned page.</summary>
		PageScan,
	}

	/// <summary>
	/// An image extracted from a PDF document, encoded as PNG or JPEG.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Images are decoded with full fidelity: every colour space (including ICC-based, Lab, Separation,
	/// DeviceN and Indexed), all bit depths, decode arrays, soft masks, colour-key masks and stencil masks
	/// are applied, and image tiles are reassembled. JPEG images without transparency are returned as the
	/// original JPEG data, without recompression.
	/// </para>
	/// </remarks>
	public sealed class PdfImage
	{
		/// <summary>
		/// Initializes an empty image; the image extractor sets the properties.
		/// </summary>
		internal PdfImage()
		{
		}

		#region Properties

		/// <summary>
		/// A stable identifier of the image within the document, for example "image-3".
		/// </summary>
		public string Id { get; internal set; }

		/// <summary>
		/// The 1-based number of the first page that shows the image.
		/// </summary>
		public int PageNumber { get; internal set; }

		/// <summary>
		/// The numbers of all the pages that show the image (when it's reused, e.g. a logo).
		/// </summary>
		public IReadOnlyList<int> PageNumbers { get; internal set; }

		/// <summary>
		/// The bounds of the image on its first page, in points from the top-left corner.
		/// </summary>
		public PdfRect Bounds { get; internal set; }

		/// <summary>
		/// The width of the image in pixels.
		/// </summary>
		public int Width { get; internal set; }

		/// <summary>
		/// The height of the image in pixels.
		/// </summary>
		public int Height { get; internal set; }

		/// <summary>
		/// The horizontal resolution of the image as displayed on the page, in dots per inch.
		/// </summary>
		public double Dpi => this.Bounds.Width > 0 ? this.Width * 72 / this.Bounds.Width : 0;

		/// <summary>
		/// The kind of image.
		/// </summary>
		public PdfImageKind Kind { get; internal set; }

		/// <summary>
		/// The encoded image data.
		/// </summary>
		public byte[] Data { get; internal set; }

		/// <summary>
		/// The MIME type of <see cref="Data"/>: "image/png" or "image/jpeg".
		/// </summary>
		public string MimeType { get; internal set; }

		/// <summary>
		/// The file extension for <see cref="Data"/>, including the dot: ".png" or ".jpg".
		/// </summary>
		public string Extension => this.MimeType == "image/jpeg" ? ".jpg" : ".png";

		/// <summary>
		/// True when the image has transparent pixels.
		/// </summary>
		public bool HasTransparency { get; internal set; }

		/// <summary>
		/// The alternate text declared by the document for accessibility, or null.
		/// </summary>
		public string AltText { get; internal set; }

		/// <summary>
		/// The caption of the figure that contains the image, or null.
		/// </summary>
		public string Caption { get; internal set; }

		/// <summary>
		/// The name of the image resource in the document, or null.
		/// </summary>
		public string ResourceName { get; internal set; }

		/// <summary>
		/// The suggested file name: <see cref="Id"/> plus <see cref="Extension"/>.
		/// </summary>
		public string FileName => this.Id + this.Extension;

		#endregion

		#region Methods

		/// <summary>
		/// Saves the image to a file.
		/// </summary>
		/// <param name="path">The path of the file. When it has no extension, <see cref="Extension"/> is appended.</param>
		/// <returns>The full path of the saved file.</returns>
		public string Save(string path)
		{
			if (path == null)
				throw new ArgumentNullException(nameof(path));

			if (string.IsNullOrEmpty(Path.GetExtension(path)))
				path += this.Extension;

			File.WriteAllBytes(path, this.Data);
			return Path.GetFullPath(path);
		}

		/// <summary>
		/// Writes the image to a stream.
		/// </summary>
		/// <param name="stream">The target stream.</param>
		public void Save(Stream stream)
		{
			if (stream == null)
				throw new ArgumentNullException(nameof(stream));

			stream.Write(this.Data, 0, this.Data.Length);
		}

		/// <summary>
		/// Returns the image as a data URI, for embedding in HTML or Markdown, or for sending to vision models.
		/// </summary>
		/// <returns>The data URI.</returns>
		public string ToDataUri() => "data:" + this.MimeType + ";base64," + Convert.ToBase64String(this.Data);

		/// <summary>
		/// Returns the image data encoded in Base64.
		/// </summary>
		/// <returns>The Base64 text.</returns>
		public string ToBase64() => Convert.ToBase64String(this.Data);

		/// <inheritdoc/>
		public override string ToString() => $"{this.Id} {this.Width}x{this.Height} {this.MimeType} (p. {this.PageNumber})";

		#endregion
	}
}
