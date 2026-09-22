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
| J2 | Resolver + encoder: HEIC files appear **with thumbnails** on a 10-file folder | most of the technical risk dies here |
| | *resolver* — done | HEIC files become `Photo` items |
| | *encoder decoration + ffmpeg version gate* — done, verified live: `Image encoder decorated: "HeicForJelly over Skia"` | the plugin is on the decode path for every image |
| | *the decode itself* — written, 40/40 correct offline at 0.54 s median; **not yet verified inside Jellyfin** | ffprobe geometry, `xstack`, crop, `irot` transpose |
| J3 | EXIF to `PremiereDate` — correct order in jellypic | |
| J4 | Hardening: ignore-files, video-owned images, corrupt files, logging, **and the full-size request that bypasses `EncodeImage` entirely** (see `02` §4) | |
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
