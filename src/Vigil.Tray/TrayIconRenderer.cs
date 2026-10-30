using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vigil.Tray;

namespace Vigil.Tray.Ui;

/// <summary>
/// Draws the five tray icons, so there are no image assets to ship or to take from anyone. Each state has its own shape (a ring
/// with a dot, a solid disc, two bars, an exclamation mark, a dashed ring), so the icons differ without relying on color.
/// </summary>
/// <remarks>
/// On macOS the icons are template images (black on transparent): the menu bar tints them for light and dark mode, which is
/// why they carry no color. The Windows notification area and Linux panels do not tint, and their background is dark or light
/// at the user's choice, so there each state is a colored disc with a white shape, which reads on both.
/// </remarks>
internal static class TrayIconRenderer
{
    private const int Size = 64;

    private static readonly Point Center = new(Size / 2.0, Size / 2.0);

    /// <summary>True where the menu bar tints template images (macOS only).</summary>
    public static bool UsesTemplateImages => OperatingSystem.IsMacOS();

    public static WindowIcon Render(TrayIconKind kind)
    {
        var template = UsesTemplateImages;
        var shape = template ? Brushes.Black : Brushes.White;
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext(clear: true))
        {
            if (template)
            {
                DrawTemplate(context, kind, shape);
            }
            else
            {
                DrawColored(context, kind, shape);
            }
        }

        return new WindowIcon(bitmap);
    }

    private static void DrawTemplate(DrawingContext context, TrayIconKind kind, IBrush ink)
    {
        var ring = new Pen(ink, 5, kind == TrayIconKind.NotRunning ? new DashStyle([1.6, 1.6], 0) : null);
        context.DrawEllipse(null, ring, Center, 27, 27);
        DrawShape(context, kind, ink, insideRing: true);
    }

    private static void DrawColored(DrawingContext context, TrayIconKind kind, IBrush ink)
    {
        if (kind == TrayIconKind.NotRunning)
        {
            // Grey and hollow: there is nothing to show about a daemon that is not there.
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 6, new DashStyle([1.4, 1.4], 0)), Center, 27, 27);
            return;
        }

        context.DrawEllipse(new SolidColorBrush(ColorOf(kind)), null, Center, 31, 31);
        DrawShape(context, kind, ink, insideRing: false);
    }

    private static Color ColorOf(TrayIconKind kind) => kind switch
    {
        TrayIconKind.Watching => Color.FromRgb(0x2E, 0x8B, 0x57),
        TrayIconKind.Injected => Color.FromRgb(0x1E, 0x78, 0xD2),
        TrayIconKind.Paused => Color.FromRgb(0xB8, 0x7A, 0x00),
        TrayIconKind.Degraded => Color.FromRgb(0xC6, 0x28, 0x28),
        _ => Color.FromRgb(0x80, 0x80, 0x80),
    };

    private static void DrawShape(DrawingContext context, TrayIconKind kind, IBrush ink, bool insideRing)
    {
        // Inside a drawn ring the shapes are smaller, so they do not touch it.
        var scale = insideRing ? 0.8 : 1.0;
        switch (kind)
        {
            case TrayIconKind.Watching:
                context.DrawEllipse(ink, null, Center, 7 * scale, 7 * scale);
                break;
            case TrayIconKind.Injected:
                context.DrawEllipse(ink, null, Center, 17 * scale, 17 * scale);
                break;
            case TrayIconKind.Paused:
                context.DrawRectangle(ink, null, new Rect(Center.X - 13 * scale, Center.Y - 15 * scale, 9 * scale, 30 * scale));
                context.DrawRectangle(ink, null, new Rect(Center.X + 4 * scale, Center.Y - 15 * scale, 9 * scale, 30 * scale));
                break;
            case TrayIconKind.Degraded:
                context.DrawRectangle(ink, null, new Rect(Center.X - 4.5 * scale, Center.Y - 18 * scale, 9 * scale, 22 * scale));
                context.DrawEllipse(ink, null, new Point(Center.X, Center.Y + 13 * scale), 5.5 * scale, 5.5 * scale);
                break;
            case TrayIconKind.NotRunning:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown icon.");
        }
    }
}
