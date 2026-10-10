using System.Text.Json;
using System.Text.Json.Serialization;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;

namespace Wsm.Persistence;

/// <summary>
/// The on-disk format of a sandbox: a single, self-contained
/// <c>&lt;name&gt;.wsm</c> JSON document.
/// <para>
/// A <c>.wsm</c> embeds the complete <see cref="Recipe"/> (engine preference,
/// type, resources, mapped folders, logon commands) and stores the user-data
/// folder as a path <b>relative to the file</b>, so the whole folder can be
/// moved to a USB stick or another PC. Nothing container-related is stored
/// outside this file.
/// </para>
/// </summary>
public sealed record SandboxManifest
{
    /// <summary>Format identifier/version so future readers can migrate.</summary>
    public const string CurrentSchema = "wsm.sandbox/2";

    /// <summary>The file extension of a portable sandbox file.</summary>
    public const string FileExtension = ".wsm";

    /// <summary>Format identifier; always <see cref="CurrentSchema"/> for files we write.</summary>
    public string Schema { get; init; } = CurrentSchema;

    /// <summary>The sandbox instance id (also the identity used by the engines).</summary>
    public Guid Id { get; init; }

    /// <summary>The display name of the sandbox.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The preferred sandbox engine, or <c>null</c> for automatic selection.</summary>
    public SandboxEngineKind? Engine { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>The complete recipe, embedded as a nested JSON object.</summary>
    public SandboxRecipe Recipe { get; init; } = SandboxRecipe.Default;

    /// <summary>
    /// The user-data folder, stored <b>relative to this file</b> so the sandbox
    /// stays portable. Null/empty means "next to the <c>.wsm</c>".
    /// </summary>
    public string? DataPath { get; init; }

    /// <summary>Human-readable type derived from the recipe (not persisted).</summary>
    [JsonIgnore]
    public string Type => Recipe.RailMode ? "RemoteApp" : "FullDesktop";

    /// <summary>Projects a live aggregate onto its manifest.</summary>
    public static SandboxManifest FromInstance(SandboxInstance instance) => new()
    {
        Id = instance.Id.Value,
        Name = instance.Name.Value,
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
