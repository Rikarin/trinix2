using System.Net.Sockets;
using System.Text;

namespace Trinix.Compositor;

/// <summary>
/// The half of <c>sd_notify(3)</c> the compositor needs.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Extensions.Hosting.Systemd does this, and trinixd uses it. The
/// compositor does not, because it is not a hosted service: it is a process
/// that hands its thread to a C event loop and never gets it back. Pulling in
/// the hosting stack to send one datagram would invert that relationship for
/// no benefit.
/// </para>
/// <para>
/// The protocol is deliberately trivial — a datagram to the socket named in
/// <c>NOTIFY_SOCKET</c>, with a leading NUL for the abstract namespace — and
/// what it buys is real: <c>Type=notify</c> means "ready" is the moment the
/// Wayland socket exists, so anything ordered after the compositor can
/// actually connect to it. Without that, every client would race the display.
/// </para>
/// </remarks>
internal static class Systemd
{
    /// <summary>Tells systemd the service is up. Silent if not running under systemd.</summary>
    internal static void NotifyReady() => Send("READY=1");

    /// <summary>Reports a human-readable state, shown by <c>systemctl status</c>.</summary>
    /// <param name="status">A short phrase, no newline.</param>
    internal static void NotifyStatus(string status) => Send("STATUS=" + status);

    private static void Send(string message)
    {
        string? path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            // A leading '@' names a socket in Linux's abstract namespace,
            // which is spelled as a leading NUL byte in the address.
            if (path[0] == '@')
            {
                path = "\0" + path[1..];
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send(Encoding.UTF8.GetBytes(message));
        }
        catch (SocketException error)
        {
            // Failing to tell systemd it is ready must not stop the compositor
            // being ready. The unit will time out and say so.
            Log.Error($"sd_notify failed: {error.Message}");
        }
    }
}
