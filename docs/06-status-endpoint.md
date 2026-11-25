# The status endpoint — `GET /HeicForJelly/Status`

Added in **1.1.0.0**, 2026-11-24. Jellyfin facts read from the **`v10.11.6`** tag and
re-checked on **`v12.4`**. Citations are `file:symbol`.

The plugin's first and only HTTP route. It narrows the "no new HTTP surface" rule of
`CLAUDE.md` by exactly one exception, and this document is the reason that exception is
safe.

## 1. Why it exists

jellypic's onboarding has to tell a user whether HEIC photos will render on their server.
An iPhone uploads HEIC originals by default, and without this plugin those files never
become items at all (`02` §1): a backup that silently shows nothing. Detecting it before
the first upload is the whole point.

A non-administrator had no way to learn it:

- `Jellyfin.Api/Controllers/PluginsController.cs` is `[Authorize(Policy = Policies.RequiresElevation)]`
  at class level, and so are `Jellyfin.Api/Controllers/PackageController.cs` (catalogue,
  repositories) and `DashboardController.GetConfigurationPages`.
- `DashboardController.GetDashboardConfigurationPage` is public, but this plugin has no
  configuration page (`Plugin` does not implement `IHasWebPages`), so there is nothing for
  it to answer.
- The one visible side effect is a HEIC file existing as a `Photo` item, and any user may
  read an item's path (`Emby.Server.Implementations/Dto/DtoService.cs:AttachBasicFields`
  gates `Path` on the requested fields only). But that proof is positive-only: a server
  with no HEIC yet — a new library, or one filled from a phone that shoots JPEG — looks
  identical with and without the plugin, and `/Items` cannot filter by extension.

## 2. Why here, and not in Upload for Jelly

The alternative was for `../upload-for-jelly` to report a `heic` flag in its `Targets`
response, read from `IImageEncoder.SupportedInputFormats`, which this plugin already
extends only when decoding is on. Rejected, for independence:

