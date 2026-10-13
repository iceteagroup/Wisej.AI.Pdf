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

// Recognizes list item markers: bullets and enumerators.

using System;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// Recognizes bullets ("•", "–", Wingdings bullets) and enumerators ("1.", "a)", "(iv)") at the start of lines.
	/// </summary>
	internal static class ListMarkers
	{
		// bullet characters, including the private-use code points of the Symbol and Wingdings fonts.
		private const string Bullets = "•◦▪▫■□●○◆◇►▸▹‣⁃–—-*✓✔✗✘➢➤➔→⇒·∙⦿⦾❖❑❒";

		/// <summary>
		/// Returns the list marker that starts <paramref name="line"/>, or null.
		/// </summary>
		/// <param name="line">The line.</param>
		/// <returns>The marker text, or null when the line doesn't start a list item.</returns>
		public static string GetMarker(TextLine line)
		{
			// the marker must be followed by text.
			if (line.Words.Count < 2)
				return null;

			var first = line.Words[0];
			if (!IsMarker(first.Text))
			{
				// "o" is the second-level bullet of Word, drawn with Courier New or a symbol font.
				if (first.Text == "o" && IsSymbolOrMonoFont(first))
					return "o";

				return null;
			}

			// a lone hyphen or asterisk must be followed by a space, otherwise it's part of the text.
			if ((first.Text == "-" || first.Text == "*") && !first.SpaceAfter)
				return null;

			return first.Text;
		}

		/// <summary>
		/// Returns true when <paramref name="text"/> is a bullet or an enumerator.
		/// </summary>
		/// <param name="text">The text of the first word of a line.</param>
		/// <returns>True when it's a list marker.</returns>
		public static bool IsMarker(string text)
		{
			if (string.IsNullOrEmpty(text) || text.Length > 8)
				return false;

			if (text.Length == 1 && Bullets.IndexOf(text[0]) >= 0)
				return true;

			return IsEnumerator(text);
		}

		/// <summary>
		/// Returns true when <paramref name="text"/> is a section number such as "100.1", "4.2.3", "§ 12" or "(iv)".
		/// </summary>
		/// <param name="text">The text of a word.</param>
		/// <returns>True when the text is a section label.</returns>
		public static bool IsSectionLabel(string text)
		{
			if (string.IsNullOrEmpty(text) || text.Length > 12)
				return false;

			var body = text.TrimStart('�', '(').TrimEnd('.', ')', ':');
			if (body.Length == 0)
				return false;

			// digits separated by single dots: "100", "100.1", "4.2.3".
			var digits = 0;
			for (int i = 0; i < body.Length; i++)
			{
				var c = body[i];
				if (c >= '0' && c <= '9')
					digits++;
				else if (c != '.' || i == 0 || body[i - 1] == '.')
					return false;
			}

			return digits > 0;
		}

		/// <summary>
		/// Returns true when the marker is an enumerator rather than a bullet.
		/// </summary>
		/// <param name="marker">The marker.</param>
		/// <returns>True for numbered or lettered markers.</returns>
		public static bool IsOrdered(string marker) => marker != null && IsEnumerator(marker);

		// "1." "12)" "(3)" "a." "b)" "(c)" "iv." "(ii)" "A)" "IV."
		private static bool IsEnumerator(string text)
		{
			var start = 0;
			var end = text.Length;
			var parenthesized = false;

			if (text[0] == '(' && text[end - 1] == ')')
			{
				parenthesized = true;
				start = 1;
				end--;
			}
			else if (text[end - 1] == '.' || text[end - 1] == ')')
			{
				end--;
			}
			else
			{
				return false;
			}

			var body = text.Substring(start, end - start);
			if (body.Length == 0)
				return false;

			// numbers up to 3 digits.
			if (body.Length <= 3 && IsDigits(body))
				return true;

			// a single lowercase letter: "a." "b)" "(c)".
			if (body.Length == 1 && body[0] >= 'a' && body[0] <= 'z')
				return true;

			// a single uppercase letter only with a parenthesis: "A." is often an initial.
			if (body.Length == 1 && body[0] >= 'A' && body[0] <= 'Z')
				return parenthesized || text[text.Length - 1] == ')';

			// roman numerals.
			if (body.Length <= 6 && (IsRoman(body, "ivxlcdm") || IsRoman(body, "IVXLCDM")))
				return true;

			return false;
		}

		// true when the text is made of decimal digits only.
		private static bool IsDigits(string text)
		{
			foreach (var c in text)
			{
				if (c < '0' || c > '9')
					return false;
			}

			return true;
		}

		// true when the text is made of the given roman numeral letters only.
		private static bool IsRoman(string text, string letters)
		{
			foreach (var c in text)
			{
				if (letters.IndexOf(c) < 0)
					return false;
			}

			return true;
		}

		// true when the word is drawn with a symbol or monospaced font (Word's "o" bullet).
		private static bool IsSymbolOrMonoFont(TextWord word)
		{
			var name = word.First.Font?.BaseFont ?? string.Empty;
			return word.IsMonospace
				|| name.IndexOf("Courier", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("Wingdings", StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}
}
