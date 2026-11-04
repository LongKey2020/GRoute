# LRoute for Windows

Windows desktop implementation of LRoute. This project is isolated under `windows/`; the iOS implementation remains unchanged.

## Stack
- C# / WPF, .NET 8, Windows x64
- Dark monochrome desktop UI

## Current implementation
- Responsive dashboard and profile list with persistent profile metadata.
- Standard WireGuard profiles are detected separately from AmneziaWG profiles.
- WireGuard adapter calls the official WireGuard for Windows tunnel-service CLI.
- Connect/disconnect requests UAC elevation and checks the Windows tunnel service state.
- Before installation, the configuration is DPAPI-protected with machine scope and placed in a dedicated ProgramData folder with restricted ACLs.
- AmneziaWG and Xray profiles are detected/listed but remain disabled until their native engine adapters are complete.

## WireGuard prerequisite
Install the official WireGuard for Windows client from https://www.wireguard.com/install/. LRoute locates `wireguard.exe` in the standard Program Files locations. Connecting creates a service named `WireGuardTunnel$LRoute-...`; disconnecting removes that tunnel service. Administrator approval is required.

The official tunnel service CLI and `.conf.dpapi` format are documented in the [WireGuard for Windows enterprise guide](https://github.com/WireGuard/wireguard-windows/blob/master/docs/enterprise.md).

## Build
Open `LRoute.Windows.sln` in Visual Studio 2022 with the .NET desktop development workload, or run:

```powershell
dotnet build .\LRoute.Windows.sln -c Release
```

The application must not show Connected unless the WireGuard tunnel service reports the Running state. Device-level tunnel and handshake tests are still required.
