using System.Diagnostics;
using System.IO;

namespace LRoute.Windows;

public sealed class WireGuardEngine
{
    private static readonly string ProgramDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LRoute", "WireGuard");

    public string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WireGuard", "wireguard.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task ConnectAsync(ProfileItem profile, CancellationToken cancellationToken = default)
    {
        if (profile.Type != "WireGuard")
            throw new NotSupportedException("Only standard WireGuard profiles are currently supported.");

        var executable = FindExecutable()
            ?? throw new FileNotFoundException("WireGuard for Windows is not installed. Install the official client first.");
        if (!File.Exists(profile.FilePath))
            throw new FileNotFoundException("The selected profile file could not be found.", profile.FilePath);
        if (string.IsNullOrWhiteSpace(profile.TunnelName))
            throw new InvalidOperationException("The profile has no tunnel identifier. Remove and re-import this profile.");

        Directory.CreateDirectory(ProgramDataDirectory);
        var configPath = Path.Combine(ProgramDataDirectory, profile.TunnelName + ".conf");
        var plainConfig = await File.ReadAllBytesAsync(profile.FilePath, cancellationToken);
        // WireGuard for Windows accepts a normal .conf file directly. Do not
        // hand-roll .conf.dpapi: its DPAPI description and execution identity
        // must match WireGuard's own implementation. Keep plaintext under the
        // current user's LocalAppData ACL, which also grants SYSTEM read access.
        await File.WriteAllBytesAsync(configPath, plainConfig, cancellationToken);

        await RunElevatedWireGuardInstallAsync(
            executable, configPath, ProgramDataDirectory,
            System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
            cancellationToken);
        var serviceName = "WireGuardTunnel$" + profile.TunnelName;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await IsServiceRunningAsync(serviceName, cancellationToken)) return;
            await Task.Delay(500, cancellationToken);
        }
        var serviceDiagnostics = await RunAsync("sc.exe", new[] { "queryex", serviceName }, cancellationToken);
        var diagnosticPath = Path.Combine(ProgramDataDirectory, profile.TunnelName + ".diagnostics.txt");
        var logText = File.Exists(diagnosticPath)
            ? await File.ReadAllTextAsync(diagnosticPath, cancellationToken)
            : "Elevated WireGuard diagnostic log was not created.";
        var serviceText = string.IsNullOrWhiteSpace(serviceDiagnostics.Output)
            ? $"sc.exe queryex exit code: {serviceDiagnostics.ExitCode}"
            : serviceDiagnostics.Output.Trim();
        if (logText.Length > 3500) logText = logText[^3500..];
        throw new InvalidOperationException(
            $"WireGuard tunnel service did not reach RUNNING within 10 seconds. " +
            $"Service: {serviceName}{Environment.NewLine}{serviceText}" +
            $"{Environment.NewLine}WireGuard log:{Environment.NewLine}{logText}");
    }

    public async Task DisconnectAsync(ProfileItem profile, CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable()
            ?? throw new FileNotFoundException("WireGuard for Windows was not found.");
        var serviceName = "WireGuardTunnel$" + profile.TunnelName;
        if (!await ServiceExistsAsync(serviceName, cancellationToken)) return;

        await RunElevatedAsync(executable, new[] { "/uninstalltunnelservice", profile.TunnelName }, cancellationToken);
        var configPath = Path.Combine(ProgramDataDirectory, profile.TunnelName + ".conf");
        try { if (File.Exists(configPath)) File.Delete(configPath); } catch { }
    }

    public async Task<bool> IsConnectedAsync(ProfileItem profile, CancellationToken cancellationToken = default)
        => await IsServiceRunningAsync("WireGuardTunnel$" + profile.TunnelName, cancellationToken);

    private static async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken token)
    {
        var result = await RunAsync("sc.exe", new[] { "query", serviceName }, token);
        return result.ExitCode == 0;
    }

    private static async Task<bool> IsServiceRunningAsync(string serviceName, CancellationToken token)
    {
        var result = await RunAsync("sc.exe", new[] { "query", serviceName }, token);
        return result.ExitCode == 0 && result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RunElevatedWireGuardInstallAsync(
        string wireGuardExe, string configPath, string dataDirectory, string userSid,
        CancellationToken token)
    {
        // Apply ACLs and install the tunnel in one elevated PowerShell process,
        // so the user sees one UAC consent prompt rather than two.
        static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
        var command =
            "& { " +
            "& 'icacls.exe' " + PsQuote(dataDirectory) +
            " '/inheritance:r' '/grant:r' " +
            "'*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' " +
            PsQuote("*" + userSid + ":(OI)(CI)M") + " '/T' '/C'; " +
            "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; " +
            "& " + PsQuote(wireGuardExe) + " '/installtunnelservice' " +
            PsQuote(configPath) + "; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; " +
            // Wait for the tunnel to initialize, then dump its shared log while
            // still elevated. Running /dumplog unelevated causes a misleading
            // Access Denied because WireGuard protects its log under Program Files.
            "Start-Sleep -Seconds 11; " +
            "$diagPath = Join-Path " + PsQuote(dataDirectory) + " " + PsQuote(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(configPath)) + ".diagnostics.txt") + "; " +
            "$diag = & " + PsQuote(wireGuardExe) + " '/dumplog' 2>&1 | Out-String; " +
            "Set-Content -LiteralPath $diagPath -Value $diag -Encoding UTF8; " +
            "& 'icacls.exe' $diagPath '/grant:r' " + PsQuote("*" + userSid + ":(R)") + " | Out-Null; exit 0 }";

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the elevated WireGuard installation.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"WireGuard installation failed (exit code {process.ExitCode}). The elevated ACL or tunnel-service command failed.");
    }

    private static async Task RunElevatedAsync(string fileName, IEnumerable<string> arguments, CancellationToken token)
    {
        // Tunnel installation and private config ACL changes require an
        // administrator token. Request elevation only for this operation.
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the elevated WireGuard operation.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"The elevated operation failed with exit code {process.ExitCode}. Check WireGuard and Windows service logs.");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }
}
