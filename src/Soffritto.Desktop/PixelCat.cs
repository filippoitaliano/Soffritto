using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Soffritto.Desktop;

/// <summary>
/// Easter egg di Ctrl+G: un gattino in pixel art che passeggia avanti e indietro coi piedi sul
/// footer, oppure si ferma a scodinzolare, sbattere le palpebre, sonnecchiare o mandare un cuore.
/// Quando <see cref="NewMessages"/> porta qualcosa di nuovo si ferma e lo dice in un fumetto;
/// con <see cref="Wave"/> saluta con la zampa.
/// </summary>
public sealed class PixelCat : Canvas
{
    // Lo sprite e' una griglia di caratteri, uno per colore della palette, disegnata rivolta a
    // destra. Il contorno scuro non si disegna: Build lo aggiunge attorno a tutto cio' che e'
    // pieno, cosi' zampe e coda che si spostano restano sempre contornate.
    // Due colonne oltre il muso: servono alla zampa del saluto e al suo contorno.
    private const int SpriteWidth = 28;
    private const int SpriteHeight = 24;
    private const int CatTop = 6;
    private const int PixelSize = 3;

    // Colonna centrale della testa nello sprite rivolto a destra: la codina del fumetto punta li'.
    private const double HeadCenter = 19.5;

    // Il fumetto finisce sopra le orecchie (riga 18 px dello sprite) con la punta della codina.
    private const double BubbleBottom = 6;
    private const int ListenEveryTicks = 8;

    private static readonly Dictionary<char, uint> Palette = new()
    {
        ['K'] = 0xFF241C2E,
        ['E'] = 0xFF241C2E,
        ['O'] = 0xFFEE9A3D,
        ['D'] = 0xFFC8692A,
        ['L'] = 0xFFF7C98B,
        ['C'] = 0xFFF8DDB5,
        ['P'] = 0xFFEE9C9C,
        ['W'] = 0xFFFFFFFF,
        ['H'] = 0xFFE5486A,
    };

    private static readonly string[] Head =
    [
        ".O.....O.",
        "OPO...OPO",
        "OPOOOOOPO",
        "OODODODOO",
        "OEWOOOEWO",
        "OEEOOOEEO",
        "CCCCPCCCC",
        "CCCECECCC",
        ".CCCCCCC.",
    ];

    private static readonly string[] Interjections = ["Miao!", "Psst…", "Mrrr…", "Ehi!"];

    private static readonly string[] Greetings =
    [
        "Ciao! Vengo bene in foto?",
        "Cheese!",
        "Prendimi dal lato buono, quello col baffo.",
        "Ciao! Per gli autografi c'è la coda.",
        "Foto? Arrivo, mi sistemo il pelo.",
    ];

    // La zampa del saluto, al posto della zampa anteriore vicina: il braccio esce dal petto e sale
    // accanto alla faccia, staccato di una riga e di una colonna perche' il contorno li separi.
    // Le due pose (zampa alta e zampa bassa) si alternano ogni due giri.
    private static readonly (int X, int Y, char C)[][] WavePaw = [WavePose(pawTop: 4), WavePose(pawTop: 7)];

    private const int WaveDuration = 26;
    private const int WaveOdds = 3;

    private static readonly string[] Heart =
    [
        ".H.H.",
        "HHHHH",
        ".HHH.",
        "..H..",
    ];

    private static readonly (int X, int Y)[] TailUp =
        [(3, 6), (4, 6), (3, 5), (4, 5), (2, 4), (3, 4), (2, 3), (3, 3), (2, 2), (3, 2), (3, 1)];

    private static readonly (int X, int Y)[] TailBent =
        [(3, 6), (4, 6), (3, 5), (4, 5), (3, 4), (4, 4), (4, 3), (5, 3), (4, 2), (5, 2), (5, 1)];

    private static readonly (int X, int Y)[] Stripes =
        [(8, 5), (8, 6), (11, 5), (11, 6), (11, 7), (14, 5), (14, 6), (6, 8), (6, 9)];

    // Le zampe lontane vanno prima, cosi' quelle vicine le coprono. Nella camminata le coppie
    // diagonali si muovono insieme e quella che torna avanti si alza di un pixel.
    private static readonly Leg[] IdleLegs = [new(8, false, false), new(13, false, false), new(6, true, false), new(15, true, false)];

