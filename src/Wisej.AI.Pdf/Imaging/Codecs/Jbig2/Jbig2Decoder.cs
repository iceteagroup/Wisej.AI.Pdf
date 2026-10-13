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

// JBIG2 decoder for the JBIG2Decode filter (ISO 32000-2, 7.4.7; ITU T.88, embedded stream organization).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Decodes the JBIG2-compressed images of PDF files.
	/// </summary>
	/// <remarks>
	/// <para>
	/// PDF embeds JBIG2 data in the "embedded stream" organization of T.88 Annex D.3: no file header,
	/// a sequence of segments for a single page, and optionally a second stream (JBIG2Globals) with the
	/// segments shared by several images, such as symbol dictionaries and custom Huffman tables.
	/// </para>
	/// <para>
	/// All the region and dictionary types of T.88 are supported. Segments of unknown types are
	/// skipped, and a corrupt segment only loses its own contribution: everything decoded before it
	/// is kept. The decoder never throws.
	/// </para>
	/// </remarks>
	internal static class Jbig2Decoder
	{
		#region Methods

		/// <summary>
		/// Decodes the page of a JBIG2 image stream.
		/// </summary>
		/// <param name="data">The image stream.</param>
		/// <param name="globals">The decoded JBIG2Globals stream, or null.</param>
		/// <param name="width">Returns the page width in pixels.</param>
		/// <param name="height">Returns the page height in pixels.</param>
		/// <returns>The packed 1-bit rows of the page, <c>(width + 7) / 8</c> bytes per row, where
		/// a 1 bit is white and a 0 bit is black as the JBIG2Decode filter requires; or null when
		/// nothing can be decoded.</returns>
		public static byte[] Decode(byte[] data, byte[] globals, out int width, out int height)
		{
			width = 0;
			height = 0;
			if (data == null)
				return null;

			try
			{
				var session = new Session();
				if (globals != null && globals.Length > 0)
					session.Process(globals);

				session.Process(data);

				var page = session.GetPage();
				if (page == null || page.Width == 0 || page.Height == 0)
					return null;

				// the PDF filter inverts JBIG2's polarity: 1 bits are white.
				var output = page.Data;
				for (var i = 0; i < output.Length; i++)
					output[i] = (byte)~output[i];

				width = page.Width;
				height = page.Height;
				return output;
			}
			catch (Exception)
			{
				// the session catches errors per segment; this only guards against the unexpected.
				return null;
			}
		}

		#endregion

		#region Nested types

		/// <summary>
		/// The region segment information field (7.4.1).
		/// </summary>
		private struct RegionInfo
		{
			/// <summary>The width of the region.</summary>
			public long Width;

			/// <summary>The height of the region.</summary>
			public long Height;

			/// <summary>The left edge of the region on the page.</summary>
			public long X;

			/// <summary>The top edge of the region on the page.</summary>
			public long Y;

			/// <summary>The external combination operator.</summary>
			public int CombinationOperator;
		}

		/// <summary>
		/// The result of a symbol dictionary segment.
		/// </summary>
		private sealed class SymbolDictionaryResult
		{
			/// <summary>The exported symbols.</summary>
			public List<Jbig2Bitmap> Symbols;

			/// <summary>The arithmetic statistics retained for later dictionaries, or null.</summary>
			public Jbig2ArithmeticContexts Contexts;
		}

		/// <summary>
		/// The result of a pattern dictionary segment.
		/// </summary>
		private sealed class PatternDictionaryResult
		{
			/// <summary>The patterns.</summary>
			public List<Jbig2Bitmap> Patterns;
		}

		/// <summary>
		/// The result of an intermediate region segment.
		/// </summary>
		private sealed class RegionResult
		{
			/// <summary>The decoded region.</summary>
			public Jbig2Bitmap Bitmap;
		}

		/// <summary>
		/// The state of one decoding: the page and the results of the segments decoded so far.
		/// </summary>
		private sealed class Session
		{
			#region Fields

			/// <summary>The allocator enforcing the memory budget.</summary>
			private readonly Jbig2Allocator _allocator = new Jbig2Allocator();

			/// <summary>The results of the dictionary, table and intermediate region segments, by segment number.</summary>
			private readonly Dictionary<uint, object> _results = new Dictionary<uint, object>();

			/// <summary>The page bitmap, created by the page information segment.</summary>
			private Jbig2Bitmap _page;

			/// <summary>The page default pixel value.</summary>
			private int _pageDefaultPixel;

			/// <summary>The page default combination operator.</summary>
			private int _pageOperator;

			/// <summary>True when regions may override the page default combination operator.</summary>
			private bool _pageOperatorOverride;

			/// <summary>True when the page height is unknown and grows with the stripes.</summary>
			private bool _pageHeightUnknown;

			/// <summary>True once the end of the page or of the file has been reached.</summary>
			private bool _finished;

			#endregion

			#region Methods

			/// <summary>
			/// Returns the page bitmap, or null when there was no page information segment.
			/// </summary>
			/// <returns>The page.</returns>
			public Jbig2Bitmap GetPage()
			{
				return _page;
			}

			/// <summary>
			/// Decodes the segments of an embedded stream.
			/// </summary>
			/// <param name="data">The stream.</param>
			public void Process(byte[] data)
			{
				var position = 0;
				var end = data.Length;

				// tolerate a stream with the file header of the standalone format (D.4.1).
				if (end >= 9 && data[0] == 0x97 && data[1] == 0x4A && data[2] == 0x42 && data[3] == 0x32
					&& data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
				{
					var fileFlags = data[8];
					position = 9 + ((fileFlags & 2) == 0 ? 4 : 0);

					// the random-access organization stores all the headers first: not used in PDF.
					if ((fileFlags & 1) == 0)
						return;
				}

				while (!_finished && position < end)
				{
					var segment = Jbig2Segment.Read(data, ref position, end);
					if (segment == null)
						break;

					try
					{
						ProcessSegment(segment, data);
					}
					catch (Exception)
					{
						// a corrupt segment is skipped; the following segments are independent.
					}
				}
			}

			#endregion

			#region Implementation

			// dispatches a segment by type (7.3).
			private void ProcessSegment(Jbig2Segment segment, byte[] data)
			{
				switch (segment.Type)
				{
					case Jbig2Segment.SymbolDictionary:
						ProcessSymbolDictionary(segment, data);
						break;

					case Jbig2Segment.IntermediateTextRegion:
					case Jbig2Segment.ImmediateTextRegion:
					case Jbig2Segment.ImmediateLosslessTextRegion:
						ProcessTextRegion(segment, data);
						break;

					case Jbig2Segment.PatternDictionary:
						ProcessPatternDictionary(segment, data);
						break;

					case Jbig2Segment.IntermediateHalftoneRegion:
					case Jbig2Segment.ImmediateHalftoneRegion:
					case Jbig2Segment.ImmediateLosslessHalftoneRegion:
						ProcessHalftoneRegion(segment, data);
						break;

					case Jbig2Segment.IntermediateGenericRegion:
					case Jbig2Segment.ImmediateGenericRegion:
					case Jbig2Segment.ImmediateLosslessGenericRegion:
						ProcessGenericRegion(segment, data);
						break;

					case Jbig2Segment.IntermediateRefinementRegion:
					case Jbig2Segment.ImmediateRefinementRegion:
					case Jbig2Segment.ImmediateLosslessRefinementRegion:
						ProcessRefinementRegion(segment, data);
						break;

					case Jbig2Segment.PageInformation:
						ProcessPageInformation(segment, data);
						break;

					case Jbig2Segment.EndOfPage:
					case Jbig2Segment.EndOfFile:
						_finished = _page != null;
						break;

					case Jbig2Segment.EndOfStripe:
						ProcessEndOfStripe(segment, data);
						break;

					case Jbig2Segment.Tables:
						var table = Jbig2HuffmanTable.Parse(data, segment.DataStart, segment.DataEnd);
						if (table != null)
							_results[segment.Number] = table;

						break;

					default:
						// profiles, extensions, colour palettes and unknown types do not affect the page.
						break;
				}
			}

			// 7.4.8: page information.
			private void ProcessPageInformation(Jbig2Segment segment, byte[] data)
			{
				// a second page in the stream is not part of the image.
				if (_page != null)
				{
					_finished = true;
					return;
				}

				var pos = segment.DataStart;
				if (segment.DataEnd - pos < 19)
					return;

				long width = Jbig2Segment.ReadUInt32(data, pos);
				long height = Jbig2Segment.ReadUInt32(data, pos + 4);
				var flags = data[pos + 16];
				var striping = (data[pos + 17] << 8) | data[pos + 18];

				_pageDefaultPixel = (flags >> 2) & 1;
				_pageOperator = (flags >> 3) & 3;
				_pageOperatorOverride = (flags & 0x40) != 0;

				// 7.4.8.2: with an unknown height, the page grows with every stripe.
				if (height == 0xFFFFFFFF)
				{
					_pageHeightUnknown = true;
					height = (striping & 0x8000) != 0 ? Math.Min(striping & 0x7FFF, Jbig2Allocator.MaxPixels / Math.Max(1, width + 8)) : 0;
				}

				_allocator.GrantPage(width, height);
				_page = _allocator.Create(width, height);
				_page.Fill(_pageDefaultPixel);
			}

			// 7.4.10: end of stripe; for pages of unknown height, it fixes the height so far.
			private void ProcessEndOfStripe(Jbig2Segment segment, byte[] data)
			{
				if (_page == null || !_pageHeightUnknown || segment.DataEnd - segment.DataStart < 4)
					return;

				var lastRow = (long)Jbig2Segment.ReadUInt32(data, segment.DataStart);
				EnsurePageHeight(lastRow + 1);
			}

			// grows a page of unknown height to at least the given height.
			private void EnsurePageHeight(long height)
			{
				if (!_pageHeightUnknown || height <= _page.Height)
					return;

				// a page that would exceed the size limit keeps its current height and clips.
				if ((_page.Width + 8L) * height > Jbig2Allocator.MaxPixels)
					return;

				_allocator.GrantPage(_page.Width, height - _page.Height);
				_allocator.Reserve(_page.Width, height - _page.Height);
				_page.Resize((int)height, _pageDefaultPixel);
			}

			// returns true when a region segment can affect the page: immediate regions need a page,
			// intermediate ones are kept for later refinement regions.
			private bool IsNeeded(Jbig2Segment segment)
			{
				switch (segment.Type)
				{
					case Jbig2Segment.IntermediateTextRegion:
					case Jbig2Segment.IntermediateHalftoneRegion:
					case Jbig2Segment.IntermediateGenericRegion:
					case Jbig2Segment.IntermediateRefinementRegion:
						return true;

					default:
						return _page != null;
				}
			}

			// draws a region on the page, or stores the region of an intermediate segment.
			private void Complete(Jbig2Segment segment, RegionInfo info, Jbig2Bitmap bitmap, bool intermediate)
			{
				if (intermediate)
				{
					_results[segment.Number] = new RegionResult { Bitmap = bitmap };
					return;
				}

				if (_page == null)
					return;

				EnsurePageHeight(info.Y + bitmap.Height);

				var op = _pageOperatorOverride ? info.CombinationOperator : _pageOperator;
				_page.Compose(bitmap, info.X, info.Y, op);
			}

			// 7.4.1: reads the region segment information field.
			private static RegionInfo ReadRegionInfo(byte[] data, int pos, int end)
			{
				if (end - pos < 17)
					throw new System.IO.InvalidDataException("Truncated region segment.");

				return new RegionInfo
				{
					Width = Jbig2Segment.ReadUInt32(data, pos),
					Height = Jbig2Segment.ReadUInt32(data, pos + 4),
					X = (int)Jbig2Segment.ReadUInt32(data, pos + 8),
					Y = (int)Jbig2Segment.ReadUInt32(data, pos + 12),
					CombinationOperator = Math.Min(data[pos + 16] & 7, Jbig2Bitmap.OperatorReplace),
				};
			}

			// reads AT pixel pairs as signed bytes.
			private static int[] ReadAt(byte[] data, ref int pos, int pairs)
			{
				var at = new int[pairs * 2];
				for (var i = 0; i < at.Length; i++)
					at[i] = (sbyte)data[pos++];

				return at;
			}

			// 7.4.6: generic region segment.
			private void ProcessGenericRegion(Jbig2Segment segment, byte[] data)
			{
				// regions are only decoded when they can contribute to the page.
				if (!IsNeeded(segment))
					return;

				var pos = segment.DataStart;
				var end = segment.DataEnd;
				var info = ReadRegionInfo(data, pos, end);
				pos += 17;

				var flags = data[pos++];
				var mmr = (flags & 1) != 0;
				var template = (flags >> 1) & 3;
				var typicalPrediction = (flags & 8) != 0;
				var extendedTemplate = (flags & 0x10) != 0;

				int[] at = null;
				if (!mmr)
				{
					// the extended template of T.88 Amendment 2 is not used in PDF; its 12 AT pixels
					// are read so that the coded data is located correctly.
					var pairs = template == 0 ? (extendedTemplate ? 12 : 4) : 1;
					at = ReadAt(data, ref pos, pairs);
				}

				// with an unknown data length, the real height follows the coded data (7.2.7).
				var height = segment.RowCount >= 0 ? segment.RowCount : info.Height;

				// rows are decoded top to bottom, so the rows below a page of known height can be
				// left out: they would be clipped anyway.
				var intermediate = segment.Type == Jbig2Segment.IntermediateGenericRegion;
				if (!intermediate && _page != null && !_pageHeightUnknown)
					height = Math.Max(0, Math.Min(height, _page.Height - info.Y));

				var bitmap = _allocator.Create(info.Width, height);

				try
				{
					if (mmr)
						Jbig2MmrDecoder.Decode(new Jbig2BitReader(data, pos, end), bitmap);
					else
						Jbig2GenericRegion.Decode(new Jbig2ArithmeticDecoder(data, pos, end), new byte[Jbig2GenericRegion.GenericContextSize], bitmap, template, typicalPrediction, at, null);
				}
				finally
				{
					// whatever was decoded before an error is still drawn.
					Complete(segment, info, bitmap, intermediate);
				}
			}

			// 7.4.7: generic refinement region segment.
			private void ProcessRefinementRegion(Jbig2Segment segment, byte[] data)
			{
				// regions are only decoded when they can contribute to the page.
				if (!IsNeeded(segment))
					return;

				var pos = segment.DataStart;
				var end = segment.DataEnd;
				var info = ReadRegionInfo(data, pos, end);
				pos += 17;

				var flags = data[pos++];
				var template = flags & 1;
				var typicalPrediction = (flags & 2) != 0;
				var at = template == 0 ? ReadAt(data, ref pos, 2) : null;

				// 7.4.7.5: the reference is the referred-to intermediate region, or else the part of
				// the page under this region.
				Jbig2Bitmap reference = null;
				foreach (var number in segment.ReferredTo)
				{
					if (_results.TryGetValue(number, out var result) && result is RegionResult region)
					{
						reference = region.Bitmap;
						break;
					}
				}

				if (reference == null)
				{
					if (_page == null)
						return;

					_allocator.Reserve(info.Width, info.Height);
					reference = _page.Extract((int)info.X, (int)info.Y, (int)info.Width, (int)info.Height);
				}

				var bitmap = _allocator.Create(info.Width, info.Height);
				try
				{
					var decoder = new Jbig2ArithmeticDecoder(data, pos, end);
					Jbig2GenericRegion.DecodeRefinement(decoder, new byte[Jbig2GenericRegion.RefinementContextSize], bitmap, template, reference, 0, 0, typicalPrediction, at);
				}
				finally
				{
					Complete(segment, info, bitmap, segment.Type == Jbig2Segment.IntermediateRefinementRegion);
				}
			}

			// 7.4.2: symbol dictionary segment.
			private void ProcessSymbolDictionary(Jbig2Segment segment, byte[] data)
			{
				var pos = segment.DataStart;
				var end = segment.DataEnd;
				if (end - pos < 2)
					return;

				var flags = (data[pos] << 8) | data[pos + 1];
				pos += 2;

				var p = new Jbig2SymbolDictionaryParameters
				{
					Huffman = (flags & 1) != 0,
					RefinementAggregate = (flags & 2) != 0,
					Template = (flags >> 10) & 3,
					RefinementTemplate = (flags >> 12) & 1,
				};

				var contextUsed = (flags & 0x100) != 0;
				var contextRetained = (flags & 0x200) != 0;

				if (!p.Huffman)
					p.At = ReadAt(data, ref pos, p.Template == 0 ? 4 : 1);

				if (p.RefinementAggregate && p.RefinementTemplate == 0)
					p.RefinementAt = ReadAt(data, ref pos, 2);

				if (end - pos < 8)
					return;

				p.NumberOfExportedSymbols = Jbig2Segment.ReadUInt32(data, pos);
				p.NumberOfNewSymbols = Jbig2Segment.ReadUInt32(data, pos + 4);
				pos += 8;

				// the input symbols are the exports of the referred-to dictionaries, in order.
				var inputSymbols = new List<Jbig2Bitmap>();
				var tables = new List<Jbig2HuffmanTable>();
				SymbolDictionaryResult lastDictionary = null;
				foreach (var number in segment.ReferredTo)
				{
					if (!_results.TryGetValue(number, out var result))
						continue;

					if (result is SymbolDictionaryResult dictionary)
					{
						inputSymbols.AddRange(dictionary.Symbols);
						lastDictionary = dictionary;
					}
					else if (result is Jbig2HuffmanTable table)
					{
						tables.Add(table);
					}
				}

				p.InputSymbols = inputSymbols;

				// 7.4.2.1.6: the Huffman tables, the custom ones taken in order from the referred tables.
				if (p.Huffman)
				{
					var custom = 0;
					var dhSelection = (flags >> 2) & 3;
					var dwSelection = (flags >> 4) & 3;
					p.TableDh = dhSelection == 3 ? NextTable(tables, ref custom) : Jbig2HuffmanTable.GetStandardTable(dhSelection == 0 ? 4 : 5);
					p.TableDw = dwSelection == 3 ? NextTable(tables, ref custom) : Jbig2HuffmanTable.GetStandardTable(dwSelection == 0 ? 2 : 3);
					p.TableBitmapSize = (flags & 0x40) != 0 ? NextTable(tables, ref custom) : Jbig2HuffmanTable.GetStandardTable(1);
					p.TableAggregateInstances = (flags & 0x80) != 0 ? NextTable(tables, ref custom) : Jbig2HuffmanTable.GetStandardTable(1);
					if (p.TableDh == null || p.TableDw == null || p.TableBitmapSize == null || p.TableAggregateInstances == null)
						return;
				}

				// 7.4.2.2: "bitmap coding context used" continues the statistics of the last referred-to
				// dictionary; they are copied so that the retained ones stay intact.
				Jbig2ArithmeticContexts contexts;
				if (contextUsed && lastDictionary?.Contexts != null)
				{
					contexts = new Jbig2ArithmeticContexts(
						(byte[])lastDictionary.Contexts.Generic.Clone(),
						(byte[])lastDictionary.Contexts.Refinement.Clone());
				}
				else
				{
					contexts = new Jbig2ArithmeticContexts();
				}

				var symbols = Jbig2SymbolDictionary.Decode(p, data, pos, end, contexts, _allocator);
				_results[segment.Number] = new SymbolDictionaryResult
				{
					Symbols = symbols,
					Contexts = contextRetained ? contexts : null,
				};
			}

			// 7.4.3: text region segment.
			private void ProcessTextRegion(Jbig2Segment segment, byte[] data)
			{
				// regions are only decoded when they can contribute to the page.
				if (!IsNeeded(segment))
					return;

				var pos = segment.DataStart;
				var end = segment.DataEnd;
				var info = ReadRegionInfo(data, pos, end);
				pos += 17;

				var flags = (data[pos] << 8) | data[pos + 1];
				pos += 2;

				var p = new Jbig2TextRegionParameters
				{
					Huffman = (flags & 1) != 0,
					Refine = (flags & 2) != 0,
					LogStrips = (flags >> 2) & 3,
					ReferenceCorner = (flags >> 4) & 3,
					Transposed = (flags & 0x40) != 0,
					CombinationOperator = (flags >> 7) & 3,

					// SBDSOFFSET is a signed 5-bit value.
					DsOffset = ((flags >> 10) & 0x1F) >= 16 ? ((flags >> 10) & 0x1F) - 32 : (flags >> 10) & 0x1F,
					RefinementTemplate = (flags >> 15) & 1,
				};

				var defaultPixel = (flags >> 9) & 1;

				var huffmanFlags = 0;
				if (p.Huffman)
				{
					huffmanFlags = (data[pos] << 8) | data[pos + 1];
					pos += 2;
				}

				if (p.Refine && p.RefinementTemplate == 0)
					p.RefinementAt = ReadAt(data, ref pos, 2);

				p.NumberOfInstances = Jbig2Segment.ReadUInt32(data, pos);
				pos += 4;

				// the symbols are the exports of the referred-to dictionaries, in order.
				var symbols = new List<Jbig2Bitmap>();
				var tables = new List<Jbig2HuffmanTable>();
				foreach (var number in segment.ReferredTo)
				{
					if (!_results.TryGetValue(number, out var result))
						continue;

					if (result is SymbolDictionaryResult dictionary)
						symbols.AddRange(dictionary.Symbols);
					else if (result is Jbig2HuffmanTable table)
						tables.Add(table);
				}

				p.Symbols = symbols;

				var bitmap = _allocator.Create(info.Width, info.Height);
				if (defaultPixel != 0)
					bitmap.Fill(1);

				Jbig2ArithmeticDecoder decoder = null;
				Jbig2BitReader reader = null;
				try
				{
					if (p.Huffman)
					{
						reader = new Jbig2BitReader(data, pos, end);
						if (!SelectTextRegionTables(p, huffmanFlags, tables))
							return;

						// 7.4.3.1.7: the symbol ID code lengths are coded at the start of the data.
						p.SymbolIdTable = DecodeSymbolIdTable(reader, symbols.Count);
						if (p.SymbolIdTable == null)
							return;

						reader.AlignToByte();
					}
					else
					{
						decoder = new Jbig2ArithmeticDecoder(data, pos, end);
						var codeLength = 0;
						while ((1L << codeLength) < symbols.Count)
							codeLength++;

						p.SymbolCodeLength = codeLength;
					}

					Jbig2TextRegion.Decode(p, bitmap, decoder, new Jbig2ArithmeticContexts(), reader, _allocator);
				}
				finally
				{
					Complete(segment, info, bitmap, segment.Type == Jbig2Segment.IntermediateTextRegion);
				}
			}

			// 7.4.3.1.6: selects the Huffman tables of a text region.
			private static bool SelectTextRegionTables(Jbig2TextRegionParameters p, int flags, List<Jbig2HuffmanTable> tables)
			{
				var custom = 0;
				p.TableFs = SelectTable(flags & 3, new[] { 6, 7, 0 }, tables, ref custom);
				p.TableDs = SelectTable((flags >> 2) & 3, new[] { 8, 9, 10 }, tables, ref custom);
				p.TableDt = SelectTable((flags >> 4) & 3, new[] { 11, 12, 13 }, tables, ref custom);
				p.TableRdw = SelectTable((flags >> 6) & 3, new[] { 14, 15, 0 }, tables, ref custom);
				p.TableRdh = SelectTable((flags >> 8) & 3, new[] { 14, 15, 0 }, tables, ref custom);
				p.TableRdx = SelectTable((flags >> 10) & 3, new[] { 14, 15, 0 }, tables, ref custom);
				p.TableRdy = SelectTable((flags >> 12) & 3, new[] { 14, 15, 0 }, tables, ref custom);
				p.TableRsize = (flags & 0x4000) != 0 ? NextTable(tables, ref custom) : Jbig2HuffmanTable.GetStandardTable(1);

				return p.TableFs != null && p.TableDs != null && p.TableDt != null
					&& p.TableRdw != null && p.TableRdh != null && p.TableRdx != null && p.TableRdy != null
					&& p.TableRsize != null;
			}

			// returns the standard table for a selection value, or the next custom table for 3.
			private static Jbig2HuffmanTable SelectTable(int selection, int[] standard, List<Jbig2HuffmanTable> tables, ref int custom)
			{
				if (selection == 3)
					return NextTable(tables, ref custom);

				var number = standard[selection];
				return number == 0 ? null : Jbig2HuffmanTable.GetStandardTable(number);
			}

			// returns the next custom table referred to by the segment, or null.
			private static Jbig2HuffmanTable NextTable(List<Jbig2HuffmanTable> tables, ref int custom)
			{
				return custom < tables.Count ? tables[custom++] : null;
			}

			// 7.4.3.1.7: decodes the run-length coded symbol ID code lengths of a Huffman text region.
			private static Jbig2HuffmanTable DecodeSymbolIdTable(Jbig2BitReader reader, int symbolCount)
			{
				// the prefix lengths of the 35 run codes.
				var runLengths = new int[35];
				for (var i = 0; i < runLengths.Length; i++)
					runLengths[i] = (int)reader.ReadBits(4);

				var runTable = Jbig2HuffmanTable.FromCodeLengths(runLengths);

				var lengths = new int[symbolCount];
				var index = 0;
				while (index < symbolCount)
				{
					if (reader.IsExhausted)
						return null;

					var code = runTable.Decode(reader);
					if (code == Jbig2HuffmanTable.Oob || code < 0 || code > 34)
						return null;

					if (code < 32)
					{
						lengths[index++] = code;
						continue;
					}

					// 32 repeats the previous length 3 to 6 times; 33 and 34 code runs of zeros.
					int repeat, value;
					if (code == 32)
					{
						if (index == 0)
							return null;

						repeat = 3 + (int)reader.ReadBits(2);
						value = lengths[index - 1];
					}
					else if (code == 33)
					{
						repeat = 3 + (int)reader.ReadBits(3);
						value = 0;
					}
					else
					{
						repeat = 11 + (int)reader.ReadBits(7);
						value = 0;
					}

					for (var i = 0; i < repeat && index < symbolCount; i++)
						lengths[index++] = value;
				}

				return Jbig2HuffmanTable.FromCodeLengths(lengths);
			}

			// 7.4.4: pattern dictionary segment.
			private void ProcessPatternDictionary(Jbig2Segment segment, byte[] data)
			{
				var pos = segment.DataStart;
				var end = segment.DataEnd;
				if (end - pos < 7)
					return;

				var flags = data[pos];
				var mmr = (flags & 1) != 0;
				var template = (flags >> 1) & 3;
				var patternWidth = data[pos + 1];
				var patternHeight = data[pos + 2];
				var grayMax = Jbig2Segment.ReadUInt32(data, pos + 3);
				pos += 7;

				var patterns = Jbig2Halftone.DecodePatterns(mmr, template, patternWidth, patternHeight, grayMax, data, pos, end, _allocator);
				_results[segment.Number] = new PatternDictionaryResult { Patterns = patterns };
			}

			// 7.4.5: halftone region segment.
			private void ProcessHalftoneRegion(Jbig2Segment segment, byte[] data)
			{
				// regions are only decoded when they can contribute to the page.
				if (!IsNeeded(segment))
					return;

				var pos = segment.DataStart;
				var end = segment.DataEnd;
				var info = ReadRegionInfo(data, pos, end);
				pos += 17;
				if (end - pos < 21)
					return;

				var flags = data[pos];
				var p = new Jbig2HalftoneParameters
				{
					Mmr = (flags & 1) != 0,
					Template = (flags >> 1) & 3,
					EnableSkip = (flags & 8) != 0,
					CombinationOperator = Math.Min((flags >> 4) & 7, Jbig2Bitmap.OperatorReplace),
					GridWidth = Jbig2Segment.ReadUInt32(data, pos + 1),
					GridHeight = Jbig2Segment.ReadUInt32(data, pos + 5),
					GridX = (int)Jbig2Segment.ReadUInt32(data, pos + 9),
					GridY = (int)Jbig2Segment.ReadUInt32(data, pos + 13),
					VectorX = (data[pos + 17] << 8) | data[pos + 18],
					VectorY = (data[pos + 19] << 8) | data[pos + 20],
				};

				var defaultPixel = (flags >> 7) & 1;
				pos += 21;

				// the region refers to exactly one pattern dictionary.
				List<Jbig2Bitmap> patterns = null;
				foreach (var number in segment.ReferredTo)
				{
					if (_results.TryGetValue(number, out var result) && result is PatternDictionaryResult dictionary)
					{
						patterns = dictionary.Patterns;
						break;
					}
				}

				var bitmap = _allocator.Create(info.Width, info.Height);
				if (defaultPixel != 0)
					bitmap.Fill(1);

				try
				{
					Jbig2Halftone.DecodeRegion(p, patterns, bitmap, data, pos, end, _allocator);
				}
				finally
				{
					Complete(segment, info, bitmap, segment.Type == Jbig2Segment.IntermediateHalftoneRegion);
				}
			}

			#endregion
		}

		#endregion
	}
}
