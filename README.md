# PS2DL
A modern PlayStation 2 DVD image builder reverse-engineered from Sony's "DVD-ROM GENERATOR" mastering conventions. <br>
## Features built for high-end modding <br>
- **ISO Extraction with automatic .iml generation and file preview**<br>
- **Build Single-layer DVD5 and Dual-layer DVD9 ISO images**<br>
- **Build spanning Dual-layer ISO images: Games with large archive files can be spanned across layers, <br>
unlocking 8.5GB of disc space for modified games.**

## The IML config

`disc.iml` is a small XML document capturing everything needed to rebuild an image:
- Volume identity (ISO + UDF names, the per-master unique prefix, dates),
- The directory tree with original-case names and per-file timestamps,
- The physical data layout order (with optional LBA pins)

Edit it to reorder files, change timestamps, swap payloads, or retarget media, then `build`.

## Example: Making a dual-layer Gran Turismo 3
GT3's data lives in one big archive at the end of the file order, `GT3.VOL`.<br>
To exceed a single layer you first repack a modified and > 4.7GB `GT3.VOL`, then build with Spanning mode on.

### How the spanning trick works (and the caveat)
GT4 shipped a dual-layer disc using **two separate volumes** (2nd layer holds `GT4L1.VOL`), which
requires the game executable to know about the second volume, so the only route to a dual-layer GT3 is a **single volume that spans both layers**,
with `GT3.VOL` growing past one layer and crossing the break.

The disc's logical sector space is continuous across an OTP dual-layer break, so the filesystem is just one large volume.<br>
This tool is able to pack files like `GT3.VOL` as one contiguous run that crosses the break.<br>

Spanning discs also get 10,240 runout sectors appended after the data.<br>
The game's streaming engine reads ahead in fixed chunks; when it streams the last file
in `GT3.VOL` at the disc edge, that read-ahead would otherwise run off the end of the disc and freeze
the game. The runout gives it valid zero-filled sectors to land on.<br>
(This makes the image a little larger and shifts the layer break, which only matters when burning)

## Project layout

```
src/PS2Iso.Core/   format model, readers, writers, layout engine, builder
src/PS2Iso.Cli/    command-line interface
src/PS2Iso.Gui/    Windows Forms GUI
```

## Compiling
Requires the .NET 10 SDK.
