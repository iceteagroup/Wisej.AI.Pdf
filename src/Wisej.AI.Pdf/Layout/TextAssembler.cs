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

// Assembles the text of blocks: joins lines, removes end-of-line hyphenation
// and produces styled runs.

using System;
using System.Collections.Generic;
using System.Text;

namespace Wisej.AI.Pdf.Layout
{
	/// <summary>
	/// A span of text with uniform style.
	/// </summary>
	internal sealed class StyledRun
	{
		/// <summary>The text.</summary>
		public string Text;

		/// <summary>True when bold.</summary>
		public bool Bold;

		/// <summary>True when italic.</summary>
		public bool Italic;

		/// <summary>True when monospaced.</summary>
		public bool Monospace;

		/// <summary>True when superscript.</summary>
		public bool Superscript;

		/// <summary>True when subscript.</summary>
		public bool Subscript;

		/// <summary>The link URI, or null.</summary>
		public string LinkUri;

		/// <summary>The internal link page, or 0.</summary>
		public int LinkPage;

		/// <summary>The font size.</summary>
		public double FontSize;

		/// <summary>The font name.</summary>
		public string FontName;

		/// <summary>The colour as 0xRRGGBB.</summary>
		public int Color;

		/// <summary>
		/// Returns true when <paramref name="other"/> has the same style.
		/// </summary>
		/// <param name="other">The run to compare.</param>
		/// <returns>True when the styles match.</returns>
		public bool SameStyle(StyledRun other)
		{
			return this.Bold == other.Bold && this.Italic == other.Italic && this.Monospace == other.Monospace
				&& this.Superscript == other.Superscript && this.Subscript == other.Subscript
				&& this.LinkUri == other.LinkUri && this.LinkPage == other.LinkPage;
		}

		/// <inheritdoc/>
		public override string ToString() => this.Text;
	}

	/// <summary>
	/// Joins lines into running text.
	/// </summary>
	internal static class TextAssembler
	{
		/// <summary>
		/// Joins the lines of a block with spaces, removing end-of-line hyphenation.
		/// </summary>
		/// <param name="lines">The lines, top to bottom.</param>
		/// <param name="vocabulary">The words of the document in lower case, used to tell hyphenated words from compounds; can be null.</param>
		/// <returns>The text.</returns>
		public static string JoinLines(IList<TextLine> lines, ISet<string> vocabulary = null)
		{
			var sb = new StringBuilder();
			foreach (var run in GetRuns(lines, false, vocabulary))
				sb.Append(run.Text);

			return sb.ToString();
		}

		/// <summary>
		/// Returns the styled runs of a sequence of lines, joined as running text.
		/// </summary>
		/// <param name="lines">The lines, top to bottom.</param>
		/// <param name="preserveLineBreaks">True to keep line breaks (code blocks, addresses) instead of joining with spaces.</param>
		/// <param name="vocabulary">The words of the document in lower case, used to tell hyphenated words from compounds; can be null.</param>
		/// <returns>The runs.</returns>
		public static List<StyledRun> GetRuns(IList<TextLine> lines, bool preserveLineBreaks = false, ISet<string> vocabulary = null)
		{
			var runs = new List<StyledRun>();
			StyledRun current = null;
			var sb = new StringBuilder();

			// code keeps its indentation: the offset of each line from the leftmost line, in characters.
			double codeLeft = double.MaxValue, charWidth = 0;
			if (preserveLineBreaks)
			{
				double width = 0;
				var chars = 0;
				foreach (var line in lines)
				{
					codeLeft = Math.Min(codeLeft, line.Left);
					width += line.Bounds.Width;
					chars += line.Text.Length;
				}

				charWidth = chars > 0 ? width / chars : 0;
			}

			for (int l = 0; l < lines.Count; l++)
			{
				var line = lines[l];
				for (int w = 0; w < line.Words.Count; w++)
				{
					var word = line.Words[w];
					var text = word.Text;

					// the separator before this word: a space within the line, a space or nothing between lines.
					string separator = null;
					if (w > 0)
					{
						separator = line.Words[w - 1].SpaceAfter ? " " : null;
					}
					else if (l == 0 && preserveLineBreaks)
					{
						var indentation = Indentation(line, codeLeft, charWidth);
						separator = indentation.Length > 0 ? indentation : null;
					}
					else if (l > 0 && (sb.Length > 0 || runs.Count > 0))
					{
						if (preserveLineBreaks)
						{
							// blank lines inside code are kept.
							var blank = line.Top - lines[l - 1].Bottom > lines[l - 1].FontSize * 0.8;
							separator = (blank ? "\n\n" : "\n") + Indentation(line, codeLeft, charWidth);
						}
						else if (EndsWithHyphenation(lines[l - 1], text, vocabulary))
						{
							// remove the hyphen that was added at the end of the previous line.
							RemoveTrailingHyphen(sb, runs);
							separator = null;
						}
						else
						{
							// a kept hyphen at the end of the line joins the halves of a compound: no space.
							separator = EndsWithSpace(sb, runs) || EndsWithKeptHyphen(lines[l - 1]) ? null : " ";
						}
					}

					var run = CreateRun(word);
					if (current == null || !current.SameStyle(run))
					{
						// a separating space keeps the style of the previous run (so "**bold** text" doesn't swallow it).
						if (current != null)
						{
							if (separator != null)
							{
								sb.Append(separator);
								separator = null;
							}

							current.Text = sb.ToString();
							runs.Add(current);
						}

						sb.Clear();
						current = run;
					}

					if (separator != null)
						sb.Append(separator);

					sb.Append(text);
				}
			}

			if (current != null)
			{
				current.Text = sb.ToString();
				runs.Add(current);
			}

			return runs;
		}

