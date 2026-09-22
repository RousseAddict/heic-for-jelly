# J0 — the ffmpeg spike

**Status: J0 PASSED. 575 genuine HEICs, 2022-2025. jellyfin-ffmpeg cannot decode them
with any plain invocation — they are HEVC tile grids and the CLI returns a single
512x512 tile with exit code 0 — but a filtergraph generated from
`ffprobe -show_stream_groups`, plus the `irot` rotation parsed from the container,
yields a correct full-resolution image in ~0.6 s. Verified end to end inside the
Jellyfin container. J1 may start; read "Consequence" first, the encoder is not a thin
ffmpeg wrapper.**

## Run 1, 2026-09-20 — a false positive

Server ffmpeg: **jellyfin-ffmpeg 7.1.3**, Debian, amd64, gcc 14. Version is above the
7.0 floor that FFmpeg's Changelog gives for HEIF/AVIF still images, so the version
gate passes.

```
/usr/lib/jellyfin-ffmpeg/ffmpeg -i ./IMG_2720.HEIC /tmp/spike.png
```

Exit 0, a PNG written, `real 0m0.121s`. And yet the run proves nothing, because of
two lines in the output:

```
Input #0, jpeg_pipe, from './IMG_2720.HEIC':
  Stream #0:0: Video: mjpeg (Baseline), yuvj444p, 544x617
```

`jpeg_pipe` is the raw JPEG **pipe demuxer**: ffmpeg never opened the HEIF container.
It scanned the bytes, found a JPEG stream and decoded that. A real HEIF demux would
report the `mov,mp4,m4a,3gp,3g2,mj2` demuxer and an `hevc` stream.

And 544x617 is not an iPhone photo — an iPhone frame is around 4032x3024. So what
came out is either the **embedded JPEG preview** of the HEIC, or the file is not a
HEIC at all. The 0.121 s figure measures a thumbnail decode and must not be quoted as
a decode time.

## Why this matters beyond the spike

If the plugin ever shells out to ffmpeg **without forcing the demuxer**, this exact
fallback happens silently in production: `GetImageSize` returns 544x617, Jellyfin
persists those dimensions on the item, and every "full size" request serves a preview.
No error, no log, just wrong pixels.

**Rule for the encoder, whatever J0 concludes: force the demuxer, and assert the
decoded dimensions are plausible rather than trusting the exit code.**

## Run 2, same day — a bigger file, and the premise starts to crack

`IMG_2719.HEIC`, no forced demuxer:

```
Input #0, jpeg_pipe, from './IMG_2719.HEIC':
  Stream #0:0: Video: mjpeg (Baseline), yuvj420p, 4032x3024 [SAR 1:1 DAR 4:3]
```

Full resolution this time — **and still `jpeg_pipe` / `mjpeg`.** No `mov` demuxer,
no HEVC stream, on either file.

That is the tell. FFmpeg's `jpeg_pipe` probe only wins when the file **starts** with
a JPEG SOI marker; a real HEIC starts with an `ftyp` box and the `mov` demuxer scores
`AVPROBE_SCORE_MAX` on it. Two files, two different resolutions, both baseline MJPEG
from byte zero.

**Working hypothesis: these `.HEIC` files are JPEGs carrying a `.HEIC` extension.**
Something upstream in the upload chain already transcodes them — that is exactly what
iOS does when the transfer setting is "Automatic" rather than "Keep Originals" — while
keeping the original filename.

If that holds, **the whole premise of this project inverts for the current library**:
Jellyfin is not failing to decode HEIC, it is rejecting the files on their extension
in `PhotoResolver.IsImageFile`, while the bytes are ordinary JPEG that Skia reads
perfectly. The fix is a rename, not a 900-line plugin.

`real 0m6.435s` is again not a decode benchmark: most of it is writing a 16 MB PNG.
Re-time against a JPEG output if the number is ever needed.

## The decisive test, run 2026-09-20 — the library is mixed

`xxd` is absent from the container (it ships with vim); `od` is in coreutils and is
always present. On a file taken straight off the phone, `~/Downloads/IMG_4255.HEIC`:

```
00 00 00 28  66 74 79 70  68 65 69 63   ....ftypheic
6d 69 66 31  4d 69 48 45  4d 69 50 72  6d 69 61 66   mif1 MiHE MiPr miaf
```

