using System.Runtime.InteropServices;
using System.Text;
using Wsm.Connection.RemoteApp;
using Wsm.Connection.RemoteApp.Internal;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers the RemoteApp client document (<c>.rdp</c>) rendering: the RAIL
/// settings (<c>remoteapplicationmode</c>/<c>remoteapplicationprogram</c>), the
/// loopback endpoint, the DPAPI-protected credential and the redirection flags.
/// </summary>
public sealed class RemoteAppRdpDocumentTests
{
    [Fact]
    public void Render_writes_the_remote_app_bootstrap_settings()
    {
        var request = new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            PipeName = @"\\.\pipe\test",
            Program = @"C:\Windows\System32\notepad.exe",
            DisplayName = "Notepad",
            UserName = "WDAGUtilityAccount",
        };

        var document = RemoteAppRdpDocument.Render(request);

        Assert.Contains("remoteapplicationmode:i:1\r\n", document);
        Assert.Contains(@"remoteapplicationprogram:s:C:\Windows\System32\notepad.exe" + "\r\n", document);
        Assert.Contains("remoteapplicationname:s:Notepad\r\n", document);
        Assert.Contains("alternate shell:s:rdpinit.exe\r\n", document);
        Assert.Contains("disableremoteappcapscheck:i:1\r\n", document);
        Assert.Contains("full address:s:127.0.0.1\r\n", document);
    }

    [Fact]
    public void Render_uses_the_supplied_tcp_endpoint()
    {
        var request = new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            ServerAddress = "172.21.80.101",
            ServerPort = 3390,
            Program = @"C:\app.exe",
        };

        var document = RemoteAppRdpDocument.Render(request);

        Assert.Contains("full address:s:172.21.80.101\r\n", document);
        Assert.Contains("server port:i:3390\r\n", document);
    }

    [Fact]
    public void Render_maps_the_command_line_arguments()
    {
        var request = new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            Program = @"C:\app.exe",
            ProgramArguments = "--flag value",
        };

        var document = RemoteAppRdpDocument.Render(request);

        Assert.Contains("remoteapplicationcmdline:s:--flag value\r\n", document);
        Assert.Contains("remoteapplicationexpandcmdline:i:1\r\n", document);
    }

    [Fact]
    public void Render_omits_the_command_line_when_there_are_no_arguments()
    {
        var document = RemoteAppRdpDocument.Render(new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            Program = @"C:\app.exe",
        });

        Assert.DoesNotContain("remoteapplicationcmdline", document);
    }

    [Fact]
    public void Render_protects_the_credential_instead_of_writing_it_in_clear()
    {
        var request = new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            Program = @"C:\app.exe",
            UserName = "WDAGUtilityAccount",
            Password = "s3cret!",
        };

        var document = RemoteAppRdpDocument.Render(request);

        Assert.DoesNotContain("s3cret!", document);
        Assert.Contains("username:s:WDAGUtilityAccount\r\n", document);
        Assert.Contains("password 51:b:", document);

        var encoded = document
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("password 51:b:", StringComparison.Ordinal))["password 51:b:".Length..];
        Assert.Equal("s3cret!", RdpPasswordProbe.Unprotect(encoded));
    }

    [Fact]
    public void Render_omits_the_credential_when_the_password_is_empty()
    {
        var document = RemoteAppRdpDocument.Render(new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            Program = @"C:\app.exe",
            UserName = "WDAGUtilityAccount",
        });

        Assert.DoesNotContain("password 51", document);
    }

    [Fact]
    public void Render_uses_standard_rdp_security_like_the_platform_client()
    {
        var document = RemoteAppRdpDocument.Render(new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            Program = @"C:\app.exe",
        });

        Assert.Contains("authentication level:i:0\r\n", document);
        Assert.Contains("enablecredsspsupport:i:0\r\n", document);
        Assert.Contains("negotiate security layer:i:1\r\n", document);
    }

    [Fact]
    public void Render_honours_full_screen()
    {
        var document = RemoteAppRdpDocument.Render(new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            Program = @"C:\app.exe",
            FullScreen = true,
        });

        Assert.Contains("screen mode id:i:2\r\n", document);
    }

    [Fact]
    public void Render_maps_the_redirection_flags()
    {
        var document = RemoteAppRdpDocument.Render(new RemoteAppSessionRequest
        {
            SandboxId = Guid.NewGuid(),
            Program = @"C:\app.exe",
            Redirections = SandboxRedirections.Default with
            {
                Clipboard = false,
                Printer = true,
                SmartCard = false,
            },
        });

        Assert.Contains("redirectclipboard:i:0\r\n", document);
        Assert.Contains("redirectprinters:i:1\r\n", document);
        Assert.Contains("redirectsmartcards:i:0\r\n", document);
    }

    [Fact]
    public void Write_creates_the_file_at_the_requested_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wsm-rdp-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "app.rdp");
        try
        {
            RemoteAppRdpDocument.Write(
                new RemoteAppSessionRequest { SandboxId = Guid.NewGuid(), Program = @"C:\app.exe" },
                path);

            Assert.True(File.Exists(path));
            Assert.StartsWith("full address:s:127.0.0.1\r\n", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>DPAPI round-trip helper for the encoded <c>password 51:b:</c> value.</summary>
    private static class RdpPasswordProbe
    {
        public static string Unprotect(string hex)
        {
            var encrypted = Convert.FromHexString(hex);
            var input = new DataBlob
            {
                Size = encrypted.Length,
                Data = Marshal.AllocHGlobal(encrypted.Length),
            };
            try
            {
                Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out var output))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    var bytes = new byte[output.Size];
                    Marshal.Copy(output.Data, bytes, 0, output.Size);
                    return Encoding.Unicode.GetString(bytes);
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
