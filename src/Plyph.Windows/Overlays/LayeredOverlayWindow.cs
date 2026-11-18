using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Plyph.Windows.Interop;

namespace Plyph.Windows.Overlays;

/// <summary>
/// Lightweight Win32 WS_EX_LAYERED floating popup base rendered with GDI+ 32bpp premultiplied alpha.
/// Supports true non-activating overlays (WS_EX_NOACTIVATE) with zero focus stealing.
/// </summary>
public abstract class LayeredOverlayWindow : IDisposable
{
    private readonly bool _nonActivating;
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private bool _trackingLeave;
    private bool _disposed;

    protected IntPtr Hwnd { get; private set; }
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Visible { get; private set; }
    public Rectangle Bounds => new(Left, Top, Width, Height);

    protected LayeredOverlayWindow(bool nonActivating)
    {
        _nonActivating = nonActivating;
        _wndProc = WindowProc;

        if (OperatingSystem.IsWindows())
        {
            var className = $"PlyphOverlay_{GetType().Name}_{Environment.ProcessId}_{Guid.NewGuid():N}";
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = NativeMethods.GetModuleHandleW(null),
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW),
                lpszClassName = className
            };
            NativeMethods.RegisterClassExW(ref wc);

            var exStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW;
            if (_nonActivating)
                exStyle |= NativeMethods.WS_EX_NOACTIVATE;

            Hwnd = NativeMethods.CreateWindowExW(
                exStyle,
                className,
                "",
                NativeMethods.WS_POPUP | NativeMethods.WS_CLIPSIBLINGS,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }
    }

    protected float DpiScale =>
        OperatingSystem.IsWindows() && Hwnd != IntPtr.Zero
            ? Math.Max(1f, NativeMethods.GetDpiForWindow(Hwnd) / 96f)
            : 1f;

    public bool IsScreenPointInside(Point screenPoint, int margin = 6) =>
        Visible && Rectangle.Inflate(Bounds, margin, margin).Contains(screenPoint);

    protected void SetPositionAndSize(int left, int top, int width, int height)
    {
        Left = left;
        Top = top;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    protected void ShowOverlay(bool activateIfAllowed = true)
    {
        if (!OperatingSystem.IsWindows() || Hwnd == IntPtr.Zero)
            return;

        // Populate the layered surface with position/size BEFORE showing so DWM never skips initial 1x1 composition.
        Render();

        if (_nonActivating || !activateIfAllowed)
        {
            NativeMethods.SetWindowPos(
                Hwnd,
                NativeMethods.HWND_TOPMOST,
                Left, Top, Width, Height,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        else
        {
            NativeMethods.SetWindowPos(
                Hwnd,
                NativeMethods.HWND_TOPMOST,
                Left, Top, Width, Height,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.ShowWindow(Hwnd, NativeMethods.SW_SHOW);
            NativeMethods.SetForegroundWindow(Hwnd);
        }
        Visible = true;
    }

    protected void HideOverlay()
    {
        if (OperatingSystem.IsWindows() && Hwnd != IntPtr.Zero)
            NativeMethods.ShowWindow(Hwnd, NativeMethods.SW_HIDE);
        Visible = false;
    }

    protected void Render()
    {
        if (_disposed || !OperatingSystem.IsWindows() || Hwnd == IntPtr.Zero || Width <= 0 || Height <= 0)
            return;

        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            OnPaintOverlay(g, Width, Height);
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);
            var size = new NativeMethods.SIZE { cx = Width, cy = Height };
            var ptSrc = new NativeMethods.POINT { X = 0, Y = 0 };
            var ptDst = new NativeMethods.POINT { X = Left, Y = Top };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };
            NativeMethods.UpdateLayeredWindow(
                Hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            if (oldBitmap != IntPtr.Zero)
                NativeMethods.SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
        }
    }

    protected abstract void OnPaintOverlay(Graphics g, int width, int height);
    protected virtual void OnMouseMove(Point clientPoint) { }
    protected virtual void OnMouseLeave() { }
    protected virtual void OnMouseLeftDown(Point clientPoint) { }
    protected virtual void OnMouseLeftUp(Point clientPoint) { }
    protected virtual void OnMouseWheel(int delta) { }
    protected virtual void OnKeyDown(int virtualKey) { }
    protected virtual void OnDeactivated() { }

    protected void SetHandCursor(bool hand)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var cur = NativeMethods.LoadCursor(IntPtr.Zero, hand ? NativeMethods.IDC_HAND : NativeMethods.IDC_ARROW);
        if (cur != IntPtr.Zero)
            NativeMethods.SetCursor(cur);
    }

    private static Point ExtractPoint(IntPtr lParam)
    {
        var raw = unchecked((int)lParam.ToInt64());
        var x = (short)(raw & 0xFFFF);
        var y = (short)((raw >> 16) & 0xFFFF);
        return new Point(x, y);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                if (_nonActivating)
                    return (IntPtr)NativeMethods.MA_NOACTIVATE;
                break;

            case NativeMethods.WM_ACTIVATE:
                if (!_nonActivating && ((wParam.ToInt64() & 0xFFFF) == NativeMethods.WA_INACTIVE))
                    OnDeactivated();
                break;

            case NativeMethods.WM_MOUSEMOVE:
                if (!_trackingLeave)
                {
                    var tme = new NativeMethods.TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                        dwFlags = NativeMethods.TME_LEAVE,
                        hwndTrack = hwnd
                    };
                    NativeMethods.TrackMouseEvent(ref tme);
                    _trackingLeave = true;
                }
                OnMouseMove(ExtractPoint(lParam));
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSELEAVE:
                _trackingLeave = false;
                OnMouseLeave();
                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONDOWN:
                OnMouseLeftDown(ExtractPoint(lParam));
                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONUP:
                OnMouseLeftUp(ExtractPoint(lParam));
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEWHEEL:
                var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                OnMouseWheel(delta);
                return IntPtr.Zero;

            case NativeMethods.WM_KEYDOWN:
                OnKeyDown((int)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public virtual void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (OperatingSystem.IsWindows() && Hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
        }
    }
}
