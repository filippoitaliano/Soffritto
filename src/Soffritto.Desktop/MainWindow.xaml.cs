using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly WindowsNotifier notifier;
    private readonly AppSettings settings = AppSettings.Current;
    private DemoViewModel? demo;
    private readonly bool hasConfiguration;
    private SnipShortcutHook? snipHook;
    private bool exiting;

    // Col gradiente il colore viene dal tema (Theme.ChromeGradient): espresso di giorno, piu' scuro di notte.
    private readonly SolidColorBrush chrome = new(Color.FromRgb(0x3A, 0x31, 0x28));

    /// <param name="setupNotice">Cosa e' successo alla configurazione all'avvio (import, errori): lo dice il setup.</param>
    /// <param name="openSetup">Nessuna configurazione usabile: il setup si apre subito e non si chiude.</param>
    /// <param name="firstRun">Prima apertura dopo il Setup.exe: il setup si apre sulla scheda Componenti.</param>
    public MainWindow(MainViewModel viewModel, string? setupNotice, bool openSetup, bool firstRun = false)
    {
        this.viewModel = viewModel;
        hasConfiguration = !openSetup;
        DataContext = viewModel;
        InitializeComponent();
        Footer.Background = chrome;
        // La barra del titolo segue il footer anche durante la dissolvenza: il pennello animato
        // notifica Changed a ogni fotogramma.
        chrome.Changed += (_, _) => TitleBar.SetCaption(this, chrome.Color);
        Overlay.HeaderBackground = chrome;
        Scenery.AppearanceChanged += () =>
        {
            AnimateChrome();
            ThemeManager.SetSceneryDark(Scenery.IsDark);
        };
        ThemeManager.Changed += AnimateChrome;
        ThemeManager.Start();
        chrome.Color = ChromeTarget();
        Setup.CanClose = hasConfiguration;
        Setup.CloseRequested += HideSetup;
        PullRequests.DataContext = viewModel.PullRequests;
        Notifications.DataContext = viewModel.Notifications;
        Meeting.DataContext = viewModel.Meeting;
        Meeting.CloseRequested += HideMeeting;

        // Gattino e Windows ascoltano le notifiche ognuno per conto suo: il gattino le dice solo
        // quando e' visibile, Windows anche a finestra nascosta.
        Cat.NewMessages = viewModel.Notifications.Listen();
        viewModel.Notifications.Opened += ShowInPullRequests;
        notifier = new WindowsNotifier(this, viewModel.WindowTitle, viewModel.Notifications);
        notifier.ExitRequested += Exit;
        notifier.NotificationOpened += () =>
        {
            notifier.BringToFront();
            ShowNotifications();
        };

        SourceInitialized += (_, _) => TitleBar.Apply(this, chrome.Color);
        Loaded += (_, _) =>
        {
            // WindowState="Maximized" nello XAML a volte non viene rispettato all'avvio (la
            // finestra parte normale o ridotta a icona): lo si riapplica a finestra caricata.
            WindowState = WindowState.Maximized;
            viewModel.Start();
            notifier.Start();

            // Win+Shift+S: il gattino, se visibile, saluta chi fa lo screenshot. La callback
            // dell'hook deve tornare subito a Windows: il saluto parte al giro dopo.
            snipHook = new SnipShortcutHook();
            snipHook.Pressed += () => Dispatcher.BeginInvoke(Cat.Wave);

            // Gattino e paesaggio misurano la finestra all'accensione: si applicano a layout fatto.
            ApplySettings();
            settings.PropertyChanged += OnSettingsChanged;

            if (openSetup || setupNotice is not null || firstRun)
            {
                ShowSetup(setupNotice);
                if (firstRun)
                {
                    Setup.ShowComponents();
                }
            }
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            settings.PropertyChanged -= OnSettingsChanged;
            snipHook?.Dispose();
            notifier.Dispose();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        PullRequests.CloseRequested += HidePullRequests;
        PullRequests.SwitchSideRequested += SwitchPullRequestsSide;
        Notifications.CloseRequested += HideNotifications;
        Docker.CloseRequested += HideDocker;
        Tomato.CloseRequested += HideTomato;
    }

    // Il «+» della testata: il riquadro di creazione rilegge numero e branch a ogni apertura.
    private void OpenDocker_Click(object sender, RoutedEventArgs e)
    {
        if (demo is not null)
        {
            return;
        }

        if (Docker.Visibility == Visibility.Visible)
        {
            HideDocker();
            return;
        }

        HidePullRequests();
        HideNotifications();
        HideTomato();
        ShowFloating(Docker);
        _ = viewModel.Docker.OpenCreateAsync();
    }

    private void HideDocker() => HideFloating(Docker);

    private Color ChromeTarget() => Scenery.ChromeColor ?? (Color)FindResource("Theme.ChromeGradient");

    private void AnimateChrome() => chrome.BeginAnimation(
        SolidColorBrush.ColorProperty,
        new ColorAnimation(ChromeTarget(), TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        });

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => ApplySettings();

    // Le scorciatoie e le preferenze del setup scrivono le impostazioni; qui si allinea cio' che si vede.
    private void ApplySettings()
    {
        if (settings.ShowCat != Cat.IsActive)
        {
            Cat.Toggle();
        }

        Scenery.SetRotation(settings.Sceneries);
        if (settings.ShowScenery != Scenery.IsActive)
        {
            Scenery.Toggle();
        }

        if (settings.IsDemo != (demo is not null))
        {
            ToggleDemo();
        }
    }

    public void BringToFront() => notifier.BringToFront();

    /// <summary>Il setup ha salvato una configurazione nuova: l'exe va riaperto.</summary>
    public event Action? RestartRequested;

    /// <summary>Chiude come «Esci» (con la conferma se c'e' una scrittura in corso). Falso se l'utente ha annullato.</summary>
    public bool ExitForRestart()
    {
        Exit();
        return !IsVisible;
    }

    private void OpenSetup_Click(object sender, RoutedEventArgs e) => ShowSetup(null);

    // Ogni apertura riparte dalla configurazione attiva: cio' che non si era salvato si perde.
    private void ShowSetup(string? notice)
    {
        Setup.DataContext = new SetupViewModel(ActiveConfiguration.Current, notice, () =>
        {
            // Un riavvio a meta' di «Allinea seed» o di una creazione lascerebbe container e worktree a meta'.
            if (viewModel.Docker.IsWorking)
            {
                return "c'e' un'operazione Docker in corso (Allinea seed, creazione di un ambiente o pool dei tunnel): l'exe non si riavvia ora; a operazione finita premi di nuovo Salva";
            }

            RestartRequested?.Invoke();
            return null;
        })
        {
            Pool = viewModel.Docker,
        };
        SetupBackdrop.Visibility = Visibility.Visible;
        SetupBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    private void HideSetup()
    {
        if (!hasConfiguration)
        {
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            SetupBackdrop.Visibility = Visibility.Collapsed;
            SetupBackdrop.BeginAnimation(OpacityProperty, null);
            Setup.DataContext = null;
        };

        SetupBackdrop.BeginAnimation(OpacityProperty, fade);
    }

    // Chiudere la riunione la nasconde soltanto: riaprendola si ritrova il turno e il timer.
    // Il tema della riunione si cambia dentro la riunione, ma ogni apertura riparte da quello dell'exe.
    private void OpenMeeting_Click(object sender, RoutedEventArgs e)
    {
        (demo?.Meeting ?? viewModel.Meeting).IsLight = !ThemeManager.IsDark;
        PullRequests.Visibility = Visibility.Collapsed;
        Notifications.Visibility = Visibility.Collapsed;
        MeetingBackdrop.Visibility = Visibility.Visible;
        MeetingBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    private void HideMeeting()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            MeetingBackdrop.Visibility = Visibility.Collapsed;
            MeetingBackdrop.BeginAnimation(OpacityProperty, null);
        };

        MeetingBackdrop.BeginAnimation(OpacityProperty, fade);
    }

    // La voce di menu apre e chiude il pannello. Le PR le legge il refresh globale: aprirlo e'
    // immediato.
    private void OpenPullRequests_Click(object sender, RoutedEventArgs e)
    {
        if (PullRequests.Visibility == Visibility.Visible)
        {
            HidePullRequests();
            return;
        }

        HideNotifications();
        HideTomato();
        HideDocker();
        ShowPullRequests();
    }

    private void OpenNotifications_Click(object sender, RoutedEventArgs e)
    {
        if (Notifications.Visibility == Visibility.Visible)
        {
            HideNotifications();
            return;
        }

        ShowNotifications();
    }

    private void ShowNotifications()
    {
        if (Notifications.Visibility == Visibility.Visible)
        {
            return;
        }

        HidePullRequests();
        HideTomato();
        HideDocker();
        ShowFloating(Notifications);
    }

    private void HideNotifications() => HideFloating(Notifications);

    // La card si cerca nell'insieme che si sta guardando (vero o demo). Lo scroll aspetta il
    // layout: il pannello e il corpo della card appena aperta non sono ancora misurati.
    private void ShowInPullRequests(PullRequestNotification notification)
    {
        HideNotifications();
        if (PullRequests.Visibility != Visibility.Visible)
        {
            ShowPullRequests();
        }

        var group = (demo?.PullRequests ?? viewModel.PullRequests).Highlight(notification.PullRequestId);
        if (group is not null)
        {
            Dispatcher.BeginInvoke(() => PullRequests.Reveal(group), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // Il lato vale finche' l'exe resta aperto: all'avvio il pannello torna a sinistra. Il
    // pannello passa subito all'altro lato e lo scivolamento parte dalla posizione vecchia:
    // attraversa la finestra accelerando e frenando.
    private void SwitchPullRequestsSide()
    {
        var parent = (UIElement)PullRequests.Parent;
        var from = PullRequests.TranslatePoint(new Point(0, 0), parent).X;

        var toLeft = PullRequests.HorizontalAlignment == HorizontalAlignment.Right;
        PullRequests.HorizontalAlignment = toLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PullRequests.Margin = toLeft ? new Thickness(8, 8, 0, 8) : new Thickness(0, 8, 8, 8);
        PullRequests.RenderTransformOrigin = new Point(toLeft ? 0 : 1, 0);
        PullRequests.UpdateLayout();

        var slide = (TranslateTransform)((TransformGroup)PullRequests.RenderTransform).Children[1];
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        var to = PullRequests.TranslatePoint(new Point(0, 0), parent).X;

        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from - to, 0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void ShowPullRequests() => ShowFloating(PullRequests);

    private bool tomatoOpen;

    private void OpenTomato_Click(object sender, RoutedEventArgs e)
    {
        if (tomatoOpen)
        {
            HideTomato();
            return;
        }

        HidePullRequests();
        HideNotifications();
        tomatoOpen = true;
        Tomato.IsHitTestVisible = true;
        Tomato.EnsurePage();
        ShowFloating(Tomato);
    }

    // Il pomodoro non si nasconde come gli altri pannelli: con Collapsed Chromium considera la
    // pagina nascosta e dopo qualche minuto rallenta i suoi timer, e l'allarme potrebbe suonare
    // in ritardo. Resta visibile ma trasparente e non cliccabile, e il focus torna alla finestra
    // perche' la tastiera non finisca in una pagina che non si vede.
    private void HideTomato()
    {
        if (!tomatoOpen)
        {
            return;
        }

        tomatoOpen = false;
        Tomato.IsHitTestVisible = false;
        if (Tomato.IsKeyboardFocusWithin)
        {
            Keyboard.Focus(this);
        }

        Tomato.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
    }

    private void HidePullRequests() => HideFloating(PullRequests);

    // I pannelli galleggianti (PR e notifiche) entrano con una piccola scala e uno scivolamento
    // dal loro lato, ed escono sfumando: abbastanza per sentirli morbidi, non tanto da far aspettare.
    private static void ShowFloating(FrameworkElement panel)
    {
        panel.Visibility = Visibility.Visible;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        var transforms = (TransformGroup)panel.RenderTransform;
        var scale = (ScaleTransform)transforms.Children[0];
        var slide = (TranslateTransform)transforms.Children[1];

        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(panel.HorizontalAlignment == HorizontalAlignment.Left ? -24 : 24, 0, duration) { EasingFunction = ease });
    }

    // Un pannello gia' chiuso resta com'e': la sfumatura partirebbe da opacita' piena.
    private static void HideFloating(FrameworkElement panel)
    {
        if (panel.Visibility != Visibility.Visible)
        {
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            panel.Visibility = Visibility.Collapsed;
            panel.BeginAnimation(OpacityProperty, null);
        };

        panel.BeginAnimation(OpacityProperty, fade);
    }

    // Un clic fuori da un pannello lo chiude e basta: non arriva a cio' che sta sotto, cosi'
    // chiudere non avvia per sbaglio un'azione di una card. Il bottone che lo apre resta escluso:
    // chiude da se'. Il menu «Apri in» e' un popup, fuori da questa finestra.
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SetupBackdrop.Visibility == Visibility.Visible || MeetingBackdrop.Visibility == Visibility.Visible)
        {
            return;
        }

        // I bottoni dei due pannelli passano sempre: aprono il loro e chiudono l'altro.
        var onPanelButton = PullRequestsButton.IsMouseOver || NotificationsButton.IsMouseOver || TomatoButton.IsMouseOver || NewDockerButton.IsMouseOver;

        // Il riquadro «Nuovo ambiente Docker» non si chiude con un clic fuori: la creazione deve
        // restare visibile mentre si guardano le colonne. Lo chiudono X, Esc e il «+».

        if (PullRequests.Visibility == Visibility.Visible && !onPanelButton && !Contains(PullRequests, e))
        {
            HidePullRequests();
            e.Handled = true;
        }

        if (Notifications.Visibility == Visibility.Visible && !onPanelButton && !Contains(Notifications, e))
        {
            HideNotifications();
            e.Handled = true;
        }

        if (tomatoOpen && !onPanelButton && !Contains(Tomato, e))
        {
            HideTomato();
            e.Handled = true;
        }
    }

    private static bool Contains(FrameworkElement panel, MouseEventArgs e)
    {
        var point = e.GetPosition(panel);
        return point.X >= 0 && point.Y >= 0 && point.X <= panel.ActualWidth && point.Y <= panel.ActualHeight;
    }

    private void OpenPreview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FrontendPreviewViewModel preview)
        {
            Overlay.Open(preview);
        }
    }

    // Preview: la tendina dei branch e' una casella di testo e la scorciatoia deve valere
    // anche quando ha il focus.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc chiude l'anteprima ingrandita: nel browser di Claude non arriva quindi alla pagina,
        // che lo riceve dalla card.
        if (SetupBackdrop.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                HideSetup();
                e.Handled = true;
            }

            return;
        }

        if (MeetingBackdrop.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                HideMeeting();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape && Overlay.IsOpen)
        {
            Overlay.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && PullRequests.Visibility == Visibility.Visible)
        {
            HidePullRequests();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Notifications.Visibility == Visibility.Visible)
        {
            HideNotifications();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && tomatoOpen)
        {
            HideTomato();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Docker.Visibility == Visibility.Visible)
        {
            HideDocker();
            e.Handled = true;
        }
        else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            settings.ShowCat = !settings.ShowCat;
            e.Handled = true;
        }
        else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            settings.IsDemo = !settings.IsDemo;
            e.Handled = true;
        }
        else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            settings.ShowScenery = !settings.ShowScenery;
            e.Handled = true;
        }
    }

    // Le letture vere continuano sotto: tornando dalla demo la finestra le ritrova aggiornate.
    // Pannello, badge e gattino seguono le notifiche dell'insieme che si sta guardando; Windows
    // parla solo delle notifiche vere, quindi tace finche' dura la demo.
    private void ToggleDemo()
    {
        Overlay.Close();

        if (demo is null)
        {
            demo = new DemoViewModel(viewModel.AppVersion);
            DataContext = demo;
            PullRequests.DataContext = demo.PullRequests;
            Notifications.DataContext = demo.Notifications;
            Meeting.DataContext = demo.Meeting;
            Cat.NewMessages = demo.Notifications.Listen();
            demo.Notifications.Opened += ShowInPullRequests;
            notifier.IsPaused = true;
            demo.Start();
        }
        else
        {
            demo.Stop();
            demo = null;
            DataContext = viewModel;
            PullRequests.DataContext = viewModel.PullRequests;
            Notifications.DataContext = viewModel.Notifications;
            Meeting.DataContext = viewModel.Meeting;
            Cat.NewMessages = viewModel.Notifications.Listen();
            notifier.IsPaused = false;
        }
    }

    // In prima riga: bottoni dei pannelli, chip, risorse della macchina e comandi. Se non ci stanno,
    // chip e risorse scendono insieme nella seconda riga (chip a sinistra, risorse a destra); le
    // risorse vanno nella terza solo se accanto ai chip non c'e' posto.
    private void TopBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double afterButtons = 12;
        const double gap = 24;
        const double meterSide = 5;
        var width = TopBar.ActualWidth;
        var chips = TopBarChips.ActualWidth;
        var meter = TopBarMeter.ActualWidth + 2 * meterSide;
        var start = TopBarLeft.ActualWidth + afterButtons;

        var oneRow = start + chips + gap + meter + TopBarRight.ActualWidth <= width;
        var meterRow = oneRow ? 0 : chips + gap + meter <= width ? 1 : 2;

        System.Windows.Controls.Grid.SetRow(TopBarChips, oneRow ? 0 : 1);
        TopBarChips.Margin = new Thickness(oneRow ? start : 0, oneRow ? 0 : 8, 0, 0);
        System.Windows.Controls.Grid.SetRow(TopBarMeter, meterRow);
        TopBarMeter.Margin = new Thickness(0, meterRow == 0 ? 0 : 8, meterRow == 0 ? TopBarRight.ActualWidth + meterSide : meterSide, 0);
    }

    // Il chip dell'ambiente nella testata mostra o nasconde la colonna; mostrandola la porta in vista.
    // Lo scroll aspetta il layout: la colonna appena riaperta non e' ancora misurata.
    private void StatusChip_Click(object sender, RoutedEventArgs e) => ToggleColumn((sender as FrameworkElement)?.DataContext);

    private void ChipCardToggle_Click(object sender, RoutedEventArgs e) => ToggleColumn(ChipCard.DataContext);

    private System.Windows.Threading.DispatcherTimer? chipCardOpen;
    private System.Windows.Threading.DispatcherTimer? chipCardClose;
    private FrameworkElement? chipCardTarget;

    // La scheda del chip si apre dopo una breve sosta del mouse; se e' gia' aperta su un altro chip
    // ci si sposta subito, cosi' scorrere i chip non la fa lampeggiare.
    private void StatusChip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement chip)
        {
            return;
        }

        EnsureChipCardTimers();
        chipCardClose!.Stop();
        chipCardTarget = chip;
        if (ChipCard.IsOpen)
        {
            ShowChipCard();
            return;
        }

        chipCardOpen!.Stop();
        chipCardOpen.Start();
    }

    private void ChipCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => chipCardClose?.Stop();

    // Lasciando chip o scheda la chiusura aspetta un attimo: il tempo di passare dall'uno all'altra.
    private void ChipCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        EnsureChipCardTimers();
        chipCardOpen!.Stop();
        chipCardClose!.Stop();
        chipCardClose.Start();
    }

    private void EnsureChipCardTimers()
    {
        if (chipCardOpen is not null)
        {
            return;
        }

        chipCardOpen = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        chipCardOpen.Tick += (_, _) =>
        {
            chipCardOpen.Stop();
            if (chipCardTarget?.IsMouseOver == true)
            {
                ShowChipCard();
            }
        };
        chipCardClose = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        chipCardClose.Tick += (_, _) =>
        {
            chipCardClose.Stop();
            if (chipCardTarget?.IsMouseOver != true && ChipCard.Child?.IsMouseOver != true)
            {
                ChipCard.IsOpen = false;
            }
        };
        ChipCard.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
        Deactivated += (_, _) => ChipCard.IsOpen = false;
    }

    private void ShowChipCard()
    {
        if (chipCardTarget is null)
        {
            return;
        }

        ChipCard.DataContext = chipCardTarget.DataContext;
        ChipCard.PlacementTarget = chipCardTarget;
        if (ChipCard.IsOpen)
        {
            // Una variazione dell'offset fa ricalcolare la posizione sotto il nuovo chip.
            ChipCard.HorizontalOffset = ChipCard.HorizontalOffset == 0 ? 0.01 : 0;
            return;
        }

        ChipCard.IsOpen = true;
    }

    private void ToggleColumn(object? item)
    {
        if (item is not EnvironmentViewModel environment)
        {
            return;
        }

        environment.IsCollapsed = !environment.IsCollapsed;
        if (environment.IsCollapsed)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => (BodyColumns.ItemContainerGenerator.ContainerFromItem(environment) as FrameworkElement)?.BringIntoView(),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Solo «Esci» dell'icona di sistema chiude davvero: la X nasconde la finestra e le letture
    // continuano. Se la chiusura viene annullata la X torna a nascondere.
    private void Exit()
    {
        exiting = true;
        Close();
        exiting = false;
    }

    // Chiudere interrompe l'azione in corso: per un database update vuol dire poter lasciare il
    // database a meta' di una migrazione, quindi la scelta torna a chi chiude.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (viewModel.HasWriteInProgress(out var description))
        {
            notifier.BringToFront();

            var confirmed = ConfirmDialog.Ask(
                this,
                new Soffritto.Presentation.Confirmation(
                    "Operazione di scrittura in corso",
                    "Chiudendo ora l'operazione si interrompe: un dotnet ef database update interrotto può lasciare il database a metà di una migrazione.",
                    [new("In corso", [new(description)])],
                    "Chiudi comunque"));

            if (!confirmed)
            {
                e.Cancel = true;
                return;
            }
        }

        viewModel.Shutdown();
    }
}
