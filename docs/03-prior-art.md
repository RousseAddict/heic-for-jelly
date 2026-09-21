# Prior art

Surveyed 2026-09-20. Nothing usable was found; the value here is the list of traps.

## 1. Official plugin repo — nothing

`https://repo.jellyfin.org/files/plugin/manifest.json` lists 36 plugins. None touches
image codecs; the categories are metadata scrapers, LiveTV, subtitles, admin tools.
There is no image-codec extension point advertised by the project.

## 2. `harikrishnan606/heicext` — do not install

"Jellyfin plugin for heic support", 0 stars, 0 forks, **no licence file**, created
2026-06-02.

Its architecture: `HeicItemResolver` (IItemResolver) + `HeicLocalImageProvider` +
`HeicConversionService` (Magick.NET) + an ASP.NET middleware that rewrites
`/Items/{id}/Images/Primary` and `/Items/{id}/Download` onto its own `/heic/convert`
endpoint.

### The disqualifier

`HeicApiController.ConvertHeic` is decorated **`[AllowAnonymous]`** (the class carries
`[Authorize]`, the action overrides it) and takes `[FromQuery] string path` as an
absolute filesystem path, validated only on its extension. No containment to library
roots, no item lookup, no user permission check.

Consequences on a running server:

- **Unauthenticated arbitrary file read** of any `.heic`/`.heif` on the host,
  returned as JPEG. It never consults the library, so it **bypasses Jellyfin's
  per-library ACL entirely** — a private photo library is readable by anyone who can
  reach the port.
- **Unauthenticated resource exhaustion**: each distinct `(path, quality, maxWidth,
  maxHeight)` tuple triggers a fresh ImageMagick decode and writes a new file. No
  rate limit, no cache bound, no eviction.
- **Path disclosure** in the 404 body (`File not found: {path}`).
- Converted private photos land in a shared temp dir (`/tmp/JellyfinHeicCache`) under
  predictable names, readable by any local user on the host.

No malware was found — no exfiltration, no obfuscation, no shell-out. The verdict is
*insecure and unmaintained*, not *hostile*.

### Other reasons it is a dead end

- Targets ABI **10.9.11** (`Jellyfin.Controller` 10.9.11). Frozen there since.
- Commits ~160 MB of **prebuilt binaries**, including native `.so`/`.dylib`/`.dl_`
  files, into the repo. Never install its released artifact.
- `<NoWarn>NU1901;NU1902;NU1903</NoWarn>` silences NuGet vulnerability warnings for
  ImageMagick — the very component that parses untrusted files **inside** the Jellyfin
  process.
- Its middleware runs on **every** HTTP request and does a `GetItemById` for every
  `/Items/.../Images/Primary` — i.e. on the hot path of a 20k-thumbnail grid.
- Renames native DLLs to `.dl_` in a post-build step to stop Jellyfin's plugin scanner
  from loading and crashing on them.
- Queries `ILibraryManager.GetItemList` **through reflection** rather than the typed
  API. Fragility, not malice.
- Its resolver sets `DateCreated` from the file's creation time and provides no EXIF,
  so `PremiereDate` is never set. See `02-jellyfin-internals.md` §5 for why that alone
  would break jellypic's timeline.

### What it teaches

The middleware exists only because the plugin has no decoder in the image pipeline.
Decorating `IImageEncoder` instead removes the need for an endpoint, and with it the
whole class of vulnerability — **by construction, not by patch**.

## 3. `great-horn/jellyfin-plugin-immich-albums`

Syncs Immich albums into Jellyfin via symlinks, converting HEIC to JPEG on the way.
That is the transcode-the-library approach wearing a plugin costume, and it requires
Immich alongside. Not applicable, but a clean reminder that the transcode route stays
the cheap fallback if the plugin proves unmaintainable.

## 4. Upstream

jellyfin#17312, "HEIC images are not indexed in Home Videos and Photos library",
open, last activity 2026-08-23. No PR, and `master` still has no heic/heif in either
format list. Assume no upstream relief.
