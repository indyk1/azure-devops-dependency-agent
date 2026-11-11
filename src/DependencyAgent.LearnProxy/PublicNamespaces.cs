namespace DependencyAgent.LearnProxy;

/// <summary>Namespace roots and well-known BCL/ASP.NET type names that are public and safe to send to a docs service.</summary>
public static class PublicNamespaces
{
    /// <summary>Root namespaces of public Microsoft and widely used third-party libraries.</summary>
    public static readonly IReadOnlySet<string> Roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Microsoft", "Azure", "Windows", "Newtonsoft", "Serilog", "Polly", "Xunit", "NUnit", "MSTest", "Dapper",
        "AutoMapper", "MediatR", "Swashbuckle", "FluentValidation", "StackExchange", "Npgsql", "Grpc", "Google", "Moq",
        "NSubstitute", "FluentAssertions", "Bogus", "RestSharp", "Refit", "Humanizer", "CsvHelper", "Hangfire", "Quartz",
        "MassTransit", "NLog", "log4net", "Castle", "MySql", "MySqlConnector", "Oracle", "EntityFramework", "Amazon", "AWSSDK",
        "Autofac", "SimpleInjector", "Ninject", "Owin", "IdentityModel", "IdentityServer4", "Duende", "OpenTelemetry",
        "Confluent", "RabbitMQ", "NodaTime", "Markdig", "HtmlAgilityPack", "ClosedXML", "BenchmarkDotNet", "Testcontainers",
        "WireMock", "AngleSharp", "SixLabors", "SkiaSharp", "Elasticsearch", "MongoDB", "Lamar", "Scrutor", "Ardalis",
        "Asp", "Blazor", "Mono", "Xamarin", "Android", "Foundation", "UIKit", "Avalonia", "CommunityToolkit", "Prism",
    };

    /// <summary>Well-known public type names used as the first segment of <c>Type.Member</c> references.</summary>
    public static readonly IReadOnlySet<string> WellKnownTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Task", "ValueTask", "String", "Console", "Math", "Convert", "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "TimeProvider",
        "Enum", "Type", "Activator", "Environment", "Path", "File", "Directory", "Uri", "HttpClient", "Stream", "Encoding",
        "Assembly", "Array", "List", "Dictionary", "Enumerable", "Interlocked", "Thread", "ThreadPool", "Process", "GC",
        "Regex", "Marshal", "Span", "Memory", "Buffer", "BitConverter", "Random", "RandomNumberGenerator",
        "IServiceCollection", "IApplicationBuilder", "WebApplication", "WebApplicationBuilder", "IHostBuilder", "Host",
        "HttpContext", "HttpRequest", "HttpResponse", "ILogger", "ILoggerFactory", "DbContext", "DbSet", "IConfiguration",
        "ConfigurationBuilder", "IOptions", "JsonSerializer", "ControllerBase", "Controller", "ServiceCollectionServiceExtensions",
        "Rfc2898DeriveBytes", "SHA256", "SHA1", "MD5", "Aes", "RSA", "X509Certificate2", "WebHost", "Startup",
    };
}
