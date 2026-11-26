namespace Soffritto.Desktop;

// The power station turned into a venue, in the evening. From the left: the stage with the
// comedian, the hall with the tables in front of the old turbine kept as decor, the bar. Three fixed
// layers (background in sky, furniture behind the people in land, furniture in front in fore) with
// the animated people in between; the lights on top of everything.
public sealed partial class PixelScenery
{
    private const int StageHeight = 7;
    private const int TileBand = 22;
    private const uint WindowFrame = 0xFF15181C;
    private const uint Night = 0xFF0C080A;

    // Dark brick, between the walls and the curtain.
    private const uint VenueChrome = 0xFF3A1B1F;

    private static readonly uint[] Dusk = [0xFF0E1433, 0xFF1F2152, 0xFF452A66, 0xFF8A3E6E, 0xFFC8645A];
    private static readonly uint[] Bricks = [0xFF5C2E25, 0xFF54291F, 0xFF66352A, 0xFF4E261E];
    private static readonly uint[] TurbineShade = [0xFF5A8C74, 0xFF3F6B5A, 0xFF34584A, 0xFF22382F];
    private static readonly uint[] Drape = [0xFF4A0C16, 0xFF6E1222, 0xFF8E1C2E, 0xFF6E1222];
    private static readonly uint[] BottleColors = [0xFF2E7A3A, 0xFFB06A1E, 0xFFB8D8E0, 0xFF8A1E2A, 0xFF2A4A9A, 0xFFD8B84A, 0xFF5A2A6A];
    private static readonly (uint Light, uint Shade)[] Skins = [(0xFFF2C9A0, 0xFFD9A57A), (0xFFE0A47A, 0xFFB97E58), (0xFFC68642, 0xFF9C6431), (0xFF8D5524, 0xFF6A3E1A)];
    private static readonly uint[] HairColors = [0xFF2A1A12, 0xFF5A3A1E, 0xFFD8B060, 0xFF8A2E1A, 0xFF9A9A9A, 0xFF151515];
    private static readonly uint[] ShirtColors = [0xFFB8443A, 0xFF3A6EA5, 0xFF4E8A4A, 0xFFD8A23A, 0xFF7A4A9A, 0xFFE8E2D0, 0xFF2A2A2A, 0xFF3AA59A, 0xFFD86A8A];
    private static readonly uint[] PantsColors = [0xFF2A3550, 0xFF3A3A3A, 0xFF5A4A3A, 0xFF1E2A3A];

    private static readonly Look ComicLook = new(0xFF2A1A12, 0xFFE0A47A, 0xFFB97E58, 0xFF2E4270, 0xFF1F2D50, 0xFF2A2A30, 0xFF141414);

    private static readonly (int Dx, int Dy, double Weight)[] Halo3 = Halo(3);
    private static readonly (int Dx, int Dy, double Weight)[] Halo4 = Halo(4);

    private static readonly Dictionary<char, string[]> NeonFont = new()
    {
        ['C'] = [".XXX.", "X...X", "X....", "X....", "X....", "X...X", ".XXX."],
        ['O'] = [".XXX.", "X...X", "X...X", "X...X", "X...X", "X...X", ".XXX."],
        ['M'] = ["X...X", "XX.XX", "X.X.X", "X.X.X", "X...X", "X...X", "X...X"],
        ['E'] = ["XXXXX", "X....", "X....", "XXXX.", "X....", "X....", "XXXXX"],
        ['D'] = ["XXXX.", "X...X", "X...X", "X...X", "X...X", "X...X", "XXXX."],
        ['Y'] = ["X...X", "X...X", ".X.X.", "..X..", "..X..", "..X..", "..X.."],
        ['B'] = ["XXXX.", "X...X", "X...X", "XXXX.", "X...X", "X...X", "XXXX."],
        ['A'] = [".XXX.", "X...X", "X...X", "XXXXX", "X...X", "X...X", "X...X"],
        ['R'] = ["XXXX.", "X...X", "X...X", "XXXX.", "X.X..", "X..X.", "X...X"],
    };

    private static readonly string[] LaughH = ["X.X", "X.X", "XXX", "X.X", "X.X"];
    private static readonly string[] LaughA = [".X.", "X.X", "XXX", "X.X", "X.X"];

    // The comedian faces front, 24 rows tall: 17 of torso and 7 of legs, which change while walking.
    private static readonly string[] ComicTalk =
    [
        "....HHHH....",
        "...HHHHHH...",
        "...HSSSSs...",
        "...SESSEs...",
        "...SSSSSs.mm",
        "....SSsSS.mm",
        ".....SS...M.",
        "..CCCTTCCSS.",
        ".CCCCTTCCCc.",
        ".CCCCTTCCcc.",
        ".CCCCTTCCcc.",
        ".SCCCTTCCc..",
        ".SCCCTTCCc..",
        "..CCCCCCCc..",
        "..CCCCCCCc..",
        "...PPPPPP...",
        "...PPPPPP...",
    ];

    private static readonly string[] ComicTalkOpen =
    [
        "....HHHH....",
        "...HHHHHH...",
        "...HSSSSs...",
        "...SESSEs...",
        "...SSSSSs.mm",
        "....SSESS.mm",
        ".....SS...M.",
        "..CCCTTCCSS.",
        ".CCCCTTCCCc.",
        ".CCCCTTCCcc.",
        ".CCCCTTCCcc.",
        ".SCCCTTCCc..",
        ".SCCCTTCCc..",
        "..CCCCCCCc..",
        "..CCCCCCCc..",
        "...PPPPPP...",
        "...PPPPPP...",
    ];

    private static readonly string[] ComicRaise =
    [
        "S...HHHH....",
        "S..HHHHHH...",
        "C..HSSSSs...",
        "C..SESSEs...",
        ".C.SSSSSs.mm",
        ".C..SSESS.mm",
        "..C..SS...M.",
        "..CCCTTCCSS.",
        "..CCCTTCCCc.",
        "..CCCTTCCcc.",
        "..CCCTTCCcc.",
        "..CCCTTCCc..",
        "..CCCTTCCc..",
        "..CCCCCCCc..",
        "..CCCCCCCc..",
        "...PPPPPP...",
        "...PPPPPP...",
    ];

    private static readonly string[] ComicShrug =
    [
        "....HHHH....",
        "...HHHHHH...",
        "...HSSSSs...",
        "...SESSEs...",
        "...SSSSSs.mm",
        "....SEEES.mm",
        ".....SS...M.",
        "S.CCCTTCCSS.",
        "SCCCCTTCCCc.",
        "..CCCTTCCcc.",
        "..CCCTTCCcc.",
        "..CCCTTCCc..",
        "..CCCTTCCc..",
        "..CCCCCCCc..",
        "..CCCCCCCc..",
        "...PPPPPP...",
        "...PPPPPP...",
    ];

    private static readonly string[] Bartender =
    [
        "....HHHH....",
        "...HHHHHH...",
        "...HSSSSs...",
        "...SESSEs...",
        "...SSSSSs...",
        "....SSsSS...",
        ".....SS.....",
        "..WWVRRVWW..",
        ".WWWVWWVWWW.",
        ".WWWVWWVWWW.",
        ".WWWVVVVWWW.",
        ".SWWVVVVWWS.",
        ".S.AAAAAA.S.",
        "...AAAAAA...",
        "...AAAAAA...",
        "...AAAAAA...",
        "...AAAAAA...",
    ];

    private static readonly string[] BartenderShaking =
    [
        "....HHHH..S.",
        "...HHHHHH.S.",
        "...HSSSSs.W.",
        "...SESSEs.W.",
        "...SSSSSsW..",
        "....SSsSSW..",
        ".....SS..W..",
        "..WWVRRVWW..",
        ".WWWVWWVWW..",
        ".WWWVWWVWW..",
        ".WWWVVVVWW..",
        ".SWWVVVVWW..",
        ".S.AAAAAA...",
        "...AAAAAA...",
        "...AAAAAA...",
        "...AAAAAA...",
        "...AAAAAA...",
    ];

