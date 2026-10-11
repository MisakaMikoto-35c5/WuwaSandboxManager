using Wsm.Domain.Exceptions;
using Wsm.Hcs.Process.Internal;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Covers the parsing of the HCS <c>ProcessStatus</c> document returned by
/// <c>HcsWaitForProcessExit</c>. The document is trusted for its shape only when
/// it actually carries an <c>ExitCode</c>; anything else must fail loudly rather
/// than masquerade as a successful exit. The exit code is a <c>uint32</c>, so
/// status/crash codes above <see cref="int.MaxValue"/> must round-trip.
/// </summary>
public sealed class ComputeProcessExecutorTests
{
    [Fact]
    public void ParseExitCode_reads_the_reported_exit_code()
    {
        var document = """{ "ProcessId": 1234, "Exited": true, "ExitCode": 7 }""";

        Assert.Equal(7, ComputeProcessExecutor.ParseExitCode(document));
    }

    [Fact]
    public void ParseExitCode_accepts_a_zero_exit_code()
    {
        var document = """{ "ProcessId": 1, "Exited": true, "ExitCode": 0 }""";

        Assert.Equal(0, ComputeProcessExecutor.ParseExitCode(document));
    }

    [Fact]
    public void ParseExitCode_preserves_unsigned_status_codes()
    {
        // 0xC0000135 (STATUS_DLL_NOT_FOUND): above int.MaxValue, so a signed
        // 32-bit read would fail and misreport the outcome.
        var document = """{ "ProcessId": 9, "Exited": true, "ExitCode": 3221225781 }""";

        Assert.Equal(unchecked((int)3221225781), ComputeProcessExecutor.ParseExitCode(document));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void ParseExitCode_throws_when_the_status_is_unusable(string? document)
    {
        Assert.Throws<SandboxBackendException>(() => ComputeProcessExecutor.ParseExitCode(document));
    }

    [Fact]
    public void ParseExitCode_throws_when_the_process_has_not_exited()
    {
        var document = """{ "ProcessId": 9, "Exited": false, "ExitCode": 0 }""";

        Assert.Throws<SandboxBackendException>(() => ComputeProcessExecutor.ParseExitCode(document));
    }
}
