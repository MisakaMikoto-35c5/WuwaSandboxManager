using System.Runtime.InteropServices;

namespace Wsm.Hcs.Network.Internal;

/// <summary>
/// P/Invoke surface over the Host Network Service (HNS) JSON dispatcher. HNS is
/// reached through the single <c>HNSCall</c> export of <c>vmcompute.dll</c>,
/// which forwards a REST-like request and returns a JSON envelope
/// (<c>{ "Success": bool, "Error": string, "Output": ... }</c>).
/// </summary>
internal static class HnsNative
{
    private const string VmCompute = "vmcompute.dll";

    /// <summary>
    /// Invokes the HNS dispatcher: <paramref name="method"/> is
    /// <c>GET</c>/<c>POST</c>/<c>DELETE</c>, <paramref name="path"/> a resource
    /// path such as <c>/endpoints/</c> and <paramref name="request"/> the JSON
    /// body (or an empty string for reads). The response buffer is allocated
    /// with <c>CoTaskMemAlloc</c> and must be released with
    /// <see cref="System.Runtime.InteropServices.Marshal.FreeCoTaskMem"/>.
    /// </summary>
    [DllImport(VmCompute, ExactSpelling = true, CharSet = CharSet.Unicode, EntryPoint = "HNSCall")]
    public static extern int HNSCall(string method, string path, string request, out IntPtr response);
}
