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

// Reader and writer of the compressed archives that embed the predefined CMaps and the
// CID to Unicode tables.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Wisej.AI.Pdf.Fonts
{
	/// <summary>
	/// An archive of named, individually Deflate-compressed entries stored as an embedded resource.
	/// </summary>
	/// <remarks>
	/// <para>Layout (all integers are unsigned LEB128 varints, strings are a varint length and ASCII bytes):</para>
	/// <code>
	/// "WCM1"                       magic
	/// count                        number of entries
	/// { name, offset, length }     the entries, offsets relative to the end of the index
	/// data                         the raw Deflate (RFC 1951) blobs
	/// </code>
	/// <para>
	/// Entries are inflated on demand, so only the CMaps actually used by a document are decoded.
	/// </para>
	/// </remarks>
	internal sealed class CMapArchive
	{
		#region Fields

		// the archive signature.
		private static readonly byte[] Magic = { (byte)'W', (byte)'C', (byte)'M', (byte)'1' };

		// the raw archive bytes.
		private readonly byte[] _data;

		// the offset of the first blob.
		private readonly int _dataStart;

		// entry name -> (offset, length) of the compressed blob.
		private readonly Dictionary<string, KeyValuePair<int, int>> _entries = new Dictionary<string, KeyValuePair<int, int>>(StringComparer.Ordinal);

		#endregion

		#region Constructors

		/// <summary>
		/// Opens an archive.
		/// </summary>
		/// <param name="data">The archive bytes; null or invalid data produces an empty archive.</param>
		public CMapArchive(byte[] data)
		{
			_data = data ?? Array.Empty<byte>();
			if (_data.Length < Magic.Length)
				return;

			for (int i = 0; i < Magic.Length; i++)
			{
				if (_data[i] != Magic[i])
					return;
			}

			var reader = new CMapDataReader(_data, Magic.Length);
			var count = reader.ReadVarInt();
			var entries = new List<KeyValuePair<string, KeyValuePair<int, int>>>();
			for (int i = 0; i < count && !reader.IsEnd; i++)
			{
				var name = reader.ReadString();
				var offset = reader.ReadVarInt();
				var length = reader.ReadVarInt();
				entries.Add(new KeyValuePair<string, KeyValuePair<int, int>>(name, new KeyValuePair<int, int>(offset, length)));
			}

			_dataStart = reader.Position;
			foreach (var entry in entries)
			{
				var offset = entry.Value.Key;
				var length = entry.Value.Value;
				if (offset >= 0 && length >= 0 && (long)_dataStart + offset + length <= _data.Length)
					_entries[entry.Key] = entry.Value;
			}
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the names of the entries.
		/// </summary>
		public ICollection<string> Names => _entries.Keys;

		#endregion

		#region Methods

		/// <summary>
		/// Loads an archive from an embedded resource of this assembly.
		/// </summary>
		/// <param name="resourceName">The logical name of the resource.</param>
		/// <returns>The archive; empty when the resource is missing.</returns>
		public static CMapArchive LoadResource(string resourceName)
		{
			try
			{
				using (var stream = typeof(CMapArchive).Assembly.GetManifestResourceStream(resourceName))
				{
					if (stream == null)
						return new CMapArchive(null);

					var buffer = new MemoryStream();
					stream.CopyTo(buffer);
					return new CMapArchive(buffer.ToArray());
				}
			}
			catch (IOException)
			{
				return new CMapArchive(null);
			}
		}

		/// <summary>
		/// Returns true when the archive contains <paramref name="name"/>.
		/// </summary>
		/// <param name="name">The entry name.</param>
		/// <returns>True when the entry exists.</returns>
		public bool Contains(string name)
		{
			return name != null && _entries.ContainsKey(name);
		}

		/// <summary>
		/// Inflates an entry.
		/// </summary>
		/// <param name="name">The entry name.</param>
		/// <returns>The decompressed bytes, or null when the entry doesn't exist or is corrupt.</returns>
		public byte[] Read(string name)
		{
			if (name == null || !_entries.TryGetValue(name, out var entry))
				return null;

			try
			{
				using (var input = new MemoryStream(_data, _dataStart + entry.Key, entry.Value, false))
				using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
				{
					var output = new MemoryStream(entry.Value * 4);
					deflate.CopyTo(output);
					return output.ToArray();
				}
			}
			catch (InvalidDataException)
			{
				return null;
			}
		}

		/// <summary>
		/// Builds an archive from named uncompressed entries. Used by the resource generator.
		/// </summary>
		/// <param name="entries">The entries, in the order they are stored.</param>
		/// <returns>The archive bytes.</returns>
		public static byte[] Build(IList<KeyValuePair<string, byte[]>> entries)
		{
			var blobs = new List<byte[]>(entries.Count);
			foreach (var entry in entries)
			{
				var compressed = new MemoryStream();
				using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
					deflate.Write(entry.Value, 0, entry.Value.Length);

				blobs.Add(compressed.ToArray());
			}

			var writer = new CMapDataWriter();
			writer.WriteBytes(Magic);
			writer.WriteVarInt(entries.Count);
			var offset = 0;
			for (int i = 0; i < entries.Count; i++)
			{
				writer.WriteString(entries[i].Key);
				writer.WriteVarInt(offset);
				writer.WriteVarInt(blobs[i].Length);
				offset += blobs[i].Length;
			}

			foreach (var blob in blobs)
				writer.WriteBytes(blob);

			return writer.ToArray();
		}

		#endregion
	}

	/// <summary>
	/// Reads varints and strings from the decompressed archive entries.
	/// </summary>
	/// <remarks>
	/// Reading past the end returns zeros instead of throwing, so a corrupt resource degrades to empty tables.
	/// </remarks>
	internal struct CMapDataReader
	{
		// the source bytes.
		private readonly byte[] _data;

		// the read position in _data.
		private int _position;

		/// <summary>
		/// Initializes the reader.
		/// </summary>
		/// <param name="data">The bytes to read.</param>
		/// <param name="position">The start position.</param>
		public CMapDataReader(byte[] data, int position)
		{
			_data = data ?? Array.Empty<byte>();
			_position = position;
		}

		/// <summary>
		/// Returns the read position.
		/// </summary>
		public int Position => _position;

		/// <summary>
		/// Returns true when all the bytes have been read.
		/// </summary>
		public bool IsEnd => _position >= _data.Length;

		/// <summary>
		/// Reads one byte.
		/// </summary>
		/// <returns>The byte, or 0 past the end.</returns>
		public int ReadByte()
		{
			return _position < _data.Length ? _data[_position++] : 0;
		}

		/// <summary>
		/// Reads an unsigned LEB128 varint of up to 64 bits.
		/// </summary>
		/// <returns>The value.</returns>
		public long ReadVarLong()
		{
			long value = 0;
			var shift = 0;
			while (_position < _data.Length && shift < 64)
			{
				var b = _data[_position++];
				value |= (long)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					break;

				shift += 7;
			}

			return value;
		}

		/// <summary>
		/// Reads an unsigned varint that fits in an <see cref="int"/>.
		/// </summary>
		/// <returns>The value, clamped to <see cref="int.MaxValue"/>.</returns>
		public int ReadVarInt()
		{
			var value = ReadVarLong();
			return value > int.MaxValue || value < 0 ? int.MaxValue : (int)value;
		}

		/// <summary>
		/// Reads a zigzag-encoded signed varint.
		/// </summary>
		/// <returns>The value.</returns>
		public long ReadSignedVarLong()
		{
			var value = ReadVarLong();
			return (long)((ulong)value >> 1) ^ -(value & 1);
		}

		/// <summary>
		/// Reads a varint length followed by ASCII bytes.
		/// </summary>
		/// <returns>The string.</returns>
		public string ReadString()
		{
			var length = ReadVarInt();
			length = Math.Min(length, _data.Length - _position);
			if (length <= 0)
				return string.Empty;

			var text = Encoding.ASCII.GetString(_data, _position, length);
			_position += length;
			return text;
		}
	}

	/// <summary>
	/// Writes varints and strings for the archive entries. Used by the resource generator.
	/// </summary>
	internal sealed class CMapDataWriter
	{
		// the output buffer.
		private readonly MemoryStream _stream = new MemoryStream();

		/// <summary>
		/// Writes one byte.
		/// </summary>
		/// <param name="value">The byte.</param>
		public void WriteByte(int value)
		{
			_stream.WriteByte((byte)value);
		}

		/// <summary>
		/// Writes raw bytes.
		/// </summary>
		/// <param name="bytes">The bytes.</param>
		public void WriteBytes(byte[] bytes)
		{
			_stream.Write(bytes, 0, bytes.Length);
		}

		/// <summary>
		/// Writes an unsigned LEB128 varint.
		/// </summary>
		/// <param name="value">The value, which must not be negative.</param>
		public void WriteVarInt(long value)
		{
			var v = (ulong)value;
			while (v >= 0x80)
			{
				_stream.WriteByte((byte)(v | 0x80));
				v >>= 7;
			}

			_stream.WriteByte((byte)v);
		}

		/// <summary>
		/// Writes a zigzag-encoded signed varint.
		/// </summary>
		/// <param name="value">The value.</param>
		public void WriteSignedVarInt(long value)
		{
			WriteVarInt((value << 1) ^ (value >> 63));
		}

		/// <summary>
		/// Writes a varint length followed by ASCII bytes.
		/// </summary>
		/// <param name="text">The string; null is written as empty.</param>
		public void WriteString(string text)
		{
			var bytes = Encoding.ASCII.GetBytes(text ?? string.Empty);
			WriteVarInt(bytes.Length);
			WriteBytes(bytes);
		}

		/// <summary>
		/// Returns the bytes written so far.
		/// </summary>
		/// <returns>The bytes.</returns>
		public byte[] ToArray()
		{
			return _stream.ToArray();
		}
	}
}
