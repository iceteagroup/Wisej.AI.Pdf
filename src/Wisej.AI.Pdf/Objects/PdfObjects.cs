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

// Low-level PDF object model.
//
// PDF values are represented with a small set of CLR types to keep the parser
// fast and allocation-light:
//
//   null        -> PdfNull.Instance
//   boolean     -> System.Boolean
//   integer     -> System.Int32 (System.Double when it overflows)
//   real        -> System.Double
//   string      -> PdfString
//   name        -> PdfName (interned)
//   array       -> PdfArray
//   dictionary  -> PdfDictionary
//   stream      -> PdfStream
//   reference   -> PdfReference

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wisej.AI.Pdf.Objects
{
	/// <summary>
	/// Represents a PDF name object, for example <c>/Type</c>.
	/// </summary>
	/// <remarks>
	/// Instances are interned: two names with the same value are always the same instance,
	/// so they can be compared by reference and used as fast dictionary keys.
	/// </remarks>
	public sealed class PdfName : IEquatable<PdfName>
	{
		/// <summary>The interned names, keyed by value; also the lock for its own access.</summary>
		private static readonly Dictionary<string, PdfName> _table = new Dictionary<string, PdfName>(StringComparer.Ordinal);

		/// <summary>
		/// The decoded value of the name, without the leading slash.
		/// </summary>
		public readonly string Value;

		/// <summary>
		/// Initializes a name; use <see cref="Get"/> to obtain the interned instance.
		/// </summary>
		/// <param name="value">The value of the name, without the leading slash.</param>
		private PdfName(string value)
		{
			this.Value = value;
		}

		/// <summary>
		/// Returns the interned <see cref="PdfName"/> for <paramref name="value"/>.
		/// </summary>
		/// <param name="value">The value of the name, without the leading slash.</param>
		/// <returns>The unique instance representing <paramref name="value"/>.</returns>
		public static PdfName Get(string value)
		{
			lock (_table)
			{
				if (!_table.TryGetValue(value, out var name))
				{
					name = new PdfName(value);
					_table[value] = name;
				}

				return name;
			}
		}

		/// <summary>
		/// Converts a string to the corresponding interned <see cref="PdfName"/>.
		/// </summary>
		/// <param name="value">The value of the name, without the leading slash.</param>
		public static implicit operator PdfName(string value) => Get(value);

		/// <summary>
		/// Returns true when <paramref name="other"/> is the same name.
		/// </summary>
		/// <param name="other">The name to compare.</param>
		/// <returns>True when both instances represent the same name.</returns>
		public bool Equals(PdfName other) => ReferenceEquals(this, other);

		/// <inheritdoc/>
		public override bool Equals(object obj) => ReferenceEquals(this, obj);

		/// <inheritdoc/>
		public override int GetHashCode() => this.Value.GetHashCode();

		/// <inheritdoc/>
		public override string ToString() => "/" + this.Value;
	}

	/// <summary>
	/// Represents a PDF string object, either literal <c>(...)</c> or hexadecimal <c>&lt;...&gt;</c>.
	/// </summary>
	/// <remarks>
	/// PDF strings are byte sequences. Their meaning depends on the context: text strings
	/// (see <see cref="ToText"/>), character codes shown with a font, or binary data.
	/// </remarks>
	public sealed class PdfString
	{
		/// <summary>
		/// The raw bytes of the string, already decrypted.
		/// </summary>
		public byte[] Bytes;

		/// <summary>
		/// True when the string was written in hexadecimal form.
		/// </summary>
		public bool IsHex;

		/// <summary>
		/// Initializes a new instance of <see cref="PdfString"/>.
		/// </summary>
		/// <param name="bytes">The raw bytes of the string.</param>
		/// <param name="isHex">True when the string was written in hexadecimal form.</param>
		public PdfString(byte[] bytes, bool isHex = false)
		{
			this.Bytes = bytes;
			this.IsHex = isHex;
		}

		/// <summary>
		/// Decodes the string as a PDF text string.
		/// </summary>
		/// <returns>The decoded text.</returns>
		/// <remarks>
		/// Text strings are encoded in UTF-16BE or UTF-8 when they start with the corresponding
		/// byte order mark, and in PDFDocEncoding otherwise. A UTF-16LE byte order mark is not
		/// allowed by the specification but is accepted because some producers write it.
		/// </remarks>
		public string ToText()
		{
			var b = this.Bytes;

			if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
				return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2).TrimEnd('\0');

			if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
				return Encoding.Unicode.GetString(b, 2, b.Length - 2).TrimEnd('\0');

			if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
				return Encoding.UTF8.GetString(b, 3, b.Length - 3);

			return Fonts.Encodings.PdfDocEncoding.Decode(b);
		}

		/// <summary>
		/// Returns the bytes of the string mapped one-to-one to Latin-1 characters.
		/// </summary>
		/// <returns>A string with one character per byte.</returns>
		public string ToLatin1()
		{
			var chars = new char[this.Bytes.Length];
			for (int i = 0; i < chars.Length; i++)
				chars[i] = (char)this.Bytes[i];

			return new string(chars);
		}

		/// <inheritdoc/>
		public override string ToString() => ToText();
	}

	/// <summary>
	/// Represents an indirect reference to an object, for example <c>12 0 R</c>.
	/// </summary>
	public sealed class PdfReference : IEquatable<PdfReference>
	{
		/// <summary>
		/// The object number.
		/// </summary>
		public readonly int Number;

		/// <summary>
		/// The generation number.
		/// </summary>
		public readonly int Generation;

		/// <summary>
		/// Initializes a new instance of <see cref="PdfReference"/>.
		/// </summary>
		/// <param name="number">The object number.</param>
		/// <param name="generation">The generation number.</param>
		public PdfReference(int number, int generation)
		{
			this.Number = number;
			this.Generation = generation;
		}

		/// <summary>
		/// Returns true when <paramref name="other"/> references the same object.
		/// </summary>
		/// <param name="other">The reference to compare.</param>
		/// <returns>True when both object and generation numbers match.</returns>
		public bool Equals(PdfReference other) => other != null && other.Number == this.Number && other.Generation == this.Generation;

		/// <inheritdoc/>
		public override bool Equals(object obj) => Equals(obj as PdfReference);

		/// <inheritdoc/>
		public override int GetHashCode() => this.Number * 31 + this.Generation;

		/// <inheritdoc/>
		public override string ToString() => $"{this.Number} {this.Generation} R";
	}

	/// <summary>
	/// Represents the PDF <c>null</c> object.
	/// </summary>
	public sealed class PdfNull
	{
		/// <summary>
		/// The only instance of <see cref="PdfNull"/>.
		/// </summary>
		public static readonly PdfNull Instance = new PdfNull();

		/// <summary>
		/// Prevents external instantiation: use <see cref="Instance"/>.
		/// </summary>
		private PdfNull()
		{
		}

		/// <inheritdoc/>
		public override string ToString() => "null";
	}

	/// <summary>
	/// Represents an operator token in a content stream, for example <c>Tj</c> or <c>re</c>.
	/// </summary>
	/// <remarks>
	/// Operators only appear while parsing content streams. Instances are interned so the
	/// interpreter can dispatch on the <see cref="Name"/> without allocating.
	/// </remarks>
	public sealed class PdfOperator
	{
		/// <summary>The interned operators, keyed by keyword; also the lock for its own access.</summary>
		private static readonly Dictionary<string, PdfOperator> _table = new Dictionary<string, PdfOperator>(StringComparer.Ordinal);

		/// <summary>
		/// The operator keyword.
		/// </summary>
		public readonly string Name;

		/// <summary>
		/// Initializes an operator; use <see cref="Get"/> to obtain the interned instance.
		/// </summary>
		/// <param name="name">The operator keyword.</param>
		private PdfOperator(string name)
		{
			this.Name = name;
		}

		/// <summary>
		/// Returns the interned <see cref="PdfOperator"/> for <paramref name="name"/>.
		/// </summary>
		/// <param name="name">The operator keyword.</param>
		/// <returns>The unique instance representing the operator.</returns>
		public static PdfOperator Get(string name)
		{
			lock (_table)
			{
				if (!_table.TryGetValue(name, out var op))
				{
					op = new PdfOperator(name);
					_table[name] = op;
				}

				return op;
			}
		}

		/// <inheritdoc/>
		public override string ToString() => this.Name;
	}

	/// <summary>
	/// Represents a PDF array.
	/// </summary>
	/// <remarks>
	/// Elements may be indirect references. The typed getters resolve references
	/// through the owning <see cref="PdfFile"/>; the indexer returns the raw element.
	/// </remarks>
	public sealed class PdfArray : List<object>
	{
		/// <summary>
		/// The file used to resolve indirect references, or null.
		/// </summary>
		internal PdfFile File;

		/// <summary>
		/// Initializes a new empty <see cref="PdfArray"/>.
		/// </summary>
		public PdfArray()
		{
		}

		/// <summary>
		/// Initializes a new empty <see cref="PdfArray"/> bound to <paramref name="file"/>.
		/// </summary>
		/// <param name="file">The file used to resolve indirect references.</param>
		internal PdfArray(PdfFile file)
		{
			this.File = file;
		}

		/// <summary>
		/// Returns the element at <paramref name="index"/>, resolving indirect references.
		/// </summary>
		/// <param name="index">Zero-based index of the element.</param>
		/// <returns>The resolved element, or null when the index is out of range.</returns>
		public object Resolve(int index)
		{
			if (index < 0 || index >= this.Count)
				return null;

			return PdfFile.Resolve(this.File, this[index]);
		}

		/// <summary>
		/// Returns the element at <paramref name="index"/> as a number.
		/// </summary>
		/// <param name="index">Zero-based index of the element.</param>
		/// <param name="defaultValue">Value returned when the element is missing or not a number.</param>
		/// <returns>The numeric value of the element.</returns>
		public double GetNumber(int index, double defaultValue = 0) => PdfConvert.ToNumber(Resolve(index), defaultValue);

		/// <summary>
		/// Returns the element at <paramref name="index"/> as an integer, rounding reals.
		/// </summary>
		/// <param name="index">Zero-based index of the element.</param>
		/// <param name="defaultValue">Value returned when the element is missing or not a number.</param>
		/// <returns>The integer value of the element.</returns>
		public int GetInt(int index, int defaultValue = 0) => PdfConvert.ToInt(Resolve(index), defaultValue);

		/// <summary>
		/// Returns the element at <paramref name="index"/> as a <see cref="PdfName"/>.
		/// </summary>
		/// <param name="index">Zero-based index of the element.</param>
		/// <returns>The name, or null when the element is not a name.</returns>
		public PdfName GetName(int index) => Resolve(index) as PdfName;

		/// <summary>
		/// Returns the element at <paramref name="index"/> as a <see cref="PdfDictionary"/>.
		/// </summary>
		/// <param name="index">Zero-based index of the element.</param>
		/// <returns>The dictionary (or stream), or null when the element is not a dictionary.</returns>
		public PdfDictionary GetDictionary(int index) => Resolve(index) as PdfDictionary;

		/// <summary>
		/// Returns the element at <paramref name="index"/> as a <see cref="PdfArray"/>.
		/// </summary>
		/// <param name="index">Zero-based index of the element.</param>
		/// <returns>The array, or null when the element is not an array.</returns>
		public PdfArray GetArray(int index) => Resolve(index) as PdfArray;

		/// <summary>
		/// Returns the element at <paramref name="index"/> as a <see cref="PdfStream"/>.
		/// </summary>
		/// <param name="index">Zero-based index of the element.</param>
		/// <returns>The stream, or null when the element is not a stream.</returns>
		public PdfStream GetStream(int index) => Resolve(index) as PdfStream;

		/// <summary>
		/// Returns the element at <paramref name="index"/> as a <see cref="PdfString"/>.
		/// </summary>
		/// <param name="index">Zero-based index of the element.</param>
		/// <returns>The string, or null when the element is not a string.</returns>
		public PdfString GetString(int index) => Resolve(index) as PdfString;

		/// <summary>
		/// Converts all the elements to numbers.
		/// </summary>
		/// <returns>An array with the numeric value of each element; non-numeric elements become 0.</returns>
		public double[] ToNumbers()
		{
			var values = new double[this.Count];
			for (int i = 0; i < values.Length; i++)
				values[i] = GetNumber(i);

			return values;
		}
	}

	/// <summary>
	/// Represents a PDF dictionary.
	/// </summary>
	/// <remarks>
	/// Values may be indirect references. The typed getters resolve references
	/// through the owning <see cref="PdfFile"/>; the indexer returns the raw value.
	/// </remarks>
	public class PdfDictionary : Dictionary<PdfName, object>
	{
		/// <summary>
		/// The file used to resolve indirect references, or null.
		/// </summary>
		internal PdfFile File;

		/// <summary>
		/// Initializes a new empty <see cref="PdfDictionary"/>.
		/// </summary>
		public PdfDictionary()
		{
		}

		/// <summary>
		/// Initializes a new empty <see cref="PdfDictionary"/> bound to <paramref name="file"/>.
		/// </summary>
		/// <param name="file">The file used to resolve indirect references.</param>
		internal PdfDictionary(PdfFile file)
		{
			this.File = file;
		}

		/// <summary>
		/// Returns the value of <paramref name="key"/>, resolving indirect references.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The resolved value, or null when the key is not present.</returns>
		public object Resolve(PdfName key)
		{
			if (key == null || !TryGetValue(key, out var value))
				return null;

			return PdfFile.Resolve(this.File, value);
		}

		/// <summary>
		/// Returns the value of <paramref name="key"/>, or of <paramref name="alternate"/> when
		/// <paramref name="key"/> is missing.
		/// </summary>
		/// <param name="key">The key to look up first.</param>
		/// <param name="alternate">The key to look up when <paramref name="key"/> is missing, for example the abbreviated key of an inline image.</param>
		/// <returns>The resolved value, or null when neither key is present.</returns>
		public object Resolve(PdfName key, PdfName alternate) => Resolve(key) ?? Resolve(alternate);

		/// <summary>
		/// Returns true when the dictionary contains <paramref name="key"/>.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>True when the key is present, even if its value is null.</returns>
		public bool Has(PdfName key) => ContainsKey(key);

		/// <summary>
		/// Returns the value of <paramref name="key"/> as a number.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <param name="defaultValue">Value returned when the key is missing or not a number.</param>
		/// <returns>The numeric value.</returns>
		public double GetNumber(PdfName key, double defaultValue = 0) => PdfConvert.ToNumber(Resolve(key), defaultValue);

		/// <summary>
		/// Returns the value of <paramref name="key"/> as an integer, rounding reals.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <param name="defaultValue">Value returned when the key is missing or not a number.</param>
		/// <returns>The integer value.</returns>
		public int GetInt(PdfName key, int defaultValue = 0) => PdfConvert.ToInt(Resolve(key), defaultValue);

		/// <summary>
		/// Returns the value of <paramref name="key"/> as a boolean.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <param name="defaultValue">Value returned when the key is missing or not a boolean.</param>
		/// <returns>The boolean value.</returns>
		public bool GetBool(PdfName key, bool defaultValue = false) => Resolve(key) is bool b ? b : defaultValue;

		/// <summary>
		/// Returns the value of <paramref name="key"/> as a <see cref="PdfName"/>.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The name, or null when the value is missing or not a name.</returns>
		public PdfName GetName(PdfName key) => Resolve(key) as PdfName;

		/// <summary>
		/// Returns the value of <paramref name="key"/> as a <see cref="PdfDictionary"/>.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The dictionary (or stream), or null when the value is missing or not a dictionary.</returns>
		public PdfDictionary GetDictionary(PdfName key) => Resolve(key) as PdfDictionary;

		/// <summary>
		/// Returns the value of <paramref name="key"/> as a <see cref="PdfArray"/>.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The array, or null when the value is missing or not an array.</returns>
		public PdfArray GetArray(PdfName key) => Resolve(key) as PdfArray;

		/// <summary>
		/// Returns the value of <paramref name="key"/> as a <see cref="PdfStream"/>.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The stream, or null when the value is missing or not a stream.</returns>
		public PdfStream GetStream(PdfName key) => Resolve(key) as PdfStream;

		/// <summary>
		/// Returns the value of <paramref name="key"/> as a <see cref="PdfString"/>.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The string, or null when the value is missing or not a string.</returns>
		public PdfString GetString(PdfName key) => Resolve(key) as PdfString;

		/// <summary>
		/// Returns the value of <paramref name="key"/> decoded as a PDF text string.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The decoded text, or null when the value is missing or not a string.</returns>
		public string GetText(PdfName key) => GetString(key)?.ToText();

		/// <summary>
		/// Returns the value of the name stored in <paramref name="key"/>.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The name value without the slash, or null when the value is missing or not a name.</returns>
		public string GetNameValue(PdfName key) => GetName(key)?.Value;

		/// <summary>
		/// Returns true when the <c>/Type</c> entry equals <paramref name="type"/>.
		/// </summary>
		/// <param name="type">The expected type name, without the slash.</param>
		/// <returns>True when the dictionary is of the specified type.</returns>
		public bool IsType(string type) => GetName(PdfNames.Type)?.Value == type;

		/// <summary>
		/// Returns the value of <paramref name="key"/> when it is an indirect reference.
		/// </summary>
		/// <param name="key">The key to look up.</param>
		/// <returns>The unresolved reference, or null when the value is direct or missing.</returns>
		public PdfReference GetReference(PdfName key) => TryGetValue(key, out var value) ? value as PdfReference : null;
	}

	/// <summary>
	/// Represents a PDF stream: a dictionary followed by a sequence of bytes.
	/// </summary>
	public sealed class PdfStream : PdfDictionary
	{
		/// <summary>
		/// The stream data, decrypted but still encoded with the stream filters.
		/// </summary>
		internal byte[] RawData;

		/// <summary>
		/// The reference of the indirect object that holds this stream, if any.
		/// </summary>
		internal PdfReference Reference;

		/// <summary>The cached result of <c>GetBytes(false)</c>, or null.</summary>
		private byte[] _decoded;

		/// <summary>
		/// Initializes a new instance of <see cref="PdfStream"/> bound to <paramref name="file"/>.
		/// </summary>
		/// <param name="file">The file used to resolve indirect references.</param>
		internal PdfStream(PdfFile file)
			: base(file)
		{
		}

		/// <summary>
		/// Returns the stream data after decryption, still encoded with the stream filters.
		/// </summary>
		/// <returns>The encoded bytes; never null.</returns>
		public byte[] GetRawBytes() => this.RawData ?? Array.Empty<byte>();

		/// <summary>
		/// Returns the stream data decoded through its filters.
		/// </summary>
		/// <param name="decodeImageFilters">
		/// When false (default) the image codecs (<c>DCTDecode</c>, <c>JPXDecode</c>, <c>JBIG2Decode</c>
		/// and <c>CCITTFaxDecode</c>) are not applied, and the returned bytes are the compressed image.
		/// When true all the filters are applied.
		/// </param>
		/// <returns>The decoded bytes; never null.</returns>
		/// <remarks>
		/// The result of the default call is cached on the instance.
		/// </remarks>
		public byte[] GetBytes(bool decodeImageFilters = false)
		{
			if (!decodeImageFilters && _decoded != null)
				return _decoded;

			var data = Filters.FilterPipeline.Decode(this, decodeImageFilters, out _);
			if (!decodeImageFilters)
				_decoded = data;

			return data;
		}

		/// <summary>
		/// Releases the cached decoded data.
		/// </summary>
		internal void ReleaseCache() => _decoded = null;
	}

	/// <summary>
	/// Conversion helpers for loosely typed PDF values.
	/// </summary>
	public static class PdfConvert
	{
		/// <summary>
		/// Converts a PDF numeric value to <see cref="double"/>.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <param name="defaultValue">Value returned when <paramref name="value"/> is not a number.</param>
		/// <returns>The numeric value.</returns>
		public static double ToNumber(object value, double defaultValue = 0)
		{
			switch (value)
			{
				case int i: return i;
				case double d: return d;
				case long l: return l;
				case float f: return f;
				default: return defaultValue;
			}
		}

		/// <summary>
		/// Converts a PDF numeric value to <see cref="int"/>, rounding reals.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <param name="defaultValue">Value returned when <paramref name="value"/> is not a number.</param>
		/// <returns>The integer value.</returns>
		public static int ToInt(object value, int defaultValue = 0)
		{
			switch (value)
			{
				case int i: return i;
				case double d: return double.IsNaN(d) ? defaultValue : d >= int.MaxValue ? int.MaxValue : d <= int.MinValue ? int.MinValue : (int)Math.Round(d);
				case long l: return (int)l;
				default: return defaultValue;
			}
		}

		/// <summary>
		/// Returns a PDF-like representation of <paramref name="value"/>, for diagnostics.
		/// </summary>
		/// <param name="value">The value to format.</param>
		/// <returns>The formatted value.</returns>
		public static string ToDebugString(object value)
		{
			switch (value)
			{
				case null:
					return "null";

				case double d:
					return d.ToString(CultureInfo.InvariantCulture);

				case PdfString s:
					return "(" + s.ToLatin1() + ")";

				case PdfArray a:
					{
						var sb = new StringBuilder("[");
						foreach (var item in a)
							sb.Append(ToDebugString(item)).Append(' ');

						return sb.Append(']').ToString();
					}

				case PdfStream s:
					return "stream" + DictionaryToString(s);

				case PdfDictionary d:
					return DictionaryToString(d);

				default:
					return value.ToString();
			}
		}

		/// <summary>
		/// Formats a dictionary as <c>&lt;&lt;/Key value ...&gt;&gt;</c>, for diagnostics.
		/// </summary>
		/// <param name="dictionary">The dictionary to format.</param>
		/// <returns>The formatted dictionary.</returns>
		private static string DictionaryToString(PdfDictionary dictionary)
		{
			var sb = new StringBuilder("<<");
			foreach (var entry in dictionary)
				sb.Append(entry.Key).Append(' ').Append(ToDebugString(entry.Value)).Append(' ');

			return sb.Append(">>").ToString();
		}
	}
}