- **Each plugin must be removable without touching the other.** HEIC is the one Jellyfin
  may well support natively one day (jellyfin#17312). On that day this plugin is deleted,
  and nothing about uploads should have to change.
- `IImageEncoder.SupportedInputFormats` is read by nothing in the server (`02` §4). A
  native implementation would most likely add `heic` to `ImageProcessor`'s own list
  instead, and a flag derived from the encoder's would turn wrong without anyone noticing.

So each plugin reports itself. The cost lands on the client, knowingly: it knows this
plugin by its route, so the day Jellyfin decodes HEIC natively and the plugin is removed,
the route answers 404 and the client needs a server-version rule. That is jellypic's
`docs/20`, not this repository's concern.

## 3. Contract

`GET /HeicForJelly/Status` — no parameters, no body.

```json
{ "decoding": true }
{ "decoding": false, "reason": "FFMPEG_TOO_OLD" }
```

| `reason` | Meaning |
|---|---|
| absent | decoding is on |
| `FFMPEG_TOO_OLD` | the server's ffmpeg predates 7.0, the first to demux HEIF stills |
| `FFMPEG_MISSING` | the server reports no ffmpeg version (§5 says when that happens) |
| `ENCODER_NOT_DECORATED` | the image encoder the server resolves is not this plugin's (§7) |

| Status | Meaning |
|---|---|
| 200 | the body above |
| 401 | no token, or one the server does not accept |
| 403 | a valid user refused by the default policy: remote access disabled for them while remote, or outside their parental schedule |
| 404 | **not installed — or 1.0.x**, which has no route. A client must word it "install or update" |
| 503 | the server is still starting (§5) |

- **Property names are pinned lowercase** with `[JsonPropertyName]`. Jellyfin answers a
  plain `application/json` with its PascalCase formatter; Upload for Jelly hit exactly
  that, and a client decoding `decoding` would have read nothing.
- **`reason` is omitted, not null, when decoding is on**, by an explicit
  `JsonIgnore(WhenWritingNull)` rather than by relying on the server's serializer defaults.
- **Neither the ffmpeg version nor its path is ever sent.** The server log already gives
  both to the administrator (`HeicImageEncoder.CheckFfmpeg`); a non-administrator has no
  use for them, and they describe the host.
- **An unknown `reason` must read as "off"**, never as success. The set may grow.
- The answer comes from memory, with no I/O and no ffmpeg run, so calling it is free.

## 4. Who can call it — verified

The action carries a plain `[Authorize]`, so it gets the default policy.

- `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:AddJellyfinApiAuthorization`
  sets `options.DefaultPolicy` to the `CustomAuthentication` scheme plus a
  `DefaultAuthorizationRequirement`. No `FallbackPolicy` is set anywhere.
- `Jellyfin.Api/Auth/CustomAuthenticationHandler.cs:HandleAuthenticateAsync` returns
  `NoResult` for a request without a token.
- `Jellyfin.Api/Auth/DefaultAuthorizationPolicy/DefaultAuthorizationHandler.cs:HandleRequirementAsync`
  then sees neither an API key nor a user id and returns **without succeeding**, so the
  requirement is unmet and an anonymous caller is challenged: **401**.
- An API key succeeds unconditionally. A user succeeds unless they are remote without
  `EnableRemoteAccess`, or outside their parental schedule — both `Fail`, so **403**.

The route is mounted because `Emby.Server.Implementations/ApplicationHost.cs:GetApiPluginAssemblies`
returns every assembly holding a `ControllerBase`, and `Jellyfin.Server/Startup.cs:ConfigureServices`
passes them to `AddJellyfinApi`. There is no prefix: `[Route("HeicForJelly")]` is the path.

On v12.4, `DefaultAuthorizationHandler.cs` is byte-identical and the default policy is
built the same way.

## 5. The timing trap — verified, and closed by the server

`HeicImageEncoder.CheckFfmpeg` runs once, lazily, and caches what it sees. It reads
`MediaBrowser.MediaEncoding/Encoder/MediaEncoder.cs:EncoderVersion`, which stays null until
`MediaEncoder.SetFFmpegPath` runs. If the first evaluation came too early, decoding would
be frozen off for the life of the process. A status route is a new way to trigger that
first evaluation, so the order had to be checked.

**Kestrel does accept requests before ffmpeg is validated.**
`Jellyfin.Server/Program.cs:StartServer` calls `appHost.InitializeServices`, then
`_jellyfinHost.StartAsync()`, and only then `appHost.RunStartupTasksAsync()` —
`Emby.Server.Implementations/ApplicationHost.cs:RunStartupTasksAsync` is where
`SetFFmpegPath()` runs, immediately followed by `CoreStartupHasCompleted = true`.

**No controller action runs in that window.**
`Jellyfin.Api/Middleware/ServerStartupMessageMiddleware.cs:Invoke` answers **503** to every
request except `/system/ping` until `CoreStartupHasCompleted`, and `Jellyfin.Server/Startup.cs:Configure`
installs it (`UseServerStartupMessage`) before `UseEndpoints`. So a status call cannot be
what freezes decoding. Identical on v12.4: the middleware file is byte-identical and the
order in `Program.cs`, `ApplicationHost.cs` and `Startup.cs` is unchanged.

One detail of that window: `UseAuthorization` comes before the startup middleware, so an
anonymous caller gets 401 there while a signed-in one gets 503. A client should treat 503
as "unknown", never as "not installed".

**`FFMPEG_MISSING` is still reachable on a running server**, because a stock server that
finds no valid ffmpeg refuses to start (`RunStartupTasksAsync` throws `FfmpegException`) but
two paths get past that with a null version:

- `MediaEncoder.SetFFmpegPath` returns `true` without validating anything when
  `FFmpeg:novalidation` is set.
- `MediaBrowser.MediaEncoding/Encoder/EncoderValidator.cs:GetFFmpegVersion` returns null for
  a version string it cannot parse.

A non-HTTP path evaluating the decision before validation would land there too. The route
reports it either way, because it reports the decision itself (§7).

## 6. Why this does not reopen the prior-art hole

`03` §2 disqualified `heicext` for an action that was **anonymous**, took an **absolute
path** from the query string, **decoded and served** that file, and so bypassed the
per-library ACL. This route:

- is authenticated by the default policy (§4);
- takes **no input** at all — no query, no route value, no body;
- reads **one value already in memory**;
- serves **no file**, touches **no path**, decodes nothing, does no I/O.

Images still go through Jellyfin's own pipeline only, with its auth, ACL, cache and ETags.
That is why `CLAUDE.md` now forbids any HTTP surface that takes input or serves content,
with this route as the single exception — not any HTTP surface at all.

What it does disclose, to a signed-in user, is whether the server's ffmpeg is at least 7.0.
That is less than the version Jellyfin already publishes anonymously at
`/System/Info/Public`.

## 7. One source of truth

The route reports `HeicImageEncoder.DecodingState`, which is the same `Lazy` that gates
`GetImageSize` and `EncodeImage`. There is no second ffmpeg check that could drift from it:
`CheckFfmpeg` now returns a `HeicDecodingState` instead of a `bool`, and the encoder's own
gate reads `DecodingState == Enabled`. The log lines are unchanged.

The controller resolves `IImageEncoder` from the server's container — the decorated
singleton whenever `PluginServiceRegistrator` took effect — and pattern-matches it. If it
is not a `HeicImageEncoder`, the answer is `ENCODER_NOT_DECORATED`:

- when `PluginServiceRegistrator` declined to decorate (the original registration had no
  implementation type), HEIC truly does not decode, so this is accurate;
- if another plugin ever decorated the encoder *after* this one, HEIC might still decode
  through the chain and the answer would be a false negative. Accepted: no such plugin is
  known, and a conservative "off" beats a confident wrong "on".

No new DI registration was needed: the encoder already is the one shared singleton.

## 8. Checking it after deployment

With a user token `$TOKEN` (any signed-in user):

```sh
H='Authorization: MediaBrowser Token="'"$TOKEN"'", Client="curl", Device="curl", DeviceId="curl", Version="1"'

curl -s -H "$H" "$SERVER/HeicForJelly/Status"                          # 200 {"decoding":true}
curl -s -o /dev/null -w '%{http_code}\n' "$SERVER/HeicForJelly/Status" # 401, anonymous
```

On a server still running 1.0.x, the first command answers **404**. The server log at
startup still carries the line the answer comes from:
`HEIC decoding enabled, using ffmpeg "…" at …`.
