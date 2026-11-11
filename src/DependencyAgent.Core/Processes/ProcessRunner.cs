using System.Collections;
using System.Diagnostics;
using System.Text;

namespace DependencyAgent.Core.Processes;

/// <summary>
/// Which credentials a child process may inherit. Everything that looks like a secret is removed
/// from the child environment unless the scope explicitly allows that category.
/// </summary>
public enum SecretScope
{
    /// <summary>No secrets at all. Use for anything that executes repository code (build, test) or untrusted tools.</summary>
    None,

    /// <summary>NuGet feed credentials only (dotnet restore, package metadata queries).</summary>
    NuGetFeeds,

    /// <summary>Azure DevOps job token only (git clone/push). Prefer passing the token via arguments/headers.</summary>
    AzureDevOps,

    /// <summary>AI provider credentials only (Copilot CLI / OpenAI).</summary>
    AiProvider,
}

public static class SecretEnvironment
{
    public static readonly IReadOnlySet<string> AzureDevOpsVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SYSTEM_ACCESSTOKEN", "AZURE_DEVOPS_EXT_PAT", "ADO_PAT", "AZURE_DEVOPS_TOKEN",
    };

    public static readonly IReadOnlySet<string> NuGetVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "VSS_NUGET_ACCESSTOKEN", "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS", "NUGET_AUTH_TOKEN",
        "ARTIFACTS_CREDENTIALPROVIDER_FEED_ENDPOINTS",
    };

    public static readonly IReadOnlySet<string> AiVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN", "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY",
    };

    private static readonly string[] SecretLikeSuffixes = ["TOKEN", "_PAT", "_KEY", "SECRET", "PASSWORD", "PASSWD", "_PWD", "CONNECTIONSTRING", "CREDENTIALS"];

    /// <summary>
    /// Reads a secret from the environment. Returns null when unset, empty, or an unexpanded Azure Pipelines
    /// macro such as <c>$(OPENAI_API_KEY)</c> (what a pipeline passes when the variable is not defined).
    /// </summary>
    public static string? GetSecret(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("$(", StringComparison.Ordinal) ? null : value;
    }

    /// <summary>True if the variable name is (or looks like) a credential.</summary>
    public static bool IsSecret(string name) =>
        AzureDevOpsVariables.Contains(name) || NuGetVariables.Contains(name) || AiVariables.Contains(name)
        || SecretLikeSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    public static bool IsAllowed(string name, SecretScope scope, IEnumerable<string>? extraAllowed = null)
    {
        if (!IsSecret(name))
        {
            return true;
        }

        if (extraAllowed is not null && extraAllowed.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return scope switch
        {
            SecretScope.NuGetFeeds => NuGetVariables.Contains(name),
            SecretScope.AzureDevOps => AzureDevOpsVariables.Contains(name),
            SecretScope.AiProvider => AiVariables.Contains(name),
            _ => false,
        };
    }
}

public sealed class ProcessSpec
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public required string WorkingDirectory { get; init; }

    public SecretScope SecretScope { get; init; } = SecretScope.None;

    /// <summary>Additional secret variable names allowed through (e.g. a custom token variable name from settings).</summary>
    public IReadOnlyList<string> AllowedSecretVariables { get; init; } = [];

    /// <summary>Variables to set (null value = remove).</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public string? StandardInput { get; init; }

    /// <summary>Values masked as *** in captured output (e.g. tokens passed as arguments).</summary>
    public IReadOnlyList<string> MaskValues { get; init; } = [];

    /// <summary>Optional live line callback (already masked).</summary>
    public Action<string>? OnOutputLine { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    public string CombinedOutput => string.IsNullOrEmpty(StandardError) ? StandardOutput : StandardOutput + "\n" + StandardError;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

/// <summary>Runs child processes with a scrubbed environment, timeout and output masking.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StandardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in spec.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        ApplyEnvironment(psi.Environment, spec);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data, spec);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data, spec);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {spec.FileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (spec.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(spec.Timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            if (!timedOut)
            {
                throw;
            }
        }

        // Flush async readers.
        process.WaitForExit();
        sw.Stop();
        lock (stdout)
        {
            lock (stderr)
            {
                return new ProcessResult(timedOut ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut, sw.Elapsed);
            }
        }
    }

    /// <summary>Applies scoped secret scrubbing then explicit overrides. Public for testing.</summary>
    public static void ApplyEnvironment(IDictionary<string, string?> environment, ProcessSpec spec)
    {
        foreach (var key in environment.Keys.ToList())
        {
            if (!SecretEnvironment.IsAllowed(key, spec.SecretScope, spec.AllowedSecretVariables))
            {
                environment.Remove(key);
            }
        }

        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        environment["GIT_TERMINAL_PROMPT"] = "0";

        foreach (var (key, value) in spec.Environment)
        {
            if (value is null)
            {
                environment.Remove(key);
            }
            else
            {
                environment[key] = value;
            }
        }
    }

    /// <summary>Current process environment snapshot (for tests / callers that need to inspect it).</summary>
    public static Dictionary<string, string?> CurrentEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
        {
            result[(string)e.Key] = (string?)e.Value;
        }

        return result;
    }

    private static void Append(StringBuilder sb, string? line, ProcessSpec spec)
    {
        if (line is null)
        {
            return;
        }

        // Child output (which may come from repository code) must never be able to issue Azure Pipelines
        // logging commands such as ##vso[task.setvariable] if it is echoed to the agent log.
        line = PipelineCommandNeutralizer.Neutralize(line);

        foreach (var mask in spec.MaskValues)
        {
            if (!string.IsNullOrEmpty(mask))
            {
                line = line.Replace(mask, "***", StringComparison.Ordinal);
            }
        }

        lock (sb)
        {
            sb.AppendLine(line);
        }

        spec.OnOutputLine?.Invoke(line);
    }
}

public static class PipelineCommandNeutralizer
{
    /// <summary>Breaks <c>##vso[</c> and <c>##[</c> logging-command prefixes so the agent will not interpret them.</summary>
    public static string Neutralize(string text) =>
        text.Replace("##vso[", "#\u200B#vso[", StringComparison.OrdinalIgnoreCase)
            .Replace("##[", "#\u200B#[", StringComparison.Ordinal);
}