    private static readonly Leg[][] WalkLegs =
    [
        [new(5, false, false), new(16, false, false), new(8, true, false), new(13, true, false)],
        [new(6, false, true), new(15, false, false), new(7, true, false), new(14, true, true)],
        [new(8, false, false), new(13, false, false), new(5, true, false), new(16, true, false)],
        [new(7, false, false), new(14, false, true), new(6, true, true), new(15, true, false)],
    ];

    private readonly Dictionary<(int Phase, bool TailUp, bool EyesClosed, int HeartY, int Wave), BitmapSource> frames = [];
    private readonly Image sprite = new()
    {
        Width = SpriteWidth * PixelSize,
        Height = SpriteHeight * PixelSize,
        RenderTransformOrigin = new Point(0.5, 0.5),
    };

    private readonly ScaleTransform flip = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Random random = new();
    private readonly Queue<string> messages = new();
    private Func<IReadOnlyList<string>>? newMessages;
    private readonly Border bubble;
    private readonly TextBlock bubbleText;
    private readonly Polygon bubbleTail;
    private readonly Polygon bubbleTailFill;

    private bool walking;
    private bool sleeping;
    private int direction = 1;
    private double x;
    private int phase;
    private int tick;
    private int ticksLeft;
    private int blinkTicks;
    private int heartTicks;
    private int speakTicks;
    private int waveTicks;

