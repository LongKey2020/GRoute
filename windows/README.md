# LRoute for Windows

Windows desktop implementation of LRoute. This project is isolated under `windows/`; the existing iOS implementation remains unchanged.

## Stack
- C# / WPF
- .NET 8 (Windows x64)
- Dark monochrome UI

## Current status
- Desktop application shell and profile import UI are scaffolded.
- Import currently indexes selected file names in the current session; it does not yet store credentials or activate a VPN.
- Connect is intentionally disabled until the native tunnel engines and service lifecycle are integrated.

## Planned engine adapters
1. WireGuard: official WireGuard for Windows / WireGuardNT service integration.
2. AmneziaWG: Windows-compatible AmneziaWG engine, subject to verified upstream packaging and API.
3. Xray: Xray-core process integration for VLESS, VMess, Trojan and Shadowsocks.

## Build
Open `LRoute.Windows.sln` in Visual Studio 2022 with the .NET desktop development workload, or run:

```powershell
dotnet build .\LRoute.Windows.sln -c Release
```

VPN engine integration will require an elevated service/driver installation path. The UI must never claim connected until the underlying engine confirms the tunnel state.
