# Jellyfin 12 — one binary for 10.11 and 12

Verified 2026-11-20 by diffing the **`v10.11.6`** and **`v12.4`** tags file by file,
then by loading the published 1.0.0.0 zip, unmodified, into a live 12.4 server.
Citations are `file:symbol`.

**Verdict: the existing binary already serves both.** No source change, no second
build, no new release was needed. What follows is why, what was measured, and the one
condition under which that stops being true.

## 1. What Jellyfin 12 changed that could have mattered

| Change | Where | Effect on this plugin |
|---|---|---|
| Version jumps 10.11 → 12; tags `v12.0`…`v12.4`, NuGet `12.x.0` | `SharedVersion.cs` (`AssemblyVersion("12.4.0")`) | none — see §3 |
| **.NET 10.** `Jellyfin.Controller` 12.x ships a `net10.0` asset only | `global.json`, the package's nuspec | a 12.x build would need net10.0; the 10.11 build is net9.0 |
| **ffmpeg 8.1.3** instead of 7.1.3 | jellyfin-packaging `docker/Dockerfile:FFMPEG_PACKAGE` = `jellyfin-ffmpeg8` | the decoder parses ffprobe's JSON — §5 |
| Resized-image cache invalidated | `src/Jellyfin.Drawing/ImageProcessor.cs:Version` `'3'` → `'4'` | every thumbnail is regenerated once after a server upgrade |

## 2. What did not change

**Byte-identical between the two tags**, i.e. everything the plugin implements, calls,
or depends on behaviourally:

- `MediaBrowser.Controller/Drawing/IImageEncoder.cs` — all 11 members
- `MediaBrowser.Controller/MediaEncoding/IMediaEncoder.cs`
- `Emby.Server.Implementations/Library/Resolvers/PhotoResolver.cs` — **still no HEIC**,
  so the plugin is still needed on 12
- `Emby.Photos/PhotoProvider.cs` — the EXIF extension list still excludes HEIC
- `MediaBrowser.Controller/Resolvers/ItemResolver.cs`, `ResolverPriority.cs`
- `MediaBrowser.Controller/Plugins/IPluginServiceRegistrator.cs`,
  `MediaBrowser.Common/Plugins/BasePlugin*.cs`
- `Jellyfin.Server/CoreAppHost.cs:RegisterServices` — the image encoder is still
  registered by type before `PluginManager.RegisterServices` runs, which is the
  ordering the decorator relies on
- `MediaBrowser.Controller/Entities/Photo.cs`, the three provider interfaces, and every
  enum whose values are compiled into the dll (`ItemUpdateType`, `ImageOrientation`,
  `ImageFormat`, `CollectionType`)
- `Emby.Server.Implementations/Plugins/PluginLoadContext.cs`

**Changed, but not in anything the plugin touches:**

- `SkiaEncoder` moved to SkiaSharp 3 and sharpens in place, but `EncodeImage` is
  identical, including the `HasDefaultOptions(...) && !autoOrient` short-circuit of
  `02` §4a. `GetImageSize` only gained an SVG safety check.
- `ImageProcessor.ProcessImage` is identical, so the first short-circuit of `02` §4a
  still stays shut while `Orientation` is null. Only the cache key changed.
- `IDirectoryService` changed `GetFilePaths` and gained `Invalidate`/`Move`; the plugin
  calls `GetFiles` and `GetFile`, both unchanged.
- `BaseItem` gained members and changed `GetExtras`; none are used here.
- `MetadataService` now saves the refresh stamp on a full refresh; provider selection,
  and therefore `02` §5b, is unchanged.

## 3. Why a net9.0 binary built against 10.11.6 loads on 12

1. **`targetAbi` is a floor, never a ceiling.**
   `Emby.Server.Implementations/Updates/InstallationManager.cs:GetPackages` drops a
   version only when its `targetAbi` is *above* the server, and
   `PluginManager.cs:LoadManifest` loads a plugin when `_appVersion >= targetAbi`.
   10.11.6.0 ≤ 12.4.0.0, so 1.0.0.0 was being offered to 12 servers before anyone
   checked. There is no "maximum ABI" field to declare.
2. **The host's assemblies satisfy the old references.**
   `PluginLoadContext.Load` resolves only what sits in the plugin's own folder and
   returns null for everything else, so `MediaBrowser.Controller` 12.4.0.0 from the host
   answers a reference to 10.11.6.0 — .NET Core binds to a higher version.
3. **.NET 10 runs a net9.0 assembly.**
4. **Every member the plugin references exists with the same signature** (§2).

