using System.Globalization;
using System.Xml.Linq;
using Wsm.Domain.ValueObjects;

namespace Wsm.SandboxServer.Client.Internal;

/// <summary>
/// Parses the <c>RdpClientConfig</c> XML blob returned by the control plane
/// (it is a <c>XmlSerializer</c> serialization of Microsoft's RdpClientConfig).
/// </summary>
internal static class RdpClientConfigParser
{
    public static SandboxRuntimeInfo Parse(string? xml, SandboxInstanceId sandboxId)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new SandboxRuntimeInfo { SandboxId = sandboxId };
        }

        var root = XDocument.Parse(xml).Root!;
        var vmId = Guid.TryParse(Element(root, "VMId"), out var vm) ? vm : Guid.Empty;
        var transport = Enum.TryParse<RdpTransportKind>(Element(root, "RdpTransport"), ignoreCase: true, out var t)
            ? t
            : RdpTransportKind.NamedPipe;
        var ip = Element(root, "IpAddress") ?? string.Empty;

        return new SandboxRuntimeInfo
        {
            SandboxId = sandboxId,
            VmId = vmId,
            Transport = transport,
            RailMode = Bool(root, "RailModeEnabled") ?? false,
            FullScreen = Bool(root, "Fullscreen") ?? false,
            WindowTitle = Element(root, "WindowTitle") ?? "Windows Sandbox",
            UserName = Element(root, "Username") ?? string.Empty,
            Password = Element(root, "Password") ?? string.Empty,
            IpAddress = ip,
            IpAddresses = string.IsNullOrEmpty(ip) ? Array.Empty<string>() : new[] { ip },
            Redirections = new SandboxRedirections
            {
                AudioInput = Bool(root, "AudioInputEnabled") ?? true,
                VideoInput = Bool(root, "VideoInputEnabled") ?? false,
                Printer = Bool(root, "PrinterRedirection") ?? false,
                Clipboard = Bool(root, "ClipboardRedirection") ?? true,
                SmartCard = Bool(root, "SmartCardRedirection") ?? false,
            },
        };
    }

    private static string? Element(XElement root, string name) => root.Element(name)?.Value;

    private static bool? Bool(XElement root, string name)
    {
        var value = root.Element(name)?.Value?.Trim().ToLowerInvariant();
        return value switch
        {
            "true" or "enable" or "enabled" => true,
            "false" or "disable" or "disabled" => false,
            _ => null,
        };
    }

    public static int ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
}
