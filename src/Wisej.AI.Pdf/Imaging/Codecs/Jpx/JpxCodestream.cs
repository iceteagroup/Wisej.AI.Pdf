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

// JPEG 2000 codestream syntax: marker segments, main and tile-part headers (ITU-T T.800, Annex A).

using System;
using System.Collections.Generic;
using System.IO;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Describes one image component as declared by the SIZ marker (ITU-T T.800, A.5.1).
	/// </summary>
	internal sealed class JpxComponentInfo
	{
		/// <summary>
		/// The sample precision in bits (1 to 38; this decoder supports up to 16).
		/// </summary>
		public int Precision;

		/// <summary>
		/// True when the samples are signed.
		/// </summary>
		public bool Signed;

		/// <summary>
		/// The horizontal separation of the samples on the reference grid (XRsiz).
		/// </summary>
		public int XRsiz;

		/// <summary>
		/// The vertical separation of the samples on the reference grid (YRsiz).
		/// </summary>
		public int YRsiz;
	}

	/// <summary>
	/// Coding style of one tile-component, from COD or COC (ITU-T T.800, A.6.1 and A.6.2).
	/// </summary>
	internal sealed class JpxCodingStyle
	{
		/// <summary>
		/// The number of wavelet decomposition levels (NL).
		/// </summary>
		public int Levels;

		/// <summary>
		/// The base-2 logarithm of the nominal code-block width.
		/// </summary>
		public int CodeBlockWidthExp;

		/// <summary>
		/// The base-2 logarithm of the nominal code-block height.
		/// </summary>
		public int CodeBlockHeightExp;

		/// <summary>
		/// The code-block style flags (bypass, reset, termall, vertically causal, predictable termination, segmentation symbols).
		/// </summary>
		public int CodeBlockStyle;

		/// <summary>
		/// True for the reversible 5/3 wavelet, false for the irreversible 9/7 wavelet.
		/// </summary>
		public bool Reversible;

		/// <summary>
		/// The base-2 logarithm of the precinct width for each resolution level (index 0 is the lowest resolution).
		/// </summary>
		public int[] PrecinctWidthExp;

		/// <summary>
		/// The base-2 logarithm of the precinct height for each resolution level.
		/// </summary>
		public int[] PrecinctHeightExp;
	}

	/// <summary>
	/// Coding parameters shared by all components of a tile, from COD (ITU-T T.800, A.6.1).
	/// </summary>
	internal sealed class JpxCodingDefaults
	{
		/// <summary>
		/// True when packets may be preceded by SOP marker segments.
		/// </summary>
		public bool UsesSop;

		/// <summary>
		/// True when packet headers are terminated by EPH markers.
		/// </summary>
		public bool UsesEph;

		/// <summary>
		/// The progression order (0 = LRCP, 1 = RLCP, 2 = RPCL, 3 = PCRL, 4 = CPRL).
		/// </summary>
		public int Progression;

		/// <summary>
		/// The number of quality layers.
		/// </summary>
		public int Layers;

		/// <summary>
		/// The multiple component transform flag (1 = RCT or ICT on the first three components).
		/// </summary>
		public int Mct;

		/// <summary>
		/// The default coding style of the components.
		/// </summary>
		public JpxCodingStyle Style;
	}

	/// <summary>
	/// Quantization parameters of one tile-component, from QCD or QCC (ITU-T T.800, A.6.4 and A.6.5).
	/// </summary>
	internal sealed class JpxQuantization
	{
		/// <summary>
		/// The quantization style (0 = none, 1 = scalar derived, 2 = scalar expounded).
		/// </summary>
		public int Style;

		/// <summary>
		/// The number of guard bits.
		/// </summary>
		public int GuardBits;

		/// <summary>
		/// The exponent of each sub-band step size (one entry for the derived style).
		/// </summary>
		public int[] Exponents;

		/// <summary>
		/// The mantissa of each sub-band step size (one entry for the derived style).
		/// </summary>
		public int[] Mantissas;
	}

	/// <summary>
	/// One progression order change from a POC marker (ITU-T T.800, A.6.6).
	/// </summary>
	internal sealed class JpxProgressionChange
	{
		/// <summary>
		/// The first resolution level (inclusive).
		/// </summary>
		public int ResolutionStart;

		/// <summary>
		/// The first component (inclusive).
		/// </summary>
		public int ComponentStart;

		/// <summary>
		/// The last layer (exclusive).
		/// </summary>
		public int LayerEnd;

		/// <summary>
		/// The last resolution level (exclusive).
		/// </summary>
		public int ResolutionEnd;

		/// <summary>
		/// The last component (exclusive).
		/// </summary>
		public int ComponentEnd;

		/// <summary>
		/// The progression order of this volume.
		/// </summary>
		public int Progression;
	}

	/// <summary>
	/// The coding parameters of the main header or of one tile, where null entries inherit the main header.
	/// </summary>
	internal sealed class JpxHeaderSet
	{
		/// <summary>
		/// The COD parameters, or null.
		/// </summary>
		public JpxCodingDefaults Cod;

		/// <summary>
		/// The COC overrides indexed by component, with null entries for components without one.
		/// </summary>
		public JpxCodingStyle[] Coc;

		/// <summary>
		/// The QCD parameters, or null.
		/// </summary>
		public JpxQuantization Qcd;

		/// <summary>
		/// The QCC overrides indexed by component.
		/// </summary>
		public JpxQuantization[] Qcc;

		/// <summary>
		/// The ROI maxshift value of each component, or -1 when not set.
		/// </summary>
		public int[] RoiShift;

		/// <summary>
		/// The progression order changes, or null.
		/// </summary>
		public List<JpxProgressionChange> Poc;

		/// <summary>
		/// Initializes a new instance of <see cref="JpxHeaderSet"/> for <paramref name="components"/> components.
		/// </summary>
		/// <param name="components">The number of image components.</param>
		public JpxHeaderSet(int components)
		{
			this.Coc = new JpxCodingStyle[components];
			this.Qcc = new JpxQuantization[components];
			this.RoiShift = new int[components];
			for (int i = 0; i < components; i++)
				this.RoiShift[i] = -1;
		}
	}

	/// <summary>
	/// Collects the tile-parts of one tile.
	/// </summary>
	internal sealed class JpxTileInfo
	{
		/// <summary>
		/// The tile index in raster order.
		/// </summary>
		public int Index;

		/// <summary>
		/// The coding parameters declared in the tile-part headers.
		/// </summary>
		public JpxHeaderSet Header;

		/// <summary>
		/// The bit-stream data of the tile: the concatenated bodies of its tile-parts.
		/// </summary>
		public MemoryStream Data = new MemoryStream();

		/// <summary>
		/// The packet headers taken from PPM or PPT markers, or null when they are in the bit-stream.
		/// </summary>
		public MemoryStream PacketHeaders;

		/// <summary>
		/// The PPT marker segments of this tile keyed by their Zppt index, merged into <see cref="PacketHeaders"/> later.
		/// </summary>
		public SortedDictionary<int, byte[]> Ppt;
	}

	/// <summary>
	/// Parses a JPEG 2000 codestream into its main header and its tiles.
	/// </summary>
	/// <remarks>
	/// The parser is lenient: unknown markers are skipped, wrong tile-part lengths are repaired by
	/// scanning for the next SOT marker, and a truncated codestream yields the tiles read so far.
	/// </remarks>
	internal sealed class JpxCodestream
	{
		#region Marker codes

		// delimiting and marker segment codes (T.800, Table A.2).

		/// <summary>Start of codestream.</summary>
		private const int SOC = 0xFF4F;

		/// <summary>Image and tile size.</summary>
		private const int SIZ = 0xFF51;

		/// <summary>Coding style default.</summary>
		private const int COD = 0xFF52;

		/// <summary>Coding style component.</summary>
		private const int COC = 0xFF53;

		/// <summary>Tile-part lengths (ignored).</summary>
		private const int TLM = 0xFF55;

		/// <summary>Packet length, main header (ignored).</summary>
		private const int PLM = 0xFF57;

		/// <summary>Packet length, tile-part header (ignored).</summary>
		private const int PLT = 0xFF58;

		/// <summary>Quantization default.</summary>
		private const int QCD = 0xFF5C;

		/// <summary>Quantization component.</summary>
		private const int QCC = 0xFF5D;

		/// <summary>Region of interest.</summary>
		private const int RGN = 0xFF5E;

		/// <summary>Progression order change.</summary>
		private const int POC = 0xFF5F;

		/// <summary>Packed packet headers, main header.</summary>
		private const int PPM = 0xFF60;

		/// <summary>Packed packet headers, tile-part header.</summary>
		private const int PPT = 0xFF61;

		/// <summary>Start of tile-part.</summary>
		private const int SOT = 0xFF90;

		/// <summary>Start of data.</summary>
		private const int SOD = 0xFF93;

		/// <summary>End of codestream.</summary>
		private const int EOC = 0xFFD9;

		#endregion

		#region Fields

		// the codestream bytes.
		private readonly byte[] _data;

		// the end of the codestream (exclusive).
		private readonly int _end;

		// the PPM marker segments keyed by their Zppm index.
		private SortedDictionary<int, byte[]> _ppm;

		// reader over the merged PPM data, consumed one tile-part at a time.
		private int _ppmPosition;

		// the merged PPM data.
		private byte[] _ppmData;

		#endregion

		#region Properties

		/// <summary>
		/// The width of the reference grid (Xsiz).
		/// </summary>
		public int Xsiz;

		/// <summary>
		/// The height of the reference grid (Ysiz).
		/// </summary>
		public int Ysiz;

		/// <summary>
		/// The horizontal offset of the image area (XOsiz).
		/// </summary>
		public int XOsiz;

		/// <summary>
		/// The vertical offset of the image area (YOsiz).
		/// </summary>
		public int YOsiz;

		/// <summary>
		/// The nominal tile width (XTsiz).
		/// </summary>
		public int XTsiz;

		/// <summary>
		/// The nominal tile height (YTsiz).
		/// </summary>
		public int YTsiz;

		/// <summary>
		/// The horizontal offset of the first tile (XTOsiz).
		/// </summary>
		public int XTOsiz;

		/// <summary>
		/// The vertical offset of the first tile (YTOsiz).
		/// </summary>
		public int YTOsiz;

		/// <summary>
		/// The number of tiles across.
		/// </summary>
		public int TilesX;

		/// <summary>
		/// The number of tiles down.
		/// </summary>
		public int TilesY;

		/// <summary>
		/// The image components.
		/// </summary>
		public JpxComponentInfo[] Components;

		/// <summary>
		/// The coding parameters of the main header.
		/// </summary>
		public JpxHeaderSet Main;

		/// <summary>
		/// The tiles indexed by their raster index; null entries were not found in the codestream.
		/// </summary>
		public JpxTileInfo[] Tiles;

		/// <summary>
		/// True when the codestream was truncated or a marker segment was corrupt.
		/// </summary>
		public bool Truncated;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of <see cref="JpxCodestream"/>.
		/// </summary>
		/// <param name="data">The buffer containing the codestream.</param>
		/// <param name="end">The end of the codestream in <paramref name="data"/>.</param>
		private JpxCodestream(byte[] data, int end)
		{
			_data = data;
			_end = end;
		}

		#endregion

		#region Methods

		/// <summary>
		/// Parses the codestream that starts at <paramref name="offset"/>.
		/// </summary>
		/// <param name="data">The buffer containing the codestream.</param>
		/// <param name="offset">The position of the SOC marker.</param>
		/// <param name="length">The length of the codestream.</param>
		/// <param name="headerOnly">True to stop after the main header.</param>
		/// <returns>The parsed codestream, or null when the main header is unusable.</returns>
		public static JpxCodestream Parse(byte[] data, int offset, int length, bool headerOnly)
		{
			if (data == null || offset < 0 || length < 4 || offset + length > data.Length)
				return null;

			var codestream = new JpxCodestream(data, offset + length);
			try
			{
				if (!codestream.ParseMainHeader(offset, headerOnly, out var position))
					return null;

				if (!headerOnly)
					codestream.ParseTileParts(position);

				return codestream;
			}
			catch (IndexOutOfRangeException)
			{
				// a marker segment ran past the end of the data: keep what was parsed, provided the header is usable.
				codestream.Truncated = true;
				return codestream.Main != null && codestream.Main.Cod != null && codestream.Main.Qcd != null ? codestream : null;
			}
		}

		/// <summary>
		/// Returns the bounds of tile <paramref name="index"/> on the reference grid.
		/// </summary>
		/// <param name="index">The tile index in raster order.</param>
		/// <param name="x0">Receives the left edge.</param>
		/// <param name="y0">Receives the top edge.</param>
		/// <param name="x1">Receives the right edge (exclusive).</param>
		/// <param name="y1">Receives the bottom edge (exclusive).</param>
		public void GetTileBounds(int index, out int x0, out int y0, out int x1, out int y1)
		{
			var p = index % this.TilesX;
			var q = index / this.TilesX;
			x0 = (int)Math.Max((long)this.XTOsiz + (long)p * this.XTsiz, this.XOsiz);
			y0 = (int)Math.Max((long)this.YTOsiz + (long)q * this.YTsiz, this.YOsiz);
			x1 = (int)Math.Min((long)this.XTOsiz + (long)(p + 1) * this.XTsiz, this.Xsiz);
			y1 = (int)Math.Min((long)this.YTOsiz + (long)(q + 1) * this.YTsiz, this.Ysiz);
		}

		#endregion

		#region Implementation

		// reads a big-endian 16-bit value.
		private int U16(int position)
		{
			return (_data[position] << 8) | _data[position + 1];
		}

		// reads a big-endian 32-bit value.
		private int U32(int position)
		{
			return (_data[position] << 24) | (_data[position + 1] << 16) | (_data[position + 2] << 8) | _data[position + 3];
		}

		// parses the main header up to the first SOT marker.
		private bool ParseMainHeader(int position, bool headerOnly, out int next)
		{
			next = position;
			if (position + 4 > _end || U16(position) != SOC)
				return false;

			position += 2;
			while (position + 4 <= _end)
			{
				var marker = U16(position);
				if (marker == SOT || marker == EOC)
					break;

				// tolerate fill bytes between marker segments.
				if ((marker & 0xFF00) != 0xFF00)
				{
					position++;
					continue;
				}

				// the reserved markers 0xFF30 to 0xFF3F have no marker segment.
				if (marker >= 0xFF30 && marker <= 0xFF3F)
				{
					position += 2;
					continue;
				}

				var segmentLength = U16(position + 2);
				var start = position + 4;
				var end = position + 2 + segmentLength;
				if (segmentLength < 2 || end > _end)
				{
					this.Truncated = true;
					break;
				}

				switch (marker)
				{
					case SIZ:
						if (!ParseSiz(start, end))
							return false;
						if (headerOnly)
						{
							next = end;
							return true;
						}
						break;

					default:
						if (this.Main == null)
						{
							// SIZ must come first; skip anything that precedes it.
							break;
						}
						ParseMarker(marker, start, end, this.Main, null);
						break;
				}

				position = end;
			}

			next = position;
			if (this.Main == null)
				return false;

			// a codestream without COD or QCD cannot be decoded; fall back to sensible defaults
			// rather than failing, since some writers rely on the defaults.
			if (this.Main.Cod == null)
				this.Main.Cod = CreateDefaultCod();
			if (this.Main.Qcd == null)
				this.Main.Qcd = new JpxQuantization { Style = 0, GuardBits = 2, Exponents = new[] { 8 }, Mantissas = new[] { 0 } };

			// merge the PPM segments into one stream in Zppm order.
			if (_ppm != null)
			{
				var merged = new MemoryStream();
				foreach (var segment in _ppm.Values)
					merged.Write(segment, 0, segment.Length);
				_ppmData = merged.ToArray();
				_ppmPosition = 0;
			}

			return true;
		}

		// creates the COD defaults used when the main header has none.
		private static JpxCodingDefaults CreateDefaultCod()
		{
			var style = new JpxCodingStyle
			{
				Levels = 5,
				CodeBlockWidthExp = 6,
				CodeBlockHeightExp = 6,
				Reversible = true,
				PrecinctWidthExp = new int[6],
				PrecinctHeightExp = new int[6],
			};
			for (int i = 0; i < 6; i++)
			{
				style.PrecinctWidthExp[i] = 15;
				style.PrecinctHeightExp[i] = 15;
			}

			return new JpxCodingDefaults { Layers = 1, Style = style };
		}

		// parses the SIZ marker segment (A.5.1).
		private bool ParseSiz(int start, int end)
		{
			if (end - start < 36)
				return false;

			this.Xsiz = U32(start + 2);
			this.Ysiz = U32(start + 6);
			this.XOsiz = U32(start + 10);
			this.YOsiz = U32(start + 14);
			this.XTsiz = U32(start + 18);
			this.YTsiz = U32(start + 22);
			this.XTOsiz = U32(start + 26);
			this.YTOsiz = U32(start + 30);
			var count = U16(start + 34);

			// the values are unsigned 32-bit integers: anything that doesn't fit in an int is unusable here.
			if (this.Xsiz <= 0 || this.Ysiz <= 0 || this.XOsiz < 0 || this.YOsiz < 0 || this.XOsiz >= this.Xsiz || this.YOsiz >= this.Ysiz)
				return false;
			if (this.XTsiz <= 0 || this.YTsiz <= 0 || this.XTOsiz < 0 || this.YTOsiz < 0 || this.XTOsiz > this.XOsiz || this.YTOsiz > this.YOsiz)
				return false;
			if ((long)this.XTOsiz + this.XTsiz <= this.XOsiz || (long)this.YTOsiz + this.YTsiz <= this.YOsiz)
				return false;
			if (count < 1 || count > 16384 || start + 36 + count * 3 > end)
				return false;

			this.TilesX = (int)(((long)this.Xsiz - this.XTOsiz + this.XTsiz - 1) / this.XTsiz);
			this.TilesY = (int)(((long)this.Ysiz - this.YTOsiz + this.YTsiz - 1) / this.YTsiz);
			if ((long)this.TilesX * this.TilesY > 65535)
				return false;

			this.Components = new JpxComponentInfo[count];
			for (int i = 0; i < count; i++)
			{
				var p = start + 36 + i * 3;
				var ssiz = _data[p];
				var component = new JpxComponentInfo
				{
					Precision = (ssiz & 0x7F) + 1,
					Signed = (ssiz & 0x80) != 0,
					XRsiz = _data[p + 1],
					YRsiz = _data[p + 2],
				};
				if (component.XRsiz == 0 || component.YRsiz == 0 || component.Precision > 38)
					return false;

				this.Components[i] = component;
			}

			this.Main = new JpxHeaderSet(count);
			this.Tiles = new JpxTileInfo[this.TilesX * this.TilesY];
			return true;
		}

		// parses a marker segment that may appear in the main header or in a tile-part header.
		private void ParseMarker(int marker, int start, int end, JpxHeaderSet header, JpxTileInfo tile)
		{
			switch (marker)
			{
				case COD:
					ParseCod(start, end, header);
					break;

				case COC:
					ParseCoc(start, end, header);
					break;

				case QCD:
					header.Qcd = ParseQuantization(start, end) ?? header.Qcd;
					break;

				case QCC:
					{
						var componentBytes = this.Components.Length < 257 ? 1 : 2;
						if (end - start <= componentBytes)
							break;

						var component = componentBytes == 1 ? _data[start] : U16(start);
						var quantization = ParseQuantization(start + componentBytes, end);
						if (component < header.Qcc.Length && quantization != null)
							header.Qcc[component] = quantization;
					}
					break;

				case RGN:
					{
						var componentBytes = this.Components.Length < 257 ? 1 : 2;
						if (end - start < componentBytes + 2)
							break;

						var component = componentBytes == 1 ? _data[start] : U16(start);
						var style = _data[start + componentBytes];
						if (component < header.RoiShift.Length && style == 0)
							header.RoiShift[component] = _data[start + componentBytes + 1];
					}
					break;

				case POC:
					ParsePoc(start, end, header);
					break;

				case PPM:
					if (tile == null && end > start)
					{
						if (_ppm == null)
							_ppm = new SortedDictionary<int, byte[]>();

						var bytes = new byte[end - start - 1];
						Buffer.BlockCopy(_data, start + 1, bytes, 0, bytes.Length);

						// Zppm wraps around after 255 segments: keep arrival order for duplicates.
						var key = (int)_data[start];
						while (_ppm.ContainsKey(key))
							key += 256;
						_ppm[key] = bytes;
					}
					break;

				case PPT:
					if (tile != null && end > start)
					{
						if (tile.Ppt == null)
							tile.Ppt = new SortedDictionary<int, byte[]>();

						var bytes = new byte[end - start - 1];
						Buffer.BlockCopy(_data, start + 1, bytes, 0, bytes.Length);

						// Zppt wraps around after 255 segments: keep arrival order for duplicates.
						var key = (int)_data[start];
						while (tile.Ppt.ContainsKey(key))
							key += 256;
						tile.Ppt[key] = bytes;
					}
					break;

				// TLM, PLM, PLT, CRG, COM and unknown markers carry nothing the decoder needs.
				default:
					break;
			}
		}

		// parses the SPcod/SPcoc parameters shared by COD and COC.
		private JpxCodingStyle ParseStyle(int position, int end, bool customPrecincts)
		{
			if (position + 5 > end)
				return null;

			var style = new JpxCodingStyle
			{
				Levels = Math.Min((int)_data[position], 32),
				CodeBlockWidthExp = Math.Min(_data[position + 1] + 2, 10),
				CodeBlockHeightExp = Math.Min(_data[position + 2] + 2, 10),
				CodeBlockStyle = _data[position + 3],
				Reversible = _data[position + 4] == 1,
			};

			// the code-block area is limited to 4096 samples (A.6.1).
			while (style.CodeBlockWidthExp + style.CodeBlockHeightExp > 12)
			{
				if (style.CodeBlockWidthExp > style.CodeBlockHeightExp)
					style.CodeBlockWidthExp--;
				else
					style.CodeBlockHeightExp--;
			}

			var count = style.Levels + 1;
			style.PrecinctWidthExp = new int[count];
			style.PrecinctHeightExp = new int[count];
			for (int i = 0; i < count; i++)
			{
				var value = 0xFF;
				if (customPrecincts && position + 5 + i < end)
					value = _data[position + 5 + i];

				style.PrecinctWidthExp[i] = customPrecincts ? value & 0x0F : 15;
				style.PrecinctHeightExp[i] = customPrecincts ? value >> 4 : 15;
			}

			return style;
		}

		// parses the COD marker segment (A.6.1).
		private void ParseCod(int start, int end, JpxHeaderSet header)
		{
			if (end - start < 10)
				return;

			var scod = _data[start];
			var style = ParseStyle(start + 5, end, (scod & 1) != 0);
			if (style == null)
				return;

			header.Cod = new JpxCodingDefaults
			{
				UsesSop = (scod & 2) != 0,
				UsesEph = (scod & 4) != 0,
				Progression = _data[start + 1],
				Layers = Math.Max(1, U16(start + 2)),
				Mct = _data[start + 4],
				Style = style,
			};
		}

		// parses the COC marker segment (A.6.2).
		private void ParseCoc(int start, int end, JpxHeaderSet header)
		{
			var componentBytes = this.Components.Length < 257 ? 1 : 2;
			if (end - start < componentBytes + 6)
				return;

			var component = componentBytes == 1 ? _data[start] : U16(start);
			var scoc = _data[start + componentBytes];
			var style = ParseStyle(start + componentBytes + 1, end, (scoc & 1) != 0);
			if (style != null && component < header.Coc.Length)
				header.Coc[component] = style;
		}

		// parses the Sqcx/SPqcx parameters of QCD and QCC (A.6.4).
		private JpxQuantization ParseQuantization(int start, int end)
		{
			if (end - start < 2)
				return null;

			var sq = _data[start];
			var quantization = new JpxQuantization
			{
				Style = sq & 0x1F,
				GuardBits = sq >> 5,
			};

			var position = start + 1;
			if (quantization.Style == 0)
			{
				var count = end - position;
				quantization.Exponents = new int[count];
				quantization.Mantissas = new int[count];
				for (int i = 0; i < count; i++)
					quantization.Exponents[i] = _data[position + i] >> 3;
			}
			else
			{
				var count = (end - position) / 2;
				if (count < 1)
					return null;
				if (quantization.Style == 1)
					count = 1;

				quantization.Exponents = new int[count];
				quantization.Mantissas = new int[count];
				for (int i = 0; i < count; i++)
				{
					var value = U16(position + i * 2);
					quantization.Exponents[i] = value >> 11;
					quantization.Mantissas[i] = value & 0x7FF;
				}
			}

			return quantization.Exponents.Length > 0 ? quantization : null;
		}

		// parses the POC marker segment (A.6.6).
		private void ParsePoc(int start, int end, JpxHeaderSet header)
		{
			var componentBytes = this.Components.Length < 257 ? 1 : 2;
			var entrySize = 5 + 2 * componentBytes;
			if (header.Poc == null)
				header.Poc = new List<JpxProgressionChange>();

			for (int position = start; position + entrySize <= end; position += entrySize)
			{
				var p = position;
				var change = new JpxProgressionChange();
				change.ResolutionStart = _data[p++];
				change.ComponentStart = componentBytes == 1 ? _data[p] : U16(p);
				p += componentBytes;
				change.LayerEnd = U16(p);
				p += 2;
				change.ResolutionEnd = _data[p++];
				change.ComponentEnd = componentBytes == 1 ? _data[p] : U16(p);
				p += componentBytes;
				change.Progression = _data[p];

				// CEpoc = 0 stands for 256 components (A.6.6).
				if (change.ComponentEnd == 0)
					change.ComponentEnd = 256;

				header.Poc.Add(change);
			}
		}

		// parses the tile-parts that follow the main header.
		private void ParseTileParts(int position)
		{
			while (position + 12 <= _end)
			{
				var marker = U16(position);
				if (marker == EOC)
					return;

				if (marker != SOT || U16(position + 2) != 10)
				{
					// a wrong Psot or junk between tile-parts: look for the next SOT marker segment.
					var found = FindSot(position + 1);
					if (found < 0)
					{
						this.Truncated = true;
						return;
					}

					position = found;
					continue;
				}

				var sotStart = position;
				var tileIndex = U16(position + 4);
				var psot = (uint)U32(position + 6);
				position += 12;

				// the tile-part ends Psot bytes after the SOT marker, or at EOC when Psot is 0.
				long partEnd = psot == 0 ? _end : sotStart + (long)psot;
				if (psot != 0 && psot < 14)
				{
					// impossible length: assume the tile-part runs to the end of the data.
					partEnd = _end;
					this.Truncated = true;
				}
				else if (partEnd > _end)
				{
					partEnd = _end;
					this.Truncated = true;
				}

				JpxTileInfo tile = null;
				if (tileIndex < this.Tiles.Length)
				{
					tile = this.Tiles[tileIndex];
					if (tile == null)
					{
						tile = new JpxTileInfo { Index = tileIndex, Header = new JpxHeaderSet(this.Components.Length) };
						this.Tiles[tileIndex] = tile;
					}
				}

				// tile-part header.
				var dataStart = -1;
				while (position + 2 <= partEnd)
				{
					marker = U16(position);
					if (marker == SOD)
					{
						dataStart = position + 2;
						break;
					}

					if (marker >= 0xFF30 && marker <= 0xFF3F)
					{
						position += 2;
						continue;
					}

					if (position + 4 > partEnd)
						break;

					var segmentLength = U16(position + 2);
					var end = position + 2 + segmentLength;
					if (segmentLength < 2 || end > partEnd)
						break;

					if (tile != null)
						ParseMarker(marker, position + 4, end, tile.Header, tile);

					position = end;
				}

				if (dataStart < 0)
				{
					this.Truncated = true;
					return;
				}

				// strip the EOC marker from the last tile-part when Psot is 0.
				var dataEnd = (int)partEnd;
				if (psot == 0 && dataEnd - 2 >= dataStart && U16(dataEnd - 2) == EOC)
					dataEnd -= 2;

				if (tile != null)
				{
					tile.Data.Write(_data, dataStart, dataEnd - dataStart);

					// assign the next chunk of PPM packet headers to this tile-part.
					if (_ppmData != null)
						TakePpmChunk(tile);
				}

				position = (int)partEnd;
			}
		}

		// appends the packet headers of the next tile-part from the merged PPM data.
		private void TakePpmChunk(JpxTileInfo tile)
		{
			if (tile.PacketHeaders == null)
				tile.PacketHeaders = new MemoryStream();

			if (_ppmPosition + 4 > _ppmData.Length)
				return;

			var length = (_ppmData[_ppmPosition] << 24) | (_ppmData[_ppmPosition + 1] << 16) | (_ppmData[_ppmPosition + 2] << 8) | _ppmData[_ppmPosition + 3];
			_ppmPosition += 4;
			if (length < 0 || length > _ppmData.Length - _ppmPosition)
				length = _ppmData.Length - _ppmPosition;

			tile.PacketHeaders.Write(_ppmData, _ppmPosition, length);
			_ppmPosition += length;
		}

		// finds the next SOT marker segment at or after position.
		private int FindSot(int position)
		{
			for (int i = position; i + 12 <= _end; i++)
			{
				if (_data[i] == 0xFF && _data[i + 1] == 0x90 && _data[i + 2] == 0 && _data[i + 3] == 10)
					return i;
			}

			return -1;
		}

		#endregion
	}
}
