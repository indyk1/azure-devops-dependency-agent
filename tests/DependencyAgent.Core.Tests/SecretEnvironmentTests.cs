using DependencyAgent.Core.Processes;

namespace DependencyAgent.Core.Tests;

public class SecretEnvironmentTests
{
    [Theory]
    [InlineData("SYSTEM_ACCESSTOKEN", true)]
    [InlineData("system_accesstoken", true)]
    [InlineData("COPILOT_GITHUB_TOKEN", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("VSS_NUGET_EXTERNAL_FEED_ENDPOINTS", true)]
    [InlineData("MY_SERVICE_PASSWORD", true)]
    [InlineData("AWS_SECRET", true)]
    [InlineData("DB_CONNECTIONSTRING", true)]
    [InlineData("SOMETHING_PAT", true)]
    [InlineData("PATH", false)]
    [InlineData("HOME", false)]
    [InlineData("DOTNET_NOLOGO", false)]
    [InlineData("BUILD_BUILDID", false)]
    [InlineData("TOKENIZER_MODE", false)]
    public void IsSecret_classifies_names(string name, bool expected) =>
        Assert.Equal(expected, SecretEnvironment.IsSecret(name));

    [Theory]
    [InlineData("PATH", SecretScope.None, true)]
    [InlineData("SYSTEM_ACCESSTOKEN", SecretScope.None, false)]
    [InlineData("SYSTEM_ACCESSTOKEN", SecretScope.NuGetFeeds, false)]
    [InlineData("SYSTEM_ACCESSTOKEN", SecretScope.AiProvider, false)]
    [InlineData("SYSTEM_ACCESSTOKEN", SecretScope.AzureDevOps, true)]
    [InlineData("VSS_NUGET_ACCESSTOKEN", SecretScope.NuGetFeeds, true)]
    [InlineData("VSS_NUGET_ACCESSTOKEN", SecretScope.AiProvider, false)]
    [InlineData("VSS_NUGET_ACCESSTOKEN", SecretScope.AzureDevOps, false)]
    [InlineData("VSS_NUGET_ACCESSTOKEN", SecretScope.None, false)]
    [InlineData("COPILOT_GITHUB_TOKEN", SecretScope.AiProvider, true)]
    [InlineData("OPENAI_API_KEY", SecretScope.AiProvider, true)]
    [InlineData("COPILOT_GITHUB_TOKEN", SecretScope.AzureDevOps, false)]
    [InlineData("COPILOT_GITHUB_TOKEN", SecretScope.NuGetFeeds, false)]
    [InlineData("COPILOT_GITHUB_TOKEN", SecretScope.None, false)]
    [InlineData("UNKNOWN_SECRET", SecretScope.AiProvider, false)]
    public void IsAllowed_respects_scope(string name, SecretScope scope, bool expected) =>
        Assert.Equal(expected, SecretEnvironment.IsAllowed(name, scope));

    [Fact]
    public void IsAllowed_honours_extra_allowed_names_case_insensitively()
    {
        Assert.True(SecretEnvironment.IsAllowed("MY_CUSTOM_TOKEN", SecretScope.AiProvider, ["my_custom_token"]));
        Assert.False(SecretEnvironment.IsAllowed("MY_CUSTOM_TOKEN", SecretScope.AiProvider, ["OTHER_TOKEN"]));
        Assert.True(SecretEnvironment.IsAllowed("MY_CUSTOM_TOKEN", SecretScope.None, ["MY_CUSTOM_TOKEN"]));
    }

    [Fact]
    public void GetSecret_returns_value_when_set()
    {
        const string name = "DA_TEST_SECRET_SET_TOKEN";
        Environment.SetEnvironmentVariable(name, "s3cret");
        try
        {
            Assert.Equal("s3cret", SecretEnvironment.GetSecret(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("$(OPENAI_API_KEY)")]
    [InlineData("$(COPILOT_GITHUB_TOKEN)")]
    public void GetSecret_treats_unset_blank_and_unexpanded_macros_as_unset(string? value)
    {
        const string name = "DA_TEST_SECRET_UNSET_TOKEN";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            Assert.Null(SecretEnvironment.GetSecret(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
