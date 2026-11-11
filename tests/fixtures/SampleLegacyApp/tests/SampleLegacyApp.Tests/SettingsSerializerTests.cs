using SampleLegacyApp.Core.Settings;

namespace SampleLegacyApp.Tests;

public class SettingsSerializerTests
{
    private static readonly AppSettings Sample = new("demo", 3);

    [Fact]
    public void System_text_json_output_is_stable()
    {
        Assert.Equal("""{"Name":"demo","RetryCount":3}""", SettingsSerializer.ToJson(Sample));
        Assert.Equal(Sample, SettingsSerializer.FromJson("""{"Name":"demo","RetryCount":3}"""));
    }

    [Fact]
    public void Newtonsoft_output_is_stable()
    {
        Assert.Equal("""{"Name":"demo","RetryCount":3}""", SettingsSerializer.ToLegacyJson(Sample));
        Assert.Equal(Sample, SettingsSerializer.FromLegacyJson("""{"Name":"demo","RetryCount":3}"""));
    }
}
