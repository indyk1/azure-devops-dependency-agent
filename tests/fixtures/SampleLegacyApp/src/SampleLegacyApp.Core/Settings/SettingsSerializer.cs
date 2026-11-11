using System.Text.Json;
using Newtonsoft.Json;

namespace SampleLegacyApp.Core.Settings;

public sealed record AppSettings(string Name, int RetryCount);

/// <summary>Serialises settings with both serialisers (the legacy store still uses Newtonsoft.Json).</summary>
public static class SettingsSerializer
{
    public static string ToJson(AppSettings settings) =>
        System.Text.Json.JsonSerializer.Serialize(settings);

    public static AppSettings? FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

    public static string ToLegacyJson(AppSettings settings) =>
        JsonConvert.SerializeObject(settings, Formatting.None);

    public static AppSettings? FromLegacyJson(string json) =>
        JsonConvert.DeserializeObject<AppSettings>(json);
}
