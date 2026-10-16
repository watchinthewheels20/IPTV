# LoungeTV v1.0.0 — First functional baseline

Windows x64 living-room IPTV player with a **classic Sky+ HD-inspired** blue/gold interface.

## Included

- Xtream Codes server login, categories and live channels.
- MPEG-TS and HLS playback using bundled LibVLC; full-screen operation.
- Classic programme guide with two-hour timeline, programme details and category filtering.
- Background XMLTV loading and channel-ID/name matching.
- UK GMT/BST time conversion and **saved EPG SHIFT (−2h to +2h, 30-minute steps)** to correct provider listings.
- Xbox XInput controller navigation, including LB/RB programme-time correction.
- Last-channel resume, encrypted optional credentials and privacy-conscious diagnostics.
- Self-contained Windows x64 application (no separate .NET runtime or desktop VLC install).

## Validation

GitHub Actions compiles the Windows WPF application, runs automated XMLTV and UK BST/GMT smoke tests and checks that LibVLC native libraries are present. Live stream playback was previously exercised on the target PC, but the integrated v1.0 EPG and controller navigation still require provider-specific runtime testing.

## Not included

Movies/series on demand, recording or rewinding live TV, favourites, persistent EPG cache, signed installer, HDMI-CEC power control or DRM playback.

For architecture, controls, settings and development constraints, see [Development Reference](DEVELOPMENT_REFERENCE.md).
