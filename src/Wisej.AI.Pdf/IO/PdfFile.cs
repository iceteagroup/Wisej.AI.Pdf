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

// File-level structure: header, cross-reference data, trailer, indirect object
// resolution, object streams, encryption and damaged-file reconstruction.

using System;
using System.Collections.Generic;
using Wisej.AI.Pdf.IO;
using Wisej.AI.Pdf.Objects;
using Wisej.AI.Pdf.Security;

namespace Wisej.AI.Pdf
{
	/// <summary>
	/// Provides access to the indirect objects stored in a PDF file.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The whole file is kept in memory and objects are parsed lazily on first access and cached.
	/// All public members are thread-safe so pages can be processed in parallel.
	/// </para>
	/// <para>
	/// When the cross-reference data is missing or damaged, the file is reconstructed by
	/// scanning the bytes for object definitions, the same way viewers repair broken files.
	/// </para>
	/// </remarks>
	internal sealed class PdfFile
	{
		#region Nested types

		/// <summary>
		/// Kind of a cross-reference entry.
		/// </summary>
		private enum EntryType : byte
		{
			/// <summary>The object is free (deleted).</summary>
			Free,

			/// <summary>The object is stored at a byte offset in the file.</summary>
			InFile,

			/// <summary>The object is stored inside an object stream.</summary>
			InStream,
		}

		/// <summary>
		/// A cross-reference entry.
		/// </summary>
		private struct Entry
		{
			/// <summary>The kind of the entry.</summary>
			public EntryType Type;

			/// <summary>Byte offset for <see cref="EntryType.InFile"/>; object stream number for <see cref="EntryType.InStream"/>.</summary>
			public long Offset;

			/// <summary>Generation for <see cref="EntryType.InFile"/>; index within the object stream for <see cref="EntryType.InStream"/>.</summary>
			public int Index;
		}

		#endregion

		// raw bytes of the file.
		private readonly byte[] _data;

		// offset of "%PDF-": all the offsets in the file are relative to it.
		private int _headerOffset;

		// cross-reference entries by object number.
		private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();

		// parsed objects by object number.
		private readonly Dictionary<int, object> _cache = new Dictionary<int, object>();

		// parsed object streams: object number of the stream -> objects by object number.
		private readonly Dictionary<int, Dictionary<int, object>> _objectStreams = new Dictionary<int, Dictionary<int, object>>();

		// objects currently being loaded, to break reference cycles.
		private readonly HashSet<int> _loading = new HashSet<int>();

		// offsets found by scanning the file; built on demand.
		private Dictionary<int, int> _scannedOffsets;

		// guards the caches: all the public members can be called from multiple threads.
		private readonly object _sync = new object();

		/// <summary>
		/// Initializes a new instance of <see cref="PdfFile"/> and reads the file structure.
		/// </summary>
		/// <param name="data">The bytes of the PDF file.</param>
		/// <param name="password">The user or owner password; null or empty for none.</param>
		/// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
		/// <exception cref="PdfException">The data is not a PDF file, or the password is wrong.</exception>
		public PdfFile(byte[] data, string password = null)
		{
			_data = data ?? throw new ArgumentNullException(nameof(data));

			FindHeader();
			LoadCrossReference();
			SetupEncryption(password);
		}

		#region Properties

		/// <summary>
		/// The raw bytes of the file.
		/// </summary>
		public byte[] Data => _data;

		/// <summary>
		/// The PDF version declared in the header, for example "1.7".
		/// </summary>
		public string Version { get; private set; } = "1.4";

		/// <summary>
		/// The merged trailer dictionary.
		/// </summary>
		public PdfDictionary Trailer { get; private set; }

		/// <summary>
		/// The document catalog (<c>/Root</c>).
		/// </summary>
		public PdfDictionary Catalog { get; private set; }

		/// <summary>
		/// The decryptor for encrypted files, or null.
		/// </summary>
		public StandardDecryptor Decryptor { get; private set; }