    public PixelCat()
    {
        Height = sprite.Height;
        IsHitTestVisible = false;

        // Hidden e non Collapsed: il Canvas resta misurato e alla prima comparsa conosce gia'
        // la larghezza su cui camminare.
        Visibility = Visibility.Hidden;
        sprite.RenderTransform = flip;
        RenderOptions.SetBitmapScalingMode(sprite, BitmapScalingMode.NearestNeighbor);
        Children.Add(sprite);

        var ink = new SolidColorBrush(Color.FromRgb(0x24, 0x1C, 0x2E));
        ink.Freeze();
        bubbleText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 260, FontSize = 12, Foreground = ink };
        bubble = new Border
        {
            Background = Brushes.White,
            BorderBrush = ink,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 7),
            Child = bubbleText,
            Visibility = Visibility.Collapsed,
        };

        // Due triangoli sovrapposti: quello scuro fa il bordo della codina, quello bianco copre
        // il tratto di bordo del fumetto dove la codina si attacca. Il bianco parte 3 px dentro il
        // fumetto, sugli stessi lati: se partisse a filo del bordo, l'antialiasing lascerebbe
        // una riga scura tra fumetto e codina.
        bubbleTail = new Polygon { Fill = ink, Points = [new(0, 0), new(14, 0), new(3, 11)], Visibility = Visibility.Collapsed };
        bubbleTailFill = new Polygon { Fill = Brushes.White, Points = [new(1.3, -3), new(14.2, -3), new(3.6, 7)], Visibility = Visibility.Collapsed };
        Children.Add(bubble);
        Children.Add(bubbleTail);
        Children.Add(bubbleTailFill);

        timer.Tick += (_, _) => Step();
    }

    /// <summary>Le segnalazioni nuove da dire: interrogata circa una volta al secondo mentre il gattino e' visibile.</summary>
    public Func<IReadOnlyList<string>>? NewMessages
    {
        get => newMessages;
        set
        {
            // Una fonte nuova (la demo di Ctrl+D e ritorno): cio' che restava da dire non vale piu'.
            newMessages = value;
            messages.Clear();
            speakTicks = 0;
            ShowBubble(false);
        }
    }

    public bool IsActive => timer.IsEnabled;

    public void Toggle()
    {
        if (timer.IsEnabled)
        {
            timer.Stop();
            speakTicks = 0;
            ShowBubble(false);
            Visibility = Visibility.Hidden;
            return;
        }

        x = Math.Max(0, (ActualWidth - sprite.Width) / 2);
        walking = false;
        sleeping = false;
        ticksLeft = 15;
        heartTicks = 12;
        Visibility = Visibility.Visible;
        Listen();
        Step();
        timer.Start();
    }

    /// <summary>
    /// Saluta con la zampa e un fumetto, solo se e' gia' visibile e solo una volta su
    /// <see cref="WaveOdds"/>. Se sta dicendo una segnalazione saluta senza fumetto, per non coprirla.
    /// </summary>
    public void Wave()
    {
        if (!timer.IsEnabled || random.Next(WaveOdds) != 0)
        {
            return;
        }

        walking = false;
        sleeping = false;
        heartTicks = 0;
        waveTicks = WaveDuration;

        if (speakTicks == 0)
        {
            Speak(Greetings[random.Next(Greetings.Length)]);
            speakTicks = WaveDuration + 10;
        }

        Step();
    }

    private void Step()
    {
        tick++;
        if (tick % ListenEveryTicks == 0)
        {
            Listen();
        }

        if (speakTicks > 0)
        {
            if (--speakTicks == 0)
            {
                ShowBubble(false);
                ticksLeft = messages.Count > 0 ? 6 : random.Next(15, 35);
            }
        }
        else if (--ticksLeft <= 0)
        {
            if (messages.Count > 0)
            {
                Speak(messages.Dequeue());
            }
            else
            {
                ChooseNext();
            }
        }

        // Il saluto vince sulla prossima mossa scelta qui sopra: resta fermo finche' agita la zampa.
        var wave = -1;
        if (waveTicks > 0)
        {
            waveTicks--;
            walking = false;
            sleeping = false;
            wave = waveTicks / 2 % 2;

            if (waveTicks == 0 && speakTicks == 0)
            {
                ticksLeft = random.Next(10, 20);
            }
        }

        var maxX = Math.Max(0, ActualWidth - sprite.Width);
        if (walking)
        {
            phase = (phase + 1) % WalkLegs.Length;
            x += direction * PixelSize;
            if (x <= 0 || x >= maxX)
            {
                direction = x <= 0 ? 1 : -1;
            }
        }

        x = Math.Clamp(x, 0, maxX);

        if (blinkTicks > 0)
        {
            blinkTicks--;
        }
        else if (!sleeping && random.Next(30) == 0)
        {
            blinkTicks = 2;
        }

        if (heartTicks > 0)
        {
            heartTicks--;
        }

        var tailUp = walking ? phase < 2 : tick / (sleeping ? 8 : 4) % 2 == 0;
        var heartY = heartTicks == 0 ? 0 : heartTicks > 6 ? 2 : 1;
        sprite.Source = Frame(walking ? phase : -1, tailUp, sleeping || blinkTicks > 0, heartY, wave);
        flip.ScaleX = direction;
        SetLeft(sprite, x);
        PlaceBubble();
    }

    private void Listen()
    {
        var fresh = NewMessages?.Invoke() ?? [];
        if (fresh.Count == 0)
        {
            return;
        }

        foreach (var message in fresh)
        {
            messages.Enqueue(message);
        }

        // Smette subito di camminare o di dormire: chi ha qualcosa da dire si ferma a dirlo.
        if (speakTicks == 0)
        {
            ticksLeft = Math.Min(ticksLeft, 2);
        }
    }

    private void Speak(string message)
    {
        walking = false;
        sleeping = false;
        heartTicks = 0;
        speakTicks = Math.Clamp(28 + (message.Length / 3), 32, 75);

        bubbleText.Inlines.Clear();
        bubbleText.Inlines.Add(new Bold(new Run(Interjections[random.Next(Interjections.Length)])));
        bubbleText.Inlines.Add(new Run(" " + message));
        ShowBubble(true);
    }

    private void ShowBubble(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        bubble.Visibility = visibility;
        bubbleTail.Visibility = visibility;
        bubbleTailFill.Visibility = visibility;
    }

    private void PlaceBubble()
    {
        if (speakTicks == 0)
        {
            return;
        }

        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = bubble.DesiredSize;
        var headX = x + ((direction > 0 ? HeadCenter : SpriteWidth - HeadCenter) * PixelSize);
        var left = Math.Clamp(headX - 30, 0, Math.Max(0, ActualWidth - size.Width));
        var tailX = Math.Clamp(headX - 3 - left, 8, Math.Max(8, size.Width - 22));

        SetLeft(bubble, left);
        SetTop(bubble, BubbleBottom - size.Height);
        SetLeft(bubbleTail, left + tailX);
        SetTop(bubbleTail, BubbleBottom - 2);
        SetLeft(bubbleTailFill, left + tailX);
        SetTop(bubbleTailFill, BubbleBottom - 2);
    }

    private void ChooseNext()
    {
        walking = !walking;
        sleeping = false;

        if (walking)
        {
            ticksLeft = random.Next(25, 80);
            if (random.Next(2) == 0)
            {
                direction = -direction;
            }

            return;
        }

        sleeping = random.Next(4) == 0;
        ticksLeft = random.Next(20, 60) + (sleeping ? 40 : 0);
        if (!sleeping && random.Next(3) == 0)
        {
            heartTicks = 12;
        }
    }

    private BitmapSource Frame(int walkPhase, bool tailUp, bool eyesClosed, int heartY, int wave)
    {
        var key = (walkPhase, tailUp, eyesClosed, heartY, wave);
        if (!frames.TryGetValue(key, out var frame))
        {
            frame = Build(walkPhase, tailUp, eyesClosed, heartY, wave);
            frames[key] = frame;
        }

        return frame;
    }

    private static BitmapSource Build(int walkPhase, bool tailUp, bool eyesClosed, int heartY, int wave)
    {
        var grid = new char[SpriteHeight, SpriteWidth];
        for (var y = 0; y < SpriteHeight; y++)
        {
            for (var x = 0; x < SpriteWidth; x++)
            {
                grid[y, x] = '.';
            }
        }

        void Put(int x, int y, char c) => grid[y + CatTop, x] = c;

        var tail = tailUp ? TailUp : TailBent;
        for (var i = 0; i < tail.Length; i++)
        {
            Put(tail[i].X, tail[i].Y, i >= tail.Length - 2 ? 'D' : 'O');
        }

        for (var y = 5; y <= 11; y++)
        {
            for (var x = y == 5 ? 6 : 5; x <= (y >= 10 ? 17 : 15); x++)
            {
                Put(x, y, 'O');
            }
        }

        foreach (var (x, y) in Stripes)
        {
            Put(x, y, 'D');
        }

        for (var y = 9; y <= 11; y++)
        {
            for (var x = 15; x <= 17; x++)
            {
                Put(x, y, 'C');
            }
        }

        for (var r = 0; r < Head.Length; r++)
        {
            for (var c = 0; c < Head[r].Length; c++)
            {
                var ch = Head[r][c];
                if (ch == '.')
                {
                    continue;
                }

                if (eyesClosed && r is 4 or 5 && c is 1 or 2 or 6 or 7)
                {
                    ch = r == 4 ? 'O' : 'E';
                }

                Put(15 + c, 1 + r, ch);
            }
        }

        foreach (var leg in walkPhase < 0 ? IdleLegs : WalkLegs[walkPhase])
        {
            if (wave >= 0 && leg.Near && leg.X == 15)
            {
                foreach (var (px, py, pc) in WavePaw[wave])
                {
                    Put(px, py, pc);
                }

                continue;
            }

            var bottom = leg.Lifted ? 15 : 16;
            for (var y = 12; y <= bottom; y++)
            {
                var ch = leg.Near ? (y == bottom ? 'L' : 'O') : 'D';
                Put(leg.X, y, ch);
                Put(leg.X + 1, y, ch);
            }
        }

        if (heartY > 0)
        {
            for (var r = 0; r < Heart.Length; r++)
            {
                for (var c = 0; c < Heart[r].Length; c++)
                {
                    if (Heart[r][c] != '.')
                    {
                        grid[heartY + r, 17 + c] = Heart[r][c];
                    }
                }
            }
        }

        var pixels = new uint[SpriteWidth * SpriteHeight];
        for (var y = 0; y < SpriteHeight; y++)
        {
            for (var x = 0; x < SpriteWidth; x++)
            {
                var ch = grid[y, x];
                if (ch == '.' && TouchesFilled(grid, x, y))
                {
                    ch = 'K';
                }

                pixels[(y * SpriteWidth) + x] = ch == '.' ? 0u : Palette[ch];
            }
        }

        var bitmap = BitmapSource.Create(SpriteWidth, SpriteHeight, 96, 96, PixelFormats.Bgra32, null, pixels, SpriteWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int X, int Y, char C)[] WavePose(int pawTop)
    {
        var pixels = new List<(int X, int Y, char C)>();

        for (var x = 18; x <= 26; x++)
        {
            pixels.Add((x, 11, 'O'));
            pixels.Add((x, 12, 'O'));
        }

        for (var y = pawTop; y <= 10; y++)
        {
            var ch = y < pawTop + 2 ? 'L' : 'O';
            pixels.Add((25, y, ch));
            pixels.Add((26, y, ch));
        }

        return [.. pixels];
    }

    private static bool TouchesFilled(char[,] grid, int x, int y) =>
        (x > 0 && grid[y, x - 1] != '.')
        || (x < SpriteWidth - 1 && grid[y, x + 1] != '.')
        || (y > 0 && grid[y - 1, x] != '.')
        || (y < SpriteHeight - 1 && grid[y + 1, x] != '.');

    private readonly record struct Leg(int X, bool Near, bool Lifted);
}
