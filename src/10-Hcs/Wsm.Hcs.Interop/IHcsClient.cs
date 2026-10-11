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

    /// <summary>
    /// Opens an <b>existing</b> compute system and returns an RAII handle that
    /// owns the native <c>HCS_SYSTEM</c>. Used to adopt a system this process did
    /// not create (for example one started by another process and still running),
    /// so that starting a sandbox that is already up is idempotent rather than an
    /// "already exists" failure.
    /// </summary>
    IDisposable Open(string id);

    void Start(string id);

    void Shutdown(string id);

    void Terminate(string id);

    string? Save(string id, string options);

    void CreateEmptyGuestStateFile(string path);

    void CreateEmptyRuntimeStateFile(string path);

    void GrantVmAccess(string id, string path);

    /// <summary>
    /// Runs a process inside the compute system (via <c>HcsCreateProcess</c>) and
    /// returns its exit code and captured output. <paramref name="user"/> selects
    /// the guest account; when null the system's default (SYSTEM) is used. Used to
    /// provision the guest logon account so the sandbox can be reached over RDP,
    /// mirroring the platform's own <c>net user</c> set-up.
    /// </summary>
    HcsProcessResult RunProcess(string id, string commandLine, string? user = null, uint timeoutMs = 120_000);
}

/// <summary>The outcome of a process run inside an HCS compute system.</summary>
public sealed record HcsProcessResult(int ExitCode, string StandardOutput, string StandardError);
