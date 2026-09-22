# Jellyfin internals — what was actually verified

All of it read from the **`v10.11.0`** tag (and `master` where noted), 2026-09-20.
Citations are `file:symbol`. Re-verify against the tag the plugin ends up targeting.

## 1. Why HEIC is invisible

`Emby.Server.Implementations/Library/Resolvers/PhotoResolver.cs:IsImageFile` gates on
`IImageProcessor.SupportedInputFormats`. `src/Jellyfin.Drawing/ImageProcessor.cs:SupportedInputFormats`
is a **hardcoded** set:

```
tiff tif jpeg jpg png aiff cr2 crw nef orf pef arw webp gif bmp erf raf rw2 nrw dng
ico astc ktx pkm wbmp avif
```

No `heic`, no `heif` — neither in `v10.11.0` nor on `master` as of 2026-09-20. The
resolver returns `null`; **no `Photo` item is created at all.**

Upstream: one open issue, jellyfin#17312 (last activity 2026-08-23). Nothing merged.

## 2. Why "just add the extension" is a trap

Two independent format lists exist:

| List | Used for |
|---|---|
| `ImageProcessor.SupportedInputFormats` | whether the file becomes an item |
| `src/Jellyfin.Drawing.Skia/SkiaEncoder.cs:SupportedInputFormats` | whether pixels can be read |

Skia's list has no heic/heif either. `SkiaEncoder.EncodeImage` **returns `inputPath`
unchanged** when the format is unknown, and `ImageProcessor.ProcessImage` then serves
the original file as-is with the extension's mime type. So patching only the resolver
list yields: items appear, and every image request returns the **full-resolution
original**, with no resize and no thumbnail. That is the same failure mode as AVIF
and TIFF, which sit in the resolver list but not in Skia's (jellyfin#9364).

**JPEG is the only format that is safe end to end.**

## 3. The extension points that make a plugin possible

- `Emby.Server.Implementations/ApplicationHost.cs:RegisterServices` runs the core
  registrations, then `_pluginManager.RegisterServices(serviceCollection)` — plugins
  register **after** the core, and Microsoft DI resolves the **last** registration of
  a service type. A plugin can therefore replace a core singleton.
- `ApplicationHost` calls `GetExports<IItemResolver>()`, which scans plugin
  assemblies. A plugin can add its own resolver **without touching any format list**.
  Those exports are built with `ApplicationHost.cs:CreateInstanceSafe`, which calls
  `ActivatorUtilities.CreateInstance` — so **constructor injection works** in a plugin
  resolver, and `NamingOptions` and `IDirectoryService` are both registered singletons
  (`ApplicationHost.cs:RegisterServices`). A constructor that throws does not crash
  the scan: it is caught, logged, and the whole **plugin is failed**.
- Nothing filters files by extension before the resolvers run.
  `Emby.Server.Implementations/Library/IgnorePatterns.cs` is a deny-list of names and
  directories (`@eaDir`, `sample.*`, `**/metadata/**`), not an allow-list of formats,
  so `.heic` paths do reach a resolver. Verified at `v10.11.6`.
- `Jellyfin.Server/CoreAppHost.cs` registers `IImageEncoder` **by interface**
  (`AddSingleton(typeof(IImageEncoder), imageEncoderType)`, Skia or `NullImageEncoder`).
  Nothing in the core resolves `SkiaEncoder` concretely, so the interface can be
  swapped. `SkiaEncoder` is **not `sealed`**.
- `ImageProcessor` **is `sealed`**, and its format list is a class constant — so
  replacing `IImageProcessor` would mean reimplementing it wholesale. Do not.

**Load-bearing consequence:** the way in is *a resolver of our own* plus *a decorated
`IImageEncoder`*. Not a patched format list, not a replaced `IImageProcessor`, not an
HTTP middleware.

## 4. `IImageEncoder` is small

**Eleven** members at v10.11.6, not ten — `CreateTrickplayTile` was added and an
implementation will not compile without it: `SupportedInputFormats`,
`SupportedOutputFormats`, `Name`, `SupportsImageCollageCreation`,
`SupportsImageEncoding`, `GetImageSize`, `GetImageBlurHash`, `EncodeImage`,
`CreateImageCollage`, `CreateSplashscreen`, `CreateTrickplayTile`.

A decorator intercepts `GetImageSize` and `EncodeImage` (plus `GetImageBlurHash` if
blurhashes are wanted) and delegates the rest.

