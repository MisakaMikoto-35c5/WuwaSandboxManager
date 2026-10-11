using System.Runtime.InteropServices;

namespace Wsm.Connection.RdpBase.Internal;

/// <summary>
/// Default <see cref="IRdpBaseConnector"/>. Wraps the RDPBASE named-pipe
/// connector and surfaces the RDP network-stream endpoint through
/// <see cref="GetEndpointAsync"/>. Produced by <see cref="RdpBaseConnectorFactory"/>.
/// <para>
/// The class is the COM callback sink handed to
/// <c>IRDPENCNamedPipeDirectConnector.InitializeInstance</c>; it must be
/// COM-visible so the CLR can build a callable wrapper for it (otherwise the
/// native connector cannot invoke <c>OnConnectionCompleted</c> and the connect
/// never completes).
/// </para>
/// </summary>
[ComVisible(true)]
internal sealed class RdpBaseConnector : RdpBaseInterop.IRDPENCNamedPipeDirectConnectorCallbacks, IRdpBaseConnector
{
    private RdpBaseInterop.IRDPENCNamedPipeDirectConnector? _connector;
    private readonly TaskCompletionSource<object> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RdpBaseConnector(RdpBaseInterop.IRDPENCNamedPipeDirectConnector connector)
    {
        _connector = connector;
        var hr = connector.InitializeInstance(this);
        if (hr < 0)
        {
            _completion.TrySetException(Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("RDPBASE init failed."));
        }
    }

    /// <summary>Starts the connect and returns the RDP net-stream endpoint.</summary>
    public async Task<object> GetEndpointAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        if (_connector is null)
        {
            throw new ObjectDisposedException(nameof(RdpBaseConnector));
        }

        var hr = _connector.StartConnect(pipeName);
        if (hr < 0)
        {
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("StartConnect failed.");
        }

        return await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    void RdpBaseInterop.IRDPENCNamedPipeDirectConnectorCallbacks.OnConnectionCompleted(object netStream)
        => _completion.TrySetResult(netStream);

    void RdpBaseInterop.IRDPENCNamedPipeDirectConnectorCallbacks.OnConnectorError(int hr)
        => _completion.TrySetException(
            Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"RDP named-pipe connector error 0x{hr:X8}."));

    public void Dispose()
    {
        try
        {
            Interlocked.Exchange(ref _connector, null)?.TerminateInstance();
        }
        catch
        {
            // ignore
        }
    }
}
