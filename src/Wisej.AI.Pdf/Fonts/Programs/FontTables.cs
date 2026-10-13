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

// Static tables for font programs: the CFF standard strings and predefined charsets and
// encodings (Adobe TN #5176, appendices A-C), and the Macintosh standard glyph order (TrueType post).
// The tables were generated from the pdf.js data files (Apache-2.0), which transcribe the specifications.

namespace Wisej.AI.Pdf.Fonts.Programs
{
	/// <summary>
	/// Provides the static name tables used by the font program parsers.
	/// </summary>
	internal static class FontTables
	{
		/// <summary>
		/// The number of CFF standard strings; string ids at or above this value index the String INDEX.
		/// </summary>
		public const int CffStandardStringCount = 391;

		/// <summary>
		/// The ISOAdobe charset covers the string ids 0 to this value, in order (TN #5176, appendix C).
		/// </summary>
		public const int CffIsoAdobeLastSid = 228;

		/// <summary>
		/// The CFF standard strings, indexed by string id (TN #5176, appendix A).
		/// </summary>
		public static readonly string[] CffStandardStrings =
		{
			".notdef", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quoteright",
			"parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero",
			"one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "colon", "semicolon",
			"less", "equal", "greater", "question", "at", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J",
			"K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "bracketleft",
			"backslash", "bracketright", "asciicircum", "underscore", "quoteleft", "a", "b", "c", "d", "e",
			"f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x",
			"y", "z", "braceleft", "bar", "braceright", "asciitilde", "exclamdown", "cent", "sterling", "fraction",
			"yen", "florin", "section", "currency", "quotesingle", "quotedblleft", "guillemotleft", "guilsinglleft",
			"guilsinglright", "fi", "fl", "endash", "dagger", "daggerdbl", "periodcentered", "paragraph",
			"bullet", "quotesinglbase", "quotedblbase", "quotedblright", "guillemotright", "ellipsis", "perthousand",
			"questiondown", "grave", "acute", "circumflex", "tilde", "macron", "breve", "dotaccent", "dieresis",
			"ring", "cedilla", "hungarumlaut", "ogonek", "caron", "emdash", "AE", "ordfeminine", "Lslash",
			"Oslash", "OE", "ordmasculine", "ae", "dotlessi", "lslash", "oslash", "oe", "germandbls", "onesuperior",
			"logicalnot", "mu", "trademark", "Eth", "onehalf", "plusminus", "Thorn", "onequarter", "divide",
			"brokenbar", "degree", "thorn", "threequarters", "twosuperior", "registered", "minus", "eth",
			"multiply", "threesuperior", "copyright", "Aacute", "Acircumflex", "Adieresis", "Agrave", "Aring",
			"Atilde", "Ccedilla", "Eacute", "Ecircumflex", "Edieresis", "Egrave", "Iacute", "Icircumflex",
			"Idieresis", "Igrave", "Ntilde", "Oacute", "Ocircumflex", "Odieresis", "Ograve", "Otilde", "Scaron",
			"Uacute", "Ucircumflex", "Udieresis", "Ugrave", "Yacute", "Ydieresis", "Zcaron", "aacute", "acircumflex",
			"adieresis", "agrave", "aring", "atilde", "ccedilla", "eacute", "ecircumflex", "edieresis", "egrave",
			"iacute", "icircumflex", "idieresis", "igrave", "ntilde", "oacute", "ocircumflex", "odieresis",
			"ograve", "otilde", "scaron", "uacute", "ucircumflex", "udieresis", "ugrave", "yacute", "ydieresis",
			"zcaron", "exclamsmall", "Hungarumlautsmall", "dollaroldstyle", "dollarsuperior", "ampersandsmall",
			"Acutesmall", "parenleftsuperior", "parenrightsuperior", "twodotenleader", "onedotenleader",
			"zerooldstyle", "oneoldstyle", "twooldstyle", "threeoldstyle", "fouroldstyle", "fiveoldstyle",
			"sixoldstyle", "sevenoldstyle", "eightoldstyle", "nineoldstyle", "commasuperior", "threequartersemdash",
			"periodsuperior", "questionsmall", "asuperior", "bsuperior", "centsuperior", "dsuperior", "esuperior",
			"isuperior", "lsuperior", "msuperior", "nsuperior", "osuperior", "rsuperior", "ssuperior", "tsuperior",
			"ff", "ffi", "ffl", "parenleftinferior", "parenrightinferior", "Circumflexsmall", "hyphensuperior",
			"Gravesmall", "Asmall", "Bsmall", "Csmall", "Dsmall", "Esmall", "Fsmall", "Gsmall", "Hsmall",
			"Ismall", "Jsmall", "Ksmall", "Lsmall", "Msmall", "Nsmall", "Osmall", "Psmall", "Qsmall", "Rsmall",
			"Ssmall", "Tsmall", "Usmall", "Vsmall", "Wsmall", "Xsmall", "Ysmall", "Zsmall", "colonmonetary",
			"onefitted", "rupiah", "Tildesmall", "exclamdownsmall", "centoldstyle", "Lslashsmall", "Scaronsmall",
			"Zcaronsmall", "Dieresissmall", "Brevesmall", "Caronsmall", "Dotaccentsmall", "Macronsmall",
			"figuredash", "hypheninferior", "Ogoneksmall", "Ringsmall", "Cedillasmall", "questiondownsmall",
			"oneeighth", "threeeighths", "fiveeighths", "seveneighths", "onethird", "twothirds", "zerosuperior",
			"foursuperior", "fivesuperior", "sixsuperior", "sevensuperior", "eightsuperior", "ninesuperior",
			"zeroinferior", "oneinferior", "twoinferior", "threeinferior", "fourinferior", "fiveinferior",
			"sixinferior", "seveninferior", "eightinferior", "nineinferior", "centinferior", "dollarinferior",
			"periodinferior", "commainferior", "Agravesmall", "Aacutesmall", "Acircumflexsmall", "Atildesmall",
			"Adieresissmall", "Aringsmall", "AEsmall", "Ccedillasmall", "Egravesmall", "Eacutesmall", "Ecircumflexsmall",
			"Edieresissmall", "Igravesmall", "Iacutesmall", "Icircumflexsmall", "Idieresissmall", "Ethsmall",
			"Ntildesmall", "Ogravesmall", "Oacutesmall", "Ocircumflexsmall", "Otildesmall", "Odieresissmall",
			"OEsmall", "Oslashsmall", "Ugravesmall", "Uacutesmall", "Ucircumflexsmall", "Udieresissmall",
			"Yacutesmall", "Thornsmall", "Ydieresissmall", "001.000", "001.001", "001.002", "001.003", "Black",
			"Bold", "Book", "Light", "Medium", "Regular", "Roman", "Semibold",
		};