		/// <summary>
		/// True when the cross-reference data was damaged and the file had to be reconstructed.
		/// </summary>
		public bool WasRepaired { get; private set; }

		#endregion

		#region Methods

		/// <summary>
		/// Resolves <paramref name="value"/> when it's an indirect reference.
		/// </summary>
		/// <param name="file">The file used to resolve the reference; can be null.</param>
		/// <param name="value">The value to resolve.</param>
		/// <returns>The referenced object, or <paramref name="value"/> itself when it's a direct object. <see cref="PdfNull"/> becomes null.</returns>
		public static object Resolve(PdfFile file, object value)
		{
			if (value is PdfReference reference)
				value = file?.GetObject(reference);

			return value is PdfNull ? null : value;
		}

		/// <summary>
		/// Returns the indirect object identified by <paramref name="reference"/>.
		/// </summary>
		/// <param name="reference">The reference to resolve.</param>
		/// <returns>The object, or null when it doesn't exist or can't be parsed.</returns>
		public object GetObject(PdfReference reference)
		{
			if (reference == null)
				return null;

			lock (_sync)
			{
				var number = reference.Number;
				if (_cache.TryGetValue(number, out var cached))
					return cached;

				// a reference cycle (e.g. a /Length that points to its own stream): give up on this branch.
				if (!_loading.Add(number))
					return null;

				try
				{
					var value = LoadObject(reference);
					_cache[number] = value;
					return value;
				}
				catch (Exception)
				{
					_cache[number] = null;
					return null;
				}
				finally
				{
					_loading.Remove(number);
				}
			}
		}

		/// <summary>
		/// Enumerates all the objects defined in the file that are of type <typeparamref name="T"/>.
		/// </summary>
		/// <typeparam name="T">The type of the objects to return.</typeparam>
		/// <returns>The objects, parsed on demand.</returns>
		/// <remarks>
		/// This is used to recover from broken object graphs, for example a damaged page tree.
		/// </remarks>
		public IEnumerable<T> EnumerateObjects<T>() where T : class
		{
			List<int> numbers;
			lock (_sync)
			{
				BuildScannedOffsets();
				numbers = new List<int>(_entries.Keys);
				foreach (var number in _scannedOffsets.Keys)
				{
					if (!_entries.ContainsKey(number))
						numbers.Add(number);
				}
			}

			numbers.Sort();
			foreach (var number in numbers)
			{
				if (GetObject(new PdfReference(number, 0)) is T value)
					yield return value;
			}
		}

		#endregion

		#region Header and cross-reference

		/// <summary>
		/// Locates the <c>%PDF-</c> header in the first 64 KB, reads the version and sets the base offset.
		/// </summary>
		/// <exception cref="PdfException">There is neither a header nor an object definition in the first 64 KB.</exception>
		private void FindHeader()
		{
			var limit = Math.Min(_data.Length, 1024 * 64);
			var offset = PdfParser.IndexOf(_data, "%PDF-", 0, limit);
			if (offset < 0)
			{
				// not a PDF header: accept files that at least contain an object definition.
				if (PdfParser.IndexOf(_data, " obj", 0, limit) < 0)
					throw new PdfException("The data is not a PDF document.");

				offset = 0;
			}
			else
			{
				var end = offset + 5;
				while (end < _data.Length && end < offset + 12 && (char.IsDigit((char)_data[end]) || _data[end] == '.'))
					end++;

				this.Version = PdfLexer.Latin1(_data, offset + 5, end - offset - 5);
			}

			_headerOffset = offset;
		}

