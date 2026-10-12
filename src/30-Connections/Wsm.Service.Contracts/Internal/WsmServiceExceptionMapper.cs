using Grpc.Core;
using Wsm.Domain.Exceptions;

namespace Wsm.Service.Contracts.Internal;

/// <summary>
/// Client-side counterpart of <see cref="WsmServiceStatus"/>: turns a caught
/// <see cref="RpcException"/> back into the matching <c>Wsm.Domain.Exceptions</c>
/// type so callers can keep handling domain exceptions regardless of whether the
/// work ran in-process or over the pipe.
/// </summary>
public static class WsmServiceExceptionMapper
{
    private static readonly string[] KnownTypeNames =
    {
        nameof(SandboxNotFoundException),
        nameof(SandboxNotSupportedException),
        nameof(SandboxValidationException),
        nameof(MultipleInstancesNotAllowedException),
        nameof(SandboxBackendException),
        nameof(SandboxDomainException),
    };

    /// <summary>
    /// Reconstructs a domain exception from an <see cref="RpcException"/>:
    /// <see cref="StatusCode.NotFound"/> -&gt; <see cref="SandboxNotFoundException"/>,
    /// <see cref="StatusCode.FailedPrecondition"/> -&gt; the most specific of
    /// <see cref="SandboxNotSupportedException"/> / <see cref="SandboxValidationException"/> /
    /// <see cref="MultipleInstancesNotAllowedException"/> carried in the status detail,
    /// and anything else (incl. <see cref="StatusCode.Unknown"/>) -&gt;
    /// <see cref="SandboxBackendException"/>.
    /// </summary>
    public static SandboxDomainException ToDomainException(RpcException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var detail = exception.Status.Detail;
        var typeName = ReadTypeName(detail);
        var message = ReadMessage(detail);

        return exception.StatusCode switch
        {
            StatusCode.NotFound => new SandboxNotFoundException(message),
            StatusCode.FailedPrecondition => typeName switch
            {
                nameof(SandboxNotSupportedException) => new SandboxNotSupportedException(message),
                nameof(SandboxValidationException) => new SandboxValidationException(message),
                nameof(MultipleInstancesNotAllowedException) => new MultipleInstancesNotAllowedException(message),
                _ => new SandboxBackendException(message, 0, exception),
            },
            // Transport-level statuses: the agent never answered. The server maps
            // every domain failure through WsmServiceStatus (NotFound /
            // FailedPrecondition / Unknown with a type-name detail), so these can
            // only originate client-side:
            //  - Unavailable: connection refused/reset (e.g. stale pipe after restart).
            //  - Cancelled: the named-pipe connect aborted against a dead pipe
            //    (ConnectTimeout) before any bytes were exchanged.
            //  - Internal: SocketsHttpHandler wrapped the connect failure in an
            //    HttpRequestException (observed on warm channels).
            // They carry the ServerUnavailable sentinel so the CLI can distinguish
            // "agent unreachable" (hint / in-process fallback) from real backend
            // failures (Unknown -> HResult 0 -> surface, never silently re-run).
            StatusCode.Unavailable => new SandboxBackendException(message, WsmServiceStatus.ServerUnavailable, exception),
            StatusCode.Cancelled => new SandboxBackendException(message, WsmServiceStatus.ServerUnavailable, exception),
            StatusCode.Internal => new SandboxBackendException(message, WsmServiceStatus.ServerUnavailable, exception),
            _ => new SandboxBackendException(message, 0, exception),
        };
    }

    /// <summary>Extracts the original exception message from a status detail, if present.</summary>
    private static string ReadMessage(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return "The WSM agent service call failed.";
        }

        var typeName = ReadTypeName(detail);
        if (typeName is not null)
        {
            return detail[(typeName.Length + WsmServiceStatus.TypeNameSeparator.Length)..];
        }

        return detail;
    }

    /// <summary>Recovers the embedded domain exception type name, or <c>null</c> when absent.</summary>
    private static string? ReadTypeName(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }

        foreach (var typeName in KnownTypeNames)
        {
            if (detail.StartsWith(typeName + WsmServiceStatus.TypeNameSeparator, StringComparison.Ordinal))
            {
                return typeName;
            }
        }

        return null;
    }
}
