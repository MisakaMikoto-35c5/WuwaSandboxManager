using System.Text.Json;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;
using Wsm.Persistence.Internal;
using Xunit;

namespace Wsm.Tests.Persistence;

/// <summary>
/// Covers the persisted instance record's engine field. The record is what
/// <c>FileSandboxRepository</c> serializes as JSON, so a JSON round-trip proves
/// the pinned engine survives a save/load cycle.
/// </summary>
public sealed class SandboxInstanceRecordTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(SandboxEngineKind.HcsSandbox)]
    [InlineData(SandboxEngineKind.WindowsSandbox)]
    [InlineData(SandboxEngineKind.WindowsContainer)]
    public void Engine_round_trips_through_json(SandboxEngineKind? engine)
    {
        var record = new SandboxInstanceRecord(
            Guid.NewGuid(),
            "Box",
            SandboxRecipe.Default,
            SandboxState.Created,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            engine);

        var json = JsonSerializer.Serialize(record);
        var parsed = JsonSerializer.Deserialize<SandboxInstanceRecord>(json);

        Assert.NotNull(parsed);
        Assert.Equal(engine, parsed!.Engine);
    }
}
