# Plan

Agreed in chat 2026-09-20. Facts behind every design choice are in
`02-jellyfin-internals.md`; the reasons not to reuse existing work are in
`03-prior-art.md`.

## 1. Scope

A Jellyfin **server plugin**, C#/.NET 9, its own repository, its own release cycle.
Roughly **four useful files, 600-900 lines**, half of it template boilerplate.

`../jellypic` is a consumer, not a dependency: if the plugin works, the iOS reader
sees extra items in `/Items` and needs no change whatsoever.

## 2. The four pieces

| Piece | Role | ~size |
|---|---|---|
| `Plugin.cs` + `PluginServiceRegistrator.cs` | official template boilerplate | 100 l. |
| `HeicItemResolver : IItemResolver` | creates the `Photo` item `PhotoResolver` refuses | 80 l. |
| `HeicImageEncoder : IImageEncoder` | decorates `SkiaEncoder`: decodes HEIC, delegates the rest | 200 l. |
| `HeicMetadataProvider : ICustomMetadataProvider<Photo>` | EXIF to `PremiereDate`, orientation, GPS | 150 l. |

**The encoder decorator is the heart of the design.** Plugging in there keeps the
request inside Jellyfin's own pipeline — auth, per-library ACL, resized-image cache,
encoding concurrency limit, ETags, `?tag=` immutability. No controller, no
middleware, **no new attack surface**.

The metadata provider is not optional: it is the only thing that sets `PremiereDate`,
and jellypic's entire timeline sorts on it.

No `ILocalImageProvider` is needed — `PhotoProvider.FetchAsync` already sets the
primary image path and probes the dimensions for us.

## 3. Decision to settle: the decoder backend

| | Magick.NET (what the prior art chose) | jellyfin-ffmpeg (recommended) |
|---|---|---|
| Dependency | ~150 MB of native code **per platform**, shipped in the plugin | already installed on the server |
| Isolation | parses untrusted input **inside** the Jellyfin process | separate process, discarded after use |
| CVE exposure | steady ImageMagick stream, in-process | ffmpeg surface, out-of-process |
| Plugin size | ~200 MB | ~50 KB |
| Risk | — | needs **ffmpeg >= 7.0**; Jellyfin itself accepts 4.4 |

Within the ffmpeg option, ffmpeg **decodes only**, to a temporary PNG, and **Skia
still performs the resize and the final encode**. That way none of
`ImageProcessingOptions` (blur, background colour, foreground layer, played
indicators, output format negotiation) is reimplemented — the plugin only opens the
door to the format. Cost: one temp file per decode.

EXIF parsing needs a managed library (`MetadataExtractor` is the candidate; TagLib#,
used by the core, is what `PhotoProvider`'s own comment suspects of hanging on exotic
formats). **Security-scan it before adding the dependency.**

## 4. Out of scope, explicitly

- **No file is ever written next to the originals.** Transcoding the library is the
  other solution, not this one.
- HEIC for `Photo` items only — not avatars, posters or artwork.
- No Live Photos, no bursts. Primary image only — but note that the secondary grid
  present in every file (probably the HDR gain map) must be *actively avoided*, not
  merely ignored.

**Tiled HEIC is emphatically in scope** — the line that once excluded it was wrong.
J0 found that 100 % of the library is tiled, so excluding it would exclude everything.
ffmpeg 7.1.3 does not stitch tiles by itself, but `04-j0-spike.md` has a working
recipe built on `ffprobe -show_stream_groups`.
- No configuration page in the first pass.
- Zero changes to jellypic.

## 5. What can sink the project

1. **ABI drift.** The plugin links against one `Jellyfin.Controller` version; every
   minor 10.x can break it. This is the real, recurring cost — not the 900 lines.
   It is what froze `heicext` at 10.9.
2. **`ResolverPriority.First` runs before every other resolver, for every file.** A
   mistake in the early-exit breaks the whole library, not just photos. It must also
   reproduce `PhotoResolver`'s `_ignoreFiles` and `IsOwnedByMedia` rejections.
