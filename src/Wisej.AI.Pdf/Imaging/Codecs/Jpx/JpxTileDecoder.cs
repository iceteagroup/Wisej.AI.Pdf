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

// JPEG 2000 tile decoding: packets and progression orders (Annex B), dequantization (Annex E),
// inverse wavelet (Annex F) and multiple component transforms (Annex G).

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Receives the reconstructed samples of the image components.
	/// </summary>
	internal sealed class JpxComponentPlanes
	{
		/// <summary>
		/// The samples of each component, offset to the unsigned range, in raster order.
		/// </summary>
		public ushort[][] Samples;

		/// <summary>
		/// The width of each component plane.
		/// </summary>
		public int[] Widths;

		/// <summary>
		/// The height of each component plane.
		/// </summary>
		public int[] Heights;

		/// <summary>
		/// The left edge of each component plane in (reduced) component coordinates.
		/// </summary>
		public int[] X0;

		/// <summary>
		/// The top edge of each component plane in (reduced) component coordinates.
		/// </summary>
		public int[] Y0;

		/// <summary>
		/// The precision of the stored samples of each component (at most 16 bits).
		/// </summary>
		public int[] Precisions;
	}

	/// <summary>
	/// Decodes the tiles of a codestream into component planes.
	/// </summary>
	/// <remarks>
	/// An instance keeps scratch buffers between tiles and is not thread safe.
	/// </remarks>
	internal sealed class JpxTileDecoder
	{
		#region Nested types

		/// <summary>
		/// One code-block contribution read from a packet header.
		/// </summary>
		private struct Chunk
		{
			// the code-block that receives the data.
			public JpxCodeBlock Block;

			// the number of bytes.
			public int Length;

			// the number of coding passes.
			public int Passes;

			// true when the chunk starts a codeword segment.
			public bool NewSegment;

			// true when the resolution level is discarded by the reduce option.
			public bool Discard;
		}

		/// <summary>
		/// One code-block to decode, with the sub-band and component it belongs to.
		/// </summary>
		private struct BlockJob
		{
			// the tile-component.
			public JpxTileComponent Component;

			// the sub-band.
			public JpxBand Band;

			// the code-block.
			public JpxCodeBlock Block;
		}

		#endregion

		#region Constants

		// upper bounds that protect against absurd allocations from corrupt headers.

		/// <summary>The largest number of precincts allowed in one tile, over all components and resolutions.</summary>
		private const int MaxPrecincts = 1 << 22;

		/// <summary>The largest number of code-blocks allowed in one tile, over all components and sub-bands.</summary>
		private const int MaxCodeBlocks = 1 << 22;

		// the number of code-blocks from which tier-1 decoding is spread over several threads.
		private const int ParallelThreshold = 16;

		// the largest number of precinct positions walked by the position-driven progressions.
		private const long MaxPositions = 1L << 24;

		// the largest number of packets visited in one tile.
		private const long MaxPacketVisits = 1L << 26;

		#endregion

		#region Fields

		// the codestream.
		private readonly JpxCodestream _codestream;

		// the number of highest resolution levels to discard.
		private readonly int _reduce;

		// the destination planes.
		private readonly JpxComponentPlanes _planes;

		// tier-1 decoder reused for all the code-blocks.
		private readonly JpxCodeBlockDecoder _blockDecoder = new JpxCodeBlockDecoder();

		// packet header reader.
		private readonly JpxBitReader _headerReader = new JpxBitReader();

		// code-block contributions of the current packet.
		private readonly List<Chunk> _chunks = new List<Chunk>();

		// state of the tile being decoded.

		/// <summary>The tile-components of the current tile.</summary>
		private JpxTileComponent[] _components;

		/// <summary>The COD parameters in effect for the current tile.</summary>
		private JpxCodingDefaults _cod;

		/// <summary>The left edge of the current tile on the reference grid.</summary>
		private int _tileX0;

		/// <summary>The top edge of the current tile on the reference grid.</summary>
		private int _tileY0;

		/// <summary>The right edge (exclusive) of the current tile on the reference grid.</summary>
		private int _tileX1;

		/// <summary>The bottom edge (exclusive) of the current tile on the reference grid.</summary>
		private int _tileY1;

		/// <summary>The bit-stream of the current tile (concatenated tile-part bodies).</summary>
		private byte[] _body;

		/// <summary>The read position in <see cref="_body"/>.</summary>
		private int _bodyPosition;

		/// <summary>The end (exclusive) of the valid data in <see cref="_body"/>.</summary>
		private int _bodyEnd;

		/// <summary>The packed packet headers (PPM/PPT) of the current tile, when <see cref="_separateHeaders"/> is set.</summary>
		private byte[] _headers;

		/// <summary>The end (exclusive) of the valid data in <see cref="_headers"/>.</summary>
		private int _headersEnd;

		/// <summary>True when the packet headers come from PPM/PPT markers instead of the bit-stream.</summary>
		private bool _separateHeaders;

		// the number of packets visited in the tile, bounded to stop absurd progression volumes.
		private long _packetVisits;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of <see cref="JpxTileDecoder"/>.
		/// </summary>
		/// <param name="codestream">The parsed codestream.</param>
		/// <param name="reduce">The number of highest resolution levels to discard.</param>
		/// <param name="planes">The destination planes.</param>
		public JpxTileDecoder(JpxCodestream codestream, int reduce, JpxComponentPlanes planes)
		{
			_codestream = codestream;
			_reduce = reduce;
			_planes = planes;
		}

		#endregion

		#region Properties

		/// <summary>
		/// True when some data was missing or could not be decoded.
		/// </summary>
		public bool Incomplete;

		#endregion

		#region Methods

		/// <summary>
		/// Returns the coding style of component <paramref name="component"/> in <paramref name="tile"/>, applying the
		/// precedence rules of A.6: tile COC, tile COD, main COC, main COD.
		/// </summary>
		/// <param name="codestream">The codestream.</param>
		/// <param name="tile">The tile, or null for the main header only.</param>
		/// <param name="component">The component index.</param>
		/// <returns>The coding style.</returns>
		public static JpxCodingStyle ResolveStyle(JpxCodestream codestream, JpxTileInfo tile, int component)
		{
			if (tile != null)
			{
				if (tile.Header.Coc[component] != null)
					return tile.Header.Coc[component];
				if (tile.Header.Cod != null)
					return tile.Header.Cod.Style;
			}

			return codestream.Main.Coc[component] ?? codestream.Main.Cod.Style;
		}

		/// <summary>
		/// Decodes one tile into the component planes.
		/// </summary>
		/// <param name="tile">The tile.</param>
		public void Decode(JpxTileInfo tile)
		{
			var header = tile.Header;
			_cod = header.Cod ?? _codestream.Main.Cod;
			_codestream.GetTileBounds(tile.Index, out _tileX0, out _tileY0, out _tileX1, out _tileY1);
			if (_tileX1 <= _tileX0 || _tileY1 <= _tileY0)
				return;

			if (!BuildComponents(tile))
			{
				this.Incomplete = true;
				return;
			}

			// tier-2: collect the code-block data from the packets.
			_packetVisits = 0;
			_body = tile.Data.GetBuffer();
			_bodyPosition = 0;
			_bodyEnd = (int)tile.Data.Length;

			if (tile.Ppt != null)
			{
				tile.PacketHeaders = new MemoryStream();
				foreach (var segment in tile.Ppt.Values)
					tile.PacketHeaders.Write(segment, 0, segment.Length);
			}

			_separateHeaders = tile.PacketHeaders != null;
			if (_separateHeaders)
			{
				_headers = tile.PacketHeaders.GetBuffer();
				_headersEnd = (int)tile.PacketHeaders.Length;
				_headerReader.Reset(_headers, 0, _headersEnd);
			}

			if (!DecodePackets(header.Poc ?? _codestream.Main.Poc))
				this.Incomplete = true;

			// tier-1, dequantization and inverse wavelet transform of each component.
			Reconstruct();

			ApplyComponentTransform();
			StoreSamples();

			_components = null;
			_body = null;
			_headers = null;
		}

		#endregion

		#region Implementation: structure

		// ceil(value / 2^exponent) for non-negative values.
		private static int CeilShift(int value, int exponent)
		{
			if (exponent >= 31)
				return value > 0 ? 1 : 0;

			return (int)(((long)value + (1L << exponent) - 1) >> exponent);
		}

		// ceil(value / 2^exponent) for any sign.
		private static long CeilShiftSigned(long value, int exponent)
		{
			return -((-value) >> Math.Min(exponent, 62));
		}

		// ceil(a / b) for non-negative values.
		private static int CeilDiv(int a, int b)
		{
			return (int)(((long)a + b - 1) / b);
		}

		// builds the resolution, sub-band, precinct and code-block structures of every component.
		private bool BuildComponents(JpxTileInfo tile)
		{
			var header = tile.Header;
			var main = _codestream.Main;
			var count = _codestream.Components.Length;
			_components = new JpxTileComponent[count];

			long precinctTotal = 0;
			long blockTotal = 0;

			for (int c = 0; c < count; c++)
			{
				var info = _codestream.Components[c];
				var style = ResolveStyle(_codestream, tile, c);
				var quantization = header.Qcc[c] ?? header.Qcd ?? main.Qcc[c] ?? main.Qcd;
				var roi = header.RoiShift[c] >= 0 ? header.RoiShift[c] : main.RoiShift[c];

				var component = new JpxTileComponent
				{
					X0 = CeilDiv(_tileX0, info.XRsiz),
					Y0 = CeilDiv(_tileY0, info.YRsiz),
					X1 = CeilDiv(_tileX1, info.XRsiz),
					Y1 = CeilDiv(_tileY1, info.YRsiz),
					Style = style,
					RoiShift = Math.Max(0, roi),
					Resolutions = new JpxResolution[style.Levels + 1],
					DecodedLevel = Math.Max(0, style.Levels - _reduce),
				};
				_components[c] = component;

				// the code-block style of Part 15 (HTJ2K) is not supported.
				if ((style.CodeBlockStyle & JpxCodeBlockDecoder.StyleHighThroughput) != 0)
					this.Incomplete = true;

				var levels = style.Levels;
				for (int r = 0; r <= levels; r++)
				{
					var levelShift = levels - r;
					var resolution = new JpxResolution
					{
						X0 = CeilShift(component.X0, levelShift),
						Y0 = CeilShift(component.Y0, levelShift),
						X1 = CeilShift(component.X1, levelShift),
						Y1 = CeilShift(component.Y1, levelShift),
						PrecinctWidthExp = style.PrecinctWidthExp[r],
						PrecinctHeightExp = style.PrecinctHeightExp[r],
					};
					component.Resolutions[r] = resolution;

					var ppx = resolution.PrecinctWidthExp;
					var ppy = resolution.PrecinctHeightExp;
					if (resolution.X1 > resolution.X0 && resolution.Y1 > resolution.Y0)
					{
						resolution.PrecinctsWide = CeilShift(resolution.X1, ppx) - (resolution.X0 >> ppx);
						resolution.PrecinctsHigh = CeilShift(resolution.Y1, ppy) - (resolution.Y0 >> ppy);
					}

					precinctTotal += (long)resolution.PrecinctsWide * resolution.PrecinctsHigh;
					if (precinctTotal > MaxPrecincts)
						return false;

					BuildBands(component, resolution, r, info, quantization, style);
					if (!BuildPrecincts(resolution, r, style, ref blockTotal))
						return false;
				}
			}

			return true;
		}

		// creates the sub-bands of a resolution level with their quantization parameters.
		private static void BuildBands(JpxTileComponent component, JpxResolution resolution, int r, JpxComponentInfo info, JpxQuantization quantization, JpxCodingStyle style)
		{
			var levels = style.Levels;
			if (r == 0)
			{
				resolution.Bands = new[]
				{
					new JpxBand { Type = 0, X0 = resolution.X0, Y0 = resolution.Y0, X1 = resolution.X1, Y1 = resolution.Y1 },
				};
			}
			else
			{
				// nb is the number of decomposition levels between the tile-component and the sub-band (B.5).
				var nb = levels - r + 1;
				var lower = component.Resolutions[r - 1];
				resolution.Bands = new JpxBand[3];
				for (int b = 0; b < 3; b++)
				{
					var type = b + 1;
					var xob = type & 1;
					var yob = type >> 1;
					resolution.Bands[b] = new JpxBand
					{
						Type = type,
						X0 = (int)CeilShiftSigned(component.X0 - ((long)xob << (nb - 1)), nb),
						Y0 = (int)CeilShiftSigned(component.Y0 - ((long)yob << (nb - 1)), nb),
						X1 = (int)CeilShiftSigned(component.X1 - ((long)xob << (nb - 1)), nb),
						Y1 = (int)CeilShiftSigned(component.Y1 - ((long)yob << (nb - 1)), nb),

						// the sub-band position in the interleaved (Mallat) coefficient layout.
						OffsetX = xob != 0 ? lower.Width : 0,
						OffsetY = yob != 0 ? lower.Height : 0,
					};
				}
			}

			foreach (var band in resolution.Bands)
			{
				// the sub-band index in the QCD/QCC parameters: LL, then HL, LH, HH for each level.
				var index = r == 0 ? 0 : 3 * (r - 1) + band.Type;
				int exponent, mantissa;
				if (quantization.Style == 1)
				{
					// scalar derived: the step sizes follow from the LL one (E.1.1.2).
					var nb = r == 0 ? levels : levels - r + 1;
					exponent = quantization.Exponents[0] - levels + nb;
					mantissa = quantization.Mantissas[0];
				}
				else
				{
					var i = Math.Min(index, quantization.Exponents.Length - 1);
					exponent = quantization.Exponents[i];
					mantissa = quantization.Mantissas[i];
				}

				band.MagnitudeBits = quantization.GuardBits + exponent - 1;

				if (quantization.Style == 0)
				{
					// no quantization: the coefficients are integers.
					band.Scale = 0.5f;
				}
				else
				{
					// the nominal dynamic range adds the log2 gain of the sub-band (E.1.1.1).
					var gain = band.Type == 0 ? 0 : band.Type == 3 ? 2 : 1;
					var range = Math.Min(info.Precision, 16) + gain;
					var step = Math.Pow(2.0, range - exponent) * (1.0 + mantissa / 2048.0);
					band.Scale = (float)(step * 0.5);
				}
			}
		}

		// creates the precincts of a resolution level and their code-blocks.
		private static bool BuildPrecincts(JpxResolution resolution, int r, JpxCodingStyle style, ref long blockTotal)
		{
			var ppx = resolution.PrecinctWidthExp;
			var ppy = resolution.PrecinctHeightExp;

			// below the lowest resolution the precinct partition is halved in the sub-bands (B.6).
			var bandPpx = r == 0 ? ppx : Math.Max(0, ppx - 1);
			var bandPpy = r == 0 ? ppy : Math.Max(0, ppy - 1);
			var xcb = Math.Min(style.CodeBlockWidthExp, bandPpx);
			var ycb = Math.Min(style.CodeBlockHeightExp, bandPpy);

			var wide = resolution.PrecinctsWide;
			var high = resolution.PrecinctsHigh;
			resolution.Precincts = new JpxPrecinct[wide * high];

			var firstX = resolution.X0 >> ppx;
			var firstY = resolution.Y0 >> ppy;

			for (int j = 0; j < high; j++)
			{
				for (int i = 0; i < wide; i++)
				{
					var precinct = new JpxPrecinct { Bands = new JpxPrecinctBand[resolution.Bands.Length] };
					resolution.Precincts[j * wide + i] = precinct;

					for (int b = 0; b < resolution.Bands.Length; b++)
					{
						var band = resolution.Bands[b];
						var px0 = Math.Max((long)(firstX + i) << bandPpx, band.X0);
						var py0 = Math.Max((long)(firstY + j) << bandPpy, band.Y0);
						var px1 = Math.Min((long)(firstX + i + 1) << bandPpx, band.X1);
						var py1 = Math.Min((long)(firstY + j + 1) << bandPpy, band.Y1);

						var precinctBand = new JpxPrecinctBand();
						precinct.Bands[b] = precinctBand;
						if (px1 <= px0 || py1 <= py0)
						{
							precinctBand.Blocks = new JpxCodeBlock[0];
							continue;
						}

						var cbx0 = (int)(px0 >> xcb);
						var cby0 = (int)(py0 >> ycb);
						var cbx1 = (int)((px1 + (1L << xcb) - 1) >> xcb);
						var cby1 = (int)((py1 + (1L << ycb) - 1) >> ycb);
						var cbw = cbx1 - cbx0;
						var cbh = cby1 - cby0;

						blockTotal += (long)cbw * cbh;
						if (blockTotal > MaxCodeBlocks)
							return false;

						var blocks = new JpxCodeBlock[cbw * cbh];
						for (int y = 0; y < cbh; y++)
						{
							for (int x = 0; x < cbw; x++)
							{
								blocks[y * cbw + x] = new JpxCodeBlock
								{
									X0 = (int)Math.Max((long)(cbx0 + x) << xcb, band.X0),
									Y0 = (int)Math.Max((long)(cby0 + y) << ycb, band.Y0),
									X1 = (int)Math.Min((long)(cbx0 + x + 1) << xcb, band.X1),
									Y1 = (int)Math.Min((long)(cby0 + y + 1) << ycb, band.Y1),
								};
							}
						}

						precinctBand.Blocks = blocks;
						precinctBand.Inclusion = new JpxTagTree(cbw, cbh);
						precinctBand.ZeroBitPlanes = new JpxTagTree(cbw, cbh);
					}
				}
			}

			return true;
		}

		#endregion

		#region Implementation: tier-2

		// iterates the packets of the tile in the progression order(s); returns false when the data ran out.
		private bool DecodePackets(List<JpxProgressionChange> changes)
		{
			var componentCount = _components.Length;
			var maxResolutions = 0;
			foreach (var component in _components)
				maxResolutions = Math.Max(maxResolutions, component.Resolutions.Length);

			if (changes == null || changes.Count == 0)
			{
				var progression = new JpxProgressionChange
				{
					ResolutionStart = 0,
					ComponentStart = 0,
					LayerEnd = _cod.Layers,
					ResolutionEnd = maxResolutions,
					ComponentEnd = componentCount,
					Progression = _cod.Progression,
				};
				return DecodeProgression(progression, maxResolutions);
			}

			foreach (var change in changes)
			{
				if (!DecodeProgression(change, maxResolutions))
					return false;
			}

			return true;
		}

		// decodes the packets of one progression volume (B.12).
		private bool DecodeProgression(JpxProgressionChange change, int maxResolutions)
		{
			var layerEnd = Math.Min(change.LayerEnd, _cod.Layers);
			var resolutionStart = change.ResolutionStart;
			var resolutionEnd = Math.Min(change.ResolutionEnd, maxResolutions);
			var componentStart = change.ComponentStart;
			var componentEnd = Math.Min(change.ComponentEnd, _components.Length);

			switch (change.Progression)
			{
				case 0:
					// layer, resolution, component, position.
					for (int l = 0; l < layerEnd; l++)
					{
						for (int r = resolutionStart; r < resolutionEnd; r++)
						{
							for (int c = componentStart; c < componentEnd; c++)
							{
								if (!DecodeAllPrecincts(c, r, l))
									return false;
							}
						}
					}
					return true;

				case 1:
					// resolution, layer, component, position.
					for (int r = resolutionStart; r < resolutionEnd; r++)
					{
						for (int l = 0; l < layerEnd; l++)
						{
							for (int c = componentStart; c < componentEnd; c++)
							{
								if (!DecodeAllPrecincts(c, r, l))
									return false;
							}
						}
					}
					return true;

				case 2:
				case 3:
				case 4:
					return DecodePositional(change.Progression, layerEnd, resolutionStart, resolutionEnd, componentStart, componentEnd);

				default:
					// unknown progression order: nothing can be read reliably.
					return false;
			}
		}

		// decodes the packets of every precinct of a resolution level for one layer.
		private bool DecodeAllPrecincts(int c, int r, int layer)
		{
			var component = _components[c];
			if (r >= component.Resolutions.Length)
				return true;

			var resolution = component.Resolutions[r];
			for (int p = 0; p < resolution.Precincts.Length; p++)
			{
				if (!DecodePacket(component, r, resolution.Precincts[p], layer))
					return false;
			}

			return true;
		}

		// decodes the position-driven progressions RPCL, PCRL and CPRL (B.12.1.3 to B.12.1.5).
		private bool DecodePositional(int order, int layerEnd, int resolutionStart, int resolutionEnd, int componentStart, int componentEnd)
		{
			// the step between precinct origins on the reference grid.
			long stepX = long.MaxValue;
			long stepY = long.MaxValue;
			for (int c = componentStart; c < componentEnd; c++)
			{
				var component = _components[c];
				var info = _codestream.Components[c];
				var levels = component.Resolutions.Length - 1;
				for (int r = 0; r <= levels; r++)
				{
					var resolution = component.Resolutions[r];
					var shiftX = Math.Min(resolution.PrecinctWidthExp + levels - r, 40);
					var shiftY = Math.Min(resolution.PrecinctHeightExp + levels - r, 40);
					stepX = Math.Min(stepX, (long)info.XRsiz << shiftX);
					stepY = Math.Min(stepY, (long)info.YRsiz << shiftY);
				}
			}

			// corrupt precinct sizes could make the walk over the reference grid endless.
			if (stepX == long.MaxValue || stepY == long.MaxValue)
				return true;

			if (((long)_tileX1 - _tileX0) / stepX * (((long)_tileY1 - _tileY0) / stepY) > MaxPositions)
				return false;

			switch (order)
			{
				case 2:
					// resolution, position, component, layer.
					for (int r = resolutionStart; r < resolutionEnd; r++)
					{
						for (long y = _tileY0; y < _tileY1; y += stepY - (y % stepY))
						{
							for (long x = _tileX0; x < _tileX1; x += stepX - (x % stepX))
							{
								for (int c = componentStart; c < componentEnd; c++)
								{
									if (!DecodeAt(c, r, x, y, layerEnd))
										return false;
								}
							}
						}
					}
					return true;

				case 3:
					// position, component, resolution, layer.
					for (long y = _tileY0; y < _tileY1; y += stepY - (y % stepY))
					{
						for (long x = _tileX0; x < _tileX1; x += stepX - (x % stepX))
						{
							for (int c = componentStart; c < componentEnd; c++)
							{
								for (int r = resolutionStart; r < resolutionEnd; r++)
								{
									if (!DecodeAt(c, r, x, y, layerEnd))
										return false;
								}
							}
						}
					}
					return true;

				default:
					// component, position, resolution, layer.
					for (int c = componentStart; c < componentEnd; c++)
					{
						for (long y = _tileY0; y < _tileY1; y += stepY - (y % stepY))
						{
							for (long x = _tileX0; x < _tileX1; x += stepX - (x % stepX))
							{
								for (int r = resolutionStart; r < resolutionEnd; r++)
								{
									if (!DecodeAt(c, r, x, y, layerEnd))
										return false;
								}
							}
						}
					}
					return true;
			}
		}

		// decodes the layers of the precinct of (c, r) that starts at reference grid position (x, y), if any.
		private bool DecodeAt(int c, int r, long x, long y, int layerEnd)
		{
			var component = _components[c];
			if (r >= component.Resolutions.Length)
				return true;

			var resolution = component.Resolutions[r];
			if (resolution.PrecinctsWide == 0 || resolution.PrecinctsHigh == 0)
				return true;

			var info = _codestream.Components[c];
			var levelShift = component.Resolutions.Length - 1 - r;
			var ppx = resolution.PrecinctWidthExp;
			var ppy = resolution.PrecinctHeightExp;
			var shiftX = Math.Min(ppx + levelShift, 40);
			var shiftY = Math.Min(ppy + levelShift, 40);

			// a precinct starts here when the position is on the precinct grid, or at the tile origin
			// when the first precinct is cut by the tile edge (B.12.1.3).
			long x0 = resolution.X0;
			long y0 = resolution.Y0;
			var onRow = y % ((long)info.YRsiz << shiftY) == 0 || y == _tileY0 && ((y0 << Math.Min(levelShift, 30)) % (1L << shiftY)) != 0;
			if (!onRow)
				return true;

			var onColumn = x % ((long)info.XRsiz << shiftX) == 0 || x == _tileX0 && ((x0 << Math.Min(levelShift, 30)) % (1L << shiftX)) != 0;
			if (!onColumn)
				return true;

			var scaleX = (long)info.XRsiz << Math.Min(levelShift, 30);
			var scaleY = (long)info.YRsiz << Math.Min(levelShift, 30);
			var i = (((x + scaleX - 1) / scaleX) >> ppx) - (x0 >> ppx);
			var j = (((y + scaleY - 1) / scaleY) >> ppy) - (y0 >> ppy);
			if (i < 0 || j < 0 || i >= resolution.PrecinctsWide || j >= resolution.PrecinctsHigh)
				return true;

			var precinct = resolution.Precincts[j * resolution.PrecinctsWide + i];
			for (int l = 0; l < layerEnd; l++)
			{
				if (!DecodePacket(component, r, precinct, l))
					return false;
			}

			return true;
		}

		// returns the pass index that ends the codeword segment containing pass p (B.10.7.2, D.4.1).
		private static int SegmentEnd(int pass, int style)
		{
			if ((style & JpxCodeBlockDecoder.StyleTermAll) != 0)
				return pass + 1;

			if ((style & JpxCodeBlockDecoder.StyleBypass) != 0)
			{
				// the first ten passes form one MQ segment; then each SP+MR pair is a raw segment and
				// each cleanup pass an MQ segment.
				if (pass < 10)
					return 10;

				return (pass - 10) % 3 == 0 ? pass + 2 : pass + 1;
			}

			return int.MaxValue;
		}

		// floor(log2(value)) for value >= 1.
		private static int FloorLog2(int value)
		{
			var result = 0;
			while ((value >>= 1) != 0)
				result++;

			return result;
		}

		// reads the number of coding passes (Table B.4).
		private static int ReadPassCount(JpxBitReader reader)
		{
			if (reader.ReadBit() == 0)
				return 1;

			if (reader.ReadBit() == 0)
				return 2;

			var value = reader.ReadBits(2);
			if (value != 3)
				return 3 + value;

			value = reader.ReadBits(5);
			if (value != 31)
				return 6 + value;

			return 37 + reader.ReadBits(7);
		}

		// decodes one packet (B.9, B.10); returns false when the data ran out.
		private bool DecodePacket(JpxTileComponent component, int r, JpxPrecinct precinct, int layer)
		{
			// packets of a precinct come in layer order; anything else was already read or is out of sequence.
			if (++_packetVisits > MaxPacketVisits)
				return false;

			if (layer != precinct.NextLayer)
				return true;

			precinct.NextLayer++;

			var body = _body;
			var discard = r > component.DecodedLevel;
			var style = component.Style.CodeBlockStyle;

			// skip the SOP marker segment that may precede the packet.
			if (_cod.UsesSop && _bodyPosition + 6 <= _bodyEnd && body[_bodyPosition] == 0xFF && body[_bodyPosition + 1] == 0x91)
				_bodyPosition += 6;

			var reader = _headerReader;
			if (!_separateHeaders)
			{
				if (_bodyPosition >= _bodyEnd)
					return false;

				reader.Reset(body, _bodyPosition, _bodyEnd);
			}
			else if (reader.Position >= _headersEnd)
			{
				return false;
			}

			_chunks.Clear();

			// the first bit tells whether the packet is empty.
			if (reader.ReadBit() != 0)
			{
				foreach (var precinctBand in precinct.Bands)
				{
					var blocks = precinctBand.Blocks;
					for (int b = 0; b < blocks.Length; b++)
					{
						var block = blocks[b];

						// inclusion: a tag tree until the first inclusion, a single bit afterwards (B.10.4).
						bool included;
						if (!block.Included)
							included = precinctBand.Inclusion.Decode(reader, b, layer + 1);
						else
							included = reader.ReadBit() != 0;

						if (!included)
							continue;

						if (!block.Included)
						{
							// the number of missing most significant bit-planes (B.10.5).
							var threshold = 1;
							while (!precinctBand.ZeroBitPlanes.Decode(reader, b, threshold) && threshold < 64)
								threshold++;

							block.ZeroBitPlanes = threshold - 1;
							block.Included = true;
						}

						var passes = ReadPassCount(reader);

						// Lblock grows by the number of leading 1 bits (B.10.7.1).
						while (reader.ReadBit() != 0 && block.LBlock < 32)
							block.LBlock++;

						// one length per codeword segment touched by the new passes (B.10.7.2).
						var pass = block.Passes;
						var remaining = passes;
						while (remaining > 0)
						{
							var count = Math.Min(remaining, SegmentEnd(pass, style) - pass);
							var bits = block.LBlock + FloorLog2(count);
							if (bits > 31)
								return false;

							_chunks.Add(new Chunk
							{
								Block = block,
								Length = reader.ReadBits(bits),
								Passes = count,
								NewSegment = pass == 0 || SegmentEnd(pass - 1, style) == pass,
								Discard = discard,
							});

							pass += count;
							remaining -= count;
						}

						block.Passes = pass;
					}

					if (reader.Overrun)
						return false;
				}
			}

			reader.Align();
			if (reader.Overrun)
				return false;

			// skip the EPH marker that may terminate the header.
			var headerData = _separateHeaders ? _headers : body;
			var headerEnd = _separateHeaders ? _headersEnd : _bodyEnd;
			var position = reader.Position;
			if (_cod.UsesEph && position + 2 <= headerEnd && headerData[position] == 0xFF && headerData[position + 1] == 0x92)
			{
				position += 2;
				reader.Seek(position);
			}

			if (!_separateHeaders)
				_bodyPosition = position;

			// packet body: the code-block contributions in header order.
			var complete = true;
			foreach (var chunk in _chunks)
			{
				var length = chunk.Length;
				if (length > _bodyEnd - _bodyPosition)
				{
					length = _bodyEnd - _bodyPosition;
					complete = false;
				}

				if (!chunk.Discard && length >= 0)
					chunk.Block.Append(body, _bodyPosition, length, chunk.Passes, chunk.NewSegment);

				_bodyPosition += length;
			}

			return complete;
		}

		#endregion

		#region Implementation: reconstruction

		// decodes the code-blocks of every component, then runs the inverse wavelet transforms.
		private void Reconstruct()
		{
			// allocate the coefficient buffers and collect the code-blocks that have data.
			var jobs = new List<BlockJob>();
			foreach (var component in _components)
			{
				var top = component.Resolutions[component.DecodedLevel];
				if (top.Width <= 0 || top.Height <= 0)
					continue;

				component.Samples = new float[top.Width * top.Height];

				// the high-throughput block coder (Part 15) is not supported: leave the coefficients at zero.
				if ((component.Style.CodeBlockStyle & JpxCodeBlockDecoder.StyleHighThroughput) != 0)
					continue;

				for (int r = 0; r <= component.DecodedLevel; r++)
				{
					var resolution = component.Resolutions[r];
					foreach (var precinct in resolution.Precincts)
					{
						for (int b = 0; b < precinct.Bands.Length; b++)
						{
							foreach (var block in precinct.Bands[b].Blocks)
							{
								if (block.SegmentCount > 0)
									jobs.Add(new BlockJob { Component = component, Band = resolution.Bands[b], Block = block });
							}
						}
					}
				}
			}

			// code-blocks are independent and write disjoint areas: decode them in parallel when there are enough.
			if (jobs.Count >= ParallelThreshold && Environment.ProcessorCount > 1)
			{
				Parallel.For(
					0,
					jobs.Count,
					() => new JpxCodeBlockDecoder(),
					(i, loop, decoder) =>
					{
						DecodeBlock(decoder, jobs[i]);
						return decoder;
					},
					decoder => { });
			}
			else
			{
				foreach (var job in jobs)
					DecodeBlock(_blockDecoder, job);
			}

			// the inverse wavelet transforms of the components are independent as well.
			if (jobs.Count >= ParallelThreshold && _components.Length > 1 && Environment.ProcessorCount > 1)
			{
				Parallel.For(0, _components.Length, c => InverseWavelet(_components[c]));
			}
			else
			{
				foreach (var component in _components)
					InverseWavelet(component);
			}
		}

		// decodes one code-block and stores its dequantized coefficients in the sub-band area of the buffer.
		private void DecodeBlock(JpxCodeBlockDecoder decoder, BlockJob job)
		{
			var component = job.Component;
			var band = job.Band;
			var block = job.Block;
			var samples = component.Samples;
			var width = component.Resolutions[component.DecodedLevel].Width;

			var blockWidth = block.X1 - block.X0;
			var blockHeight = block.Y1 - block.Y0;
			var planes = band.MagnitudeBits + component.RoiShift - block.ZeroBitPlanes;
			if (planes > JpxCodeBlockDecoder.MaxBitPlanes)
				this.Incomplete = true;

			decoder.Decode(block.Data, block.SegmentLengths, block.SegmentPasses, block.SegmentCount, blockWidth, blockHeight, band.Type, component.Style.CodeBlockStyle, planes, component.RoiShift);

			// release the coded data as soon as it has been decoded.
			block.Data = null;

			var coefficients = decoder.Coefficients;
			var scale = band.Scale;
			var integer = component.Style.Reversible && scale == 0.5f;
			var target = (band.OffsetY + block.Y0 - band.Y0) * width + band.OffsetX + block.X0 - band.X0;
			var source = 0;
			for (int y = 0; y < blockHeight; y++, target += width)
			{
				if (integer)
				{
					// reversible path: drop the half unit, rounding toward zero.
					for (int x = 0; x < blockWidth; x++)
					{
						var value = coefficients[source++];
						samples[target + x] = value >= 0 ? value >> 1 : -((-value) >> 1);
					}
				}
				else
				{
					for (int x = 0; x < blockWidth; x++)
						samples[target + x] = coefficients[source++] * scale;
				}
			}
		}

		// runs the inverse wavelet transform of a component.
		private static void InverseWavelet(JpxTileComponent component)
		{
			if (component.Samples == null)
				return;

			var width = component.Resolutions[component.DecodedLevel].Width;
			JpxWavelet.Inverse(component.Samples, width, component.Resolutions, component.DecodedLevel, component.Style.Reversible);
		}

		// applies the inverse RCT or ICT to the first three components (G.2, G.3).
		private void ApplyComponentTransform()
		{
			if (_cod.Mct != 1 || _components.Length < 3)
				return;

			var c0 = _components[0].Samples;
			var c1 = _components[1].Samples;
			var c2 = _components[2].Samples;
			if (c0 == null || c1 == null || c2 == null || c0.Length != c1.Length || c0.Length != c2.Length)
				return;

			var count = c0.Length;
			if (_components[0].Style.Reversible)
			{
				for (int i = 0; i < count; i++)
				{
					var y = (int)c0[i];
					var u = (int)c1[i];
					var v = (int)c2[i];
					var g = y - ((u + v) >> 2);
					c0[i] = v + g;
					c1[i] = g;
					c2[i] = u + g;
				}
			}
			else
			{
				for (int i = 0; i < count; i++)
				{
					var y = c0[i];
					var cb = c1[i];
					var cr = c2[i];
					c0[i] = y + 1.402f * cr;
					c1[i] = y - 0.34413f * cb - 0.71414f * cr;
					c2[i] = y + 1.772f * cb;
				}
			}
		}

		// applies the DC level shift and writes the samples into the component planes (G.1).
		private void StoreSamples()
		{
			for (int c = 0; c < _components.Length; c++)
			{
				var component = _components[c];
				var samples = component.Samples;
				if (samples == null)
					continue;

				var info = _codestream.Components[c];
				var plane = _planes.Samples[c];
				var top = component.Resolutions[component.DecodedLevel];
				var width = top.Width;
				var height = top.Height;
				var planeWidth = _planes.Widths[c];
				var planeHeight = _planes.Heights[c];
				var offsetX = top.X0 - _planes.X0[c];
				var offsetY = top.Y0 - _planes.Y0[c];

				// signed samples are offset to the unsigned range just like the DC level shift of unsigned ones;
				// precisions above 16 bits are scaled down to 16.
				var precision = info.Precision;
				var shift = (float)Math.Pow(2.0, precision - 1);
				var downscale = precision > 16 ? (float)Math.Pow(2.0, 16 - precision) : 1f;
				var max = (float)((1 << Math.Min(precision, 16)) - 1);

				for (int y = 0; y < height; y++)
				{
					var py = offsetY + y;
					if (py < 0 || py >= planeHeight)
						continue;

					var source = y * width;
					var target = py * planeWidth + offsetX;
					for (int x = 0; x < width; x++)
					{
						var px = offsetX + x;
						if (px < 0 || px >= planeWidth)
							continue;

						var value = (samples[source + x] + shift) * downscale;
						if (value <= 0f)
							plane[target + x] = 0;
						else if (value >= max)
							plane[target + x] = (ushort)max;
						else
							plane[target + x] = (ushort)(value + 0.5f);
					}
				}

				component.Samples = null;
			}
		}

		#endregion
	}
}
