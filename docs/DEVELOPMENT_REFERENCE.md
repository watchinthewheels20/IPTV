# LoungeTV — Development Reference

**Baseline:** v1.0.0 (16 October 2026)  
**Repository:** https://github.com/watchinthewheels20/IPTV  
**Platform:** Windows 11 x64; native WPF/.NET application  
**Purpose:** This is the maintained context for future development. It describes the selected design, current implementation, interfaces and known constraints—not the development history.

## 1. Product goal and design decisions

LoungeTV is a lightweight, controller-first living-room IPTV application for a Windows PC connected to a television. The target UX is **classic Sky+ HD** (approximately the 2009–2015 interface), *not* Sky Glass or Sky Q.

- Dark/navy-blue screens, rectangular guide panels, blue programme blocks and **gold/yellow focus and selection**.
- **Direct access to the TV Guide after Xtream login**, with a separate classic text-menu Home page.
- Emphasise instantaneous D-pad response, readable text at TV viewing distance, and minimal decorative animation.
- Show a **2-hour programme timeline**, channel rows, programme titles/descriptions, category filtering and a current-time indicator.
- Keep a simple **channel-list + Mini TV player** screen alongside the EPG. Choosing a guide channel opens this screen and starts its stream; guide video preview is **not** yet integrated.
- Use the Xbox controller for everyday operation. Keyboard/mouse remain available for account entry, search and recovery.
- Do not copy Sky trademarks or proprietary assets; the styling and interaction are inspired by the older interface.

## 2. Technical stack / packaging

| Area | Decision / implementation |
| --- | --- |
| Language/framework | C#, **.NET 10**, **WPF**, Windows x64 |
| Playback | **LibVLCSharp.WPF 3.10.2** + **VideoLAN.LibVLC.Windows 3.0.24** |
| IPTV authentication/catalogue | Xtream-compatible **Player API** |
| EPG | Xtream-compatible **XMLTV** feed via `xmltv.php` |
| Controller | Windows **XInput** via `xinput1_4.dll`, D-pad and ABXY/shoulders |
| UI | XAML and WPF code-behind; no browser/Electron runtime |
| App packaging | `Build-LoungeTV.ps1` publishes self-contained `win-x64` output |
| CI | `.github/workflows/windows-build.yml`: .NET 10, EPG smoke tests, publish, verify `LoungeTV.exe` and `libvlc.dll`, upload artifact |

The published app bundles the native VLC libraries and .NET runtime. **VLC Media Player does not need to be installed separately.** Keep the full published folder (including native DLLs/plugins) together. A build requires the .NET 10 SDK; the default published app does not.

## 3. Xtream authentication, catalogue, and playback

User enters **base server URL** (http/https, optional port), username, and password. The URL must not be an M3U or `player_api.php` URL. Typical requests:

- `/player_api.php?username=…&password=…` — account/auth/status/output formats.
- `/player_api.php?...&action=get_live_categories` — categories.
- `/player_api.php?...&action=get_live_streams` — channels, `stream_id`, `category_id`, `epg_channel_id`.
- `/live/{username}/{password}/{stream_id}.ts` or `.m3u8` — live stream.
- `/xmltv.php?username=…&password=…` — programme listings.

Account status must be active. Auto playback prefers TS where allowed, otherwise HLS; user can override to MPEG-TS or HLS.

Only one LibVLC playback session is intended. Playback stop/start operations use a `SemaphoreSlim` and background work so a synchronous native VLC stop does not block the WPF UI thread. A **20-second watchdog** reports when playback has not reached the Playing event. These are initial reliability measures, not proof of universal provider compatibility.

**Security:** These Xtream API/stream URLs contain credentials. Never log complete URLs, dump secrets into CI, commit credentials, or turn off TLS certificate validation. Prefer HTTPS when supported. Use only streams the user is authorised to access.

## 4. EPG model, UK time and manual offset

- `XmlTvService` streams XMLTV using `XmlReader` (not a whole-document DOM), looks at the subscribed channel IDs/names and bounds the retained data to approximately **12 hours back / 4 days forward**, capped at **250,000 programmes**.
- `EpgSnapshot` holds programme times as **UTC `DateTimeOffset`** values. Mapping tries `epg_channel_id`, numeric stream ID, exact channel name, then normalised name.
- XMLTV `start` / `stop` timezones, such as `+0100`, `+0000`, `BST`, `GMT`, are parsed before display. An XMLTV timestamp without a timezone is treated as UTC.
- Display times use the Windows timezone **`GMT Standard Time`** (London), which handles the annual BST/GMT change.
- **Manual EPG SHIFT is a separate provider correction**, applied **after** timezone conversion, and it moves programme blocks/times **without moving the current-time clock or changing streaming**.
- UI choices: **−2h to +2h**, steps of 30 minutes; default **0h**. **+1h** corrects programmes appearing one hour too early; **−1h** corrects those appearing one hour too late.
- The chosen number of minutes is saved at `%LOCALAPPDATA%\LoungeTV\epg-offset.txt`; it persists across restarts. This is **EPG display correction**, not pause/rewind/recording of live video.
- EPG loading is asynchronous **after login** so channel browsing/playback is not blocked by a large or unavailable feed. Refresh EPG retries the download.
- **Current limitation:** EPG lives in memory; there is no persisted XMLTV cache or periodic automatic refresh. Provider-specific channel ID mismatches/incorrect timezones may still require follow-up.