		/// <summary>
		/// Reads the cross-reference chain and the trailer, falling back to reconstruction when they are damaged,
		/// and resolves the document catalog.
		/// </summary>
		/// <exception cref="PdfException">No catalog can be found, even after reconstruction.</exception>
		private void LoadCrossReference()
		{
			try
			{
				var startXRef = FindStartXRef();
				if (startXRef < 0 || !ReadCrossReferenceChain(startXRef))
					Reconstruct();
			}
			catch (Exception)
			{
				Reconstruct();
			}

			this.Catalog = this.Trailer?.GetDictionary(PdfNames.Root);

			// the trailer may point to a bad /Root: try reconstructing once before giving up.
			if (this.Catalog == null && !this.WasRepaired)
			{
				Reconstruct();
				this.Catalog = this.Trailer?.GetDictionary(PdfNames.Root);
			}

			if (this.Catalog == null)
				this.Catalog = FindCatalogByScan();

			if (this.Catalog == null)
				throw new PdfException("The PDF document has no catalog (/Root) and cannot be repaired.");
		}

		/// <summary>
		/// Finds the last <c>startxref</c> keyword in the final 4 KB of the file and reads the offset that follows it
		/// (ISO 32000-2, 7.5.5).
		/// </summary>
		/// <returns>The offset of the newest cross-reference section, relative to the header; -1 when not found.</returns>
		private long FindStartXRef()
		{
			// "startxref" is near the end of the file; search backwards in a generous window.
			var start = Math.Max(0, _data.Length - 4096);
			var found = -1;
			for (int p = start; ;)
			{
				var next = PdfParser.IndexOf(_data, "startxref", p, _data.Length);
				if (next < 0)
					break;

				found = next;
				p = next + 9;
			}

			if (found < 0)
				return -1;

			var lexer = new PdfLexer(_data, found + 9, _data.Length - found - 9);
			if (lexer.Next() != PdfTokenType.Number)
				return -1;

			return (long)PdfConvert.ToNumber(lexer.TokenNumber);
		}

		/// <summary>
		/// Reads the chain of cross-reference sections (tables, streams and hybrid <c>/XRefStm</c> sections)
		/// starting at the newest one and following the <c>/Prev</c> links.
		/// </summary>
		/// <param name="offset">Offset of the newest section, relative to the header.</param>
		/// <returns>False when the newest section is broken or no usable trailer with <c>/Root</c> was found.</returns>
		private bool ReadCrossReferenceChain(long offset)
		{
			var visited = new HashSet<long>();
			var pending = new Queue<long>();
			pending.Enqueue(offset);

			while (pending.Count > 0)
			{
				var current = pending.Dequeue();
				if (!visited.Add(current))
					continue;

				var position = ToFilePosition(current);
				if (position < 0)
					return false;

				// some files have the offset slightly wrong: look for "xref" or an object header nearby.
				PdfDictionary trailer;
				if (TryReadXRefTable(position, out trailer) || TryReadXRefStream(position, out trailer))
				{
					MergeTrailer(trailer);

					// hybrid files: the /XRefStm entries take precedence over the older table sections.
					var xrefStm = trailer.Resolve(PdfNames.XRefStm);
					if (xrefStm != null)
					{
						var streamPosition = ToFilePosition((long)PdfConvert.ToNumber(xrefStm));
						if (streamPosition >= 0)
							TryReadXRefStream(streamPosition, out _);
					}

					var prev = trailer.Resolve(PdfNames.Prev);
					if (prev != null)
						pending.Enqueue((long)PdfConvert.ToNumber(prev));
				}
				else
				{
					// the newest section must be valid; older broken ones are tolerated.
					if (current == offset)
						return false;
				}
			}

			return this.Trailer != null && this.Trailer.Has(PdfNames.Root) && _entries.Count > 0;
		}

		/// <summary>
		/// Converts an offset stored in the file (relative to the header) to a position in <see cref="_data"/>.
		/// </summary>
		/// <param name="offset">The offset relative to the <c>%PDF-</c> header.</param>
		/// <returns>The absolute position in the data, or -1 when it's out of range.</returns>
		private int ToFilePosition(long offset)
		{
			var position = offset + _headerOffset;
			if (position < 0 || position >= _data.Length)
			{
				// some writers compute offsets from the start of the file even when there is junk before the header.
				if (offset >= 0 && offset < _data.Length)
					return (int)offset;

				return -1;
			}

			return (int)position;
		}

