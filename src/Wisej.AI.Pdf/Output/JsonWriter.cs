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

// JSON output (dependency-free writer).

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Writes <see cref="PdfContent"/> as JSON.
	/// </summary>
	/// <remarks>
	/// The schema is stable and documented by the public model: each block has a <c>type</c>,
	/// <c>page</c>, <c>bounds</c> ([left, top, right, bottom] in points) and type-specific properties.
	/// Image data is not included; images are referenced by <c>id</c> and listed with their metadata.
	/// </remarks>
	internal sealed class JsonWriter
	{
		// the output.
		private readonly StringBuilder _sb = new StringBuilder();
		// true to indent the output.
		private readonly bool _indented;
		// the current nesting depth, for indentation.
		private int _depth;
		// true before the first member of the current object or array.
		private bool _first = true;

		// true right after a property name: the next value must not be preceded by a comma.
		private bool _pendingValue;

		private JsonWriter(bool indented)
		{
			_indented = indented;
		}

		/// <summary>
		/// Writes the content as JSON.
		/// </summary>
		/// <param name="content">The content.</param>
		/// <param name="indented">True to indent the output.</param>
		/// <returns>The JSON text.</returns>
		public static string Write(PdfContent content, bool indented)
		{
			var w = new JsonWriter(indented);
			w.BeginObject();

			if (content.Info != null)
			{
				w.Name("info");
				w.BeginObject();
				w.Property("title", content.Info.Title);
				w.Property("author", content.Info.Author);
				w.Property("subject", content.Info.Subject);
				w.Property("keywords", content.Info.Keywords);
				w.Property("creator", content.Info.Creator);
				w.Property("producer", content.Info.Producer);
				w.Property("created", content.Info.CreationDate?.ToString("o", CultureInfo.InvariantCulture));
				w.Property("modified", content.Info.ModificationDate?.ToString("o", CultureInfo.InvariantCulture));
				w.EndObject();
			}

			w.Property("pageCount", content.PageCount);

			w.Name("pages");
			w.BeginArray();
			foreach (var page in content.Pages)
			{
				w.BeginObject();
				w.Property("number", page.Number);
				w.Property("label", page.Label);
				w.Property("width", page.Width);
				w.Property("height", page.Height);
				w.Name("blocks");
				w.BeginArray();
				foreach (var block in page.Blocks)
					w.WriteBlock(block);
				w.EndArray();
				w.EndObject();
			}
			w.EndArray();

			w.Name("images");
			w.BeginArray();
			foreach (var image in content.Images)
			{
				w.BeginObject();
				w.Property("id", image.Id);
				w.Property("fileName", image.FileName);
				w.Property("mimeType", image.MimeType);
				w.Property("kind", image.Kind.ToString());
				w.Property("width", image.Width);
				w.Property("height", image.Height);
				w.Property("page", image.PageNumber);
				w.Bounds(image.Bounds);
				w.Property("caption", image.Caption);
				w.Property("altText", image.AltText);
				w.EndObject();
			}
			w.EndArray();

			w.EndObject();
			return w._sb.ToString();
		}

		#region Blocks

		/// <summary>
		/// Writes a block with its type-specific properties.
		/// </summary>
		/// <param name="block">The block.</param>
		private void WriteBlock(PdfBlock block)
		{
			BeginObject();
			Property("type", char.ToLowerInvariant(block.Type.ToString()[0]) + block.Type.ToString().Substring(1));
			Property("page", block.PageNumber);
			Bounds(block.Bounds);

			switch (block)
			{
				case PdfTextBlock text:
					if (text.Level > 0)
						Property("level", text.Level);
					Property("text", text.Text);
					Runs(text.Runs);
					break;

				case PdfList list:
					Property("ordered", list.IsOrdered);
					Name("items");
					BeginArray();
					foreach (var item in list.Items)
					{
						BeginObject();
						Property("marker", item.Marker);
						Property("depth", item.Depth);
						Property("text", item.Text);
						Runs(item.Runs);
						EndObject();
					}
					EndArray();
					break;

				case PdfTable table:
					Property("rows", table.RowCount);
					Property("columns", table.ColumnCount);
					Property("headerRows", table.HeaderRowCount);
					Property("caption", table.Caption);
					Name("cells");
					BeginArray();
					foreach (var cell in table.Cells)
					{
						BeginObject();
						Property("row", cell.Row);
						Property("column", cell.Column);
						if (cell.RowSpan > 1)
							Property("rowSpan", cell.RowSpan);
						if (cell.ColumnSpan > 1)
							Property("columnSpan", cell.ColumnSpan);
						if (cell.IsHeader)
							Property("header", true);
						Property("text", cell.Text);
						EndObject();
					}
					EndArray();
					break;

				case PdfFigure figure:
					Property("image", figure.Image?.Id);
					Property("vector", figure.IsVector);
					Property("caption", figure.Caption);
					Property("altText", figure.AltText);
					Property("labels", figure.Labels);
					break;
			}

			EndObject();
		}

		/// <summary>
		/// Writes the formatted runs, only when some run carries formatting.
		/// </summary>
		/// <param name="runs">The runs.</param>
		private void Runs(IReadOnlyList<PdfTextRun> runs)
		{
			// runs are only written when they carry formatting, to keep the output compact.
			var formatted = false;
			foreach (var run in runs)
			{
				if (run.IsBold || run.IsItalic || run.IsMonospace || run.IsSuperscript || run.IsSubscript || run.LinkUri != null)
				{
					formatted = true;
					break;
				}
			}

			if (!formatted)
				return;

			Name("runs");
			BeginArray();
			foreach (var run in runs)
			{
				BeginObject();
				Property("text", run.Text);
				if (run.IsBold)
					Property("bold", true);
				if (run.IsItalic)
					Property("italic", true);
				if (run.IsMonospace)
					Property("monospace", true);
				if (run.IsSuperscript)
					Property("superscript", true);
				if (run.IsSubscript)
					Property("subscript", true);
				if (run.LinkUri != null)
					Property("link", run.LinkUri);
				EndObject();
			}
			EndArray();
		}

		/// <summary>
		/// Writes the bounds property as [left, top, right, bottom].
		/// </summary>
		/// <param name="bounds">The bounds.</param>
		private void Bounds(PdfRect bounds)
		{
			Name("bounds");
			_sb.Append('[')
				.Append(Number(bounds.Left)).Append(',')
				.Append(Number(bounds.Top)).Append(',')
				.Append(Number(bounds.Right)).Append(',')
				.Append(Number(bounds.Bottom)).Append(']');

			_pendingValue = false;
			_first = false;
		}

		#endregion

		#region Primitives

		// writes '{'.
		private void BeginObject()
		{
			Separator();
			_sb.Append('{');
			_depth++;
			_first = true;
		}

		// writes '}'.
		private void EndObject()
		{
			_depth--;
			NewLine();
			_sb.Append('}');
			_first = false;
		}

		// writes '['.
		private void BeginArray()
		{
			Separator();
			_sb.Append('[');
			_depth++;
			_first = true;
		}

		// writes ']'.
		private void EndArray()
		{
			_depth--;
			NewLine();
			_sb.Append(']');
			_first = false;
		}

		// writes a property name; the value follows without a separator.
		private void Name(string name)
		{
			Separator();
			String(name);
			_sb.Append(_indented ? ": " : ":");
			_first = true;
			_pendingValue = true;
		}

		// writes a string property; null values are omitted.
		private void Property(string name, string value)
		{
			if (value == null)
				return;

			Name(name);
			String(value);
			_pendingValue = false;
			_first = false;
		}

		// writes an integer property.
		private void Property(string name, int value)
		{
			Name(name);
			_sb.Append(value.ToString(CultureInfo.InvariantCulture));
			_pendingValue = false;
			_first = false;
		}

		// writes a number property.
		private void Property(string name, double value)
		{
			Name(name);
			_sb.Append(Number(value));
			_pendingValue = false;
			_first = false;
		}

		// writes a boolean property.
		private void Property(string name, bool value)
		{
			Name(name);
			_sb.Append(value ? "true" : "false");
			_pendingValue = false;
			_first = false;
		}

		// writes the comma and new line that precede a value, except right after a property name.
		private void Separator()
		{
			if (_pendingValue)
			{
				_pendingValue = false;
				return;
			}

			if (!_first)
				_sb.Append(',');

			NewLine();
			_first = false;
		}

		// starts a new indented line when indenting.
		private void NewLine()
		{
			if (!_indented || _sb.Length == 0)
				return;

			_sb.Append('\n').Append(' ', _depth * 2);
		}

		// formats a number with up to two decimals.
		private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

		// true when the surrogate at the index is part of a valid pair (unpaired surrogates make invalid JSON).
		private static bool IsValidSurrogate(string text, int index)
		{
			var c = text[index];
			if (char.IsHighSurrogate(c))
				return index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);

			return index > 0 && char.IsHighSurrogate(text[index - 1]);
		}

		// writes a JSON string literal with escapes.
		private void String(string value)
		{
			_sb.Append('"');
			for (int i = 0; i < value.Length; i++)
			{
				var c = value[i];
				switch (c)
				{
					case '"': _sb.Append("\\\""); break;
					case '\\': _sb.Append("\\\\"); break;
					case '\n': _sb.Append("\\n"); break;
					case '\r': _sb.Append("\\r"); break;
					case '\t': _sb.Append("\\t"); break;
					case '\b': _sb.Append("\\b"); break;
					case '\f': _sb.Append("\\f"); break;
					default:
						if (char.IsSurrogate(c) && !IsValidSurrogate(value, i))
							_sb.Append("\ufffd");
						else if (c < 0x20)
							_sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
						else
							_sb.Append(c);
						break;
				}
			}

			_sb.Append('"');
		}

		#endregion
	}
}
