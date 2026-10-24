using System.Runtime.InteropServices;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// A read-only <see cref="Stream"/> over a mapped view, so the mirror layout readers
/// work the same on a file and on a passed-through section.
/// </summary>
internal sealed class MappingReadStream : Stream
{
    private readonly IntPtr _baseAddress;
    private readonly long _length;
    private long _position;

    public MappingReadStream(IntPtr baseAddress, long length)
    {
        _baseAddress = baseAddress;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var available = (int)Math.Min(count, Math.Max(0, _length - _position));
        if (available > 0)
        {
            Marshal.Copy(IntPtr.Add(_baseAddress, (int)_position), buffer, offset, available);
            _position += available;
        }

        return available;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
