namespace Wsm.Udk.Sandbox;

/// <summary>How a guest process is launched (mirrors the UDK's <c>VMCreateProcessRunAs</c>).</summary>
public enum UdkProcessRunAs
{
    /// <summary>Run as the guest's SYSTEM account (image-prep / provisioning).</summary>
    System,

    /// <summary>
    /// Run as the specified user inside its already-established interactive
    /// logon session (the platform's <c>UseExistingLoginSession</c>).
    /// </summary>
    ExistingLogin,
}

/// <summary>Options for <see cref="IUdkVirtualMachine.Execute"/>.</summary>
public sealed record UdkProcessOptions
{
    public UdkProcessRunAs RunAs { get; init; } = UdkProcessRunAs.System;

    /// <summary>The account to run as. Required for <see cref="UdkProcessRunAs.ExistingLogin"/>.</summary>
    public string? UserName { get; init; }

    public string? WorkingDirectory { get; init; }
}
