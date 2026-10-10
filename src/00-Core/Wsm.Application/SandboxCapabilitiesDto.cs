using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Application;

/// <summary>
/// The capabilities reported by the engine that would serve a default recipe,
/// together with its identity and current availability. Used by the
/// presentation layers to show which backend is active and what it supports.
/// </summary>
public sealed record SandboxCapabilitiesDto(
    string BackendName,
    SandboxEngineKind Kind,
    bool IsAvailable,
    SandboxEngineCapabilities Capabilities);
