using Wsm.Connection.Legacy.Internal;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers the transient-file housekeeping used by the legacy launcher: stale
/// <c>.wsb</c>/<c>.rdp</c> files are swept, fresh ones and unrelated files are
/// kept.
/// </summary>
public sealed class TemporarySandboxFilesTests
{
    [Fact]
    public void Sweep_removes_stale_transient_files_but_keeps_fresh_and_unrelated_ones()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);

        var staleRecipe = Path.Combine(directory, "stale.wsb");
        var staleRdp = Path.Combine(directory, "stale.rdp");
        var freshRecipe = Path.Combine(directory, "fresh.wsb");
        var unrelated = Path.Combine(directory, "keep.txt");

        try
        {
            File.WriteAllText(staleRecipe, "<Configuration/>");
            File.WriteAllText(staleRdp, "full address:s:foo");
            File.WriteAllText(freshRecipe, "<Configuration/>");
            File.WriteAllText(unrelated, "keep me");

            var old = DateTime.UtcNow - TimeSpan.FromHours(24);
            File.SetLastWriteTimeUtc(staleRecipe, old);
            File.SetLastWriteTimeUtc(staleRdp, old);

            TemporarySandboxFiles.Sweep(directory, TimeSpan.FromHours(6));

            Assert.False(File.Exists(staleRecipe));
            Assert.False(File.Exists(staleRdp));
            Assert.True(File.Exists(freshRecipe));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PathFor_uses_the_instance_id_and_extension()
    {
        var id = SandboxInstanceId.New();

        var path = TemporarySandboxFiles.PathFor(@"C:\temp", id, TemporarySandboxFiles.RecipeExtension);

        Assert.Equal(Path.Combine(@"C:\temp", $"{id.Value:D}.wsb"), path);
    }

    [Fact]
    public void Sweep_is_a_no_op_for_a_missing_directory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid());

        var exception = Record.Exception(() => TemporarySandboxFiles.Sweep(missing, TimeSpan.Zero));

        Assert.Null(exception);
    }
}
