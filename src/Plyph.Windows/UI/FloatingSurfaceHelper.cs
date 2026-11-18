using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Plyph.Core;
using Plyph.Windows.Interop;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;
using WinPoint = System.Drawing.Point;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Plyph.Windows.UI;

public sealed class FloatingPopupChrome : IDisposable
{
    private static readonly UIntPtr SubclassId = new(0x504C5950);
    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly bool _nonActivating;
    private readonly bool _allowTinySize;
    private readonly int _cornerRadiusDip;
    private readonly NativeMethods.SUBCLASSPROC _subclassProc;
    private bool _disposed;

    public IntPtr Hwnd => _hwnd;

    public FloatingPopupChrome(
        Window window,
        bool nonActivating,
        bool allowTinySize,
        int cornerRadiusDip = 8)
    {
        _window = window;
        _nonActivating = nonActivating;
        _allowTinySize = allowTinySize;
        _cornerRadiusDip = cornerRadiusDip;
        _hwnd = WindowNative.GetWindowHandle(window);
        _subclassProc = WindowSubclassProc;

        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero)
            return;

        if (window.AppWindow is { } appWindow)
        {
            try { appWindow.IsShownInSwitchers = false; } catch { }
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                try { presenter.SetBorderAndTitleBar(false, false); } catch { }
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsAlwaysOnTop = true;
            }
        }

        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        ex &= ~(NativeMethods.WS_EX_APPWINDOW | 0x00000100L | 0x00000200L | 0x00000001L);
        if (_nonActivating)
            ex |= NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        const int GWL_STYLE = -16;
        const long WS_BORDER = 0x00800000L;
        const long WS_DLGFRAME = 0x00400000L;
        const long WS_THICKFRAME = 0x00040000L;
        var st = NativeMethods.GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
        st &= ~(WS_BORDER | WS_DLGFRAME | WS_THICKFRAME);
        NativeMethods.SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(st));

        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, SubclassId, UIntPtr.Zero);
        NativeMethods.ApplyWindowCornerPreference(_hwnd, NativeMethods.DWMWCP_ROUND);
        NativeMethods.SuppressWindowBorder(_hwnd);
    }

    public static void ConfigureSurfaceBorder(Border border, int cornerRadiusDip = 8)
    {
        if (NativeMethods.IsWindows11OrGreater())
        {
            border.BorderThickness = new Thickness(0);
            border.CornerRadius = new CornerRadius(0);
        }
        else
        {
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(cornerRadiusDip);
        }
    }

    public double GetDpiScale() =>
        OperatingSystem.IsWindows() && _hwnd != IntPtr.Zero
            ? Math.Max(1.0, NativeMethods.GetDpiForWindow(_hwnd) / 96.0)
            : 1.0;

    public void UpdateRegionForWin10(int physW, int physH)
    {
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero || NativeMethods.IsWindows11OrGreater())
            return;
        if (_cornerRadiusDip <= 0 || physW <= 0 || physH <= 0)
            return;

        var scale = GetDpiScale();
        var diameter = Math.Max(2, (int)Math.Round(_cornerRadiusDip * 2 * scale));
        var hrgn = NativeMethods.CreateRoundRectRgn(0, 0, physW + 1, physH + 1, diameter, diameter);
        if (hrgn != IntPtr.Zero)
        {
            if (NativeMethods.SetWindowRgn(_hwnd, hrgn, true) == 0)
                NativeMethods.DeleteObject(hrgn);
        }
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (_nonActivating && uMsg == NativeMethods.WM_MOUSEACTIVATE)
            return (IntPtr)NativeMethods.MA_NOACTIVATE;

        if (uMsg == NativeMethods.WM_ACTIVATE)
            NativeMethods.SuppressWindowBorder(hWnd);

        if (_allowTinySize && uMsg == NativeMethods.WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            var res = NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
            var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = 1;
            mmi.ptMinTrackSize.Y = 1;
            Marshal.StructureToPtr(mmi, lParam, false);
            return res;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (OperatingSystem.IsWindows() && _hwnd != IntPtr.Zero)
        {
            NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        }
    }
}

