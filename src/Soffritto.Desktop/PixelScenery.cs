using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Soffritto.Desktop;

/// <summary>
/// Ctrl+T easter egg: instead of the background gradient, a pixel-art scene picked at random
/// (different from the previous one) among three Terraria-style landscapes (snow, jungle and ocean,
/// with clouds, birds, leaves, snowflakes, fireflies and waves), the inside of an old power station
/// turned into a venue, with a comedian on stage, the audience at the tables and the bar, and the
/// art deco seafront in the style of the Florida crime sagas. Pressing it again goes back to the gradient.
/// </summary>
public sealed partial class PixelScenery : Canvas
{
    // A landscape pixel is a 4 DIP square: the grid stays small (under 500x300 on a full HD
    // screen) and redrawing all of it every frame is cheap.
    private const int PixelSize = 4;

    // Ground rows at the bottom of the window: the Ctrl+G kitten walks on the grass.
    private const int GroundRows = 3;
    private const int FireflyHeight = 70;
    private const double SeaLevel = 0.74;

    // Sizes are designed around the kitten, about fourteen landscape pixels tall: as with the
    // Terraria player, trees tower two to four times above its head.
    private static readonly string[] BirdUp = ["XX.....XX", "..X...X..", "...XXX...", "....X...."];
    private static readonly string[] BirdDown = [".........", "...XXX...", "XXX.X.XXX", "X.......X"];

    private static readonly TreeColors JungleWood = new(0xFF5B3A26, 0xFF432A1B, 0xFF2E7D32, 0xFF4FAA3C, 0xFF1F5E25);

    private static readonly Biome[] Biomes =
    [
        // Snow
        new(
            Sky: [0xFF6A9FD6, 0xFF97C2E8, 0xFFC6E0F3, 0xFFE8F3FB],
            Far: new(0xFF8FA8C6, 0xFFA6BCD6, 0.78, 0.13, 120, Peaks: true, Cap: 0xFFF5F9FD),
            Mid: new(0xFFBCD0E4, 0xFFDDE9F4, 0.885, 0.05, 100),
            Soil: new(0xFFFFFFFF, 0xFFE2EEF8, 0xFFB9D5EA, 0xFF97BFDF),
            Plant: Pine,
            Spacing: (12, 30),
            Life: Life.Clouds | Life.Snow,
            Cloud: 0xFFFFFFFF,
            CloudShade: 0xFFDDE8F2,
            Chrome: 0xFF2E4A6B),

        // Jungle
        new(
            Sky: [0xFF3F8EC4, 0xFF63B2B4, 0xFF9AD3A2, 0xFFC9E9B8],
            Far: new(0xFF4F9060, 0xFF62A56D, 0.78, 0.05, 28),
            Mid: new(0xFF2E6A3B, 0xFF3D8247, 0.865, 0.045, 19),
            Soil: new(0xFF9BE03E, 0xFF6CB82E, 0xFF5A3D2B, 0xFF45301F),
            Plant: JungleTree,
            Spacing: (17, 36),
            Life: Life.Clouds | Life.Birds | Life.Leaves | Life.Fireflies,
            Cloud: 0xFFFFFFFF,
            CloudShade: 0xFFDCEBDF,
            Birds: [0xFFE0453A, 0xFF3A7BE0, 0xFFF2C230],
            Flowers: [0xFFE24FA0, 0xFFF29A2E],
            FireflyEvery: 40,
            Chrome: 0xFF1E4A2B),

        // Ocean: the sea lies behind the beach, up to the horizon.
        new(
            Sky: [0xFF3A84D8, 0xFF64A9EC, 0xFF9ACDF4, 0xFFD0ECFB],
            Far: null,
            Mid: null,
            Soil: new(0xFFF5DC9C, 0xFFE8C980, 0xFFD9B46C, 0xFFC59C56),
            Plant: Palm,
            Spacing: (38, 90),
            Life: Life.Clouds | Life.Birds,
            Cloud: 0xFFFFFFFF,
            CloudShade: 0xFFD6E8F5,
            Birds: [0xFFFFFFFF],
            Sea: [0xFF8FCBEE, 0xFF4A9FD8, 0xFF2F86C6, 0xFF2477BA],
            Chrome: 0xFF17507F),
    ];

    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Random random = new();
    private readonly List<Mote> motes = [];
    private readonly List<bool[,]> cloudShapes = [];
    private List<(int X, int Y)> canopy = [];

    private Biome biome = Biomes[0];
    private int lastScene = -1;
    private int seed;
    private bool active;
    private int tick;

    private int cols;
    private int rows;
    private int horizon;
    private int sunX;
    private uint[] sky = [];
    private uint[] land = [];
    private uint[] frame = [];
    private int[] groundTop = [];
    private WriteableBitmap? bitmap;

    private delegate void Plant(Painter painter, int x, int ground);

