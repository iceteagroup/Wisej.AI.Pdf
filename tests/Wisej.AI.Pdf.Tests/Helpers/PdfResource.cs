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

// Test helper: a named resource (font, image, form) created by PdfBuilder.

using System.Collections.Generic;

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// An indirect object that pages use through their resource dictionary, for example a font or an image.
	/// </summary>
	internal sealed class PdfResource
	{
		/// <summary>
		/// Initializes a new resource.
		/// </summary>
		/// <param name="category">The resource category: "Font", "XObject", "ColorSpace", ...</param>
		/// <param name="name">The resource name, without the slash.</param>
		/// <param name="number">The object number.</param>
		public PdfResource(string category, string name, int number)
		{
			this.Category = category;
			this.Name = name;
			this.Number = number;
		}

		/// <summary>
		/// The resource category, for example "Font" or "XObject".
		/// </summary>
		public string Category { get; }

		/// <summary>
		/// The name used in content streams, without the slash.
		/// </summary>
		public string Name { get; }

		/// <summary>
		/// The object number.
		/// </summary>
		public int Number { get; }

		/// <summary>
		/// The indirect reference token, for example "12 0 R".
		/// </summary>
		public string Reference => PdfText.Reference(this.Number);

		/// <inheritdoc/>
		public override string ToString() => $"/{this.Name} ({this.Reference})";
	}

	/// <summary>
	/// An item of the document outline created by <see cref="PdfBuilder.AddOutline"/>.
	/// </summary>
	internal sealed class PdfOutlineItem
	{
		/// <summary>
		/// Initializes a new outline item.
		/// </summary>
		/// <param name="number">The object number.</param>
		/// <param name="title">The title.</param>
		/// <param name="page">The target page.</param>
		/// <param name="parent">The parent item, or null for a top-level item.</param>
		public PdfOutlineItem(int number, string title, PdfPageBuilder page, PdfOutlineItem parent)
		{
			this.Number = number;
			this.Title = title;
			this.Page = page;
			this.Parent = parent;
		}

		/// <summary>The object number.</summary>
		public int Number { get; }

		/// <summary>The title.</summary>
		public string Title { get; }

		/// <summary>The target page.</summary>
		public PdfPageBuilder Page { get; }

		/// <summary>The parent item, or null for a top-level item.</summary>
		public PdfOutlineItem Parent { get; }

		/// <summary>The children, in order.</summary>
		public List<PdfOutlineItem> Children { get; } = new List<PdfOutlineItem>();
	}
}
