# LRoute for Windows

Windows desktop implementation of LRoute. This project is isolated under `windows/`; the iOS implementation remains unchanged.

## Stack
- C# / WPF, .NET 8, Windows x64
- Dark monochrome desktop UI

## Current implementation
- Responsive dashboard and profile list with persistent profile metadata.
- Windows build currently focuses on Xray JSON profiles. WireGuard support is intentionally deferred to a later update.
- QR scanning is intentionally removed from this update and may be added in a later update.
- Manual Config saves Xray JSON profiles.

## Build
Open `LRoute.Windows.sln` in Visual Studio 2022 with the .NET desktop development workload, or run:

```powershell
dotnet build .\LRoute.Windows.sln -c Release
```

The application must not show Connected for WireGuard unless the WireGuard tunnel service reports the Running state. Xray local proxy mode is reported separately from TUN mode. TUN mode is only reported as engine-running until real device traffic, DNS, route restoration, and adapter cleanup are tested on Windows. IPv4 is routed through the TUN; IPv6 is blocked by the Windows leak guard when not routed by this profile. Device-level tunnel, DNS, routing, and handshake tests are still required.
