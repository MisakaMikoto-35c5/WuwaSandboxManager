using Microsoft.Extensions.Logging;
using WindowsUdk.Security.Isolation;

namespace Wsm.Udk.Sandbox.Internal;

/// <summary>
/// <see cref="IUdkVirtualMachine"/> over a real
/// <c>WindowsUdk.Security.Isolation.ManagedWindowsVM</c>. It keeps the
/// <c>VMRunningReference</c> alive for as long as the VM runs, because every
/// operation after start (exec, share, network) requires it.
/// </summary>
internal sealed class ManagedWindowsVm : IUdkVirtualMachine
{
    private readonly ManagedWindowsVM _vm;
    private readonly ILogger _logger;
    private VMRunningReference? _running;

    public ManagedWindowsVm(ManagedWindowsVM vm, ILogger logger)
    {
        _vm = vm;
        _logger = logger;
    }

    public Guid Id => _vm.Id;

    public string DefaultUserName => _vm.DefaultUserName;

    public UdkTerminationReason TerminatedReason => Map(_vm.TerminatedReason);

    public void Start()
    {
        if (_running is not null)
        {
            return;
        }

        try
        {
            _running = _vm.CreateRunningReference();
            _logger.LogInformation("Started UDK VM {VmId}.", Id);
        }
        catch (Exception ex) when (ex is not UdkSandboxException)
        {
            throw new UdkSandboxException(
                $"Failed to start UDK VM {Id:D} (0x{ex.HResult:X8}): {ex.Message}",
                ex);
        }
    }

    public void Terminate()
    {
        try
        {
            _vm.Terminate();
        }
        catch (Exception ex) when (ex is not UdkSandboxException)
        {
            throw new UdkSandboxException($"Failed to terminate UDK VM {Id:D}: {ex.Message}", ex);
        }
        finally
        {
            _running?.Dispose();
            _running = null;
        }
    }

    public void ShareFolder(string hostFolder, string guestFolder, bool allowWrite)
    {
        try
        {
            var result = _vm.ShareFolder(
                RequireRunning(),
                hostFolder,
                guestFolder,
                new VMShareFolderOptions { AllowWrite = allowWrite });

            if (result.Status != VMShareFolderStatus.Success)
            {
                throw new UdkSandboxException(
                    $"Sharing '{hostFolder}' as '{guestFolder}' failed: {result.Status}.",
                    result.ExtendedError);
            }
        }
        catch (Exception ex) when (ex is not UdkSandboxException)
        {
            throw new UdkSandboxException($"Failed to share '{hostFolder}' as '{guestFolder}': {ex.Message}", ex);
        }
    }

    public void UnshareFolder(string guestFolder)
    {
        try
        {
            var result = _vm.UnshareFolder(RequireRunning(), guestFolder);
            if (result.Status != VMUnshareFolderStatus.Success)
            {
                throw new UdkSandboxException(
                    $"Unsharing '{guestFolder}' failed: {result.Status}.",
                    result.ExtendedError);
            }
        }
        catch (Exception ex) when (ex is not UdkSandboxException)
        {
            throw new UdkSandboxException($"Failed to unshare '{guestFolder}': {ex.Message}", ex);
        }
    }

    public int Execute(string commandLine, UdkProcessOptions? options = null)
    {
        options ??= new UdkProcessOptions();

        var runAsExistingLogin = options.RunAs == UdkProcessRunAs.ExistingLogin;
        var processOptions = new VMCreateProcessOptions
        {
            RunAs = runAsExistingLogin ? VMCreateProcessRunAs.SpecifiedUser : VMCreateProcessRunAs.System,
            UseExistingLoginSession = runAsExistingLogin,
        };

        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            processOptions.UserName = options.UserName!;
        }

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            processOptions.WorkingDirectory = options.WorkingDirectory!;
        }

        try
        {
            var result = _vm.CreateProcessInVM(RequireRunning(), commandLine, processOptions);
            if (result.Status != VMCreateProcessStatus.Success)
            {
                throw new UdkSandboxException(
                    $"Running '{commandLine}' failed: {result.Status}.",
                    result.ExtendedError);
            }

            result.Process.WaitForExit();
            if (!result.Process.TryGetExitCode(out var exitCode))
            {
                throw new UdkSandboxException($"Could not read the exit code of '{commandLine}'.");
            }

            return (int)exitCode;
        }
        catch (Exception ex) when (ex is not UdkSandboxException)
        {
            throw new UdkSandboxException($"Failed to run '{commandLine}': {ex.Message}", ex);
        }
    }

    public IReadOnlyList<string> GetIpAddresses()
    {
        try
        {
            var info = _vm.GetNetworkInformation(RequireRunning());
            return info.Interfaces.SelectMany(i => i.IPAddresses).ToArray();
        }
        catch (Exception ex) when (ex is not UdkSandboxException)
        {
            throw new UdkSandboxException($"Failed to read the network information of {Id:D}: {ex.Message}", ex);
        }
    }

    private VMRunningReference RequireRunning()
        => _running ?? throw new UdkSandboxException($"UDK VM {Id:D} has not been started.");

    private static UdkTerminationReason Map(VMTerminatedReason reason) => reason switch
    {
        VMTerminatedReason.None => UdkTerminationReason.None,
        VMTerminatedReason.UserInitiated => UdkTerminationReason.UserInitiated,
        VMTerminatedReason.Crashed => UdkTerminationReason.Crashed,
        VMTerminatedReason.UnknownFailure => UdkTerminationReason.UnknownFailure,
        VMTerminatedReason.UserInitiatedShutdown => UdkTerminationReason.UserInitiatedShutdown,
        VMTerminatedReason.UserInitiatedShutdownForReboot => UdkTerminationReason.UserInitiatedShutdownForReboot,
        _ => UdkTerminationReason.UnknownFailure,
    };
}
