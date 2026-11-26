using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class SystemClipboardTests
{
    [Fact]
    public void Osc52_Wraps_Utf8_Base64_For_The_Clipboard_Selection()
    {
        // "Zażółć" in UTF-8, base64-encoded, terminated by BEL.
        Assert.Equal("\x1b]52;c;WmHFvMOzxYLEhw==\x07", SystemClipboard.Osc52("Zażółć"));
    }
}
