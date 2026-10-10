using Wsm.Domain.ValueObjects;

namespace Wsm.Domain.Events;

public sealed record SandboxInstanceCreatedEvent(SandboxInstanceId Id, SandboxName Name) : DomainEvent;

public sealed record SandboxStartedEvent(SandboxInstanceId Id, SandboxRuntimeInfo Runtime) : DomainEvent;

public sealed record SandboxStoppedEvent(SandboxInstanceId Id) : DomainEvent;

public sealed record SandboxTerminatedEvent(SandboxInstanceId Id, SandboxTerminationReason Reason) : DomainEvent;

public sealed record SandboxConfigurationChangedEvent(SandboxInstanceId Id, SandboxRecipe Recipe) : DomainEvent;

public sealed record SandboxFolderMappedEvent(SandboxInstanceId Id, MappedFolder Folder) : DomainEvent;

public sealed record SandboxFaultedEvent(SandboxInstanceId Id, string Reason) : DomainEvent;
