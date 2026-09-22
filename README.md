# HEIC for Jelly

A Jellyfin server plugin that makes HEIC/HEIF photos appear and render in a photo
library, without converting or touching the originals.

## The problem

`PhotoResolver.IsImageFile()` gates on `ImageProcessor.SupportedInputFormats`, which
lists no `heic`/`heif`. The resolver returns `null`, so no `Photo` item is ever
created: iPhone photos are absent from `/Items` entirely, not merely missing a
thumbnail.

## What it does

- Resolves `.heic`/`.heif` into `Photo` items, reproducing `PhotoResolver`'s own
  guards (ignored filenames, collection type, images owned by a sibling video).
- Decodes them by **decorating `IImageEncoder`**, so Jellyfin's auth, per-library
  permissions, resized-image cache, ETags and encoding concurrency limit all keep
  applying. No HTTP endpoint is added.
- Reads EXIF into `PremiereDate`, camera, exposure and GPS, so the timeline sorts.

Decoding runs **out of process**, through the `ffmpeg` the server already ships.
Nothing parses attacker-influenced image data inside the Jellyfin process, and the
plugin carries no native dependency: it is about 50 KB.

## Requirements

- Jellyfin **10.11.6** (the ABI is pinned; any 10.x minor can break a plugin)
- **ffmpeg 7.0+** — the first release that demuxes HEIF still images. Jellyfin itself
  accepts 4.4, so a healthy server can still be too old; the plugin checks once at
  startup and says so in the log.

## Install

Dashboard → Plugins → Repositories → **+**, and add:

```
https://raw.githubusercontent.com/RousseAddict/heic-for-jelly/main/manifest.json
```

The plugin then appears under Catalogue → General. Install it and restart the server.
Updates arrive the same way.

Jellyfin hides any plugin whose `targetAbi` is above the server's own version, so an
empty catalogue means the server is older than the requirement above.

### From source

Build the DLL, then drop it with `meta.json` into
`<config>/plugins/HeicForJelly_<version>/` and restart the server.

`tools/build.sh` does this for a setup where compilation happens on another machine
over ssh. Copy `tools/build.env.example` to `tools/build.env` and fill in your hosts:

```sh
sh tools/build.sh              # sync + build
sh tools/build.sh --deploy     # + install into Jellyfin (never restarts it)
```

## Notes

These files are not ordinary HEICs. Every one in the library this was built against is
an HEVC **tile grid** of 9 to 98 tiles, which no plain `ffmpeg -i` decodes — it returns
a single 512x512 tile and exits 0. The plugin generates an `xstack` filtergraph from
`ffprobe -show_stream_groups`, crops to the declared size, and applies the `irot`
rotation that ffprobe does not expose. Results are asserted against the declared
dimensions, because exit codes prove nothing here.

`docs/` is the working record: verified Jellyfin internals with `file:symbol`
citations, the decoder spike, the prior-art audit, and what each milestone measured.

## Licence

MIT. See [LICENSE](LICENSE).
