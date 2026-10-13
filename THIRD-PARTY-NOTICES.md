# Third-party notices

PixCodec is original code. The following DATA was taken from third-party sources.

## libtiff - 24-bit LogLuv chromaticity table

`src/PixCodec/Tiff/TiffLogLuvTable.cs` contains the `uv_row` table (163 rows describing the visible gamut in the
CIE (u', v') plane, used by the 24-bit LogLuv TIFF encoding) from `libtiff/uvcode.h` of libtiff
(https://gitlab.com/libtiff/libtiff), "Version 1.0 generated April 7, 1997 by Greg Ward Larson, SGI".
Only the table values were taken; no code.

Copyright © 1988-1997 Sam Leffler
Copyright © 1991-1997 Silicon Graphics, Inc.

Permission to use, copy, modify, distribute, and sell this software and 
its documentation for any purpose is hereby granted without fee, provided
that (i) the above copyright notices and this permission notice appear in
all copies of the software and related documentation, and (ii) the names of
Sam Leffler and Silicon Graphics may not be used in any advertising or
publicity relating to the software without the specific, prior written
permission of Sam Leffler and Silicon Graphics.

THE SOFTWARE IS PROVIDED "AS-IS" AND WITHOUT WARRANTY OF ANY KIND, 
EXPRESS, IMPLIED OR OTHERWISE, INCLUDING WITHOUT LIMITATION, ANY 
WARRANTY OF MERCHANTABILITY OR FITNESS FOR A PARTICULAR PURPOSE.  

IN NO EVENT SHALL SAM LEFFLER OR SILICON GRAPHICS BE LIABLE FOR
ANY SPECIAL, INCIDENTAL, INDIRECT OR CONSEQUENTIAL DAMAGES OF ANY KIND,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER OR NOT ADVISED OF THE POSSIBILITY OF DAMAGE, AND ON ANY THEORY OF 
LIABILITY, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE 
OF THIS SOFTWARE.

## XZ Utils / LZMA SDK (BCJ filter definitions)

`src/PixCodec/Core/Compression/BcjFilters.cs`

- The IA-64 table that tells which slots of a bundle template can hold a branch (`Ia64BranchSlots`) has the same
  content as the table in XZ Utils (`src/liblzma/simple/ia64.c`) and the LZMA SDK (`Bra.c`/`BraIA64.c`).
- The .xz format documents the BCJ filters only by reference to their implementation, so the exact conversions
  (which instructions, which bits, the x86 look-back rule, the ARM64 ADRP range rule and the layout of the RISC-V
  filter) follow the behaviour of XZ Utils. The RISC-V layout was taken from the description in
  `src/liblzma/simple/riscv.c`. The code here is written independently and verified against files made by `xz`.

XZ Utils: Copyright The XZ Utils authors and contributors (Lasse Collin, Jia Tan and others); the liblzma parts
referred to are in the public domain (releases before 5.6) or under the BSD Zero Clause License (0BSD).
LZMA SDK: by Igor Pavlov, public domain.
