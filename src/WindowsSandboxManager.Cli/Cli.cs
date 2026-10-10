using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsSandboxManager.Application.Abstractions;
using WindowsSandboxManager.Application.Dtos;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.ValueObjects;
using WindowsSandboxManager.Infrastructure.Elevation;
using WindowsSandboxManager.Infrastructure.Environment;
using WindowsSandboxManager.Infrastructure.Hcs;

namespace WindowsSandboxManager.Cli;

/// <summary>
/// Minimal, dependency-free command line front end over the application layer.
/// Useful for scripting and for validating the engine without the GUI.
/// </summary>
public static class Cli
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> RunAsync(IServiceProvider provider, string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var app = provider.GetRequiredService<ISandboxApplicationService>();
        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        var options = ParseOptions(rest);
        var positional = Positional(rest);

        try
        {
            switch (command)
            {
                case "env":
                    return await EnvAsync(app);

                case "list":
                    return WriteJson(await app.ListAsync());

                case "running":
                    return WriteJson(await app.ListRunningAsync());

                case "hcs":
                    return Hcs(provider);

                case "layers":
                    return Layers(provider);

                case "create":
                    return await CreateAsync(app, options);

                case "show":
                    return WriteJson(await app.GetAsync(RequireGuid(positional, 0)));

                case "start":
                    return await StartAsync(app, positional, options);

                case "stop":
                    await app.StopAsync(RequireGuid(positional, 0));
                    Console.WriteLine("Stopped.");
                    return 0;

                case "connect":
                    await app.ConnectAsync(
                        RequireGuid(positional, 0),
                        options.ContainsKey("remoteapp") ? SandboxConnectionMode.RemoteApp : SandboxConnectionMode.FullDesktop);
                    Console.WriteLine("Session launched.");
                    return 0;

                case "ip":
                    return WriteJson(await app.GetIpAddressesAsync(RequireGuid(positional, 0)));

                case "exec":
                    return await ExecAsync(app, positional);

                case "share":
                    return await ShareAsync(app, positional, options);

                case "checkpoint":
                    return await CheckpointAsync(app, positional, options);

                case "multi":
                    return Multi(options);

                case "elevate":
                    return Elevate();

                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> EnvAsync(ISandboxApplicationService app)
    {
        var caps = await app.GetCapabilitiesAsync();
        Console.WriteLine($"Backend            : {caps.BackendName}");
        Console.WriteLine($"Supported          : {caps.Environment.IsSupported}");
        Console.WriteLine($"Elevated           : {caps.Environment.IsElevated}");
        Console.WriteLine($"Multi-instance     : {caps.Environment.IsMultipleInstancesAllowed}");
        Console.WriteLine($"Control pipe       : {caps.Environment.ServerPipeName}");
        Console.WriteLine($"Legacy exe         : {caps.Environment.LegacyExecutablePath}");
        Console.WriteLine($"Remote session exe : {caps.Environment.RemoteSessionExecutablePath}");
        Console.WriteLine($"Server exe         : {caps.Environment.ServerExecutablePath}");
        Console.WriteLine($"Capabilities       : {JsonSerializer.Serialize(caps.Backend, Json)}");
        return 0;
    }

    private static int Hcs(IServiceProvider provider)
    {
        var hcs = provider.GetRequiredService<HcsService>();
        try
        {
            var systems = hcs.List();
            return WriteJson(systems.Select(s => new { s.Id, s.Owner, s.SystemType, s.State, s.Name }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Layers(IServiceProvider provider)
    {
        var layout = provider.GetRequiredService<HcsHostLayout>();
        Console.WriteLine($"Data root       : {layout.DataRoot}");
        Console.WriteLine($"Layers root     : {layout.LayersRoot}");
        Console.WriteLine($"Storage root    : {layout.StorageRoot}");
        Console.WriteLine($"Snapshots root  : {layout.SnapshotsRoot}");
        Console.WriteLine($"Base layer      : {layout.ResolveBaseLayerPath() ?? "<not found>"}");
        Console.WriteLine($"Elevated        : {HcsHostLayout.IsElevated}");
        Console.WriteLine("Layers:");
        foreach (var layer in layout.ListLayers())
        {
            Console.WriteLine($"  {layer.Id}  seq={layer.SequenceNumber}  os={layer.OsVersion}  usable={layer.IsUsableBaseImage}");
            Console.WriteLine($"      {layer.Path}");
        }

        return 0;
    }

    private static async Task<int> CreateAsync(ISandboxApplicationService app, Dictionary<string, string?> options)
    {
        var recipe = BuildRecipe(options);
        var dto = await app.CreateAsync(new CreateSandboxRequest(options.GetValueOrDefault("name"), recipe));
        Console.WriteLine($"Created sandbox {dto.Id}");
        return 0;
    }

    private static async Task<int> StartAsync(ISandboxApplicationService app, List<string> positional, Dictionary<string, string?> options)
    {
        var mode = options.ContainsKey("remoteapp") ? SandboxConnectionMode.RemoteApp : SandboxConnectionMode.FullDesktop;
        var dto = await app.StartAsync(RequireGuid(positional, 0), mode);
        Console.WriteLine($"Started sandbox {dto.Id} (state={dto.State})");
        if (dto.Runtime is not null)
        {
            Console.WriteLine($"  vm={dto.Runtime.VmId} transport={dto.Runtime.Transport} rail={dto.Runtime.RailMode}");
            Console.WriteLine($"  pipe={dto.Runtime.NamedPipeEndpoint} user={dto.Runtime.UserName}");
        }

        return 0;
    }

    private static async Task<int> ExecAsync(ISandboxApplicationService app, List<string> positional)
    {
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: wsm exec <id> <command>");
            return 2;
        }

        var id = Guid.Parse(positional[0]);
        var command = string.Join(' ', positional.Skip(1));
        var exitCode = await app.ExecuteAsync(id, command);
        Console.WriteLine($"Exit code: {exitCode}");
        return exitCode;
    }

    private static async Task<int> ShareAsync(ISandboxApplicationService app, List<string> positional, Dictionary<string, string?> options)
    {
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: wsm share <id> <hostPath> [sandboxPath] [--rw]");
            return 2;
        }

        var id = Guid.Parse(positional[0]);
        var host = positional[1];
        var sandbox = positional.Count > 2 ? positional[2] : string.Empty;
        var readOnly = !options.ContainsKey("rw");
        await app.MapFolderAsync(id, new MappedFolder(host, sandbox, readOnly));
        Console.WriteLine("Folder mapped.");
        return 0;
    }

    private static async Task<int> CheckpointAsync(ISandboxApplicationService app, List<string> positional, Dictionary<string, string?> options)
    {
        if (positional.Count == 0)
        {
            Console.Error.WriteLine("Usage: wsm checkpoint save <id> <name> [--template] | list <id> | restore <id> <checkpointId>");
            return 2;
        }

        switch (positional[0].ToLowerInvariant())
        {
            case "save":
            {
                var id = Guid.Parse(positional[1]);
                var name = positional.Count > 2 ? positional[2] : $"checkpoint-{DateTime.Now:yyyyMMdd-HHmmss}";
                var type = options.ContainsKey("template") ? SandboxCheckpointType.Template : SandboxCheckpointType.FullState;
                var checkpoint = await app.SaveCheckpointAsync(id, name, type);
                return WriteJson(checkpoint);
            }

            case "list":
                return WriteJson(await app.ListCheckpointsAsync(Guid.Parse(positional[1])));

            case "restore":
            {
                var id = Guid.Parse(positional[1]);
                var checkpointId = Guid.Parse(positional[2]);
                var newId = await app.RestoreCheckpointAsync(id, checkpointId);
                Console.WriteLine($"Restored as {newId}");
                return 0;
            }

            default:
                Console.Error.WriteLine("Unknown checkpoint subcommand.");
                return 2;
        }
    }

    private static int Multi(Dictionary<string, string?> options)
    {
        Console.WriteLine($"BuildBranch           : {SandboxMultiInstancePolicy.CurrentBuildBranch}");
        Console.WriteLine($"Elevated              : {SandboxMultiInstancePolicy.IsElevated}");

        if (options.ContainsKey("enable"))
        {
            Console.WriteLine("WARNING: This changes a machine-wide Windows registry value (BuildBranch).");
            if (!SandboxMultiInstancePolicy.TryEnable(out var enableMessage))
            {
                Console.Error.WriteLine(enableMessage);
                return 1;
            }

            Console.WriteLine(enableMessage);
        }
        else if (options.ContainsKey("disable"))
        {
            if (!SandboxMultiInstancePolicy.TryRestore("ge_release", out var restoreMessage))
            {
                Console.Error.WriteLine(restoreMessage);
                return 1;
            }

            Console.WriteLine(restoreMessage);
        }

        return 0;
    }

    private static int Elevate()
    {
        if (ElevationHelper.IsElevated)
        {
            Console.WriteLine("Already elevated.");
            return 0;
        }

        if (ElevationHelper.TryRelaunchElevated())
        {
            Console.WriteLine("Relaunched elevated; continue in the new window.");
            return 0;
        }

        Console.Error.WriteLine("Elevation was declined.");
        return 1;
    }

    private static SandboxRecipe BuildRecipe(Dictionary<string, string?> options)
    {
        var storage = SandboxStorageOptions.Default;
        if (options.TryGetValue("storage-path", out var storagePath) && !string.IsNullOrWhiteSpace(storagePath))
        {
            storage = storage.WithPath(storagePath);
        }

        if (options.TryGetValue("disk-size", out var diskSize) && uint.TryParse(diskSize, out var size))
        {
            storage = storage with { DiskSizeMb = size };
        }

        var recipe = SandboxRecipe.Default with
        {
            Memory = new SandboxMemoryConfiguration(GetInt(options, "memory", SandboxMemoryConfiguration.DefaultMemoryMb)),
            Cpu = new SandboxCpuConfiguration(GetInt(options, "cpu", SandboxCpuConfiguration.Default().CoreCount)),
            Networking = ParseNetworking(options.GetValueOrDefault("networking")),
            VirtualGpu = ParseVgpu(options.GetValueOrDefault("vgpu")),
            RailMode = options.ContainsKey("rail"),
            FullScreen = options.ContainsKey("fullscreen"),
            ProtectedClient = options.ContainsKey("protected"),
            Storage = storage,
        };

        if (options.TryGetValue("map", out var map) && !string.IsNullOrEmpty(map))
        {
            recipe = recipe.WithMappedFolder(ParseMappedFolder(map));
        }

        if (options.TryGetValue("logon", out var logon) && !string.IsNullOrEmpty(logon))
        {
            recipe = recipe.WithLogonCommand(new LogonCommand(logon));
        }

        return recipe;
    }

    private static MappedFolder ParseMappedFolder(string value)
    {
        var parts = value.Split(';');
        var host = parts[0];
        var sandbox = parts.Length > 1 ? parts[1] : string.Empty;
        var readOnly = parts.Length <= 2 || !parts[2].Equals("rw", StringComparison.OrdinalIgnoreCase);
        return new MappedFolder(host, sandbox, readOnly);
    }

    private static NetworkingMode ParseNetworking(string? value) => value?.ToLowerInvariant() switch
    {
        "enable" or "enabled" or "on" => NetworkingMode.Enabled,
        "disable" or "disabled" or "off" => NetworkingMode.Disabled,
        _ => NetworkingMode.Default,
    };

    private static VirtualGpuMode ParseVgpu(string? value) => value?.ToLowerInvariant() switch
    {
        "enable" or "enabled" or "on" => VirtualGpuMode.Enabled,
        "disable" or "disabled" or "off" => VirtualGpuMode.Disabled,
        _ => VirtualGpuMode.Default,
    };

    private static int GetInt(Dictionary<string, string?> options, string key, int fallback)
        => options.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static Guid RequireGuid(List<string> positional, int index)
    {
        if (positional.Count <= index || !Guid.TryParse(positional[index], out var id))
        {
            throw new ArgumentException("A valid sandbox id (GUID) is required.");
        }

        return id;
    }

    private static int WriteJson(object? value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, Json));
        return 0;
    }

    private static List<string> Positional(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            result.Add(arg);
        }

        return result;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[i + 1];
                i++;
            }

            options[key] = value;
        }

        return options;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            wsm - Windows Sandbox Manager CLI (HCS-first)

            Usage: wsm <command> [options]

            Commands:
              env                       Show host environment and backend capabilities
              list                      List defined sandbox instances
              running                   List running sandbox ids (selected backend)
              hcs                       List HCS compute systems (requires elevation)
              layers                    Show layer/storage/snapshot layout + base image
              create [options]          Create a sandbox definition
              show <id>                 Show a sandbox definition
              start <id> [--remoteapp]  Start a sandbox
              stop <id>                 Stop a sandbox
              connect <id> [--remoteapp] Connect an RDP session
              ip <id>                   Get the sandbox IP addresses
              exec <id> <command>       Execute a command inside the sandbox (gRPC backend)
              share <id> <host> [sbx] [--rw]  Map a folder (gRPC backend)
              checkpoint save <id> <name> [--template]  Save an HCS checkpoint
              checkpoint list <id>                      List HCS checkpoints
              checkpoint restore <id> <checkpointId>    Restore a checkpoint
              multi [--enable|--disable]      Inspect/change the multi-instance policy
              elevate                         Relaunch elevated (needed for HCS)

            create options:
              --name <name>
              --memory <MB>             (default 4096)
              --cpu <cores>             (HCS backend)
              --networking enable|disable|default
              --vgpu enable|disable|default
              --rail                    Start in RemoteApp (RAIL) mode
              --fullscreen
              --protected
              --storage-path <dir>      Custom user-data location (HCS backend)
              --disk-size <MB>          User-data disk size
              --map "C:\host;C:\guest;rw"
              --logon "cmd.exe /c echo hi"
            """);
    }
}
