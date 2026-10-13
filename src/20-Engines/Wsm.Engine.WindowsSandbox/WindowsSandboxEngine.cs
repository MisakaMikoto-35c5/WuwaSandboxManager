using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Host.Environment;
using Wsm.Udk.Sandbox;

namespace Wsm.Engine.WindowsSandbox;

/// <summary>
/// The Windows Sandbox engine, driven through the Windows UDK
/// (<c>WindowsUdk.Security.Isolation.ManagedWindowsVM</c>) instead of the
/// named-pipe gRPC control plane.
/// <para>
/// This is exactly how Microsoft's own <c>WindowsSandboxServer</c> creates its
/// VM (<c>new ManagedWindowsVM(VMOptions)</c> → <c>CreateRunningReference()</c>),
/// and it is what makes the guest complete an interactive logon: the UDK derives
/// the whole HCS document (silo, devices, HvSocket, guest-state) that a
/// hand-assembled configuration gets wrong. Provisioning the guest account then
/// mirrors the platform's <c>PrepareVM</c>.
/// </para>
/// </summary>
[ExposeServices(typeof(ISandboxEngine))]
internal sealed class WindowsSandboxEngine : ISandboxEngine, ISingletonDependency
{
    private const string DefaultGuestUser = "WDAGUtilityAccount";
    private const int GuestReadyTimeoutSeconds = 180;

    /// <summary>
    /// The platform's <c>Constants.VMSpecializationCommands</c> (mirrored from
    /// <c>WindowsSandboxServer</c>), run as SYSTEM right after the account set-up.
    /// </summary>
    private const string GuestSpecializationCommands =
        "cmd.exe /c " +
        "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Network\\NewNetworkWindowOff\" /f & " +
        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FlyoutMenuSettings\" " +
        "/v ShowLockOption /t REG_DWORD /d 0 /f & " +
        "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" " +
        "/v ShowTaskViewButton /t REG_DWORD /d 0 /f";

    private readonly IUdkSandboxHost _host;
    private readonly ISandboxEnvironment _environment;
    private readonly ILogger<WindowsSandboxEngine> _logger;

    /// <summary>The VMs this process created or opened, keyed by compute-system id.</summary>
    private readonly ConcurrentDictionary<Guid, IUdkVirtualMachine> _machines = new();

    public WindowsSandboxEngine(
        IUdkSandboxHost host,
        ISandboxEnvironment environment,
        ILogger<WindowsSandboxEngine> logger)
    {
        _host = host;
        _environment = environment;
        _logger = logger;
    }

    public string Name => "Windows Sandbox (UDK ManagedWindowsVM)";

    public SandboxEngineKind Kind => SandboxEngineKind.WindowsSandbox;

    public bool IsAvailable => _host.IsAvailable && _environment.IsSupported && _environment.IsElevated;

    public SandboxEngineCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = _environment.IsMultipleInstancesAllowed,
        SupportsCpuConfiguration = true,
        SupportsCustomStorageLocation = false,
        SupportsCustomBaseImage = false,
        SupportsCheckpoints = false,
        SupportsRuntimeFolderMapping = true,
        SupportsProcessExecution = true,

