using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Soffritto.Desktop;

/// <summary>
/// Default window background when the Ctrl+T scenery is off: a still pixel-art image on the same
/// 4 DIP grid as the sceneries, in stone greys. A dithered diagonal gradient, lighter at the top
/// left, and on the right a single large mountain in the style of Mount Fuji, in soft flat tones
/// with a faint snow cap. Redrawn only on resize and theme change.
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

    private static readonly Palette Light = new(
        Sky: [0xFFF1F1EE, 0xFFECECE9, 0xFFE6E6E3, 0xFFE1E1DE, 0xFFDBDBD8],
        Mountain: [0xFFD3D4D1, 0xFFCBCDCA, 0xFFC3C6C4],
        Rim: 0xFFDEDFDC,
        Snow: 0xFFE8E9E6);

    private static readonly Palette Dark = new(
        Sky: [0xFF1F1F1E, 0xFF1C1C1C, 0xFF1A1A1A, 0xFF171717, 0xFF151515],
        Mountain: [0xFF252627, 0xFF2A2B2D, 0xFF2E2F31],
        Rim: 0xFF303133,
        Snow: 0xFF333436);

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

    private static uint[] Render(int cols, int rows, Palette palette)
    {
        var pixels = new uint[cols * rows];
        var mountain = new Mountain(cols, rows);

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var threshold = Bayer[((y & 3) * 4) + (x & 3)];
                pixels[(y * cols) + x] = mountain.Covers(x, y)
                    ? mountain.Color(x, y, palette, threshold)
                    : Pick(palette.Sky, SkyLevel(x, y, cols, rows) * (palette.Sky.Length - 1), threshold);
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

    // Hash in [0, 1) of an integer, stable across redraws.
    private static double Hash(int i)
    {
        unchecked
        {
            var h = (uint)(i * 374761393) + 668265263u;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
    }

    /// <summary>
    /// A single large volcano on the right: concave flanks widening toward the bottom edge, an
    /// uneven truncated top, a light rim on the crest, a faint snow cap that breaks up into dithered
    /// pixels down the flanks, and the body darkening in dithered steps toward the bottom.
    /// </summary>
    private sealed class Mountain
    {
        private readonly int rows;
        private readonly double height;
        private readonly double snowBand;
        private readonly int[] top;
        private readonly double[] snowLine;

        public Mountain(int cols, int rows)
        {
            this.rows = rows;
            var center = cols * 0.76;
            height = rows * 0.34;
            var plain = rows * 0.05;
            var summit = height * 0.1;
            var foot = height * 2.6;

            snowBand = height * 0.16;
            top = new int[cols];
            snowLine = new double[cols];
            for (var x = 0; x < cols; x++)
            {
                // Fuji-like profile: flat for the summit, then flanks that fall steeply and level out
                // into a low plain running to the window's edges.
                var offset = (int)Math.Floor(x - center);
                var dx = Math.Abs(x - center);
                var h = dx <= summit ? height : plain + ((height - plain) * Math.Pow(1 - Math.Min(1, (dx - summit) / (foot - summit)), 2.6));

                // An uneven top: steps of one or two pixels every couple of columns near the summit,
                // fading out down the flanks, and a small crater notch.
                var nearTop = Math.Clamp(1 - ((dx - summit) / (summit * 3)), 0, 1);
                h -= Math.Floor(Hash(offset / 2) * 2.6) * nearTop;
                if (dx < summit * 0.5 && Hash((offset / 3) + 41) > 0.35)
                {
                    h -= 1;
                }

                top[x] = Math.Clamp(rows - (int)Math.Round(h), 0, rows);

                // Where the snow starts thinning: a little uneven, in steps of a few columns.
                snowLine[x] = (height * 0.8) - (Hash((offset / 4) + 211) * height * 0.05);
            }
        }

        public bool Covers(int x, int y) => y >= top[x];

        public uint Color(int x, int y, Palette palette, double threshold)
        {
            if (y == top[x])
            {
                return palette.Rim;
            }

            // Solid snow at the top, then a band where it breaks up into dithered pixels.
            var snow = ((rows - y) - (snowLine[x] - snowBand)) / snowBand;
            if (snow > threshold)
            {
                return palette.Snow;
            }

            var depth = (double)(y - top[x]) / Math.Max(1, rows - top[x]);
            return Pick(palette.Mountain, depth * (palette.Mountain.Length - 1) * 1.2, threshold);
        }
    }

    private sealed record Palette(uint[] Sky, uint[] Mountain, uint Rim, uint Snow);
}
