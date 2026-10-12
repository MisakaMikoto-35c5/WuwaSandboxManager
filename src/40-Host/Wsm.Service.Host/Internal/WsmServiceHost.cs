using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Wsm.Persistence;

namespace Wsm.Service.Host.Internal;

/// <summary>
/// Builds and runs the WSM agent host: an ABP application graph (so the service
/// owns the engines and the sandbox VMs) fronted by Kestrel serving gRPC over a
/// Windows named pipe.
/// </summary>
/// <remarks>
/// The transport mirrors Microsoft's own <c>WindowsSandboxServer.exe</c>: HTTP/2
/// over a named pipe. The pipe name is currently fixed (see the plan §8), so it
/// can be extended later with per-user suffixes / ACLs.
/// </remarks>
public static class WsmServiceHost
{
    /// <summary>The named pipe the agent listens on (<c>\\.\pipe\wsm-agent</c>).</summary>
    public const string PipeName = "wsm-agent";

    /// <summary>The command-line switch that overrides the config &amp; data folder.</summary>
    public const string DataFolderOption = "--data";

    /// <summary>
    /// The result of scanning the command line for <see cref="DataFolderOption"/>.
    /// </summary>
    /// <param name="Requested">Whether the <c>--data</c> switch was present.</param>
    /// <param name="Folder">
    /// The value supplied after the switch, or <c>null</c> when the switch had no
    /// following value.
    /// </param>
    public readonly record struct DataFolderArgument(bool Requested, string? Folder);

    /// <summary>
    /// Scans <paramref name="args"/> (position-independent) for
    /// <c>--data &lt;folder&gt;</c> and reports whether it was present and its value.
    /// </summary>
    /// <remarks>
    /// <c>wsm service install</c> appends <c>--data "&lt;user's WsmContainers
    /// folder&gt;"</c> to the service <c>binPath</c> so the LocalSystem service
    /// reads and writes the <em>user's</em> <c>.wsm</c> definition folder instead
    /// of the LocalSystem profile's <c>%USERPROFILE%</c> (<c>systemprofile</c>),
    /// which would otherwise be the default.
    /// </remarks>
    public static DataFolderArgument ParseDataFolder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], DataFolderOption, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            return new DataFolderArgument(true, hasValue ? args[i + 1] : null);
        }

        return default;
    }

    /// <summary>
    /// Builds the host with the ABP container and the gRPC endpoint wired up. The
    /// caller owns the returned <see cref="WebApplication"/> and should dispose it.
    /// </summary>
    /// <param name="args">The raw command line (forwarded to the web host).</param>
    /// <param name="dataFolder">
    /// The optional <c>--data</c> override for the config &amp; data folder. When
    /// absent or invalid the default (<c>%USERPROFILE%\Documents\WsmContainers</c>)
    /// is used and a warning is logged.
    /// </param>
    public static async Task<WebApplication> BuildAsync(string[] args, DataFolderArgument dataFolder = default)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Use Autofac as the container (the external-provider counterpart of the
        // CLI's CreateAsync(..., o => o.UseAutofac())) and load the ABP module
        // graph. AbpAutofacModule is pulled in by WsmServiceModule's DependsOn.
        builder.Host.UseAutofac();
        builder.Services.AddApplication<WsmServiceModule>(_ => { });

        // Point the persistence layer (FileSandboxRepository reads
        // IOptions<SandboxPersistenceOptions>) at the folder supplied on the command
        // line. Same mechanism the WinUI host uses (Configure<SandboxPersistenceOptions>).
        var configDataFolder = ResolveConfigDataFolder(dataFolder, out var dataFolderWarning);
        if (configDataFolder is not null)
        {
            builder.Services.Configure<SandboxPersistenceOptions>(
                options => options.ConfigDataFolder = configDataFolder);
        }

        builder.Services.AddGrpc();
        builder.WebHost.ConfigureKestrel(options => options.ListenNamedPipe(
            PipeName,
            lo => lo.Protocols = HttpProtocols.Http2));

        var app = builder.Build();

        // Run ABP module initialization against the host's (Autofac) provider.
        await app.Services
            .GetRequiredService<IAbpApplicationWithExternalServiceProvider>()
            .InitializeAsync(app.Services);

        if (dataFolderWarning is not null)
        {
            app.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Wsm.Service.Host")
                .LogWarning("{Warning}", dataFolderWarning);
        }

        app.MapGrpcService<WsmServiceGrpc>();

        return app;
    }

    /// <summary>Runs the agent in the foreground (console / development mode).</summary>
    public static async Task RunConsoleAsync(string[] args, DataFolderArgument dataFolder = default)
    {
        var app = await BuildAsync(args, dataFolder);

        app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Wsm.Service.Host")
            .LogInformation(@"Wsm agent listening on \\.\pipe\{PipeName} (console mode).", PipeName);

        await app.RunAsync();
    }

    /// <summary>
    /// Validates the <c>--data</c> override and returns the folder to configure,
    /// or <c>null</c> to keep the default. A missing or non-absolute folder is
    /// reported through <paramref name="warning"/> (logged by the caller once the
    /// host is built) so a bad argument never stops the service from starting.
    /// </summary>
    private static string? ResolveConfigDataFolder(DataFolderArgument dataFolder, out string? warning)
    {
        warning = null;

        if (!dataFolder.Requested)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dataFolder.Folder))
        {
            warning =
                $"'{DataFolderOption}' was given without a folder; using the default config & data folder " +
                $"({SandboxPersistenceOptions.DefaultConfigDataFolder}).";
            return null;
        }

        if (!Path.IsPathFullyQualified(dataFolder.Folder))
        {
            warning =
                $"Ignoring '{DataFolderOption}' folder '{dataFolder.Folder}': an absolute path is required. " +
                $"Using the default config & data folder ({SandboxPersistenceOptions.DefaultConfigDataFolder}).";
            return null;
        }

        return dataFolder.Folder;
    }
}
