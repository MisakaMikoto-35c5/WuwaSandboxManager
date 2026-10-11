using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Engines.Abstractions;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;
using Wsm.Localization;

namespace Wsm.Cli;

/// <summary>
/// The command line front end over the application layer. It resolves the
/// application, engine registry, policy and elevation ports from the ABP
/// container and exposes the same command surface as the legacy CLI (minus the
/// HCS/layer diagnostics, which belonged to the old infrastructure).
/// </summary>
public sealed class CliApplication
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ISandboxManager _manager;
    private readonly ISandboxEngineRegistry _registry;
    private readonly IMultiInstancePolicy _multiInstancePolicy;
    private readonly IElevationService _elevation;
    private readonly IStringLocalizer<WindowsSandboxManagerResource> _localizer;

    public CliApplication(IServiceProvider provider)
    {
        _manager = provider.GetRequiredService<ISandboxManager>();
        _registry = provider.GetRequiredService<ISandboxEngineRegistry>();
        _multiInstancePolicy = provider.GetRequiredService<IMultiInstancePolicy>();
        _elevation = provider.GetRequiredService<IElevationService>();
        _localizer = provider.GetRequiredService<IStringLocalizer<WindowsSandboxManagerResource>>();
    }

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        var options = ParseOptions(rest);
        var positional = Positional(rest);

        try
        {
            switch (command)
            {
                case "env":
                    return Env();

                case "list":
                    return WriteJson(await _manager.ListAsync());

                case "running":
                    return WriteJson(await _manager.ListRunningAsync());

                case "create":
                    return await CreateAsync(options);

                case "show":
                    return WriteJson(await _manager.GetAsync(RequireGuid(positional, 0)));

                case "start":
                    return await StartAsync(positional, options);

                case "host":
                    return await HostAsync(positional, options);

                case "stop":
                    await _manager.StopAsync(RequireGuid(positional, 0));
                    Console.WriteLine("Stopped.");
                    return 0;

                case "connect":
                    await _manager.ConnectAsync(
                        RequireGuid(positional, 0),
                        options.ContainsKey("remoteapp") ? SandboxConnectionMode.RemoteApp : SandboxConnectionMode.FullDesktop);
                    Console.WriteLine("Session launched.");
                    return 0;

                case "ip":
                    return WriteJson(await _manager.GetIpAddressesAsync(RequireGuid(positional, 0)));

                case "exec":
                    return await ExecAsync(positional);

                case "share":
                    return await ShareAsync(positional, options);

                case "checkpoint":
                    return await CheckpointAsync(positional, options);

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
        catch (SandboxNotFoundException)
        {
            Console.Error.WriteLine(_localizer["SandboxNotFound"]);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private int Env()
    {
        var engine = ActiveEngine();
        if (engine is null)
        {
            Console.WriteLine(_localizer["EngineUnavailable"]);
        }
        else
        {
            Console.WriteLine($"Engine      : {engine.Name}");
            Console.WriteLine($"Kind        : {engine.Kind}");
            Console.WriteLine($"Available   : {engine.IsAvailable}");
            Console.WriteLine($"Capabilities: {JsonSerializer.Serialize(engine.Capabilities, Json)}");
        }

        Console.WriteLine($"Elevated    : {_elevation.IsElevated}");
        Console.WriteLine($"BuildBranch : {_multiInstancePolicy.CurrentBuildBranch ?? "<unknown>"}");
        Console.WriteLine($"Multi-instance allowed : {_multiInstancePolicy.AllowsMultipleInstances}");
        return 0;
    }

    private ISandboxEngine? ActiveEngine()
        => _registry.Engines
            .Where(engine => engine.IsAvailable)
            .OrderBy(engine => (int)engine.Kind)
            .FirstOrDefault();

    private async Task<int> CreateAsync(Dictionary<string, string?> options)
    {
        var recipe = BuildRecipe(options);
        var dto = await _manager.CreateAsync(options.GetValueOrDefault("name"), recipe);
        Console.WriteLine($"Created sandbox {dto.Id}");
        return 0;
    }

    private async Task<int> StartAsync(List<string> positional, Dictionary<string, string?> options)
    {
        var mode = options.ContainsKey("remoteapp") ? SandboxConnectionMode.RemoteApp : SandboxConnectionMode.FullDesktop;

        SandboxInstanceDto dto;
        try
        {
            dto = await _manager.StartAsync(RequireGuid(positional, 0), mode);
        }
        catch (MultipleInstancesNotAllowedException)
        {
            Console.Error.WriteLine(_localizer["MultipleInstances"]);
            return 1;
        }

        Console.WriteLine($"Started sandbox {dto.Id} (state={dto.State})");
        if (dto.Runtime is not null)
        {
            Console.WriteLine($"  vm={dto.Runtime.VmId} transport={dto.Runtime.Transport} rail={dto.Runtime.RailMode}");
            Console.WriteLine($"  pipe={dto.Runtime.NamedPipeEndpoint} user={dto.Runtime.UserName}");
        }

        return 0;
    }

    /// <summary>
    /// Starts a sandbox and keeps this process alive (holding the engine's
    /// compute-system handle) until Ctrl+C. Because the HCS configuration sets
    /// <c>ShouldTerminateOnLastHandleClosed</c>, a plain <c>start</c> tears the
    /// sandbox down the moment the CLI exits; this command keeps it running
    /// outside the GUI.
    /// </summary>
    private async Task<int> HostAsync(List<string> positional, Dictionary<string, string?> options)
    {
        var mode = options.ContainsKey("remoteapp") ? SandboxConnectionMode.RemoteApp : SandboxConnectionMode.FullDesktop;

        SandboxInstanceDto dto;
        try
        {
            dto = await _manager.StartAsync(RequireGuid(positional, 0), mode);
        }
        catch (MultipleInstancesNotAllowedException)
        {
            Console.Error.WriteLine(_localizer["MultipleInstances"]);
            return 1;
        }

        Console.WriteLine($"Started sandbox {dto.Id} (state={dto.State}); holding until Ctrl+C.");
        if (dto.Runtime is not null)
        {
            Console.WriteLine($"  user={dto.Runtime.UserName} password={(string.IsNullOrEmpty(dto.Runtime.Password) ? "<none>" : "<set>")}");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            completion.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => completion.TrySetResult();

        await completion.Task;

        await _manager.StopAsync(dto.Id);
        Console.WriteLine("Stopped.");
        return 0;
    }

    private async Task<int> ExecAsync(List<string> positional)
    {
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: wsm exec <id> <command>");
            return 2;
        }

        var id = Guid.Parse(positional[0]);
        var command = string.Join(' ', positional.Skip(1));
        var exitCode = await _manager.ExecuteAsync(id, command);
        Console.WriteLine($"Exit code: {exitCode}");
        return exitCode;
    }

    private async Task<int> ShareAsync(List<string> positional, Dictionary<string, string?> options)
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
        await _manager.MapFolderAsync(id, new MappedFolder(host, sandbox, readOnly));
        Console.WriteLine("Folder mapped.");
        return 0;
    }

    private async Task<int> CheckpointAsync(List<string> positional, Dictionary<string, string?> options)
    {
        if (positional.Count == 0)
        {
            Console.Error.WriteLine("Usage: wsm checkpoint save <id> <name> [--template] | list <id> | restore <id> <checkpointId>");
            return 2;
        }

        try
        {
            switch (positional[0].ToLowerInvariant())
            {
                case "save":
                {
                    var id = Guid.Parse(positional[1]);
                    var name = positional.Count > 2 ? positional[2] : $"checkpoint-{DateTime.Now:yyyyMMdd-HHmmss}";
                    var type = options.ContainsKey("template") ? SandboxCheckpointType.Template : SandboxCheckpointType.FullState;
                    var checkpoint = await _manager.SaveCheckpointAsync(id, name, type);
                    return WriteJson(checkpoint);
                }

                case "list":
                    return WriteJson(await _manager.ListCheckpointsAsync(Guid.Parse(positional[1])));

                case "restore":
                {
                    var id = Guid.Parse(positional[1]);
                    var checkpointId = Guid.Parse(positional[2]);
                    var newId = await _manager.RestoreCheckpointAsync(id, checkpointId);
                    Console.WriteLine($"Restored as {newId}");
                    return 0;
                }

                default:
                    Console.Error.WriteLine("Unknown checkpoint subcommand.");
                    return 2;
            }
        }
        catch (SandboxNotSupportedException)
        {
            Console.Error.WriteLine(_localizer["CheckpointNotSupported"]);
            return 1;
        }
    }

    private int Multi(Dictionary<string, string?> options)
    {
        Console.WriteLine($"BuildBranch   : {_multiInstancePolicy.CurrentBuildBranch ?? "<unknown>"}");
        Console.WriteLine($"Allowed       : {_multiInstancePolicy.AllowsMultipleInstances}");

        if (options.ContainsKey("enable"))
        {
            Console.WriteLine("WARNING: This changes a machine-wide Windows registry value (BuildBranch).");
            if (!_multiInstancePolicy.TryEnable(out var enableMessage))
            {
                Console.Error.WriteLine(enableMessage);
                return 1;
            }

            Console.WriteLine(enableMessage);
        }
        else if (options.ContainsKey("disable"))
        {
            if (!_multiInstancePolicy.TryRestore("ge_release", out var restoreMessage))
            {
                Console.Error.WriteLine(restoreMessage);
                return 1;
            }

            Console.WriteLine(restoreMessage);
        }

        return 0;
    }

    private int Elevate()
    {
        if (_elevation.IsElevated)
        {
            Console.WriteLine("Already elevated.");
            return 0;
        }

        if (_elevation.TryRelaunchElevated())
        {
            Console.WriteLine("Relaunched elevated; continue in the new window.");
            return 0;
        }

        Console.Error.WriteLine(_localizer["ElevationRequired"]);
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
            wsm - Windows Sandbox Manager CLI (ABP-hosted, multi-engine)

            Usage: wsm <command> [options]

            Commands:
              env                       Show the active engine, capabilities, elevation and policy
              list                      List defined sandbox instances
              running                   List running sandbox ids (selected engine)
              create [options]          Create a sandbox definition
              show <id>                 Show a sandbox definition
              start <id> [--remoteapp]  Start a sandbox
              host <id> [--remoteapp]   Start a sandbox and hold it running until Ctrl+C
              stop <id>                 Stop a sandbox
              connect <id> [--remoteapp] Connect an RDP session
              ip <id>                   Get the sandbox IP addresses
              exec <id> <command>       Execute a command inside the sandbox
              share <id> <host> [sbx] [--rw]  Map a folder
              checkpoint save <id> <name> [--template]  Save a checkpoint
              checkpoint list <id>                      List checkpoints
              checkpoint restore <id> <checkpointId>    Restore a checkpoint
              multi [--enable|--disable]      Inspect/change the multi-instance policy
              elevate                         Relaunch elevated (needed for HCS)

            create options:
              --name <name>
              --memory <MB>             (default 4096)
              --cpu <cores>             (HCS engine)
              --networking enable|disable|default
              --vgpu enable|disable|default
              --rail                    Start in RemoteApp (RAIL) mode
              --fullscreen
              --protected
              --storage-path <dir>      Custom user-data location (HCS engine)
              --disk-size <MB>          User-data disk size
              --map "C:\host;C:\guest;rw"
              --logon "cmd.exe /c echo hi"
            """);
    }
}
