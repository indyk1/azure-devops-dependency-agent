using Microsoft.Extensions.DependencyInjection;

namespace SampleLegacyApp.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSampleCore(this IServiceCollection services)
    {
        services.AddHttpClient("partner", client => client.Timeout = TimeSpan.FromSeconds(10));
        return services;
    }
}
