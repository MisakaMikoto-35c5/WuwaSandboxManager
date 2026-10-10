using Wsm.Domain;
using Wsm.Domain.ValueObjects;

namespace Wsm.Application;

/// <summary>
/// A projection of a sandbox instance for the presentation layers, combining
/// its persisted definition with live runtime information when available.
/// </summary>
public sealed record SandboxInstanceDto(
    Guid Id,
    string Name,
    SandboxState State,
    SandboxRecipe Recipe,
    SandboxRuntimeInfo? Runtime,
    string? FaultReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    SandboxEngineKind? Engine = null,
    string? ManifestPath = null);
