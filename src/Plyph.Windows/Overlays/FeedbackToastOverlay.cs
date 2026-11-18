using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.UI.Dispatching;
using Plyph.Core;
using Plyph.Windows.Interop;
using Plyph.Windows.UI;

namespace Plyph.Windows.Overlays;

/// <summary>
/// Non-activating layered status toast ("Working…", "Replaced", "Copied", etc.) matching Windows 11 Light/Dark themes.
/// </summary>
public sealed class FeedbackToastOverlay : LayeredOverlayWindow
{
    private readonly IPlyphSettings _settings;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _spinTimer;
    private string _message = "";
    private string _state = "success";
    private float _spinAngle;
    private bool _isDark;

    public FeedbackToastOverlay(IPlyphSettings settings) : base(nonActivating: true)
    {
        _settings = settings;

        var dq = DispatcherQueue.GetForCurrentThread();
        _spinTimer = dq.CreateTimer();
        _spinTimer.Interval = TimeSpan.FromMilliseconds(45);
        _spinTimer.IsRepeating = true;
        _spinTimer.Tick += (_, _) =>
        {
            _spinAngle = (_spinAngle + 24f) % 360f;
            if (Visible)
                Render();
        };

        _hideTimer = dq.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            _spinTimer.Stop();
            HideOverlay();
        };
    }

    public void ShowStatus(string message, string state, int durationMs = 0)
    {
        _isDark = ThemeHelper.ShouldUseDark(_settings);
        _message = message;
        _state = state;
        _hideTimer.Stop();
        _spinTimer.Stop();

        var s = DpiScale;
        var shadowPad = (int)Math.Ceiling(5f * s);
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var font = new Font("Segoe UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        var textSize = g.MeasureString(message, font, (int)(360 * s));

        var contentW = Math.Max((int)Math.Ceiling(160 * s), (int)Math.Ceiling(50 * s + textSize.Width));
        var contentH = Math.Max((int)Math.Ceiling(38 * s), (int)Math.Ceiling(textSize.Height + 18 * s));
        var totalW = contentW + shadowPad * 2;
        var totalH = contentH + shadowPad * 2;

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.GetWorkAreaMonitorIndex(new Point(pt.X, pt.Y), out var work);

        var margin = (int)Math.Round(18 * s);
        var posSetting = _settings.GetString("feedback-position");

        int x = posSetting switch
        {
            "top-left" or "center-left" or "bottom-left" => work.Left + margin,
            "top-right" or "center-right" or "bottom-right" => work.Right - totalW - margin,
            _ => work.Left + (work.Right - work.Left - totalW) / 2
        };

        int y = posSetting switch
        {
            "top-left" or "top-center" or "top-right" => work.Top + margin,
            "center-left" or "center" or "center-right" => work.Top + (work.Bottom - work.Top - totalH) / 2,
            _ => work.Bottom - totalH - margin
        };

        SetPositionAndSize(x, y, totalW, totalH);
        ShowOverlay(activateIfAllowed: false);

        if (state == "working")
            _spinTimer.Start();

        if (durationMs > 0)
        {
            _hideTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _hideTimer.Start();
        }
    }

    public void Hide()
    {
        _hideTimer.Stop();
        _spinTimer.Stop();
        if (Visible)
            HideOverlay();
    }

    protected override void OnPaintOverlay(Graphics g, int width, int height)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var s = DpiScale;
        var shadowPad = 5f * s;
        var rect = new RectangleF(shadowPad, shadowPad, width - shadowPad * 2 - 1f, height - shadowPad * 2 - 1f);

        for (var i = 3; i >= 1; i--)
        {
            var expand = i * 1.1f * s;
            var sRect = new RectangleF(rect.X - expand * 0.4f, rect.Y + i * 0.9f * s, rect.Width + expand * 0.8f, rect.Height + expand * 0.8f);
            using var sPath = IconRenderer.CreateRoundedRectPath(sRect, rect.Height / 2f);
            using var sBrush = new SolidBrush(Color.FromArgb(14, 0, 0, 0));
            g.FillPath(sBrush, sPath);
        }

        var bg = _isDark ? Color.FromArgb(245, 44, 44, 46) : Color.FromArgb(248, 252, 252, 254);
        var border = _isDark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(65, 70, 76, 88);
        var fg = _isDark ? Color.FromArgb(250, 250, 250) : Color.FromArgb(27, 29, 34);

        using (var path = IconRenderer.CreateRoundedRectPath(rect, rect.Height / 2f))
        {
            using var bgBrush = new SolidBrush(bg);
            g.FillPath(bgBrush, path);

            using var borderPen = new Pen(border, 1f * s);
            g.DrawPath(borderPen, path);
        }

        var iconSize = 16f * s;
        var iconRect = new RectangleF(rect.X + 14f * s, rect.Y + (rect.Height - iconSize) / 2f, iconSize, iconSize);
        var iconColor = _state switch
        {
            "success" => Color.FromArgb(22, 163, 74),
            "error" => Color.FromArgb(220, 38, 38),
            _ => Color.FromArgb(0, 103, 192)
        };

        if (_state == "working")
        {
            using var arcPen = new Pen(iconColor, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arcPen, iconRect, _spinAngle, 265f);
        }
        else
        {
            IconRenderer.DrawIcon(g, _state, iconRect, iconColor);
        }

        using var font = new Font("Segoe UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(fg);
        var textRect = new RectangleF(iconRect.Right + 10f * s, rect.Y, rect.Right - iconRect.Right - 22f * s, rect.Height);
        using var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        g.DrawString(_message, font, textBrush, textRect, sf);
    }

    public override void Dispose()
    {
        _hideTimer.Stop();
        _spinTimer.Stop();
        base.Dispose();
    }
}