    private static readonly string[] Waiter =
    [
        "....HHHH....",
        "...HHHHHH...",
        "...HSSSSs...",
        "...SESSEs...",
        "...SSSSSs...",
        "....SSsSS...",
        ".....SS.....",
        "..WWVRRVWW..",
        "SWWWVWWVWWW.",
        "SWWWVWWVWWW.",
        "..WWVVVVWWW.",
        "...WVVVVWWS.",
        "...AAAAAA.S.",
        "...AAAAAA...",
        "...AAAAAA...",
        "...AAAAAA...",
        "...AAAAAA...",
    ];

    private static readonly string[] LegsStand =
    [
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "..BBB..BBB..",
    ];

    private static readonly string[] LegsStepA =
    [
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "...PP..BBB..",
        "..BBB.......",
    ];

    private static readonly string[] LegsStepB =
    [
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "...PP..PP...",
        "..BBB..PP...",
        ".......BBB..",
    ];

    // Seated in profile, facing left: the hands (row 9) rest on the table, the thighs (rows 12-13)
    // on the chair, the feet (row 19) on the floor. Mirrored, it faces right.
    private static readonly string[] Seated =
    [
        "...HHHH...",
        "..HHHHHH..",
        "..SSSHHH..",
        "..ESSHHH..",
        ".SSSSSHH..",
        "..SsSSHL..",
        "...SSSLL..",
        "..CCCCCc..",
        ".CCCCCCc..",
        "SSCCCCCc..",
        "...CCCCc..",
        "...CCCCc..",
        "PPPPPPPP..",
        "PPPPPPPP..",
        "PP........",
        "PP........",
        "PP........",
        "PP........",
        "PP........",
        "BBB.......",
    ];

    private readonly List<Patron> patrons = [];
    private readonly List<Bulb> bulbs = [];
    private readonly List<Neon> neons = [];
    private readonly List<Lamp> lamps = [];
    private readonly List<(int X, int Y, double Phase)> stars = [];
    private readonly List<(int X, int Y, double Phase)> candles = [];
    private readonly List<(int X, int Y)> pendants = [];
    private readonly List<(int X, int Y, int Radius, double Phase)> gauges = [];
    private readonly List<Mote> dust = [];
    private readonly List<Mote> laughs = [];

    private bool venue;
    private uint[] fore = [];
    private int floor;
    private int stageLeft;
    private int stageRight;
    private int stageTop;
    private int barLeft;
    private int spotX;
    private int spotY;

    private double comedianX;
    private double comedianTarget;
    private int comedianMin;
    private int comedianMax;
    private bool comedianWalking;
    private bool mouthOpen;
    private Pose pose;
    private int poseTicks;
    private int jokeTicks;
    private int laughTicks;

    private Walker bartender = new();
    private Walker? waiter;

    private enum Pose { Talk, Raise, Shrug }

    private void BuildVenue()
    {
        var r = new Random(seed);
        fore = new uint[cols * rows];
        patrons.Clear();
        bulbs.Clear();
        neons.Clear();
        lamps.Clear();
        stars.Clear();
        candles.Clear();
        pendants.Clear();
        dust.Clear();
        laughs.Clear();
        gauges.Clear();

        floor = rows - GroundRows;
        var truss = Math.Clamp(rows / 11, 8, 18);
        var girder = truss + 1;
        var below = girder + 5;

        stageLeft = 2;
        stageRight = stageLeft + Math.Clamp(cols * 26 / 100, 50, 120);
        stageTop = floor - StageHeight;
        barLeft = cols - Math.Clamp(cols * 28 / 100, 56, 140);
        var hallLeft = stageRight + 6;
        var hallRight = barLeft - 6;

        PaintBricks();
        PaintWindows(r, below, hallLeft, hallRight);
        PaintTiles();
        PaintFloor(r);
        PaintPipe(hallLeft, hallRight);
        PaintTurbine(hallLeft, hallRight, truss);
        PaintTruss(truss);
        PaintGirder(girder, hallLeft, hallRight);
        PaintStage(below);
        PaintBackBar(r, below);
        PaintSwitchboard(below);
        PaintStrings(r, below);
        PaintTables(r, hallLeft, hallRight);
        PaintCounter(r);

        spotX = Math.Min(stageRight + 16, cols - 4);
        spotY = below;
        PaintSpotFixture();

        comedianMin = stageLeft + 18;
        comedianMax = Math.Max(comedianMin, stageRight - 30);
        comedianX = comedianTarget = (comedianMin + comedianMax) / 2;
        comedianWalking = false;
        pose = Pose.Talk;
        poseTicks = 0;
        jokeTicks = r.Next(40, 120);
        laughTicks = 0;

        var bartenderMax = Math.Max(barLeft + 4, cols - 14);
        bartender = new Walker { Min = barLeft + 4, Max = bartenderMax, Look = RandomLook(r) with { Pants = 0xFF1E1E22 } };
        bartender.X = bartender.Target = r.Next(bartender.Min, bartenderMax + 1);

        waiter = null;
        if (hallRight - 12 > hallLeft)
        {
            waiter = new Walker { Min = hallLeft, Max = hallRight - 12, Look = RandomLook(r) with { Pants = 0xFF1E1E22 } };
            waiter.X = waiter.Target = r.Next(waiter.Min, waiter.Max + 1);
        }

        for (var i = 0; i < 18; i++)
        {
            var speck = new Mote();
            RespawnDust(speck, r);
            dust.Add(speck);
        }
    }

    private void PaintBricks()
    {
        for (var y = 0; y < floor; y++)
        {
            var course = y / 3;
            var offset = course % 2 == 0 ? 0 : 4;
            var dark = 0.55 * (1 - ((double)y / floor));
            for (var x = 0; x < cols; x++)
            {
                var bx = x + offset;
                var color = y % 3 == 2 || bx % 8 == 7 ? 0xFF2C1814 : Bricks[Hash(course, bx / 8) % Bricks.Length];
                sky[(y * cols) + x] = Mix(color, Night, dark);
            }
        }
    }

    private void PaintWindows(Random r, int below, int left, int right)
    {
        var top = below + 12;
        var bottom = floor - TileBand - 18;
        if (right - left < 24 || bottom - top < 20)
        {
            return;
        }

        var bays = Math.Max(1, (right - left) / 44);
        var bay = (double)(right - left) / bays;
        var width = Math.Clamp((int)(bay * 0.5) & ~1, 12, 26);
        var moon = r.Next(bays);
        for (var b = 0; b < bays; b++)
        {
            PaintWindow(r, left + (int)(bay * (b + 0.5)), width, top, bottom, b == moon);
        }

        for (var b = 0; b <= bays; b++)
        {
            var x0 = left + (int)(bay * b) - 3;
            for (var y = below; y < floor - TileBand; y++)
            {
                for (var x = x0 - 1; x <= x0 + 6; x++)
                {
                    if (Inside(x, y))
                    {
                        var i = (y * cols) + x;
                        sky[i] = x == x0 - 1 || x == x0 + 6 ? Mix(sky[i], 0xFF000000, 0.35) : Mix(sky[i], 0xFFA06048, 0.16);
                    }
                }
            }
        }
    }

