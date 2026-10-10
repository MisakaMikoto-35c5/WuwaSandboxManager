using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WindowsSandboxManager.Application;
using WindowsSandboxManager.Cli;
using WindowsSandboxManager.Infrastructure;

var services = new ServiceCollection();
services.AddLogging(builder => builder
    .AddSimpleConsole(options => options.SingleLine = true)
    .SetMinimumLevel(LogLevel.Warning));
services.AddSandboxApplication();
services.AddSandboxInfrastructure();

await using var provider = services.BuildServiceProvider();
return await Cli.RunAsync(provider, args).ConfigureAwait(false);
