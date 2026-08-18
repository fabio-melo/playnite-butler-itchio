# Butler (Itch.io) — Playnite Library Extension

A [Playnite](https://playnite.link/) library plugin that imports your
[itch.io](https://itch.io/) library and installs, updates, and launches games
through **[butler](https://itch.io/docs/butler/)**, itch.io's official
command-line tool.

Downloads run through the
[Unified Download Manager](https://playnite.link/addons.html) (UDM), so itch.io
installs share the same queue, progress UI, and concurrency limits as your other
Playnite libraries.

## Features

- Imports your itch.io library into Playnite
- Installs and updates games via butler (delta patching, resumable downloads)
- Butler is fetched automatically at runtime — no manual setup of the CLI
- Integrates with the Unified Download Manager download queue
- Works in Desktop and Fullscreen mode

## Requirements

- **Playnite 10+** (built against PlayniteSDK 6.16)
- **[Unified Download Manager](https://github.com/hawkeye116477/playnite-unifiedDownloadManager-plugin)**
  extension by [hawkeye116477](https://github.com/hawkeye116477), installed in
  Playnite. This plugin builds against its public API
  (`UnifiedDownloadManagerApi.dll`) — see [Building](#building).
- An itch.io account
- Windows (butler is downloaded per-platform; only Windows is tested)

`butler.exe` itself is **not** bundled. It is resolved at runtime in this order:
a copy previously downloaded into the extension's data folder, the copy shipped
with the itch.io desktop app if present, or a fresh download from
[broth.itch.zone](https://broth.itch.zone/butler) (itch.io's own distribution
channel).

## Building

### Prerequisites

- Windows with the **.NET SDK** (any recent version — it targets `net462`,
  whose reference assemblies ship with the SDK / Visual Studio Build Tools).
- **Playnite** installed (only needed to package a `.pext`, and to obtain the
  DLL below).

### 1. Supply the UDM assembly

This repository does **not** redistribute third-party assemblies. Before
building you must supply `UnifiedDownloadManagerApi.dll`:

1. Install the **Unified Download Manager** extension in Playnite.
2. Locate its folder under
   `%APPDATA%\Playnite\Extensions\<UDM-extension-id>\`.
3. Copy `UnifiedDownloadManagerApi.dll` from there into `lib/` in this repo.

### 2. Compile

```bash
dotnet build -c Release
```

The output lands in `bin/Release/net462/`. `PlayniteSDK` and `Newtonsoft.Json`
are restored from NuGet automatically.

### 3. Package a `.pext` (optional)

A `.pext` is the installable package. Use Playnite's own **Toolbox** to build
it — it validates the manifest and names the file
`<AddonId>_<version>.pext`:

```pwsh
# Toolbox.exe lives in your Playnite install folder
& "$env:LOCALAPPDATA\Playnite\Toolbox.exe" pack "bin\Release\net462" "dist"
```

This writes `dist\Butler_Itchio_0_1_0.pext`. CI does exactly this on every push
(see [Continuous integration](#continuous-integration)), so packaging by hand is
only needed for local testing.

## Installing (from source)

Either double-click the `.pext` (Playnite installs it), or, for a dev loop:

1. Build in Release.
2. Copy the contents of `bin/Release/net462/` into a new folder under
   `%APPDATA%\Playnite\Extensions\`.
3. Restart Playnite and sign in to itch.io from the extension settings.

## Continuous integration

[`.github/workflows/build-pext.yml`](.github/workflows/build-pext.yml) builds the
extension and packages a `.pext` with Playnite's Toolbox on every push to `main`,
on pull requests, and on demand (`workflow_dispatch`). The packaged `.pext` is
uploaded as a build artifact; pushing a `v*` tag also attaches it to a GitHub
Release.

Because the repo does not redistribute `UnifiedDownloadManagerApi.dll`, CI reads it
from a repository secret:

1. Base64-encode your local copy:
   ```pwsh
   [Convert]::ToBase64String([IO.File]::ReadAllBytes("lib/UnifiedDownloadManagerApi.dll")) | Set-Clipboard
   ```
2. Add it under **Settings → Secrets and variables → Actions** as
   `UDM_API_DLL_BASE64`.

Without the secret (e.g. on forks) the build step is skipped with a notice rather
than failing.

### Releasing

1. Bump `Version` in [`extension.yaml`](extension.yaml).
2. Add a matching entry to [`installer.yaml`](installer.yaml) — the manifest the
   Playnite add-on database reads. Its `PackageUrl` must point at the release
   asset, e.g.
   `.../releases/download/v<version>/Butler_Itchio_<version-with-underscores>.pext`.
3. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`. CI builds the
   `.pext` and attaches it to the release automatically.

`installer.yaml` (`AddonId: Butler_Itchio`) is what lists this extension in
Playnite's built-in add-on browser once submitted to the
[Playnite add-on database](https://github.com/JosefNemec/PlayniteAddonDatabase).

## Project layout

| Path | Purpose |
|------|---------|
| `Butler/` | butler binary resolution, JSON-RPC daemon client |
| `Controllers/` | Playnite install/uninstall/play controllers |
| `Services/` | install, update, migration, headless-install services |
| `Udm/` | Unified Download Manager integration |
| `Views/` | WPF settings + install UI |
| `extension.yaml` | Playnite extension manifest |
| `installer.yaml` | add-on database manifest (versions + download URLs) |

## Credits

- **[Unified Download Manager](https://github.com/hawkeye116477/playnite-unifiedDownloadManager-plugin)**
  by [hawkeye116477](https://github.com/hawkeye116477) — this extension plugs
  into UDM's download queue through its public API. The CI packaging approach
  (Playnite Toolbox + an `installer.yaml` add-on manifest) also follows the
  pattern from hawkeye116477's Playnite plugins.

## License

[MIT](LICENSE)

butler and itch.io are trademarks of their respective owners. This is an
unofficial, community-maintained extension and is not affiliated with itch.io.