    // Large arched window of the engine hall: outside, the evening sky, the city in silhouette and a
    // few stars; the iron mullions stay in front of everything.
    private void PaintWindow(Random r, int center, int width, int top, int bottom, bool moon)
    {
        var half = width / 2;
        var left = center - half;
        var right = left + width - 1;
        var cx = left + (width / 2.0);
        var spring = top + half;
        var mullion1 = width / 3;
        var mullion2 = width * 2 / 3;
        var moonX = cx + 2;
        var moonY = spring + 3.0;

        for (var y = top - 4; y <= spring; y++)
        {
            for (var x = left - 4; x <= right + 4; x++)
            {
                var d = Distance(x + 0.5, y + 0.5, cx, spring);
                if (d > half && d <= half + 3.5)
                {
                    var angle = Math.Atan2(spring - (y + 0.5), x + 0.5 - cx);
                    Put(sky, x, y, Mix((int)(angle * 7) % 2 == 0 ? 0xFF7A4232 : 0xFF6A3828, Night, 0.3));
                }
            }
        }

        var skyline = new int[width];
        for (var x = 0; x < width;)
        {
            var w = r.Next(3, 7);
            var h = r.Next(3, Math.Max(4, (bottom - top) / 4));
            for (var k = 0; k < w && x < width; k++, x++)
            {
                skyline[x] = h;
            }
        }

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var d = Distance(x + 0.5, y + 0.5, cx, spring);
                if (y < spring && d > half)
                {
                    continue;
                }

                var lx = x - left;
                var color = Gradient(Dusk, (double)(y - top) / (bottom - top));
                if (moon && Distance(x + 0.5, y + 0.5, moonX, moonY) <= 3 && Distance(x + 0.5, y + 0.5, moonX + 1.6, moonY - 1) > 3)
                {
                    color = 0xFFEDE6C8;
                }
                else if (moon && Distance(x + 0.5, y + 0.5, moonX, moonY) < 7)
                {
                    color = Mix(color, 0xFF8A8AB0, 0.2);
                }

                if (bottom - y < skyline[lx])
                {
                    color = ((x * 7) + (y * 3)) % 11 == 0 ? 0xFF8A7440 : 0xFF140F24;
                }

                if (lx == 0 || x == right || y == bottom || lx == mullion1 || lx == mullion2 || (y < spring && d > half - 1) || (y > spring && (y - spring) % 7 == 0))
                {
                    color = WindowFrame;
                }

                Put(sky, x, y, color);
            }
        }

        for (var x = left - 2; x <= right + 2; x++)
        {
            Put(sky, x, bottom + 1, 0xFF6A645C);
            Put(sky, x, bottom + 2, 0xFF4A4640);
        }

        var count = r.Next(3, 7);
        for (var i = 0; i < count; i++)
        {
            var x = r.Next(left + 1, right);
            var y = r.Next(top + 2, top + Math.Max(3, (bottom - top) / 2));
            var inGlass = y >= spring || Distance(x + 0.5, y + 0.5, cx, spring) <= half - 1;
            if (inGlass && Inside(x, y) && sky[(y * cols) + x] != WindowFrame && sky[(y * cols) + x] != 0xFFEDE6C8)
            {
                stars.Add((x, y, r.NextDouble() * Math.Tau));
            }
        }
    }

    private void PaintTiles()
    {
        var top = floor - TileBand;
        for (var x = 0; x < cols; x++)
        {
            Put(sky, x, top - 2, 0xFF5E5A4A);
            Put(sky, x, top - 1, 0xFF8E8870);
            for (var y = top; y < floor; y++)
            {
                var ty = (y - top) % 4;
                var color = ty == 3 || x % 7 == 6 ? 0xFF1C302A
                    : ty == 0 ? 0xFF3A6656
                    : ((x / 7) + ((y - top) / 4)) % 3 == 0 ? 0xFF2A5044 : 0xFF2D5448;
                Put(sky, x, y, color);
            }
        }
    }

    private void PaintFloor(Random r)
    {
        for (var y = floor; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                Put(sky, x, y, y == floor ? 0xFF5A524A : r.Next(9) == 0 ? 0xFF2C2826 : 0xFF38332F);
            }
        }
    }

    private void PaintPipe(int left, int right)
    {
        uint[] shades = [0xFF8A3A2E, 0xFF6A2A22, 0xFF481A15];
        var y = floor - TileBand - 9;
        for (var x = left - 6; x <= right + 6; x++)
        {
            for (var k = 0; k < shades.Length; k++)
            {
                Put(sky, x, y + k, shades[k]);
            }

            if ((x - left) % 26 == 0)
            {
                for (var k = -1; k <= 3; k++)
                {
                    Put(sky, x, y + k, 0xFF6A6460);
                    Put(sky, x + 1, y + k, 0xFF4A4642);
                }
            }
        }
    }

    // The power station's turbine, left in the hall as decor: a cylinder lying on a plinth, with a
    // pressure gauge, a handwheel, a small panel of indicator lights and the exhaust rising to the trusses.
    private void PaintTurbine(int left, int right, int truss)
    {
        var span = right - left;
        if (span < 40)
        {
            return;
        }

        var width = Math.Clamp(span * 55 / 100, 32, 100);
        var height = Math.Clamp(rows / 7, 16, 30);
        var x0 = ((left + right) / 2) - (width / 2);
        var x1 = x0 + width - 1;
        var bottom = floor - 5;
        var top = bottom - height + 1;

        for (var y = floor - 4; y < floor; y++)
        {
            for (var x = x0 - 3; x <= x1 + 13; x++)
            {
                Put(sky, x, y, y == floor - 4 ? 0xFF6A645C : 0xFF4E4A44);
            }
        }

        uint[] exhaust = [0xFF34403A, 0xFF5A6A62, 0xFF7A8A80, 0xFF4A5A52];
        var px = x1 - 10;
        for (var y = truss; y <= top + 2; y++)
        {
            for (var k = 0; k < exhaust.Length; k++)
            {
                Put(sky, px + k, y, (y - truss) % 24 == 0 ? 0xFF8A9A90 : exhaust[k]);
            }
        }

        var exciterTop = top + (height / 4);
        var exciterBottom = bottom - (height / 4);
        for (var y = exciterTop; y <= exciterBottom; y++)
        {
            var color = Mix(Gradient(TurbineShade, (double)(y - exciterTop) / Math.Max(1, exciterBottom - exciterTop)), 0xFF000000, 0.15);
            for (var x = x1 + 1; x <= x1 + 10; x++)
            {
                Put(sky, x, y, x == x1 + 10 ? Mix(color, 0xFF000000, 0.3) : color);
            }
        }

        var cap = height / 3.0;
        var ring = Math.Max(4, width / 5);
        for (var y = top; y <= bottom; y++)
        {
            var v = (double)(y - top) / (height - 1);
            var inset = (int)Math.Round(cap * (1 - Math.Sqrt(Math.Max(0, 1 - Math.Pow((2 * v) - 1, 2)))));
            var shade = Gradient(TurbineShade, v);
            for (var x = x0 + inset; x <= x1 - inset; x++)
            {
                var color = (x - x0) % ring == 0 ? Mix(shade, 0xFFB8D8C8, 0.25) : (x - x0) % ring == 1 ? Mix(shade, 0xFF000000, 0.3) : shade;
                if (y == top + (height / 2))
                {
                    color = x % 4 == 0 ? 0xFF9AB8A8 : Mix(shade, 0xFF000000, 0.35);
                }

                Put(sky, x, y, color);
            }
        }

        var center = (x0 + x1) / 2;
        for (var y = top + 3; y <= top + 5; y++)
        {
            for (var x = center - 5; x <= center + 5; x++)
            {
                Put(sky, x, y, y == top + 4 && x % 2 == 0 ? 0xFF5A4418 : 0xFFA8823A);
            }
        }

        var box = x0 + (int)cap + 3;
        for (var y = top + 3; y <= top + 7; y++)
        {
            for (var x = box; x <= box + 8; x++)
            {
                Put(sky, x, y, y == top + 3 || y == top + 7 || x == box || x == box + 8 ? 0xFF2E3A34 : 0xFF1C2420);
            }
        }

        lamps.Add(new Lamp(box + 2, top + 5, 0xFFFF4A3A, 0xFF4A1A16, 7, 0));
        lamps.Add(new Lamp(box + 4, top + 5, 0xFF5AFF6A, 0xFF1A3A1E, 23, 5));
        lamps.Add(new Lamp(box + 6, top + 5, 0xFFFFB83A, 0xFF4A3414, 13, 3));

        var gaugeX = x0 + (width / 4);
        Put(sky, gaugeX, top - 1, 0xFF5A5A5A);
        Put(sky, gaugeX, top - 2, 0xFF5A5A5A);
        PaintGauge(gaugeX, top - 7, 4, 0);

        var hx = x0 + (width * 2 / 3);
        var hy = top - 5;
        Put(sky, hx, top - 1, 0xFF5A5A5A);
        for (var y = hy - 4; y <= hy + 4; y++)
        {
            for (var x = hx - 4; x <= hx + 4; x++)
            {
                var d = Distance(x, y, hx, hy);
                if (d > 2.6 && d <= 4.1)
                {
                    Put(sky, x, y, 0xFFA0281F);
                }
                else if ((x == hx || y == hy) && d <= 2.6)
                {
                    Put(sky, x, y, d < 1 ? 0xFF5A5A5A : 0xFF7A1E18);
                }
            }
        }
    }

    private void PaintTruss(int truss)
    {
        var panel = Math.Max(2, truss - 5);
        for (var x = 0; x < cols; x++)
        {
            for (var y = 0; y < truss; y++)
            {
                Put(sky, x, y, 0xFF100C10);
            }

            Put(sky, x, 1, 0xFF4C5864);
            Put(sky, x, 2, x % 5 == 0 ? 0xFF6A7682 : 0xFF333B44);
            Put(sky, x, truss - 2, 0xFF4C5864);
            Put(sky, x, truss - 1, x % 5 == 0 ? 0xFF6A7682 : 0xFF333B44);

            var phase = x % (2 * panel);
            Put(sky, x, 3 + (phase < panel ? phase : (2 * panel) - phase), 0xFF3A444E);
            if (phase == 0)
            {
                for (var y = 3; y < truss - 2; y++)
                {
                    Put(sky, x, y, 0xFF2E3640);
                }
            }
        }
    }

    // The overhead crane beam runs from wall to wall; the trolley sits still above the hall with
    // its hook lowered.
    private void PaintGirder(int girder, int left, int right)
    {
        for (var x = 0; x < cols; x++)
        {
            for (var k = 0; k < 5; k++)
            {
                var color = k == 0 ? 0xFFC09A3A : k == 4 ? 0xFF6A5420 : 0xFF9A7A2A;
                if ((x < 12 || x >= cols - 12) && k is >= 1 and <= 3)
                {
                    color = (x + k) / 3 % 2 == 0 ? 0xFF1A1A1A : 0xFFC8A030;
                }
                else if (k is 1 or 3 && x % 6 == 3)
                {
                    color = 0xFF6A5420;
                }

                Put(sky, x, girder + k, Mix(color, Night, 0.25));
            }
        }

        if (right - left < 30)
        {
            return;
        }

        var tx = left + ((right - left) * 2 / 5);
        var hook = girder + 24;
        for (var y = girder + 5; y <= girder + 7; y++)
        {
            for (var x = tx - 5; x <= tx + 4; x++)
            {
                Put(sky, x, y, y == girder + 5 ? 0xFF4A4A4A : 0xFF3A3A3A);
            }
        }

        for (var y = girder + 8; y <= hook - 4; y++)
        {
            var link = y % 2 == 0 ? 0xFF8A8A8A : 0xFF5A5A5A;
            Put(sky, tx - 2, y, link);
            Put(sky, tx + 1, y, link);
        }

        for (var y = hook - 3; y < hook; y++)
        {
            for (var x = tx - 3; x <= tx + 2; x++)
            {
                Put(sky, x, y, y == hook - 1 ? 0xFF8A6E20 : 0xFFB0902A);
            }
        }

        foreach (var (x, y) in new[] { (tx - 1, hook), (tx - 1, hook + 1), (tx - 1, hook + 2), (tx, hook + 3), (tx + 1, hook + 3), (tx + 2, hook + 2) })
        {
            Put(sky, x, y, 0xFF8A8A8A);
        }
    }

    private void PaintStage(int below)
    {
        var valance = below + 1;
        var drapeHeight = Math.Max(1, stageTop - valance);
        for (var y = valance; y < stageTop; y++)
        {
            var shade = 0.35 * (1 - ((double)(y - valance) / drapeHeight));
            for (var k = 0; k < 8; k++)
            {
                var color = Mix(Drape[k % 4], Night, shade);
                Put(sky, stageLeft + k, y, color);
                Put(sky, stageRight - k, y, color);
            }
        }

        var tie = stageTop - 22;
        for (var k = 0; k < 8; k++)
        {
            Put(sky, stageLeft + k, tie, 0xFFC8A040);
            Put(sky, stageLeft + k, tie + 1, 0xFF9A7A30);
            Put(sky, stageRight - k, tie, 0xFFC8A040);
            Put(sky, stageRight - k, tie + 1, 0xFF9A7A30);
        }

        for (var x = stageLeft; x <= stageRight; x++)
        {
            var depth = 5 + (int)Math.Round(3 * Math.Sin(Math.PI * ((x - stageLeft) % 10) / 10));
            for (var k = 0; k < depth; k++)
            {
                Put(sky, x, valance + k, Drape[(x / 2) % Drape.Length]);
            }

            Put(sky, x, valance + depth, 0xFFC8A040);
            if (x % 2 == 0)
            {
                Put(sky, x, valance + depth + 1, 0xFF9A7A30);
            }
        }

        AddNeon("COMEDY", (stageLeft + stageRight) / 2, stageTop - 38, 0xFFFF5AB8);

        for (var y = stageTop; y < floor; y++)
        {
            for (var x = stageLeft; x <= stageRight; x++)
            {
                var color = y == stageTop ? 0xFF9A6A3E : y == stageTop + 1 ? 0xFF6A4628 : (x - stageLeft) % 14 == 0 ? 0xFF241C1E : 0xFF1A1416;
                Put(land, x, y, color);
            }
        }

        var sx = stageLeft + 10;
        for (var x = sx; x <= sx + 5; x++)
        {
            Put(land, x, stageTop - 10, 0xFF4A4A50);
            Put(land, x, stageTop - 9, 0xFF2A2A2E);
        }

        for (var y = stageTop - 8; y < stageTop; y++)
        {
            Put(land, sx + 1, y, 0xFF3A3A40);
            Put(land, sx + 4, y, 0xFF3A3A40);
        }

        Put(land, sx + 2, stageTop - 4, 0xFF3A3A40);
        Put(land, sx + 3, stageTop - 4, 0xFF3A3A40);
        for (var y = stageTop - 14; y <= stageTop - 11; y++)
        {
            Put(land, sx + 2, y, y == stageTop - 14 ? 0xFFD8F0F8 : 0xFF8ACBE0);
            Put(land, sx + 3, y, y == stageTop - 14 ? 0xFFD8F0F8 : 0xFF6AABC0);
        }

        var mx = stageRight - 13;
        for (var y = stageTop - 21; y <= stageTop - 2; y++)
        {
            Put(land, mx, y, 0xFF6A6A72);
        }

        Put(land, mx, stageTop - 22, 0xFF2A2A2E);
        Put(land, mx, stageTop - 23, 0xFF2A2A2E);
        Put(land, mx + 1, stageTop - 23, 0xFF2A2A2E);
        for (var x = mx - 3; x <= mx + 3; x++)
        {
            Put(land, x, stageTop - 1, 0xFF3A3A40);
        }

        for (var x = mx - 1; x <= mx + 1; x++)
        {
            Put(land, x, stageTop - 2, 0xFF3A3A40);
        }

        var wx = ((stageLeft + stageRight) / 2) - 4;
        for (var y = stageTop - 4; y < stageTop; y++)
        {
            var inset = y == stageTop - 4 ? 1 : 0;
            for (var x = wx + inset; x <= wx + 8 - inset; x++)
            {
                Put(fore, x, y, (x + y) % 2 == 0 ? 0xFF2A2A30 : 0xFF141418);
            }
        }
    }

    // The back bar: three shelves of bottles lit from below, the sign above and the pendant lamps
    // hanging from the beam.
    private void PaintBackBar(Random r, int below)
    {
        var left = barLeft + 3;
        var right = cols - 1;
        var width = cols - barLeft;
        int[] shelves = [floor - 18, floor - 27, floor - 36, floor - 45];

        for (var x = left - 1; x <= right; x++)
        {
            Put(sky, x, floor - 47, 0xFF4A3020);
            Put(sky, x, floor - 46, 0xFF2A1A12);
        }

        for (var y = floor - 45; y <= floor - 16; y++)
        {
            for (var x = left; x <= right; x++)
            {
                uint color;
                if (x <= left + 1)
                {
                    color = 0xFF2A1A12;
                }
                else if (y > shelves[0])
                {
                    color = 0xFF140E10;
                }
                else if (Array.IndexOf(shelves, y) >= 0)
                {
                    color = 0xFF7A5434;
                }
                else
                {
                    var shelf = shelves.Where(s => s > y).Min();
                    color = Mix(0xFF9A6A30, 0xFF16121C, 0.35 + (0.65 * (shelf - 1 - y) / 8.0));
                }

                Put(sky, x, y, color);
            }
        }

        foreach (var shelf in shelves.Take(3))
        {
            for (var x = left + 3; x < right - 3;)
            {
                if (r.Next(6) == 0)
                {
                    x += 3;
                    continue;
                }

                var h = r.Next(5, 8);
                var c = BottleColors[r.Next(BottleColors.Length)];
                Put(sky, x + 1, shelf - h, r.Next(2) == 0 ? 0xFFC8A040 : Mix(c, 0xFF000000, 0.4));
                Put(sky, x + 1, shelf - h + 1, c);
                var label = r.Next(2) == 0;
                for (var y = shelf - h + 2; y < shelf; y++)
                {
                    var band = label && y == shelf - 3;
                    Put(sky, x, y, band ? 0xFFC8BCA0 : Mix(c, 0xFFFFFFFF, 0.3));
                    Put(sky, x + 1, y, band ? 0xFFE8DCC0 : c);
                    Put(sky, x + 2, y, band ? 0xFFA89C80 : Mix(c, 0xFF000000, 0.3));
                }

                x += 3 + r.Next(1, 3);
            }
        }

        foreach (var f in new[] { 0.2, 0.5, 0.8 })
        {
            var px = barLeft + (int)(width * f);
            for (var y = below; y < floor - 57; y++)
            {
                Put(sky, px, y, 0xFF0E0C0C);
            }

            for (var k = 0; k < 3; k++)
            {
                for (var x = px - 1 - k; x <= px + 1 + k; x++)
                {
                    Put(sky, x, floor - 57 + k, k == 2 ? 0xFF3A6050 : 0xFF1E3A2E);
                }
            }

            pendants.Add((px, floor - 54));
        }

        AddNeon("BAR", (left + right) / 2, floor - 66, 0xFF5CE8FF);
    }

    private void PaintStrings(Random r, int below)
    {
        var left = stageRight + 1;
        var right = barLeft - 1;
        if (right - left < 20)
        {
            return;
        }

        var spans = Math.Max(1, (right - left) / 70);
        var span = (double)(right - left) / spans;
        for (var strand = 0; strand < 2; strand++)
        {
            var anchor = below + 8 + (strand * 9);
            var sag = 10 - (strand * 3);
            for (var x = left; x <= right; x++)
            {
                var t = (x - left) % span / span;
                var y = anchor + (int)Math.Round(sag * 4 * t * (1 - t));
                Put(sky, x, y, 0xFF0A0808);
                if ((x - left + (strand * 4)) % 8 == 4)
                {
                    Put(sky, x, y + 1, 0xFF1A1414);
                    bulbs.Add(new Bulb { X = x, Y = y + 2, Phase = r.NextDouble() * Math.Tau });
                }
            }
        }
    }

    private void PaintTables(Random r, int left, int right)
    {
        const int unit = 42;
        var count = (right - left) / unit;
        if (count < 1)
        {
            return;
        }

        var step = (double)(right - left) / count;
        for (var i = 0; i < count; i++)
        {
            var tx = left + (int)(step * (i + 0.5));

            // Whoever sits on the right of the table faces the stage; whoever sits on the left looks
            // at their tablemate, and that chair is empty more often.
            PaintSeat(r, tx + 5, false, r.Next(5) != 0);
            PaintSeat(r, tx - 14, true, r.Next(3) != 0);

            for (var x = tx - 7; x <= tx + 7; x++)
            {
                Put(fore, x, floor - 10, 0xFF9A7048);
                Put(fore, x, floor - 9, 0xFF5A3C26);
            }

            for (var y = floor - 8; y < floor - 1; y++)
            {
                Put(fore, tx - 1, y, 0xFF3A3A40);
                Put(fore, tx, y, 0xFF242428);
            }

            for (var x = tx - 3; x <= tx + 3; x++)
            {
                Put(fore, x, floor - 1, 0xFF2A2A2E);
            }

            PaintDrink(r, tx - 4, floor - 11);
            PaintDrink(r, tx + 3, floor - 11);
            Put(fore, tx, floor - 12, 0xFFB04A30);
            Put(fore, tx, floor - 11, 0xFF8A3A2A);
            candles.Add((tx, floor - 13, r.NextDouble() * Math.Tau));
        }
    }

    private void PaintSeat(Random r, int left, bool mirror, bool taken)
    {
        const uint chair = 0xFF2E2428;
        var top = floor - 20;
        int X(int column) => mirror ? left + 9 - column : left + column;

        for (var y = top + 6; y <= top + 13; y++)
        {
            Put(land, X(8), y, chair);
        }

        Put(land, X(9), top + 6, chair);
        for (var c = 1; c <= 8; c++)
        {
            Put(land, X(c), top + 14, 0xFF4A3A30);
        }

        for (var y = top + 15; y <= top + 19; y++)
        {
            Put(land, X(2), y, chair);
            Put(land, X(8), y, chair);
        }

        if (taken)
        {
            patrons.Add(new Patron { X = left, Top = top, Mirror = mirror, Look = RandomLook(r).Dimmed(), Phase = r.Next(100), Laughs = r.Next(5) != 0 });
        }
    }

    // A glass on the table, with its base on the bottom row: beer, wine or a cocktail.
    private void PaintDrink(Random r, int x, int bottom)
    {
        const uint glass = 0xFFC8D8E0;
        switch (r.Next(3))
        {
            case 0:
                Put(fore, x, bottom - 4, 0xFFF0E8D8);
                Put(fore, x + 1, bottom - 4, 0xFFF0E8D8);
                for (var y = bottom - 3; y <= bottom; y++)
                {
                    Put(fore, x, y, 0xFFD08A2A);
                    Put(fore, x + 1, y, 0xFFA86A1A);
                }

                break;

            case 1:
                for (var k = 0; k < 3; k++)
                {
                    Put(fore, x + k, bottom - 4, glass);
                    Put(fore, x + k, bottom - 3, 0xFF8A1E2A);
                    Put(fore, x + k, bottom, glass);
                }

                Put(fore, x + 1, bottom - 2, 0xFF8A1E2A);
                Put(fore, x + 1, bottom - 1, glass);
                break;

            default:
                for (var k = 0; k < 3; k++)
                {
                    Put(fore, x + k, bottom - 4, 0xFFE0508A);
                    Put(fore, x + k, bottom, glass);
                }

                Put(fore, x + 3, bottom - 5, 0xFF6AC040);
                Put(fore, x + 1, bottom - 3, 0xFFE0508A);
                Put(fore, x + 1, bottom - 2, glass);
                Put(fore, x + 1, bottom - 1, glass);
                break;
        }
    }

    private void PaintCounter(Random r)
    {
        var width = cols - barLeft;
        for (var x = barLeft; x < cols; x++)
        {
            Put(fore, x, floor - 15, 0xFFC8A47A);
            Put(fore, x, floor - 14, 0xFF9A7A5A);
            Put(fore, x, floor - 13, x == barLeft ? 0xFF2A1A10 : 0xFFFFC870);
            var groove = (x - barLeft) % 11;
            for (var y = floor - 12; y < floor; y++)
            {
                var color = x <= barLeft + 1 ? 0xFF4A3020 : groove == 0 ? 0xFF241610 : groove == 1 ? 0xFF4A3020 : 0xFF3A2416;
                if (y == floor - 12)
                {
                    color = Mix(color, 0xFFFFC870, 0.3);
                }
                else if (y == floor - 11)
                {
                    color = Mix(color, 0xFFFFC870, 0.12);
                }

                if (y == floor - 4)
                {
                    color = 0xFFC9A04A;
                }
                else if (y == floor - 3 && (x - barLeft) % 20 == 6)
                {
                    color = 0xFF8A6A30;
                }

                Put(fore, x, y, color);
            }
        }

        var tx = barLeft + (width * 6 / 10);
        for (var y = floor - 21; y <= floor - 16; y++)
        {
            for (var k = -4; k <= 4; k++)
            {
                Put(fore, tx + k, y, k <= -3 ? 0xFF6A7078 : k <= 0 ? 0xFFC8D0D8 : 0xFF8A9098);
            }
        }

        uint[] handles = [0xFFB02A2A, 0xFF1A1A1A, 0xFFC8A040];
        for (var h = 0; h < handles.Length; h++)
        {
            var hx = tx - 3 + (h * 3);
            Put(fore, hx, floor - 27, Mix(handles[h], 0xFFFFFFFF, 0.4));
            for (var y = floor - 26; y <= floor - 22; y++)
            {
                Put(fore, hx, y, handles[h]);
            }
        }

        var pint = barLeft + 5;
        Put(fore, pint, floor - 20, 0xFFF0E8D8);
        Put(fore, pint + 1, floor - 20, 0xFFF0E8D8);
        for (var y = floor - 19; y <= floor - 16; y++)
        {
            Put(fore, pint, y, 0xFFD08A2A);
            Put(fore, pint + 1, y, 0xFFA86A1A);
        }

        var cocktail = tx + 8;
        for (var k = 0; k < 5; k++)
        {
            Put(fore, cocktail + k, floor - 20, 0xFFE0508A);
        }

        for (var k = 1; k <= 3; k++)
        {
            Put(fore, cocktail + k, floor - 19, 0xFFE0508A);
            Put(fore, cocktail + k, floor - 16, 0xFFC8D8E0);
        }

        Put(fore, cocktail + 4, floor - 21, 0xFF6AC040);
        Put(fore, cocktail + 2, floor - 18, 0xFFC8D8E0);
        Put(fore, cocktail + 2, floor - 17, 0xFFC8D8E0);

        var rocks = cols - 9;
        for (var k = 0; k < 3; k++)
        {
            Put(fore, rocks + k, floor - 19, 0xFFC8D8E0);
            for (var y = floor - 18; y <= floor - 16; y++)
            {
                Put(fore, rocks + k, y, 0xFFB86A20);
            }
        }

        Put(fore, rocks + 1, floor - 18, 0xFFDCEAF0);

        var stools = Math.Max(1, (width - 12) / 24);
        for (var i = 0; i < stools; i++)
        {
            var sx = barLeft + 6 + (i * 24);
            for (var x = sx; x <= sx + 6; x++)
            {
                Put(fore, x, floor - 12, 0xFF5A3A2A);
                Put(fore, x, floor - 11, 0xFF3A2418);
            }

            for (var y = floor - 10; y < floor - 1; y++)
            {
                Put(fore, sx + 3, y, 0xFF8A9098);
            }

            for (var x = sx + 1; x <= sx + 5; x++)
            {
                Put(fore, x, floor - 5, 0xFF6A7078);
                Put(fore, x, floor - 1, 0xFF4A4E54);
            }

            if (r.Next(5) < 3)
            {
                patrons.Add(new Patron { X = sx - 1, Top = floor - 26, Look = RandomLook(r).Dimmed(), Phase = r.Next(100), Laughs = r.Next(5) != 0, OnStool = true });
            }
        }
    }

    // The power station's old control panel, still hanging above the bar: a marble slab with three
    // gauges still alive and a row of knife switches.
    private void PaintSwitchboard(int below)
    {
        var bottom = floor - 74;
        var top = Math.Max(below + 12, bottom - 34);
        var left = barLeft + 8;
        var right = cols - 8;
        if (bottom - top < 24 || right - left < 40)
        {
            return;
        }

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var border = y == top || y == bottom || x == left || x == right;
                var vein = (Hash(x / 3, y / 2) % 23 == 0) || ((x + (y * 2)) % 31 == 0 && Hash(x, y) % 3 == 0);
                var color = border ? 0xFF2A2A2E : vein ? 0xFF6A6A66 : (x + y) % 2 == 0 ? 0xFF8A8A84 : 0xFF86867E;
                Put(sky, x, y, Mix(color, Night, border ? 0 : 0.35));
            }
        }

        foreach (var (x, y) in new[] { (left + 2, top + 2), (right - 2, top + 2), (left + 2, bottom - 2), (right - 2, bottom - 2) })
        {
            Put(sky, x, y, 0xFFA8823A);
        }

        var dialY = top + 9;
        var spacing = (right - left) / 3;
        for (var i = 0; i < 3; i++)
        {
            var dialX = left + (spacing / 2) + (i * spacing);
            PaintGauge(dialX, dialY, 5, i * 2.1);
            for (var x = dialX - 3; x <= dialX + 3; x++)
            {
                Put(sky, x, dialY + 8, 0xFFA8823A);
            }
        }

        var switches = Math.Max(2, (right - left - 8) / 9);
        var gap = (double)(right - left - 8) / switches;
        for (var i = 0; i < switches; i++)
        {
            var sx = left + 6 + (int)(gap * i);
            var open = Hash(i, seed & 0xFF) % 4 == 0;
            for (var y = bottom - 8; y <= bottom - 3; y++)
            {
                Put(sky, sx, y, 0xFF3A3A3A);
                Put(sky, sx + 3, y, 0xFF3A3A3A);
            }

            for (var k = 0; k < 5; k++)
            {
                var y = open ? bottom - 9 - k : bottom - 8 + k;
                Put(sky, sx + 1, y, 0xFFC8844A);
                Put(sky, sx + 2, y, 0xFF9A5E2A);
            }

            var handle = open ? bottom - 14 : bottom - 3;
            Put(sky, sx + 1, handle, 0xFF141414);
            Put(sky, sx + 2, handle, 0xFF141414);
        }

        lamps.Add(new Lamp(right - 4, top + 3, 0xFFFF4A3A, 0xFF4A1A16, 9, 2));
    }

    private void PaintGauge(int cx, int cy, int radius, double phase)
    {
        for (var y = cy - radius - 1; y <= cy + radius + 1; y++)
        {
            for (var x = cx - radius - 1; x <= cx + radius + 1; x++)
            {
                var d = Distance(x + 0.5, y + 0.5, cx + 0.5, cy + 0.5);
                if (d <= radius + 0.6)
                {
                    Put(sky, x, y, d > radius - 0.6 ? 0xFFB08A3A : 0xFFE6DEC6);
                }
            }
        }

        foreach (var angle in new[] { -2.4, -1.95, -1.57, -1.15, -0.7 })
        {
            Put(sky, cx + (int)Math.Round(Math.Cos(angle) * (radius - 1)), cy + (int)Math.Round(Math.Sin(angle) * (radius - 1)), 0xFF3A3A3A);
        }

        gauges.Add((cx, cy, radius - 1, phase));
    }

    private void PaintSpotFixture()
    {
        for (var x = spotX - 1; x <= spotX + 1; x++)
        {
            Put(sky, x, spotY, 0xFF2A2A2E);
        }

        for (var y = spotY + 1; y <= spotY + 3; y++)
        {
            for (var x = spotX - 2; x <= spotX + 2; x++)
            {
                Put(sky, x, y, y == spotY + 1 ? 0xFF34343A : 0xFF222226);
            }
        }

        Put(sky, spotX - 2, spotY + 3, 0xFFFFF0C0);
        Put(sky, spotX - 1, spotY + 3, 0xFFFFF0C0);
    }

    // The unlit tube shows even while a letter flickers; the halo is computed only once.
    private void AddNeon(string text, int center, int top, uint color)
    {
        var neon = new Neon { Color = color, Core = Mix(color, 0xFFFFFFFF, 0.45), Off = Mix(color, 0xFF100810, 0.75), Letters = text.Length };
        var left = center - (((text.Length * 6) - 1) / 2);
        for (var i = 0; i < text.Length; i++)
        {
            var glyph = NeonFont[text[i]];
            for (var gy = 0; gy < glyph.Length; gy++)
            {
                for (var gx = 0; gx < glyph[gy].Length; gx++)
                {
                    if (glyph[gy][gx] == 'X' && Inside(left + (i * 6) + gx, top + gy))
                    {
                        neon.Pixels.Add((left + (i * 6) + gx, top + gy, i));
                        Put(sky, left + (i * 6) + gx, top + gy, neon.Off);
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
                        glow[i] = Math.Max(glow.GetValueOrDefault(i), weight * 0.55);
                    }
                }
            }
        }

        neon.Glow = [.. glow.Select(g => (g.Key, g.Value))];
        neons.Add(neon);
    }

    private void StepVenue()
    {
        StepComedian();
        if (laughTicks > 0)
        {
            laughTicks--;
        }

        Walk(bartender, 0.3, canShake: true);
        if (waiter is not null)
        {
            Walk(waiter, 0.45, canShake: false);
        }

        foreach (var bulb in bulbs)
        {
            if (bulb.Off > 0)
            {
                bulb.Off--;
            }
            else if (random.Next(1500) == 0)
            {
                bulb.Off = random.Next(3, 10);
            }
        }

        foreach (var neon in neons)
        {
            if (neon.Flicker > 0)
            {
                neon.Flicker--;
            }
            else if (random.Next(300) == 0)
            {
                neon.Flicker = random.Next(4, 14);
                neon.Letter = random.Next(neon.Letters);
            }
        }

        foreach (var speck in dust)
        {
            speck.X += speck.Vx;
            speck.Y += speck.Vy;
            if (speck.Y >= stageTop - 1 || speck.X < 0 || speck.X >= cols)
            {
                RespawnDust(speck, random);
                speck.Y = spotY + 3;
            }
        }

        foreach (var laugh in laughs)
        {
            laugh.Y -= 0.3;
            laugh.Phase++;
        }

        laughs.RemoveAll(l => l.Phase > 32);
    }

    // The comedian talks and now and then walks across the stage; on every joke he raises an arm or
    // spreads his arms, and when the pose ends the audience laughs.
    private void StepComedian()
    {
        if (poseTicks > 0)
        {
            poseTicks--;
            if (poseTicks == 0)
            {
                pose = Pose.Talk;
                StartLaugh();
            }

            return;
        }

        mouthOpen = laughTicks == 0 && random.Next(3) == 0;
        var gap = comedianTarget - comedianX;
        comedianWalking = Math.Abs(gap) > 0.25;
        if (comedianWalking)
        {
            comedianX += Math.Sign(gap) * 0.25;
            return;
        }

        if (--jokeTicks <= 0)
        {
            pose = random.Next(2) == 0 ? Pose.Raise : Pose.Shrug;
            poseTicks = 14;
            jokeTicks = random.Next(90, 200);
            mouthOpen = false;
        }
        else if (laughTicks == 0 && random.Next(90) == 0)
        {
            comedianTarget = random.Next(comedianMin, comedianMax + 1);
        }
    }

    private void StartLaugh()
    {
        laughTicks = random.Next(30, 50);
        foreach (var patron in patrons)
        {
            if (patron.Laughs && random.Next(3) == 0 && laughs.Count < 8)
            {
                laughs.Add(new Mote { X = patron.X + 2, Y = patron.Top - 6 });
            }
        }
    }

    private void Walk(Walker w, double speed, bool canShake)
    {
        if (w.Ticks > 0)
        {
            w.Ticks--;
            w.Shaking &= w.Ticks > 0;
            return;
        }

        var gap = w.Target - w.X;
        w.Walking = Math.Abs(gap) > speed;
        if (w.Walking)
        {
            w.X += Math.Sign(gap) * speed;
            return;
        }

        var roll = random.Next(10);
        if (roll < 4)
        {
            w.Target = random.Next(w.Min, w.Max + 1);
        }
        else if (canShake && roll < 6)
        {
            w.Shaking = true;
            w.Ticks = random.Next(25, 50);
        }
        else
        {
            w.Ticks = random.Next(15, 60);
        }
    }

    private void RespawnDust(Mote speck, Random r)
    {
        speck.X = r.Next(Math.Min(comedianMin, spotX), Math.Max(comedianMin, spotX) + 1);
        speck.Y = r.Next(spotY + 3, Math.Max(spotY + 4, stageTop - 1));
        speck.Vx = (r.NextDouble() - 0.5) * 0.06;
        speck.Vy = 0.02 + (r.NextDouble() * 0.04);
    }

    private void RenderVenue()
    {
        Array.Copy(sky, frame, frame.Length);

        foreach (var (x, y, phase) in stars)
        {
            Tint(x, y, 0xFFFFFFFF, 0.35 + (0.35 * Math.Sin((tick * 0.09) + phase)));
        }

        DrawGauges();
        foreach (var lamp in lamps)
        {
            var lit = (tick + lamp.Offset) / lamp.Period % 2 == 0;
            Plot(lamp.X, lamp.Y, lit ? lamp.On : lamp.Off);
            if (lit)
            {
                Cross(lamp.X, lamp.Y, lamp.On, 0.3);
            }
        }

        DrawNeons();
        Overlay(land);

        DrawComedian();
        DrawBartender();
        foreach (var patron in patrons)
        {
            if (!patron.OnStool)
            {
                DrawPatron(patron);
            }
        }

        Overlay(fore);
        foreach (var patron in patrons)
        {
            if (patron.OnStool)
            {
                DrawPatron(patron);
            }
        }

        DrawWaiter();
        DrawSpotlight();
        DrawPendants();
        DrawBulbs();
        DrawCandles();
        DrawLaughs();
    }

    private void Overlay(uint[] layer)
    {
        for (var i = 0; i < frame.Length; i++)
        {
            if (layer[i] != 0)
            {
                frame[i] = layer[i];
            }
        }
    }

    // Needles that swing slowly and tremble a little, as on a plant still under load.
    private void DrawGauges()
    {
        foreach (var (x, y, length, phase) in gauges)
        {
            var angle = (-Math.PI / 2) + (0.9 * Math.Sin((tick * 0.03) + phase)) + ((random.NextDouble() - 0.5) * 0.15);
            for (var k = 1; k <= length; k++)
            {
                Plot(x + (int)Math.Round(Math.Cos(angle) * k), y + (int)Math.Round(Math.Sin(angle) * k), 0xFFC0281E);
            }

            Plot(x, y, 0xFF2A2A2A);
        }
    }

    private void DrawNeons()
    {
        foreach (var neon in neons)
        {
            // While a letter flickers, the sign's halo dims a little.
            var level = neon.Flicker > 0 ? 0.8 : 1;
            foreach (var (i, amount) in neon.Glow)
            {
                frame[i] = Mix(frame[i], neon.Color, amount * level);
            }

            foreach (var (x, y, letter) in neon.Pixels)
            {
                var lit = neon.Flicker == 0 || letter != neon.Letter || random.Next(3) == 0;
                Plot(x, y, lit ? neon.Core : neon.Off);
            }
        }
    }

    private void DrawComedian()
    {
        var left = (int)Math.Round(comedianX);
        var top = stageTop - 24;
        var body = pose switch
        {
            Pose.Raise => ComicRaise,
            Pose.Shrug => ComicShrug,
            _ => mouthOpen ? ComicTalkOpen : ComicTalk,
        };

        Stamp(body, left, top, ComicLook);
        Stamp(comedianWalking ? (tick / 3 % 2 == 0 ? LegsStepA : LegsStepB) : LegsStand, left, top + 17, ComicLook);
    }

    private void DrawBartender()
    {
        var left = (int)Math.Round(bartender.X);
        var top = floor - 24;
        Stamp(bartender.Shaking ? BartenderShaking : Bartender, left, top, bartender.Look);
        Stamp(LegsStand, left, top + 17, bartender.Look);
        if (bartender.Shaking)
        {
            var sy = top - 4 + (tick / 2 % 2);
            for (var y = sy; y < sy + 4; y++)
            {
                Plot(left + 10, y, y == sy ? 0xFF6A7078 : 0xFFC8D0D8);
                Plot(left + 11, y, y == sy ? 0xFF4A4E54 : 0xFF8A9098);
            }
        }
    }

    private void DrawWaiter()
    {
        if (waiter is null)
        {
            return;
        }

        var left = (int)Math.Round(waiter.X);
        var top = floor - 24;
        Stamp(Waiter, left, top, waiter.Look);
        Stamp(waiter.Walking ? (tick / 3 % 2 == 0 ? LegsStepA : LegsStepB) : LegsStand, left, top + 17, waiter.Look);
        for (var x = left - 3; x <= left + 1; x++)
        {
            Plot(x, top + 7, 0xFF9AA0A8);
        }

        Plot(left - 2, top + 4, 0xFFF0E8D8);
        Plot(left - 1, top + 4, 0xFFF0E8D8);
        Plot(left - 2, top + 5, 0xFFD08A2A);
        Plot(left - 1, top + 5, 0xFFA86A1A);
        Plot(left - 2, top + 6, 0xFFD08A2A);
        Plot(left - 1, top + 6, 0xFFA86A1A);
    }

    private void DrawPatron(Patron p)
    {
        var nod = laughTicks > 0 && p.Laughs && ((tick / 3) + p.Phase) % 2 == 0 ? 1 : 0;
        Stamp(Seated, p.X, p.Top, p.Look, p.Mirror, headRows: 7, headDrop: nod);
    }

    private void DrawSpotlight()
    {
        var cx = comedianX + 6;
        var start = spotY + 3;
        var length = stageTop - 1 - start;
        if (length <= 0)
        {
            return;
        }

        for (var y = start; y < stageTop; y++)
        {
            var t = (double)(y - start) / length;
            var center = spotX - 2 + ((cx - spotX + 2) * t);
            var half = 1.5 + (12 * t);
            for (var x = (int)(center - half); x <= center + half; x++)
            {
                var edge = 1 - (Math.Abs(x + 0.5 - center) / half);
                if (edge > 0)
                {
                    Tint(x, y, 0xFFFFF2C8, (0.07 + (0.08 * t)) * Math.Sqrt(edge));
                }
            }
        }

        for (var x = (int)cx - 14; x <= cx + 14; x++)
        {
            var edge = 1 - (Math.Abs(x + 0.5 - cx) / 14);
            Tint(x, stageTop, 0xFFFFF2C8, 0.4 * edge);
            Tint(x, stageTop + 1, 0xFFFFF2C8, 0.2 * edge);
        }

        foreach (var speck in dust)
        {
            var x = (int)speck.X;
            var y = (int)speck.Y;
            var t = (double)(y - start) / length;
            var center = spotX - 2 + ((cx - spotX + 2) * t);
            if (t > 0 && t < 1 && Math.Abs(x + 0.5 - center) < 1.5 + (12 * t))
            {
                Tint(x, y, 0xFFFFF8E0, 0.45);
            }
        }
    }

    private void DrawPendants()
    {
        var counter = floor - 15;
        foreach (var (px, py) in pendants)
        {
            var length = Math.Max(1, counter - py);
            for (var y = py + 1; y < counter; y++)
            {
                var t = (double)(y - py) / length;
                var half = 2 + (9 * t);
                for (var x = (int)(px - half); x <= px + half; x++)
                {
                    var edge = 1 - (Math.Abs(x + 0.5 - (px + 0.5)) / half);
                    if (edge > 0)
                    {
                        Tint(x, y, 0xFFFFE0A0, 0.09 * (1 - (0.5 * t)) * Math.Sqrt(edge));
                    }
                }
            }

            foreach (var (dx, dy, weight) in Halo3)
            {
                Tint(px + dx, py + dy, 0xFFFFD890, weight * 0.5);
            }

            Plot(px - 1, py, 0xFFE8B860);
            Plot(px, py, 0xFFFFF0C0);
            Plot(px + 1, py, 0xFFE8B860);
        }
    }

    private void DrawBulbs()
    {
        foreach (var bulb in bulbs)
        {
            if (bulb.Off > 0)
            {
                Plot(bulb.X, bulb.Y, 0xFF5A4020);
                continue;
            }

            var level = 0.8 + (0.2 * Math.Sin((tick * 0.06) + bulb.Phase));
            foreach (var (dx, dy, weight) in Halo3)
            {
                Tint(bulb.X + dx, bulb.Y + dy, 0xFFFFC870, weight * level * 0.55);
            }

            Plot(bulb.X, bulb.Y, Mix(0xFFFFB050, 0xFFFFF0C0, level));
        }
    }

    private void DrawCandles()
    {
        foreach (var (x, y, phase) in candles)
        {
            var level = 0.75 + (0.25 * Math.Sin((tick * 0.4) + phase)) - (random.NextDouble() * 0.15);
            foreach (var (dx, dy, weight) in Halo4)
            {
                Tint(x + dx, y + dy, 0xFFFFB050, weight * level * 0.4);
            }

            Plot(x, y, level > 0.7 ? 0xFFFFD060 : 0xFFFF9A30);
            if (level > 0.85)
            {
                Plot(x, y - 1, 0xFFFFE8A0);
            }
        }
    }

    private void DrawLaughs()
    {
        foreach (var laugh in laughs)
        {
            var amount = 1 - (laugh.Phase / 32);
            var x = (int)laugh.X;
            var y = (int)laugh.Y;
            for (var sy = 0; sy < LaughH.Length; sy++)
            {
                for (var sx = 0; sx < 3; sx++)
                {
                    if (LaughH[sy][sx] == 'X')
                    {
                        Tint(x + sx, y + sy, 0xFFFFF0B0, amount);
                    }

                    if (LaughA[sy][sx] == 'X')
                    {
                        Tint(x + 4 + sx, y + sy, 0xFFFFF0B0, amount);
                    }
                }
            }
        }
    }

    private void Stamp(string[] sprite, int left, int top, Look look, bool mirror = false, int headRows = 0, int headDrop = 0)
    {
        var width = sprite[0].Length;
        for (var sy = 0; sy < sprite.Length; sy++)
        {
            var y = top + sy + (sy < headRows ? headDrop : 0);
            for (var sx = 0; sx < width; sx++)
            {
                var color = look.Color(sprite[sy][sx]);
                if (color != 0)
                {
                    Plot(mirror ? left + width - 1 - sx : left + sx, y, color);
                }
            }
        }
    }

    private void Put(uint[] layer, int x, int y, uint color)
    {
        if (Inside(x, y))
        {
            layer[(y * cols) + x] = color;
        }
    }

    private static Look RandomLook(Random r)
    {
        var (light, shade) = Skins[r.Next(Skins.Length)];
        var shirt = ShirtColors[r.Next(ShirtColors.Length)];
        return new Look(HairColors[r.Next(HairColors.Length)], light, shade, shirt, Mix(shirt, 0xFF000000, 0.3), PantsColors[r.Next(PantsColors.Length)], 0xFF161214, r.Next(3) == 0);
    }

    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));

    private static int Hash(int a, int b) => (int)(unchecked((uint)(a * 73856093) ^ (uint)(b * 19349663)) % 9973);

    private static (int Dx, int Dy, double Weight)[] Halo(int radius)
    {
        var halo = new List<(int, int, double)>();
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                var d = Math.Sqrt((dx * dx) + (dy * dy));
                if (d > 0 && d <= radius + 0.5)
                {
                    halo.Add((dx, dy, Math.Pow(1 - (d / (radius + 0.8)), 2)));
                }
            }
        }

        return [.. halo];
    }

    private sealed record Look(uint Hair, uint Skin, uint SkinShade, uint Shirt, uint ShirtShade, uint Pants, uint Shoes, bool LongHair = false)
    {
        public uint Color(char c) => c switch
        {
            'H' => Hair,
            'L' => LongHair ? Hair : 0,
            'S' => Skin,
            's' => SkinShade,
            'E' => 0xFF1A1012,
            'C' => Shirt,
            'c' => ShirtShade,
            'P' => Pants,
            'B' => Shoes,
            'T' => 0xFFE6DFCC,
            'M' => 0xFF18181A,
            'm' => 0xFF9AA2AA,
            'W' => 0xFFE4E0D6,
            'V' => 0xFF22262E,
            'R' => 0xFFB0242E,
            'A' => 0xFF2A2A30,
            _ => 0,
        };

        // The audience sits in the dim light of the hall: colours fade toward the dark.
        public Look Dimmed() => new(Dim(Hair), Dim(Skin), Dim(SkinShade), Dim(Shirt), Dim(ShirtShade), Dim(Pants), Dim(Shoes), LongHair);

        private static uint Dim(uint color) => Mix(color, 0xFF140E18, 0.25);
    }

    private sealed class Patron
    {
        public int X;
        public int Top;
        public bool Mirror;
        public bool OnStool;
        public bool Laughs;
        public int Phase;
        public Look Look = ComicLook;
    }

    private sealed class Walker
    {
        public double X;
        public double Target;
        public int Min;
        public int Max;
        public int Ticks;
        public bool Walking;
        public bool Shaking;
        public Look Look = ComicLook;
    }

    private sealed class Bulb
    {
        public int X;
        public int Y;
        public double Phase;
        public int Off;
    }

    private sealed class Neon
    {
        public uint Color;
        public uint Core;
        public uint Off;
        public int Letters;
        public int Letter;
        public int Flicker;
        public List<(int X, int Y, int Letter)> Pixels = [];
        public (int Index, double Amount)[] Glow = [];
    }

    private readonly record struct Lamp(int X, int Y, uint On, uint Off, int Period, int Offset);
}
