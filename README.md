# DesktopBuddy

A Resonite mod that spawns world-space desktop/window viewers with touch input, GPU-accelerated capture, and remote streaming.

## Note on Development

This mod was initially developed with AI assistance (Claude) and has since been mostly humanized through manual review and rewrites. If you're familiar with Resonite's UIX system or the context menu API, contributions to improve those areas are especially welcome — extra eyes on the UI code would be a huge help.

We're also looking for help with:

- **AMD GPU support** — currently NVENC-only, needs AMF/VCE encoder paths
- **Linux support** — WGC is Windows-only, needs PipeWire/XDG portal capture + VA-API encoding
- **Code review** — if you spot anything that looks off, please open an issue or PR

## Features

- **GPU-accelerated capture** via Windows.Graphics.Capture (WGC) — no GDI overhead
- **GPU BGRA-to-RGBA conversion** via D3D11 compute shader
- **Hardware H.264 encoding** via NVENC through FFmpeg
- **Remote streaming** via MPEG-TS over Cloudflare Tunnel — other users see your desktop in VR
- **Per-window audio capture** via WASAPI process loopback
- **Touch/mouse/keyboard input** injection from VR controllers, keeps input secure.
- **Child window detection** — popups and dialogs auto-spawn as separate panels
- **Context menu integration** — use context menu to pick a window or monitor

## Prerequisites

- Windows 10+ with an NVIDIA GPU (NVENC required for streaming)
- [Resonite](https://store.steampowered.com/app/2519830/Resonite/) with [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) installed
- .NET 10 SDK
- Windows SDK 10.0.19041.0+ (for `fxc.exe` shader compiler)

## Building

```
scripts/build.sh
```

This will:
1. Kill any running Resonite/cloudflared processes, sometimes cloudflare keeps resonite open
2. Build the mod DLL (compiles the HLSL compute shader, ILRepacks dependencies into a single DLL)
3. Copy `DesktopBuddy.dll` to `Resonite/rml_mods/`
4. Copy FFmpeg DLLs to `Resonite/ffmpeg/`
5. Copy `cloudflared.exe` to `Resonite/cloudflared/`
6. Launch Resonite

The Resonite install path is configured in `DesktopBuddy/DesktopBuddy.csproj` (`ResonitePath` property).

## Packaging

```
scripts/package.sh
```

Creates `DesktopBuddy.zip` from the last build. Extract into the Resonite root folder. Contents:

- `rml_mods/DesktopBuddy.dll` — the mod (all managed deps merged in)
- `ffmpeg/` — FFmpeg shared libraries (for MPEG-TS muxing)
- `cloudflared/` — Cloudflare Tunnel binary (for remote user streaming)

## Usage

1. In Resonite, open the context menu (right-click or equivalent VR gesture)
2. Select **Desktop** to open the window/monitor picker
3. Pick a window or monitor to spawn a viewer panel
4. Interact with the panel using VR controllers (touch, scroll, keyboard)
5. Other users in the session see the stream via Cloudflare Tunnel

## Troubleshooting

**Streaming not working for other users?** The mod runs a local HTTP server on port 48080. If streaming isn't working, run this command once as Administrator to allow it through:

```
netsh http add urlacl url=http://+:48080/ sddl="D:(A;;GX;;;S-1-1-0)"
```

The mod tries to do this automatically, but it may fail without admin privileges.

## License

AGPL-3.0 — see [LICENSE](LICENSE). Forks must contribute changes back via pull request.