A genuine HEIC — major brand `heic`, MIAF-conformant, HEVC Main Still Picture.
So the "everything is a mislabelled JPEG" hypothesis of run 2 is **wrong as a
general statement**. It was true only of the two files the spike happened to pick.

### Census, the photo host, `~/Downloads/Photos`

Content sniffed on every `.HEIC`, extension ignored:

| Year | `.HEIC` files | Real HEIC | JPEG mislabelled | % real |
|---|---|---|---|---|
| 2022 | 338 | 90 | 248 | 27 % |
| 2023 | 550 | 103 | 447 | 19 % |
| 2024 | 574 | **358** | 216 | 62 % |
| 2025 | 499 | 24 | 475 | 5 % |
| **Total** | **1 961** | **575** | **1 386** | **29 %** |

No `.heic` or `.heif` exists anywhere before 2022 — the tree goes back to 2007, so
the format simply predates none of the devices that fed it. The reverse error does
not occur either: no file named `.jpg`/`.jpeg`/`.png` anywhere in the tree contains
HEIF bytes.

**The 1 386 mislabelled files were renamed to `.jpg` on 2026-09-20** with
`tools/rename-mislabelled-heic.sh --apply`; a rescan confirmed they appear in
Jellyfin. Undo scripts sit in `~` on the server, one per pass
(`heic-rename-undo-2026092*.sh`). Every remaining `.heic` in the tree is genuine, so
**575 files is the plugin's real workload.**

The mislabelled files carry intact Apple EXIF (`iPhone 7`, `iPhone 11`,
`iPhone 13 Pro Max`, iOS 15.8-18.7), so they left the phone as HEIC and were
transcoded in transit while keeping the name. The collapse from 62 % to 5 %
between 2024 and 2025 points at a change of transfer method around the turn of the
year; the cause is unidentified, so **the ratio of future imports is unknown**.

The 24 real files of 2025 cluster in January (`./01/IMG_25xx`-`IMG_27xx`), plus two
in February and one in July. `IMG_2719` and `IMG_2720`, the spike's two subjects,
are *not* among them — which is exactly why both runs demuxed as `jpeg_pipe`.

## Consequences

**The project is not parked.** 575 files, and every correctly-transferred import from
here on, need a decoder. No rename touches them.

**The rename pass was orthogonal, and is done.** Those 1 386 files were invisible
purely on the extension gate and would have stayed invisible with the plugin
installed. Conversely the 575 survivors stay invisible until the plugin ships.

**The encoder should still not trust the extension.** The library is clean *today*,
but the transcoding upstream is unidentified and still running, so mislabelled files
will reappear. An encoder that assumes every `.heic` handed to it is HEIF will throw
on them. Sniff the magic bytes and decline politely on non-HEIF input — cheap
insurance, and it keeps the failure out of Jellyfin's logs.

Tooling for the rename lives in `tools/rename-mislabelled-heic.sh` (dry run by
default, content-sniffed, refuses to overwrite, emits an undo script). Re-run it
after each import. A rename also forces Jellyfin to re-index the file.

## Run 3, 2026-09-20 — ffmpeg demuxes the container but cannot deliver the image

On `2025/01/IMG_2547.HEIC`, confirmed genuine. **Caveat: this ran with Debian's
system ffmpeg `7.1.3-0+deb13u1` on the photo host, not with jellyfin-ffmpeg — the
Jellyfin host is a different machine.** Same upstream version; the behaviour below is
CLI logic rather than build configuration, so it is expected to carry over, but it is
not yet confirmed on the target.

`ffprobe` finally reports what run 1 and run 2 never did:

```
Input #0, mov,mp4,m4a,3gp,3g2,mj2
  major_brand: heic   compatible_brands: mif1MiPrmiafMiHBheic
  Stream group #0:0[0x31]: Tile Grid: hevc (Main Still Picture), yuvj420p, 4032x3024 (default)
  Stream #0:48[0x32]:      Video:      hevc (Main Still Picture), yuvj420p, 320x240
```

The `mov` demuxer, an HEVC Main Still Picture stream, full sensor resolution. The
version gate and the demuxer question are both settled: **not the problem.**

The problem is the word *Tile Grid*. The primary image is not one HEVC stream, it is
**48 tiles of 512x512** (8 x 6 covering 4032x3024) exposed as a *stream group*, plus
a 320x240 thumbnail as stream `#0:48`. 98 streams in the file overall.