		/// <summary>
		/// The string ids of the predefined Expert charset, indexed by glyph id (TN #5176, appendix C).
		/// </summary>
		public static readonly short[] CffExpertCharset =
		{
			0, 1, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 13, 14, 15, 99,
			239, 240, 241, 242, 243, 244, 245, 246, 247, 248, 27, 28, 249, 250, 251, 252,
			253, 254, 255, 256, 257, 258, 259, 260, 261, 262, 263, 264, 265, 266, 109, 110,
			267, 268, 269, 270, 271, 272, 273, 274, 275, 276, 277, 278, 279, 280, 281, 282,
			283, 284, 285, 286, 287, 288, 289, 290, 291, 292, 293, 294, 295, 296, 297, 298,
			299, 300, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 311, 312, 313, 314,
			315, 316, 317, 318, 158, 155, 163, 319, 320, 321, 322, 323, 324, 325, 326, 150,
			164, 169, 327, 328, 329, 330, 331, 332, 333, 334, 335, 336, 337, 338, 339, 340,
			341, 342, 343, 344, 345, 346, 347, 348, 349, 350, 351, 352, 353, 354, 355, 356,
			357, 358, 359, 360, 361, 362, 363, 364, 365, 366, 367, 368, 369, 370, 371, 372,
			373, 374, 375, 376, 377, 378,
		};

