using System.Diagnostics;
using Volo.Abp.DependencyInjection;

namespace Wsm.Connection.Session.Internal;

/// <summary>
/// Default <see cref="ISessionProcessLauncher"/>. Starts the client through the
/// shell so AppExecutionAlias reparse points resolve with the correct package
/// identity.
/// </summary>
[ExposeServices(typeof(ISessionProcessLauncher))]
internal sealed class ShellSessionProcessLauncher : ISessionProcessLauncher, ISingletonDependency
{
    public void Launch(ProcessStartInfo startInfo) => Process.Start(startInfo);
}
