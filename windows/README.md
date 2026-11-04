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
- Standard WireGuard `.conf` files are passed directly to the official tunnel-service CLI; LRoute does not generate its own `.conf.dpapi` blobs.
- The runtime copy is stored under the current user's LocalAppData and the directory/file ACLs are restricted to the current user, Local System, and Administrators before the tunnel starts.
- Standard WireGuard profiles connect through the official WireGuard tunnel service with a UAC prompt and service-state verification.
- Xray JSON profiles are validated and can start in local proxy mode or optional system-wide TUN mode.\n- System-wide TUN mode creates a Wintun adapter, applies IPv4 default routing, assigns DNS servers, uses Xray outbound-interface binding to avoid routing loops, and enables Windows DNS / misconfigured-IP leak protection. It requests UAC elevation.\n- TUN mode uses a temporary runtime copy of the imported JSON; the original profile file is not modified. Runtime config and error logs are removed when the engine disconnects.\n- The Windows Xray bundle must include the official `wintun.dll` alongside `xray.exe`; CI verifies both files are present.
- AmneziaWG profiles are detected and listed but remain disabled until a compatible native engine is integrated.

## WireGuard prerequisite
Install the official WireGuard for Windows client from https://www.wireguard.com/install/. LRoute locates `wireguard.exe` in the standard Program Files locations. Connecting creates a service named `WireGuardTunnel$LRoute-...`; disconnecting removes that tunnel service. Administrator approval is required.

The official tunnel service CLI and supported `.conf` / `.conf.dpapi` input formats are documented in the [WireGuard for Windows enterprise guide](https://github.com/WireGuard/wireguard-windows/blob/master/docs/enterprise.md).

## Build
Open `LRoute.Windows.sln` in Visual Studio 2022 with the .NET desktop development workload, or run:

```powershell
dotnet build .\LRoute.Windows.sln -c Release
```

The application must not show Connected for WireGuard unless the WireGuard tunnel service reports the Running state. Xray local proxy mode is reported separately from TUN mode. TUN mode is only reported as engine-running until real device traffic, DNS, route restoration, and adapter cleanup are tested on Windows. IPv4 is routed through the TUN; IPv6 is blocked by the Windows leak guard when not routed by this profile. Device-level tunnel, DNS, routing, and handshake tests are still required.
