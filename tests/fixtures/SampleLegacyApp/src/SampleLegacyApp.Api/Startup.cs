using SampleLegacyApp.Core;
using SampleLegacyApp.Core.Hashing;

namespace SampleLegacyApp.Api;

public sealed class Startup
{
    public void ConfigureServices(IServiceCollection services) => services.AddSampleCore();

    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/", () => "SampleLegacyApp");
            endpoints.MapGet("/hash/{password}", (string password) =>
                PasswordHasher.Hash(password, new byte[16]));
        });
    }
}
