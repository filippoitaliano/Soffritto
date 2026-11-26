namespace Soffritto.Desktop;

// A tribute to the crime sagas set on the Florida coast, by day and in light colours: a seafront
// with pastel art deco hotels, vertical neon signs, palm trees, passing convertibles and a plane
// towing a banner.
// The sky lives in sky; skyline, hotels, palms and road live in land. Clouds, gulls and the plane
// pass behind the buildings, signs and cars in front.
public sealed partial class PixelScenery
{
    private const int RoadRows = 12;
    private const int SidewalkRows = 3;
    private const int FloorRows = 7;
    private const int PorchRows = 7;
    private const uint Trim = 0xFFFFFFFF;

    // Dark plum, the seafront evening behind the pink and purple neon.
    private const uint BayChrome = 0xFF4A2350;
    private const string BannerText = "VICE COAST";

    private static readonly uint[] BaySky = [0xFF8FCBEF, 0xFFB5DDF4, 0xFFDDE7F2, 0xFFF9DADF, 0xFFFFE3C8];
    private static readonly uint[] SignColors = [0xFFFF4FA3, 0xFF1FB5CC, 0xFF9B5CF6, 0xFFFF7A3D];
    private static readonly string[] HotelNames = ["VICE", "OCEAN", "PALMS", "SUNSET", "LEONIDA", "TIDES", "CORAL", "DECO"];

    private static readonly Facade[] Facades =
    [
        new(0xFFC4EBD8, 0xFFA9D9C2, 0xFF58B79A),
        new(0xFFF9CFDB, 0xFFEDB4C4, 0xFFE0709A),
        new(0xFFFCEFB8, 0xFFF0DD95, 0xFFE9B949),
        new(0xFFDDD2F3, 0xFFC8BAE8, 0xFF9A7FD6),
        new(0xFFC6E6F7, 0xFFA9D3EC, 0xFF4FA3D1),
        new(0xFFFFDCC2, 0xFFF6C4A3, 0xFFF08A5D),
    ];

    private static readonly (uint Body, uint Shade)[] CarColors =
    [
        (0xFFFF6FA8, 0xFFD9558A),
        (0xFF3CC8D8, 0xFF2AA3B2),
        (0xFFF7F7F7, 0xFFD2D2DC),
        (0xFFFFD84D, 0xFFDDB53A),
        (0xFFA88BF0, 0xFF8569D0),
        (0xFFFF9F5A, 0xFFE07F3E),
        (0xFF7FDDB0, 0xFF5CBF92),
    ];

    private static readonly Dictionary<char, string[]> SignFont = new()
    {
        ['A'] = [".X.", "X.X", "XXX", "X.X", "X.X"],
        ['C'] = [".XX", "X..", "X..", "X..", ".XX"],
        ['D'] = ["XX.", "X.X", "X.X", "X.X", "XX."],
        ['E'] = ["XXX", "X..", "XX.", "X..", "XXX"],
        ['G'] = [".XX", "X..", "X.X", "X.X", ".XX"],
        ['I'] = ["XXX", ".X.", ".X.", ".X.", "XXX"],
        ['L'] = ["X..", "X..", "X..", "X..", "XXX"],
        ['M'] = ["X.X", "XXX", "XXX", "X.X", "X.X"],
        ['N'] = ["XX.", "X.X", "X.X", "X.X", "X.X"],
        ['O'] = [".X.", "X.X", "X.X", "X.X", ".X."],
        ['P'] = ["XX.", "X.X", "XX.", "X..", "X.."],
        ['R'] = ["XX.", "X.X", "XX.", "X.X", "X.X"],
        ['S'] = [".XX", "X..", ".X.", "..X", "XX."],
        ['T'] = ["XXX", ".X.", ".X.", ".X.", ".X."],
        ['U'] = ["X.X", "X.X", "X.X", "X.X", "XXX"],
        ['V'] = ["X.X", "X.X", "X.X", "X.X", ".X."],
        [' '] = ["...", "...", "...", "...", "..."],
    };

    // Convertible facing right: B body, b shaded side, W windscreen, D hair, K skin, S seats,
    // L headlights, R tail lights, T tyres, H rims.
    private static readonly string[] Convertible =
    [
        "...........W............",
        ".....DD....W............",
        "....SKKS...WW...........",
        "..BBBBBBBBBBBBBBBBBBBB..",
        ".RBBBBBBBBBBBBBBBBBBBBBL",
        ".bbTTTTbbbbbbbbbbTTTTbb.",
        "...THHT..........THHT...",
        "....TT............TT....",
    ];