		/// <summary>
		/// The string ids of the predefined ExpertSubset charset, indexed by glyph id (TN #5176, appendix C).
		/// </summary>
		public static readonly short[] CffExpertSubsetCharset =
		{
			0, 1, 231, 232, 235, 236, 237, 238, 13, 14, 15, 99, 239, 240, 241, 242,
			243, 244, 245, 246, 247, 248, 27, 28, 249, 250, 251, 253, 254, 255, 256, 257,
			258, 259, 260, 261, 262, 263, 264, 265, 266, 109, 110, 267, 268, 269, 270, 272,
			300, 301, 302, 305, 314, 315, 158, 155, 163, 320, 321, 322, 323, 324, 325, 326,
			150, 164, 169, 327, 328, 329, 330, 331, 332, 333, 334, 335, 336, 337, 338, 339,
			340, 341, 342, 343, 344, 345, 346,
		};

		/// <summary>
		/// The glyph names of the predefined CFF Expert encoding, indexed by character code (TN #5176, appendix B).
		/// </summary>
		public static readonly string[] CffExpertEncoding =
		{
			null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
			null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
			"space", "exclamsmall", "Hungarumlautsmall", null, "dollaroldstyle", "dollarsuperior", "ampersandsmall",
			"Acutesmall", "parenleftsuperior", "parenrightsuperior", "twodotenleader", "onedotenleader",
			"comma", "hyphen", "period", "fraction", "zerooldstyle", "oneoldstyle", "twooldstyle", "threeoldstyle",
			"fouroldstyle", "fiveoldstyle", "sixoldstyle", "sevenoldstyle", "eightoldstyle", "nineoldstyle",
			"colon", "semicolon", "commasuperior", "threequartersemdash", "periodsuperior", "questionsmall",
			null, "asuperior", "bsuperior", "centsuperior", "dsuperior", "esuperior", null, null, null, "isuperior",
			null, null, "lsuperior", "msuperior", "nsuperior", "osuperior", null, null, "rsuperior", "ssuperior",
			"tsuperior", null, "ff", "fi", "fl", "ffi", "ffl", "parenleftinferior", null, "parenrightinferior",
			"Circumflexsmall", "hyphensuperior", "Gravesmall", "Asmall", "Bsmall", "Csmall", "Dsmall", "Esmall",
			"Fsmall", "Gsmall", "Hsmall", "Ismall", "Jsmall", "Ksmall", "Lsmall", "Msmall", "Nsmall", "Osmall",
			"Psmall", "Qsmall", "Rsmall", "Ssmall", "Tsmall", "Usmall", "Vsmall", "Wsmall", "Xsmall", "Ysmall",
			"Zsmall", "colonmonetary", "onefitted", "rupiah", "Tildesmall", null, null, null, null, null,
			null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
			null, null, null, null, null, null, null, null, null, null, null, null, null, "exclamdownsmall",
			"centoldstyle", "Lslashsmall", null, null, "Scaronsmall", "Zcaronsmall", "Dieresissmall", "Brevesmall",
			"Caronsmall", null, "Dotaccentsmall", null, null, "Macronsmall", null, null, "figuredash", "hypheninferior",
			null, null, "Ogoneksmall", "Ringsmall", "Cedillasmall", null, null, null, "onequarter", "onehalf",
			"threequarters", "questiondownsmall", "oneeighth", "threeeighths", "fiveeighths", "seveneighths",
			"onethird", "twothirds", null, null, "zerosuperior", "onesuperior", "twosuperior", "threesuperior",
			"foursuperior", "fivesuperior", "sixsuperior", "sevensuperior", "eightsuperior", "ninesuperior",
			"zeroinferior", "oneinferior", "twoinferior", "threeinferior", "fourinferior", "fiveinferior",
			"sixinferior", "seveninferior", "eightinferior", "nineinferior", "centinferior", "dollarinferior",
			"periodinferior", "commainferior", "Agravesmall", "Aacutesmall", "Acircumflexsmall", "Atildesmall",
			"Adieresissmall", "Aringsmall", "AEsmall", "Ccedillasmall", "Egravesmall", "Eacutesmall", "Ecircumflexsmall",
			"Edieresissmall", "Igravesmall", "Iacutesmall", "Icircumflexsmall", "Idieresissmall", "Ethsmall",
			"Ntildesmall", "Ogravesmall", "Oacutesmall", "Ocircumflexsmall", "Otildesmall", "Odieresissmall",
			"OEsmall", "Oslashsmall", "Ugravesmall", "Uacutesmall", "Ucircumflexsmall", "Udieresissmall",
			"Yacutesmall", "Thornsmall", "Ydieresissmall",
		};

