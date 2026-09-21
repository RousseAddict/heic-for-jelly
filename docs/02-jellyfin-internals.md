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

Ten members: `SupportedInputFormats`, `SupportedOutputFormats`, `Name`,
`SupportsImageCollageCreation`, `SupportsImageEncoding`, `GetImageSize`,
`GetImageBlurHash`, `EncodeImage`, `CreateImageCollage`, `CreateSplashscreen`.

A decorator intercepts three (`SupportedInputFormats`, `GetImageSize`, `EncodeImage`,
plus `GetImageBlurHash` if blurhashes are wanted) and delegates the rest to Skia.

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

## 6. Guards to copy from `PhotoResolver` (behaviour, not code)

A plugin resolver runs at `ResolverPriority.First`, i.e. **before everything, for
every file**, so it must bail out instantly on a non-heic extension. It must also
reproduce two rejections that `PhotoResolver.Resolve` performs, or artwork gets
indexed as photos:

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
