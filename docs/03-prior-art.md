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

## 4. Magick.NET — scanned 2026-09-21, **not adopted**

Evaluated as the alternative to the ffmpeg decoder, because libheif handles grid
assembly, `irot` and primary-item selection natively and would delete the most
error-prone code in the plugin. Rejected. Recorded here so it is not re-litigated.

**Security: clean provenance.** Apache-2.0, published by Dirk Lemstra, who is also an
ImageMagick core maintainer. ~29M downloads, frequent releases, packages signed
(Azure Trusted Signing plus nuget.org's repository signature) and built in public
GitHub Actions so a shipped nupkg can be compared against a CI-produced one. No
credential access, no network egress, no telemetry, no install-time scripts —
`install.ps1` only runs under the legacy `packages.config` flow, not `PackageReference`.
Nothing resembling `heicext`'s hole. This is **not** a "compromised library" finding.

**The objection is blast radius, not malice.** Magick.NET decodes **in-process**, so a
memory-safety bug in libheif or libde265 reached through a photo is a compromise of the
Jellyfin server itself. That is not hypothetical: CVE-2026-84383 (CVSS 9.8, heap
out-of-bounds write via a crafted HEIF/HEIC, libheif 1.22.0-1.23.1) was published
2026-09-18, three days before this evaluation, and ImageMagick carries a steady stream
of its own parser CVEs. Patching would mean waiting on a Magick.Native rebuild and then
shipping a new plugin release. The ffmpeg path decodes **out-of-process**, in a
short-lived child, and is patched by jellyfin-ffmpeg in the server image — a different
owner and a different cadence for the same class of bug. ImageMagick's delegate system
(the "ImageTragick" family: MSL, MVG, `url:`, `ephemeral:`) would also need a
restrictive `policy.xml`, a configuration burden ffmpeg does not carry.

**Capability: never settled.** Sources conflict — the maintainer states in discussion
#670 that "heic/heif isn't [supported] due to licensing issues", while `Notice.txt` in
the shipped package credits both **libheif and libde265**. Inspecting the linux-x64
native (`Magick.Native-Q8-x64.dll.so`, 36 MB) found libheif genuinely linked in
(`LIBHEIF_SECURITY_LIMITS`, `Decoder_HEVC`, `Decoder_AVIF`, `Decoder_JPEG`) but no
`de265` and no `HEIC` string anywhere. The binary is stripped, so **that proves nothing
either way** — `Decoder_HEVC` is a libheif wrapper class that exists with or without a
backend. Deliberately not resolved by inference: reasoning from weak signals about what
a decoder does is exactly what sent the first two J0 runs astray.

**Deployability: two failures observed, not predicted.** The probe could not be run at
all. `runtimes/<rid>/native/` is a NuGet + `.deps.json` convention honoured at process
start or by an `AssemblyDependencyResolver`; a plugin folder dropped beside an
already-running Jellyfin participates in neither, and indeed the native was not copied
to the output directory without an explicit RID. Copied by hand, it then failed on the
Monterey build host with `Symbol not found: __libcpp_verbose_abort` — built against a
newer libc++ than the host provides. Both failures are the exact class of fragility the
plugin would inherit inside the container, plus 36 MB of native binary to ship.

**Decision.** ffmpeg, on a ground that does not depend on the unresolved question: it
already works, verified end to end at ~0.6 s/photo inside the real container, out of
process, with nothing to ship. Magick.NET would have to be clearly *better* to be worth
three added costs, and the best case was parity.

**If it is ever reopened**, the one command that settles it is a throwaway
`dotnet/sdk` container on the Jellyfin host — linux-amd64, the real target — printing
`MagickNET.Delegates` and `MagickNET.SupportedFormats`. Anything less is inference. And
a positive result would still need decoded dimensions asserted against EXIF, because
these `MiPr` files hold several image items and a plausible-looking small one can come
back instead of the photo.

## 5. Upstream

jellyfin#17312, "HEIC images are not indexed in Home Videos and Photos library",
open, last activity 2026-08-23. No PR, and `master` still has no heic/heif in either
format list. Assume no upstream relief.
