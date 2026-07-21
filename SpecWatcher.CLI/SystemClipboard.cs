using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpecWatcher;

/// <summary>
/// Best-effort copy of text to the OS clipboard. On Windows it uses the Win32 clipboard API
/// (<c>CF_UNICODETEXT</c>), which is reliable on both the classic console (conhost) and Windows
/// Terminal. On other platforms it is a no-op — those hosts never take over the mouse, so the
/// terminal's own drag-to-select handles copying.
/// </summary>
internal static class SystemClipboard
{
    public static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return OperatingSystem.IsWindows() && TryCopyWindows(text);
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
