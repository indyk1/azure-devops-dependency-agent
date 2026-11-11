using Microsoft.AspNetCore;

namespace SampleLegacyApp.Api;

public static class Program
{
    public static void Main(string[] args) => CreateWebHostBuilder(args).Build().Run();

    // Classic ASP.NET Core 2.x/3.x style host bootstrap.
    public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
        WebHost.CreateDefaultBuilder(args).UseStartup<Startup>();
}