3. **Replacing `IImageEncoder` relies on last-registration-wins**, a Microsoft DI
   behaviour that Jellyfin does not document as a contract. True today; not promised.
4. **Scale.** A full scan means one HEIC decode per photo for the dimensions, plus one
   per requested thumbnail size. On 20k items and a modest NAS that can take a very
   long time. Measure on a sample before unleashing a full scan.

## 6. Milestones

| | Deliverable | What it decides |
|---|---|---|
| ~~**J0**~~ | ~~**Spike**: does jellyfin-ffmpeg decode a real HEIC, and how fast~~ | **passed 2026-09-21** — it decodes, but only through a generated `xstack` graph; see §8 |
| ~~J1~~ | ~~Skeleton that loads and shows up in the dashboard~~ | **passed 2026-09-21** — net9.0 against `Jellyfin.Controller` 10.11.6 is the right ABI; the server logs `Loaded plugin: "HEIC for Jelly" "1.0.0.0"` with no warning |
| ~~J2~~ | ~~Resolver + encoder: HEIC files appear **with thumbnails**~~ | **passed 2026-09-22** — most of the technical risk is dead; see §9 |
| | *resolver* — done | HEIC files become `Photo` items |
| | *encoder decoration + ffmpeg version gate* — done, verified live: `Image encoder decorated: "HeicForJelly over Skia"` | the plugin is on the decode path for every image |
| | *the decode itself* — done | ffprobe geometry, `xstack`, crop, `irot` transpose |
| ~~J3~~ | ~~EXIF to `PremiereDate` — correct order in jellypic~~ | **passed 2026-09-22** — 575/575 on twelve fields; see §10 |
| ~~J4~~ | ~~Hardening: ignore-files, video-owned images, corrupt files, logging, and the full-size request~~ | **passed 2026-09-22** — two real bugs found and fixed; see §11 |
| J5 | Full library, with measurements | |

J0 is deliberately first: it is the only step that can cancel the other five, and it
costs one command.

## 7. Needed before J1 — all answered 2026-09-21

- Jellyfin **10.11.6**, amd64 Debian, under **podman** (not docker), container
  `jellyfin`, on the photo host.
- jellyfin-ffmpeg **7.1.3-Jellyfin** at `/usr/lib/jellyfin-ffmpeg/ffmpeg`.
- **575 genuine HEICs**, 2022-2025, sitting beside jpg/png/mov in
  `/media/Downloads/Photos`, which is a scanned library.

Both former blockers are cleared. The **SDK is .NET 9**, not 8: Jellyfin 10.11 moved
to net9.0 and `Jellyfin.Controller` 10.11.6 ships no net8.0 asset at all. The dev Mac
runs Big Sur, which no supported .NET reaches, so compilation is delegated over ssh to
a Monterey machine — `tools/build.sh` does the sync, build and install. The project is
now a git repository.

## 8. Revision to §3, after J0

The ffmpeg column won, but not for the reason stated: the "decode to a temp PNG"
sketch understated the work. The encoder must build a per-file `xstack` filtergraph
from `ffprobe -show_stream_groups -of json` and append a `transpose` derived from the
container's `irot` box, which ffprobe does not expose. Roughly 100 lines of geometry
plumbing rather than a one-line shell-out. Skia still resizes and encodes; that part
of §3 stands.

Magick.NET was the alternative, and **it was evaluated and rejected on 2026-09-21** —
scan clean, capability never settled, and it decodes in-process. Full reasoning in
`03-prior-art.md` §4. The decoder is ffmpeg; treat that as closed.

## 9. J2, measured live 2026-09-22

| | |
|---|---|
| `Photo` items created | **575** — the whole library, not the test folder |
| Stored dimensions vs an independent ffprobe survey | **575 / 575 exact, zero mismatch** |
| Primary image rows | 575 |
| Temp files left behind | **0** |
| Plugin errors or warnings | none |
| Full scan, 7 716 files including 575 probes | **1 min 01 s** |

