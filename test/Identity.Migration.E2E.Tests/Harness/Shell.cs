using System.Diagnostics;
using System.Text;

namespace Identity.Migration.E2E.Tests.Harness;

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr)
{
    public string Combined => StdOut + StdErr;
}

public static class Shell
{
    /// <summary>Runs a bash script or inline bash (-c) with the given environment, capturing output.</summary>
    public static async Task<ShellResult> BashAsync(string command, IDictionary<string, string?>? env = null, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("bash", ["-c", command])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"bash command timed out: {command}");
        }

        return new ShellResult(process.ExitCode, await stdout, await stderr);
    }

    public static async Task<ShellResult> RunAsync(string fileName, string[] args, IDictionary<string, string?>? env = null, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {string.Join(' ', args)} timed out");
        }

        return new ShellResult(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>A long-running `dotnet <app>.dll` host (monolith, Identity.API, ApiGateway) with captured logs.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly Process process;
    private readonly StringBuilder output = new();
    private readonly Lock sync = new();

    public string Name { get; }
    public Uri BaseUrl { get; }
    public string Output { get { lock (sync) return output.ToString(); } }
    public bool HasExited => process.HasExited;

    private AppHost(string name, Uri baseUrl, Process process)
    {
        Name = name;
        BaseUrl = baseUrl;
        this.process = process;
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (sync) output.AppendLine(line);
    }

    public static async Task<AppHost> StartAsync(string name, string dllPath, int port, IDictionary<string, string?> env,
        string readinessPath, string[]? extraArgs = null, TimeSpan? timeout = null, bool https = false)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"{name}: {dllPath} not found - build it first.", dllPath);

        var baseUrl = new Uri($"{(https ? "https" : "http")}://127.0.0.1:{port}/");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dllPath)!
        };
        psi.ArgumentList.Add(dllPath);
        foreach (var a in extraArgs ?? []) psi.ArgumentList.Add(a);
        psi.Environment["ASPNETCORE_URLS"] = baseUrl.ToString().TrimEnd('/');
        psi.Environment["DOTNET_ENVIRONMENT"] = "Production";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        foreach (var (k, v) in env) psi.Environment[k] = v;

        var host = new AppHost(name, baseUrl, Process.Start(psi)!);
        await host.WaitUntilReadyAsync(readinessPath, timeout ?? TimeSpan.FromMinutes(3));
        return host;
    }

    private async Task WaitUntilReadyAsync(string path, TimeSpan timeout)
    {
        using var http = NewInsecureClient(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"{Name} exited with code {process.ExitCode} during startup:\n{Output}");
            try
            {
                using var response = await http.GetAsync(new Uri(BaseUrl, path));
                // Any HTTP answer (200 healthz, 401 on a protected endpoint) means Kestrel + the pipeline are up.
                if ((int)response.StatusCode < 500)
                    return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }

            await Task.Delay(500);
        }

        throw new TimeoutException($"{Name} did not become ready at {BaseUrl}{path} within {timeout}:\n{Output}");
    }

    /// <summary>HttpClient trusting the self-signed Kestrel certificate the monolith is started with.</summary>
    public static HttpClient NewInsecureClient(TimeSpan? timeout = null) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    { Timeout = timeout ?? TimeSpan.FromSeconds(100) };

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { }
        process.Dispose();
    }
}
