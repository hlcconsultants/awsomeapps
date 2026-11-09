# FastGet

An open-source download manager for Windows, with Chrome integration.

- Up to 32 parallel connections per download, with idle connections taking over from slower ones
- Pause and resume that survives restarts, automatic retries, and updating an expired link
- Queue, scheduler, speed limit, and sleep or shutdown when finished
- HLS and DASH video grabbing
- Chrome extension for capturing downloads and detecting video on a page

Parallel connections help when a server limits speed per connection or latency is high. They can't exceed your own bandwidth.

FastGet refuses DRM-protected and live streams. It doesn't capture YouTube. You are responsible for only downloading content you have the right to download.

## Install

Download `FastGet-Setup-<version>.exe` from a `fastget-v` release on the [Releases page](https://github.com/hlcconsultants/awsomeapps/releases) and run it. No admin rights are needed.

To connect Chrome, install the FastGet extension. For a local build, open `chrome://extensions`, turn on Developer mode, choose **Load unpacked**, and select the `extension` folder.

## Video output

FastGet needs ffmpeg to produce `.mp4` files and merge separate audio. Choose **Install ffmpeg** in Settings, or put `ffmpeg.exe` next to `FastGet.exe` or on `PATH`. Without it, HLS saves as `.ts` and separate audio stays in its own file.

## Build from source

Requires the .NET 10 SDK and, for the installer, [Inno Setup 6](https://jrsoftware.org/isinfo.php). Run these from the `FastGet` folder.

```bash
dotnet test tests/FastGet.Tests
```

```bash
pwsh installer/build.ps1 -Version 1.0.0
```

The installer and Chrome Web Store package are written to `installer/output`.

## Project layout

| Path | What it is |
|---|---|
| `src/FastGet.Core` | Download engine, queue, scheduler, HLS/DASH, IPC |
| `src/FastGet.App` | WPF desktop app (`FastGet.exe`) |
| `src/FastGet.Host` | Chrome native-messaging bridge (`FastGet.Host.exe`) |
| `extension` | Chrome extension (Manifest V3) |
| `installer` | Inno Setup script and build script |
| `store` | Chrome Web Store listing text |
| `tests/FastGet.Tests` | Engine tests against a local HTTP server |

## More

- [Contributing](CONTRIBUTING.md)
- [Privacy](PRIVACY.md)
- [Publishing a release](RELEASING.md)

## Code signing policy

Releases are built from this repository by GitHub Actions. FastGet is applying to SignPath Foundation for free code signing, and current releases are unsigned. If approved, signed releases will carry this statement: Free code signing provided by SignPath.io, certificate by SignPath Foundation.

Team roles:

- Committers and reviewers: [hlcconsultants](https://github.com/hlcconsultants)
- Approvers: [hlcconsultants](https://github.com/hlcconsultants)

Privacy: see the [privacy policy](PRIVACY.md). FastGet only contacts the websites you download from. The optional ffmpeg install uses winget, which has its own terms.

## License

Copyright (C) 2026 FastGet contributors. Licensed under the [GNU General Public License v3.0 or later](LICENSE). The bundled .NET runtime has its own notices, installed alongside the app.
