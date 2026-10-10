using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.SandboxServer.Client.Internal;
using Xunit;

namespace Wsm.Tests.SandboxServer;

public class RdpClientConfigParserTests
{
    [Fact]
    public void Parses_a_real_rdp_client_config()
    {
        const string xml = """
            <RdpClientConfig>
              <WindowTitle>Windows Sandbox</WindowTitle>
              <RdpTransport>NamedPipe</RdpTransport>
              <Fullscreen>false</Fullscreen>
              <RailModeEnabled>true</RailModeEnabled>
              <IpAddress>172.26.246.60</IpAddress>
              <VMId>aa4f54a6-d304-4893-88b6-cb947a7795e6</VMId>
              <SandboxId>d420db25-36e0-4ab8-a1bc-ad26e719d36d</SandboxId>
              <AudioInputEnabled>true</AudioInputEnabled>
              <VideoInputEnabled>false</VideoInputEnabled>
              <PrinterRedirection>false</PrinterRedirection>
              <ClipboardRedirection>true</ClipboardRedirection>
              <SmartCardRedirection>false</SmartCardRedirection>
              <Username>WDAGUtilityAccount</Username>
              <Password>secret</Password>
            </RdpClientConfig>
            """;

        var id = SandboxInstanceId.New();
        var runtime = RdpClientConfigParser.Parse(xml, id);

        Assert.Equal(Guid.Parse("aa4f54a6-d304-4893-88b6-cb947a7795e6"), runtime.VmId);
        Assert.Equal(RdpTransportKind.NamedPipe, runtime.Transport);
        Assert.True(runtime.RailMode);
        Assert.Equal("WDAGUtilityAccount", runtime.UserName);
        Assert.Equal("secret", runtime.Password);
        Assert.Equal("172.26.246.60", runtime.IpAddress);
        Assert.Equal(@"\\.\pipe\aa4f54a6-d304-4893-88b6-cb947a7795e6", runtime.NamedPipeEndpoint);
    }

    [Fact]
    public void Empty_payload_yields_defaults()
    {
        var id = SandboxInstanceId.New();
        var runtime = RdpClientConfigParser.Parse(string.Empty, id);
        Assert.Equal(Guid.Empty, runtime.VmId);
        Assert.Equal(RdpTransportKind.NamedPipe, runtime.Transport);
    }
}
