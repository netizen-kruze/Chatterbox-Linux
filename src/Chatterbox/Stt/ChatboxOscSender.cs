using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Chatterbox.Stt;

// Sends caption text to VRChat's chatbox over OSC (UDP, localhost only).
// Message layout per the OSC 1.0 spec: address pattern, then a type-tag
// string, then arguments — every string NUL-terminated and zero-padded to a
// four-byte boundary. VRChat's endpoints:
//   /chatbox/input  s T/F T/F   (text, send-immediately, play-sfx)
//   /chatbox/typing T/F         (typing-indicator bubble)
public sealed class ChatboxOscSender : IDisposable
{
    public const int MaxChars = 144; // VRChat chatbox hard cap

    private readonly UdpClient _udp;
    private readonly object _sendGate = new();

    public ChatboxOscSender(string ip = "127.0.0.1", int port = 9000)
    {
        _udp = new UdpClient();
        _udp.Connect(ip, port);
    }

    public void SendInput(string text)
    {
        if (text.Length > MaxChars) text = text[..MaxChars];
        // Immediate send on, notification sound off.
        Send(Packet("/chatbox/input", ",sTF", text));
    }

    public void SendTyping(bool active) =>
        Send(Packet("/chatbox/typing", active ? ",T" : ",F"));

    private void Send(byte[] datagram)
    {
        lock (_sendGate)
        {
            try { _udp.Send(datagram, datagram.Length); }
            catch { /* VRChat not listening — nothing useful to do */ }
        }
    }

    internal static byte[] Packet(string address, string typeTags, string? stringArg = null)
    {
        using var ms = new MemoryStream(64);
        AppendPadded(ms, address);
        AppendPadded(ms, typeTags);
        if (stringArg != null) AppendPadded(ms, stringArg);
        return ms.ToArray();
    }

    // OSC strings: UTF-8 bytes, then NUL padding out to the next multiple of
    // four (an already-aligned string still gets four NULs — the terminator
    // is mandatory).
    private static void AppendPadded(MemoryStream ms, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        ms.Write(utf8, 0, utf8.Length);
        int paddedLength = (utf8.Length + 4) & ~3;
        for (int written = utf8.Length; written < paddedLength; written++)
            ms.WriteByte(0);
    }

    public void Dispose() => _udp.Dispose();
}
