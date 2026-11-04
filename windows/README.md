# LRoute for Windows

Windows desktop implementation of LRoute. This project is isolated under `windows/`; the existing iOS implementation remains unchanged.

## Stack
- C# / WPF
- .NET 8 (Windows x64)
- Dark monochrome desktop UI

## Current implementation
- Responsive dashboard and profile list.
- Imports and persists profile metadata.
- WireGuard adapter uses the official WireGuard for Windows tunnel-service CLI.
- Connect/disconnect requests UAC elevation and verifies the Windows tunnel service state.
- Imported WireGuard configurations are DPAPI-protected with machine scope before being handed to the WireGuard service.
- Xray and AmneziaWG profiles are listed but intentionally not connectable until their Windows-native engine adapters are integrated.

## WireGuard prerequisite
Install the official WireGuard for Windows client from https://www.wireguard.com/install/. LRoute locates `wireguard.exe` in the standard Program Files locations. Connecting installs a dedicated Windows tunnel service named `WireGuardTunnel$LRoute-...`; disconnecting uninstalls that service. The operation requires administrator approval.

## Build
Open `LRoute.Windows.sln` in Visual Studio 2022 with the .NET desktop development workload, or run:

```powershell
dotnet build .\LRoute.Windows.sln -c Release
```

The application must not display Connected unless the WireGuard tunnel service reports the Running state.
