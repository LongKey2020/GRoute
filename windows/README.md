# LRoute for Windows — Rebuild

A clean Windows rewrite of LRoute, using the Android APK as the feature and behavior reference.

## Current foundation
- New NetBird-inspired LRoute dashboard with native Windows minimize, maximize and close controls.
- New profile domain model, profile persistence and first-pass share-link parser.
- Manual profile import and file import for Xray-compatible links / JSON.
- Runtime Xray JSON builder for VLESS, VMess, Trojan, Shadowsocks, SOCKS and HTTP profiles.
- WireGuard and QR scanning are intentionally excluded.

## Build
```powershell
dotnet build .\windows\LRoute.Windows\LRoute.Windows.csproj -c Release
```

## Important implementation status
This is the first foundation milestone, not feature parity. The parser currently supports common share-link forms; advanced transports and subscription formats still need parity work against Android `ConfigParser.kt` and `ConfigBuilder.kt`. TUN, DNS/split routing, kill switch, SSH/SFTP, diagnostics and optional services must be implemented and tested separately. A successful CI build is not proof of device-level VPN behavior.
