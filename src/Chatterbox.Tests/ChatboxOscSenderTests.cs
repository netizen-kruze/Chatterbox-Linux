using System.Text;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The OSC datagrams VRChat receives, byte for byte: NUL-terminated strings
// padded to four-byte boundaries, type tags, and the boolean tags that
// select "send now, no sound".
public class ChatboxOscSenderTests
{
    [Fact]
    public void ChatboxInputPacketHasSpecLayout()
    {
        var p = ChatboxOscSender.Packet("/chatbox/input", ",sTF", "hi");
        Assert.Equal(28, p.Length);
        Assert.Equal("/chatbox/input", Encoding.ASCII.GetString(p, 0, 14));
        Assert.Equal(new byte[] { 0, 0 }, p[14..16]);            // terminator + pad to 16
        Assert.Equal(",sTF", Encoding.ASCII.GetString(p, 16, 4));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, p[20..24]);      // aligned strings still get four NULs
        Assert.Equal("hi", Encoding.ASCII.GetString(p, 24, 2));
        Assert.Equal(new byte[] { 0, 0 }, p[26..28]);
    }

    [Fact]
    public void TypingPacketCarriesOnlyTheBooleanTag()
    {
        var on = ChatboxOscSender.Packet("/chatbox/typing", ",T");
        Assert.Equal(20, on.Length);
        Assert.Equal("/chatbox/typing", Encoding.ASCII.GetString(on, 0, 15));
        Assert.Equal(0, on[15]);
        Assert.Equal(",T", Encoding.ASCII.GetString(on, 16, 2));
        Assert.Equal(new byte[] { 0, 0 }, on[18..20]);
    }

    [Fact]
    public void NonAsciiTextIsUtf8()
    {
        var p = ChatboxOscSender.Packet("/x", ",s", "é");
        // "/x" + 2 NULs = 4; ",s" + 2 NULs = 4; "é" is 2 bytes + 2 NULs = 4
        Assert.Equal(12, p.Length);
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0, 0 }, p[8..12]);
    }
}
