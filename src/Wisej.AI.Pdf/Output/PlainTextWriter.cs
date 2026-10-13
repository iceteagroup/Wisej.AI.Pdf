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

// Plain text output.

using System.Text;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Writes <see cref="PdfContent"/> as plain text.
	/// </summary>
	internal static class PlainTextWriter
	{
		/// <summary>
		/// Writes the content as plain text: blocks separated by blank lines, tables as tab-separated rows.
		/// </summary>
		/// <param name="content">The content.</param>
		/// <param name="includePageBreaks">True to separate pages with a form feed.</param>
		/// <returns>The text.</returns>
		public static string Write(PdfContent content, bool includePageBreaks)
		{
			var sb = new StringBuilder();
			for (int p = 0; p < content.Pages.Count; p++)
			{
				if (p > 0 && includePageBreaks)
					sb.Append('\f');

				foreach (var block in content.Pages[p].Blocks)
				{
					if (block.Type == PdfBlockType.PageHeader || block.Type == PdfBlockType.PageFooter)
						continue;

					string text;
					switch (block)
					{
						case PdfTable table:
							text = (table.Caption != null ? table.Caption + "\n" : string.Empty) + table.Text;
							break;

						case PdfFigure figure:
							text = figure.Caption;
							break;

						default:
							text = block.Text;
							break;
					}

					if (string.IsNullOrWhiteSpace(text))
						continue;

					if (sb.Length > 0 && sb[sb.Length - 1] != '\f')
						sb.Append("\n\n");

					sb.Append(text.Trim());
				}
			}

			return sb.ToString() + "\n";
		}
	}
}
