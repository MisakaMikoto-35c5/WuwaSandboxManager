using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Wsm.Domain;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Connection.Session;
using Wsm.Engines.Abstractions;
using Wsm.Host.Elevation;
using Wsm.Hcs.Storage;
using Wsm.Host.Policy;
using Wsm.Localization;
using Wsm.Persistence;
using Wsm.Service.Client;
using Wsm.Service.Contracts.Internal;

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
    private readonly IWsmServiceClient? _serviceClient;
    private readonly IContainerImageImporter? _imageImporter;

    /// <summary>
    /// The client-side RDP entry point. <c>connect</c> presents the session in
    /// this process, even when the live VM is owned by the agent service: only
    /// the connection parameters are sourced from the service (see
    /// <c>connect</c>), the session itself is never proxied.
    /// </summary>
    private readonly ISessionLauncher _sessionLauncher;

    private IWsmServiceClient? _service;
    private bool _serviceResolved;

    public CliApplication(IServiceProvider provider)
    {
        _manager = provider.GetRequiredService<ISandboxManager>();
        _registry = provider.GetRequiredService<ISandboxEngineRegistry>();
        _multiInstancePolicy = provider.GetRequiredService<IMultiInstancePolicy>();
        _elevation = provider.GetRequiredService<IElevationService>();
        _localizer = provider.GetRequiredService<IStringLocalizer<WindowsSandboxManagerResource>>();
        _sessionLauncher = provider.GetRequiredService<ISessionLauncher>();

        // The agent client is optional at runtime: when the WsmAgent service is
        // not installed the CLI keeps using the in-process application layer.
        _serviceClient = provider.GetService<IWsmServiceClient>();
        _imageImporter = provider.GetService<IContainerImageImporter>();
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
        var quiet = options.ContainsKey("quiet");

        try
        {
            switch (command)
            {
                case "env":
                    return Env();

                case "list":
                    return WriteJson(await _manager.ListAsync());

                case "running":
                    return await RunningAsync(options);

                case "create":
                    return await CreateAsync(options);

                case "show":
                    return WriteJson(await _manager.GetAsync(RequireGuid(positional, 0)));

                case "start":
                    return await StartAsync(positional, options);

                case "host":
                    return await HostAsync(positional, options);

                case "multiconnect":
                    return await MultiConnectAsync(positional, options);

                case "stop":
                    return await StopAsync(positional, options);

                case "connect":
                    return await ConnectAsync(positional, options);

                case "ip":
                    return await IpAsync(positional, options);

                case "exec":
                    return await ExecAsync(positional, options);

                case "share":
                    return await ShareAsync(positional, options);

                case "checkpoint":
                    return await CheckpointAsync(positional, options);

                case "service":
                    return await ServiceAsync(positional, options);

                case "image":
                    return await ImageAsync(positional);

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
            return ReportError(_localizer["SandboxNotFound"], quiet);
        }
        catch (SandboxBackendException ex) when (IsForced(options) && ex.HResult == WsmServiceStatus.ServerUnavailable)
        {
            // --service was forced and the agent pipe did not answer: keep the
            // original error, then add the actionable follow-up the raw message
            // lacks (the auto-detect path degrades to in-process instead).
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ReportError(
                "WsmAgent is not available; check 'wsm service status', start it " +
                "('wsm service start' or 'wsm service run'), or drop --service to run in-process.",
                quiet);
        }
        catch (Exception ex)
        {
            return ReportError($"Error: {ex.Message}", quiet);
        }
    }

    /// <summary>
    /// Reports a failure. In quiet mode (a desktop shortcut launching the CLI
    /// with a hidden console) the message is also surfaced in a message box, so
    /// the user sees why the connection did not open instead of nothing.
    /// </summary>
    private static int ReportError(string message, bool quiet)
    {
        Console.Error.WriteLine(message);
        if (quiet)
        {
            ConsoleWindow.ShowError(message);
        }

        return 1;
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

        Console.WriteLine("Engines     :");
        foreach (var candidate in _registry.Engines.OrderBy(candidate => (int)candidate.Kind))
        {
            Console.WriteLine(
                $"  {candidate.Kind,-18} {candidate.Name}  [{(candidate.IsAvailable ? "available" : "unavailable")}]");
        }

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
        var engine = ParseEngine(options.GetValueOrDefault("engine"));
        var dto = await _manager.CreateAsync(options.GetValueOrDefault("name"), recipe, engine);
        Console.WriteLine($"Created sandbox {dto.Id} (engine={dto.Engine?.ToString() ?? "auto"})");
        return 0;
    }

    /// <summary>
    /// Resolves the optional <c>--engine</c> selector. Absent/unknown means
    /// "let the selector pick" (the lowest available <see cref="SandboxEngineKind"/>).
    /// </summary>
    private static SandboxEngineKind? ParseEngine(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "default" or "auto" => null,
        "hcs" or "hcs-sandbox" => SandboxEngineKind.HcsSandbox,
        "wsb" or "udk" or "windows-sandbox" or "windowssandbox" => SandboxEngineKind.WindowsSandbox,
        "container" or "windows-container" or "windowscontainer" => SandboxEngineKind.WindowsContainer,
        _ => null,
    };

    private async Task<int> StartAsync(List<string> positional, Dictionary<string, string?> options)
    {
        var mode = options.ContainsKey("remoteapp") ? SandboxConnectionMode.RemoteApp : SandboxConnectionMode.FullDesktop;
        var id = RequireGuid(positional, 0);

        return await WithServiceAsync(
            options,
            async service =>
            {
                SandboxRuntimeInfo runtime;
                try
                {
                    runtime = await service.StartAsync(id, mode);
                }
                catch (MultipleInstancesNotAllowedException)
                {
                    Console.Error.WriteLine(_localizer["MultipleInstances"]);
                    return 1;
                }

                Console.WriteLine($"Started sandbox {id} (state={SandboxState.Running})");
                Console.WriteLine($"  vm={runtime.VmId} transport={runtime.Transport} rail={runtime.RailMode}");
                Console.WriteLine($"  pipe={runtime.NamedPipeEndpoint} user={runtime.UserName}");
                return 0;
            },
            async () =>
            {
                SandboxInstanceDto dto;
                try
                {
                    dto = await _manager.StartAsync(id, mode);
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
            });
    }

    /// <summary>
    /// Routes the read of the running instance ids: through the agent service when
    /// it is available (or <c>--service</c> was passed), otherwise in-process.
    /// </summary>
    private async Task<int> RunningAsync(Dictionary<string, string?> options)
        => await WithServiceAsync(
            options,
            async service => WriteJson(await service.EnumerateAsync()),
            async () => WriteJson(await _manager.ListRunningAsync()));

    private async Task<int> StopAsync(List<string> positional, Dictionary<string, string?> options)
    {
        var id = RequireGuid(positional, 0);

        return await WithServiceAsync(
            options,
            async service =>
            {
                await service.StopAsync(id);
                Console.WriteLine("Stopped.");
                return 0;
            },
            async () =>
            {
                await _manager.StopAsync(id);
                Console.WriteLine("Stopped.");
                return 0;
            });
    }

    private async Task<int> IpAsync(List<string> positional, Dictionary<string, string?> options)
    {
        var id = RequireGuid(positional, 0);

        return await WithServiceAsync(
            options,
            async service => WriteJson(await service.GetIpAddressesAsync(id)),
            async () => WriteJson(await _manager.GetIpAddressesAsync(id)));
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

    /// <summary>
    /// Diagnostic verb (not part of the supported command surface): starts and
    /// connects several sandboxes from this single process, mirroring how the
    /// GUI hosts every session in its own process. Each chain is
    /// start-then-auto-connect, exactly like the GUI's primary action. The
    /// process holds for a while so session events (connect/disconnect) can be
    /// observed in <c>rdp-events.log</c> and via <c>qwinsta</c>.
    /// </summary>
    private async Task<int> MultiConnectAsync(List<string> positional, Dictionary<string, string?> options)
    {
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: wsm multiconnect <id> <id> [<id>...] [--stagger] [--hold <seconds>]");
            return 2;
        }

        var stagger = options.ContainsKey("stagger");
        var holdSeconds = int.TryParse(options.GetValueOrDefault("hold"), out var h) ? h : 120;

        var ids = positional.Select(Guid.Parse).ToArray();
        if (stagger)
        {
            // Sequential chains: start and connect one sandbox at a time, letting
            // each logon settle before the next begins.
            foreach (var id in ids)
            {
                try
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] starting {id}");
                    var dto = await _manager.StartAsync(id, SandboxConnectionMode.FullDesktop);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] started {dto.Name} (state={dto.State}); auto-connecting");
                    var connect = _manager.ConnectAsync(id, SandboxConnectionMode.FullDesktop);
                    _ = connect.ContinueWith(
                        t => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] session for {dto.Name} ended: {(t.IsFaulted ? t.Exception!.GetBaseException().Message : "window closed")}"),
                        TaskContinuationOptions.ExecuteSynchronously);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] letting the logon settle for 45s");
                    await Task.Delay(TimeSpan.FromSeconds(45));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] chain for {id} failed: {ex.GetBaseException().Message}");
                }
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] holding {holdSeconds}s");
            await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] closing all sessions");
            _sessionLauncher.CloseAll();
            return 0;
        }

        var chains = ids
            .Select(id => Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] starting {id}");
                    var dto = await _manager.StartAsync(id, SandboxConnectionMode.FullDesktop);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] started {dto.Name} (state={dto.State}); auto-connecting");
                    await _manager.ConnectAsync(id, SandboxConnectionMode.FullDesktop);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] session window for {dto.Name} closed");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] chain for {id} failed: {ex.GetBaseException().Message}");
                }
            }))
            .ToArray();

        // Hold while the session windows live, like the GUI does.
        await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] closing all sessions");
        _sessionLauncher.CloseAll();
        await Task.WhenAll(chains);
        return 0;
    }

    private async Task<int> ExecAsync(List<string> positional, Dictionary<string, string?> options)
    {
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: wsm exec <id> <command>");
            return 2;
        }

        var id = Guid.Parse(positional[0]);
        // The guest receives the joined text as a raw command line, so tokens
        // containing spaces (e.g. "HKLM\...\Terminal Server") must be re-quoted:
        // argv parsing consumed the original quotes before they reached us.
        var command = string.Join(' ', positional.Skip(1).Select(QuoteIfNeeded));

        return await WithServiceAsync(
            options,
            async service =>
            {
                var result = await service.ExecuteAsync(id, command);
                Console.WriteLine($"Exit code: {result.ExitCode}");
                return result.ExitCode;
            },
            async () =>
            {
                var exitCode = await _manager.ExecuteAsync(id, command);
                Console.WriteLine($"Exit code: {exitCode}");
                return exitCode;
            });
    }

    private static string QuoteIfNeeded(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

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
        var folder = new MappedFolder(host, sandbox, readOnly);

        return await WithServiceAsync(
            options,
            async service =>
            {
                await service.MapFolderAsync(id, folder);
                Console.WriteLine("Folder mapped.");
                return 0;
            },
            async () =>
            {
                await _manager.MapFolderAsync(id, folder);
                Console.WriteLine("Folder mapped.");
                return 0;
            });
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

    /// <summary>
    /// Decides whether a routable command should run through the agent service.
    /// <c>--service</c> forces the service; <c>--local</c> forces the in-process
    /// engine even when an agent is reachable (diagnostics and tests); by default
    /// the agent pipe is probed once per invocation — and a standalone console
    /// agent is spawned on demand when none answers — and the in-process path is
    /// used when no agent can be reached. Returns <c>null</c> to select the
    /// in-process
    /// <see cref="ISandboxManager"/> path.
    /// </summary>
    private async Task<IWsmServiceClient?> TryGetServiceAsync(Dictionary<string, string?> options)
    {
        if (_serviceClient is null)
        {
            return null;
        }

        if (IsForcedLocal(options))
        {
            return null;
        }

        if (IsForced(options))
        {
            return _serviceClient;
        }

        if (!_serviceResolved)
        {
            _service = await _serviceClient.EnsureAvailableAsync() ? _serviceClient : null;
            _serviceResolved = true;
        }

        return _service;
    }

    private static bool IsForced(Dictionary<string, string?> options) => options.ContainsKey("service");

    private static bool IsForcedLocal(Dictionary<string, string?> options) => options.ContainsKey("local");

    /// <summary>
    /// Runs <paramref name="viaService"/> when the agent service serves the call,
    /// otherwise <paramref name="inProcess"/>. In auto-detect mode a service that
    /// turns out to be unreachable degrades to the in-process path; domain
    /// exceptions other than "service unavailable" surface exactly as they do
    /// from the in-process manager.
    /// </summary>
    private async Task<int> WithServiceAsync(
        Dictionary<string, string?> options,
        Func<IWsmServiceClient, Task<int>> viaService,
        Func<Task<int>> inProcess)
    {
        var service = await TryGetServiceAsync(options);
        if (service is null)
        {
            return await inProcess();
        }

        try
        {
            return await viaService(service);
        }
        catch (SandboxBackendException ex) when (!IsForced(options) && ex.HResult == WsmServiceStatus.ServerUnavailable)
        {
            // The agent went away between the probe and the call: fall back.
            return await inProcess();
        }
    }

    /// <summary>
    /// Opens the user-visible RDP session for a running sandbox.
    /// <para>
    /// In service mode the live VM (and, for the UDK/Windows Sandbox engine, its
    /// process-local running reference) belongs to the agent process, so the
    /// in-process path cannot attach to it. The connection parameters are read
    /// from the agent instead (<see cref="IWsmServiceClient.PrepareConnectAsync"/>),
    /// then the session is presented <b>here</b> through
    /// <see cref="ISessionLauncher"/>: only the parameters are sourced from the
    /// service, the RDP/ActiveX session is never proxied (plan §8.5). When the
    /// service is absent the in-process <see cref="ISandboxManager.ConnectAsync"/>
    /// path is used unchanged.
    /// </para>
    /// </summary>
    private async Task<int> ConnectAsync(List<string> positional, Dictionary<string, string?> options)
    {
        var id = RequireGuid(positional, 0);
        var mode = options.ContainsKey("remoteapp") ? SandboxConnectionMode.RemoteApp : SandboxConnectionMode.FullDesktop;
        var program = options.GetValueOrDefault("program");
        var programArguments = options.GetValueOrDefault("program-args");

        var service = await TryGetServiceAsync(options);
        if (service is null)
        {
            await _manager.ConnectAsync(id, mode, program, programArguments);
            Console.WriteLine("Session launched.");
            return 0;
        }

        SandboxRuntimeInfo runtime;
        try
        {
            // Server-side pre-connect: the agent reclaims orphaned guest
            // sessions and returns the engine-backed live runtime. A not-running
            // instance surfaces as the same SandboxDomainException the
            // in-process path throws. The catch is narrowed to this call only,
            // so a failure in the session launch below can never be misrouted
            // into the in-process connect.
            runtime = await service.PrepareConnectAsync(id);
        }
        catch (SandboxBackendException ex) when (!IsForced(options) && ex.HResult == WsmServiceStatus.ServerUnavailable)
        {
            // The agent went away between the probe and the runtime fetch: fall back.
            await _manager.ConnectAsync(id, mode, program, programArguments);
            Console.WriteLine("Session launched.");
            return 0;
        }

        var engineKind = await ResolveEngineKindAsync(id);

        // The service prepared the runtime but knows nothing about the mode: a
        // RAIL connect must open the guest's RemoteApp surface first (see
        // RemoteAppGuestPolicy), which the client can do through the service's
        // in-guest command channel.
        if (mode == SandboxConnectionMode.RemoteApp)
        {
            await EnsureGuestRemoteAppAllowedAsync(service, id);
        }

        await _sessionLauncher.LaunchAsync(SandboxInstanceId.From(id), runtime, mode, engineKind, program, programArguments);
        Console.WriteLine("Session launched.");
        return 0;
    }

    /// <summary>
    /// Best-effort guest RemoteApp enablement through the agent service; a
    /// failure only warns (the connect proceeds and the client reports whatever
    /// the guest decides).
    /// </summary>
    private static async Task EnsureGuestRemoteAppAllowedAsync(IWsmServiceClient service, Guid id)
    {
        try
        {
            foreach (var command in Wsm.Application.RemoteAppGuestPolicy.EnablementCommands)
            {
                await service.ExecuteAsync(id, command);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: preparing the guest RemoteApp surface failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the engine kind of <paramref name="id"/> from its persisted
    /// definition so the session launcher logs/errors with the right engine.
    /// The launcher presents every engine through the same RDP connector, so the
    /// kind is purely informative and an unreadable definition must not block a
    /// connect to a service-owned VM.
    /// </summary>
    private async Task<SandboxEngineKind> ResolveEngineKindAsync(Guid id)
    {
        try
        {
            var dto = await _manager.GetAsync(id);
            return dto.Engine ?? SandboxEngineKind.HcsSandbox;
        }
        catch (Exception)
        {
            return SandboxEngineKind.HcsSandbox;
        }
    }

    /// <summary>
    /// Imports the base OS layer of a container image (default
    /// <c>mcr.microsoft.com/windows/servercore:ltsc2022</c>) so the process
    /// isolation engine can build containers from it. A Windows Server image is
    /// exempt from HCS's host/image build-match rule on Windows 11, which the
    /// Windows Sandbox client image is not.
    /// </summary>
    private async Task<int> ImageAsync(List<string> positional)
    {
        if (positional.Count < 3 || !positional[0].Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: wsm image import <reference> <layerPath>");
            Console.Error.WriteLine("  e.g. wsm image import mcr.microsoft.com/windows/servercore:ltsc2022 C:\\wsm-images\\servercore-ltsc2022");
            return 2;
        }

        if (_imageImporter is null)
        {
            Console.Error.WriteLine("The image importer is not available in this build.");
            return 1;
        }

        var reference = positional[1];
        var layerPath = positional[2];
        Console.WriteLine($"Importing the base layer of {reference} into {layerPath} ...");

        var result = await _imageImporter.ImportBaseLayerAsync(reference, layerPath);
        Console.WriteLine($"Imported base layer: {result}");
        Console.WriteLine("Point the engine at it with the WSM_CONTAINER_BASE_IMAGE environment variable");
        Console.WriteLine("(or WindowsContainer:BaseImageLayerPath), then create a 'container' sandbox.");
        return 0;
    }

    private async Task<int> ServiceAsync(List<string> positional, Dictionary<string, string?> options)
    {
        var subcommand = positional.Count > 0 ? positional[0].ToLowerInvariant() : "status";
        return subcommand switch
        {
            "status" => await ServiceStatusAsync(),
            "install" => await ServiceInstallAsync(),
            "uninstall" => await ServiceUninstallAsync(),
            "run" => await ServiceRunAsync(),
            "start" => await ServiceStartAsync(),
            "stop" => await ServiceStopAsync(),
            "restart" => await ServiceRestartAsync(),
            _ => UnknownServiceSubcommand(subcommand),
        };
    }

    private static int UnknownServiceSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown service subcommand '{subcommand}'.");
        Console.Error.WriteLine("Usage: wsm service status|install|uninstall|run|start|stop|restart");
        return 2;
    }

    /// <summary>
    /// Runs the agent in the foreground (standalone console mode) for machines
    /// where the WsmAgent Windows service is not installed. The agent holds its
    /// sandbox VMs for as long as this process lives; Ctrl+C stops it.
    /// </summary>
    private async Task<int> ServiceRunAsync()
    {
        var exe = ServiceHostExeLocator.Find();
        if (exe is null)
        {
            Console.Error.WriteLine("Could not locate Wsm.Service.Host.exe.");
            Console.Error.WriteLine("Build the host first, e.g. dotnet build src/40-Host/Wsm.Service.Host/Wsm.Service.Host.csproj");
            return 1;
        }

        // Attached to this console (no redirection) so the agent's log output is
        // visible and Ctrl+C reaches it.
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--console");
        startInfo.ArgumentList.Add("--data");
        startInfo.ArgumentList.Add(SandboxPersistenceOptions.DefaultConfigDataFolder);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Wsm.Service.Host.exe.");

        Console.WriteLine($"Running the WSM agent in the foreground ({exe}); Ctrl+C to stop.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    /// <summary>Starts the SCM-installed WsmAgent service.</summary>
    private async Task<int> ServiceStartAsync()
    {
        var result = await RunScAsync("start", "WsmAgent");
        ReportSc(result);
        if (result.ExitCode != 0)
        {
            ReportScFailure(result);
            return 1;
        }

        Console.WriteLine("Service 'WsmAgent' started.");
        return 0;
    }

    /// <summary>
    /// Stops the agent: through the Service Control Manager when the WsmAgent
    /// service is installed and running, otherwise through the Shutdown RPC when
    /// a standalone console agent is answering on the pipe.
    /// </summary>
    private async Task<int> ServiceStopAsync()
    {
        var query = await RunScAsync("query", "WsmAgent");
        var serviceRunning = query.ExitCode == 0
            && query.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);

        if (serviceRunning)
        {
            var stop = await RunScAsync("stop", "WsmAgent");
            ReportSc(stop);
            if (stop.ExitCode != 0)
            {
                ReportScFailure(stop);
                return 1;
            }

            Console.WriteLine("Service 'WsmAgent' stopped.");
            return 0;
        }

        if (_serviceClient is not null && await _serviceClient.IsAvailableAsync())
        {
            await _serviceClient.ShutdownAsync();
            Console.WriteLine("Standalone agent stopped.");
            return 0;
        }

        Console.Error.WriteLine("The WSM agent is not running (no WsmAgent service, no agent on the pipe).");
        return 1;
    }

    /// <summary>
    /// Restarts the SCM-installed WsmAgent service. A standalone console agent
    /// has no SCM entry and must be stopped (<c>service stop</c>) and re-run
    /// (<c>service run</c>) instead.
    /// </summary>
    private async Task<int> ServiceRestartAsync()
    {
        var query = await RunScAsync("query", "WsmAgent");
        if (query.ExitCode != 0)
        {
            ReportScFailure(query);
            Console.Error.WriteLine("A standalone console agent cannot be restarted; use 'wsm service stop' and 'wsm service run'.");
            return 1;
        }

        if (query.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            var stop = await RunScAsync("stop", "WsmAgent");
            ReportSc(stop);
            if (stop.ExitCode != 0)
            {
                ReportScFailure(stop);
                return 1;
            }
        }

        return await ServiceStartAsync();
    }

    /// <summary>
    /// Reports whether the agent pipe answers and the SCM service state. Exits 0
    /// only when the agent is available (the pipe probe succeeds); exits 1 when
    /// it is not (service missing or pipe not reachable) so scripts can gate on
    /// the result.
    /// </summary>
    private async Task<int> ServiceStatusAsync()
    {
        var available = _serviceClient is not null && await _serviceClient.IsAvailableAsync();
        Console.WriteLine($"Agent pipe  : {(available ? "available" : "not available")}");

        var result = await RunScAsync("query", "WsmAgent");
        ReportSc(result);
        ReportScFailure(result);

        return available ? 0 : 1;
    }

    private async Task<int> ServiceInstallAsync()
    {
        var exe = ServiceHostExeLocator.Find();
        if (exe is null)
        {
            Console.Error.WriteLine("Could not locate Wsm.Service.Host.exe.");
            Console.Error.WriteLine("Build the host first, e.g. dotnet build src/40-Host/Wsm.Service.Host/Wsm.Service.Host.csproj");
            return 1;
        }

        Console.WriteLine($"Installing service 'WsmAgent' -> {exe}");

        // Share the user's config & data folder with the LocalSystem service: the
        // agent would otherwise resolve SandboxPersistenceOptions against
        // %USERPROFILE% of LocalSystem and never find the user's .wsm files. This
        // is resolved exactly as this CLI's own persistence resolves it today (the
        // CLI does not honour UiSettings, so it is the default folder that
        // 'wsm create' writes to); the host overrides its own options with --data.
        var dataFolder = SandboxPersistenceOptions.DefaultConfigDataFolder;
        var binPath = $"\"{exe}\" --data \"{dataFolder}\"";

        var result = await RunScAsync("create", "WsmAgent", "binPath=", binPath, "start=", "auto");
        ReportSc(result);
        if (result.ExitCode != 0)
        {
            ReportScFailure(result);
            return 1;
        }

        // Best effort: a description is nice but never fatal to the install.
        var description = await RunScAsync("description", "WsmAgent", "Windows Sandbox Manager agent (holds sandbox VM lifetimes).");
        ReportSc(description);

        Console.WriteLine("Service 'WsmAgent' installed.");
        return 0;
    }

    private async Task<int> ServiceUninstallAsync()
    {
        var result = await RunScAsync("delete", "WsmAgent");
        ReportSc(result);
        if (result.ExitCode != 0)
        {
            ReportScFailure(result);
            return 1;
        }

        Console.WriteLine("Service 'WsmAgent' uninstalled.");
        return 0;
    }

    /// <summary>
    /// Maps an sc.exe failure to the actionable follow-up. Only exit code 5
    /// (ACCESS_DENIED) is an elevation problem; 1060 (service not installed) and
    /// 1073 (service already installed) get their own remediation, and every
    /// other code is left to the verbatim sc.exe output. Never fires for a
    /// successful (zero) exit.
    /// </summary>
    private void ReportScFailure(ScResult result)
    {
        switch (result.ExitCode)
        {
            case 5:
                if (!_elevation.IsElevated)
                {
                    Console.Error.WriteLine("Re-run this command from an elevated (administrator) prompt.");
                }

                break;

            case 1060:
                Console.Error.WriteLine("WsmAgent is not installed (run 'wsm service install', or start a standalone agent with 'wsm service run').");
                break;

            case 1073:
                Console.Error.WriteLine("WsmAgent is already installed (run 'wsm service uninstall' first).");
                break;
        }
    }

    private readonly record struct ScResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Runs <c>sc.exe</c> and captures its output; never throws on failure.</summary>
    private static async Task<ScResult> RunScAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start sc.exe.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ScResult(process.ExitCode, stdout, stderr);
    }

    private static void ReportSc(ScResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            Console.Write(result.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            Console.Error.Write(result.StandardError);
        }

        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"(sc.exe exited with code {result.ExitCode})");
        }
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
            Transport = ParseTransport(options.GetValueOrDefault("transport")),
            RailMode = options.ContainsKey("rail"),
            RailProgram = options.GetValueOrDefault("rail-program"),
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

    /// <summary>
    /// <c>pipe</c> (default, fastest) or <c>tcp</c> — the latter makes the guest
    /// listen on 3389 so an ordinary <c>mstsc</c> can connect (needs networking).
    /// </summary>
    private static RdpTransportKind ParseTransport(string? value) => value?.ToLowerInvariant() switch
    {
        "tcp" or "3389" => RdpTransportKind.Tcp,
        _ => RdpTransportKind.NamedPipe,
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
              connect <id> [--remoteapp] [--program <path>] [--program-args <args>]
                                                                  Connect a session (RAIL runs through
                                                                  mstsc + a generated .rdp file)
              ip <id>                   Get the sandbox IP addresses
              exec <id> <command>       Execute a command inside the sandbox
              share <id> <host> [sbx] [--rw]  Map a folder
              checkpoint save <id> <name> [--template]  Save a checkpoint
              checkpoint list <id>                      List checkpoints
              checkpoint restore <id> <checkpointId>    Restore a checkpoint
              service status                  Show the agent pipe and WsmAgent service state
                                              (exits 1 when the agent/pipe is unavailable)
              service install                 Register the WsmAgent Windows service (needs admin);
                                              pins the agent to your current WsmContainers folder
              service uninstall               Remove the WsmAgent Windows service (needs admin)
              service run                     Run a standalone agent in the foreground (no
                                              service install needed); Ctrl+C to stop
              service start                   Start the installed WsmAgent service
              service stop                    Stop the agent (SCM service or standalone agent)
              service restart                 Restart the installed WsmAgent service
              image import <ref> <path>       Import the base OS layer of a Windows
                                              container image for the process-isolation
                                              engine (e.g. mcr.microsoft.com/windows/
                                              servercore:ltsc2022)
              multi [--enable|--disable]      Inspect/change the multi-instance policy
              elevate                         Relaunch elevated (needed for HCS)

            options:
              --service                 Force commands through the resident WsmAgent service.
                                        By default the agent pipe is probed automatically,
                                        a standalone agent is spawned on demand when none
                                        answers, and the CLI falls back to the in-process
                                        engine when no agent can be started. Affects:
                                        running, start, stop, connect, exec, ip, share.
              --local                   Force the in-process engine even when an agent is
                                        running or can be started (diagnostics and tests).
              --quiet                   Hide the console window and surface failures in a message
                                        box. Intended for desktop shortcuts that run 'connect'.

            create options:
              --name <name>
              --engine <kind>           auto | hcs | udk (Windows Sandbox) | container
              --memory <MB>             (default 4096)
              --cpu <cores>             (HCS engine)
              --networking enable|disable|default
              --vgpu enable|disable|default
              --transport pipe|tcp        RDP transport (tcp lets mstsc connect on 3389;
                                          requires networking)
              --rail                    Start in RemoteApp (RAIL) mode
              --rail-program <path>     Program a RAIL session launches
                                        (default C:\Windows\explorer.exe)
              --fullscreen
              --protected
              --storage-path <dir>      Custom user-data location (HCS engine)
              --disk-size <MB>          User-data disk size
              --map "C:\host;C:\guest;rw"
              --logon "cmd.exe /c echo hi"
            """);
    }
}
