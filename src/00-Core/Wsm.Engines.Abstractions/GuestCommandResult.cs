namespace Wsm.Engines.Abstractions;

/// <summary>
/// The outcome of running a command inside a sandbox guest: the process exit
/// code plus the captured standard output and error. Engines that can only
/// observe an exit code return empty streams.
/// </summary>
/// <param name="ExitCode">The guest process exit code.</param>
/// <param name="StandardOutput">Captured standard output (empty when the engine cannot capture it).</param>
/// <param name="StandardError">Captured standard error (empty when the engine cannot capture it).</param>
public sealed record GuestCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>A result that carries only an exit code (no captured output).</summary>
    public static GuestCommandResult FromExitCode(int exitCode) => new(exitCode, string.Empty, string.Empty);
}