		/// <summary>
		/// Reads a classic <c>xref</c> table section and the trailer dictionary that follows it (ISO 32000-2, 7.5.4).
		/// </summary>
		/// <param name="position">Absolute position of the <c>xref</c> keyword; small errors are tolerated.</param>
		/// <param name="trailer">Receives the trailer dictionary.</param>
		/// <returns>True when a table and its trailer were read.</returns>
		private bool TryReadXRefTable(int position, out PdfDictionary trailer)
		{
			trailer = null;

			var lexer = new PdfLexer(_data, position, _data.Length - position);
			if (lexer.Next() != PdfTokenType.Keyword || lexer.TokenText != "xref")
			{
				// tolerate a small offset error by searching a few bytes around the position.
				var near = PdfParser.IndexOf(_data, "xref", Math.Max(0, position - 16), Math.Min(_data.Length, position + 20));
				if (near < 0 || PdfParser.IndexOf(_data, "startxref", Math.Max(0, near - 5), near + 4) >= 0)
					return false;

				lexer.Position = near + 4;
			}

			while (true)
			{
				var type = lexer.Next();
				if (type == PdfTokenType.Keyword && lexer.TokenText == "trailer")
					break;

				if (type != PdfTokenType.Number)
					return false;

				var first = PdfConvert.ToInt(lexer.TokenNumber);
				if (lexer.Next() != PdfTokenType.Number)
					return false;

				var count = PdfConvert.ToInt(lexer.TokenNumber);

				// each entry takes at least 18 bytes: a larger count is corrupt (and must not allocate).
				if (count < 0 || count > (_data.Length - lexer.Position) / 18 + 1)
					return false;

				// read the subsection first: entries are "offset generation n|f", whitespace separated.
				var offsets = new long[count];
				var inUse = new bool[count];
				var generations = new int[count];
				for (int i = 0; i < count; i++)
				{
					if (lexer.Next() != PdfTokenType.Number)
						return false;
					offsets[i] = (long)PdfConvert.ToNumber(lexer.TokenNumber);

					if (lexer.Next() != PdfTokenType.Number)
						return false;
					generations[i] = PdfConvert.ToInt(lexer.TokenNumber);

					if (lexer.Next() != PdfTokenType.Keyword)
						return false;
					inUse[i] = lexer.TokenText == "n";
				}

				// known writer bug: a single subsection declared as starting at 1 that actually includes the free object 0.
				if (first == 1 && count > 0 && !inUse[0] && generations[0] == 65535)
					first = 0;

				for (int i = 0; i < count; i++)
				{
					var number = first + i;

					// newer sections are read first: don't overwrite their entries.
					if (_entries.ContainsKey(number))
						continue;

					// offset 0 marks a missing object in some broken writers.
					_entries[number] = inUse[i] && offsets[i] > 0
						? new Entry { Type = EntryType.InFile, Offset = offsets[i], Index = generations[i] }
						: new Entry { Type = EntryType.Free };
				}
			}

			var parser = new PdfParser(lexer, this);
			trailer = parser.ParseObject() as PdfDictionary;
			return trailer != null;
		}

