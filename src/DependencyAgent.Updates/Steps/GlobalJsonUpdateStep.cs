using System.Globalization;
using System.Text;
using System.Text.Json;
using DependencyAgent.Core;
using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Updates.Steps;

/// <summary>Raises <c>sdk.version</c> in every global.json below the target major and relaxes <c>rollForward</c> when needed.</summary>
public sealed class GlobalJsonUpdateStep : IUpdateStep
{
    private static readonly HashSet<string> RelaxableRollForward = new(StringComparer.OrdinalIgnoreCase)
    {
        "disable", "patch", "latestPatch", "feature", "minor", "latestMinor",
    };

    public string Name => "global-json";

    public Task<UpdateStepResult> ApplyAsync(RepoContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Apply(context, cancellationToken));

    private static UpdateStepResult Apply(RepoContext context, CancellationToken ct)
    {
        var changes = new List<ChangeRecord>();
        var notes = new NoteList();

        if (!TargetFrameworkMapper.TryParseTarget(context.Settings.TargetFramework, out var targetMajor, out _))
        {
            notes.Add($"Target framework '{context.Settings.TargetFramework}' is not a modern .NET moniker; global-json step skipped");
            return new UpdateStepResult(changes, notes.ToList());
        }

        var layout = RepoLayout.Scan(context.WorkingDirectory);
        foreach (var relative in layout.GlobalJsonFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(context.WorkingDirectory, relative);
            try
            {
                ProcessFile(fullPath, relative, targetMajor, changes, notes, context.Logger);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                notes.Add($"Could not update {relative}: {ex.Message}");
            }
        }

        return new UpdateStepResult(changes, notes.ToList());
    }

    private readonly record struct Token(int Start, int End, string? Value, bool IsString);

    private readonly record struct Splice(int Start, int End, string Text);

    private static void ProcessFile(string fullPath, string relative, int targetMajor, List<ChangeRecord> changes, NoteList notes, ILogger logger)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var bomLength = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var content = bytes.AsSpan(bomLength).ToArray();

        var (version, versionNameStart, rollForward) = Locate(content);
        if (version is not { IsString: true, Value: { } oldVersion })
        {
            return;
        }

        var dot = oldVersion.IndexOf('.');
        if (dot <= 0 || !int.TryParse(oldVersion[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major >= targetMajor)
        {
            return;
        }

        var newVersion = $"{targetMajor}.0.100";
        var splices = new List<Splice> { new(version.Value.Start, version.Value.End, $"\"{newVersion}\"") };
        changes.Add(new ChangeRecord(ChangeKind.Sdk, relative, "sdk.version", oldVersion, newVersion));

        var newLine = ContainsCrLf(content) ? "\r\n" : "\n";
        if (rollForward is null)
        {
            splices.Add(new Splice(version.Value.End, version.Value.End, InsertProperty(content, versionNameStart, newLine, "rollForward", "latestFeature")));
            changes.Add(new ChangeRecord(ChangeKind.Sdk, relative, "sdk.rollForward", null, "latestFeature"));
        }
        else if (rollForward.Value is { IsString: true, Value: { } oldRoll } && RelaxableRollForward.Contains(oldRoll))
        {
            splices.Add(new Splice(rollForward.Value.Start, rollForward.Value.End, "\"latestFeature\""));
            changes.Add(new ChangeRecord(ChangeKind.Sdk, relative, "sdk.rollForward", oldRoll, "latestFeature"));
        }

        var output = new List<byte>(content);
        foreach (var s in splices.OrderByDescending(s => s.Start))
        {
            output.RemoveRange(s.Start, s.End - s.Start);
            output.InsertRange(s.Start, Encoding.UTF8.GetBytes(s.Text));
        }

        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bomLength);
        stream.Write(output.ToArray());
        logger.LogInformation("Updated {File}: sdk.version {From} -> {To}", relative, oldVersion, newVersion);
    }

    private static bool ContainsCrLf(byte[] content)
    {
        for (var i = 0; i + 1 < content.Length; i++)
        {
            if (content[i] == '\r' && content[i + 1] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Text to insert directly after the version value: a new sibling property, using the same indentation as the
    /// version property (or an inline form when the object is on one line).
    /// </summary>
    private static string InsertProperty(byte[] content, int nameStart, string newLine, string name, string value)
    {
        var lineStart = nameStart;
        while (lineStart > 0 && content[lineStart - 1] is not (byte)'\n' and not (byte)'\r')
        {
            lineStart--;
        }

        var indent = Encoding.UTF8.GetString(content, lineStart, nameStart - lineStart);
        return indent.Length > 0 && indent.All(c => c is ' ' or '\t')
            ? $",{newLine}{indent}\"{name}\": \"{value}\""
            : lineStart == nameStart && nameStart > 0
                ? $",{newLine}\"{name}\": \"{value}\""
                : $", \"{name}\": \"{value}\"";
    }

    private static (Token? Version, int VersionNameStart, Token? RollForward) Locate(byte[] content)
    {
        var reader = new Utf8JsonReader(content, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Token? version = null;
        Token? rollForward = null;
        var versionNameStart = 0;
        var inSdk = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && inSdk && reader.CurrentDepth == 1)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var depth = reader.CurrentDepth;
            var name = reader.GetString()!;
            var nameStart = (int)reader.TokenStartIndex;
            if (!reader.Read())
            {
                break;
            }

            if (depth == 1 && name.Equals("sdk", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartObject)
            {
                inSdk = true;
                continue;
            }

            if (inSdk && depth == 2 && reader.TokenType != JsonTokenType.StartObject && reader.TokenType != JsonTokenType.StartArray)
            {
                var isString = reader.TokenType == JsonTokenType.String;
                var span = new Token((int)reader.TokenStartIndex, (int)reader.BytesConsumed, isString ? reader.GetString() : null, isString);
                if (name.Equals("version", StringComparison.OrdinalIgnoreCase))
                {
                    version = span;
                    versionNameStart = nameStart;
                }
                else if (name.Equals("rollForward", StringComparison.OrdinalIgnoreCase))
                {
                    rollForward = span;
                }

                continue;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return (version, versionNameStart, rollForward);
    }
}
