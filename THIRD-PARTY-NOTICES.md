# Third-party notices

PixCodec is written from format specifications. The items below are data or format definitions that only exist
in another project's sources.

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