		// leading spaces that reproduce the horizontal offset of a code line.
		private static string Indentation(TextLine line, double left, double charWidth)
		{
			if (charWidth <= 0)
				return string.Empty;

			var count = (int)Math.Round((line.Left - left) / charWidth);
			return count > 0 ? new string(' ', Math.Min(count, 80)) : string.Empty;
		}

		// true when the line ends with a hyphen after a letter (a compound split by the line break).
		private static bool EndsWithKeptHyphen(TextLine line)
		{
			if (line.Words.Count == 0)
				return false;

			var last = line.Words[line.Words.Count - 1].Text;
			return last.Length >= 2 && (last[last.Length - 1] == '-' || last[last.Length - 1] == '‐') && char.IsLetter(last[last.Length - 2]);
		}

		// true when the text emitted so far ends with a space.
		private static bool EndsWithSpace(StringBuilder sb, List<StyledRun> runs)
		{
			if (sb.Length > 0)
				return sb[sb.Length - 1] == ' ';

			if (runs.Count > 0)
			{
				var text = runs[runs.Count - 1].Text;
				return text.Length > 0 && text[text.Length - 1] == ' ';
			}

			return false;
		}

		// a run with the style of the word and no text yet.
		private static StyledRun CreateRun(TextWord word)
		{
			var glyph = word.First;
			return new StyledRun
			{
				Bold = word.IsBold,
				Italic = word.IsItalic,
				Monospace = word.IsMonospace,
				Superscript = word.IsSuperscript,
				Subscript = word.IsSubscript,
				LinkUri = word.LinkUri,
				LinkPage = word.LinkPage,
				FontSize = word.FontSize,
				FontName = glyph.Font?.BaseFont,
				Color = glyph.Color,
			};
		}

		/// <summary>
		/// Returns true when the previous line ends with a hyphen that splits a word continued by <paramref name="nextWord"/>.
		/// </summary>
		/// <param name="previous">The previous line.</param>
		/// <param name="nextWord">The first word of the next line.</param>
		/// <param name="vocabulary">The words of the document in lower case; can be null.</param>
		/// <returns>True when the hyphen must be removed.</returns>
		/// <remarks>
		/// A hyphen at the end of a line is either a typographic hyphenation ("transduc-tion") or part of
		/// a compound ("sequence-aligned"). The words of the document decide: the joined word or the two
		/// halves appearing elsewhere in the text indicate which one it is.
		/// </remarks>
		public static bool EndsWithHyphenation(TextLine previous, string nextWord, ISet<string> vocabulary = null)
		{
			if (previous.Words.Count == 0 || string.IsNullOrEmpty(nextWord))
				return false;

			var last = previous.Words[previous.Words.Count - 1].Text;
			if (last.Length < 3)
				return false;

			var hyphen = last[last.Length - 1];
			if (hyphen != '-' && hyphen != '\u00AD' && hyphen != '\u2010')
				return false;

			// a soft hyphen is always a hyphenation point.
			if (hyphen == '\u00AD')
				return true;

			// the hyphen follows a letter, and the next line continues with a lowercase letter.
			if (!char.IsLetter(last[last.Length - 2]) || !char.IsLower(nextWord[0]))
				return false;

			// chains of compounds ("English-to-German") keep their hyphens.
			var fragment = last.Substring(0, last.Length - 1);
			if (fragment.IndexOf('-') >= 0 || nextWord.IndexOf('-') >= 0)
				return false;

			if (vocabulary != null)
			{
				var head = NormalizeWord(fragment);
				var tail = NormalizeWord(nextWord);

				if (vocabulary.Contains(head + tail))
					return true;
				if (vocabulary.Contains(head + "-" + tail))
					return false;

				// two real words: a compound.
				if (head.Length >= 3 && tail.Length >= 3 && vocabulary.Contains(head) && vocabulary.Contains(tail))
					return false;
			}

			return true;
		}

		/// <summary>
		/// Normalizes a word for vocabulary lookups: lower case without surrounding punctuation.
		/// </summary>
		/// <param name="word">The word.</param>
		/// <returns>The normalized word.</returns>
		public static string NormalizeWord(string word)
		{
			var start = 0;
			var end = word.Length;
			while (start < end && !char.IsLetterOrDigit(word[start]))
				start++;
			while (end > start && !char.IsLetterOrDigit(word[end - 1]))
				end--;

			return word.Substring(start, end - start).ToLowerInvariant();
		}

		// removes the last character emitted (the hyphen at the end of the previous line).
		private static void RemoveTrailingHyphen(StringBuilder sb, List<StyledRun> runs)
		{
			if (sb.Length > 0)
			{
				sb.Length--;
			}
			else if (runs.Count > 0)
			{
				var last = runs[runs.Count - 1];
				if (last.Text.Length > 0)
					last.Text = last.Text.Substring(0, last.Text.Length - 1);
			}
		}
	}
}