**`SupportedInputFormats` is not one of them.** Nothing in the server reads
`IImageEncoder.SupportedInputFormats`: `ImageProcessor.cs:SupportedInputFormats` is its
own hardcoded `HashSet` and never consults the encoder, and no other call site exists.
Overriding it in a decorator is documentation, not mechanism — which is precisely why
the plugin needs a resolver as well. Verified at v10.11.6.

### Wiring the decorator

`CoreAppHost.cs:RegisterServices` registers the encoder **by implementation type**
(`AddSingleton(typeof(IImageEncoder), imageEncoderType)`) and *then* calls
`base.RegisterServices`, whose last statement is
`ApplicationHost.cs:RegisterServices` → `_pluginManager.RegisterServices`. So a plugin's
`IPluginServiceRegistrator` sees the original `ServiceDescriptor` and can remove it,
rebuild the inner encoder from `descriptor.ImplementationType` via
`ActivatorUtilities.CreateInstance`, and register a wrapper in its place.
`IPluginServiceRegistrator` is instantiated with `Activator.CreateInstance`, so it
**requires a parameterless constructor**; a throw there marks the plugin
`Malfunctioned`.

### Two traps in the encode path

- `MediaEncoder.cs:EncoderVersion` returns a field that is **`null`** until
  `SetFFmpegPath` runs during startup validation. Any version gate must be evaluated
  lazily, on first use, not in a constructor.
- `ImageProcessor.cs:ProcessImage` returns the **original file untouched** when
  `options.HasDefaultOptions(...)` holds and auto-orientation is not required — before
  `EncodeImage` is ever called. A client asking for an unresized image therefore still
  receives raw HEIC. Decoding alone does not close that hole.

Decorating here means Jellyfin's own machinery keeps applying: the resized-image
cache (`ImageProcessor.ResizedImageCachePath`), the encoding concurrency limit
(`ParallelImageEncodingLimit`), ETags, conditional GETs, auth and per-library ACL.

## 5. Metadata: the half that is free and the half that is not

`Emby.Photos/PhotoProvider.cs:FetchAsync`:

- calls `item.SetImagePath(ImageType.Primary, item.Path)` **before** any extension
  check, and
- probes `item.Width`/`item.Height` through `IImageProcessor.GetImageDimensions`,
  catching `ArgumentException` for unsupported formats.

**So the primary image and the dimensions come for free** once the encoder can read
HEIC. No `ILocalImageProvider` is needed (the prior art added one only because it had
no encoder).

But the EXIF block is gated on `PhotoProvider._includeExtensions`:

```
.jpg .jpeg .png .tiff .cr2 .webp .avif
```

HEIC is not there, so **aperture, shutter, ISO, focal length, GPS, orientation and —
critically — `PremiereDate` are never set.** That is why the plugin needs its own
`ICustomMetadataProvider<Photo>`. Not cosmetic: jellypic sorts its whole timeline on
`PremiereDate`, so without it every HEIC lands in the undated bucket.

`PremiereDate` must be stored **unconverted**. `Emby.Photos/PhotoProvider.cs` sets
`DateCreated = dateTaken.ToUniversalTime()` and `PremiereDate = dateTaken` raw,
precisely because EXIF `DateTime` carries no timezone and `ToUniversalTime()` on a
`Kind == Unspecified` value assumes the *server's* local zone — which makes
`DateCreated` drift with the container's `TZ`. Mirror that behaviour exactly.

The same argument applies to `OffsetTimeOriginal`, the EXIF tag that *does* carry a
zone. `PhotoProvider` ignores it, so the plugin ignores it too — honouring it would
place a HEIC and a JPEG shot the same afternoon hours apart in the same timeline.

`Aperture` and `ShutterSpeed` are stored as the **raw APEX values**, not f-numbers.
`PhotoProvider` reads `ExifEntryTag.ApertureValue` and divides the rational without
converting, so a JPEG in this library shot at f/1.5 is recorded as `1.17`. Converting
in the plugin would make HEIC and JPEG items disagree about the same lens.

### 5a. `Orientation` must be left null — verified 2026-09-22

The one field of `PhotoProvider`'s set the plugin deliberately does **not** fill.
Two independent reasons, both read from source:

