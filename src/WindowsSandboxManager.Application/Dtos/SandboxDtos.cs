using WindowsSandboxManager.Domain;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.Application.Dtos;

/// <summary>
/// Read model for a sandbox instance. The domain value objects are immutable
/// records, so they double as transport DTOs here to keep mapping friction low.
/// </summary>
public sealed record SandboxInstanceDto(
    Guid Id,
    string Name,
    SandboxState State,
    SandboxRecipe Recipe,
    SandboxRuntimeInfo? Runtime,
    string? FaultReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>Capabilities reported by the selected backend + host environment.</summary>
public sealed record SandboxCapabilitiesDto(
    string BackendName,
    SandboxBackendCapabilities Backend,
    SandboxEnvironmentInfo Environment);

public sealed record SandboxEnvironmentInfo(
    bool IsSupported,
    bool IsElevated,
    bool IsMultipleInstancesAllowed,
    string? PackageInstallPath,
    string? ServerPipeName,
    string? LegacyExecutablePath,
    string? RemoteSessionExecutablePath,
    string? ServerExecutablePath);

/// <summary>Request used to create a new sandbox definition.</summary>
public sealed record CreateSandboxRequest(string? Name = null, SandboxRecipe? Recipe = null);

/// <summary>Request used to replace the configuration of an existing sandbox.</summary>
public sealed record UpdateSandboxRequest(SandboxRecipe Recipe);