| Invocation | Output |
|---|---|
| `ffmpeg -i f.HEIC -frames:v 1 -update 1 out.jpg` | **512x512** — one arbitrary tile |
| `ffmpeg -i f.HEIC -map 0:g:0 -frames:v 1 -update 1 out.jpg` | **512x512** — the group expands to 48 separate output streams, each written to the same path in turn, so the last tile wins |

`-map 0:g:0` does not stitch. The CLI exposes the tile grid but has no assembly step,
so no invocation tried yields a 4032x3024 frame. Exit code 0 in both cases.

**This is run 1's bug, in its final form.** The naive command returns a plausible
JPEG, silently, at 1.6 % of the pixels. Any encoder that trusts ffmpeg's exit code
ships wrong pixels into Jellyfin's image cache, where they persist.

Tiling is not an edge case here: **40 of 40 sampled genuine HEICs are tile grids**,
across all four years. Assume all 575 are.

## Run 4 — reproduced on the target, then solved with `xstack`

Confirmed on the Jellyfin host itself. Jellyfin runs under **podman**, container
`jellyfin`, image `docker.io/jellyfin/jellyfin:latest`, on the photo host — the same
box as the photos. `podman exec jellyfin …` reaches `/usr/lib/jellyfin-ffmpeg/ffmpeg`
(`7.1.3-Jellyfin`). The library is mounted `~/Downloads/Photos` -> `/media/Downloads/Photos`,
so this *is* the scanned library, not a staging area.

jellyfin-ffmpeg behaves exactly like the Debian build: both invocations return
512x512. **The tile-grid limitation is confirmed on the target.**

But stitching by hand works, and it is not the fragile last resort the table above
assumed:

```sh
ffmpeg -i f.HEIC -filter_complex "[0:0][0:1]…[0:47]xstack=inputs=48:layout=0_0|512_0|…,crop=4032:3024:0:0[out]" \
       -map "[out]" -frames:v 1 -update 1 out.jpg
```

4032x3024, visually verified correct — tiles in raster order, no seams. **0.58 s**
wall clock. Note `[0:N]` filtergraph labels: `-map` on the inputs fails with
`Cannot find a matching stream for unlabeled input pad xstack`.

### The geometry comes from ffprobe, not from guesswork

`ffprobe -show_stream_groups -of json` returns everything the filtergraph needs:

```json
{"index":0,"nb_streams":48,"type":"Tile Grid",
 "components":[{"nb_tiles":48,"coded_width":4096,"coded_height":3072,
   "width":4032,"height":3024,
   "subcomponents":[{"stream_index":0,"tile_horizontal_offset":0,"tile_vertical_offset":0}, …]}]}
```

Per-tile stream index and exact offsets, plus the crop target (`width`/`height`
against `coded_*`). The layout string is a mechanical transform of this JSON.

**Nothing may be hardcoded.** Sampling 30 files:

- tiles are usually 512x512, but one file is **896x960** — tile size varies;
- some files carry a second tile grid — see the full survey below, which corrects the
  count this line originally gave;
- the 320x240 thumbnail is usually present but not always.

### The one thing ffprobe will not tell you: rotation

Every genuine file carries an `irot` box, and **ffprobe exposes no rotation at all** —
no side data, no tag, nothing but ICC profiles. The stitched output is therefore
mis-rotated with no error of any kind.

Parsed straight out of the container (`irot` box, angle in the low 2 bits of the
following byte, counter-clockwise), across all 575:

| Rotation | Files |
|---|---|
| 0° | 240 |
| 90° | 2 |
| 180° | 22 |
| 270° | **311** |

**335 of 575 files — 58 % — are wrongly oriented if `irot` is ignored.** Applying it
as a `transpose` appended to the filtergraph (270° CCW -> `transpose=1`) yields a
correct 3024x4032 portrait, visually verified.

## Consequence: the ffmpeg route survives J0, with conditions

Plan §3's preferred column stands, but the encoder is no longer a thin `ffmpeg -i`
wrapper. It must, per file: probe the stream groups, build an N-input `xstack`
layout from `subcomponents`, crop to `width`x`height`, parse `irot` from the raw
bytes, and append the matching `transpose`.

| Option | Note |
|---|---|
| **jellyfin-ffmpeg + generated filtergraph** | Zero new dependencies, 0.58 s/photo, proven end to end on the target. Cost: ~100 lines of geometry plumbing and a 6-byte container parse. Leading option. |
| **Magick.NET** | libheif handles grid *and* `irot` natively, so the plugin shrinks to a few lines. Cost: in-process native dependency and CVE surface, **and a mandatory security scan** per the third-party rule. Worth benchmarking against the above. |
| Stitching without `irot` | Not an option. 58 % of the library comes out sideways, silently. |

