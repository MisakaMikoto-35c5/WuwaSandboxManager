using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Default <see cref="IWcowLayerService"/>: drives the WCOW layer APIs exported
/// by <c>vmcompute.dll</c> through <see cref="WcowLayerNative"/>. Every native
/// failure is surfaced as a <see cref="SandboxBackendException"/> (or
/// <see cref="SandboxNotSupportedException"/> for the elevation gate) with the
/// failing operation and HRESULT, so a caller gets an actionable message.
/// </summary>
[ExposeServices(typeof(IWcowLayerService))]
internal sealed class WcowLayerService : IWcowLayerService, ISingletonDependency
{
    /// <summary><c>HCS_E_ACCESS_DENIED</c>/<c>E_ACCESSDENIED</c> for the layer APIs.</summary>
    private const int AccessDenied = unchecked((int)0x8037011B);

    private readonly ILogger<WcowLayerService> _logger;

    /// <summary>
    /// The shared native <c>DriverInfo</c>. <c>Type</c> is <c>1</c> (filter
    /// driver) and <c>HomeDir</c> points at a UTF-16 NUL, matching hcsshim's
    /// <c>stdDriverInfo</c>. Native calls never mutate it.
    /// </summary>
    private WcowLayerNative.DriverInfo _info;

    private readonly IntPtr _homeDir;

    public WcowLayerService(ILogger<WcowLayerService> logger)
    {
        _logger = logger;

        // HomeDir must be a valid pointer to an empty string, not IntPtr.Zero.
        _homeDir = Marshal.StringToHGlobalUni(string.Empty);
        _info = new WcowLayerNative.DriverInfo { Type = 1, HomeDir = _homeDir };
    }

    public Guid GetLayerId(string layerPath)
    {
        var hr = WcowLayerNative.NameToGuid(Normalize(layerPath), out var id);
        Throw(hr, nameof(WcowLayerNative.NameToGuid), layerPath);
        return id;
    }

    public void CreateScratchLayer(string scratchPath, IReadOnlyList<string> parentLayerPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchPath);
        var scratch = Normalize(scratchPath);

        // A scratch layer carries a sandbox.vhdx; it is created by the call
        // itself, but HCS/its layer store expects the folder to exist first.
        Directory.CreateDirectory(scratch);

        WithDescriptors(parentLayerPaths, descriptors =>
        {
            var hr = WcowLayerNative.CreateSandboxLayer(
                ref _info, scratch, IntPtr.Zero, descriptors, (UIntPtr)(uint)descriptors.Length);
            Throw(hr, nameof(WcowLayerNative.CreateSandboxLayer), scratchPath);
        });

        _logger.LogDebug("Created WCOW scratch layer '{Scratch}'.", scratch);
    }

    public void Activate(string layerPath)
    {
        var path = Normalize(layerPath);
        var hr = WcowLayerNative.ActivateLayer(ref _info, path);
        Throw(hr, nameof(WcowLayerNative.ActivateLayer), layerPath);
        _logger.LogDebug("Activated WCOW layer '{Layer}'.", path);
    }

    public void Prepare(string layerPath, IReadOnlyList<string> parentLayerPaths)
    {
        var path = Normalize(layerPath);
        WithDescriptors(parentLayerPaths, descriptors =>
        {
            var hr = WcowLayerNative.PrepareLayer(
                ref _info, path, descriptors, (UIntPtr)(uint)descriptors.Length);
            Throw(hr, nameof(WcowLayerNative.PrepareLayer), layerPath);
        });

        _logger.LogDebug("Prepared WCOW layer '{Layer}'.", path);
    }

    public void Unprepare(string layerPath)
    {
        var hr = WcowLayerNative.UnprepareLayer(ref _info, Normalize(layerPath));
        Throw(hr, nameof(WcowLayerNative.UnprepareLayer), layerPath);
    }

    public void Deactivate(string layerPath)
    {
        var hr = WcowLayerNative.DeactivateLayer(ref _info, Normalize(layerPath));
        Throw(hr, nameof(WcowLayerNative.DeactivateLayer), layerPath);
    }

    public void Destroy(string layerPath)
    {
        var hr = WcowLayerNative.DestroyLayer(ref _info, Normalize(layerPath));
        Throw(hr, nameof(WcowLayerNative.DestroyLayer), layerPath);
    }

    public string GetMountPath(string layerPath)
    {
        var path = Normalize(layerPath);

        // First call: discover the required buffer length (in UTF-16 chars).
        UIntPtr length = UIntPtr.Zero;
        var hr = WcowLayerNative.GetLayerMountPath(ref _info, path, ref length, IntPtr.Zero);
        Throw(hr, nameof(WcowLayerNative.GetLayerMountPath), layerPath);

        var chars = (int)length;
        if (chars <= 0)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal(chars * sizeof(char));
        try
        {
            hr = WcowLayerNative.GetLayerMountPath(ref _info, path, ref length, buffer);
            Throw(hr, nameof(WcowLayerNative.GetLayerMountPath), layerPath);
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void ImportLayer(string layerPath, string importFolderPath, IReadOnlyList<string> parentLayerPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(importFolderPath);

        // ImportLayer expects a fresh (non-existent) destination directory.
        var destination = Normalize(layerPath);
        Directory.CreateDirectory(destination);

        WithDescriptors(parentLayerPaths, descriptors =>
        {
            var hr = WcowLayerNative.ImportLayer(
                ref _info,
                destination,
                Normalize(importFolderPath),
                descriptors,
                (UIntPtr)(uint)descriptors.Length);
            Throw(hr, nameof(WcowLayerNative.ImportLayer), layerPath);
        });

        _logger.LogDebug("Imported WCOW layer '{Layer}' from '{Import}'.", destination, importFolderPath);
    }

    public void ProcessBaseImage(string layerPath)
    {
        var hr = WcowLayerNative.ProcessBaseImage(Normalize(layerPath));
        Throw(hr, nameof(WcowLayerNative.ProcessBaseImage), layerPath);
    }

    /// <summary>
    /// Builds the native layer descriptors for the parent paths (id =
    /// <see cref="GetLayerId"/>, path = the absolute layer directory) and
    /// releases the unmanaged strings afterwards.
    /// </summary>
    private void WithDescriptors(IReadOnlyList<string> parentLayerPaths, Action<WcowLayerNative.LayerDescriptor[]> action)
    {
        var parents = parentLayerPaths ?? Array.Empty<string>();
        var descriptors = new WcowLayerNative.LayerDescriptor[parents.Count];

        try
        {
            for (var i = 0; i < parents.Count; i++)
            {
                var path = Normalize(parents[i]);
                descriptors[i] = new WcowLayerNative.LayerDescriptor
                {
                    LayerId = GetLayerId(path),
                    Flags = 0,
                    Path = Marshal.StringToHGlobalUni(path),
                };
            }

            action(descriptors);
        }
        finally
        {
            foreach (var descriptor in descriptors)
            {
                if (descriptor.Path != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(descriptor.Path);
                }
            }
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static void Throw(int hresult, string operation, string subject)
    {
        if (hresult >= 0)
        {
            return;
        }

        if (hresult == AccessDenied)
        {
            throw new SandboxNotSupportedException(
                $"The Windows container layer operation '{operation}' for '{subject}' requires an elevated " +
                "process (run WindowsSandboxManager as Administrator).");
        }

        throw new SandboxBackendException(
            $"WCOW layer operation '{operation}' failed for '{subject}' with HRESULT 0x{hresult:X8}.", hresult);
    }
}