Dimensions are stored portrait (3024x4032) for grids coded landscape (4032x3024), so
the `irot` rotation ffprobe cannot see is being read and applied. A 575-file agreement
also validates the primary-group choice and the stream-index translation across every
file rather than a sample.

**Risk 4 of §5 — scale — did not materialise.** Answering `GetImageSize` from the probe
instead of a decode is what bought that: a scan costs one ffprobe per photo, not one
0.5 s decode. Decoding still happens once per requested thumbnail size, which is the
figure J5 has to measure.

**Two things J2 did not prove**, both already booked for J4: a full-size request still
bypasses `EncodeImage` entirely (`02` §4), and no corrupt or truncated HEIC has been
put through the decoder yet.

## 10. J3, measured live 2026-09-22

The whole `Photos` library refreshed with `replaceAllMetadata=true`, then every stored
item diffed field by field against an independent Python extractor built from the same
specification but sharing no code.

| | |
|---|---|
| HEIC items carrying a `PremiereDate` | **575 / 575** — none left undated |
| `DateTaken` exact match | 575 / 575 |
| CameraMake · CameraModel · Software | 575 / 575 each |
| ExposureTime · ISO · ShutterSpeed · Aperture · FocalLength | 575 / 575 each |
| Latitude · Longitude · Altitude | 575 / 575 each |
| Items with `Orientation` written | **0** — deliberate, see `02` §5a |
| Plugin errors or warnings | none |

Twelve fields × 575 files, zero mismatch.

§3's "EXIF parsing needs a managed library" **did not hold**, and the security scan it
called for never had to happen: `MetadataExtractor` was dropped in favour of a
hand-written TIFF reader (`HeicExif.cs`, ~570 lines with its documentation) that reads
only the dozen tags Jellyfin stores. No new dependency, no transitive tree, and every
offset it follows is bounds-checked against the block it was handed.

Two findings that cost real time are written up where they belong: the EXIF block is
**not in the head of the file** and its marker is **not unique** within it
(`04-j0-spike.md`), and a library scan **will not re-run a newly deployed metadata
provider** (`02` §5b).

## 11. J4, measured live 2026-09-22

J4 was booked as hardening against inputs the plugin had never seen. It found two bugs
in the path it had already shipped, which is the better outcome.

**The full-size request did not bypass `EncodeImage` — it 404ed.** §5's list of things
that could sink the project did not contain "the inner encoder hands back a path we are
about to delete", and that is exactly what happened. Root cause and fix in `02` §4a; it
is one boolean, plus a guard so the class of bug cannot recur silently.

**Eleven files decoded one pixel short and were correctly discarded.** The post-decode
assertion J0 insisted on did its job; the cause was `crop` aligning to the input's
chroma subsampling, fixed with `exact=1` (`04-j0-spike.md`). Two wrong hypotheses were
tested and rejected before the right one — the output pixel format, then PNG.

| | |
|---|---|
| Full-size requests, 40-file random sample | **40 / 40 served at exactly the stored dimensions** |
| The 11 odd-dimension files, full size | 11 / 11 exact |
| Malformed inputs (8 files, scan + request) | no hang, no exception, original bytes served |
| Temporary files left behind | **0** |
| Plugin errors or warnings during the sample | none |
| Full-size decode, median · max | **3.1 s · 16.0 s** (the 15736x3804 panorama) |

The resolver guards J4 was also meant to add were already in `HeicPhotoResolver`:
ignored filename prefixes, the collection-type check, and the sibling-video ownership
test. They were written in J2 and are listed in `02` §6.

**The cost J5 has to weigh is now visible.** Forty full-size renders added 210 MB to
`cache/images`. The whole library at full size is roughly 3 GB, and a full-size decode
is five times a thumbnail's. That is a measurement, not a blocker, but J5 should size
the cache volume before sweeping 575 files.
