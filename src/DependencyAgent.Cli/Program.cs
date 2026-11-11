using DependencyAgent.Cli;
using DependencyAgent.LearnProxy;

// `learn-proxy` speaks MCP over stdout, so it must run before anything else writes to the console.
if (args.Length > 0 && string.Equals(args[0], "learn-proxy", StringComparison.OrdinalIgnoreCase))
{
    return await LearnProxyServer.RunAsync(args[1..], CancellationToken.None);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var commandLine = CommandLine.Parse(args, Commands.Flags);
    return commandLine.Command switch
    {
        "plan" => await Commands.PlanAsync(commandLine, Console.Out),
        "run" => await Commands.RunAsync(commandLine, Console.Out, cts.Token),
        "report" => await Commands.ReportAsync(commandLine, Console.Out, cts.Token),
        "validate-config" => Commands.ValidateConfig(commandLine, Console.Out),
        _ => Commands.Help(Console.Out),
    };
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync($"error: {ex.Message}");
    Commands.Help(Console.Error);
    return 2;
}
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
{
    await Console.Error.WriteLineAsync($"error: {ex.Message}");
    return 2;
}
