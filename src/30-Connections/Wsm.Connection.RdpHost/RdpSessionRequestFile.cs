using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// Persists an <see cref="RdpSessionRequest"/> to a small JSON file so a
/// connector that runs out-of-process (the WinUI 3 connector) can be handed the
/// connection parameters without them appearing on the command line.
/// </summary>
public static class RdpSessionRequestFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The default directory for request files under <c>%LOCALAPPDATA%</c>.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsSandboxManager",
        "rdp-requests");

    /// <summary>Writes <paramref name="request"/> to a new JSON file and returns its path.</summary>
    public static string Save(RdpSessionRequest request, string? directory = null)
    {
        var actual = directory ?? DefaultDirectory;
        Directory.CreateDirectory(actual);

        var path = Path.Combine(actual, $"{request.SandboxId:D}-{Guid.NewGuid():N}.json");
        var json = JsonSerializer.Serialize(request, JsonOptions);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Reads the request back from <paramref name="path"/>.</summary>
    public static RdpSessionRequest Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RdpSessionRequest>(json, JsonOptions)
            ?? throw new InvalidOperationException($"The RDP session request file '{path}' is empty.");
    }

    /// <summary>Deletes the request file (best effort).</summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort: a leftover temp file is harmless.
        }
    }
}
