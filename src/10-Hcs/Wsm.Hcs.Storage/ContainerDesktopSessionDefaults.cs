namespace Wsm.Hcs.Storage;

/// <summary>
/// The defaults the offline image customization (<c>wsm image session</c>) and the
/// engine's start-time desktop configuration must agree on. Auto-logon only succeeds
/// when the password baked into the image (<c>Winlogon\DefaultPassword</c>) matches the
/// account's runtime password, which the engine sets with <c>net user</c> at container
/// start - so the value lives in exactly one place.
/// </summary>
public static class ContainerDesktopSessionDefaults
{
    /// <summary>
    /// The account auto-logon signs in as. Measured: this must be the image's SAM
    /// <c>Administrator</c> - <c>ContainerAdministrator</c> is a virtual account the
    /// User Manager service synthesizes for exec processes (S-1-5-93-2-1); it is not
    /// in the SAM, so winlogon cannot log it on, and <c>net user</c> rejects the name
    /// outright (21 characters, SAM names are capped at 20 - the command exits with a
    /// syntax error).
    /// </summary>
    public const string DefaultUserName = "Administrator";

    /// <summary>The password both sides configure when none is given explicitly.</summary>
    public const string DefaultPassword = "Wsm!Container2025";
}
