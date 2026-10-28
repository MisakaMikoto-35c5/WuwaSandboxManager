using Volo.Abp;
using Volo.Abp.Autofac;
using Wsm.Cli;

// ABP-hosted console entry point. The application graph is built from
// CliModule, then the manual argument parser in CliApplication drives the
// use cases through ISandboxManager.
//
// --quiet (desktop shortcuts) hides the console before anything is built, so
// the window never flashes; failures surface in a message box instead.
if (args.Any(arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase)))
{
    ConsoleWindow.Hide();
}

var app = await AbpApplicationFactory.CreateAsync<CliModule>(options => options.UseAutofac());
await app.InitializeAsync();
try
{
    return await new CliApplication(app.ServiceProvider).RunAsync(args);
}
finally
{
    await app.ShutdownAsync();
    app.Dispose();
}
