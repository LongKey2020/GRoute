# LRoute for Windows

A clean Windows x64 rewrite of LRoute. The Android application remains the feature and behavior reference; this Windows build is an initial usable foundation, not full Android feature parity.

## Included in this build

- Native WPF dashboard with LRoute dark/orange styling and grouped Choose Server profile management: add/import, subscription grouping, expand/collapse groups, search, name sorting, bounded-concurrency Test All, per-profile ping/edit/share/export/remove actions, group refresh/test/share/remove actions.
- Self-contained Windows x64 publish with Xray-core 26.10.31 and Wintun runtime packaged.
- Profile import from VLESS, VMess, Trojan, Shadowsocks, SOCKS and HTTP share links.
- Import of complete Xray JSON configurations and standalone outbound JSON.
- Manual profile text import, multi-file import, Xray JSON/share-link export, subscription URL download and refresh (HTTP/HTTPS), and per-line import diagnostics. A server Ping Check reports packet loss and min/average/max ICMP latency.
- DPAPI-protected profile storage under `%LOCALAPPDATA%\LRoute\profiles.dat`, plus encrypted profile backup/restore files that are bound to the current Windows user.
- Xray configuration validation before engine startup and a non-connecting “Validate selected” profile diagnostic.
- Windows Internet Settings proxy integration: WinINET-aware apps are directed to the local HTTP proxy; previous manual proxy/PAC settings are backed up with DPAPI and restored on disconnect, app close, or next launch after an unclean exit.
- Connection settings for custom DNS resolvers (IP or HTTPS DNS endpoints) and domain-based direct bypass rules.
- Persistent non-secret connection preferences under `%LOCALAPPDATA%\LRoute\settings.json`.
- Experimental opt-in TUN mode using the bundled Wintun runtime and administrator elevation; it is not a production-validated replacement for system proxy mode.
- Runtime config cleanup after Xray stops.

## Explicit exclusions and limitations

- QR scanning is not included in this Windows build.
- Hysteria2, IKEv2 and other protocols requiring a separate runtime are not supported by this Xray-only build.
- TUN mode is exposed as experimental opt-in only. CI validates generated TUN JSON with Xray, but route installation, DNS behavior, disconnect cleanup and leak protection have not been validated on physical Windows systems. Do not treat it as production-ready.
- Windows System Proxy applies to WinINET-aware applications. It does not guarantee that WinHTTP services, every game, or applications with their own proxy/network stack will use LRoute.
- Advanced DNS policies (fake DNS, encrypted DNS policy, ad-block lists), geo-based split routing, kill switch, per-app routing, SSH/SFTP, speed/stability tests, Warp/Tor/Windscribe/Aether integrations and the remaining Android tools are not yet ported. Network Test currently provides ICMP Ping to the selected or active profile server; it does not measure tunneled latency. The current DNS setting replaces Xray DNS servers; current split routing supports user-entered domain bypass only.
- Advanced Shadowsocks SIP002 plugins and uncommon subscription encodings still need compatibility testing.

## Build locally

Requires the .NET 8 SDK and Windows Desktop build support.

```powershell
dotnet build .\windows\LRoute.Windows\LRoute.Windows.csproj -c Release
dotnet publish .\windows\LRoute.Windows\LRoute.Windows.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\LRoute-win-x64
```

The GitHub Actions workflow also downloads the official Xray Windows x64 archive, packages `xray.exe` and `wintun.dll`, runs parser/configuration tests, and uploads the `LRoute-win-x64` artifact.

## Validation boundary

CI verifies the WPF build, self-contained publish, bundled Xray runtime, representative VLESS/VMess/Trojan/Shadowsocks/SOCKS/HTTP parsing, Xray `run -test`, TLS/default-Trojan behavior, VMess fallback naming, partial-import diagnostics, caller-owned config preservation, imported full-JSON local listeners, standalone profile diagnostics, generated TUN configuration, custom DNS, domain bypass ordering, subscription source metadata, Windows proxy restoration, DPAPI profile storage and encrypted backup/restore.

CI does **not** prove successful connectivity to a real proxy server, Windows proxy restoration on a user's machine, DNS leak resistance, TUN route cleanup, or compatibility with every Windows application. Those require physical Windows testing before a general production release.


### Subscription usage and expiry

When a subscription is refreshed, LRoute reads the optional `subscription-userinfo` response header and stores upload, download, total quota, and Unix expiry values with the subscription profiles. The profile list displays used/total data and expiry. If the provider omits this header, existing metadata is retained; a missing value is not treated as zero quota. Background/scheduled refresh is not yet implemented.
