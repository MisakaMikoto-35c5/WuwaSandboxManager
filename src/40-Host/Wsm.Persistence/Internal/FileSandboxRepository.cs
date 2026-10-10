using System.Text.Json;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Recipe.Serialization;

namespace Wsm.Persistence.Internal;

/// <summary>
/// File-system backed repository. Each instance is persisted as JSON (full
/// aggregate state) plus a human-editable <c>.wsb</c> recipe that can be opened
/// directly by <c>WindowsSandbox.exe</c>.
/// </summary>
[ExposeServices(typeof(ISandboxInstanceRepository))]
internal sealed class FileSandboxRepository : ISandboxInstanceRepository, ISingletonDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ISandboxRecipeSerializer _serializer;
    private readonly string _instancesDirectory;
    private readonly string _recipesDirectory;

    public FileSandboxRepository(
        IOptions<SandboxPersistenceOptions> options,
        ISandboxRecipeSerializer serializer)
    {
        _serializer = serializer;

        var root = options.Value.RootDirectory ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "WindowsSandboxManager");

        _instancesDirectory = Path.Combine(root, "instances");
        _recipesDirectory = Path.Combine(root, "recipes");
        Directory.CreateDirectory(_instancesDirectory);
        Directory.CreateDirectory(_recipesDirectory);
    }

    public async Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default)
    {
        var record = new SandboxInstanceRecord(
            instance.Id.Value,
            instance.Name.Value,
            instance.Recipe,
            instance.State,
            instance.Runtime,
            instance.FaultReason,
            instance.CreatedUtc,
            instance.UpdatedUtc);

        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.WriteAllTextAsync(InstancePath(instance.Id), json, cancellationToken).ConfigureAwait(false);

        var wsb = _serializer.Serialize(instance.Recipe);
        await File.WriteAllTextAsync(RecipePath(instance.Id), wsb, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SandboxInstance?> GetAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var path = InstancePath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var record = JsonSerializer.Deserialize<SandboxInstanceRecord>(json, JsonOptions);
        if (record is null)
        {
            return null;
        }

        return SandboxInstance.Rehydrate(
            new SandboxInstanceId(record.Id),
            new SandboxName(record.Name),
            record.Recipe,
            record.State,
            record.Runtime,
            record.FaultReason,
            record.CreatedUtc,
            record.UpdatedUtc);
    }

    public async Task<IReadOnlyList<SandboxInstance>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<SandboxInstance>();
        foreach (var file in Directory.EnumerateFiles(_instancesDirectory, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize<SandboxInstanceRecord>(json, JsonOptions);
            if (record is null)
            {
                continue;
            }

            results.Add(SandboxInstance.Rehydrate(
                new SandboxInstanceId(record.Id),
                new SandboxName(record.Name),
                record.Recipe,
                record.State,
                record.Runtime,
                record.FaultReason,
                record.CreatedUtc,
                record.UpdatedUtc));
        }

        return results.OrderBy(i => i.CreatedUtc).ToArray();
    }

    public Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        TryDelete(InstancePath(id));
        TryDelete(RecipePath(id));
        return Task.CompletedTask;
    }

    private string InstancePath(SandboxInstanceId id)
        => Path.Combine(_instancesDirectory, $"{id.Value:D}.json");

    private string RecipePath(SandboxInstanceId id)
        => Path.Combine(_recipesDirectory, $"{id.Value:D}.wsb");

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

internal sealed record SandboxInstanceRecord(
    Guid Id,
    string Name,
    SandboxRecipe Recipe,
    SandboxState State,
    SandboxRuntimeInfo? Runtime,
    string? FaultReason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
