namespace WindowsSandboxManager.Domain.Exceptions;

/// <summary>Base type for all domain level failures.</summary>
public class SandboxDomainException : Exception
{
    public SandboxDomainException(string message) : base(message) { }

    public SandboxDomainException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Raised when an invariant or validation rule is violated.</summary>
public sealed class SandboxValidationException : SandboxDomainException
{
    public SandboxValidationException(string message) : base(message) { }
}

/// <summary>Raised when the Windows Sandbox feature is not available on the host.</summary>
public sealed class SandboxNotSupportedException : SandboxDomainException
{
    public SandboxNotSupportedException(string message) : base(message) { }
}

/// <summary>
/// Raised when a backend refuses to create an instance because the host policy
/// (licensing) only allows a single concurrent sandbox
/// (the <c>CO_E_APPSINGLEUSE</c> failure seen from WindowsSandboxServer).
/// </summary>
public sealed class MultipleInstancesNotAllowedException : SandboxDomainException
{
    public const int CoEAppSingleUse = unchecked((int)0x800401F6);

    public MultipleInstancesNotAllowedException(string message) : base(message) { }
}

/// <summary>Raised when an instance id cannot be resolved by the backend.</summary>
public sealed class SandboxNotFoundException : SandboxDomainException
{
    public SandboxNotFoundException(string message) : base(message) { }
}

/// <summary>Wraps a backend/HCS/HRESULT failure in a domain friendly exception.</summary>
public sealed class SandboxBackendException : SandboxDomainException
{
    public new int HResult { get; }

    public SandboxBackendException(string message, int hresult) : base(message)
    {
        HResult = hresult;
    }

    public SandboxBackendException(string message, int hresult, Exception inner) : base(message, inner)
    {
        HResult = hresult;
    }
}
