namespace Wsm.Udk.Sandbox;

/// <summary>Raised when the UDK rejects an operation (create, start, exec, share, …).</summary>
public sealed class UdkSandboxException : Exception
{
    public UdkSandboxException(string message)
        : base(message)
    {
    }

    public UdkSandboxException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
