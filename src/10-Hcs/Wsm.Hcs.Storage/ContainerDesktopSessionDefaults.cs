using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Wsm.Hcs.Storage;

/// <summary>
/// The account and per-image credential derivation shared by offline image customization
/// and the engine's start-time desktop configuration.
/// </summary>
public static class ContainerDesktopSessionDefaults
{
    private const int KeySize = 32;
    private const string KeyOverrideVariable = "WSM_CONTAINER_DESKTOP_KEY";

    /// <summary>
    /// The account auto-logon signs in as. Measured: this must be the image's SAM
    /// <c>Administrator</c> - <c>ContainerAdministrator</c> is a virtual account the
    /// User Manager service synthesizes for exec processes (S-1-5-93-2-1); it is not
    /// in the SAM, so winlogon cannot log it on, and <c>net user</c> rejects the name
    /// outright (21 characters, SAM names are capped at 20 - the command exits with a
    /// syntax error).
    /// </summary>
    public const string DefaultUserName = "Administrator";

    /// <summary>Legacy compatibility value; null requests per-image derivation.</summary>
    public const string? DefaultPassword = null;

    /// <summary>Derives a stable 256-bit auto-logon password for a normalized image path.</summary>
    public static string GetPassword(string layerPath)
    {
        var normalizedLayer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(layerPath))
            .ToUpperInvariant();
        var keyPath = GetKeyPath();
        var key = ReadOrCreateKey(keyPath);
        var password = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalizedLayer));
        return Convert.ToBase64String(password);
    }

    private static string GetKeyPath()
    {
        var overridden = Environment.GetEnvironmentVariable(KeyOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WindowsSandboxManager",
            "secrets",
            "container-desktop.key");
    }

    private static byte[] ReadOrCreateKey(string keyPath)
    {
        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new InvalidOperationException("The desktop key path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var key = File.Exists(keyPath) ? ReadKey(keyPath) : CreateKey(keyPath);
        SecureFile(keyPath);
        SecureDirectory(directory);
        return key;
    }

    private static byte[] ReadKey(string keyPath)
    {
        var key = File.ReadAllBytes(keyPath);
        if (key.Length != KeySize)
        {
            throw new InvalidDataException(
                $"The container desktop key '{keyPath}' must contain exactly {KeySize} bytes.");
        }

        return key;
    }

    private static byte[] CreateKey(string keyPath)
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            using var stream = new FileInfo(keyPath).Create(
                FileMode.CreateNew,
                FileSystemRights.Write,
                FileShare.None,
                4096,
                FileOptions.None,
                CreateFileSecurity());
            stream.Write(key);
            return key;
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            return ReadKey(keyPath);
        }
    }

    private static void SecureDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in KeyRules(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
        {
            security.AddAccessRule(rule);
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void SecureFile(string path) =>
        new FileInfo(path).SetAccessControl(CreateFileSecurity());

    private static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in KeyRules(InheritanceFlags.None))
        {
            security.AddAccessRule(rule);
        }

        return security;
    }

    private static FileSystemAccessRule[] KeyRules(InheritanceFlags inheritance) =>
    [
        new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow),
        new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow),
    ];
}
