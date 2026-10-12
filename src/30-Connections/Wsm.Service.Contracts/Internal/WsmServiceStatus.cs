using Grpc.Core;
using Wsm.Domain.Exceptions;

namespace Wsm.Service.Contracts.Internal;

/// <summary>
/// Server-side error convention: maps the domain exceptions used by the
/// application layer onto a gRPC <see cref="Status"/>. The original exception is
/// preserved as <see cref="Status.DebugException"/> (and its type name embedded
/// in <see cref="Status.Detail"/>) so the client can reconstruct it — see
/// <see cref="WsmServiceExceptionMapper"/>.
/// </summary>
public static class WsmServiceStatus
{
    /// <summary>HRESULT of a Windows service that is unavailable (<c>RPC_S_SERVER_UNAVAILABLE</c>).</summary>
    public const int ServerUnavailable = unchecked((int)0x800706BA);

    /// <summary>Separator between the embedded exception type name and the message.</summary>
    public const string TypeNameSeparator = ": ";

    /// <summary>
    /// Maps an exception raised by the service to a gRPC <see cref="Status"/>:
    /// <list type="bullet">
    /// <item><see cref="SandboxNotFoundException"/> -&gt; <see cref="StatusCode.NotFound"/></item>
    /// <item><see cref="SandboxNotSupportedException"/>, <see cref="SandboxValidationException"/>,
    /// <see cref="MultipleInstancesNotAllowedException"/> -&gt; <see cref="StatusCode.FailedPrecondition"/></item>
    /// <item>everything else (incl. <see cref="SandboxBackendException"/> for <em>any</em> HResult,
    /// even <see cref="ServerUnavailable"/>) -&gt; <see cref="StatusCode.Unknown"/></item>
    /// </list>
    /// <see cref="StatusCode.Unavailable"/> is deliberately never produced here: it is reserved
    /// for genuine transport failures reported by gRPC itself. Mapping a domain
    /// <see cref="SandboxBackendException"/> (e.g. <c>RPC_S_SERVER_UNAVAILABLE</c> raised by the
    /// UDK/HCS stack while the service is alive) to <see cref="StatusCode.Unavailable"/> would make
    /// the client fall back to an in-process run and silently double-execute the operation.
    /// </summary>
    public static Status ToStatus(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var code = exception switch
        {
            SandboxNotFoundException => StatusCode.NotFound,
            SandboxNotSupportedException => StatusCode.FailedPrecondition,
            SandboxValidationException => StatusCode.FailedPrecondition,
            MultipleInstancesNotAllowedException => StatusCode.FailedPrecondition,
            _ => StatusCode.Unknown,
        };

        return new Status(code, Describe(exception), exception);
    }

    /// <summary>
    /// Formats the status detail as <c>"{ExceptionTypeName}: {Message}"</c> so the
    /// client mapper can recover the specific domain exception type.
    /// </summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.GetType().Name + TypeNameSeparator + exception.Message;
    }
}