		/// <summary>
		/// Reads a cross-reference stream (ISO 32000-2, 7.5.8); its dictionary doubles as the trailer.
		/// </summary>
		/// <param name="position">Absolute position of the stream's object header.</param>
		/// <param name="trailer">Receives the stream dictionary.</param>
		/// <returns>True when a valid <c>/Type /XRef</c> stream was read.</returns>
		private bool TryReadXRefStream(int position, out PdfDictionary trailer)
		{
			trailer = null;

			var lexer = new PdfLexer(_data, position, _data.Length - position);
			var parser = new PdfParser(lexer, this);
			if (!(parser.ParseIndirectObject(null, false) is PdfStream stream) || !stream.IsType("XRef"))
				return false;

			var widths = stream.GetArray(PdfNames.W);
			if (widths == null || widths.Count < 3)
				return false;

			var w0 = widths.GetInt(0);
			var w1 = widths.GetInt(1);
			var w2 = widths.GetInt(2);
			var rowSize = w0 + w1 + w2;
			if (rowSize <= 0 || w0 < 0 || w1 < 0 || w2 < 0)
				return false;

			var size = stream.GetInt(PdfNames.Size);
			var index = stream.GetArray(PdfNames.Index);
			var sections = new List<int>();
			if (index != null && index.Count >= 2)
			{
				for (int i = 0; i + 1 < index.Count; i += 2)
				{
					sections.Add(index.GetInt(i));
					sections.Add(index.GetInt(i + 1));
				}
			}
			else
			{
				sections.Add(0);
				sections.Add(size);
			}

			byte[] bytes;
			try
			{
				bytes = stream.GetBytes();
			}
			catch (Exception)
			{
				return false;
			}

			var p = 0;
			for (int s = 0; s < sections.Count; s += 2)
			{
				var first = sections[s];
				var count = sections[s + 1];
				for (int i = 0; i < count && p + rowSize <= bytes.Length; i++, p += rowSize)
				{
					// the type field defaults to 1 when its width is zero.
					var type = w0 == 0 ? 1 : (int)ReadBigEndian(bytes, p, w0);
					var field2 = ReadBigEndian(bytes, p + w0, w1);
					var field3 = (int)ReadBigEndian(bytes, p + w0 + w1, w2);

					var number = first + i;
					if (_entries.ContainsKey(number))
						continue;

					switch (type)
					{
						case 0:
							_entries[number] = new Entry { Type = EntryType.Free };
							break;

						case 1:
							_entries[number] = new Entry { Type = EntryType.InFile, Offset = field2, Index = field3 };
							break;

						case 2:
							_entries[number] = new Entry { Type = EntryType.InStream, Offset = field2, Index = field3 };
							break;

						default:
							// unknown types must be treated as null objects.
							break;
					}
				}
			}

			trailer = stream;
			return true;
		}

		/// <summary>
		/// Reads an unsigned big-endian integer of <paramref name="width"/> bytes, as used by the fields of xref streams.
		/// </summary>
		/// <param name="data">The source bytes.</param>
		/// <param name="offset">Position of the first byte.</param>
		/// <param name="width">Number of bytes; 0 returns 0.</param>
		/// <returns>The decoded value.</returns>
		private static long ReadBigEndian(byte[] data, int offset, int width)
		{
			long value = 0;
			for (int i = 0; i < width; i++)
				value = (value << 8) | data[offset + i];

			return value;
		}

		/// <summary>
		/// Merges a trailer into <see cref="Trailer"/>, skipping the keys that only describe the xref section itself.
		/// </summary>
		/// <param name="trailer">A trailer or xref stream dictionary; sections are merged newest first.</param>
		private void MergeTrailer(PdfDictionary trailer)
		{
			if (this.Trailer == null)
				this.Trailer = new PdfDictionary(this);

			// the newest trailer is read first: older ones only fill missing keys.
			foreach (var entry in trailer)
			{
				if (entry.Key == PdfNames.Prev || entry.Key == PdfNames.XRefStm || entry.Key == PdfNames.Length
					|| entry.Key == PdfNames.Filter || entry.Key == PdfNames.DecodeParms || entry.Key == PdfNames.W
					|| entry.Key == PdfNames.Index || entry.Key == PdfNames.Type)
				{
					continue;
				}

				if (!this.Trailer.ContainsKey(entry.Key))
					this.Trailer[entry.Key] = entry.Value;
			}
		}

		#endregion

		#region Object loading

		/// <summary>
		/// Loads an object using its cross-reference entry, falling back to the scanned offsets when the entry is missing or wrong.
		/// </summary>
		/// <param name="reference">The reference to load.</param>
		/// <returns>The parsed object, or null.</returns>
		private object LoadObject(PdfReference reference)
		{
			var number = reference.Number;

