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

// Symbol dictionary decoding procedure (ITU T.88, 6.5).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// The parameters of the symbol dictionary decoding procedure (Table 13).
	/// </summary>
	internal sealed class Jbig2SymbolDictionaryParameters
	{
		#region Fields

		/// <summary>SDHUFF: true when the dictionary is Huffman coded.</summary>
		public bool Huffman;

		/// <summary>SDREFAGG: true when symbols are refinement or aggregate coded.</summary>
		public bool RefinementAggregate;

		/// <summary>SDINSYMS: the symbols imported from the referred-to dictionaries.</summary>
		public IList<Jbig2Bitmap> InputSymbols;

		/// <summary>SDNUMNEWSYMS: the number of symbols defined by this dictionary.</summary>
		public long NumberOfNewSymbols;

		/// <summary>SDNUMEXSYMS: the number of exported symbols.</summary>
		public long NumberOfExportedSymbols;

		/// <summary>SDHUFFDH: the delta height table.</summary>
		public Jbig2HuffmanTable TableDh;

		/// <summary>SDHUFFDW: the delta width table.</summary>
		public Jbig2HuffmanTable TableDw;

		/// <summary>SDHUFFBMSIZE: the collective bitmap size table.</summary>
		public Jbig2HuffmanTable TableBitmapSize;

		/// <summary>SDHUFFAGGINST: the aggregate instance count table.</summary>
		public Jbig2HuffmanTable TableAggregateInstances;

		/// <summary>SDTEMPLATE: the generic region template.</summary>
		public int Template;

		/// <summary>SDAT: the generic region AT pixels.</summary>
		public int[] At;

		/// <summary>SDRTEMPLATE: the refinement template.</summary>
		public int RefinementTemplate;

		/// <summary>SDRAT: the refinement AT pixels.</summary>
		public int[] RefinementAt;

		#endregion
	}

	/// <summary>
	/// Implements the symbol dictionary decoding procedure (6.5.5).
	/// </summary>
	internal static class Jbig2SymbolDictionary
	{
		#region Fields

		// the largest number of symbols accepted in one dictionary.
		private const long MaxSymbols = 1 << 20;

		#endregion

		#region Methods

		/// <summary>
		/// Decodes a symbol dictionary and returns its exported symbols.
		/// </summary>
		/// <param name="parameters">The decoding parameters.</param>
		/// <param name="data">The segment data buffer.</param>
		/// <param name="start">The offset of the coded symbols.</param>
		/// <param name="end">The end of the segment data.</param>
		/// <param name="contexts">The arithmetic statistics, possibly inherited from a previous dictionary.</param>
		/// <param name="allocator">The bitmap allocator.</param>
		/// <returns>The exported symbols; on corrupt data, the symbols decoded so far.</returns>
		public static List<Jbig2Bitmap> Decode(
			Jbig2SymbolDictionaryParameters parameters,
			byte[] data,
			int start,
			int end,
			Jbig2ArithmeticContexts contexts,
			Jbig2Allocator allocator)
		{
			var p = parameters;
			var inputSymbols = p.InputSymbols;
			var newCount = Math.Min(p.NumberOfNewSymbols, MaxSymbols);
			var newSymbols = new List<Jbig2Bitmap>((int)Math.Min(newCount, 4096));

			var huffman = p.Huffman;
			var decoder = huffman ? null : new Jbig2ArithmeticDecoder(data, start, end);
			var reader = huffman ? new Jbig2BitReader(data, start, end) : null;

			// SBSYMCODELEN of the refinement/aggregate coding (6.5.8.2.3): at least 1 bit with Huffman.
			var codeLength = CeilLog2(inputSymbols.Count + newCount);
			if (huffman)
				codeLength = Math.Max(1, codeLength);

			if (codeLength > 30)
				return Export(p, newSymbols, decoder, contexts, reader);

			try
			{
				DecodeSymbols(p, newSymbols, newCount, codeLength, decoder, contexts, reader, allocator);
			}
			catch (System.IO.InvalidDataException)
			{
				// allocation refused: keep the symbols decoded so far.
			}

			return Export(p, newSymbols, decoder, contexts, reader);
		}

		#endregion

		#region Implementation

		// decodes the height classes (6.5.5 step 4).
		private static void DecodeSymbols(
			Jbig2SymbolDictionaryParameters p,
			List<Jbig2Bitmap> newSymbols,
			long newCount,
			int codeLength,
			Jbig2ArithmeticDecoder decoder,
			Jbig2ArithmeticContexts contexts,
			Jbig2BitReader reader,
			Jbig2Allocator allocator)
		{
			var huffman = p.Huffman;
			var heightClassHeight = 0L;

			// the symbols available to refinement/aggregate coding: the input symbols followed by
			// the new symbols decoded so far (6.5.8.2.4).
			var available = p.RefinementAggregate ? new List<Jbig2Bitmap>(p.InputSymbols) : null;
			var widths = huffman && !p.RefinementAggregate ? new List<int>() : null;

			// every height class should define at least one symbol; the limit on the number of classes
			// stops corrupt data that keeps producing empty ones.
			for (var heightClasses = 0L; newSymbols.Count < newCount; heightClasses++)
			{
				if (heightClasses > newCount + 16 || (decoder != null ? decoder.IsExhausted : reader.IsExhausted))
					return;

				// 6.5.6: the height class delta height.
				var dh = huffman ? p.TableDh.Decode(reader) : decoder.DecodeInteger(contexts.Iadh);
				if (dh == Jbig2HuffmanTable.Oob)
					return;

				heightClassHeight += dh;
				if (heightClassHeight < 0 || heightClassHeight > int.MaxValue)
					return;

				var symbolWidth = 0L;
				var totalWidth = 0L;
				var firstSymbol = newSymbols.Count;
				widths?.Clear();

				while (true)
				{
					// 6.5.7: the delta width, or OOB at the end of the height class.
					var dw = huffman ? p.TableDw.Decode(reader) : decoder.DecodeInteger(contexts.Iadw);
					if (dw == Jbig2HuffmanTable.Oob)
						break;

					if (newSymbols.Count + (widths?.Count ?? 0) >= newCount)
						return;

					symbolWidth += dw;
					totalWidth += symbolWidth;
					if (symbolWidth < 0 || symbolWidth > int.MaxValue || totalWidth > int.MaxValue)
						return;

					if (widths != null)
					{
						// Huffman without refinement: the bitmaps follow in one collective bitmap.
						widths.Add((int)symbolWidth);
						continue;
					}

					Jbig2Bitmap symbol;
					if (!p.RefinementAggregate)
					{
						// 6.5.8.1: direct generic region coding.
						symbol = allocator.Create(symbolWidth, heightClassHeight);
						Jbig2GenericRegion.Decode(decoder, contexts.Generic, symbol, p.Template, false, p.At, null);
					}
					else
					{
						symbol = DecodeAggregateSymbol(p, available, (int)symbolWidth, (int)heightClassHeight, codeLength, decoder, contexts, reader, allocator);
						if (symbol == null)
							return;

						available.Add(symbol);
					}

					newSymbols.Add(symbol);
					if (decoder != null ? decoder.IsExhausted : reader.IsExhausted)
						return;
				}

				if (widths != null && widths.Count > 0)
				{
					if (!DecodeCollectiveBitmap(p, newSymbols, widths, (int)totalWidth, (int)heightClassHeight, reader, allocator))
						return;
				}
			}
		}

		// decodes a refinement/aggregate coded symbol (6.5.8.2).
		private static Jbig2Bitmap DecodeAggregateSymbol(
			Jbig2SymbolDictionaryParameters p,
			List<Jbig2Bitmap> available,
			int width,
			int height,
			int codeLength,
			Jbig2ArithmeticDecoder decoder,
			Jbig2ArithmeticContexts contexts,
			Jbig2BitReader reader,
			Jbig2Allocator allocator)
		{
			var huffman = p.Huffman;
			var instances = huffman ? p.TableAggregateInstances.Decode(reader) : decoder.DecodeInteger(contexts.Iaai);
			if (instances == Jbig2HuffmanTable.Oob || instances < 1)
				return null;

			if (instances > 1)
			{
				// 6.5.8.2.1: an aggregate of several symbols, decoded as a text region.
				var text = new Jbig2TextRegionParameters
				{
					Huffman = huffman,
					Refine = true,
					NumberOfInstances = instances,
					LogStrips = 0,
					Symbols = available,
					SymbolCodeLength = codeLength,
					SymbolIdTable = null,
					CombinationOperator = Jbig2Bitmap.OperatorOr,
					Transposed = false,
					ReferenceCorner = 1,
					DsOffset = 0,
					RefinementTemplate = p.RefinementTemplate,
					RefinementAt = p.RefinementAt,
				};

				if (huffman)
				{
					text.TableFs = Jbig2HuffmanTable.GetStandardTable(6);
					text.TableDs = Jbig2HuffmanTable.GetStandardTable(8);
					text.TableDt = Jbig2HuffmanTable.GetStandardTable(11);
					text.TableRdw = Jbig2HuffmanTable.GetStandardTable(15);
					text.TableRdh = Jbig2HuffmanTable.GetStandardTable(15);
					text.TableRdx = Jbig2HuffmanTable.GetStandardTable(15);
					text.TableRdy = Jbig2HuffmanTable.GetStandardTable(15);
					text.TableRsize = Jbig2HuffmanTable.GetStandardTable(1);
				}

				var bitmap = allocator.Create(width, height);
				Jbig2TextRegion.Decode(text, bitmap, decoder, contexts, reader, allocator);
				return bitmap;
			}

			// 6.5.8.2.2: a refinement of a single symbol.
			int id, rdx, rdy;
			var bitmapSize = 0;
			if (huffman)
			{
				id = (int)reader.ReadBits(codeLength);
				var table15 = Jbig2HuffmanTable.GetStandardTable(15);
				rdx = table15.Decode(reader);
				rdy = table15.Decode(reader);
				bitmapSize = Jbig2HuffmanTable.GetStandardTable(1).Decode(reader);
				if (bitmapSize == Jbig2HuffmanTable.Oob || bitmapSize < 0)
					return null;

				reader.AlignToByte();
			}
			else
			{
				id = decoder.DecodeId(contexts.GetIaid(codeLength), codeLength);
				rdx = decoder.DecodeInteger(contexts.Iardx);
				rdy = decoder.DecodeInteger(contexts.Iardy);
			}

			if (id < 0 || id >= available.Count || rdx == Jbig2HuffmanTable.Oob || rdy == Jbig2HuffmanTable.Oob)
				return null;

			var refined = allocator.Create(width, height);
			if (huffman)
			{
				// the refinement data is arithmetic coded in BMSIZE bytes (6.5.8.2.2 step 4).
				var start = reader.Position;
				var end = (int)Math.Min((long)start + bitmapSize, reader.End);
				var refinementDecoder = new Jbig2ArithmeticDecoder(reader.Data, start, end);
				Jbig2GenericRegion.DecodeRefinement(refinementDecoder, contexts.Refinement, refined, p.RefinementTemplate, available[id], rdx, rdy, false, p.RefinementAt);
				reader.Seek((int)Math.Min((long)start + bitmapSize, int.MaxValue));
			}
			else
			{
				Jbig2GenericRegion.DecodeRefinement(decoder, contexts.Refinement, refined, p.RefinementTemplate, available[id], rdx, rdy, false, p.RefinementAt);
			}

			return refined;
		}

		// decodes the collective bitmap of a Huffman height class and splits it into symbols (6.5.9).
		private static bool DecodeCollectiveBitmap(
			Jbig2SymbolDictionaryParameters p,
			List<Jbig2Bitmap> newSymbols,
			List<int> widths,
			int totalWidth,
			int height,
			Jbig2BitReader reader,
			Jbig2Allocator allocator)
		{
			var bitmapSize = p.TableBitmapSize.Decode(reader);
			if (bitmapSize == Jbig2HuffmanTable.Oob || bitmapSize < 0)
				return false;

			reader.AlignToByte();

			var collective = allocator.Create(totalWidth, height);
			if (bitmapSize == 0)
			{
				// uncompressed: rows of TOTWIDTH pixels, each padded to a byte boundary.
				var start = reader.Position;
				var length = (long)collective.Stride * height;
				var available = (int)Math.Max(0, Math.Min(length, reader.End - (long)start));
				Buffer.BlockCopy(reader.Data, start, collective.Data, 0, available);
				ClearPadding(collective);
				reader.Seek((int)Math.Min((long)start + length, int.MaxValue));
			}
			else
			{
				var start = reader.Position;
				var end = (int)Math.Min((long)start + bitmapSize, reader.End);
				var mmr = new Jbig2BitReader(reader.Data, start, end);
				Jbig2MmrDecoder.Decode(mmr, collective);
				reader.Seek((int)Math.Min((long)start + bitmapSize, int.MaxValue));
			}

			// split the collective bitmap into the symbols of the height class.
			var x = 0;
			for (var i = 0; i < widths.Count; i++)
			{
				var width = widths[i];
				newSymbols.Add(collective.Extract(x, 0, width, height));
				x += width;
			}

			return true;
		}

		// 6.5.10: decodes the export flags and collects the exported symbols.
		private static List<Jbig2Bitmap> Export(
			Jbig2SymbolDictionaryParameters p,
			List<Jbig2Bitmap> newSymbols,
			Jbig2ArithmeticDecoder decoder,
			Jbig2ArithmeticContexts contexts,
			Jbig2BitReader reader)
		{
			var inputSymbols = p.InputSymbols;
			var total = inputSymbols.Count + (long)newSymbols.Count;
			var exported = new List<Jbig2Bitmap>((int)Math.Min(Math.Max(0, p.NumberOfExportedSymbols), total));
			var table = p.Huffman ? Jbig2HuffmanTable.GetStandardTable(1) : null;

			var index = 0L;
			var export = false;
			for (var iteration = 0L; index < total && iteration <= 2 * total + 2; iteration++)
			{
				var run = p.Huffman ? table.Decode(reader) : decoder.DecodeInteger(contexts.Iaex);
				if (run == Jbig2HuffmanTable.Oob || run < 0 || (decoder != null ? decoder.IsExhausted : reader.IsExhausted))
					break;

				var stop = Math.Min(total, index + run);
				if (export)
				{
					for (var i = index; i < stop; i++)
						exported.Add(i < inputSymbols.Count ? inputSymbols[(int)i] : newSymbols[(int)(i - inputSymbols.Count)]);
				}

				index = stop;
				export = !export;
			}

			// when the export flags cannot be decoded (truncated data), export the new symbols, which
			// is what nearly every encoder does, so that the text regions still find their glyphs.
			if (index < total && exported.Count == 0)
				exported.AddRange(newSymbols);

			return exported;
		}

		// clears the bits after the last pixel of every row.
		private static void ClearPadding(Jbig2Bitmap bitmap)
		{
			var tailBits = bitmap.Width & 7;
			if (tailBits == 0 || bitmap.Stride == 0)
				return;

			var mask = (byte)((0xFF << (8 - tailBits)) & 0xFF);
			var data = bitmap.Data;
			for (var row = 0; row < bitmap.Height; row++)
				data[row * bitmap.Stride + bitmap.Stride - 1] &= mask;
		}

		// returns ceil(log2(value)), with 0 for values up to 1.
		private static int CeilLog2(long value)
		{
			var bits = 0;
			while ((1L << bits) < value)
				bits++;

			return bits;
		}

		#endregion
	}
}
