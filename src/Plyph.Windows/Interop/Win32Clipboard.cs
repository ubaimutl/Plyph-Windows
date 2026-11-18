using System.Runtime.InteropServices;

namespace Plyph.Windows.Interop;

public static class Win32Clipboard
{
    public static string GetText()
    {
        if (!OperatingSystem.IsWindows())
            return "";

        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
            return "";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                    if (handle == IntPtr.Zero)
                        return "";

                    var ptr = NativeMethods.GlobalLock(handle);
                    if (ptr == IntPtr.Zero)
                        return "";

                    try
                    {
                        return Marshal.PtrToStringUni(ptr) ?? "";
                    }
                    finally
                    {
                        NativeMethods.GlobalUnlock(handle);
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }
            Thread.Sleep(10);
        }

        return "";
    }

    public static void SetText(string? text)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var safe = text ?? "";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                IntPtr hGlobal = IntPtr.Zero;
                try
                {
                    NativeMethods.EmptyClipboard();
                    var bytes = (safe.Length + 1) * 2;
                    hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero)
                        return;

                    var ptr = NativeMethods.GlobalLock(hGlobal);
                    if (ptr == IntPtr.Zero)
                    {
                        NativeMethods.GlobalFree(hGlobal);
                        return;
                    }

                    try
                    {
                        var chars = safe.ToCharArray();
                        Marshal.Copy(chars, 0, ptr, chars.Length);
                        Marshal.WriteInt16(ptr, chars.Length * 2, 0);
                    }
                    finally
                    {
                        NativeMethods.GlobalUnlock(hGlobal);
                    }

                    if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hGlobal) != IntPtr.Zero)
                        hGlobal = IntPtr.Zero; // Ownership transferred to Windows
                    return;
                }
                finally
                {
                    if (hGlobal != IntPtr.Zero)
                        NativeMethods.GlobalFree(hGlobal);
                    NativeMethods.CloseClipboard();
                }
            }
            Thread.Sleep(10);
        }
    }
}
