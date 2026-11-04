# LRoute for Windows

A clean Windows x64 rewrite of LRoute. The Android application remains the feature and behavior reference; this Windows build is an initial usable foundation, not full Android feature parity.

## Included in this build

- Native WPF dashboard with LRoute dark/orange styling.
- Self-contained Windows x64 publish with Xray-core 26.10.31 and Wintun runtime packaged.
- Profile import from VLESS, VMess, Trojan, Shadowsocks, SOCKS and HTTP share links.
- Import of complete Xray JSON configurations and standalone outbound JSON.
- Manual profile text import, file import, subscription URL download (HTTP/HTTPS), and per-line import diagnostics.
- DPAPI-protected profile storage under `%LOCALAPPDATA%\LRoute\profiles.dat`.
- Xray configuration validation before engine startup.
- Windows Internet Settings proxy integration: WinINET-aware apps are directed to the local HTTP proxy; previous manual proxy/PAC settings are backed up with DPAPI and restored on disconnect, app close, or next launch after an unclean exit.
- Runtime config cleanup after Xray stops.

## Explicit exclusions and limitations

- WireGuard (all variants) and QR scanning are intentionally excluded.
- Hysteria2, IKEv2 and other protocols requiring a separate runtime are not supported by this Xray-only build.
- TUN mode is not exposed as a user-selectable connection mode in this release candidate. The current TUN config builder has not been validated on physical Windows systems and must not be treated as production-ready.
- Windows System Proxy applies to WinINET-aware applications. It does not guarantee that WinHTTP services, every game, or applications with their own proxy/network stack will use LRoute.
- DNS controls, split routing, kill switch, per-app routing, SSH/SFTP, diagnostics, speed/stability tests, Warp/Tor/Windscribe/Aether integrations, backup/restore UI and the remaining Android tools are not yet ported.
- Advanced Shadowsocks SIP002 plugins and uncommon subscription encodings still need compatibility testing.

## Build locally

Requires the .NET 8 SDK and Windows Desktop build support.

```powershell
dotnet build .\windows\LRoute.Windows\LRoute.Windows.csproj -c Release
dotnet publish .\windows\LRoute.Windows\LRoute.Windows.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\LRoute-win-x64
```

The GitHub Actions workflow also downloads the official Xray Windows x64 archive, packages `xray.exe` and `wintun.dll`, runs parser/configuration tests, and uploads the `LRoute-win-x64` artifact.

## Validation boundary

CI verifies the WPF build, self-contained publish, bundled Xray runtime, representative VLESS/VMess/Shadowsocks configuration parsing, Xray `run -test`, TLS output, partial-import diagnostics, and unauthenticated HTTP proxy configuration.

CI does **not** prove successful connectivity to a real proxy server, Windows proxy restoration on a user's machine, DNS leak resistance, TUN route cleanup, or compatibility with every Windows application. Those require physical Windows testing before a general production release.
