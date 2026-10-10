using Wsm.Domain.Events;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;

namespace Wsm.Domain;

/// <summary>
/// Aggregate root representing a single, addressable Windows Sandbox instance.
/// It owns the immutable <see cref="SandboxRecipe"/> configuration and the
/// lifecycle state machine, and it guards the invariants around start/stop and
/// runtime information.
/// </summary>
public sealed class SandboxInstance : AggregateRoot
{
    public SandboxInstanceId Id { get; }

    public SandboxName Name { get; private set; }

    public SandboxRecipe Recipe { get; private set; }

    public SandboxState State { get; private set; }

    public SandboxRuntimeInfo? Runtime { get; private set; }

    /// <summary>
    /// The engine chosen when the sandbox was created, or <c>null</c> for
    /// automatic selection (the selector's HCS-first routing). Honoured as a
    /// preference whenever the instance is started or connected.
    /// </summary>
    public SandboxEngineKind? Engine { get; private set; }

    public string? FaultReason { get; private set; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    private SandboxInstance(SandboxInstanceId id, SandboxName name, SandboxRecipe recipe, DateTimeOffset createdUtc)
    {
        Id = id;
        Name = name;
        Recipe = recipe;
        State = SandboxState.Created;
        CreatedUtc = createdUtc;
        UpdatedUtc = createdUtc;
    }

    /// <summary>Factory: creates a new, not-yet-started sandbox definition.</summary>
    public static SandboxInstance Create(
        SandboxName? name = null,
        SandboxRecipe? recipe = null,
        SandboxInstanceId? id = null,
        SandboxEngineKind? engine = null)
    {
        var actualId = id ?? SandboxInstanceId.New();
        var actualName = name ?? SandboxName.Default();
        var actualRecipe = (recipe ?? SandboxRecipe.Default) with { Name = actualName };

        var instance = new SandboxInstance(actualId, actualName, actualRecipe, DateTimeOffset.UtcNow)
        {
            Engine = engine,
        };
        instance.Raise(new SandboxInstanceCreatedEvent(actualId, actualName));
        return instance;
    }

    /// <summary>Rehydrates an aggregate from persistence without raising events.</summary>
    public static SandboxInstance Rehydrate(
        SandboxInstanceId id,
        SandboxName name,
        SandboxRecipe recipe,
        SandboxState state,
        SandboxRuntimeInfo? runtime,
        string? faultReason,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        SandboxEngineKind? engine = null)
    {
        var instance = new SandboxInstance(id, name, recipe, createdUtc)
        {
            State = state,
            Runtime = runtime,
            FaultReason = faultReason,
            UpdatedUtc = updatedUtc,
            Engine = engine,
        };
        return instance;
    }

    public void BeginStart()
    {
        if (State.IsLive())
        {
            throw new SandboxDomainException($"Sandbox '{Id}' is already running.");
        }

        State = SandboxState.Starting;
        FaultReason = null;
        Touch();
    }

    public void MarkRunning(SandboxRuntimeInfo runtime)
    {
        if (runtime.SandboxId != Id)
        {
            throw new SandboxValidationException("Runtime info does not belong to this sandbox instance.");
        }

        Runtime = runtime;
        State = SandboxState.Running;
        FaultReason = null;
        Touch();
        Raise(new SandboxStartedEvent(Id, runtime));
    }

    public void MarkRebooting() => TransitionTo(SandboxState.Rebooting);

    public void MarkStopped()
    {
        State = SandboxState.Stopped;
        Runtime = null;
        Touch();
        Raise(new SandboxStoppedEvent(Id));
    }

    public void MarkTerminated(SandboxTerminationReason reason)
    {
        State = SandboxState.Terminated;
        Runtime = null;
        Touch();
        Raise(new SandboxTerminatedEvent(Id, reason));
    }

    public void MarkFaulted(string reason)
    {
        State = SandboxState.Faulted;
        FaultReason = reason;
        Touch();
        Raise(new SandboxFaultedEvent(Id, reason));
    }

    public void Rename(SandboxName name)
    {
        Name = name;
        Recipe = Recipe with { Name = name };
        Touch();
    }

    /// <summary>Replaces the configuration. Only allowed while not running.</summary>
    public void ChangeConfiguration(SandboxRecipe recipe)
    {
        if (State.IsLive())
        {
            throw new SandboxDomainException("Configuration cannot be changed while the sandbox is running.");
        }

        Recipe = recipe with { Name = Name };
        Touch();
        Raise(new SandboxConfigurationChangedEvent(Id, Recipe));
    }

    /// <summary>
    /// Sets the preferred engine, or clears it (with <c>null</c>) back to
    /// automatic selection. Only allowed while not running.
    /// </summary>
    public void SetEngine(SandboxEngineKind? engine)
    {
        if (State.IsLive())
        {
            throw new SandboxDomainException("The engine cannot be changed while the sandbox is running.");
        }

        Engine = engine;
        Touch();
    }

    /// <summary>Records a folder mapping (runtime mapping requires a running instance).</summary>
    public void RecordMappedFolder(MappedFolder folder)
    {
        Recipe = Recipe.WithMappedFolder(folder);
        Touch();
        Raise(new SandboxFolderMappedEvent(Id, folder));
    }

    public void RemoveMappedFolder(MappedFolder folder)
    {
        Recipe = Recipe.WithoutMappedFolder(folder);
        Touch();
    }

    public void AttachRuntime(SandboxRuntimeInfo runtime)
    {
        Runtime = runtime;
        Touch();
    }

    private void TransitionTo(SandboxState state)
    {
        State = state;
        Touch();
    }

    private void Touch() => UpdatedUtc = DateTimeOffset.UtcNow;
}