    [Flags]
    private enum Life
    {
        None = 0,
        Clouds = 1,
        Birds = 2,
        Leaves = 4,
        Snow = 8,
        Fireflies = 16,
    }

    private enum Kind { Cloud, Bird, Leaf, Flake, Firefly }

    public PixelScenery()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        // Hidden rather than Collapsed, like the kitten: the size is already known the first time it turns on.
        Visibility = Visibility.Hidden;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Children.Add(image);

        timer.Tick += (_, _) => Step();
        SizeChanged += (_, _) =>
        {
            if (active)
            {
                Build();
            }
        };

        // Window hidden in the notification area: no frames that nobody is watching.
        IsVisibleChanged += (_, _) =>
        {
            if (active && IsVisible)
            {
                timer.Start();
            }
            else
            {
                timer.Stop();
            }
        };
    }

    public bool IsActive => active;

    /// <summary>
    /// Dark title bar colour matching the active scene; null with the gradient.
    /// </summary>
    public Color? ChromeColor { get; private set; }

    /// <summary>The active scene is dark (average brightness below the threshold): the cards switch to night mode.</summary>
    public bool IsDark { get; private set; }

    public event Action? AppearanceChanged;

    /// <summary>
    /// The scenes in order: first the Biomes landscapes, then the power station and the seafront.
    /// The key is the one saved in the settings (AppSettings.Sceneries).
    /// </summary>
    public static IReadOnlyList<SceneInfo> Scenes { get; } =
    [
        new("neve", "Snow"),
        new("giungla", "Jungle"),
        new("oceano", "Ocean"),
        new("centrale", "Power station"),
        new("lungomare", "Seafront"),
    ];

    /// <summary>The keys of the scenes Toggle picks from; with no valid key, all of them apply.</summary>
    public IReadOnlyCollection<string> Rotation { get; private set; } = [.. Scenes.Select(s => s.Key)];

    public void Toggle()
    {
        if (active)
        {
            active = false;
            timer.Stop();
            Visibility = Visibility.Hidden;
            image.Source = null;
            bitmap = null;
            motes.Clear();
            IsDark = false;
            SetChrome(null);
            return;
        }

        active = true;
        Visibility = Visibility.Visible;
        ShowNext();
        timer.Start();
    }

    /// <summary>Changes the scenes in rotation: if the active one has left it, switches to another right away.</summary>
    public void SetRotation(IReadOnlyCollection<string> keys)
    {
        Rotation = keys;
        if (active && lastScene >= 0 && !InRotation().Contains(lastScene))
        {
            ShowNext();
        }
    }

    // A random scene among those in rotation, different from the previous one when there is more than one.
    private void ShowNext()
    {
        var pool = InRotation();
        if (pool.Count > 1)
        {
            pool.Remove(lastScene);
        }

        Select(pool[random.Next(pool.Count)]);
        seed = random.Next();
        Build();
        IsDark = frame.Length > 0 && MeanLuma(frame) < DarkLuma;
        SetChrome(venue ? VenueChrome : bay ? BayChrome : biome.Chrome);
    }

    private List<int> InRotation()
    {
        var pool = Enumerable.Range(0, Scenes.Count).Where(i => Rotation.Contains(Scenes[i].Key)).ToList();
        return pool.Count > 0 ? pool : [.. Enumerable.Range(0, Scenes.Count)];
    }

    private void Select(int index)
    {
        lastScene = index;
        venue = index == Biomes.Length;
        bay = index == Biomes.Length + 1;
        if (!venue && !bay)
        {
            biome = Biomes[index];
        }
    }

    /// <summary>
    /// The scene's first frame on a grid of cols x rows landscape pixels, for the setup previews:
    /// fixed seed, so the preview does not change every time it opens.
    /// </summary>
    public static BitmapSource RenderPreview(int index, int cols, int rows)
    {
        var scenery = new PixelScenery();
        scenery.Select(index);
        scenery.seed = 7919 * (index + 1);
        scenery.Build(cols, rows);
        var preview = BitmapSource.Create(scenery.cols, scenery.rows, 96, 96, PixelFormats.Bgra32, null, scenery.frame, scenery.cols * 4);
        preview.Freeze();
        return preview;
    }

    // Average luma (0-1) of the first frame: below this threshold the cards' dark daytime text
    // would sit on a dark background.
    private const double DarkLuma = 0.3;

    private static double MeanLuma(uint[] pixels)
    {
        double sum = 0;
        foreach (var p in pixels)
        {
            sum += (0.2126 * ((p >> 16) & 0xFF)) + (0.7152 * ((p >> 8) & 0xFF)) + (0.0722 * (p & 0xFF));
        }

        return sum / pixels.Length / 255;
    }

    private void SetChrome(uint? argb)
    {
        ChromeColor = argb is { } c
            ? Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)
            : null;
        AppearanceChanged?.Invoke();
    }

    // The landscape depends only on the seed: resizing the window redraws the same landscape on
    // the new grid.
    // Rounded up: the image overflows by a few DIP at the top, where the Canvas clips it. Rounding
    // down left a strip of the gradient showing under the title bar.
    private void Build() => Build((int)Math.Ceiling(ActualWidth / PixelSize), (int)Math.Ceiling(ActualHeight / PixelSize));

    private void Build(int newCols, int newRows)
    {
        // Below 160 DIP per side there is no room for trees and caves: the gradient stays.
        if (newCols < 40 || newRows < 40)
        {
            image.Source = null;
            bitmap = null;
            return;
        }

        cols = newCols;
        rows = newRows;
        sky = new uint[cols * rows];
        land = new uint[cols * rows];
        frame = new uint[cols * rows];

        if (venue)
        {
            BuildVenue();
        }
        else if (bay)
        {
            BuildBay();
        }
        else
        {
            BuildLandscape();
        }

        bitmap = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgra32, null);
        image.Source = bitmap;
        image.Width = cols * PixelSize;
        image.Height = rows * PixelSize;
        SetLeft(image, 0);
        SetTop(image, Math.Floor(ActualHeight - image.Height));
        Render();
    }

    private void BuildLandscape()
    {
        var r = new Random(seed);
        var painter = new Painter(sky, land, cols, rows, r);

        var groundBase = rows - GroundRows;
        groundTop = Ridge(r, cols, groundBase - 1, 1.3, 60, false);
        for (var x = 0; x < cols; x++)
        {
            groundTop[x] = Math.Clamp(groundTop[x], groundBase - 2, groundBase);
        }

        horizon = biome.Sea is null ? rows : (int)(rows * SeaLevel);
        sunX = (int)(cols * (0.55 + (r.NextDouble() * 0.3)));

        PaintSky(biome.Sky);
        PaintSun(painter);

        foreach (var layer in new[] { biome.Far, biome.Mid })
        {
            if (layer is not null)
            {
                PaintLayer(layer, Ridge(r, cols, rows * layer.Base, rows * layer.Amplitude, layer.Wavelength, layer.Peaks));
            }
        }

        PaintSoil(painter, r);

        for (var x = r.Next(2, biome.Spacing.Max); x < cols - 1; x += r.Next(biome.Spacing.Min, biome.Spacing.Max + 1))
        {
            biome.Plant(painter, x, groundTop[x]);
        }

        canopy = painter.Canopy;
        Spawn(r);
    }

    private void PaintSky(uint[] stops)
    {
        const int bands = 14;
        for (var y = 0; y < rows; y++)
        {
            // In bands, with a checkerboard row between one band and the next, like Terraria skies.
            var level = (double)y / rows * bands;
            var band = (int)level;
            var seam = level - band > 0.82;
            var upper = Gradient(stops, (double)band / bands);
            var lower = Gradient(stops, (double)(band + 1) / bands);
            for (var x = 0; x < cols; x++)
            {
                sky[(y * cols) + x] = seam && ((x + y) & 1) == 0 ? lower : upper;
            }
        }
    }

    private void PaintSun(Painter painter)
    {
        const int radius = 9;
        var y = rows * 0.7;
        painter.Glow(sunX, y, radius * 3.2, 0xFFFFF4C4, 0.55);
        painter.Disk(sunX, y, radius, 0xFFFFF6B8, 0xFFFFD24A);
    }

    private void PaintLayer(Layer layer, int[] top)
    {
        var capLine = rows * (layer.Base - (layer.Amplitude * 0.35));
        for (var x = 0; x < cols; x++)
        {
            for (var y = Math.Max(0, top[x]); y < rows; y++)
            {
                var color = y == top[x] ? layer.Rim : layer.Body;
                if (layer.Cap != 0 && top[x] < capLine && y < top[x] + 4 - (x % 2))
                {
                    color = layer.Cap;
                }

                land[(y * cols) + x] = color;
            }
        }
    }

    private void PaintSoil(Painter painter, Random r)
    {
        var soil = biome.Soil;
        for (var x = 0; x < cols; x++)
        {
            var top = groundTop[x];
            for (var y = top; y < rows; y++)
            {
                var color = y == top ? soil.Top : y == top + 1 ? soil.Grass : r.Next(8) == 0 ? soil.Speck : soil.Dirt;
                painter.Put(x, y, color);
            }

            if (biome.Flowers is { } flowers && r.Next(14) == 0)
            {
                painter.Put(x, top - 1, soil.Grass);
                painter.Put(x, top - 2, flowers[r.Next(flowers.Length)]);
            }
            else if (r.Next(3) == 0)
            {
                painter.Put(x, top - 1, soil.Grass);
                if (r.Next(3) == 0)
                {
                    painter.Put(x, top - 2, soil.Grass);
                }
            }
        }
    }

    private void Spawn(Random r)
    {
        motes.Clear();
        cloudShapes.Clear();
        var life = biome.Life;

        if (life.HasFlag(Life.Clouds))
        {
            for (var i = 0; i < 6; i++)
            {
                cloudShapes.Add(CloudShape(r));
            }

            for (var i = 0; i < Math.Max(2, cols / 90); i++)
            {
                motes.Add(new Mote
                {
                    Kind = Kind.Cloud,
                    X = r.NextDouble() * cols,
                    Y = rows * (0.45 + (r.NextDouble() * 0.25)),
                    Vx = 0.04 + (r.NextDouble() * 0.1),
                    Shape = r.Next(cloudShapes.Count),
                });
            }
        }

        if (life.HasFlag(Life.Birds))
        {
            for (var i = 0; i < Math.Clamp(cols / 150, 1, 4); i++)
            {
                var bird = new Mote { Kind = Kind.Bird };
                RespawnBird(bird);
                bird.X = r.NextDouble() * cols;
                motes.Add(bird);
            }
        }

        if (life.HasFlag(Life.Leaves) && canopy.Count > 0)
        {
            for (var i = 0; i < Math.Min(24, cols / 16); i++)
            {
                var leaf = new Mote { Kind = Kind.Leaf };
                RespawnLeaf(leaf);
                leaf.Y += r.NextDouble() * Math.Max(0, GroundAt(leaf.X) - leaf.Y);
                motes.Add(leaf);
            }
        }

        if (life.HasFlag(Life.Snow))
        {
            for (var i = 0; i < cols * rows / 200; i++)
            {
                var size = r.Next(10);
                motes.Add(new Mote
                {
                    Kind = Kind.Flake,
                    X = r.NextDouble() * cols,
                    Y = r.NextDouble() * rows,
                    Vx = 0.03 + (r.NextDouble() * 0.05),
                    Vy = 0.15 + (r.NextDouble() * 0.3),
                    Phase = r.NextDouble() * Math.Tau,
                    Shape = size == 0 ? 2 : size < 4 ? 1 : 0,
                });
            }
        }

        if (life.HasFlag(Life.Fireflies))
        {
            for (var i = 0; i < Math.Max(3, cols / biome.FireflyEvery); i++)
            {
                var x = r.NextDouble() * cols;
                motes.Add(new Mote { Kind = Kind.Firefly, X = x, Y = GroundAt(x) - 2 - r.Next(FireflyHeight), Phase = r.NextDouble() * Math.Tau });
            }
        }
    }

    private void Step()
    {
        if (bitmap is null)
        {
            return;
        }

        tick++;
        if (venue)
        {
            StepVenue();
        }
        else if (bay)
        {
            StepBay();
        }
        else
        {
            foreach (var mote in motes)
            {
                Move(mote);
            }
        }

        Render();
    }

    private void Move(Mote m)
    {
        switch (m.Kind)
        {
            case Kind.Cloud:
                m.X += m.Vx;
                if (m.X > cols)
                {
                    m.X = -cloudShapes[m.Shape].GetLength(1);
                }

                break;

            case Kind.Bird:
                m.X += m.Vx;
                if ((m.Vx > 0 && m.X > cols + 10) || (m.Vx < 0 && m.X < -10))
                {
                    RespawnBird(m);
                }

                break;

            case Kind.Leaf:
                m.Y += m.Vy;
                m.X += m.Vx + (Math.Sin((tick * 0.12) + m.Phase) * 0.22);
                if (m.Y >= GroundAt(m.X) || m.X < 0 || m.X >= cols)
                {
                    RespawnLeaf(m);
                }

                break;

            case Kind.Flake:
                m.Y += m.Vy;
                m.X += m.Vx + (Math.Sin((tick * 0.05) + m.Phase) * 0.12);
                if (m.Y >= rows)
                {
                    m.Y = -1;
                    m.X = random.NextDouble() * cols;
                }

                m.X = (m.X + cols) % cols;
                break;

            case Kind.Firefly:
                m.Vx = Math.Clamp(m.Vx + ((random.NextDouble() - 0.5) * 0.06), -0.25, 0.25);
                m.Vy = Math.Clamp(m.Vy + ((random.NextDouble() - 0.5) * 0.04), -0.15, 0.15);
                m.X += m.Vx;
                m.Y += m.Vy;
                if (m.X < 0 || m.X >= cols)
                {
                    m.Vx = -m.Vx;
                    m.X = Math.Clamp(m.X, 0, cols - 1);
                }

                var ground = GroundAt(m.X);
                if (m.Y > ground - 2)
                {
                    m.Y = ground - 2;
                    m.Vy = -Math.Abs(m.Vy);
                }
                else if (m.Y < ground - FireflyHeight)
                {
                    m.Y = ground - FireflyHeight;
                    m.Vy = Math.Abs(m.Vy);
                }

                break;
        }
    }

    private void RespawnBird(Mote m)
    {
        var toRight = random.Next(2) == 0;
        m.Vx = (0.25 + (random.NextDouble() * 0.35)) * (toRight ? 1 : -1);
        m.X = toRight ? -10 - random.Next(80) : cols + 10 + random.Next(80);
        m.Y = rows * (0.6 + (random.NextDouble() * 0.14));
        m.Phase = random.Next(100);
        m.Color = biome.Birds![random.Next(biome.Birds.Length)];
    }

    // A leaf starts from a point of the canopy and takes its colour: until it breaks away from
    // the canopy it cannot be told apart.
    private void RespawnLeaf(Mote m)
    {
        var (x, y) = canopy[random.Next(canopy.Count)];
        m.X = x + 0.5;
        m.Y = y;
        m.Vx = (random.NextDouble() - 0.3) * 0.12;
        m.Vy = 0.08 + (random.NextDouble() * 0.12);
        m.Phase = random.NextDouble() * Math.Tau;
        m.Color = land[(y * cols) + x];
    }

    private void Render()
    {
        if (bitmap is null)
        {
            return;
        }

        if (venue)
        {
            RenderVenue();
        }
        else if (bay)
        {
            RenderBay();
        }
        else
        {
            RenderLandscape();
        }

        bitmap.WritePixels(new Int32Rect(0, 0, cols, rows), frame, cols * 4, 0);
    }

    // Background, then what lives behind the hills (clouds, birds, sea), then hills, ground and
    // plants, then what lives in front (leaves, snow, fireflies).
    private void RenderLandscape()
    {
        Array.Copy(sky, frame, frame.Length);
        if (biome.Sea is not null)
        {
            DrawSea(biome.Sea);
        }

        foreach (var mote in motes)
        {
            if (IsBehind(mote.Kind))
            {
                Draw(mote);
            }
        }

        for (var i = 0; i < frame.Length; i++)
        {
            var pixel = land[i];
            if (pixel != 0)
            {
                frame[i] = pixel;
            }
        }

        foreach (var mote in motes)
        {
            if (!IsBehind(mote.Kind))
            {
                Draw(mote);
            }
        }
    }

    private static bool IsBehind(Kind kind) => kind is Kind.Cloud or Kind.Bird;

    private void DrawSea(uint[] colors)
    {
        var depthRows = Math.Max(1, rows - horizon);
        for (var y = horizon; y < rows; y++)
        {
            var depth = (double)(y - horizon) / depthRows;
            var water = Gradient(colors, depth);

            // Near waves move faster than far ones: parallax.
            var speed = 0.04 + (depth * 0.1);
            for (var x = 0; x < cols; x++)
            {
                var i = (y * cols) + x;
                if (land[i] != 0)
                {
                    continue;
                }

                var color = water;
                var glint = Math.Sin((x * 0.15) + (tick * speed) + (y * 1.3)) * Math.Sin((x * 0.033) - (tick * speed * 0.4) + (y * 0.5));
                if (glint > 0.72)
                {
                    color = Mix(color, 0xFFFFFFFF, 0.35 + (depth * 0.2));
                }
                else if (Math.Abs(x - sunX) < 5 + (depth * 9) && glint > 0.2)
                {
                    color = Mix(color, 0xFFFFF4C0, 0.5);
                }

                frame[i] = color;
            }
        }

        for (var x = 0; x < cols; x++)
        {
            if (((x + (tick / 4)) % 12) < 6)
            {
                Tint(x, groundTop[x] - 1, 0xFFFFFFFF, 0.8);
            }
        }
    }

    private void Draw(Mote m)
    {
        var x = (int)Math.Floor(m.X);
        var y = (int)Math.Floor(m.Y);
        switch (m.Kind)
        {
            case Kind.Cloud:
                var shape = cloudShapes[m.Shape];
                var height = shape.GetLength(0);
                for (var sy = 0; sy < height; sy++)
                {
                    for (var sx = 0; sx < shape.GetLength(1); sx++)
                    {
                        if (shape[sy, sx])
                        {
                            Plot(x + sx, y + sy, sy >= height - 3 ? biome.CloudShade : biome.Cloud);
                        }
                    }
                }

                break;

            case Kind.Bird:
                var wingsUp = ((tick / 3) + (int)m.Phase) % 2 == 0;
                var bob = (int)Math.Round(Math.Sin((tick * 0.08) + m.Phase) * 1.5);
                DrawSprite(wingsUp ? BirdUp : BirdDown, x - 4, y - 2 + bob, m.Color);
                break;

            case Kind.Leaf:
                Plot(x, y, m.Color);
                Plot(x + 1, y, m.Color);
                if (((tick / 4) + (int)(m.Phase * 10)) % 2 == 0)
                {
                    Plot(x, y + 1, m.Color);
                }

                break;

            case Kind.Flake:
                Plot(x, y, 0xFFFFFFFF);
                if (m.Shape == 1)
                {
                    Plot(x + 1, y, 0xFFFFFFFF);
                    Plot(x, y + 1, 0xFFFFFFFF);
                    Plot(x + 1, y + 1, 0xFFE8F2FA);
                }
                else if (m.Shape == 2)
                {
                    Cross(x, y, 0xFFE8F2FA, 0.8);
                }

                break;

            case Kind.Firefly:
                var glow = (Math.Sin((tick * 0.15) + m.Phase) + 1) / 2;
                if (glow > 0.15)
                {
                    Tint(x, y, 0xFFF4FF9A, glow);
                    Cross(x, y, 0xFFB6E35A, glow * 0.5);
                    Tint(x - 1, y - 1, 0xFFB6E35A, glow * 0.2);
                    Tint(x + 1, y - 1, 0xFFB6E35A, glow * 0.2);
                    Tint(x - 1, y + 1, 0xFFB6E35A, glow * 0.2);
                    Tint(x + 1, y + 1, 0xFFB6E35A, glow * 0.2);
                }

                break;
        }
    }

    private void DrawSprite(string[] sprite, int left, int top, uint color)
    {
        for (var sy = 0; sy < sprite.Length; sy++)
        {
            for (var sx = 0; sx < sprite[sy].Length; sx++)
            {
                if (sprite[sy][sx] == 'X')
                {
                    Plot(left + sx, top + sy, color);
                }
            }
        }
    }

    private void Cross(int x, int y, uint color, double amount)
    {
        Tint(x - 1, y, color, amount);
        Tint(x + 1, y, color, amount);
        Tint(x, y - 1, color, amount);
        Tint(x, y + 1, color, amount);
    }

    private void Plot(int x, int y, uint color)
    {
        if (Inside(x, y))
        {
            frame[(y * cols) + x] = color;
        }
    }

    private void Tint(int x, int y, uint color, double amount)
    {
        if (Inside(x, y))
        {
            var i = (y * cols) + x;
            frame[i] = Mix(frame[i], color, amount);
        }
    }

    private bool Inside(int x, int y) => (uint)x < (uint)cols && (uint)y < (uint)rows;

    private int GroundAt(double x) => groundTop[Math.Clamp((int)x, 0, cols - 1)];

    // Hill or mountain profile: the sum of three sine waves with random phases, in two-column
    // steps because in Terraria the ground rises and falls in blocks.
    private static int[] Ridge(Random r, int cols, double baseY, double amplitude, double wavelength, bool peaks)
    {
        var phase1 = r.NextDouble() * Math.Tau;
        var phase2 = r.NextDouble() * Math.Tau;
        var phase3 = r.NextDouble() * Math.Tau;
        var frequency = Math.Tau / wavelength;
        var top = new int[cols];
        for (var x = 0; x < cols; x++)
        {
            var bx = x - (x % 2);
            var main = peaks ? 1 - (2 * Math.Abs(Math.Sin((bx * frequency) + phase1))) : Math.Sin((bx * frequency) + phase1);
            var value = (0.65 * main) + (0.25 * Math.Sin((bx * frequency * 2.3) + phase2)) + (0.1 * Math.Sin((bx * frequency * 5.7) + phase3));
            top[x] = (int)Math.Round(baseY - (amplitude * value));
        }

        return top;
    }

    // Round puffs in a row, bigger in the middle, on a flat base. The maximum radius keeps the
    // central puff inside the shape: clipped by the edge, the cloud would turn into a rectangle.
    private static bool[,] CloudShape(Random r)
    {
        var width = r.Next(30, 65);
        var height = r.Next(12, 21);
        var shape = new bool[height, width];
        var puffs = r.Next(4, 8);
        var maxRadius = (height - 1) / 1.9;
        for (var i = 0; i < puffs; i++)
        {
            var cx = 4 + ((width - 8) * (i + 0.5) / puffs) + r.Next(-2, 3);
            var radius = maxRadius * (0.5 + (0.5 * Math.Sin(Math.PI * (i + 0.5) / puffs))) * (0.85 + (r.NextDouble() * 0.15));
            var cy = height - 1 - (radius * 0.9);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var dx = x + 0.5 - cx;
                    var dy = y + 0.5 - cy;
                    if ((dx * dx) + (dy * dy) <= radius * radius)
                    {
                        shape[y, x] = true;
                    }
                }
            }
        }

        for (var x = 3; x < width - 3; x++)
        {
            shape[height - 1, x] = true;
            shape[height - 2, x] = true;
        }

        return shape;
    }

    private static uint Gradient(uint[] stops, double t)
    {
        var position = Math.Clamp(t, 0, 1) * (stops.Length - 1);
        var index = Math.Min((int)position, stops.Length - 2);
        return Mix(stops[index], stops[index + 1], position - index);
    }

    private static uint Mix(uint from, uint to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        uint Channel(int shift) => (uint)Math.Round((((from >> shift) & 0xFF) * (1 - t)) + (((to >> shift) & 0xFF) * t)) << shift;
        return 0xFF000000 | Channel(16) | Channel(8) | Channel(0);
    }

    private static void Bush(Painter p, int x, int ground, TreeColors c)
    {
        var rx = p.Random.Next(4, 8);
        var ry = p.Random.Next(3, 5);
        Blob(p, x, ground - ry + 0.5, rx, ry, c);
    }

    private static void Blob(Painter p, double cx, double cy, double rx, double ry, TreeColors c)
    {
        for (var y = (int)(cy - ry) - 1; y <= cy + ry + 1; y++)
        {
            for (var x = (int)(cx - rx) - 1; x <= cx + rx + 1; x++)
            {
                var dx = (x + 0.5 - cx) / rx;
                var dy = (y + 0.5 - cy) / ry;
                var distance = (dx * dx) + (dy * dy);
                if (distance > 1 || (distance > 0.72 && p.Random.Next(3) == 0))
                {
                    continue;
                }

                var color = dx + dy < -0.55 ? c.Light : dx + dy > 0.6 ? c.Dark : c.Leaf;
                if (color == c.Leaf && p.Random.Next(10) == 0)
                {
                    color = c.Dark;
                }

                p.Put(x, y, color);
                if (distance < 0.6 && p.Random.Next(12) == 0 && p.Contains(x, y))
                {
                    p.Canopy.Add((x, y));
                }
            }
        }
    }

    private static void Pine(Painter p, int x, int ground)
    {
        const uint needle = 0xFF2F5E50;
        const uint shade = 0xFF234A3F;
        const uint snow = 0xFFF4F9FD;
        const uint bark = 0xFF5A4030;
        const uint barkDark = 0xFF44301F;
        const int tier = 6;

        var r = p.Random;
        var height = r.Next(26, 51);
        var top = ground - height;
        var trunk = ground - 4;
        for (var y = trunk; y < ground; y++)
        {
            p.Put(x - 1, y, bark);
            p.Put(x, y, bark);
            p.Put(x + 1, y, barkDark);
        }

        // In tiers of six rows: each tier widens and carries snow on its top row and on its tips.
        var maxHalf = 4 + (height / 5);
        for (var y = top; y < trunk; y++)
        {
            var i = y - top;
            var inTier = i % tier;
            var half = Math.Min(maxHalf, (int)(i / tier * 1.6) + inTier);
            for (var dx = -half; dx <= half; dx++)
            {
                var color = inTier == 0 || (inTier <= 1 && Math.Abs(dx) >= half - 1) ? snow : dx > 0 ? shade : needle;
                if (color == needle && r.Next(7) == 0)
                {
                    color = shade;
                }

                p.Put(x + dx, y, color);
            }
        }
    }

    private static void JungleTree(Painter p, int x, int ground)
    {
        const uint vine = 0xFF3E9C3A;

        var r = p.Random;
        var c = JungleWood;
        if (r.Next(3) == 0)
        {
            Bush(p, x, ground, c);
            return;
        }

        var height = r.Next(32, 59);
        var crown = ground - height;
        for (var y = crown; y < ground; y++)
        {
            p.Put(x - 2, y, c.Bark);
            p.Put(x - 1, y, c.Bark);
            p.Put(x, y, (y - crown) % 5 == 0 ? c.BarkDark : c.Bark);
            p.Put(x + 1, y, c.BarkDark);
            p.Put(x + 2, y, c.BarkDark);
        }

        for (var k = 1; k <= 2; k++)
        {
            p.Put(x - 3, ground - k, c.Bark);
            p.Put(x + 3, ground - k, c.BarkDark);
        }

        p.Put(x - 4, ground - 1, c.Bark);
        p.Put(x + 4, ground - 1, c.BarkDark);

        var rx = r.Next(15, 23);
        var ry = r.Next(6, 10);
        Blob(p, x - (rx * 0.55), crown + 2, rx * 0.55, ry * 0.8, c);
        Blob(p, x + (rx * 0.55), crown + 2, rx * 0.55, ry * 0.8, c);
        Blob(p, x + 0.5, crown, rx, ry, c);

        var vines = r.Next(5, 11);
        for (var i = 0; i < vines; i++)
        {
            var vineX = x + r.Next(-rx + 3, rx - 2);
            var length = r.Next(6, 23);
            for (var k = 0; k < length; k++)
            {
                p.Put(vineX, crown + ry - 1 + k, vine);
                if (k % 3 == 2)
                {
                    p.Put(vineX + (k % 2 == 0 ? 1 : -1), crown + ry - 1 + k, c.Light);
                }
            }
        }
    }

    private static void Palm(Painter p, int x, int ground)
    {
        const uint trunk = 0xFFA4763F;
        const uint trunkDark = 0xFF8A6334;
        const uint ring = 0xFF805C2E;
        const uint frond = 0xFF3FA843;
        const uint frondLight = 0xFF6AC95A;
        const uint frondDark = 0xFF2D8436;
        const uint coconut = 0xFF6B4A2B;
        const uint coconutLight = 0xFF8A6440;

        var r = p.Random;
        var height = r.Next(26, 45);
        var lean = r.Next(2) == 0 ? -1 : 1;
        var topX = x;
        for (var i = 0; i < height; i++)
        {
            var t = (double)i / height;
            topX = x + (int)Math.Round(lean * 9 * t * t);
            var y = ground - 1 - i;
            var isRing = i % 4 == 0;
            p.Put(topX - 1, y, isRing ? ring : trunk);
            p.Put(topX, y, isRing ? ring : trunk);
            p.Put(topX + 1, y, isRing ? ring : trunkDark);
        }

        // Fronds that start at the top and droop: a downward parabola along each direction.
        var topY = ground - height;
        foreach (var degrees in new[] { -175.0, -150, -125, -100, -80, -55, -30, -5 })
        {
            var angle = degrees * Math.PI / 180;
            var length = r.Next(11, 18);
            for (var s = 1; s <= length; s++)
            {
                var fx = (int)Math.Round(topX + 0.5 + (Math.Cos(angle) * s));
                var fy = (int)Math.Round(topY + (Math.Sin(angle) * s) + (0.06 * s * s));
                p.Put(fx, fy, frondLight);
                p.Put(fx, fy + 1, frond);
                if (s > 2 && s < length - 1 && s % 2 == 0)
                {
                    p.Put(fx, fy + 2, frondDark);
                }
            }
        }

        foreach (var (dx, dy) in new[] { (-2, 1), (1, 1), (-1, 3) })
        {
            p.Put(topX + dx, topY + dy, coconutLight);
            p.Put(topX + dx + 1, topY + dy, coconut);
            p.Put(topX + dx, topY + dy + 1, coconut);
            p.Put(topX + dx + 1, topY + dy + 1, coconut);
        }
    }

    private sealed record Biome(
        uint[] Sky,
        Layer? Far,
        Layer? Mid,
        Soil Soil,
        Plant Plant,
        (int Min, int Max) Spacing,
        Life Life,
        uint Cloud = 0,
        uint CloudShade = 0,
        uint[]? Birds = null,
        uint[]? Flowers = null,
        uint[]? Sea = null,
        int FireflyEvery = 1,
        uint Chrome = 0);

    private sealed record Layer(uint Body, uint Rim, double Base, double Amplitude, double Wavelength, bool Peaks = false, uint Cap = 0);

    private sealed record Soil(uint Top, uint Grass, uint Dirt, uint Speck);

    private sealed record TreeColors(uint Bark, uint BarkDark, uint Leaf, uint Light, uint Dark);

    private sealed class Mote
    {
        public Kind Kind;
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Phase;
        public uint Color;
        public int Shape;
    }

    // Draws into the landscape's two fixed layers: the sky (the sun's halo) and the
    // land (hills, ground, plants), where 0 means transparent.
    private sealed class Painter(uint[] sky, uint[] land, int cols, int rows, Random random)
    {
        public Random Random => random;

        public List<(int X, int Y)> Canopy { get; } = [];

        public bool Contains(int x, int y) => (uint)x < (uint)cols && (uint)y < (uint)rows;

        public void Put(int x, int y, uint color)
        {
            if (Contains(x, y))
            {
                land[(y * cols) + x] = color;
            }
        }

        public void PutSky(int x, int y, uint color)
        {
            if (Contains(x, y))
            {
                sky[(y * cols) + x] = color;
            }
        }

        public void Glow(double cx, double cy, double radius, uint color, double strength)
        {
            for (var y = (int)(cy - radius); y <= cy + radius; y++)
            {
                for (var x = (int)(cx - radius); x <= cx + radius; x++)
                {
                    var distance = Math.Sqrt(Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2));
                    if (distance < radius && Contains(x, y))
                    {
                        var i = (y * cols) + x;
                        sky[i] = Mix(sky[i], color, strength * Math.Pow(1 - (distance / radius), 2));
                    }
                }
            }
        }

        public void Disk(double cx, double cy, double radius, uint inner, uint rim)
        {
            for (var y = (int)(cy - radius) - 1; y <= cy + radius + 1; y++)
            {
                for (var x = (int)(cx - radius) - 1; x <= cx + radius + 1; x++)
                {
                    var distance = Math.Sqrt(Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2));
                    if (distance <= radius)
                    {
                        PutSky(x, y, distance < radius - 1.5 ? inner : rim);
                    }
                }
            }
        }
    }
}

public sealed record SceneInfo(string Key, string Label);