public static class FloatingSurfaceHelper
{
    public static Viewbox CreatePlyphMark(double sizeDip = 14, Brush? fill = null)
    {
        // Exact vector geometry from assets/icons/plyph-symbolic.svg (viewBox 160 126 704 704)
        var outerFigure = new PathFigure
        {
            StartPoint = new Point(512, 156),
            IsClosed = true,
            IsFilled = true
        };
        outerFigure.Segments.Add(new BezierSegment { Point1 = new Point(496, 257), Point2 = new Point(458, 365), Point3 = new Point(401, 455) });
        outerFigure.Segments.Add(new BezierSegment { Point1 = new Point(349, 537), Point2 = new Point(286, 607), Point3 = new Point(220, 660) });
        outerFigure.Segments.Add(new BezierSegment { Point1 = new Point(302, 636), Point2 = new Point(368, 637), Point3 = new Point(419, 675) });
        outerFigure.Segments.Add(new BezierSegment { Point1 = new Point(465, 709), Point2 = new Point(493, 755), Point3 = new Point(512, 800) });
        outerFigure.Segments.Add(new BezierSegment { Point1 = new Point(531, 755), Point2 = new Point(559, 709), Point3 = new Point(605, 675) });
        outerFigure.Segments.Add(new BezierSegment { Point1 = new Point(656, 637), Point2 = new Point(722, 636), Point3 = new Point(804, 660) });
        outerFigure.Segments.Add(new BezierSegment { Point1 = new Point(738, 607), Point2 = new Point(675, 537), Point3 = new Point(623, 455) });
        outerFigure.Segments.Add(new BezierSegment { Point1 = new Point(566, 365), Point2 = new Point(528, 257), Point3 = new Point(512, 156) });

        var innerCutout = new PathFigure
        {
            StartPoint = new Point(512, 405),
            IsClosed = true,
            IsFilled = true
        };
        innerCutout.Segments.Add(new BezierSegment { Point1 = new Point(461, 405), Point2 = new Point(424, 439), Point3 = new Point(424, 487) });
        innerCutout.Segments.Add(new BezierSegment { Point1 = new Point(424, 539), Point2 = new Point(444, 594), Point3 = new Point(468, 650) });
        innerCutout.Segments.Add(new BezierSegment { Point1 = new Point(490, 702), Point2 = new Point(505, 751), Point3 = new Point(512, 800) });
        innerCutout.Segments.Add(new BezierSegment { Point1 = new Point(509, 757), Point2 = new Point(507, 714), Point3 = new Point(511, 678) });
        innerCutout.Segments.Add(new BezierSegment { Point1 = new Point(516, 635), Point2 = new Point(532, 612), Point3 = new Point(559, 586) });
        innerCutout.Segments.Add(new BezierSegment { Point1 = new Point(589, 557), Point2 = new Point(600, 527), Point3 = new Point(600, 488) });
        innerCutout.Segments.Add(new BezierSegment { Point1 = new Point(600, 440), Point2 = new Point(563, 405), Point3 = new Point(512, 405) });

        var pathGeom = new PathGeometry { FillRule = FillRule.EvenOdd };
        pathGeom.Figures.Add(outerFigure);
        pathGeom.Figures.Add(innerCutout);

        var dotGeom = new EllipseGeometry
        {
            Center = new Point(512, 493),
            RadiusX = 37,
            RadiusY = 37
        };

        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(pathGeom);
        group.Children.Add(dotGeom);

        var effectiveFill = fill
            ?? (Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var b) ? b as Brush : null);

        var canvas = new Canvas { Width = 584, Height = 644 };
        var path = new XamlPath
        {
            Data = group,
            Fill = effectiveFill,
            RenderTransform = new TranslateTransform { X = -220, Y = -156 }
        };
        canvas.Children.Add(path);

