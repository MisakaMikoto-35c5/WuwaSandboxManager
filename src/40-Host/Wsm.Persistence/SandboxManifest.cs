using System.Text.Json;
using System.Text.Json.Serialization;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;

namespace Wsm.Persistence;

/// <summary>
/// The on-disk format of a per-sandbox manifest (<c>&lt;id&gt;.wsbx</c>).
/// <para>
/// A manifest is a small, self-contained JSON document: it identifies the sandbox
/// (<see cref="Id"/>, <see cref="Name"/>, <see cref="RailMode"/>) and embeds the
/// full <see cref="Recipe"/> as a nested JSON object. The recipe is serialized as
/// the same DTO shape the instance repository persists (System.Text.Json), not as
/// the <c>.wsb</c> XML, so a manifest can be re-imported without loss.
/// </para>
/// </summary>
public sealed record SandboxManifest
{
    /// <summary>Format identifier/version so future readers can migrate.</summary>
    public const string CurrentSchema = "wsm.sandbox/1";

    /// <summary>Format identifier; always <see cref="CurrentSchema"/> for files we write.</summary>
    public string Schema { get; init; } = CurrentSchema;

    /// <summary>The sandbox instance id (matches the aggregate and the repository record).</summary>
    public Guid Id { get; init; }

    /// <summary>The display name of the sandbox.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>True for RemoteApp/RAIL windows, false for a full desktop session.</summary>
    public bool RailMode { get; init; }

    /// <summary>The preferred sandbox engine, or <c>null</c> for automatic selection.</summary>
    public SandboxEngineKind? Engine { get; init; }

    /// <summary>Human-readable type derived from <see cref="RailMode"/> (not persisted).</summary>
    [JsonIgnore]
    public string Type => RailMode ? "RemoteApp" : "FullDesktop";

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>The complete recipe, embedded as a nested JSON object.</summary>
    public SandboxRecipe Recipe { get; init; } = SandboxRecipe.Default;

    /// <summary>Projects a live aggregate onto its manifest.</summary>
    public static SandboxManifest FromInstance(SandboxInstance instance) => new()
    {
        Id = instance.Id.Value,
        Name = instance.Name.Value,
        RailMode = instance.Recipe.RailMode,
        Engine = instance.Engine,
        CreatedUtc = instance.CreatedUtc,
        UpdatedUtc = instance.UpdatedUtc,
        Recipe = instance.Recipe,
    };

    /// <summary>Serializes this manifest to indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Parses a manifest from JSON, returning <c>null</c> when invalid.</summary>
    public static SandboxManifest? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SandboxManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads and parses a manifest file, returning <c>null</c> when missing or invalid.</summary>
    public static SandboxManifest? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
