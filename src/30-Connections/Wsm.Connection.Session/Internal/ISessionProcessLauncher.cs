using System.Diagnostics;

namespace Wsm.Connection.Session.Internal;

/// <summary>
/// Launches the user-visible client process. Abstracted so the routing logic in
/// <see cref="SessionLauncher"/> can be unit-tested without spawning processes.
/// </summary>
internal interface ISessionProcessLauncher
{
    void Launch(ProcessStartInfo startInfo);
}
