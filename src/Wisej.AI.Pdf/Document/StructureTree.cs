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

// Logical structure of tagged PDF documents (ISO 32000-2, 14.7): maps
// marked content to structure elements and their standard roles.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Provides the structure element and role of marked content in tagged documents.
	/// </summary>
	/// <remarks>
	/// Content is linked to the structure tree through the <c>/StructParents</c> key of its content stream
	/// (page, form XObject or annotation) and its marked-content identifier (MCID). The parent tree maps the
	/// pair to the structure element; custom roles are mapped to standard ones through the role map.
	/// </remarks>
	internal sealed class StructureTree
	{
		// one instance per document.
		private static readonly ConditionalWeakTable<PdfDocument, StructureTree> _instances = new ConditionalWeakTable<PdfDocument, StructureTree>();

		/// <summary>The file used to resolve indirect references.</summary>
		private readonly PdfFile _file;

		/// <summary>The flattened <c>/ParentTree</c> number tree: <c>/StructParents</c> key to an array of structure elements indexed by MCID.</summary>
		private readonly Dictionary<int, object> _parentTree;

		/// <summary>The <c>/RoleMap</c> that maps custom structure types to standard ones, or null.</summary>
		private readonly PdfDictionary _roleMap;

		/// <summary>
		/// Initializes the structure tree from the structure tree root.
		/// </summary>
		/// <param name="document">The tagged document.</param>
		/// <param name="root">The <c>/StructTreeRoot</c> dictionary.</param>
		private StructureTree(PdfDocument document, PdfDictionary root)
		{
			_file = document.File;
			_parentTree = PdfNumberTree.ToDictionary(root.GetDictionary(PdfNames.ParentTree));
			_roleMap = root.GetDictionary(PdfNames.RoleMap);
		}

		/// <summary>
		/// Returns the structure tree of <paramref name="document"/>.
		/// </summary>
		/// <param name="document">The document.</param>
		/// <returns>The structure tree, or null when the document is not tagged.</returns>
		public static StructureTree Get(PdfDocument document)
		{
			var root = document.Catalog.GetDictionary(PdfNames.StructTreeRoot);
			if (root == null)
				return null;

			return _instances.GetValue(document, d => new StructureTree(d, root));
		}

		#region Methods

		/// <summary>
		/// Returns the structure element that owns marked content.
		/// </summary>
		/// <param name="structParent">The /StructParents key of the content stream.</param>
		/// <param name="mcid">The marked-content identifier.</param>
		/// <returns>The structure element dictionary, or null.</returns>
		public PdfDictionary GetElement(int structParent, int mcid)
		{
			if (structParent < 0 || mcid < 0 || !_parentTree.TryGetValue(structParent, out var value))
				return null;

			var array = PdfFile.Resolve(_file, value) as PdfArray;
			return array?.GetDictionary(mcid);
		}

		/// <summary>
		/// Returns the standard role of a structure element, following the role map.
		/// </summary>
		/// <param name="element">The structure element.</param>
		/// <returns>The standard structure type, for example "P", "H2" or "Figure".</returns>
		public string GetRole(PdfDictionary element)
		{
			var role = element?.GetNameValue(PdfNames.S);
			for (int i = 0; i < 8 && role != null && _roleMap != null; i++)
			{
				var mapped = _roleMap.GetNameValue(PdfName.Get(role));
				if (mapped == null || mapped == role)
					break;

				role = mapped;
			}

			return role;
		}

		/// <summary>
		/// Finds the nearest ancestor (or the element itself) whose role satisfies <paramref name="predicate"/>.
		/// </summary>
		/// <param name="element">The starting element.</param>
		/// <param name="predicate">The test applied to the standard role of each element.</param>
		/// <returns>The element, or null.</returns>
		public PdfDictionary FindAncestor(PdfDictionary element, Func<string, bool> predicate)
		{
			for (int depth = 0; element != null && depth < 64; depth++)
			{
				var role = GetRole(element);
				if (role != null && predicate(role))
					return element;

				element = element.GetDictionary(PdfNames.P);
				if (element != null && element.IsType("StructTreeRoot"))
					return null;
			}

			return null;
		}

		/// <summary>
		/// Returns the heading level (1-6) of the element that owns marked content, or 0 when it isn't a heading.
		/// </summary>
		/// <param name="structParent">The /StructParents key of the content stream.</param>
		/// <param name="mcid">The marked-content identifier.</param>
		/// <returns>The heading level, 7 for the generic "H" role, -1 for the document title, or 0.</returns>
		public int GetHeadingLevel(int structParent, int mcid)
		{
			var element = FindAncestor(GetElement(structParent, mcid), role => IsHeadingRole(role) || role == "Title");
			if (element == null)
				return 0;

			// the document title is reported as level 0 by convention of the callers: -1.
			var role = GetRole(element);
			if (role == "Title")
				return -1;

			return role.Length == 2 && role[1] >= '1' && role[1] <= '6' ? role[1] - '0' : 7;
		}

		/// <summary>
		/// Returns the alternate text of the figure that owns marked content.
		/// </summary>
		/// <param name="structParent">The /StructParents key of the content stream.</param>
		/// <param name="mcid">The marked-content identifier.</param>
		/// <returns>The alternate text (or actual text), or null.</returns>
		public string GetFigureAltText(int structParent, int mcid)
		{
			var element = FindAncestor(GetElement(structParent, mcid), role => role == "Figure" || role == "Formula");
			if (element == null)
				return null;

			var text = element.GetText(PdfNames.Alt) ?? element.GetText(PdfNames.ActualText);
			return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
		}

		/// <summary>
		/// Returns true when the standard role is a heading.
		/// </summary>
		/// <param name="role">The standard role.</param>
		/// <returns>True for H and H1 to H6.</returns>
		public static bool IsHeadingRole(string role)
		{
			return role == "H" || (role.Length == 2 && role[0] == 'H' && role[1] >= '1' && role[1] <= '6');
		}

		#endregion
	}
}
