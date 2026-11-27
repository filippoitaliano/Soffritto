using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Soffritto.Desktop;

/// <summary>
/// Default window background when the Ctrl+T scenery is off: a still pixel-art image on the same
/// 4 DIP grid as the sceneries, in stone greys: a dithered diagonal gradient, lighter at the top
/// left. Redrawn only on resize and theme change.
/// </summary>
public sealed class PixelBackdrop : Canvas
{
    private const int PixelSize = 4;

    // Bayer 4x4 thresholds: the gradient bands blend with a regular pixel texture instead of
    // smooth steps, as in the old palette-limited games.
    private static readonly double[] Bayer =
    [
        0 / 16.0, 8 / 16.0, 2 / 16.0, 10 / 16.0,
        12 / 16.0, 4 / 16.0, 14 / 16.0, 6 / 16.0,
        3 / 16.0, 11 / 16.0, 1 / 16.0, 9 / 16.0,
        15 / 16.0, 7 / 16.0, 13 / 16.0, 5 / 16.0,
    ];

    private static readonly uint[] Light = [0xFFF1F1EE, 0xFFECECE9, 0xFFE6E6E3, 0xFFE1E1DE, 0xFFDBDBD8];

    private static readonly uint[] Dark = [0xFF1F1F1E, 0xFF1C1C1C, 0xFF1A1A1A, 0xFF171717, 0xFF151515];

    private readonly Image image = new() { Stretch = Stretch.Fill };

    public PixelBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Children.Add(image);

        SizeChanged += (_, _) => Build();
        ThemeManager.Changed += Build;
    }

    private void Build()
    {
        var cols = (int)Math.Ceiling(ActualWidth / PixelSize);
        var rows = (int)Math.Ceiling(ActualHeight / PixelSize);
        if (cols < 1 || rows < 1)
        {
            image.Source = null;
            return;
        }

        var pixels = Render(cols, rows, ThemeManager.IsDark ? Dark : Light);
        var bitmap = BitmapSource.Create(cols, rows, 96, 96, PixelFormats.Bgra32, null, pixels, cols * 4);
        bitmap.Freeze();
        image.Source = bitmap;
        image.Width = cols * PixelSize;
        image.Height = rows * PixelSize;

        // Anchored to the bottom edge, like the sceneries: the rounding overflow is clipped at the top.
        SetTop(image, Math.Floor(ActualHeight - image.Height));
    }

    private static uint[] Render(int cols, int rows, uint[] shades)
    {
        var pixels = new uint[cols * rows];

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var threshold = Bayer[((y & 3) * 4) + (x & 3)];
                pixels[(y * cols) + x] = Pick(shades, SkyLevel(x, y, cols, rows) * (shades.Length - 1), threshold);
            }
        }

        return pixels;
    }

    // 0 at the top left, 1 at the bottom right, with a lighter halo at the top left and a slightly
    // deeper corner at the bottom right, in the places of the old gradient's spots.
    private static double SkyLevel(int x, int y, int cols, int rows)
    {
        var u = (double)x / cols;
        var v = (double)y / rows;
        var level = (u * 0.55) + (v * 0.45);
        level -= 0.35 * Math.Exp(-((u - 0.12) * (u - 0.12) / 0.05) - (v * v / 0.12));
        level += 0.15 * Math.Exp(-((u - 0.9) * (u - 0.9) / 0.04) - ((v - 1) * (v - 1) / 0.08));
        return Math.Clamp(level, 0, 1);
    }

    private static uint Pick(uint[] shades, double level, double threshold)
    {
        level = Math.Clamp(level, 0, shades.Length - 1);
        var lower = (int)level;
        var index = level - lower > threshold ? lower + 1 : lower;
        return shades[Math.Min(index, shades.Length - 1)];
    }
}
