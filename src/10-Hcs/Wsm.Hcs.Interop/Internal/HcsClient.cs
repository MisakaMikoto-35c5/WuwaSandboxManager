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
        return ParseList(document);
    }

    /// <summary>
    /// Parses an <c>HcsEnumerateComputeSystems</c> result document. The result
    /// is normally an object carrying a <c>ComputeSystems</c> array, but some
    /// HCS builds return a bare JSON array instead. Both shapes are accepted;
    /// anything else (including empty or malformed input) yields an empty list
    /// rather than throwing.
    /// </summary>
    internal static IReadOnlyList<HcsComputeSystemInfo> ParseList(string? document)
    {
        var results = new List<HcsComputeSystemInfo>();
        if (string.IsNullOrWhiteSpace(document))
        {
            return results;
        }

        try
        {
            using var json = JsonDocument.Parse(document);
            var root = json.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ComputeSystems", out var systems) &&
                systems.ValueKind == JsonValueKind.Array)
            {
                Append(systems, results);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                Append(root, results);
            }
        }
        catch (JsonException)
        {
            // A malformed enumerate result carries no usable compute systems.
            return results;
        }

        return results;
    }

    private static void Append(JsonElement array, List<HcsComputeSystemInfo> results)
    {
        foreach (var element in array.EnumerateArray())
        {
            results.Add(Parse(element));
        }
    }

    public HcsComputeSystemInfo? Get(string id)
    {
        HcsComputeSystem system;
        try
        {
            system = Open(id);
        }
        catch (SandboxNotFoundException)
        {
            // A missing compute system is a normal, expected result here.
            return null;
        }

        using (system)
        {
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
        HcsComputeSystem system;
        try
        {
            system = Open(id);
        }
        catch (SandboxNotFoundException)
        {
            // Already gone: terminating a missing compute system is a no-op.
            return;
        }

        using (system)
        {
            using var operation = new HcsOperation();
            var hr = HcsNative.HcsTerminateComputeSystem(system.Handle, operation.Handle, "{}");
            operation.Complete(hr, nameof(HcsNative.HcsTerminateComputeSystem));
        }
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
        // 0x10000000 == GENERIC_ALL. HCS rejects an access mask of 0 with
        // E_INVALIDARG (0x80070057), which leaves a null handle that makes the
        // subsequent Start/Terminate AV (0xC0000005). GENERIC_ALL is what the
        // platform's own stack requests.
        const uint GenericAll = 0x10000000;
        var hr = HcsNative.HcsOpenComputeSystem(id, GenericAll, out var handle);
        return ResolveOpenHandle(id, hr, handle);
    }

    /// <summary>
    /// Validates the result of <see cref="HcsNative.HcsOpenComputeSystem"/>.
    /// A failed open — or, defensively, a "successful" one that still yields a
    /// null handle — must never produce an <see cref="HcsComputeSystem"/> that
    /// wraps <see cref="IntPtr.Zero"/>: dereferencing such a handle from
    /// <c>Start</c>/<c>Shutdown</c>/<c>Save</c>/<c>Terminate</c> is a documented
    /// access violation (<c>0xC0000005</c>). A "not found" HRESULT surfaces as
    /// <see cref="SandboxNotFoundException"/>; every other failure as the
    /// mapped <see cref="SandboxBackendException"/>. Exposed for unit testing
    /// of the failure mapping without a live HCS service.
    /// </summary>
    internal static HcsComputeSystem ResolveOpenHandle(string id, int hresult, IntPtr handle)
    {
        if (hresult >= 0 && handle != IntPtr.Zero)
        {
            return new HcsComputeSystem(handle);
        }

        // Never leak a handle the caller is about to discard.
        if (handle != IntPtr.Zero)
        {
            HcsNative.HcsCloseComputeSystem(handle);
        }

        if (hresult < 0)
        {
            throw HcsErrors.ToException(hresult, nameof(HcsNative.HcsOpenComputeSystem), id);
        }

        // A successful call must never yield a null handle; guard anyway so a
        // later dereference cannot AV (0xC0000005).
        throw new SandboxBackendException(
            $"HcsOpenComputeSystem returned a null compute system handle for '{id}'.", 0);
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