    // Facing left: P fuselage, p wings and tail, w windows.
    private static readonly string[] Plane =
    [
        "..........p.",
        "...pp.....pp",
        ".PwPwPPPPPPP",
        "PPPPPPPPPPP.",
        "...pp.......",
    ];

    private static readonly string[] Umbrella = ["..XXX..", ".XXXXX.", "XXXXXXX", "...P...", "...P...", ".TTPTT."];

    private static readonly string[] Flamingo = ["..XX.", "..X.B", "..X..", "..X..", ".XXX.", "XXXXX", ".XXX.", "..L..", "..L..", "..L.."];

    private readonly List<Car> cars = [];

    private bool bay;
    private int roadTop;
    private double planeX;
    private int planeY;
    private int planeWait;

    private void BuildBay()
    {
        var r = new Random(seed);
        var painter = new Painter(sky, land, cols, rows, r);
        motes.Clear();
        cloudShapes.Clear();
        cars.Clear();
        neons.Clear();

        var promenade = rows - GroundRows;
        roadTop = promenade - RoadRows;
        var street = roadTop - SidewalkRows;

        PaintSky(BaySky);
        sunX = (int)(cols * (0.7 + (r.NextDouble() * 0.2)));
        painter.Glow(sunX, rows * 0.2, 30, 0xFFFFF6D8, 0.6);
        painter.Disk(sunX, rows * 0.2, 8, 0xFFFFFBE6, 0xFFFFE9A8);

        PaintSkyline(r, street);
        PaintStreet(r, street, promenade);

        var names = HotelNames.OrderBy(_ => r.Next()).ToArray();
        var hotel = 0;
        for (var x = r.Next(-12, 4); x < cols; hotel++)
        {
            var width = r.Next(38, 67);
            var height = Math.Max(14, (int)(rows * (0.2 + (r.NextDouble() * 0.16))));
            PaintHotel(r, x, width, height, street, names[hotel % names.Length]);
            var gap = r.Next(3, 15);
            if (gap >= 7 && r.Next(2) == 0)
            {
                Stamp(land, Flamingo, x + width + (gap / 2) - 2, street + 1 - Flamingo.Length, c => c switch
                {
                    'X' => 0xFFFF8FB1,
                    'B' => 0xFF3A3040,
                    'L' => 0xFFC0607E,
                    _ => 0,
                });
            }

            x += width + gap;
        }

        for (var x = r.Next(4, 30); x < cols - 1; x += r.Next(34, 70))
        {
            Palm(painter, x, street + 2);
        }

        SpawnBay(r);
    }

    private void PaintSkyline(Random r, int bottom)
    {
        for (var x = r.Next(-6, 2); x < cols;)
        {
            var width = r.Next(9, 25);
            var height = (int)(rows * (0.28 + (r.NextDouble() * 0.3)));
            var pink = r.Next(4) == 0;
            var body = pink ? 0xFFEAD7E6 : 0xFFD5DFEF;
            var side = pink ? 0xFFDCC5DA : 0xFFC4D0E5;
            var glass = pink ? 0xFFF4E8F1 : 0xFFE7EEF8;
            var top = bottom - height;
            for (var y = top; y < bottom; y++)
            {
                for (var dx = 0; dx < width; dx++)
                {
                    var color = dx >= width - 3 ? side : body;
                    if (dx % 3 == 1 && (y - top) % 4 == 2)
                    {
                        color = glass;
                    }

                    Put(land, x + dx, y, color);
                }
            }

            if (r.Next(3) == 0)
            {
                for (var k = 1; k <= 6; k++)
                {
                    Put(land, x + (width / 2), top - k, side);
                }
            }

            x += width + r.Next(0, 5);
        }
    }

    // Hotel sidewalk, a two-lane road with a dashed centre line and, above the footer, the pink
    // checkered promenade where the kitten walks.
    private void PaintStreet(Random r, int street, int promenade)
    {
        var dashes = roadTop + (RoadRows / 2);
        for (var x = 0; x < cols; x++)
        {
            Put(land, x, street, Trim);
            Put(land, x, street + 1, 0xFFF1EAE0);
            Put(land, x, street + 2, 0xFFD8D1CA);
            for (var y = roadTop; y < promenade; y++)
            {
                var color = r.Next(10) == 0 ? 0xFF9D99AA : 0xFFA9A5B5;
                if (y == dashes && x % 10 < 6)
                {
                    color = 0xFFFFF1B8;
                }

                Put(land, x, y, color);
            }

            Put(land, x, promenade, Trim);
            for (var y = promenade + 1; y < rows; y++)
            {
                Put(land, x, y, ((x / 3) + y) % 2 == 0 ? 0xFFF9D5DF : 0xFFFFF4F6);
            }
        }
    }

