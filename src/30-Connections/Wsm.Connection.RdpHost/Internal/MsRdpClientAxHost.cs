using System.Windows.Forms;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// <see cref="AxHost"/> wrapper for the <c>mstscax</c> Remote Desktop ActiveX
/// control. Prefers the typed <c>MsRdpClient10</c> coclass (ProgID
/// <c>MsTscAx.MsTscAx.10</c>, CLSID <c>{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}</c>)
/// and falls back to the version-11 control when the v10 control is absent.
/// </summary>
internal sealed class MsRdpClientAxHost : AxHost
{
    /// <summary>CLSID of the <c>MsRdpClient10</c> ("Microsoft RDP Client Control - version 10") coclass.</summary>
    public const string MsRdpClient10Clsid = "{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}";

    private static readonly string[] CandidateProgIds =
    {
        "MsTscAx.MsTscAx.10",
        "MsTscAx.MsTscAx.11",
    };

    public MsRdpClientAxHost()
        : base(ResolveClsid())
    {
    }

    private static string ResolveClsid()
    {
        foreach (var progId in CandidateProgIds)
        {
            var type = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (type is not null)
            {
                return type.GUID.ToString("B");
            }
        }

        return MsRdpClient10Clsid;
    }
}