## 5. Navigation contract

| Context | Action | Control |
| --- | --- | --- |
| Guide | Move channel | D-pad Up/Down |
| Guide | Move selected programme time | D-pad Left/Right (30 min) |
| Guide | Tune selected channel | A / Enter |
| Guide | Change EPG correction | LB/RB (−/+30 min); EPG SHIFT dropdown |
| Guide | Cycle category | Y; category dropdown |
| Guide | Return to channel browser | B / Escape |
| Guide | Jump to now | NOW button |
| Guide | Shift visible timeline | Earlier/Later 30m buttons |
| Channel browser | Navigate categories/channels | D-pad, A |
| Channel browser | Fullscreen | X / F11 |
| Fullscreen | Change channel | D-pad Up/Down |
| Fullscreen | Exit fullscreen | B / Escape |
| Home | Move focus / select | D-pad / A; keyboard arrow keys / Enter |

Guide keyboard shortcuts: **PageUp/PageDown** or **[ / ]** change manual shift by −/+30 minutes; **C** cycles guide categories. Credentials and typed channel search still require keyboard or equivalent text input.

XInput currently polls every **30ms**, caches the active controller index, and uses roughly **195ms initial / 85ms repeat** delay. Channel search uses a **90ms debounce**, and list scrolling is deferred/coalesced to keep menus responsive.

## 6. Important code entry points

- `LoungeTV/MainWindow.xaml`, `MainWindow.xaml.cs`: login, classic Home / Guide / Channels navigation, live-player lifetime and EPG loading.
- `LoungeTV/Controls/EpgGuideControl.xaml` + `.xaml.cs`: two-hour timeline rendering, filtering, time correction, programme selection and controller commands. Only visible channel rows are constructed.
- `LoungeTV/Controls/GamepadInput.cs`: XInput polling and gamepad mapping.
- `LoungeTV/Services/XtreamApiClient.cs`: account/catalogue endpoints, channel metadata, stream URL creation, XMLTV fetch.
- `LoungeTV/Services/XmlTvService.cs`: XMLTV parser and timestamp interpretation.
- `LoungeTV/Models/EpgModels.cs`, `IptvModels.cs`: EPG/channel domain models and matching rules.
- `LoungeTV/Services/EpgOffsetStore.cs`: persisted manual guide shift.
- `LoungeTV/Services/SettingsStore.cs`: locally saved credentials and Windows DPAPI `CurrentUser` encryption.
- `LoungeTV/Services/RecentChannelStore.cs`: last-selected channel ID for resume.
- `LoungeTV/Services/PlaybackDiagnostics.cs`: bounded privacy-conscious diagnostic log.
- `LoungeTV.EpgChecks/Program.cs`: automated XMLTV parsing, mapping and BST/GMT smoke checks.

## 7. Local data and diagnostics

| Path (within `%LOCALAPPDATA%\LoungeTV`) | Use |
| --- | --- |
| `settings.json` | Server/user and DPAPI-encrypted password (if Remember login chosen) |
| `epg-offset.txt` | Manual EPG shift in minutes |
| `recent-channel.txt` | Last-selected numeric stream ID |
| `playback.log` (+ optional `.previous`) | VLC lifecycle stages, failures by exception **type**, EPG load count; no credential-bearing URLs |

Never request actual IPTV credentials in GitHub issues or include them in diagnostic reports.

## 8. v1.0 scope and honest verification status

**Implemented:** Xtream login and channel catalogue, live TS/HLS video playback, classic blue Sky+ HD-inspired Home/Channels/Guide UI, programme-grid EPG, category filtering, programme details, configurable UK EPG correction, Xbox controller support, basic last-channel resume, diagnostics and self-contained Windows packaging.

**Not included:** movies/series VOD; favourites and configurable channel ordering; live-video pause/rewind/time shifting or recording; full guide video preview; SQLite-based EPG cache; DRM playback; HDMI-CEC remote-power support; installer/signing.

**Verified:** successful Windows GitHub Actions compilation, packaged `LoungeTV.exe` with bundled `libvlc.dll`, automated sample XMLTV parsing/channel mapping and UK GMT/BST checks. Earlier live playback and Xtream catalogue operations have been exercised on the target PC; **this integrated EPG version still needs hands-on testing with the real provider and controller**. CI does not verify real provider XMLTV, display latency, stream stutter, or fullscreen overlay composition.

## 9. Next development priorities (not v1.0 features)

1. Validate EPG loading and channel matching with the user's provider, especially absent/mismatched `epg_channel_id` and one-hour offsets.
2. Profile and remove any remaining menu lag under large channel catalogues; retain minimal animation and bounded EPG rendering.
3. Improve guide focus/navigation and video-preview behaviour while preserving one active IPTV stream.
4. Add EPG disk caching, incremental/periodic refresh and robust missing-data behaviour.
5. Consider favourites, channel numbering/reordering, VOD/catch-up, and a TV remote strategy only after core guide/navigation is solid.

## 10. Release and change policy

**v1.0.0** denotes the first functional baseline with live TV, classic Sky+ HD-style EPG and an adjustable programme time offset; it is not a claim of production-grade stability. Preserve this milestone in GitHub (tag/release). Make subsequent changes on branches/PRs, run Windows CI and the EPG smoke tests, and avoid modifying the v1.0 tag. Update this document when core decisions or interfaces materially change.
