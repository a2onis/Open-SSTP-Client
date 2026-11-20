using System.Runtime.InteropServices;

namespace OpenSstpWin.Interop;

/// <summary>
/// Managed wrapper around wintun.dll 0.14 (https://www.wintun.net). Wintun is a
/// signed, redistributable layer-3 TUN driver for Windows used by WireGuard.
/// wintun.dll must sit next to OpenSstpWin.exe (the release zip ships it).
/// </summary>
public sealed class WintunSession : IDisposable
{
    private const string Dll = "wintun.dll";

    private const int ErrorNoMoreItems = 259;
    private const int ErrorAccessDenied = 5;
    private const uint MaxRingCapacity = 0x4000000;

    private IntPtr _adapter;
    private IntPtr _session;
    private bool _disposed;

    public string AdapterName { get; private set; } = string.Empty;

    public bool IsOpen => _session != IntPtr.Zero;

    /// <summary>
    /// Opens (or creates) the adapter and starts a session. Returns null on
    /// success, otherwise a message describing what went wrong.
    /// </summary>
    public string? Open(string name, uint ringCapacity = 0x400000)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "wintun.dll")))
        {
            return "wintun.dll is missing. Keep it next to OpenSstpWin.exe.";
        }

        try
        {
            _adapter = WintunOpenAdapter(name);
            if (_adapter == IntPtr.Zero)
            {
                _adapter = WintunCreateAdapter(name, "Open SSTP Client", IntPtr.Zero);
            }
        }
        catch (DllNotFoundException)
        {
            return "wintun.dll could not be loaded. Use the win-x64 build on 64-bit Windows 11.";
        }
        catch (BadImageFormatException)
        {
            return "wintun.dll has the wrong architecture for this process.";
        }

        if (_adapter == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorAccessDenied
                ? "Access denied creating the tunnel. Run OpenSstpWin.exe as administrator."
                : $"Cannot create the Wintun adapter (Win32 error {error}).";
        }

        // A stale session left by a crashed run keeps the adapter busy briefly.
        for (var attempt = 0; attempt < 20 && _session == IntPtr.Zero; attempt++)
        {
            _session = WintunStartSession(_adapter, Math.Min(ringCapacity, MaxRingCapacity));
            if (_session == IntPtr.Zero)
            {
                Thread.Sleep(100);
            }
        }

        if (_session == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorAccessDenied
                ? "Access denied opening the tunnel. Run OpenSstpWin.exe as administrator."
                : $"Cannot start the Wintun session (Win32 error {error}). Is another copy running?";
        }

        AdapterName = name;
        return null;
    }

    /// <summary>
    /// Reads one inbound IP packet. Returns null when nothing is queued within the
    /// timeout or when the tunnel is closing.
    /// </summary>
    public byte[]? Receive(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (_session == IntPtr.Zero)
        {
            return null;
        }

        var packet = WintunReceivePacket(_session, out var size);
        if (packet != IntPtr.Zero)
        {
            try
            {
                var copy = new byte[size];
                Marshal.Copy(packet, copy, 0, (int)size);
                return copy;
            }
            finally
            {
                WintunReleaseReceivePacket(_session, packet);
            }
        }

        if (Marshal.GetLastWin32Error() == ErrorNoMoreItems && !cancellationToken.IsCancellationRequested)
        {
            var readEvent = WintunGetReadWaitEvent(_session);
            if (readEvent != IntPtr.Zero)
            {
                WaitForSingleObject(readEvent, (uint)Math.Max(1, timeoutMilliseconds));
            }
            else
            {
                Thread.Sleep(Math.Min(timeoutMilliseconds, 5));
            }
        }

        return null;
    }

    public bool Send(byte[] packet)
    {
        if (_session == IntPtr.Zero)
        {
            return false;
        }

        var buffer = WintunAllocateSendPacket(_session, (uint)packet.Length);
        if (buffer == IntPtr.Zero)
        {
            return false;
        }

        Marshal.Copy(packet, 0, buffer, packet.Length);
        WintunSendPacket(_session, buffer);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session != IntPtr.Zero)
        {
            WintunEndSession(_session);
            _session = IntPtr.Zero;
        }

        if (_adapter != IntPtr.Zero)
        {
            WintunCloseAdapter(_adapter);
            _adapter = IntPtr.Zero;
        }
    }

    /// <summary>Removes the virtual adapter so no network profile is left behind.</summary>
    public static void DeleteAdapter(string name)
    {
        var adapter = WintunOpenAdapter(name);
        if (adapter != IntPtr.Zero)
        {
            WintunCloseAdapter(adapter);
        }
    }

    /// <summary>Returns the interface LUID, used for routing table entries.</summary>
    public ulong GetLuid()
    {
        return _adapter == IntPtr.Zero ? 0 : (ulong)WintunGetAdapterLuid(_adapter);
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WintunCreateAdapter(
        string name,
        string tunnelType,
        IntPtr requestedGuid);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WintunOpenAdapter(string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern void WintunCloseAdapter(IntPtr adapter);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr WintunGetAdapterLuid(IntPtr adapter);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern void WintunEndSession(IntPtr session);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern void WintunSendPacket(IntPtr session, IntPtr packet);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
