using System.Diagnostics;
using System.IO;
using System.Text;

namespace LRoute.Windows;

public sealed class XrayEngine : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private readonly StringBuilder _log = new();

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _process is { HasExited: false };
        }
    }

    public string? FindExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "xray", "xray.exe");
        return File.Exists(path) ? path : null;
    }

    public async Task ConnectAsync(string configPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException("The selected Xray JSON profile could not be found.", configPath);

        var executable = FindExecutable()
            ?? throw new FileNotFoundException("The bundled Xray-core runtime is missing. Reinstall the latest LRoute build.");

        await ValidateConfigAsync(executable, configPath, cancellationToken);

        lock (_sync)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Xray-core is already running.");
            _log.Clear();
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("-config");
        start.ArgumentList.Add(configPath);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        process.Exited += (_, _) => { lock (_sync) { if (ReferenceEquals(_process, process)) _process = null; } };

        if (!process.Start())
            throw new InvalidOperationException("Windows could not start the Xray-core process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_sync) _process = process;

        await Task.Delay(1200, cancellationToken);
        if (process.HasExited)
        {
            var details = GetRecentLog();
            lock (_sync) { if (ReferenceEquals(_process, process)) _process = null; }
            var exitCode = process.ExitCode;
            process.Dispose();
            throw new InvalidOperationException(
                $"Xray-core exited during startup (exit code {exitCode}).{Environment.NewLine}{details}");
        }
    }

    public async Task DisconnectAsync()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task ValidateConfigAsync(string executable, string configPath, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("-test");
        start.ArgumentList.Add("-config");
        start.ArgumentList.Add(configPath);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Xray-core configuration validation.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Xray-core rejected this configuration (exit code {process.ExitCode}).{Environment.NewLine}{output.Trim()}");
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_sync)
        {
            _log.AppendLine(line);
            if (_log.Length > 12000) _log.Remove(0, _log.Length - 12000);
        }
    }

    private string GetRecentLog()
    {
        lock (_sync) return _log.ToString().Trim();
    }

    public void Dispose()
    {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
    }
}
