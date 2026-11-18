using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Plyph.Windows.UI;

public static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static void DrawIcon(Graphics g, string iconName, RectangleF bounds, Color color)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        try
        {
            switch (iconName)
            {
                case "plyph":
                case "plyph-symbolic":
                    DrawPlyphLogo(g, bounds, color);
                    break;
                case "ask":
                case "dialog-question-symbolic":
                    DrawAskIcon(g, bounds, color);
                    break;
                case "correct":
                case "tools-check-spelling-symbolic":
                    DrawCorrectIcon(g, bounds, color);
                    break;
                case "rewrite":
                case "document-edit-symbolic":
                    DrawRewriteIcon(g, bounds, color);
                    break;
                case "prompt":
                case "system-run-symbolic":
                    DrawPromptIcon(g, bounds, color);
                    break;
                case "custom":
                    DrawCustomSparkleIcon(g, bounds, color);
                    break;
                case "more":
                    DrawMoreIcon(g, bounds, color);
                    break;
                case "previous":
                    DrawChevronIcon(g, bounds, color, left: true);
                    break;
                case "next":
                    DrawChevronIcon(g, bounds, color, left: false);
                    break;
                case "refine":
                    DrawRewriteIcon(g, bounds, color);
                    break;
                case "submit":
                    DrawSubmitIcon(g, bounds, color);
                    break;
                case "working":
                case "content-loading-symbolic":
                    DrawWorkingIcon(g, bounds, color);
                    break;
                case "success":
                case "emblem-ok-symbolic":
                case "object-select-symbolic":
                    DrawCheckIcon(g, bounds, color);
                    break;
                case "error":
                case "dialog-error-symbolic":
                    DrawErrorIcon(g, bounds, color);
                    break;
                case "copy":
                case "edit-copy-symbolic":
                    DrawCopyIcon(g, bounds, color);
                    break;
                case "fullscreen":
                case "view-fullscreen-symbolic":
                    DrawExpandIcon(g, bounds, color, restore: false);
                    break;
                case "restore":
                case "view-restore-symbolic":
                    DrawExpandIcon(g, bounds, color, restore: true);
                    break;
                case "close":
                case "window-close-symbolic":
                    DrawCloseIcon(g, bounds, color);
                    break;
                case "settings":
                    DrawSettingsIcon(g, bounds, color);
                    break;
                case "providers":
                    DrawProvidersIcon(g, bounds, color);
                    break;
                case "shortcuts":
                    DrawShortcutsIcon(g, bounds, color);
                    break;
                case "diff":
                    DrawDiffIcon(g, bounds, color);
                    break;
                default:
                    DrawPlyphLogo(g, bounds, color);
                    break;
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static void DrawPlyphLogo(Graphics g, RectangleF bounds, Color color)
    {
        // Exact vector geometry from assets/icons/plyph-symbolic.svg (viewBox 160 126 704 704)
        g.TranslateTransform(bounds.X, bounds.Y);
        var scaleX = bounds.Width / 704f;
        var scaleY = bounds.Height / 704f;
        g.ScaleTransform(scaleX, scaleY);
        g.TranslateTransform(-160f, -126f);

        using var path = new GraphicsPath(FillMode.Alternate);
        path.StartFigure();
        path.AddBezier(512, 156, 496, 257, 458, 365, 401, 455);
        path.AddBezier(401, 455, 349, 537, 286, 607, 220, 660);
        path.AddBezier(220, 660, 302, 636, 368, 637, 419, 675);
        path.AddBezier(419, 675, 465, 709, 493, 755, 512, 800);
        path.AddBezier(512, 800, 531, 755, 559, 709, 605, 675);
        path.AddBezier(605, 675, 656, 637, 722, 636, 804, 660);
        path.AddBezier(804, 660, 738, 607, 675, 537, 623, 455);
        path.AddBezier(623, 455, 566, 365, 528, 257, 512, 156);
        path.CloseFigure();

        path.StartFigure();
        path.AddBezier(512, 405, 461, 405, 424, 439, 424, 487);
        path.AddBezier(424, 487, 424, 539, 444, 594, 468, 650);
        path.AddBezier(468, 650, 490, 702, 505, 751, 512, 800);
        path.AddBezier(512, 800, 509, 757, 507, 714, 511, 678);
        path.AddBezier(511, 678, 516, 635, 532, 612, 559, 586);
        path.AddBezier(559, 586, 589, 557, 600, 527, 600, 488);
        path.AddBezier(600, 488, 600, 440, 563, 405, 512, 405);
        path.CloseFigure();

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        g.FillEllipse(brush, 512 - 37, 493 - 37, 74, 74);
    }

    private static void SetupViewBox24(Graphics g, RectangleF bounds)
    {
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
    }

    private static Pen CreateStrokePen(Color color, float width = 1.75f)
    {
        return new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
    }

    private static void DrawAskIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color);
        using var path = new GraphicsPath();
        path.AddLines(new PointF[]
        {
            new(5f, 3.5f), new(19f, 3.5f), new(20.5f, 5f),
            new(20.5f, 16f), new(19f, 17.5f), new(12f, 17.5f),
            new(7f, 21f), new(7f, 17.5f), new(5f, 17.5f),
            new(3.5f, 16f), new(3.5f, 5f)
        });
        path.CloseFigure();
        g.DrawPath(pen, path);

        g.DrawArc(pen, 9.8f, 6.2f, 4.4f, 3.8f, 190f, 230f);
        g.DrawLine(pen, 12f, 10.2f, 12f, 11.7f);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 11.1f, 13.6f, 1.8f, 1.8f);
    }

    private static void DrawCorrectIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color);
        using var wand = new GraphicsPath();
        wand.AddPolygon(new PointF[]
        {
            new(8.5f, 10.5f), new(19.5f, 21.5f), new(21.5f, 19.5f), new(10.5f, 8.5f)
        });
        g.DrawPath(pen, wand);
        g.DrawLine(pen, 11f, 13f, 13f, 11f);
        // Sparkles
        g.DrawLine(pen, 5f, 2f, 5f, 6f);
        g.DrawLine(pen, 3f, 4f, 7f, 4f);
        g.DrawLine(pen, 16f, 2f, 16f, 6f);
        g.DrawLine(pen, 14f, 4f, 18f, 4f);
        g.DrawLine(pen, 3f, 12f, 3f, 16f);
        g.DrawLine(pen, 1f, 14f, 5f, 14f);
    }

    private static void DrawRewriteIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color);
        g.DrawLines(pen, new PointF[]
        {
            new(10f, 5f), new(4f, 5f), new(4f, 20f), new(18f, 20f), new(18f, 14f)
        });
        using var pencil = new GraphicsPath();
        pencil.AddPolygon(new PointF[]
        {
            new(11f, 13f), new(12f, 9f), new(19f, 2f), new(22f, 5f), new(15f, 12f)
        });
        g.DrawPath(pen, pencil);
        g.DrawLine(pen, 17f, 4f, 20f, 7f);
    }

    private static void DrawPromptIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var brush = new SolidBrush(color);
        using var path = new GraphicsPath();
        path.AddPolygon(new PointF[]
        {
            new(6.5f, 3.5f), new(20.5f, 12f), new(6.5f, 20.5f)
        });
        g.FillPath(brush, path);
    }

    private static void DrawCustomSparkleIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 1.6f);
        g.DrawPolygon(pen, new PointF[]
        {
            new(10f, 4f), new(12.3f, 10.7f), new(19f, 13f), new(12.3f, 15.3f),
            new(10f, 22f), new(7.7f, 15.3f), new(1f, 13f), new(7.7f, 10.7f)
        });
        g.DrawPolygon(pen, new PointF[]
        {
            new(19f, 1f), new(20.1f, 4.9f), new(24f, 6f), new(20.1f, 7.1f),
            new(19f, 11f), new(17.9f, 7.1f), new(14f, 6f), new(17.9f, 4.9f)
        });
    }

    private static void DrawMoreIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 3.3f, 10.3f, 3.4f, 3.4f);
        g.FillEllipse(brush, 10.3f, 10.3f, 3.4f, 3.4f);
        g.FillEllipse(brush, 17.3f, 10.3f, 3.4f, 3.4f);
    }

    private static void DrawChevronIcon(Graphics g, RectangleF bounds, Color color, bool left)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 2.2f);
        if (left)
            g.DrawLines(pen, new PointF[] { new(15f, 5f), new(9f, 12f), new(15f, 19f) });
        else
            g.DrawLines(pen, new PointF[] { new(9f, 5f), new(15f, 12f), new(9f, 19f) });
    }

    private static void DrawSubmitIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var brush = new SolidBrush(color);
        using var path = new GraphicsPath();
        path.AddPolygon(new PointF[]
        {
            new(3f, 3f), new(21f, 12f), new(3f, 21f), new(5.5f, 13.2f), new(12f, 12f), new(5.5f, 10.8f)
        });
        g.FillPath(brush, path);
    }

    private static void DrawWorkingIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 2.3f);
        g.DrawArc(pen, 4f, 4f, 16f, 16f, 0f, 270f);
    }

    private static void DrawCheckIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 2.3f);
        g.DrawLines(pen, new PointF[] { new(4.5f, 12.5f), new(9.5f, 17.5f), new(19.5f, 6.5f) });
    }

    private static void DrawErrorIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 2.1f);
        g.DrawEllipse(pen, 3f, 3f, 18f, 18f);
        g.DrawLine(pen, 12f, 7.5f, 12f, 13f);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 10.9f, 15.5f, 2.2f, 2.2f);
    }

    private static void DrawCopyIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 1.8f);
        g.DrawRectangle(pen, 8, 8, 11, 12);
        g.DrawLines(pen, new PointF[] { new(15f, 8f), new(15f, 4f), new(5f, 4f), new(5f, 16f), new(8f, 16f) });
    }

    private static void DrawExpandIcon(Graphics g, RectangleF bounds, Color color, bool restore)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 1.9f);
        if (!restore)
        {
            g.DrawLines(pen, new PointF[] { new(4f, 9f), new(4f, 4f), new(9f, 4f) });
            g.DrawLines(pen, new PointF[] { new(15f, 4f), new(20f, 4f), new(20f, 9f) });
            g.DrawLines(pen, new PointF[] { new(20f, 15f), new(20f, 20f), new(15f, 20f) });
            g.DrawLines(pen, new PointF[] { new(9f, 20f), new(4f, 20f), new(4f, 15f) });
        }
        else
        {
            g.DrawLines(pen, new PointF[] { new(4f, 9f), new(9f, 9f), new(9f, 4f) });
            g.DrawLines(pen, new PointF[] { new(15f, 4f), new(15f, 9f), new(20f, 9f) });
            g.DrawLines(pen, new PointF[] { new(20f, 15f), new(15f, 15f), new(15f, 20f) });
            g.DrawLines(pen, new PointF[] { new(9f, 20f), new(9f, 15f), new(4f, 15f) });
        }
    }

    private static void DrawCloseIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 2.0f);
        g.DrawLine(pen, 6f, 6f, 18f, 18f);
        g.DrawLine(pen, 18f, 6f, 6f, 18f);
    }

    private static void DrawSettingsIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 1.8f);
        g.DrawLine(pen, 4f, 7f, 20f, 7f);
        g.DrawLine(pen, 4f, 12f, 20f, 12f);
        g.DrawLine(pen, 4f, 17f, 20f, 17f);
        using var bgBrush = new SolidBrush(color);
        g.FillEllipse(bgBrush, 7f, 4.8f, 4.4f, 4.4f);
        g.FillEllipse(bgBrush, 13.5f, 9.8f, 4.4f, 4.4f);
        g.FillEllipse(bgBrush, 8.5f, 14.8f, 4.4f, 4.4f);
    }

    private static void DrawProvidersIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 1.8f);
        g.DrawEllipse(pen, 3.5f, 3.5f, 17f, 17f);
        g.DrawEllipse(pen, 8.2f, 3.5f, 7.6f, 17f);
        g.DrawLine(pen, 3.8f, 12f, 20.2f, 12f);
    }

    private static void DrawShortcutsIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 1.8f);
        g.DrawRectangle(pen, 3f, 6f, 18f, 12f);
        g.DrawLine(pen, 7f, 10f, 8.5f, 10f);
        g.DrawLine(pen, 11.5f, 10f, 13f, 10f);
        g.DrawLine(pen, 16f, 10f, 17f, 10f);
        g.DrawLine(pen, 7f, 14f, 17f, 14f);
    }

    private static void DrawDiffIcon(Graphics g, RectangleF bounds, Color color)
    {
        SetupViewBox24(g, bounds);
        using var pen = CreateStrokePen(color, 1.8f);
        g.DrawLine(pen, 12f, 4.5f, 12f, 12.5f);
        g.DrawLine(pen, 8f, 8.5f, 16f, 8.5f);
        g.DrawLine(pen, 8f, 17.5f, 16f, 17.5f);
    }

    public static IntPtr CreateStatusIconHandle(string state = "default", int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(Color.FromArgb(14, 17, 22));
            g.FillEllipse(bgBrush, 1, 1, size - 2, size - 2);

            using var borderPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1.2f);
            g.DrawEllipse(borderPen, 1, 1, size - 2, size - 2);

            var pad = size * 0.18f;
            var iconRect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);

            switch (state)
            {
                case "working":
                    DrawIcon(g, "working", iconRect, Color.FromArgb(120, 180, 255));
                    break;
                case "success":
                    DrawIcon(g, "success", iconRect, Color.FromArgb(87, 227, 137));
                    break;
                case "error":
                    DrawIcon(g, "error", iconRect, Color.FromArgb(255, 123, 99));
                    break;
                default:
                    DrawIcon(g, "plyph", iconRect, Color.White);
                    break;
            }
        }

        return bmp.GetHicon();
    }

    public static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        var arc = new RectangleF(rect.X, rect.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
