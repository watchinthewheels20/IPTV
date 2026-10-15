# LoungeTV — Stage 1 (Windows / Xtream Codes)

A first functional **source-code release** for a Windows 11 living-room IPTV player. It uses WPF and LibVLCSharp. The supplied ZIP contains source and build scripts; **it does not contain a compiled EXE**. Compilation and video testing require Windows.

## What Stage 1 does

- Xtream Codes Player API account authentication, subscription status checking
- Live categories and channel retrieval
- Local text search and channel filtering
- Live playback via LibVLC with TS/HLS format selection (Auto / MPEG-TS / HLS)
- Fullscreen playback (F11 / Escape), channel switching in fullscreen with D-pad
- Keyboard / mouse and basic Xbox/XInput controller operation
- Optional Windows DPAPI-encrypted password storage at `%LOCALAPPDATA%\LoungeTV\settings.json`
- Single active playback session; no channel guide or VOD in Stage 1

## Build on the living-room PC

1. Download/install **.NET 10 SDK for Windows x64** (SDK, *not* just the runtime): https://dotnet.microsoft.com/download/dotnet/10.0
2. Extract the entire ZIP somewhere writable.
3. Open PowerShell in the extracted `LoungeTV-Stage1` directory.
4. Run:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-LoungeTV.ps1
   ```

5. The compiled app will be in `publish\LoungeTV-win-x64\`. Run **LoungeTV.exe** there. Keep the full folder together.

The script runs `dotnet publish -c Release -r win-x64 --self-contained true` and downloads the specified NuGet dependencies during the first build. A self-contained build needs neither a separate .NET runtime nor a separate VLC desktop installation to **run**. It **does** include native LibVLC DLLs and its plugin folder as app-local files.

To build a smaller output that requires the .NET Desktop Runtime on the target PC, add `-FrameworkDependent` to the script command.

## IPTV setup

Enter the **base server URL** (example: `https://your-provider.example:443`), username and password. This is **not** a playlist/M3U URL or a URL ending in `player_api.php`.

After connection:
- Choose a category, select a channel, then press **Play selected** or double-click it.
- Stream format defaults to TS when permitted and falls back to HLS for HLS-only accounts. You can manually override the playback format if your provider requires it.
- Press **Stop** to close the current stream.
- Use **F11** to enter/exit fullscreen; **Escape** exits fullscreen.
- **Refresh channels** fetches an updated listing.
- **Account** returns to the login form. Login must be reconfirmed after restarting (the saved password may be filled automatically).

The API is based on common Xtream-compatible endpoints:
- `/player_api.php?username=...&password=...`
- `/player_api.php?...&action=get_live_categories`
- `/player_api.php?...&action=get_live_streams`
- `/live/{username}/{password}/{stream_id}.ts` (or `.m3u8`)

Provider compatibility may vary. The app does not support DRM-protected playback, M3U-only providers, proxy login, VOD or EPG in this release.

## Xbox controller (early implementation)

- **Left / Right:** switch focus between category list and channel list
- **Up / Down:** move the selection
- **A:** play selected channel (or move to channel list if currently selecting a category)
- **B:** stop playback; if fullscreen, exit fullscreen
- **X:** toggle fullscreen
- In fullscreen, **Up / Down:** switch channels from the active channel filter

Use mouse/keyboard to enter the server credentials, username, password or search text. DirectInput-only or Bluetooth remotes that do not emulate XInput are not supported by the controller integration yet.

## Safety and privacy

- **Only use IPTV streams that you are authorised to access.**
- Passwords are stored locally using **Windows DPAPI CurrentUser** if Remember login is selected; users on a different Windows account cannot ordinarily decrypt them.
- Your provider's Xtream API and stream URLs necessarily include credentials, sometimes in URL paths. The app does not write them to log files; avoid sharing raw process dumps or network traces.
- **HTTPS is strongly recommended** if your provider supports it. The app allows HTTP for compatibility but credentials and streamed content may traverse the network unencrypted.
- Invalid TLS certificates are **not** bypassed.
- This software does not bypass DRM, geoblocking, or subscription limits.
- The build is not digitally code-signed.

## Troubleshooting

**Build error `dotnet` not recognised**: install the .NET 10 **SDK**; restart PowerShell.

**NuGet package restore fails**: verify access to nuget.org and corporate proxy/certificate settings. All library versions are pinned in `LoungeTV.csproj`.

**Connection error**: check the base server URL, port, account status and login details. Some providers use a different Xtream-compatible API or require a special URL.

**Video starts but shows a black screen**: try HLS instead of Auto, or MPEG-TS if HLS fails. Check whether the same stream works with your provider's other supported players. This is a first version, not a tested universal player.

**Xbox controller doesn't respond**: verify that Windows recognises it as an XInput-compatible controller and that LoungeTV is the active window.

## Diagnosing playback stalls (playback-fix build)

If the channel list loads but video stays black, shows "Opening stream", or the UI stops responding:

1. First choose **HLS** in the stream format box and try a channel. If that fails, try **MPEG-TS**.
2. Check the playback status below the video. A stream without a VLC `Playing` event is marked as timed out after 20 seconds.
3. Open `%LOCALAPPDATA%\LoungeTV\playback.log` (paste this path into Windows Explorer's address bar) and inspect the final events.
4. Report whether the **whole window is unresponsive** or just **video remains black/buffering**. Also report which format you tried.

The app records only playback stage names, format choice, timestamps, and exception *type*—not stream URLs, passwords, usernames, server addresses, or native VLC messages. You can normally share the log, but review it yourself first.

The previous code called LibVLC's synchronous `MediaPlayer.Stop()` on the UI thread during every channel change. This build serialises media operations and performs blocking stop/play operations on background threads so the window can still respond when a provider or stream misbehaves.

## New: Sky-inspired home screen (UI preview)

After login LoungeTV now starts on a TV-first, Sky Glass-inspired **Home** screen.
It has **Watch live TV**, **Continue watching**, **Channels**, **Search**, and
clickable category shortcuts. The **TV Guide** tile is intentionally disabled:
a full Sky Q-inspired EPG is planned for a separate development stage.

- **Home** (top bar) returns from the channel browser and stops the hidden live stream.
- **B / Escape** returns from the channel browser to Home; **B** exits fullscreen first.
- **D-pad** navigates Home controls, **A** activates the focused button.
- **Continue watching** restores the last channel ID (stored locally, without credentials).
- Channel search is debounced and replaces the list's item source in one operation to avoid
  thousands of observable-collection updates with large IPTV playlists.
- The existing LibVLC playback engine and secure login are preserved.

This is a UI preview; controller focus and runtime behaviour still need testing on
Windows with an actual controller and a provider playlist. The local playback diagnostic
log does not measure frames dropped or decode performance; choppy video would require
additional information, separate from slow channel-list navigation.

## Stage 2 and later

The planned next addition is a Sky Q-inspired channel guide using provider XMLTV/EPG data, integrated with the same existing video session. A Sky Glass-inspired home screen, favourites, VOD and improved remote control support will follow.

## Build status

**Stage 1 revision 1 (15 October 2026):** Fixed missing `using System.Net.Http;` in `XtreamApiClient.cs`, which caused compiler error `CS0246` for `HttpClient`. Removed the redundant `System.Security.Cryptography.ProtectedData` package reference to resolve the .NET 10 `NU1510` warning. The code still needs a Windows build and run check.


Source files and XAML were prepared and checked for XML well-formedness in a non-Windows build environment, **but this release has not been compiled or run on Windows here**. Treat it as a buildable first implementation requiring first-run testing and possible adjustments, not as a verified production EXE.
