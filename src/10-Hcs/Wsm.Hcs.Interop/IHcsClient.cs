namespace Wsm.Hcs.Interop;

/// <summary>
/// Thin, reusable wrapper over the Host Compute Service (HCS). Drives
/// <c>computecore.dll</c> with the operation based API: create/start/stop/
/// terminate, property inspection, enumeration and save (checkpoint) operations.
/// </summary>
public interface IHcsClient
{
    IReadOnlyList<HcsComputeSystemInfo> List(string? query = null);

    HcsComputeSystemInfo? Get(string id);

    /// <summary>
    /// Creates a compute system and returns an RAII handle that owns the native
    /// <c>HCS_SYSTEM</c>. Disposing the handle closes it.
    /// </summary>
    IDisposable Create(string id, string configuration);

    void Start(string id);

    void Shutdown(string id);

    void Terminate(string id);

    string? Save(string id, string options);

    void CreateEmptyGuestStateFile(string path);

    void CreateEmptyRuntimeStateFile(string path);

    void GrantVmAccess(string id, string path);
}