## J0 verdict

**Passed.** jellyfin-ffmpeg can produce a correct full-resolution image for this
library, without new dependencies, in ~0.6 s per photo. J1 may start.

Open items, none of them blocking. **All four are now closed** — item 1 in
`03-prior-art.md` §4, items 2 to 4 in the full survey above.

1. **Benchmark Magick.NET against the filtergraph** before committing to the
   plumbing. If it is close on speed, libheif doing grid + `irot` natively may be
   worth the dependency. Security-scan it first, package and bundled natives both.
2. **Confirm the second 48-tile grid is the HDR gain map** rather than a
   higher-quality primary. Decoding the wrong group would be invisible in testing.
3. **Check `imir`** (mirroring) alongside `irot`; it was not surveyed, and it
   composes with rotation.
4. **Re-check the tile-size assumption on the full 575**, not the 30 sampled — one
   outlier at 896x960 already showed up.

## The full survey, 2026-09-22 — all 575 files, and three of those items closed

`ffprobe -show_stream_groups` plus an `irot`/`imir` byte scan, run over every genuine
HEIC. **Zero errors.** It settles items 2, 3 and 4, and corrects run 4 on one point.

| Finding | Value |
|---|---|
| Rotation (CCW, from `irot`) | 0°: 240 · 90°: 2 · 180°: 22 · 270°: 311 — reproduces run 4 exactly |
| `imir` | **absent from all 575.** Item 3 closed: mirroring does not occur in this library |
| Tile grids per file | 573 have **one**, 2 have two |
| `nb_tiles` | 17 distinct values, from **9 to 98** |
| Grid size | mostly 4032x3024, but 3088x2316, 2200x2098, and panoramas up to **11612x3852** |

**Run 4's "every file carries 96 tile streams, two grids" was wrong.** Only two files
have a second grid, and in both it is exactly half the primary's dimensions —
3322x2534 / 1661x1267 and 4032x3024 / 2016x1512. That halving is the signature of an
HDR gain map, so **item 2 is closed too**: the companion grid is not a better primary.

The primary is identified by `disposition.default == 1` on the stream group, which the
demuxer sets from the container's primary-item box. It is correct on both two-grid
files. Picking by size or by position would also have worked here, but only by luck.

**The trap the survey exposed, which no sampling would have:**
`subcomponents[].stream_index` is numbered **relative to its own stream group**, while
a filtergraph label `[0:N]` needs the file-wide index. The two coincide for group 0 and
diverge for every group after it — group 1 of `IMG_9564.HEIC` reports tiles `0..11`
while its streams are `48..59`. Translate through the group's own `streams` array.

Item 4 is closed by the `nb_tiles` spread above: tile geometry varies far more than the
30-file sample suggested, and nothing about it may be assumed.

### End-to-end validation, same day

40 random files decoded with the algorithm as implemented: **40 correct, 0 failures**,
median **0.54 s**, worst **1.40 s**. One file per rotation bucket was also rendered and
looked at — all four upright, no seams, no scrambled tiles. `transpose=1` for 270° CCW
and `transpose=2` for 90° CCW are both confirmed visually, which a dimension check
alone cannot do, since either direction produces the same width and height.

## Where the EXIF actually sits — J3, 2026-09-22

Not a decoder question, but the same kind of container fact, established the same way:
by probing all 575 rather than a sample.

**ffprobe is not a source of EXIF here.** On a genuine HEIC it reports the brands and
the geometry and no date at all, so J3 could not shell out the way the decoder does.
The plugin parses the container itself.

**The EXIF block is found by its marker, not by walking `meta` → `iinf` → `iloc`.**
The walk is the textbook route and several times the code; the marker is exact enough
without it — on one condition.

> **`Exif\0\0` is not unique in the file.** It also appears roughly one kilobyte in,
> inside the `infe` box that *declares* the Exif item's type. The first implementation
> matched there, seeked to the `iref` box that follows, and found **zero** EXIF across
> the entire library — with no error, because a failed TIFF parse returns null.

What is matched is therefore **ten bytes**: the marker plus a well-formed TIFF header,
`Exif\0\0MM\0*` or `Exif\0\0II*\0`. Surveyed across all 575: **575 big-endian, 0
little-endian, 0 not found.** The little-endian pattern is kept because the format
permits it, not because anything here uses it.

