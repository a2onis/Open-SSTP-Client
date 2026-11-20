using OpenSstpWin.Protocol;

namespace OpenSstpWin.Core;

/// <summary>
/// Reassembles SSTP packets from the TLS byte stream. One TLS record can carry
/// several SSTP packets and one packet can span records, so the reader may never
/// assume a record maps to a packet.
/// </summary>
internal sealed class SstpPacketizer : IDisposable
{
    private readonly Func<CancellationToken, Task<byte[]>> _read;
    private byte[] _pending = Array.Empty<byte>();
    private bool _disposed;

    public SstpPacketizer(Func<CancellationToken, Task<byte[]>> read)
    {
        _read = read;
    }

    /// <summary>Returns the next complete SSTP packet, header included.</summary>
    public async Task<byte[]> NextAsync(CancellationToken token)
    {
        while (true)
        {
            if (_pending.Length >= 4)
            {
                var length = (_pending[2] << 8) | _pending[3];
                if (length < 8)
                {
                    throw new SstpProtocolException("Invalid SSTP packet length.");
                }

                if (length > 0x20000)
                {
                    throw new SstpProtocolException("SSTP packet exceeds the maximum size.");
                }

                if (_pending.Length >= length)
                {
                    var packet = _pending.AsSpan(0, length).ToArray();
                    _pending = _pending.Length == length
                        ? Array.Empty<byte>()
                        : _pending[length..];
                    return packet;
                }
            }

            var next = await _read(token).ConfigureAwait(false);
            if (next.Length == 0)
            {
                throw new IOException("The server closed the connection.");
            }

            if (_pending.Length == 0)
            {
                _pending = next;
            }
            else
            {
                var combined = new byte[_pending.Length + next.Length];
                _pending.CopyTo(combined, 0);
                next.CopyTo(combined, _pending.Length);
                _pending = combined;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending = Array.Empty<byte>();
    }
}
