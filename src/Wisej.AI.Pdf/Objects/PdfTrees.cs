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

// Name trees and number trees (ISO 32000-2, 7.9.6 and 7.9.7).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Objects
{
	/// <summary>
	/// Reads PDF number trees: sorted maps from integers to objects.
	/// </summary>
	internal static class PdfNumberTree
	{
		/// <summary>
		/// Calls <paramref name="action"/> for each entry of the number tree rooted at <paramref name="root"/>.
		/// </summary>
		/// <param name="root">The root node of the tree.</param>
		/// <param name="action">Receives each key and its unresolved value.</param>
		public static void Collect(PdfDictionary root, Action<int, object> action)
		{
			var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
			var pending = new Stack<PdfDictionary>();
			pending.Push(root);

			while (pending.Count > 0)
			{
				var node = pending.Pop();
				if (node == null || !visited.Add(node))
					continue;

				var nums = node.GetArray(PdfNames.Nums);
				if (nums != null)
				{
					for (int i = 0; i + 1 < nums.Count; i += 2)
						action(nums.GetInt(i), nums[i + 1]);
				}

				var kids = node.GetArray(PdfNames.Kids);
				if (kids != null)
				{
					for (int i = kids.Count - 1; i >= 0; i--)
						pending.Push(kids.GetDictionary(i));
				}
			}
		}

		/// <summary>
		/// Loads all the entries of a number tree in a dictionary.
		/// </summary>
		/// <param name="root">The root node of the tree.</param>
		/// <returns>The entries with unresolved values.</returns>
		public static Dictionary<int, object> ToDictionary(PdfDictionary root)
		{
			var result = new Dictionary<int, object>();
			if (root != null)
				Collect(root, (key, value) => result[key] = value);

			return result;
		}
	}

	/// <summary>
	/// Reads PDF name trees: sorted maps from strings to objects.
	/// </summary>
	internal static class PdfNameTree
	{
		/// <summary>
		/// Calls <paramref name="action"/> for each entry of the name tree rooted at <paramref name="root"/>.
		/// </summary>
		/// <param name="root">The root node of the tree.</param>
		/// <param name="action">Receives each key (as a Latin-1 string of the key bytes) and its unresolved value.</param>
		public static void Collect(PdfDictionary root, Action<string, object> action)
		{
			var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
			var pending = new Stack<PdfDictionary>();
			pending.Push(root);

			while (pending.Count > 0)
			{
				var node = pending.Pop();
				if (node == null || !visited.Add(node))
					continue;

				var names = node.GetArray(PdfNames.Names);
				if (names != null)
				{
					for (int i = 0; i + 1 < names.Count; i += 2)
					{
						var key = names.Resolve(i);
						if (key is PdfString s)
							action(s.ToLatin1(), names[i + 1]);
						else if (key is PdfName n)
							action(n.Value, names[i + 1]);
					}
				}

				var kids = node.GetArray(PdfNames.Kids);
				if (kids != null)
				{
					for (int i = kids.Count - 1; i >= 0; i--)
						pending.Push(kids.GetDictionary(i));
				}
			}
		}

		/// <summary>
		/// Loads all the entries of a name tree in a dictionary.
		/// </summary>
		/// <param name="root">The root node of the tree.</param>
		/// <returns>The entries with unresolved values.</returns>
		public static Dictionary<string, object> ToDictionary(PdfDictionary root)
		{
			var result = new Dictionary<string, object>(StringComparer.Ordinal);
			if (root != null)
				Collect(root, (key, value) => result[key] = value);

			return result;
		}
	}
}