        // The UDK exposes no enumeration API; only the instances this process
        // started (or opened) can be listed.
        SupportsEnumeration = false,
        RequiresElevation = true,
    };

    public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        IReadOnlyList<SandboxInstanceId> ids = _machines.Keys
            .Select(id => new SandboxInstanceId(id))
            .ToArray();

        return Task.FromResult(ids);
    }

    public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var id = request.Id;
        var recipe = request.Recipe;

        var options = new UdkSandboxOptions
        {
            DisplayName = SanitizeVmName($"WSM {recipe.Name.Value}"),
            MemoryMb = (uint)recipe.Memory.MemoryMb,
            CpuCount = (uint)recipe.Cpu.CoreCount,
            EnableNetworking = recipe.Networking != NetworkingMode.Disabled,
            EnableVirtualGpu = recipe.VirtualGpu != VirtualGpuMode.Disabled,
        };

        _logger.LogInformation(
            "Creating UDK sandbox VM for {SandboxId} ({Name}; memory={MemoryMb}MB, cpus={CpuCount}, networking={Networking}).",
            id, recipe.Name.Value, options.MemoryMb, options.CpuCount, options.EnableNetworking);

        var vm = _host.Create(options);

        try
        {
            vm.Start();

            foreach (var folder in recipe.MappedFolders)
            {
                vm.ShareFolder(folder.HostFolder, folder.SandboxFolder, !folder.ReadOnly);
            }
        }
        catch (Exception ex)
        {
            TryTerminate(vm, id);
            throw ex is SandboxDomainException or SandboxNotSupportedException
                ? ex
                : new SandboxBackendException(
                    $"Failed to start the UDK sandbox VM for {id}: {ex.Message}",
                    ex.HResult,
                    ex);
        }

        _machines[id.Value] = vm;

        var (accountUser, accountPassword) = ProvisionGuestAccount(vm, recipe);

        var runtime = new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = vm.Id,
            Transport = RdpTransportKind.NamedPipe,
            RailMode = recipe.RailMode,
            RailProgram = recipe.ResolvedRailProgram,
            FullScreen = recipe.FullScreen,
            WindowTitle = recipe.Name.Value,

            // The credential travels with the runtime: the WDAG guest does not
            // auto-logon, and RDP is the only way in, so the client must supply it.
            UserName = accountUser,
            Password = accountPassword,
            IpAddresses = SafeIpAddresses(vm),
            Redirections = recipe.Redirections,
        };

        return Task.FromResult(runtime);
    }

    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        if (_machines.TryRemove(id.Value, out var tracked))
        {
            tracked.Terminate();
            return Task.CompletedTask;
        }

        // A UDK VM is owned by the process that created it (the running reference
        // is process-local), so another process cannot terminate it. Surface that
        // clearly instead of failing with a confusing lookup error.
        throw new SandboxNotSupportedException(
            $"Sandbox {id} is not owned by this process; a Windows Sandbox (UDK) instance " +
            "must be stopped by the process that started it (or the GUI).");
    }

    public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        if (!_machines.TryGetValue(id.Value, out var vm))
        {
            return Task.FromResult<SandboxRuntimeInfo?>(null);
        }

        var accountUser = DefaultGuestUser;
        var accountPassword = DefaultGuestPassword(id);

        var runtime = new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = vm.Id,
            Transport = RdpTransportKind.NamedPipe,
            UserName = accountUser,
            Password = accountPassword,
            IpAddresses = SafeIpAddresses(vm),
        };

        return Task.FromResult<SandboxRuntimeInfo?>(runtime);
    }

    public Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        if (!_machines.TryGetValue(id.Value, out var vm))
        {
            throw new SandboxNotSupportedException(
                $"Sandbox {id} is not running in this process, so a folder cannot be mapped into it.");
        }

        vm.ShareFolder(folder.HostFolder, folder.SandboxFolder, !folder.ReadOnly);
        return Task.CompletedTask;
    }

    public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var vm = Require(id);
        var exitCode = vm.Execute(command.Command);
        return Task.FromResult(exitCode);
    }

    public Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        return Task.FromResult(SafeIpAddresses(Require(id)));
    }

    /// <summary>
    /// Provisions the guest logon account exactly like the platform's own
    /// <c>SandboxVM.PrepareVM</c>: activate the account with a known password,
    /// add it to the groups Remote Desktop Services checks, flip the sandbox's
    /// RDP listener switch, then complete the image-prep handshake. Best effort:
    /// a failure is logged loudly and the sandbox still starts.
    /// </summary>
    private (string User, string Password) ProvisionGuestAccount(IUdkVirtualMachine vm, SandboxRecipe recipe)
    {
        var user = string.IsNullOrWhiteSpace(recipe.AccountUser) ? DefaultGuestUser : recipe.AccountUser!;
        var password = string.IsNullOrWhiteSpace(recipe.AccountPassword)
            ? DefaultGuestPassword(new SandboxInstanceId(vm.Id))
            : recipe.AccountPassword!;

        try
        {
            WaitForGuestReady(vm);

            CheckGuest(vm, $"net user {user} {password}");
            CheckGuest(vm, $"net user {user} /active:yes");
            CheckGuest(vm, $"wcsetupagent.exe AddUserToUsersGroup {user}");
            CheckGuest(vm, $"wcsetupagent.exe AddUserToAdminGroup {user}");
            CheckGuest(vm, $"wcsetupagent.exe AddUserToRemoteDesktopUsers {user}");
            CheckGuest(
                vm,
                "reg add \"HKLM\\System\\CurrentControlSet\\Control\\Terminal Server\" " +
                "/v b8ba549e-fb39-441c-b71e-44e9e4818210 /t REG_DWORD /d 1 /f");
            CheckGuest(vm, GuestSpecializationCommands);
            CheckGuest(vm, "wcsetupagent.exe NotifyRestoreComplete");

            _logger.LogInformation("Provisioned guest account {User} for UDK VM {VmId}.", user, vm.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to provision the guest account {User} for UDK VM {VmId}; an RDP connect will likely be refused.",
                user, vm.Id);
        }

        return (user, password);
    }

    /// <summary>
    /// Waits until the guest accepts process creation. The UDK reports the VM
    /// "running" before the guest OS is ready to run commands, so the set-up is
    /// retried until a trivial command succeeds.
    /// </summary>
    private void WaitForGuestReady(IUdkVirtualMachine vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(GuestReadyTimeoutSeconds);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (vm.Execute("cmd.exe /c exit 0") == 0)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            Thread.Sleep(1000);
        }

        throw last ?? new SandboxBackendException("The sandbox guest did not become ready in time.", 0);
    }

    /// <summary>Runs a guest command and logs (but tolerates) a non-zero exit.</summary>
    private void CheckGuest(IUdkVirtualMachine vm, string commandLine)
    {
        var exitCode = vm.Execute(commandLine);
        if (exitCode != 0)
        {
            _logger.LogWarning(
                "Guest command '{Command}' in UDK VM {VmId} exited {ExitCode}.",
                commandLine, vm.Id, exitCode);
        }
        else
        {
            _logger.LogDebug("Guest command '{Command}' in UDK VM {VmId} exited 0.", commandLine, vm.Id);
        }
    }

    private IUdkVirtualMachine Require(SandboxInstanceId id)
        => _machines.TryGetValue(id.Value, out var vm)
            ? vm
            : throw new SandboxNotSupportedException($"Sandbox {id} is not running in this process.");

    private IReadOnlyList<string> SafeIpAddresses(IUdkVirtualMachine vm)
    {
        try
        {
            return vm.GetIpAddresses();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the IP addresses of UDK VM {VmId}.", vm.Id);
            return Array.Empty<string>();
        }
    }

    private void TryTerminate(IUdkVirtualMachine vm, SandboxInstanceId id)
    {
        try
        {
            vm.Terminate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate UDK VM {VmId} after a failed start.", vm.Id);
        }
    }

    private void EnsureElevated()
    {
        if (!_environment.IsElevated)
        {
            throw new SandboxNotSupportedException(
                "The Windows Sandbox (UDK) engine requires an elevated host process.");
        }
    }

    /// <summary>
    /// The deterministic guest password for an instance: <c>Wsm!</c> followed by
    /// the first ten SHA-256 bytes of the instance id, uppercase hex. It is not
    /// persisted anywhere and is recomputed on demand.
    /// </summary>
    private static string DefaultGuestPassword(SandboxInstanceId id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString()));
        return "Wsm!" + Convert.ToHexString(hash.AsSpan(0, 10));
    }

    /// <summary>
    /// The UDK validates <c>VMOptions.DisplayName</c> and rejects spaces with
    /// E_INVALIDARG, so the display name is reduced to an identifier-like form.
    /// </summary>
    private static string SanitizeVmName(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var ch in name)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        }

        var sanitized = builder.ToString().Trim('-', '.');
        if (sanitized.Length == 0)
        {
            sanitized = "WSM";
        }

        return sanitized.Length > 60 ? sanitized[..60] : sanitized;
    }
}