			if (!_entries.TryGetValue(number, out var entry))
			{
				// not in the xref: it may still exist in the file (common in damaged files).
				return LoadFromScan(reference);
			}

			switch (entry.Type)
			{
				case EntryType.InFile:
					{
						var position = ToFilePosition(entry.Offset);
						if (position >= 0)
						{
							var value = ParseAt(position, reference);
							if (value != null)
								return value;
						}

						// wrong offset: fall back to scanning the file.
						return LoadFromScan(reference);
					}

				case EntryType.InStream:
					return LoadFromObjectStream((int)entry.Offset, entry.Index, number)
						?? LoadFromScan(reference);

				default:
					return null;
			}
		}

		/// <summary>
		/// Parses the indirect object (<c>n g obj ... endobj</c>) that starts at <paramref name="position"/>.
		/// </summary>
		/// <param name="position">Absolute position of the object header.</param>
		/// <param name="reference">The expected reference, used for decryption; null when unknown.</param>
		/// <returns>The parsed object, or null.</returns>
		private object ParseAt(int position, PdfReference reference)
		{
			var lexer = new PdfLexer(_data, position, _data.Length - position);
			var parser = new PdfParser(lexer, this);
			return parser.ParseIndirectObject(reference, this.Decryptor != null);
		}

		/// <summary>
		/// Loads an object from the offset found by scanning the file for its <c>obj</c> header.
		/// </summary>
		/// <param name="reference">The reference to load.</param>
		/// <returns>The parsed object, or null when it isn't defined in the file.</returns>
		private object LoadFromScan(PdfReference reference)
		{
			BuildScannedOffsets();
			if (_scannedOffsets.TryGetValue(reference.Number, out var position))
				return ParseAt(position, reference);

			return null;
		}

		/// <summary>
		/// Loads an object stored in an object stream, parsing and caching the whole stream on first access.
		/// </summary>
		/// <param name="streamNumber">Object number of the object stream.</param>
		/// <param name="index">Index of the object within the stream, from the xref; unused because it's unreliable.</param>
		/// <param name="number">Object number of the object to load.</param>
		/// <returns>The object, or null when the stream doesn't contain it.</returns>
		private object LoadFromObjectStream(int streamNumber, int index, int number)
		{
			if (!_objectStreams.TryGetValue(streamNumber, out var objects))
			{
				objects = ParseObjectStream(streamNumber);
				_objectStreams[streamNumber] = objects;
			}

			if (objects == null)
				return null;

			// look up by object number: the index in the xref is only a hint and is sometimes wrong.
			return objects.TryGetValue(number, out var value) ? value : null;
		}

		/// <summary>
		/// Parses all the objects contained in an object stream (ISO 32000-2, 7.5.7).
		/// </summary>
		/// <param name="streamNumber">Object number of the object stream.</param>
		/// <returns>The objects by object number, or null when the stream is missing or empty.</returns>
		private Dictionary<int, object> ParseObjectStream(int streamNumber)
		{
			// the object stream itself is always stored directly in the file.
			if (!(GetObject(new PdfReference(streamNumber, 0)) is PdfStream stream))
				return null;

			var count = stream.GetInt(PdfNames.N);
			var first = stream.GetInt(PdfNames.First);
			if (count <= 0)
				return null;

			var data = stream.GetBytes();
			var lexer = new PdfLexer(data);

			// header: pairs of "object-number offset".
			var numbers = new List<int>(count);
			var offsets = new List<int>(count);
			for (int i = 0; i < count; i++)
			{
				if (lexer.Next() != PdfTokenType.Number)
					break;
				var number = PdfConvert.ToInt(lexer.TokenNumber);

				if (lexer.Next() != PdfTokenType.Number)
					break;

				numbers.Add(number);
				offsets.Add(PdfConvert.ToInt(lexer.TokenNumber));
			}

			// objects in object streams are not encrypted individually: the stream was decrypted as a whole.
			var objects = new Dictionary<int, object>(numbers.Count);
			for (int i = 0; i < numbers.Count; i++)
			{
				var start = Math.Min(data.Length, first + offsets[i]);
				var parser = new PdfParser(new PdfLexer(data, start, data.Length - start), this);
				try
				{
					objects[numbers[i]] = parser.ParseObject();
				}
				catch (Exception)
				{
					objects[numbers[i]] = null;
				}
			}

			return objects;
		}

