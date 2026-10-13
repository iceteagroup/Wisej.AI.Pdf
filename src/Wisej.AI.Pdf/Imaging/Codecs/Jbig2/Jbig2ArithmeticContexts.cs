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

// Arithmetic coding statistics shared by the procedures of a JBIG2 segment (ITU T.88, 6.4.6 to 6.5.10 and Annex A).

namespace Wisej.AI.Pdf.Imaging.Codecs
{
	/// <summary>
	/// Holds the context statistics of the integer decoders, the symbol ID decoder and the generic
	/// and refinement region decoders used while decoding one segment.
	/// </summary>
	/// <remarks>
	/// A symbol dictionary shares one instance with the text regions it decodes for aggregate symbols,
	/// as required by 6.5.8.2. The generic and refinement statistics can be carried over to a later
	/// symbol dictionary when it sets "bitmap coding context used" (7.4.2.2).
	/// </remarks>
	internal sealed class Jbig2ArithmeticContexts
	{
		#region Fields

		// every integer decoder uses 512 contexts (A.2).
		private const int IntegerContextSize = 512;

		/// <summary>IADH: height class delta height.</summary>
		public readonly byte[] Iadh = new byte[IntegerContextSize];

		/// <summary>IADW: height class delta width.</summary>
		public readonly byte[] Iadw = new byte[IntegerContextSize];

		/// <summary>IAEX: export flags.</summary>
		public readonly byte[] Iaex = new byte[IntegerContextSize];

		/// <summary>IAAI: number of symbol instances in an aggregate symbol.</summary>
		public readonly byte[] Iaai = new byte[IntegerContextSize];

		/// <summary>IADT: strip delta T.</summary>
		public readonly byte[] Iadt = new byte[IntegerContextSize];

		/// <summary>IAFS: first symbol S coordinate.</summary>
		public readonly byte[] Iafs = new byte[IntegerContextSize];

		/// <summary>IADS: symbol S coordinate delta.</summary>
		public readonly byte[] Iads = new byte[IntegerContextSize];

		/// <summary>IAIT: symbol T coordinate within the strip.</summary>
		public readonly byte[] Iait = new byte[IntegerContextSize];

		/// <summary>IARI: refinement flag.</summary>
		public readonly byte[] Iari = new byte[IntegerContextSize];

		/// <summary>IARDW: refinement delta width.</summary>
		public readonly byte[] Iardw = new byte[IntegerContextSize];

		/// <summary>IARDH: refinement delta height.</summary>
		public readonly byte[] Iardh = new byte[IntegerContextSize];

		/// <summary>IARDX: refinement X offset.</summary>
		public readonly byte[] Iardx = new byte[IntegerContextSize];

		/// <summary>IARDY: refinement Y offset.</summary>
		public readonly byte[] Iardy = new byte[IntegerContextSize];

		/// <summary>The symbol ID contexts, allocated by <see cref="GetIaid"/>.</summary>
		private byte[] _iaid;

		#endregion

		#region Constructors

		/// <summary>
		/// Creates a set of fresh statistics.
		/// </summary>
		/// <param name="generic">The generic region statistics to use, or null to create new ones.</param>
		/// <param name="refinement">The refinement region statistics to use, or null to create new ones.</param>
		public Jbig2ArithmeticContexts(byte[] generic = null, byte[] refinement = null)
		{
			this.Generic = generic ?? new byte[Jbig2GenericRegion.GenericContextSize];
			this.Refinement = refinement ?? new byte[Jbig2GenericRegion.RefinementContextSize];
		}

		#endregion

		#region Properties

		/// <summary>
		/// Returns the GB statistics of the generic region decoding procedure.
		/// </summary>
		public byte[] Generic { get; }

		/// <summary>
		/// Returns the GR statistics of the generic refinement region decoding procedure.
		/// </summary>
		public byte[] Refinement { get; }

		#endregion

		#region Methods

		/// <summary>
		/// Returns the IAID statistics for the symbol code length, allocating them on first use.
		/// </summary>
		/// <param name="codeLength">SBSYMCODELEN; at most 30.</param>
		/// <returns>The 2^<paramref name="codeLength"/> contexts.</returns>
		public byte[] GetIaid(int codeLength)
		{
			var size = 1 << codeLength;
			if (_iaid == null || _iaid.Length != size)
				_iaid = new byte[size];

			return _iaid;
		}

		#endregion
	}
}
