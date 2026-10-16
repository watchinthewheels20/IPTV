# LoungeTV

**Version 1.0.0** — Windows 11 living-room IPTV player, inspired by the classic Sky+ HD interface.

A native **C# / WPF / .NET 10** application with Xtream Codes access, live TV, an XMLTV programme guide, Xbox controller navigation and manual programme-time correction for provider EPGs. Use only services and streams you are authorised to access.

The project's maintained technical context and design decisions are in **[Development Reference](docs/DEVELOPMENT_REFERENCE.md)**.

## Download or build

- **Stable milestone:** [LoungeTV v1.0.0 on GitHub Releases](https://github.com/watchinthewheels20/IPTV/releases/tag/v1.0.0) (Windows x64 ZIP; includes LibVLC and .NET runtime).
- **Latest development build:** [GitHub Actions](https://github.com/watchinthewheels20/IPTV/actions/workflows/windows-build.yml). Open a successful run and download `LoungeTV-win-x64` from Artifacts.
- **Build from source:** Install the [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0), then run in PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-LoungeTV.ps1
```

The executable is output to `publish\LoungeTV-win-x64\LoungeTV.exe`. Keep its support files together. A separate VLC Media Player install is **not** required. The normal published app is self-contained; a smaller framework-dependent build is available with `-FrameworkDependent`.

## Set up and use

Enter the **Xtream-compatible base server URL**, username and password. The app loads categories and channels, then asynchronously retrieves the provider's `xmltv.php` programme feed. The initial screen after connecting is the TV Guide. Use the Guide/Channels/Home controls to switch screens.

- **TV Guide:** two-hour timeline, channel rows, programme information, categories, NOW and 30-minute time navigation.
- **Watch:** select a channel and press A/Enter; in the channel browser use the Mini TV and full-screen player.
- **Stream format:** Auto selects TS/HLS from account capabilities; switch to HLS or MPEG-TS manually if required.
- **EPG SHIFT:** choose -2h to +2h in 30-minute increments. Use **+1h** if the provider's programme titles are one hour early; **-1h** if they are one hour late. Leave **0h** for a correct feed. UK GMT/BST conversion happens automatically before this correction, and the choice is remembered.
- **Controller:** guide D-pad up/down = channels, left/right = programme time, A = watch, Y = next category, LB/RB = change EPG shift, B = back. The channel browser supports D-pad navigation, A to play, and X for fullscreen. Keyboard shortcuts are documented in the Development Reference.

## Security and diagnostics

Optional saved credentials use Windows DPAPI CurrentUser protection. Sensitive Xtream URLs can contain account details, so don't post or commit credentials or stream links. HTTPS is preferred when your provider supports it. Playback diagnostics are at `%LOCALAPPDATA%\LoungeTV\playback.log`; EPG correction at `%LOCALAPPDATA%\LoungeTV\epg-offset.txt`.

## Current limits

There is **no** VOD/series browser, favourites, DVR/live-video pause/rewind, persistent EPG cache, installer, code signing, DRM bypass, or HDMI-CEC remote power control. UI/controller performance and provider EPG compatibility need testing on the actual Windows PC. GitHub Actions verifies compilation, packaging and sample GMT/BST/XMLTV cases—not real provider operation.

For implementation details, file locations and future priorities, see [docs/DEVELOPMENT_REFERENCE.md](docs/DEVELOPMENT_REFERENCE.md).
