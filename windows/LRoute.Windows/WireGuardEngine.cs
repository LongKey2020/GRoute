using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LRoute.Windows;

public sealed class WireGuardEngine
{
    private static readonly string ProgramDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
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
        var configPath = Path.Combine(ProgramDataDirectory, profile.TunnelName + ".conf.dpapi");
        var plainConfig = await File.ReadAllBytesAsync(profile.FilePath, cancellationToken);
        var entropy = Encoding.UTF8.GetBytes(profile.TunnelName);
        var encryptedConfig = ProtectedData.Protect(plainConfig, entropy, DataProtectionScope.LocalMachine);
        await File.WriteAllBytesAsync(configPath, encryptedConfig, cancellationToken);

        await RunElevatedAsync("icacls.exe", new[]
        {
            ProgramDataDirectory, "/inheritance:r", "/grant:r",
            "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F",
            "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + ":(OI)(CI)M"
        }, cancellationToken);

        await RunElevatedAsync(executable, new[] { "/installtunnelservice", configPath }, cancellationToken);
        var serviceName = "WireGuardTunnel$" + profile.TunnelName;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await IsServiceRunningAsync(serviceName, cancellationToken)) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new InvalidOperationException("WireGuard did not report the tunnel service as Running within 10 seconds. Check the WireGuard log.");
    }

    public async Task DisconnectAsync(ProfileItem profile, CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable()
            ?? throw new FileNotFoundException("WireGuard for Windows was not found.");
        var serviceName = "WireGuardTunnel$" + profile.TunnelName;
        if (!await ServiceExistsAsync(serviceName, cancellationToken)) return;

        await RunElevatedAsync(executable, new[] { "/uninstalltunnelservice", profile.TunnelName }, cancellationToken);
        var configPath = Path.Combine(ProgramDataDirectory, profile.TunnelName + ".conf.dpapi");
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

    private static async Task RunElevatedAsync(string fileName, IEnumerable<string> arguments, CancellationToken token)
    {
        // Tunnel installation and protected ProgramData ACL changes require an
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
