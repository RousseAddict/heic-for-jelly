# heic-for-jelly

A Jellyfin server plugin that makes **HEIC/HEIF photos visible and renderable** in a
Jellyfin photo library. C#/.NET 9, loaded by the Jellyfin server — no client changes.

Born out of `../jellypic` (an iOS reader on top of Jellyfin) but **it is a separate
project on a separate release cycle**: Jellyfin's, not the app's. jellypic is a
consumer of the result, never a dependency.

## Why it exists

`PhotoResolver.IsImageFile()` gates on `ImageProcessor.SupportedInputFormats`, which
contains no `heic`/`heif` in 10.11 nor on `master`. The resolver returns `null`, so
**no `Photo` item is ever created** — the files are absent from `/Items` entirely,
not merely broken thumbnails. Upstream has one open issue (jellyfin#17312) and no
implementation.

Verified facts, with the exact call sites, live in `docs/02-jellyfin-internals.md`.
Read it before writing any code — several of its findings invert the obvious design.

## Layout

```
CLAUDE.md
docs/
  01-plan.md                 scope, the four pieces, milestones, risks
  02-jellyfin-internals.md   verified source facts (v10.11.0) + file:symbol refs
  03-prior-art.md            existing plugins, security audit, upstream status
  04-j0-spike.md             the decoder spike: results, next commands, branches
```

## Ground rules

**Verify Jellyfin behaviour by reading source, not search results.** `curl` against
`raw.githubusercontent.com` works; `gh` CLI is not installed. Pin to release tags
(`v10.11.0`), never `master`. The git trees API works unauthenticated for finding
paths: `https://api.github.com/repos/jellyfin/jellyfin/git/trees/v10.11.0?recursive=1`.

**Every claim in `docs/` cites `file:symbol`, never `file:line`.** Line numbers rot
across releases; symbols survive.

**No new HTTP surface.** The design plugs into `IImageEncoder`, inside Jellyfin's own
image pipeline, precisely so that auth, per-library ACL, the resized-image cache and
ETags keep applying untouched. Any proposal that adds a controller or a middleware is
re-opening the hole that disqualified the prior art — see `docs/03-prior-art.md`.

**Third-party code is scanned before use, not after.** That rule is what caught the
unauthenticated arbitrary-file-read in `heicext`. Applies to NuGet packages too.

**No prior-art code is copied.** `heicext` carries no licence at all, so it grants no
rights. It is a reference for *what breaks*, never a source.

**Commits carry no co-author trailer**, no "Generated with" footer, no tool
attribution. Message = the change and its reason. (Carried over from jellypic — say
so if it should not apply here.)

## Conventions, settled 2026-09-21

jellypic's "no comments in app sources" rule **does not apply here**. StyleCop stays
on, with its SA16xx documentation rules and `TreatWarningsAsErrors`, and **every
public member carries an XML doc comment**. Chosen for conformance with the Jellyfin
plugin ecosystem, should this ever be published.

Write doc comments that earn their place — why a guard exists, what a caller must not
assume. `<summary>Gets or sets the name.</summary>` satisfies the analyzer and tells
the reader nothing; prefer a sentence that would have saved someone an hour.

## Status

**J0 through J5 are passed. The plugin is done; what remains is upkeep.**

The premise is confirmed. The doubt raised by the first two J0 runs is resolved: the
library was *mixed*, and the spike had picked two of the bad files. A content sniff of
all 1 961 `.HEIC` found 1 386 JPEGs wearing the wrong extension and **575 genuine
HEICs** (2022-2025; the format appears nowhere earlier).

The mislabelled ones were renamed to `.jpg` on 2026-09-20 via
`tools/rename-mislabelled-heic.sh` and now show up in Jellyfin. The remaining 575 are
what this plugin exists for, and nothing but a decoder will reach them.

J1 shipped a skeleton that the server loads cleanly — `Loaded plugin: "HEIC for Jelly"
"1.0.0.0"`, no warning — which is all it was meant to prove: that net9.0 against
`Jellyfin.Controller` 10.11.6 is the ABI this server actually accepts. It decodes
nothing.

J2 shipped the resolver and the decoding encoder, and the photos now render. The
encoder is **not** a thin `ffmpeg -i` wrapper: the genuine files are HEVC **tile
grids**, and no plain invocation decodes them — each returns a single 512x512 tile
with exit code 0. What works is an `xstack` filtergraph generated from
`ffprobe -show_stream_groups -of json` (per-tile stream index and offsets), cropped
to the declared size, with a `transpose` appended for the `irot` rotation — which
ffprobe does not expose and which 58 % of the library needs.

Measured live on 2026-09-22: **575 `Photo` items**, dimensions matching an independent
ffprobe survey **575/575 with zero mismatch**, 575 primary images, no leaked temp file,
no plugin error, full scan of 7 716 files in **1 min 01 s**. The scale risk did not
materialise because `GetImageSize` answers from the probe, not from a decode. Details
in `docs/01-plan.md` §9.

J3 filled in the EXIF, and the timeline sorts. **575/575 exact on twelve fields** —
date, camera, software, exposure, GPS — diffed against an independent extractor
sharing no code (`docs/01-plan.md` §10). The planned `MetadataExtractor` dependency
was dropped: `HeicExif.cs` parses the TIFF block by hand, reading only the tags
Jellyfin stores, with every offset bounds-checked.

Three things J3 learned that will bite again:

- **`Exif\0\0` is not unique in a HEIC.** It also sits in the `infe` box a kilobyte
  in. Match the marker *plus* a TIFF header, or you find nothing and see no error.
- **The EXIF is not in the head of the file.** Ten of the 575 hide it up to 2.5 MB in.
- **A scan does not re-run a newly deployed metadata provider** — `HasChanged` gates
  it. An explicit `replaceAllMetadata=true` refresh is required (`docs/02` §5b).

`Photo.Orientation` is left null on purpose; writing it would give every portrait photo
a landscape aspect ratio, and writing `TopLeft` would serve raw HEIC. See `docs/02` §5a
before ever "fixing" that.

J4 closed both gaps, and neither was what it looked like.

- The full-size request **did not** bypass `EncodeImage` — `ImageProcessor`'s
  short-circuit needs `!options.RequiresAutoOrientation`, which is never true here. It
  404ed instead, because `SkiaEncoder` has a *second* short-circuit that returns its
  input path when `autoOrient` is false, and that input was our temporary file, already
  deleted. Passing `autoOrient: true` with `ImageOrientation.TopLeft` fixes it; a guard
  now rejects any returned path that is not `outputPath`. Full story in `docs/02` §4a.
  **Leaving `Orientation` null is what keeps the first short-circuit shut** — writing it
  would re-open the hole.
- Eleven files decoded one pixel short: `crop` aligns to the **input's** chroma
  subsampling (`yuv420p` tiles), so an odd height rounds down. `exact=1` on the crop.
  The output format is irrelevant — PNG and `yuvj444p` were both tested and both failed.
- Eight deliberately corrupt files (empty, truncated, random, broken body) scan and
  render with no hang, no exception and no leaked temporary. Empty and random bytes are
  rejected by the `ftyp` brand check before any process is spawned.

Measured: **40/40** full-size renders at exactly the stored dimensions, median 3.1 s,
max 16.0 s for a 15736x3804 panorama. `docs/01-plan.md` §11.

J5 rendered all 575 at a thumbnail size the UI never requests, so every one was a cold
decode: **575/575 correct, zero failure, not one warning logged**. Median 0.92 s, p90
0.98 s, max 3.33 s (a 98-tile panorama), 8 min 45 s for the library. A cached
re-request is 0.038 s — the cost is paid once per photo per size, and a library scan
avoids it entirely because `GetImageSize` answers from the probe. Cache: 78 MB for the
575 thumbnails, ~5 MB per full-size render. `docs/01-plan.md` §12.

The remaining risk is the one §5 of the plan named first and J5 cannot retire: **ABI
drift**. The plugin pins `Jellyfin.Controller` 10.11.6, and any 10.x minor can break
it. That is what froze the prior art at 10.9.

**Read `docs/04-j0-spike.md` before anything else.** Never benchmark on
`IMG_2719`/`IMG_2720` — they are mislabelled JPEGs and are what sent the first two
runs astray.

Server: Jellyfin **10.11.6** under podman, jellyfin-ffmpeg **7.1.3**, amd64.

Building: the dev Mac is Big Sur and no supported .NET runs there, so `tools/build.sh`
rsyncs to a Monterey box and compiles over ssh. `--deploy` installs the dll into
Jellyfin but never restarts it — a restart cuts playback, so it stays a manual call.

## Releasing

The repository is also its own Jellyfin plugin catalogue: `manifest.json` at the root
of `main` is the URL users subscribe to. Cutting a release means editing **meta.json
only** — `version` and `changelog` — bumping `AssemblyVersion`/`FileVersion` to match,
then pushing the tag `v<version>` (four segments, e.g. `v1.0.1.0`). The `release`
workflow builds, zips, publishes a GitHub release and commits the new entry into
`manifest.json`. It refuses to run if the three versions disagree.

Two invariants it guards, both learned from the official plugin template:

- `Jellyfin.Controller`/`Jellyfin.Model` carry `<ExcludeAssets>runtime</ExcludeAssets>`.
  Without it the build copies Jellyfin's own assemblies into `bin/`, and a plugin that
  ships a second copy of the host's types does not register. The Package step fails if
  more than one dll lands in the output.
- The zip holds the dll and `meta.json` at its root with no enclosing directory:
  `InstallationManager.InstallPackageInternal` extracts it straight into the plugin
  folder, after checking the zip's **MD5** against the manifest's `checksum`.

`targetAbi` stays at the exact version the plugin was built and tested against.
Jellyfin drops any entry whose `targetAbi` exceeds the running server
(`InstallationManager.GetPackages`), so lowering it to widen reach would publish a
claim nobody has verified.
