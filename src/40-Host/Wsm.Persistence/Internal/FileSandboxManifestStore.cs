using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;

namespace Wsm.Persistence.Internal;

/// <summary>
/// File-system backed manifest store. Writes one <c>&lt;id&gt;.wsbx</c> JSON file
/// per instance under the <c>manifests</c> folder of the persistence root
/// (defaults to <c>%LOCALAPPDATA%\WindowsSandboxManager\manifests</c>).
/// </summary>
[ExposeServices(typeof(ISandboxManifestStore))]
internal sealed class FileSandboxManifestStore : ISandboxManifestStore, ISingletonDependency
{
    private readonly string _manifestsDirectory;

    public FileSandboxManifestStore(IOptions<SandboxPersistenceOptions> options)
    {
        var root = options.Value.RootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsSandboxManager");

        _manifestsDirectory = Path.Combine(root, "manifests");
        Directory.CreateDirectory(_manifestsDirectory);
    }

    public string GetManifestPath(SandboxInstanceId id)
        => Path.Combine(_manifestsDirectory, $"{id.Value:D}.wsbx");

    public async Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default)
    {
        var manifest = SandboxManifest.FromInstance(instance);
        await File.WriteAllTextAsync(GetManifestPath(instance.Id), manifest.ToJson(), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var path = GetManifestPath(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
