using System.Text.Json;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Interop.Internal;

/// <summary>RAII wrapper around an <c>HCS_SYSTEM</c> handle.</summary>
internal sealed class HcsComputeSystem : IDisposable
{
    internal HcsComputeSystem(IntPtr handle) => Handle = handle;

    internal IntPtr Handle { get; }

    public bool IsInvalid => Handle == IntPtr.Zero;

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            HcsNative.HcsCloseComputeSystem(Handle);
        }
    }
}

/// <summary>
/// Default <see cref="IHcsClient"/>: drives <c>computecore.dll</c> with the
/// operation based API and is the foundation of the HCS sandbox engine.
/// </summary>
[ExposeServices(typeof(IHcsClient))]
internal sealed class HcsClient : IHcsClient, ISingletonDependency
{
    public IReadOnlyList<HcsComputeSystemInfo> List(string? query = null)
    {
        using var operation = new HcsOperation();
        var hr = HcsNative.HcsEnumerateComputeSystems(query ?? "{}", operation.Handle);
        var document = operation.Complete(hr, nameof(HcsNative.HcsEnumerateComputeSystems));

        var results = new List<HcsComputeSystemInfo>();
        if (string.IsNullOrWhiteSpace(document))
        {
            return results;
        }

        using var json = JsonDocument.Parse(document);
        if (!json.RootElement.TryGetProperty("ComputeSystems", out var systems))
        {
            return results;
        }

        foreach (var element in systems.EnumerateArray())
        {
            results.Add(Parse(element));
        }

        return results;
    }

    public HcsComputeSystemInfo? Get(string id)
    {
        using var system = Open(id);
        if (system.IsInvalid)
        {
            return null;
        }

        using var operation = new HcsOperation();
        var hr = HcsNative.HcsGetComputeSystemProperties(system.Handle, operation.Handle, "{}");
        var document = operation.Complete(hr, nameof(HcsNative.HcsGetComputeSystemProperties));
        if (string.IsNullOrWhiteSpace(document))
        {
            return null;
        }

        using var json = JsonDocument.Parse(document);
        return Parse(json.RootElement);
    }

    public IDisposable Create(string id, string configuration)
    {
        using var operation = new HcsOperation();
        var hr = HcsNative.HcsCreateComputeSystem(id, configuration, operation.Handle, IntPtr.Zero, out var handle);
        operation.Complete(hr, nameof(HcsNative.HcsCreateComputeSystem));

        if (handle == IntPtr.Zero)
        {
            throw new SandboxBackendException("HcsCreateComputeSystem returned a null compute system handle.", 0);
        }

        return new HcsComputeSystem(handle);
    }

    public void Start(string id)
    {
        using var system = Open(id);
        using var operation = new HcsOperation();
        var hr = HcsNative.HcsStartComputeSystem(system.Handle, operation.Handle, "{}");
        operation.Complete(hr, nameof(HcsNative.HcsStartComputeSystem));
    }

    public void Shutdown(string id)
    {
        using var system = Open(id);
        using var operation = new HcsOperation();
        var hr = HcsNative.HcsShutDownComputeSystem(system.Handle, operation.Handle, "{}");
        operation.Complete(hr, nameof(HcsNative.HcsShutDownComputeSystem));
    }

    public void Terminate(string id)
    {
        using var system = Open(id);
        if (system.IsInvalid)
        {
            return;
        }

        using var operation = new HcsOperation();
        var hr = HcsNative.HcsTerminateComputeSystem(system.Handle, operation.Handle, "{}");
        operation.Complete(hr, nameof(HcsNative.HcsTerminateComputeSystem));
    }

    /// <summary>
    /// Saves a compute system. <paramref name="options"/> is the JSON
    /// <c>SaveOptions</c> document (<c>SaveType</c> = ToFile / AsTemplate and
    /// <c>SaveStateFilePath</c>). Returns the operation result document.
    /// </summary>
    public string? Save(string id, string options)
    {
        using var system = Open(id);
        using var operation = new HcsOperation();
        var hr = HcsNative.HcsSaveComputeSystem(system.Handle, operation.Handle, options);
        return operation.Complete(hr, nameof(HcsNative.HcsSaveComputeSystem));
    }

    public void CreateEmptyGuestStateFile(string path) => EnsureFile(
        HcsNative.HcsCreateEmptyGuestStateFile(path), nameof(HcsNative.HcsCreateEmptyGuestStateFile));

    public void CreateEmptyRuntimeStateFile(string path) => EnsureFile(
        HcsNative.HcsCreateEmptyRuntimeStateFile(path), nameof(HcsNative.HcsCreateEmptyRuntimeStateFile));

    public void GrantVmAccess(string id, string path)
    {
        var hr = HcsNative.HcsGrantVmAccess(id, path);
        if (hr < 0)
        {
            throw HcsErrors.ToException(hr, nameof(HcsNative.HcsGrantVmAccess), null);
        }
    }

    private static HcsComputeSystem Open(string id)
    {
        // 0x20000 == GENERIC_ALL; HCS accepts 0 for a default access mask.
        var hr = HcsNative.HcsOpenComputeSystem(id, 0, out var handle);
        if (hr < 0)
        {
            return new HcsComputeSystem(IntPtr.Zero);
        }

        return new HcsComputeSystem(handle);
    }

    private static void EnsureFile(int hresult, string operationName)
    {
        if (hresult < 0)
        {
            throw HcsErrors.ToException(hresult, operationName, null);
        }
    }

    private static HcsComputeSystemInfo Parse(JsonElement element)
        => new()
        {
            Id = GetString(element, "Id") ?? string.Empty,
            Owner = GetString(element, "Owner"),
            SystemType = GetString(element, "SystemType"),
            State = GetString(element, "State"),
            Name = GetString(element, "Name"),
            Raw = element.Clone(),
        };

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