**The block is not in the head of the file.** A fixed 256 KB read reaches it in 565 of
575. The other ten — the panoramas, 3.4 to 16.3 MB — hide it between **487 760 and
2 499 938 bytes** in. The search runs in windows up to an 8 MB ceiling, carrying nine
bytes between them so a marker straddling two windows is not missed by both.

**EXIF orientation and the container's `irot` agree on all 574 files that have both**
— 0°↔1, 90°↔8, 180°↔3, 270°↔6, zero disagreement. That is a second, independent
confirmation of the rotation the decoder applies. It is also why the plugin reads
orientation and then throws it away; `02-jellyfin-internals.md` §5a says why storing
it would break the aspect ratio.

**Coverage over the 575:** EXIF block 575, date 575, ISO 575, GPS **559** — sixteen
photos genuinely carry no coordinates.

## `crop` rounds odd dimensions down — J4, 2026-09-22

Eleven of the 575 declare a size with an odd dimension, and every one of them decoded
one pixel short: `3418x2359` came out `3418x2358`, `3024x3115` came out `3024x3114`.
The acceptance test below caught them, so nothing wrong was ever cached — but eleven
perfectly good photos were being thrown away.

`crop` aligns its output to the chroma subsampling of its **input**. The tiles are
`yuv420p`, so the filter silently rounds the crop height down to an even number. The
output format has nothing to do with it: writing PNG rounds down identically, and so
does forcing `yuvj444p` on the encoder — both were tested and both failed the same way.

The fix is one token: `crop=W:H:X:Y:exact=1`. It is safe here because the crop offset
is always `0:0`, so disabling the alignment cannot shift the chroma planes. Verified
live: all eleven now serve at exactly their declared size.

## Corrupt input, measured 2026-09-22

Eight deliberately malformed `.heic` files — empty, truncated at 100 B / 2 KB / 300 KB
/ half, 200 KB of `/dev/urandom`, a valid header over a corrupted body, and a 24-byte
header alone — put through a real library scan and then a full-size image request each.

| input | what stops it | cost |
|---|---|---|
| empty, random bytes | the `ftyp` brand check, **before any process is spawned** | a 0-byte read |
| truncated at 100 B, 2 KB, 24 B | ffprobe: `moov atom not found` / `error reading header` | < 0.2 s |
| truncated at 300 KB / half, corrupted body | ffprobe reports a valid grid, **ffmpeg fails during the decode** | < 1 s |

Every case falls back to the undecorated encoder, so the client gets the original
bytes — the status quo, not a broken image. No hang, no unhandled exception, no
temporary file left in `/tmp/jellyfin`. The brand check earning its keep on the first
two rows is the same code path that absorbs the 1 386 mislabelled JPEGs.

## Acceptance test

Whatever decoder wins, the acceptance test is the same, and it is *not* the exit
code:

```
decoded dimensions == EXIF PixelXDimension/PixelYDimension
```

Run 1 and run 3 both exited 0 while returning a fraction of the image. Assert the
dimensions, or ship wrong pixels into a cache that will not forget them.

`-update 1` is required for any single-file image output: the `image2` muxer refuses
the write without it, and said so in every run.

## Server facts (confirmed 2026-09-21)

- Jellyfin **10.11.6** under **podman**, container `jellyfin`, image
  `docker.io/jellyfin/jellyfin:latest`, on the photo host. J1 targets that ABI.
  Reach the binaries with `podman exec jellyfin …` (there is no `docker` command).
- jellyfin-ffmpeg **7.1.3-Jellyfin**, Debian, amd64, gcc 14 — above the 7.0 HEIF
  floor, so the version gate is not the problem. Behaves identically to Debian's
  system `ffmpeg 7.1.3-0+deb13u1`, also installed on the host at `/usr/bin/ffmpeg`.
- Library mount: `~/Downloads/Photos` -> `/media/Downloads/Photos`. Paths differ
  between host and container; the spike commands must use the container path.

- Photo source: the photo host, `~/Downloads/Photos`, one directory per year,
  months as `01`-`12` subdirectories under the recent ones. HEICs sit beside `jpg`,
  `png`, `mov` and `mp4` in the same folders.

Still missing: whether this tree is the one mounted as a Jellyfin library, and the
2007-2022 counts.
