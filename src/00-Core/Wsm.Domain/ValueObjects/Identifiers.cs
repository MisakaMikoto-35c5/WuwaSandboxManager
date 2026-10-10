using WindowsSandboxManager.Domain.Exceptions;

namespace WindowsSandboxManager.Domain.ValueObjects;

/// <summary>
/// Strongly typed identity of a sandbox instance. This is the "SandboxId" that
/// <c>WindowsSandboxServer</c> (the gRPC service) uses to address an instance.
/// </summary>
public readonly record struct SandboxInstanceId(Guid Value)
{
    public static SandboxInstanceId New() => new(Guid.NewGuid());

    public static SandboxInstanceId From(Guid value) => new(value);

    public static bool TryParse(string? text, out SandboxInstanceId id)
    {
        if (Guid.TryParse(text, out var value))
        {
            id = new SandboxInstanceId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

/// <summary>Human friendly, validated display name for a sandbox instance.</summary>
public sealed record SandboxName
{
    public const int MaxLength = 64;
    public const string DefaultValue = "Windows Sandbox";

    public string Value { get; }

    public SandboxName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SandboxValidationException("Sandbox name must not be empty.");
        }

        value = value.Trim();
        if (value.Length > MaxLength)
        {
            throw new SandboxValidationException($"Sandbox name must be at most {MaxLength} characters.");
        }

        Value = value;
    }

    public static SandboxName Default() => new(DefaultValue);

    public override string ToString() => Value;
}
