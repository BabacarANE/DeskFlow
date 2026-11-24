using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Desk.IntegrationTests.Support;

public sealed record ProcessResult(int ExitCode, string Output, string Error, TimeSpan Elapsed);

/// <summary>Runs the real <c>Desk.Worker.dll</c> (built by the project reference) as the container would: <c>dotnet Desk.Worker.dll [args]</c>.</summary>
public sealed class WorkerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private WorkerProcess(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public int Port { get; }

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private static string WorkerDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent!.Name; // .../bin/{Configuration}/net10.0/
        var root = directory;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DeskFlow.slnx")))
        {
            root = root.Parent;
        }

        return Path.Combine(root!.FullName, "src", "Desk.Worker", "bin", configuration, "net10.0");
    }

    private static ProcessStartInfo StartInfo(IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = WorkerDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("Desk.Worker.dll");
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        return info;
    }

    /// <summary>A one-shot command (replay-dlq): waits for it to exit.</summary>
    public static async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment, int timeoutSeconds = 60)
    {
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(StartInfo(arguments, environment))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"The Worker command did not exit within {timeoutSeconds}s. Output: {await output}");
        }

        return new ProcessResult(process.ExitCode, await output, await error, clock.Elapsed);
    }

    /// <summary>The long-running Worker (consumers, dispatcher, health endpoint on a free port).</summary>
    public static WorkerProcess Start(IReadOnlyDictionary<string, string> environment)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var env = new Dictionary<string, string>(environment) { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" };
        var process = Process.Start(StartInfo([], env))!;
        var worker = new WorkerProcess(process, port);
        process.OutputDataReceived += (_, e) => worker.Append(e.Data);
        process.ErrorDataReceived += (_, e) => worker.Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return worker;
    }

    private void Append(string? line)
    {
        if (line is not null)
        {
            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }
}