        return new Viewbox
        {
            Width = sizeDip,
            Height = sizeDip,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas
        };
    }

    public static string GetCompactLabel(PlyphAction action) => action.Mode switch
    {
        "ask" => "Ask",
        "correct" => "Correct",
        "rewrite" => "Rewrite",
        "prompt" => "Run",
        _ => action.Name
    };

    public static (int X, int Y) PositionNearAnchor(
        WinPoint anchor,
        int physW,
        int physH,
        bool preferAbove = false,
        int gapPx = 8,
        int marginPx = 8)
    {
        NativeMethods.GetWorkAreaMonitorIndex(anchor, out var work);

        var x = anchor.X;
        if (x + physW > work.Right - marginPx)
            x = work.Right - physW - marginPx;
        if (x < work.Left + marginPx)
            x = work.Left + marginPx;

        int y;
        if (preferAbove)
        {
            y = anchor.Y - physH - gapPx;
            if (y < work.Top + marginPx)
                y = anchor.Y + gapPx;
        }
        else
        {
            y = anchor.Y + gapPx;
            if (y + physH > work.Bottom - marginPx && anchor.Y - physH - gapPx >= work.Top + marginPx)
                y = anchor.Y - physH - gapPx;
        }

        y = Math.Clamp(y, work.Top + marginPx, Math.Max(work.Top + marginPx, work.Bottom - physH - marginPx));
        return (x, y);
    }

    public static (int X, int Y) PositionNearSelectionRect(
        System.Drawing.Rectangle selectionRect,
        int physW,
        int physH,
        int gapPx = 6,
        int marginPx = 8,
        bool alignToTop = false)
    {
        var center = new WinPoint(
            selectionRect.Left + selectionRect.Width / 2,
            selectionRect.Top + selectionRect.Height / 2);
        NativeMethods.GetWorkAreaMonitorIndex(center, out var work);

        // Place directly to the right of single-line/compact selection if it fits inside monitor bounds
        if (selectionRect.Height <= Math.Max(48, physH * 2) &&
            selectionRect.Right + gapPx + physW <= work.Right - marginPx)
        {
            var sideX = Math.Clamp(selectionRect.Right + gapPx, work.Left + marginPx, Math.Max(work.Left + marginPx, work.Right - physW - marginPx));
            var sideY = alignToTop ? selectionRect.Top : selectionRect.Top + (selectionRect.Height - physH) / 2;
            sideY = Math.Clamp(sideY, work.Top + marginPx, Math.Max(work.Top + marginPx, work.Bottom - physH - marginPx));
            return (sideX, sideY);
        }

        if (alignToTop && selectionRect.Left - gapPx - physW >= work.Left + marginPx)
        {
            return (selectionRect.Left - gapPx - physW, Math.Clamp(selectionRect.Top,
                work.Top + marginPx, Math.Max(work.Top + marginPx, work.Bottom - physH - marginPx)));
        }

        // Otherwise place cleanly just below the selection (or flip cleanly above without covering the text)
        var x = Math.Clamp(
            selectionRect.Right - physW >= work.Left + marginPx && selectionRect.Width < physW
                ? selectionRect.Left
                : selectionRect.Right + gapPx - (selectionRect.Right + gapPx + physW > work.Right - marginPx ? physW : 0),
            work.Left + marginPx,
            Math.Max(work.Left + marginPx, work.Right - physW - marginPx));

        var y = selectionRect.Bottom + gapPx;
        if (y + physH > work.Bottom - marginPx && selectionRect.Top - physH - gapPx >= work.Top + marginPx)
            y = selectionRect.Top - physH - gapPx;

        y = Math.Clamp(y, work.Top + marginPx, Math.Max(work.Top + marginPx, work.Bottom - physH - marginPx));
        return (x, y);
    }

    public static Size MeasureDip(FrameworkElement element, double maxWidthDip = double.PositiveInfinity, double maxHeightDip = double.PositiveInfinity)
    {
        element.Measure(new Size(maxWidthDip, maxHeightDip));
        return element.DesiredSize;
    }

    public static void EnableDrag(Window window, UIElement dragHandle)
    {
        var dragging = false;
        var startCursor = new NativeMethods.POINT();
        var startPos = new PointInt32();

        dragHandle.PointerPressed += (_, e) =>
        {
            if (window.AppWindow == null) return;
            var pt = e.GetCurrentPoint(dragHandle);
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (!NativeMethods.GetCursorPos(out startCursor)) return;
            startPos = window.AppWindow.Position;
            dragging = dragHandle.CapturePointer(e.Pointer);
        };

        dragHandle.PointerMoved += (_, _) =>
        {
            if (!dragging || window.AppWindow == null) return;
            if (!NativeMethods.GetCursorPos(out var cur)) return;
            var dx = cur.X - startCursor.X;
            var dy = cur.Y - startCursor.Y;
            window.AppWindow.Move(new PointInt32(startPos.X + dx, startPos.Y + dy));
        };

        dragHandle.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            dragHandle.ReleasePointerCapture(e.Pointer);
        };

        dragHandle.PointerCaptureLost += (_, _) => dragging = false;
    }
}