Point 4 is what a compiler cannot prove for the shipped binary. A parameter that gains a
default value — 12.4 did exactly that to `BaseItem.GetExtras` — is source-compatible and
binary-breaking: the call recompiles fine and the old dll throws
`MissingMethodException`. Only running the old binary on the new server settles it,
hence §4.

## 4. The live test

**Setup.** A second container beside production on the photo host, which stayed on
10.11.6 and was never restarted:

- image `jellyfin/jellyfin:12.4` pulled **by digest**
  (`sha256:fea863d0…`, identical on Docker Hub and ghcr.io), never by tag;
- published on `127.0.0.1` only, with CPU capped at 3 of 4 cores so production
  playback keeps a core;
- **fresh** config and cache — never production's, since 12 migrates the database one
  way;
- the photo library mounted read-only, as a `homevideos` library with photos enabled,
  which is what production uses.

The plugin was installed the way a user would install it: the catalogue URL added under
Repositories, then **the published 1.0.0.0 zip installed from it unmodified**. That is
the CI-built dll (MD5 `b5bb0291…`), not a local build.

| Check | Result on 12.4 |
|---|---|
| Catalogue lists 1.0.0.0 (`targetAbi` 10.11.6.0) and installs it | yes; MD5 checksum accepted |
| Plugin state after restart | **Active** — `Loaded assembly`, `Image encoder decorated: "HeicForJelly over Skia"`, `Loaded plugin`, 0 errors |
| ffmpeg version gate | `HEIC decoding enabled, using ffmpeg "8.1.3"` |
| `Photo` items for HEIC files | **884 / 884** |
| Stored width × height vs production | 883 / 884 equal; the 884th is the file only ffmpeg 8 reads (§5): 0x0 on production, 1547x1589 here |
| Date, created, year + the 11 EXIF fields, vs production | **884 / 884 equal, every field** |
| Items with `Orientation` written | 0, as `02` §5a requires |
| Cold thumbnails (`maxWidth=396`) | **884 / 884** at the right size; median 0.64 s, p90 0.83 s, max 3.18 s, 9.9 min in all |
| Cached re-request | median 0.014 s |
| Full size: 40 random + 25 odd-dimension + 3 largest | **68 / 68** at exactly the stored dimensions; median 0.90 s, max 5.17 s |
| Corrupt corpus (8 files, rebuilt from J4's recipe) | handled exactly as in J4: empty and random rejected before any process starts; 24 B / 100 B / 2 KB die in ffprobe; 300 KB / half / broken body die in ffmpeg; the original bytes are served; no hang, no exception |
| Temporary files left in `/tmp/jellyfin` | **0** |
| Plugin warnings or errors across the 884 | **0** (the only ones logged are the expected ones for the corrupt corpus) |

The 25 odd-dimension files — 11 in J4, more since the library grew — all came out exact,
so `crop=…:exact=1` behaves the same under ffmpeg 8.

Timings are not a like-for-like comparison with J4/J5 (a fresh instance with nothing else
to do, against a production server), so no speed-up is claimed. The first scan of the
whole library took 56 min: a fresh database fetches metadata for all 7 507 photos and 515
videos on three cores, and the rate falls as the database grows — 14 photos/s through
2013, under 2/s by 2021 — **in years with no HEIC at all**, so the plugin is not the
cause. At equal size the rates match: 12.4 took about 2.7 min for its first ~1 500 photos,
10.11.6 2.4 min for the 1 529 of the subset below. A rescan of a known library is the
1-minute figure of J2.
The 178 errors in that scan's log all come from `Emby.Photos.PhotoProvider` on 177 PNG
files and one WebP — the same TagLib failures production logs, none on a HEIC.

### Backward compatibility, same binary on 10.11.6

Production has run since J5 on a **local** build: the same source, but not the zip users
download (MD5 `1ea1554c…` against the CI's `b5bb0291…`). So the published artifact had
never run on 10.11 at all. It was checked without touching production: a third container
from **production's own image** (10.11.6, ffmpeg 7.1.3, net9.0), same fresh-config setup,
the zip installed from the catalogue, on a subset chosen for coverage — 2022 (the oldest
HEIC year), 2025, and the new 2026 layout, including the file ffmpeg 7 cannot open.

| Check | Result on 10.11.6 |
|---|---|
| Plugin state | **Active**, encoder decorated, `HEIC decoding enabled, using ffmpeg "7.1.3"` |
| `Photo` items | **423 / 423** |
| Dimensions, dates, year + 11 EXIF fields vs production | **423 / 423 equal, every field** |
| Cold thumbnails | 422 / 423; the 423rd is the ffmpeg-7-unreadable file, served raw, exactly as production serves it; median 0.86 s, p90 1.06 s, max 3.09 s |
| Full size: 40 random + 13 odd-dimension + 3 largest | **53 / 53** exact; median 2.43 s, max 10.15 s |
| Corrupt corpus | identical behaviour to 12.4 and J4 |
| Temporary files left | **0** |
| Plugin warnings | 3, all about the ffmpeg-7-unreadable file |

Both test instances ran on the same host with the same CPU cap, so their render times can
be compared: **12.4 was faster** — thumbnails 0.64 s against 0.86 s median, full size
0.90 s against 2.43 s. The cause was not isolated; ffmpeg 8 and the SkiaSharp 3 encoder
both changed.

Changing the csproj did not change the shipped binary either: the default build of the
modified project has the same MD5, `1ea1554c…`, as the dll production runs.

## 5. ffmpeg 8

The decoder builds its filtergraph from `ffprobe -show_stream_groups -of json`, so a
renamed field would silently send every HEIC back to the undecoded fallback. The exact
command the plugin runs was executed under both versions on every `.HEIC` in the
library — 884 files by then, against 575 in September (§7):

| | ffprobe 7.1.3 (prod) | ffprobe 8.1.3 (12.4 image) |
|---|---|---|
| Tile grid reported | 883 | **884** |
| Fields the plugin reads (`stream_groups`, `disposition`, `components`, `subcomponents`, `stream_index`, tile offsets, `width`/`height`, `streams[].index`) | — | **identical on all 883** |
| Fields removed per stream | — | `closed_captions`, `refs`, `film_grain` (unused) |

The one extra file is `2026/2026-04/20260409-154906_iphone14-3_….heic`: ffmpeg 7 fails
with `error reading header`, ffmpeg 8 reads a 16-tile 1547x1589 grid. It is dimensionless
on 10.11 and renders on 12 — a gain, not a regression.

## 6. Releasing, and when this stops being enough

- **The release is unchanged.** One zip, built against 10.11.6 on net9.0, `targetAbi`
  10.11.6.0 — the version it is compiled against, per the rule in `CLAUDE.md`. "Tested
  on 12.4" goes in the README and the changelog, because the manifest cannot carry it.
- **`.github/workflows/compat.yml`** compiles the same source against
  `Jellyfin.Controller` 12.4.0 on net10.0 at every push. It catches source breaks only,
  for the reason in §3. The csproj takes `JellyfinVersion` and `JellyfinTargetFramework`
  as a pair; `TargetFramework` itself cannot be overridden, because NuGet restore drops
  that global property and fails NU1202.
- **Bump the 12.x row deliberately**, after scanning the new packages. The 12.4.0 graph
  was scanned on 2026-11-20: every `Jellyfin.*` package is owned by `Jellyfin` on
  nuget.org, repository-signed, `lib/net10.0` only, with no build-time code; the only
  MSBuild targets and analyzers in the graph are Microsoft's (TFM-support warnings and
  source-generator wiring).
- **Switch to two builds only if** a 12.x breaks the old binary — `MissingMethodException`,
  `TypeLoadException`, or the plugin shown `Malfunctioned` — while 10.11 still matters.
  The manifest then needs two entries with **distinct** version numbers and the 12 build
  numbered higher: `InstallationManager` sorts candidates by `VersionNumber`, installs
  into `<name>_<version>`, and looks plugins up by id and version, so two entries sharing
  a version collide. A server on 10.11 drops the 12 entry by its `targetAbi`.
- **When 10.11 is dropped**, flip the csproj defaults to 12.x/net10.0 and raise
  `targetAbi` with them.

## 7. Noticed along the way

- **Docker Hub's `jellyfin/jellyfin:latest` has pointed at 12.4 since 2026-10-26.** A
  server running from `latest` jumps to 12 at the next pull and recreate, and the
  database migration is one-way. Pin a tag and upgrade on purpose.
- **The library grew from 575 to 884 HEIC**, 290 of them in a new `2026/2026-MM/` layout
  with a different naming scheme. ffmpeg reads a tile grid in all of them, so no
  mislabelled JPEG came with this batch.
- **A photo library is `collectionType=homevideos` with `EnablePhotos`.** `photos` is not
  a value of `MediaBrowser.Model/Entities/CollectionTypeOptions.cs` in either version;
  passing it to `POST /Library/VirtualFolders` silently creates a mixed library that holds
  not a single `Photo` item. That cost one scan here.
- **The catalogue installs into `HEIC for Jelly_<version>/`** (the package name), while
  `tools/build.sh` deploys into `HeicForJelly_<version>/`. Both load; never keep both.
- **Rootless podman:** `podman restart` on a container published on `127.0.0.1` failed to
  rebind the port through pasta (`Address already in use`); a plain `podman start`
  afterwards worked.
