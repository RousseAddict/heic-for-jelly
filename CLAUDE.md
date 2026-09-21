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

**J0 and J1 are both passed. J2 is next: the resolver and the encoder.**

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

The encoder J2 must add is **not** a thin `ffmpeg -i` wrapper.
The genuine files are HEVC **tile grids**: no plain invocation decodes them, each
returns a single 512x512 tile with exit code 0. What works, verified inside the
container at ~0.6 s per photo, is an `xstack` filtergraph generated from
`ffprobe -show_stream_groups -of json` (per-tile stream index and offsets), cropped
to the declared size, with a `transpose` appended for the `irot` rotation — which
ffprobe does not expose and which 58 % of the library needs.

**Read `docs/04-j0-spike.md` before anything else.** Never benchmark on
`IMG_2719`/`IMG_2720` — they are mislabelled JPEGs and are what sent the first two
runs astray.

Server: Jellyfin **10.11.6** under podman, jellyfin-ffmpeg **7.1.3**, amd64.

Building: the dev Mac is Big Sur and no supported .NET runs there, so `tools/build.sh`
rsyncs to a Monterey box and compiles over ssh. `--deploy` installs the dll into
Jellyfin but never restarts it — a restart cuts playback, so it stays a manual call.
