# Third-party notices

Wisej.AI.Pdf has no runtime dependencies: all the code is original and written for this library. Some data tables, and a few algorithmic details, come from the open-source projects listed below. Their notices are reproduced here as their licenses require.

## Adobe Glyph List and ZapfDingbats Glyph List

`Resources/glyphlist.txt` and `Resources/zapfdingbats.txt` are distributed unmodified.

> Copyright 2002-2019 Adobe (http://www.adobe.com/).
>
> Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
> Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
> Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
> Neither the name of Adobe nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Adobe CMap resources and PDF mapping resources

`Resources/CMaps/cmaps.bin` and `Resources/CMaps/cid2unicode.bin` were generated from the predefined CMaps of [adobe-type-tools/cmap-resources](https://github.com/adobe-type-tools/cmap-resources) and the CID-to-Unicode tables of [adobe-type-tools/mapping-resources-pdf](https://github.com/adobe-type-tools/mapping-resources-pdf). The data was converted to a compact binary format; the mappings themselves are unchanged. Both projects are released under the BSD 3-Clause license; its text is in `src/Wisej.AI.Pdf/Resources/CMaps/LICENSE.txt`.

## Adobe Core 14 font metrics

`Resources/Standard14/core14.bin` holds the glyph widths, bounding boxes and encodings of the Adobe Core 14 AFM files, converted to a compact binary format. These files may be used, copied and distributed for any purpose, provided the copyright notice is preserved. The notice and Adobe's terms are in `src/Wisej.AI.Pdf/Resources/Standard14/LICENSE.txt` and `MustRead.html`.

> Copyright (c) 1985, 1987, 1989, 1990, 1991, 1992, 1993, 1997 Adobe Systems Incorporated. All Rights Reserved.

## Mozilla pdf.js

The following data was transcribed from [pdf.js](https://github.com/mozilla/pdf.js), licensed under the Apache License 2.0:

- the glyph-name tables of the simple font encodings (`Fonts/Encodings.Data.cs`);
- the CFF standard strings, charsets and encodings, and the Macintosh glyph names (`Fonts/Programs/FontTables.cs`);
- the coefficients of the polynomial approximation of the US Web Coated (SWOP) CMYK profile (`Graphics/ColorSpace.cs`).

> Copyright 2012 Mozilla Foundation
>
> Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
>
> Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.

## Independent JPEG Group

The arithmetic-coding decoder of the JPEG codec (`Imaging/Codecs/JpegArithmeticDecoder.cs`) follows the structure of libjpeg's `jdarith.c`, and its probability estimation table is the one from ITU-T T.81, Annex D.

> This software is based in part on the work of the Independent JPEG Group.
