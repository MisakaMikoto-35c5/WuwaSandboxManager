using System.Text;
using Wsm.Connection.RemoteApp.Internal;

namespace Wsm.Connection.RemoteApp;

/// <summary>
/// Renders the client connection file for a RemoteApp session — the same
/// technique <c>remoteapptool</c> uses to hand a published application to a
/// Remote Desktop client:
/// <list type="bullet">
/// <item><c>remoteapplicationmode:i:1</c> — launch a RemoteApp, not a desktop session;</item>
/// <item><c>remoteapplicationprogram</c> — the guest program (a path, or a
/// <c>||alias</c> published in the guest's <c>TSAppAllowList</c>);</item>
/// <item><c>alternate shell:s:rdpinit.exe</c> + <c>disableremoteappcapscheck:i:1</c> —
/// the canonical RAIL compatibility settings;</item>
/// <item><c>password 51:b:&lt;blob&gt;</c> — the DPAPI-protected credential, so the
/// session starts without a credential prompt.</item>
/// </list>
/// <para>
/// The document is deliberately transport-agnostic: the address may be the
/// guest IP (TCP transport) or <c>127.0.0.1</c> when the sandbox named pipe is
/// bridged to a loopback port.
/// </para>
/// </summary>
public static class RemoteAppRdpDocument
{
    /// <summary>Renders the <c>.rdp</c> document for <paramref name="request"/>.</summary>
    public static string Render(RemoteAppSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var redirections = request.Redirections;
        var builder = new StringBuilder();

        AddString(builder, "full address", request.ServerAddress ?? "127.0.0.1");
        AddInt(builder, "server port", request.ServerPort);

        if (!string.IsNullOrWhiteSpace(request.UserName))
        {
            AddString(builder, "username", request.UserName);
        }

        if (!string.IsNullOrEmpty(request.Password))
        {
            // The binary type prefix ("b") marks the DPAPI blob; it must not be
            // rendered through the string helper (which writes ":s:").
            builder.Append("password 51:b:").Append(RdpPassword.Protect(request.Password)).Append("\r\n");
        }

        AddInt(builder, "remoteapplicationmode", 1);
        AddString(builder, "remoteapplicationprogram", request.Program);
        AddString(builder, "remoteapplicationname", request.DisplayName);

        if (!string.IsNullOrWhiteSpace(request.ProgramArguments))
        {
            AddString(builder, "remoteapplicationcmdline", request.ProgramArguments!);
            AddInt(builder, "remoteapplicationexpandcmdline", 1);
        }

        // The RAIL bootstrap shell. The guest's rdpinit owns the RemoteApp
        // session; the client announces it and skips the capability probe, which
        // the WDAG guest answers with a refusal on some builds.
        AddString(builder, "alternate shell", "rdpinit.exe");
        AddInt(builder, "disableremoteappcapscheck", 1);

        // Security: standard RDP security like the platform's own client — no
        // NLA (the guest rejects enhanced protocols over the named pipe), the
        // security layer is negotiated and server authentication is not required.
        AddInt(builder, "authentication level", 0);
        AddInt(builder, "enablecredsspsupport", 0);
        AddInt(builder, "negotiate security layer", 1);
        AddInt(builder, "prompt for credentials", 0);
        AddInt(builder, "promptcredentialonce", 0);

        AddInt(builder, "autoreconnection enabled", 1);
        AddInt(builder, "autoreconnect max retries", 5);

        AddInt(builder, "screen mode id", request.FullScreen ? 2 : 1);
        AddInt(builder, "displayconnectionbar", request.FullScreen ? 0 : 1);
        AddInt(builder, "session bpp", 32);
        AddInt(builder, "smart sizing", 1);
        AddInt(builder, "networkautodetect", 1);
        AddInt(builder, "bandwidthautodetect", 1);

        AddInt(builder, "redirectclipboard", redirections.Clipboard ? 1 : 0);
        AddInt(builder, "redirectprinters", redirections.Printer ? 1 : 0);
        AddInt(builder, "redirectsmartcards", redirections.SmartCard ? 1 : 0);

        return builder.ToString();
    }

    /// <summary>
    /// Renders the document and writes it to <paramref name="path"/> (UTF-8, no
    /// BOM, CRLF line endings), returning the path.
    /// </summary>
    public static string Write(RemoteAppSessionRequest request, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Render(request), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static void AddString(StringBuilder builder, string key, string value)
        => builder.Append(key).Append(":s:").Append(value).Append("\r\n");

    private static void AddInt(StringBuilder builder, string key, int value)
        => builder.Append(key).Append(":i:").Append(value).Append("\r\n");
}