		#endregion

		#region Reconstruction

		/// <summary>
		/// Scans the whole file for <c>n g obj</c> headers and records their absolute positions in <see cref="_scannedOffsets"/>.
		/// Later definitions win, as in incremental updates.
		/// </summary>
		private void BuildScannedOffsets()
		{
			if (_scannedOffsets != null)
				return;

			_scannedOffsets = new Dictionary<int, int>();

			var data = _data;
			var length = data.Length;
			for (int p = PdfParser.IndexOf(data, "obj", 0, length); p >= 0; p = PdfParser.IndexOf(data, "obj", p + 3, length))
			{
				// must be "obj" as a whole token, preceded by whitespace.
				if (p + 3 < length && PdfLexer.IsRegular(data[p + 3]))
					continue;
				if (p == 0 || !PdfLexer.IsWhitespace(data[p - 1]))
					continue;

				// walk back over "<number> <generation> ".
				var q = p - 1;
				while (q > 0 && PdfLexer.IsWhitespace(data[q]))
					q--;
				var generationEnd = q + 1;
				while (q >= 0 && data[q] >= '0' && data[q] <= '9')
					q--;
				if (q + 1 == generationEnd)
					continue;

				while (q > 0 && PdfLexer.IsWhitespace(data[q]))
					q--;
				var numberEnd = q + 1;
				while (q >= 0 && data[q] >= '0' && data[q] <= '9')
					q--;
				if (q + 1 == numberEnd)
					continue;

				// the number must start a token.
				if (q >= 0 && PdfLexer.IsRegular(data[q]))
					continue;

				var start = q + 1;
				var number = 0;
				for (int i = start; i < numberEnd && number < 100000000; i++)
					number = number * 10 + (data[i] - '0');

				_scannedOffsets[number] = start;
			}
		}

		/// <summary>
		/// Rebuilds the cross-reference entries and the trailer from scratch by scanning the file,
		/// registering the object streams and merging every trailer found.
		/// </summary>
		private void Reconstruct()
		{
			this.WasRepaired = true;

			BuildScannedOffsets();

			_entries.Clear();
			_cache.Clear();
			_objectStreams.Clear();

			foreach (var item in _scannedOffsets)
				_entries[item.Key] = new Entry { Type = EntryType.InFile, Offset = item.Value - _headerOffset, Index = 0 };

			// register the objects stored in object streams, without overriding direct definitions.
			var trailer = new PdfDictionary(this);
			foreach (var item in _scannedOffsets)
			{
				PdfStream stream;
				try
				{
					stream = ParseAt(item.Value, null) as PdfStream;
				}
				catch (Exception)
				{
					continue;
				}

				if (stream == null)
					continue;

				if (stream.IsType("ObjStm"))
				{
					RegisterObjectStream(item.Key, stream);
				}
				else if (stream.IsType("XRef"))
				{
					// an xref stream doubles as trailer.
					CopyTrailerKeys(stream, trailer);
				}
			}

			// classic trailers: the last one in the file is the newest.
			for (int p = PdfParser.IndexOf(_data, "trailer", 0, _data.Length); p >= 0; p = PdfParser.IndexOf(_data, "trailer", p + 7, _data.Length))
			{
				var lexer = new PdfLexer(_data, p + 7, _data.Length - p - 7);
				if (new PdfParser(lexer, this).ParseObject() is PdfDictionary dictionary)
					CopyTrailerKeys(dictionary, trailer, overwrite: true);
			}

			this.Trailer = trailer;
		}

