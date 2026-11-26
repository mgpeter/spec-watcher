using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SpecWatcher;

/// <summary>
/// Best-effort copy of text to the OS clipboard, needed because the app owns the mouse and draws its
/// own selection. On Windows it uses the Win32 clipboard API (<c>CF_UNICODETEXT</c>), which is
/// reliable on both conhost and Windows Terminal. On macOS it pipes into <c>pbcopy</c>. Everywhere
/// else, or when <c>pbcopy</c> fails, it asks the terminal to copy with an OSC 52 sequence, which
/// Ghostty, iTerm2, kitty, WezTerm and tmux (with <c>set-clipboard on</c>) honour, also over SSH.
/// </summary>
internal static class SystemClipboard
{
    public static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (OperatingSystem.IsWindows()) return TryCopyWindows(text);
        if (OperatingSystem.IsMacOS() && TryCopyPbcopy(text)) return true;
        return TryCopyOsc52(text);
    }

    /// <summary>The OSC 52 "set clipboard" sequence for <paramref name="text"/>.</summary>
    internal static string Osc52(string text) =>
        $"\x1b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\x07";

    private static bool TryCopyOsc52(string text)
    {
        if (Console.IsOutputRedirected) return false;
        try
        {
            Console.Out.Write(Osc52(text));
            Console.Out.Flush();
            return true;   // the terminal does not confirm, so this is "sent", not "copied"
        }
        catch { return false; }
    }

    private static bool TryCopyPbcopy(string text)
    {
        try
        {
            var psi = new ProcessStartInfo("pbcopy")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            psi.Environment["LC_CTYPE"] = "UTF-8";   // pbcopy decodes stdin by locale

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            if (!proc.WaitForExit(2000)) { proc.Kill(); return false; }
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryCopyWindows(string text)
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();

                var chars = text.ToCharArray();
                var bytes = (chars.Length + 1) * 2;                 // + null terminator (UTF-16)
                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hGlobal == IntPtr.Zero) return false;

                var ptr = GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
                try
                {
                    Marshal.Copy(chars, 0, ptr, chars.Length);
                    Marshal.WriteInt16(ptr, chars.Length * 2, 0);   // terminating NUL
                }
                finally { GlobalUnlock(hGlobal); }

                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);                            // ownership stays with us on failure
                    return false;
                }
                return true;                                        // on success the system owns hGlobal
            }
            finally { CloseClipboard(); }
        }
        catch { return false; }
    }

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
