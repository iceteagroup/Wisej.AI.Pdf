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

// Text region decoding procedure (ITU T.88, 6.4).

using System;
using System.Collections.Generic;

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// The parameters of the text region decoding procedure (Table 9).
	/// </summary>
	internal sealed class Jbig2TextRegionParameters
	{
		#region Fields

		/// <summary>SBHUFF: true when the region is Huffman coded.</summary>
		public bool Huffman;

		/// <summary>SBREFINE: true when symbol instances may be refined.</summary>
		public bool Refine;

		/// <summary>SBNUMINSTANCES: the number of symbol instances.</summary>
		public long NumberOfInstances;

		/// <summary>LOG2SBSTRIPS: the base-2 logarithm of the strip size.</summary>
		public int LogStrips;

		/// <summary>SBSYMS: the symbols that the instances refer to.</summary>
		public IList<Jbig2Bitmap> Symbols;

		/// <summary>SBSYMCODELEN: the length of the arithmetic or fixed-length symbol IDs.</summary>
		public int SymbolCodeLength;

		/// <summary>SBSYMCODES: the Huffman symbol ID table, or null for fixed-length IDs.</summary>
		public Jbig2HuffmanTable SymbolIdTable;

		/// <summary>SBCOMBOP: the operator used to draw the symbols.</summary>
		public int CombinationOperator;

		/// <summary>TRANSPOSED: true when the S axis is vertical.</summary>
		public bool Transposed;

		/// <summary>REFCORNER: 0 bottom-left, 1 top-left, 2 bottom-right, 3 top-right.</summary>
		public int ReferenceCorner;

		/// <summary>SBDSOFFSET: the offset added to every S delta.</summary>
		public int DsOffset;

		/// <summary>SBHUFFFS: the first S table.</summary>
		public Jbig2HuffmanTable TableFs;

		/// <summary>SBHUFFDS: the delta S table.</summary>
		public Jbig2HuffmanTable TableDs;

		/// <summary>SBHUFFDT: the delta T table.</summary>
		public Jbig2HuffmanTable TableDt;

		/// <summary>SBHUFFRDW: the refinement delta width table.</summary>
		public Jbig2HuffmanTable TableRdw;

		/// <summary>SBHUFFRDH: the refinement delta height table.</summary>
		public Jbig2HuffmanTable TableRdh;

		/// <summary>SBHUFFRDX: the refinement X offset table.</summary>
		public Jbig2HuffmanTable TableRdx;

		/// <summary>SBHUFFRDY: the refinement Y offset table.</summary>
		public Jbig2HuffmanTable TableRdy;

		/// <summary>SBHUFFRSIZE: the refinement bitmap size table.</summary>
		public Jbig2HuffmanTable TableRsize;

		/// <summary>SBRTEMPLATE: the refinement template.</summary>
		public int RefinementTemplate;

		/// <summary>SBRAT: the refinement AT pixels.</summary>
		public int[] RefinementAt;

		#endregion
	}

	/// <summary>
	/// Implements the text region decoding procedure (6.4.5).
	/// </summary>
	internal static class Jbig2TextRegion
	{
		#region Methods

		/// <summary>
		/// Decodes the symbol instances of a text region into <paramref name="region"/>.
		/// </summary>
		/// <param name="parameters">The decoding parameters.</param>
		/// <param name="region">The region bitmap, already filled with SBDEFPIXEL.</param>
		/// <param name="decoder">The arithmetic decoder; null when the region is Huffman coded.</param>
		/// <param name="contexts">The arithmetic statistics; also used for refinements of Huffman regions.</param>
		/// <param name="reader">The bit reader of a Huffman coded region, or null.</param>
		/// <param name="allocator">The allocator for refined symbol bitmaps.</param>
		/// <remarks>Decoding stops quietly at the first inconsistency, leaving the instances decoded so far.</remarks>
		public static void Decode(
			Jbig2TextRegionParameters parameters,
			Jbig2Bitmap region,
			Jbig2ArithmeticDecoder decoder,
			Jbig2ArithmeticContexts contexts,
			Jbig2BitReader reader,
			Jbig2Allocator allocator)
		{
			var p = parameters;
			var huffman = p.Huffman;
			var strips = 1 << p.LogStrips;
			var symbols = p.Symbols;
			var iaid = huffman ? null : contexts.GetIaid(p.SymbolCodeLength);
			var transposed = p.Transposed;
			var right = (p.ReferenceCorner & 2) != 0;
			var top = (p.ReferenceCorner & 1) != 0;

			// 6.4.5 step 2: the initial STRIPT value.
			var dt = huffman ? p.TableDt.Decode(reader) : decoder.DecodeInteger(contexts.Iadt);
			if (dt == Jbig2HuffmanTable.Oob)
				return;

			var stripT = -(long)dt * strips;
			var firstS = 0L;
			var instances = 0L;

			while (instances < p.NumberOfInstances)
			{
				if (IsExhausted(decoder, reader))
					return;

				// 6.4.5 step 3 b: the strip delta T.
				dt = huffman ? p.TableDt.Decode(reader) : decoder.DecodeInteger(contexts.Iadt);
				if (dt == Jbig2HuffmanTable.Oob)
					return;

				stripT += (long)dt * strips;

				// the first symbol instance of the strip.
				var dfs = huffman ? p.TableFs.Decode(reader) : decoder.DecodeInteger(contexts.Iafs);
				if (dfs == Jbig2HuffmanTable.Oob)
					return;

				firstS += dfs;
				var curS = firstS;

				while (true)
				{
					// 6.4.9: the T coordinate within the strip.
					var curT = 0L;
					if (strips > 1)
						curT = huffman ? reader.ReadBits(p.LogStrips) : decoder.DecodeInteger(contexts.Iait);

					var t = stripT + curT;

					// 6.4.10: the symbol ID.
					int id;
					if (!huffman)
						id = decoder.DecodeId(iaid, p.SymbolCodeLength);
					else if (p.SymbolIdTable != null)
						id = p.SymbolIdTable.Decode(reader);
					else
						id = (int)reader.ReadBits(p.SymbolCodeLength);

					if (id < 0 || id >= symbols.Count)
						return;

					var symbol = symbols[id];

					// 6.4.11: the optional refinement of the symbol bitmap.
					if (p.Refine)
					{
						var refine = huffman ? reader.ReadBit() : decoder.DecodeInteger(contexts.Iari);
						if (refine != 0)
						{
							symbol = DecodeRefinedSymbol(p, symbol, decoder, contexts, reader, allocator);
							if (symbol == null)
								return;
						}
					}

					var width = symbol.Width;
					var height = symbol.Height;

					// 6.4.5 step 3 c x: move CURS to the reference corner and place the symbol.
					if (!transposed && right)
						curS += width - 1;
					else if (transposed && !top)
						curS += height - 1;

					long x, y;
					if (!transposed)
					{
						x = right ? curS - width + 1 : curS;
						y = top ? t : t - height + 1;
					}
					else
					{
						x = right ? t - width + 1 : t;
						y = top ? curS : curS - height + 1;
					}

					// every instance is charged, so that corrupt instance counts cannot loop for ever.
					allocator.Spend(16 + (long)width * height / 8);
					region.Compose(symbol, x, y, p.CombinationOperator);

					// 6.4.5 step 3 c xi: move CURS past the symbol.
					if (!transposed && !right)
						curS += width - 1;
					else if (transposed && top)
						curS += height - 1;

					instances++;
					if (IsExhausted(decoder, reader))
						return;

					// 6.4.8: the S delta to the next instance, or OOB at the end of the strip. The OOB
					// after the last instance must be consumed too, because the arithmetic decoder of
					// an aggregate symbol continues with the rest of the symbol dictionary.
					var ds = huffman ? p.TableDs.Decode(reader) : decoder.DecodeInteger(contexts.Iads);
					if (ds == Jbig2HuffmanTable.Oob)
						break;

					// a strip that goes on after the last instance only occurs in corrupt data.
					if (instances >= p.NumberOfInstances)
						return;

					curS += ds + p.DsOffset;
				}
			}
		}

		#endregion

		#region Implementation

		// decodes the refinement of a symbol instance (6.4.11).
		private static Jbig2Bitmap DecodeRefinedSymbol(
			Jbig2TextRegionParameters p,
			Jbig2Bitmap symbol,
			Jbig2ArithmeticDecoder decoder,
			Jbig2ArithmeticContexts contexts,
			Jbig2BitReader reader,
			Jbig2Allocator allocator)
		{
			int rdw, rdh, rdx, rdy;
			var bitmapSize = 0;
			if (p.Huffman)
			{
				rdw = p.TableRdw.Decode(reader);
				rdh = p.TableRdh.Decode(reader);
				rdx = p.TableRdx.Decode(reader);
				rdy = p.TableRdy.Decode(reader);
				bitmapSize = p.TableRsize.Decode(reader);
				if (bitmapSize == Jbig2HuffmanTable.Oob || bitmapSize < 0)
					return null;

				reader.AlignToByte();
			}
			else
			{
				rdw = decoder.DecodeInteger(contexts.Iardw);
				rdh = decoder.DecodeInteger(contexts.Iardh);
				rdx = decoder.DecodeInteger(contexts.Iardx);
				rdy = decoder.DecodeInteger(contexts.Iardy);
			}

			if (rdw == Jbig2HuffmanTable.Oob || rdh == Jbig2HuffmanTable.Oob || rdx == Jbig2HuffmanTable.Oob || rdy == Jbig2HuffmanTable.Oob)
				return null;

			var width = (long)symbol.Width + rdw;
			var height = (long)symbol.Height + rdh;
			var refined = allocator.Create(width, height);

			// the reference offsets use floor division of the size deltas.
			var dx = (rdw >> 1) + rdx;
			var dy = (rdh >> 1) + rdy;

			if (p.Huffman)
			{
				// in Huffman regions the refinement data is arithmetic coded in BMSIZE bytes (6.4.11.1).
				var start = reader.Position;
				var end = (int)Math.Min((long)start + bitmapSize, reader.End);
				var refinementDecoder = new Jbig2ArithmeticDecoder(reader.Data, start, end);
				Jbig2GenericRegion.DecodeRefinement(refinementDecoder, contexts.Refinement, refined, p.RefinementTemplate, symbol, dx, dy, false, p.RefinementAt);
				reader.Seek((int)Math.Min((long)start + bitmapSize, int.MaxValue));
			}
			else
			{
				Jbig2GenericRegion.DecodeRefinement(decoder, contexts.Refinement, refined, p.RefinementTemplate, symbol, dx, dy, false, p.RefinementAt);
			}

			return refined;
		}

		// returns true when the data source is consumed well past its end.
		private static bool IsExhausted(Jbig2ArithmeticDecoder decoder, Jbig2BitReader reader)
		{
			return decoder != null ? decoder.IsExhausted : reader.IsExhausted;
		}

		#endregion
	}
}
