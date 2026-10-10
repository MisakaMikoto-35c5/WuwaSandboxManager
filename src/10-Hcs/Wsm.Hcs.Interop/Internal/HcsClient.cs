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
        // 0x10000000 == GENERIC_ALL. HCS rejects an access mask of 0 with
        // E_INVALIDARG (0x80070057), which leaves a null handle that makes the
        // subsequent Start/Terminate AV (0xC0000005). GENERIC_ALL is what the
        // platform's own stack requests.
        const uint GenericAll = 0x10000000;
        var hr = HcsNative.HcsOpenComputeSystem(id, GenericAll, out var handle);
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
