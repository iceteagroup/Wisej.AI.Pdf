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

// Document outline (bookmarks) and destinations.

using System.Collections.Generic;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// An entry of the document outline (bookmarks).
	/// </summary>
	public sealed class PdfOutline
	{
		/// <summary>
		/// Prevents external instantiation: instances are created by <see cref="Load"/>.
		/// </summary>
		private PdfOutline()
		{
		}

		#region Properties

		/// <summary>
		/// The text of the outline entry.
		/// </summary>
		public string Title { get; private set; }

		/// <summary>
		/// The nesting level: 1 for top-level entries.
		/// </summary>
		public int Level { get; private set; }

		/// <summary>
		/// The 1-based number of the target page, or 0 when the entry doesn't point to a page.
		/// </summary>
		public int PageNumber { get; private set; }

		/// <summary>
		/// The vertical position of the target on the page, from the top, in points; null when not specified.
		/// </summary>
		public double? Top { get; private set; }

		/// <summary>
		/// The target URI when the entry points to a web address.
		/// </summary>
		public string Uri { get; private set; }

		/// <summary>
		/// The child entries.
		/// </summary>
		public IReadOnlyList<PdfOutline> Children { get; private set; }

		#endregion

		#region Loading

		/// <summary>
		/// Loads the outline tree of <paramref name="document"/>.
		/// </summary>
		/// <param name="document">The document.</param>
		/// <returns>The top-level entries.</returns>
		internal static IReadOnlyList<PdfOutline> Load(PdfDocument document)
		{
			var root = document.Catalog.GetDictionary(PdfNames.Outlines);
			if (root == null)
				return new PdfOutline[0];

			var destinations = new PdfDestinations(document);
			var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
			return LoadLevel(root.GetDictionary(PdfNames.First), 1, destinations, visited);
		}

		/// <summary>
		/// Loads a chain of sibling outline items, following <c>/Next</c>, and their children recursively.
		/// </summary>
		/// <param name="item">The first item of the chain; can be null.</param>
		/// <param name="level">The nesting level of the chain; items deeper than 32 levels are ignored.</param>
		/// <param name="destinations">The resolver of the item destinations and actions.</param>
		/// <param name="visited">The items already loaded, used to break cycles.</param>
		/// <returns>The loaded entries.</returns>
		private static List<PdfOutline> LoadLevel(PdfDictionary item, int level, PdfDestinations destinations, HashSet<PdfDictionary> visited)
		{
			var list = new List<PdfOutline>();
			while (item != null && level <= 32 && visited.Add(item))
			{
				var outline = new PdfOutline
				{
					Title = (item.GetText(PdfNames.Title) ?? string.Empty).Trim(),
					Level = level,
				};

				var target = destinations.Resolve(item.Resolve(PdfNames.Dest), item.GetDictionary(PdfNames.A));
				outline.PageNumber = target.PageNumber;
				outline.Top = target.Top;
				outline.Uri = target.Uri;
				outline.Children = LoadLevel(item.GetDictionary(PdfNames.First), level + 1, destinations, visited);

				list.Add(outline);
				item = item.GetDictionary(PdfNames.Next);
			}

			return list;
		}

		#endregion

		/// <inheritdoc/>
		public override string ToString() => $"{new string(' ', (this.Level - 1) * 2)}{this.Title} (p. {this.PageNumber})";
	}

	/// <summary>
	/// The resolved target of a destination or action.
	/// </summary>
	internal struct PdfTarget
	{
		/// <summary>The 1-based page number, or 0.</summary>
		public int PageNumber;

		/// <summary>The vertical position on the page from the top, in points, or null.</summary>
		public double? Top;

		/// <summary>The URI of a URI action, or null.</summary>
		public string Uri;
	}

	/// <summary>
	/// Resolves explicit and named destinations, and GoTo/URI actions.
	/// </summary>
	internal sealed class PdfDestinations
	{
		/// <summary>The document that contains the destinations.</summary>
		private readonly PdfDocument _document;

		/// <summary>The named destinations, built on first lookup from <c>/Names /Dests</c> and <c>/Dests</c>.</summary>
		private Dictionary<string, object> _named;

		/// <summary>
		/// Initializes a new instance of <see cref="PdfDestinations"/>.
		/// </summary>
		/// <param name="document">The document that contains the destinations.</param>
		public PdfDestinations(PdfDocument document)
		{
			_document = document;
		}

		/// <summary>
		/// Resolves a destination or an action.
		/// </summary>
		/// <param name="destination">The <c>/Dest</c> value: an array, a name or a string; can be null.</param>
		/// <param name="action">The <c>/A</c> action dictionary; can be null.</param>
		/// <returns>The target.</returns>
		public PdfTarget Resolve(object destination, PdfDictionary action)
		{
			if (destination == null && action != null)
			{
				var type = action.GetNameValue(PdfNames.S);
				if (type == "URI")
				{
					var uri = action.GetString(PdfNames.URI);
					return new PdfTarget { Uri = uri?.ToLatin1().Trim() };
				}

				if (type == "GoTo")
					destination = action.Resolve(PdfNames.D);
			}

			return ResolveDestination(destination, 0);
		}

		/// <summary>
		/// Resolves an explicit or named destination (ISO 32000-2, 12.3.2).
		/// </summary>
		/// <param name="destination">An explicit destination array, a name, a string or a dictionary with <c>/D</c>.</param>
		/// <param name="depth">The current indirection depth; chains longer than 8 yield an empty target.</param>
		/// <returns>The target; empty when the destination can't be resolved.</returns>
		private PdfTarget ResolveDestination(object destination, int depth)
		{
			if (depth > 8)
				return default;

			destination = PdfFile.Resolve(_document.File, destination);

			switch (destination)
			{
				case PdfArray array:
					return FromArray(array);

				case PdfName name:
					return ResolveDestination(LookupNamed(name.Value), depth + 1);

				case PdfString text:
					return ResolveDestination(LookupNamed(text.ToLatin1()), depth + 1);

				case PdfDictionary dictionary:
					// a named destination can be a dictionary with a /D entry.
					return ResolveDestination(dictionary.Resolve(PdfNames.D), depth + 1);

				default:
					return default;
			}
		}

		/// <summary>
		/// Resolves an explicit destination array <c>[page /Kind ...]</c>.
		/// </summary>
		/// <param name="array">The destination array.</param>
		/// <returns>The target, with <see cref="PdfTarget.Top"/> converted to top-down page coordinates when the destination specifies it.</returns>
		private PdfTarget FromArray(PdfArray array)
		{
			var target = new PdfTarget();
			if (array.Count == 0)
				return target;

			// the first element is a page dictionary, or a page index in remote destinations.
			var first = array[0];
			if (first is int index)
				target.PageNumber = index + 1 <= _document.PageCount ? index + 1 : 0;
			else
				target.PageNumber = _document.GetPageNumber(first);

			if (target.PageNumber == 0)
				return target;

			// [page /XYZ left top zoom] and [page /FitH top] carry a vertical position.
			var kind = array.GetName(1)?.Value;
			double? top = null;
			if (kind == "XYZ" && array.Count > 3 && !(array.Resolve(3) is null))
				top = array.GetNumber(3);
			else if ((kind == "FitH" || kind == "FitBH") && array.Count > 2 && !(array.Resolve(2) is null))
				top = array.GetNumber(2);

			if (top.HasValue)
			{
				var page = _document.Pages[target.PageNumber - 1];
				var x = page.CropBox.Left;
				target.Top = page.DisplayMatrix.Transform(x, top.Value).Y;
			}

			return target;
		}

		/// <summary>
		/// Looks up a named destination.
		/// </summary>
		/// <param name="name">The destination name.</param>
		/// <returns>The destination value, or null when the name is not defined.</returns>
		private object LookupNamed(string name)
		{
			if (_named == null)
			{
				_named = new Dictionary<string, object>(System.StringComparer.Ordinal);

				// PDF 1.2+: name tree in /Names /Dests.
				var tree = _document.Catalog.GetDictionary(PdfNames.Names)?.GetDictionary(PdfNames.Dests);
				if (tree != null)
					PdfNameTree.Collect(tree, (key, value) => _named[key] = value);

				// PDF 1.1: dictionary in /Dests.
				var dests = _document.Catalog.GetDictionary(PdfNames.Dests);
				if (dests != null)
				{
					foreach (var entry in dests)
					{
						if (!_named.ContainsKey(entry.Key.Value))
							_named[entry.Key.Value] = entry.Value;
					}
				}
			}

			return _named.TryGetValue(name, out var value) ? value : null;
		}
	}
}
