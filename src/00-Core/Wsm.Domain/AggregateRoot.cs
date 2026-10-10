namespace WindowsSandboxManager.Domain;

/// <summary>Marker for a domain event.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAtUtc { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Minimal DDD aggregate root with an in-memory domain-event buffer.
/// Kept dependency free so it can be reused by any bounded context.
/// </summary>
public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _domainEvents = new();

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