		/// <summary>
		/// Adds xref entries for the objects listed in the header of an object stream, without overriding existing entries.
		/// </summary>
		/// <param name="streamNumber">Object number of the object stream.</param>
		/// <param name="stream">The object stream.</param>
		private void RegisterObjectStream(int streamNumber, PdfStream stream)
		{
			var count = stream.GetInt(PdfNames.N);
			if (count <= 0)
				return;

			byte[] data;
			try
			{
				// decryption is not set up yet during reconstruction of encrypted files; the header is read lazily later.
				data = this.Decryptor == null ? stream.GetBytes() : null;
			}
			catch (Exception)
			{
				return;
			}

			if (data == null)
				return;

			var lexer = new PdfLexer(data);
			for (int i = 0; i < count; i++)
			{
				if (lexer.Next() != PdfTokenType.Number)
					return;
				var number = PdfConvert.ToInt(lexer.TokenNumber);
				if (lexer.Next() != PdfTokenType.Number)
					return;

				if (!_entries.ContainsKey(number))
					_entries[number] = new Entry { Type = EntryType.InStream, Offset = streamNumber, Index = i };
			}
		}

		/// <summary>
		/// Copies the document-level trailer keys (<c>/Root</c>, <c>/Info</c>, <c>/Encrypt</c>, <c>/ID</c>) between dictionaries.
		/// </summary>
		/// <param name="source">The dictionary to copy from.</param>
		/// <param name="target">The dictionary to copy to.</param>
		/// <param name="overwrite">True to replace the keys already present in <paramref name="target"/>.</param>
		private static void CopyTrailerKeys(PdfDictionary source, PdfDictionary target, bool overwrite = false)
		{
			foreach (var key in new[] { PdfNames.Root, PdfNames.Info, PdfNames.Encrypt, PdfNames.ID })
			{
				if (source.TryGetValue(key, out var value) && (overwrite || !target.ContainsKey(key)))
					target[key] = value;
			}
		}

		/// <summary>
		/// Finds the document catalog by scanning all the objects for a <c>/Type /Catalog</c> dictionary with <c>/Pages</c>.
		/// </summary>
		/// <returns>The last matching catalog in the file, or null.</returns>
		private PdfDictionary FindCatalogByScan()
		{
			BuildScannedOffsets();

			// prefer the last catalog in the file (incremental updates append newer ones).
			PdfDictionary found = null;
			foreach (var item in _scannedOffsets)
			{
				if (GetObject(new PdfReference(item.Key, 0)) is PdfDictionary dictionary
					&& dictionary.IsType("Catalog")
					&& dictionary.Has(PdfNames.Pages))
				{
					found = dictionary;
				}
			}

			return found;
		}

		#endregion

		#region Encryption

		/// <summary>
		/// Creates the decryptor when the trailer has an <c>/Encrypt</c> dictionary, then clears the caches
		/// so that objects are parsed again with decryption.
		/// </summary>
		/// <param name="password">The user or owner password; null or empty for none.</param>
		/// <exception cref="PdfException">The password is wrong or the security handler is not supported.</exception>
		private void SetupEncryption(string password)
		{
			var encrypt = this.Trailer?.Resolve(PdfNames.Encrypt) as PdfDictionary;
			if (encrypt == null)
				return;

			var id = this.Trailer.GetArray(PdfNames.ID);
			var firstId = id?.GetString(0)?.Bytes ?? Array.Empty<byte>();

			var decryptor = StandardDecryptor.Create(encrypt, firstId, password);

			// objects parsed so far (trailer, xref streams, the encryption dictionary) were read without decryption.
			// clear the cache so they are parsed again with the decryptor in place; the /Encrypt dictionary is exempt.
			lock (_sync)
			{
				this.Decryptor = decryptor;
				decryptor.ExemptReference = this.Trailer.GetReference(PdfNames.Encrypt);

				_cache.Clear();
				_objectStreams.Clear();
			}

			this.Catalog = this.Trailer.GetDictionary(PdfNames.Root) ?? FindCatalogByScan();
		}

		#endregion
	}
}
