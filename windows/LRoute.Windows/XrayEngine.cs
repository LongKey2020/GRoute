using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LRoute.Windows;

public sealed class XrayEngine : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private readonly StringBuilder _log = new();
    private string? _runtimeConfigPath;
    private bool _tunMode;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _process is { HasExited: false };
        }
    }

    public bool IsTunMode
    {
        get { lock (_sync) return _tunMode && _process is { HasExited: false }; }
    }

    public string? FindExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "xray", "xray.exe");
        return File.Exists(path) ? path : null;
    }

    public async Task ConnectAsync(
        string configPath,
        bool tunMode = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException("The selected Xray JSON profile could not be found.", configPath);

        var executable = FindExecutable()
            ?? throw new FileNotFoundException("The bundled Xray-core runtime is missing. Reinstall the latest LRoute build.");

        if (tunMode && !File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "wintun.dll")))
            throw new FileNotFoundException("Wintun runtime is missing. The Windows Xray package must include wintun.dll.");

        string? staleRuntimeConfig;
        lock (_sync)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Xray-core is already running.");
            _process?.Dispose();
            _process = null;
            staleRuntimeConfig = _runtimeConfigPath;
            _runtimeConfigPath = null;
            _tunMode = false;
            _log.Clear();
        }
        if (staleRuntimeConfig is not null) TryDeleteRuntimeFiles(staleRuntimeConfig);

        var runtimeConfigPath = configPath;
        if (tunMode)
        {
            var runtimeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LRoute", "Runtime");
            runtimeConfigPath = await XrayTunConfigBuilder.CreateAsync(
                configPath, runtimeDirectory, cancellationToken);
        }

        try
        {
            await ValidateConfigAsync(executable, runtimeConfigPath, cancellationToken);
        }
        catch
        {
            if (tunMode) TryDeleteRuntimeFiles(runtimeConfigPath);
            throw;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = tunMode,
            CreateNoWindow = !tunMode,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("-config");
        start.ArgumentList.Add(runtimeConfigPath);

        if (tunMode)
        {
            // Xray must be elevated to create the Wintun adapter, system routes,
            // DNS settings, and Windows Filtering Platform leak-prevention rules.
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var processStarted = false;
        if (!tunMode)
        {
            process.OutputDataReceived += (_, e) => AppendLog(e.Data);
            process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        }
        process.Exited += (_, _) =>
        {
            lock (_sync)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _tunMode = false;
                }
            }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Windows could not start the Xray-core process.");
            processStarted = true;

            if (!tunMode)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            lock (_sync)
            {
                _process = process;
                _runtimeConfigPath = tunMode ? runtimeConfigPath : null;
                _tunMode = tunMode;
            }

            await Task.Delay(tunMode ? 2500 : 1200, cancellationToken);
            if (process.HasExited)
            {
                var details = tunMode ? await ReadTunLogAsync(runtimeConfigPath, cancellationToken) : GetRecentLog();
                var exitCode = process.ExitCode;
                lock (_sync)
                {
                    if (ReferenceEquals(_process, process)) _process = null;
                    _runtimeConfigPath = null;
                    _tunMode = false;
                }
                process.Dispose();
                if (tunMode) TryDeleteRuntimeFiles(runtimeConfigPath);
                throw new InvalidOperationException(
                    $"Xray-core exited during startup (exit code {exitCode}).{Environment.NewLine}{details}");
            }
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_process, process)) _process = null;
                _runtimeConfigPath = null;
                _tunMode = false;
            }
            if (processStarted && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            process.Dispose();
            if (tunMode) TryDeleteRuntimeFiles(runtimeConfigPath);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        Process? process;
        string? runtimeConfigPath;
        lock (_sync)
        {
            process = _process;
            _process = null;
            runtimeConfigPath = _runtimeConfigPath;
            _runtimeConfigPath = null;
            _tunMode = false;
        }

        if (process is not null)
        {
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

        if (runtimeConfigPath is not null) TryDeleteRuntimeFiles(runtimeConfigPath);
    }

    private static async Task ValidateConfigAsync(
        string executable, string configPath, CancellationToken token)
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

    private static async Task<string> ReadTunLogAsync(string configPath, CancellationToken token)
    {
        var logPath = configPath + ".error.log";
        if (!File.Exists(logPath)) return "No Xray TUN diagnostic log was created.";
        var text = await File.ReadAllTextAsync(logPath, token);
        return text.Length > 7000 ? text[^7000..] : text;
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

    private static void TryDeleteRuntimeFiles(string configPath)
    {
        try { if (File.Exists(configPath)) File.Delete(configPath); } catch { }
        try { if (File.Exists(configPath + ".error.log")) File.Delete(configPath + ".error.log"); } catch { }
    }

    public void Dispose()
    {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
    }
}