		/// <summary>
		/// The 258 standard Macintosh glyph names used by the TrueType <c>post</c> table formats 1, 2 and 2.5.
		/// </summary>
		public static readonly string[] MacStandardGlyphNames =
		{
			".notdef", ".null", "nonmarkingreturn", "space", "exclam", "quotedbl", "numbersign", "dollar",
			"percent", "ampersand", "quotesingle", "parenleft", "parenright", "asterisk", "plus", "comma",
			"hyphen", "period", "slash", "zero", "one", "two", "three", "four", "five", "six", "seven", "eight",
			"nine", "colon", "semicolon", "less", "equal", "greater", "question", "at", "A", "B", "C", "D",
			"E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W",
			"X", "Y", "Z", "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "grave",
			"a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s",
			"t", "u", "v", "w", "x", "y", "z", "braceleft", "bar", "braceright", "asciitilde", "Adieresis",
			"Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute", "agrave", "acircumflex",
			"adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave", "ecircumflex", "edieresis", "iacute",
			"igrave", "icircumflex", "idieresis", "ntilde", "oacute", "ograve", "ocircumflex", "odieresis",
			"otilde", "uacute", "ugrave", "ucircumflex", "udieresis", "dagger", "degree", "cent", "sterling",
			"section", "bullet", "paragraph", "germandbls", "registered", "copyright", "trademark", "acute",
			"dieresis", "notequal", "AE", "Oslash", "infinity", "plusminus", "lessequal", "greaterequal",
			"yen", "mu", "partialdiff", "summation", "product", "pi", "integral", "ordfeminine", "ordmasculine",
			"Omega", "ae", "oslash", "questiondown", "exclamdown", "logicalnot", "radical", "florin", "approxequal",
			"Delta", "guillemotleft", "guillemotright", "ellipsis", "nonbreakingspace", "Agrave", "Atilde",
			"Otilde", "OE", "oe", "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright",
			"divide", "lozenge", "ydieresis", "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright",
			"fi", "fl", "daggerdbl", "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex",
			"Ecircumflex", "Aacute", "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave",
			"Oacute", "Ocircumflex", "apple", "Ograve", "Uacute", "Ucircumflex", "Ugrave", "dotlessi", "circumflex",
			"tilde", "macron", "breve", "dotaccent", "ring", "cedilla", "hungarumlaut", "ogonek", "caron",
			"Lslash", "lslash", "Scaron", "scaron", "Zcaron", "zcaron", "brokenbar", "Eth", "eth", "Yacute",
			"yacute", "Thorn", "thorn", "minus", "multiply", "onesuperior", "twosuperior", "threesuperior",
			"onehalf", "onequarter", "threequarters", "franc", "Gbreve", "gbreve", "Idotaccent", "Scedilla",
			"scedilla", "Cacute", "cacute", "Ccaron", "ccaron", "dcroat",
		};
	}
}
