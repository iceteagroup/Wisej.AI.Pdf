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

// A page of a PDF document.

using System;
using System.IO;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Represents a page of a <see cref="PdfDocument"/>.
	/// </summary>
	/// <remarks>
	/// The <see cref="Width"/> and <see cref="Height"/> of the page are the dimensions of the visible
	/// area (the crop box) as displayed, that is after applying the page rotation and user unit.
	/// All the coordinates returned by the extraction API are relative to this display area,
	/// with the origin at the top-left corner.
	/// </remarks>
	public sealed class PdfPage
	{
		/// <summary>
		/// Initializes a new instance of <see cref="PdfPage"/>.
		/// </summary>
		/// <param name="document">The owner document.</param>
		/// <param name="number">The 1-based page number.</param>
		/// <param name="dictionary">The page dictionary.</param>
		/// <param name="resources">The resources dictionary, including inherited resources.</param>
		/// <param name="mediaBox">The media box (inherited if necessary).</param>
		/// <param name="cropBox">The crop box (inherited if necessary), or null.</param>
		/// <param name="rotation">The rotation in degrees (inherited if necessary).</param>
		internal PdfPage(PdfDocument document, int number, PdfDictionary dictionary, PdfDictionary resources, double[] mediaBox, double[] cropBox, int rotation)
		{
			this.Document = document;
			this.Number = number;
			this.Dictionary = dictionary;
			this.Resources = resources ?? new PdfDictionary();

			// default to US Letter when the media box is missing or invalid.
			var media = NormalizeBox(mediaBox) ?? new[] { 0.0, 0, 612, 792 };
			var crop = NormalizeBox(cropBox);

			// the crop box is intersected with the media box.
			if (crop != null)
			{
				crop = new[]
				{
					Math.Max(crop[0], media[0]), Math.Max(crop[1], media[1]),
					Math.Min(crop[2], media[2]), Math.Min(crop[3], media[3]),
				};

				if (crop[2] - crop[0] < 1 || crop[3] - crop[1] < 1)
					crop = null;
			}

			this.MediaBox = ToRect(media);
			this.CropBox = ToRect(crop ?? media);

			// rotation is a multiple of 90; normalize negative and invalid values.
			rotation %= 360;
			if (rotation < 0)
				rotation += 360;

			this.Rotation = (rotation / 90) * 90;

			this.UserUnit = Math.Max(0.01, dictionary.GetNumber(PdfNames.UserUnit, 1));

			var box = crop ?? media;
			var width = (box[2] - box[0]) * this.UserUnit;
			var height = (box[3] - box[1]) * this.UserUnit;
			var swap = this.Rotation == 90 || this.Rotation == 270;
			this.Width = swap ? height : width;
			this.Height = swap ? width : height;

			this.DisplayMatrix = BuildDisplayMatrix(box, width, height);
		}

		#region Properties

		/// <summary>
		/// The document that contains this page.
		/// </summary>
		public PdfDocument Document { get; }

		/// <summary>
		/// The 1-based page number.
		/// </summary>
		public int Number { get; }

		/// <summary>
		/// The width of the displayed page in points (1/72 inch).
		/// </summary>
		public double Width { get; }

		/// <summary>
		/// The height of the displayed page in points (1/72 inch).
		/// </summary>
		public double Height { get; }

		/// <summary>
		/// The clockwise rotation of the page when displayed: 0, 90, 180 or 270.
		/// </summary>
		public int Rotation { get; }

		/// <summary>
		/// The page label (for example "iv" or "A-3") when the document defines page labels; otherwise the page number.
		/// </summary>
		public string Label => this.Document.GetPageLabel(this.Number);

		/// <summary>
		/// The media box in default user space (bottom-left origin), unrotated.
		/// </summary>
		internal PdfRect MediaBox { get; }

		/// <summary>
		/// The crop box in default user space (bottom-left origin), unrotated.
		/// </summary>
		internal PdfRect CropBox { get; }

		/// <summary>
		/// The size of a user space unit in points.
		/// </summary>
		internal double UserUnit { get; }

		/// <summary>
		/// The page dictionary.
		/// </summary>
		internal PdfDictionary Dictionary { get; }

		/// <summary>
		/// The resources dictionary of the page.
		/// </summary>
		internal PdfDictionary Resources { get; }

		/// <summary>
		/// Transforms default user space to display space: top-left origin, rotated, in points.
		/// </summary>
		internal Matrix DisplayMatrix { get; }

		#endregion

		#region Methods

		/// <summary>
		/// Returns the decoded page content, concatenating the content streams when there is more than one.
		/// </summary>
		/// <returns>The content bytes; empty when the page has no content.</returns>
		internal byte[] GetContentBytes()
		{
			var contents = this.Dictionary.Resolve(PdfNames.Contents);
			if (contents is PdfStream stream)
				return SafeGetBytes(stream);

			if (contents is PdfArray array)
			{
				using (var output = new MemoryStream())
				{
					for (int i = 0; i < array.Count; i++)
					{
						if (!(array.Resolve(i) is PdfStream part))
							continue;

						var bytes = SafeGetBytes(part);
						output.Write(bytes, 0, bytes.Length);

						// streams may split tokens at their boundaries only at whitespace: add a separator.
						output.WriteByte((byte)'\n');
					}

					return output.ToArray();
				}
			}

			return Array.Empty<byte>();
		}

		/// <inheritdoc/>
		public override string ToString() => $"Page {this.Number} ({this.Width:0.#} x {this.Height:0.#})";

		#endregion

		#region Implementation

		/// <summary>
		/// Decodes a content stream, ignoring decoding errors.
		/// </summary>
		/// <param name="stream">The content stream.</param>
		/// <returns>The decoded bytes; empty when the stream can't be decoded.</returns>
		private static byte[] SafeGetBytes(PdfStream stream)
		{
			try
			{
				return stream.GetBytes();
			}
			catch (Exception)
			{
				return Array.Empty<byte>();
			}
		}

		/// <summary>
		/// Normalizes a PDF rectangle so that the lower-left corner comes first.
		/// </summary>
		/// <param name="box">The rectangle <c>[x0 y0 x1 y1]</c> with the corners in any order; can be null.</param>
		/// <returns>The normalized rectangle, or null when it's missing, invalid or smaller than 1 unit.</returns>
		private static double[] NormalizeBox(double[] box)
		{
			if (box == null || box.Length < 4)
				return null;

			var x0 = Math.Min(box[0], box[2]);
			var x1 = Math.Max(box[0], box[2]);
			var y0 = Math.Min(box[1], box[3]);
			var y1 = Math.Max(box[1], box[3]);

			if (x1 - x0 < 1 || y1 - y0 < 1 || double.IsNaN(x0) || double.IsNaN(y0))
				return null;

			return new[] { x0, y0, x1, y1 };
		}

		/// <summary>
		/// Converts a normalized rectangle array to a <see cref="PdfRect"/>.
		/// </summary>
		/// <param name="box">The rectangle <c>[x0 y0 x1 y1]</c>.</param>
		/// <returns>The rectangle.</returns>
		private static PdfRect ToRect(double[] box) => new PdfRect(box[0], box[1], box[2], box[3]);

		/// <summary>
		/// Builds the matrix that maps default user space (bottom-left origin) to display space
		/// (top-left origin, scaled by <see cref="UserUnit"/> and rotated clockwise by <see cref="Rotation"/>).
		/// </summary>
		/// <param name="box">The visible box in user space.</param>
		/// <param name="width">The unrotated width of the box, in points.</param>
		/// <param name="height">The unrotated height of the box, in points.</param>
		/// <returns>The display matrix.</returns>
		private Matrix BuildDisplayMatrix(double[] box, double width, double height)
		{
			var toOrigin = new Matrix(this.UserUnit, 0, 0, this.UserUnit, -box[0] * this.UserUnit, -box[1] * this.UserUnit);

			Matrix rotation;
			switch (this.Rotation)
			{
				case 90:
					rotation = new Matrix(0, 1, 1, 0, 0, 0);
					break;

				case 180:
					rotation = new Matrix(-1, 0, 0, 1, width, 0);
					break;

				case 270:
					rotation = new Matrix(0, -1, -1, 0, height, width);
					break;

				default:
					rotation = new Matrix(1, 0, 0, -1, 0, height);
					break;
			}

			return toOrigin.Multiply(rotation);
		}

		#endregion
	}
}
