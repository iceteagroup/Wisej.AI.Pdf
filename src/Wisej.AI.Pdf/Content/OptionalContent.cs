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

// Optional content (layers) visibility (ISO 32000-2, 8.11).

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Content
{
	/// <summary>
	/// Determines whether optional content (layers) is visible in the default configuration.
	/// </summary>
	/// <remarks>
	/// Only the default configuration (<c>/OCProperties /D</c>) is used, which is what a viewer
	/// shows when the document is opened. Content in hidden layers is not extracted.
	/// </remarks>
	internal sealed class OptionalContent
	{
		// one instance per document; null when the document has no optional content.
		private static readonly ConditionalWeakTable<PdfDocument, OptionalContent> _instances = new ConditionalWeakTable<PdfDocument, OptionalContent>();

		// the optional content groups that are off in the default configuration.
		private readonly HashSet<object> _hidden = new HashSet<object>(ReferenceEqualityComparer.Instance);
		// resolves references to groups.
		private readonly PdfFile _file;

		private OptionalContent(PdfDocument document, PdfDictionary properties)
		{
			_file = document.File;

			var config = properties.GetDictionary(PdfNames.D);
			var groups = properties.GetArray("OCGs");
			if (config == null)
				return;

			// BaseState /OFF hides every group not listed in /ON.
			if (config.GetNameValue("BaseState") == "OFF" && groups != null)
			{
				for (int i = 0; i < groups.Count; i++)
				{
					if (groups.Resolve(i) is PdfDictionary group)
						_hidden.Add(group);
				}
			}

			var on = config.GetArray("ON");
			if (on != null)
			{
				for (int i = 0; i < on.Count; i++)
				{
					if (on.Resolve(i) is PdfDictionary group)
						_hidden.Remove(group);
				}
			}

			var off = config.GetArray("OFF");
			if (off != null)
			{
				for (int i = 0; i < off.Count; i++)
				{
					if (off.Resolve(i) is PdfDictionary group)
						_hidden.Add(group);
				}
			}
		}

		/// <summary>
		/// Returns the optional content configuration of <paramref name="document"/>.
		/// </summary>
		/// <param name="document">The document.</param>
		/// <returns>The configuration, or null when the document has no hidden layers.</returns>
		public static OptionalContent Get(PdfDocument document)
		{
			var properties = document.Catalog.GetDictionary(PdfNames.OCProperties);
			if (properties == null)
				return null;

			var instance = _instances.GetValue(document, d => new OptionalContent(d, properties));
			return instance._hidden.Count == 0 ? null : instance;
		}

		/// <summary>
		/// Returns true when content marked with <paramref name="membership"/> is visible.
		/// </summary>
		/// <param name="membership">An optional content group or membership dictionary (or a reference to it); null means always visible.</param>
		/// <returns>True when visible.</returns>
		public bool IsVisible(object membership)
		{
			if (!(PdfFile.Resolve(_file, membership) is PdfDictionary dictionary))
				return true;

			if (dictionary.IsType("OCMD"))
				return IsMembershipVisible(dictionary);

			return !_hidden.Contains(dictionary);
		}

		// membership dictionaries combine groups with a visibility policy (AnyOn by default).
		private bool IsMembershipVisible(PdfDictionary membership)
		{
			var groupsValue = membership.Resolve("OCGs");
			var groups = new List<PdfDictionary>();
			if (groupsValue is PdfArray array)
			{
				for (int i = 0; i < array.Count; i++)
				{
					if (array.Resolve(i) is PdfDictionary group)
						groups.Add(group);
				}
			}
			else if (groupsValue is PdfDictionary single)
			{
				groups.Add(single);
			}

			if (groups.Count == 0)
				return true;

			var visibleCount = 0;
			foreach (var group in groups)
			{
				if (!_hidden.Contains(group))
					visibleCount++;
			}

			switch (membership.GetNameValue("P"))
			{
				case "AllOn": return visibleCount == groups.Count;
				case "AnyOff": return visibleCount < groups.Count;
				case "AllOff": return visibleCount == 0;
				default: return visibleCount > 0;
			}
		}
	}
}