- `MediaBrowser.Controller/Entities/Photo.cs:GetDefaultPrimaryImageAspectRatio`
  swaps `Width` and `Height` whenever `Orientation` is one of the four quarter-turn
  values. Our stored dimensions are **already** the rotated ones — the encoder applies
  `irot` during the decode — so writing the file's real orientation swaps a pair that
  is already correct, and every portrait photo gets a landscape aspect ratio.
- Writing `TopLeft` to dodge that is worse.
  `src/Jellyfin.Drawing/ImageProcessor.cs:ProcessImage` reads a null orientation as
  *"unknown, auto-orient anyway"* and `TopLeft` as *"nothing to do"*; that second
  branch is what lets it return the original file without ever calling `EncodeImage`
  — i.e. hand the client raw HEIC.

Null is the value that means both "already upright" and "keep going through the
encoder". It is a behaviour, not an omission.

### 5b. A scan does not re-run providers — verified 2026-09-22

`MediaBrowser.Providers/Manager/MetadataService.cs:GetProviders` computes

```
runAllProviders = options.ReplaceAllMetadata
               || metadataRefreshMode == FullRefresh
               || (isFirstRefresh && …)
               || (requiresRefresh && …)
```

and otherwise consults `IHasItemChangeMonitor.HasChanged`. Since `HasChanged`
compares `LastWriteTimeUtc`, an ordinary library scan will **not** re-read the EXIF of
a file it has already seen. Correct behaviour — and it means that deploying a new
metadata provider over an already-scanned library needs an explicit refresh:

```
POST /Items/{libraryId}/Refresh?metadataRefreshMode=FullRefresh&replaceAllMetadata=true
```

`IForcedProvider` does **not** help here. In
`MediaBrowser.Providers/Manager/ProviderManager.cs` it only exempts a provider from
the `item.IsLocked` check; it has no bearing on the `HasChanged` gate.

Note the blast radius: `replaceAllMetadata=true` re-reads **every** item in the
library, not only the new format's. On this library that surfaced 184 pre-existing
`Emby.Photos.PhotoProvider: Error reading image tag` errors on 2017-2018 `.PNG`
files — a core TagLib failure, unrelated to the plugin, but newly visible.

## 6. Guards to copy from `PhotoResolver` (behaviour, not code)

Correction, 2026-09-21: the priority to use is **`ResolverPriority.Plugin`**, not
`First`. `MediaBrowser.Controller/Resolvers/ResolverPriority.cs` defines `Plugin = 0`
and documents it as *"the highest priority, used by plugins to bypass the default
server resolvers"*; `First = 1` is where the server's own resolvers sit.
`ItemResolver<T>.Priority` defaults to `First`, so the property must be overridden —
it is not inherited correctly by omission.

`LibraryManager` sorts by `Priority` and takes the **first non-null**
(`LibraryManager.cs:ResolveItem`), so a plugin resolver runs **before everything, for
every file in every library**. It must bail out instantly on a non-heic extension.
It must also reproduce two rejections that `PhotoResolver.Resolve` performs, or
artwork gets indexed as photos:

- `PhotoResolver._ignoreFiles` — filenames starting with `folder`, `thumb`,
  `landscape`, `fanart`, `backdrop`, `poster`, `cover`, `logo`, `default`.
- `PhotoResolver.IsOwnedByMedia` — an image whose basename prefixes a sibling video
  file belongs to that video.

Also honour the collection-type condition: photos only, or home videos with
`LibraryOptions.EnablePhotos`.

## 7. Decoding without a native dependency

FFmpeg gained **HEIF/AVIF still image support in 7.0** (verified in FFmpeg's
`Changelog`). Jellyfin ships jellyfin-ffmpeg and exposes its path via
`IMediaEncoder.EncoderPath`, so decoding can be delegated out-of-process with zero
native libraries embedded in the plugin.

The claim that *tiled* HEIF "only lands in 8.1" was **wrong, and load-bearing**.
7.1.3 demuxes tile grids and exposes their full geometry as a *stream group*; what it
lacks is a CLI step that assembles them. J0 stitches them with `xstack` instead —
see `04-j0-spike.md`, run 4. Since every file in the target library is tiled, this
correction is the difference between the plugin working and decoding one 512x512
tile per photo.

Caveat: `MediaBrowser.MediaEncoding/Encoder/EncoderValidator.cs:MinVersion` is
**4.4**, so Jellyfin will happily run with an ffmpeg far too old for this. The
plugin must check the version and degrade with an explicit message rather than
produce mysterious failures. **This is what J0 measures.**