    private void PaintHotel(Random r, int left, int width, int height, int bottom, string name)
    {
        var f = Facades[r.Next(Facades.Length)];
        var top = bottom - height;
        var right = left + width - 1;
        var porch = bottom - PorchRows;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var color = y >= porch || x >= right - 1 ? f.Shade : f.Body;
                if (y < porch && (x == left + 1 || x == right - 2))
                {
                    color = f.Accent;
                }

                Put(land, x, y, color);
            }
        }

        // Stepped crown, the hallmark of art deco.
        var crown = Math.Max(6, width / 3);
        var spire = Math.Max(3, crown / 2);
        Block(left + ((width - crown) / 2), top - 4, crown, 4, f.Body, f.Accent);
        Block(left + ((width - spire) / 2), top - 7, spire, 3, f.Body, f.Accent);
        for (var x = left; x <= right; x++)
        {
            Put(land, x, top, Trim);
        }

        for (var y = top + 2; y + 6 < porch; y += FloorRows)
        {
            for (var x = left + 2; x <= right - 3; x++)
            {
                Put(land, x, y, Trim);
                Put(land, x, y + 1, f.Shade);
            }

            for (var wx = left + 4; wx + 2 < right - 2; wx += 6)
            {
                for (var dy = 3; dy <= 5; dy++)
                {
                    for (var dx = 0; dx < 3; dx++)
                    {
                        Put(land, wx + dx, y + dy, dy == 3 ? 0xFFBFE2F2 : 0xFF8CC3DE);
                    }
                }
            }
        }

        // Striped awning over the porch, white columns and the glass door in the middle.
        for (var x = left; x <= right; x++)
        {
            Put(land, x, porch, f.Accent);
            Put(land, x, porch + 1, x % 2 == 0 ? f.Accent : Trim);
            if ((x - left) % 6 == 2)
            {
                for (var y = porch + 2; y < bottom; y++)
                {
                    Put(land, x, y, Trim);
                }
            }
        }

        var door = left + (width / 2) - 2;
        for (var y = bottom - 5; y < bottom; y++)
        {
            for (var x = door; x < door + 4; x++)
            {
                Put(land, x, y, y == bottom - 5 ? Trim : 0xFF7FB2CC);
            }
        }

        PaintSign(r, f, left, right, top, porch, name);

        Stamp(land, Umbrella, left + 3, bottom + 1 - Umbrella.Length, (c, sx) => UmbrellaColor(c, sx, f.Accent));
        if (width > 45)
        {
            Stamp(land, Umbrella, right - 10, bottom + 1 - Umbrella.Length, (c, sx) => UmbrellaColor(c, sx, f.Accent));
        }
    }

    private static uint UmbrellaColor(char c, int sx, uint accent) => c switch
    {
        'X' => sx % 2 == 0 ? accent : Trim,
        'P' => 0xFF8A8494,
        'T' => Trim,
        _ => 0,
    };

    private void Block(int left, int top, int width, int height, uint body, uint accent)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
            {
                Put(land, x, y, y == top ? accent : body);
            }
        }
    }

    // Vertical sign on a white fin sticking out of the roof: the letters are neon, which barely glows
    // by day; the name is shortened if the hotel is low, and below three letters it disappears.
    private void PaintSign(Random r, Facade f, int left, int right, int top, int porch, string name)
    {
        var finTop = top - 4;
        var letters = Math.Min(name.Length, (porch - 3 - finTop) / 6);
        if (letters < 3)
        {
            return;
        }

        var finLeft = r.Next(2) == 0 ? left + 4 : right - 8;
        var finHeight = (letters * 6) + 1;
        for (var y = finTop; y < finTop + finHeight; y++)
        {
            for (var x = finLeft; x < finLeft + 5; x++)
            {
                var edge = x == finLeft || x == finLeft + 4 || y == finTop || y == finTop + finHeight - 1;
                Put(land, x, y, edge ? f.Accent : 0xFFFFFDF8);
            }
        }

        var color = SignColors[r.Next(SignColors.Length)];
        var neon = new Neon { Color = color, Core = color, Off = Mix(color, Trim, 0.6), Letters = letters };
        for (var i = 0; i < letters; i++)
        {
            var glyph = SignFont[name[i]];
            for (var gy = 0; gy < glyph.Length; gy++)
            {
                for (var gx = 0; gx < 3; gx++)
                {
                    var x = finLeft + 1 + gx;
                    var y = finTop + 1 + (i * 6) + gy;
                    if (glyph[gy][gx] == 'X' && Inside(x, y))
                    {
                        neon.Pixels.Add((x, y, i));
                        Put(land, x, y, neon.Off);
                    }
                }
            }
        }

        var cores = neon.Pixels.Select(p => (p.Y * cols) + p.X).ToHashSet();
        var glow = new Dictionary<int, double>();
        foreach (var (x, y, _) in neon.Pixels)
        {
            foreach (var (dx, dy, weight) in Halo3)
            {
                if (Inside(x + dx, y + dy))
                {
                    var i = ((y + dy) * cols) + x + dx;
                    if (!cores.Contains(i))
                    {
                        glow[i] = Math.Max(glow.GetValueOrDefault(i), weight * 0.3);
                    }
                }
            }
        }

        neon.Glow = [.. glow.Select(g => (g.Key, g.Value))];
        neons.Add(neon);
    }

    private void SpawnBay(Random r)
    {
        for (var i = 0; i < 6; i++)
        {
            cloudShapes.Add(CloudShape(r));
        }

        for (var i = 0; i < Math.Max(3, cols / 80); i++)
        {
            motes.Add(new Mote
            {
                Kind = Kind.Cloud,
                X = r.NextDouble() * cols,
                Y = rows * (0.05 + (r.NextDouble() * 0.3)),
                Vx = 0.03 + (r.NextDouble() * 0.05),
                Shape = r.Next(cloudShapes.Count),
            });
        }

        for (var i = 0; i < Math.Clamp(cols / 160, 2, 4); i++)
        {
            var gull = new Mote { Kind = Kind.Bird };
            RespawnGull(gull);
            gull.X = r.NextDouble() * cols;
            motes.Add(gull);
        }

        // Cars in one lane all go at the same speed: they never rear-end each other.
        foreach (var toRight in new[] { false, true })
        {
            var x = r.NextDouble() * cols;
            for (var i = 0; i < Math.Max(2, cols / 140); i++)
            {
                var car = new Car { ToRight = toRight, X = x };
                Paint(car, r);
                cars.Add(car);
                x += (toRight ? -1 : 1) * r.Next(50, 160);
            }
        }

        planeX = cols * (0.3 + (r.NextDouble() * 0.6));
        planeY = (int)(rows * (0.08 + (r.NextDouble() * 0.1)));
        planeWait = 0;
    }

    private static void Paint(Car car, Random r)
    {
        (car.Body, car.Shade) = CarColors[r.Next(CarColors.Length)];
        (car.Skin, _) = Skins[r.Next(Skins.Length)];
        car.Hair = HairColors[r.Next(HairColors.Length)];
    }

    private void RespawnGull(Mote m)
    {
        var toRight = random.Next(2) == 0;
        m.Vx = (0.2 + (random.NextDouble() * 0.3)) * (toRight ? 1 : -1);
        m.X = toRight ? -10 - random.Next(80) : cols + 10 + random.Next(80);
        m.Y = rows * (0.1 + (random.NextDouble() * 0.25));
        m.Phase = random.Next(100);
        m.Color = 0xFF8C96A8;
    }

    private void StepBay()
    {
        foreach (var m in motes)
        {
            m.X += m.Vx;
            if (m.Kind == Kind.Cloud && m.X > cols)
            {
                m.X = -cloudShapes[m.Shape].GetLength(1);
            }
            else if (m.Kind == Kind.Bird && ((m.Vx > 0 && m.X > cols + 10) || (m.Vx < 0 && m.X < -10)))
            {
                RespawnGull(m);
            }
        }

        foreach (var car in cars)
        {
            car.X += car.ToRight ? 0.6 : -0.45;
            var gone = car.ToRight ? car.X > cols : car.X < -Convertible[0].Length;
            if (gone)
            {
                var lane = cars.Where(c => c != car && c.ToRight == car.ToRight).Select(c => c.X).ToList();
                var gap = random.Next(50, 160);
                car.X = car.ToRight
                    ? Math.Min(lane.Count > 0 ? lane.Min() : 0, -Convertible[0].Length) - gap
                    : Math.Max(lane.Count > 0 ? lane.Max() : cols, cols) + gap;
                Paint(car, random);
            }
        }

        if (planeWait > 0)
        {
            planeWait--;
        }
        else
        {
            planeX -= 0.3;
            if (planeX + Plane[0].Length + 6 + BannerWidth < 0)
            {
                planeWait = random.Next(200, 600);
                planeX = cols + 5;
                planeY = (int)(rows * (0.08 + (random.NextDouble() * 0.1)));
            }
        }

        foreach (var neon in neons)
        {
            if (neon.Flicker > 0)
            {
                neon.Flicker--;
            }
            else if (random.Next(400) == 0)
            {
                neon.Flicker = random.Next(4, 12);
                neon.Letter = random.Next(neon.Letters);
            }
        }
    }

    private void RenderBay()
    {
        Array.Copy(sky, frame, frame.Length);
        foreach (var m in motes)
        {
            if (m.Kind == Kind.Cloud)
            {
                DrawBayCloud(m);
            }
            else
            {
                Draw(m);
            }
        }

        DrawPlane();
        Overlay(land);
        DrawNeons();

        // Far lane first, then the near one.
        foreach (var car in cars.OrderBy(c => c.ToRight))
        {
            DrawCar(car);
        }
    }

    private void DrawBayCloud(Mote m)
    {
        var shape = cloudShapes[m.Shape];
        var height = shape.GetLength(0);
        for (var sy = 0; sy < height; sy++)
        {
            for (var sx = 0; sx < shape.GetLength(1); sx++)
            {
                if (shape[sy, sx])
                {
                    Plot((int)m.X + sx, (int)m.Y + sy, sy >= height - 3 ? 0xFFF6E1EA : Trim);
                }
            }
        }
    }

    private static int BannerWidth => (BannerText.Length * 4) + 3;

    // The plane tows the banner on a rope; the banner ripples column by column.
    private void DrawPlane()
    {
        if (planeWait > 0)
        {
            return;
        }

        var x = (int)planeX;
        Stamp(null, Plane, x, planeY, c => c switch
        {
            'P' => 0xFFFF6FA8,
            'p' => 0xFFD9558A,
            'w' => 0xFFCFEAF7,
            _ => 0,
        });

        var ropeEnd = x + Plane[0].Length + 6;
        for (var rx = x + Plane[0].Length; rx < ropeEnd; rx++)
        {
            Plot(rx, planeY + 2, 0xFF8C8C99);
        }

        const int bannerHeight = 9;
        for (var bx = 0; bx < BannerWidth; bx++)
        {
            var wave = (int)Math.Round(Math.Sin((tick * 0.2) + (bx * 0.35)) * 0.8);
            for (var by = 0; by < bannerHeight; by++)
            {
                var edge = bx == 0 || bx == BannerWidth - 1 || by == 0 || by == bannerHeight - 1;
                var color = edge ? 0xFFF4B8CD : Trim;
                var letter = (bx - 2) / 4;
                var gx = (bx - 2) % 4;
                if (!edge && bx >= 2 && letter < BannerText.Length && gx < 3 && by >= 2 && by < 7 && SignFont[BannerText[letter]][by - 2][gx] == 'X')
                {
                    color = 0xFFE0457F;
                }

                Plot(ropeEnd + bx, planeY - 2 + by + wave, color);
            }
        }
    }

    private void DrawCar(Car car)
    {
        var top = car.ToRight ? rows - GroundRows - Convertible.Length : roadTop + (RoadRows / 2) - Convertible.Length;
        Stamp(null, Convertible, (int)car.X, top, c => c switch
        {
            'B' => car.Body,
            'b' => car.Shade,
            'W' => 0xFFCFEAF7,
            'D' => car.Hair,
            'K' => car.Skin,
            'S' => 0xFFF5E6CC,
            'L' => 0xFFFFF6C8,
            'R' => 0xFFFF5A5A,
            'T' => 0xFF3A3640,
            'H' => 0xFFC9C9D2,
            _ => 0,
        }, mirror: !car.ToRight);
    }

    private void Stamp(uint[]? layer, string[] sprite, int left, int top, Func<char, uint> color, bool mirror = false) =>
        Stamp(layer, sprite, left, top, (c, _) => color(c), mirror);

    // Without a layer it draws on the frame; colour 0 lets what is underneath show through.
    private void Stamp(uint[]? layer, string[] sprite, int left, int top, Func<char, int, uint> color, bool mirror = false)
    {
        for (var sy = 0; sy < sprite.Length; sy++)
        {
            var line = sprite[sy];
            for (var sx = 0; sx < line.Length; sx++)
            {
                var c = color(line[mirror ? line.Length - 1 - sx : sx], sx);
                if (c == 0)
                {
                    continue;
                }

                if (layer is null)
                {
                    Plot(left + sx, top + sy, c);
                }
                else
                {
                    Put(layer, left + sx, top + sy, c);
                }
            }
        }
    }

    private sealed record Facade(uint Body, uint Shade, uint Accent);

    private sealed class Car
    {
        public double X;
        public bool ToRight;
        public uint Body;
        public uint Shade;
        public uint Hair;
        public uint Skin;
    }
}
