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

    // With the default background the color comes from the theme (Theme.ChromeBackdrop): slate grey by day, darker at night.
    private readonly SolidColorBrush chrome = new(Color.FromRgb(0x3B, 0x3D, 0x3F));

    /// <param name="setupNotice">What happened to the configuration at startup (import, errors): the setup reports it.</param>
    /// <param name="openSetup">No usable configuration: the setup opens right away and cannot be closed.</param>
    /// <param name="firstRun">First opening after Setup.exe: the setup opens on the Components tab.</param>
    public MainWindow(MainViewModel viewModel, string? setupNotice, bool openSetup, bool firstRun = false)
    {
        this.viewModel = viewModel;
        hasConfiguration = !openSetup;
        DataContext = viewModel;
        InitializeComponent();
        // The title bar follows the backdrop during the fade too: the animated brush
        // raises Changed on every frame.
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

        // The kitten and Windows listen to notifications each on their own: the kitten speaks them only
        // when visible, Windows even with the window hidden.
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
            // WindowState="Maximized" in the XAML is sometimes ignored at startup (the window
            // starts normal or minimized): it is reapplied once the window has loaded.
            WindowState = WindowState.Maximized;
            viewModel.Start();
            notifier.Start();

            // Win+Shift+S: the kitten, if visible, waves at whoever takes the screenshot. The hook
            // callback must return to Windows right away: the wave starts on the next round.
            snipHook = new SnipShortcutHook();
            snipHook.Pressed += () => Dispatcher.BeginInvoke(Cat.Wave);

            // The kitten and the scenery measure the window when turned on: they are applied once layout is done.
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

    // The header's “+”: the creation pane reloads number and branches every time it opens.
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

    private Color ChromeTarget() => Scenery.ChromeColor ?? (Color)FindResource("Theme.ChromeBackdrop");

    private void AnimateChrome() => chrome.BeginAnimation(
        SolidColorBrush.ColorProperty,
        new ColorAnimation(ChromeTarget(), TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        });

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => ApplySettings();

    // Shortcuts and setup preferences write the settings; here what is on screen catches up.
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

    /// <summary>The setup saved a new configuration: the exe must be reopened.</summary>
    public event Action? RestartRequested;

    /// <summary>Closes like “Exit” (with confirmation if a write is in progress). False if the user cancelled.</summary>
    public bool ExitForRestart()
    {
        Exit();
        return !IsVisible;
    }

    private void OpenSetup_Click(object sender, RoutedEventArgs e) => ShowSetup(null);

    // Every opening starts again from the active configuration: anything unsaved is lost.
    private void ShowSetup(string? notice)
    {
        Setup.DataContext = new SetupViewModel(ActiveConfiguration.Current, notice, () =>
        {
            // A restart halfway through “Align seed” or a creation would leave containers and worktrees half done.
            if (viewModel.Docker.IsWorking)
            {
                return "a Docker operation is in progress (Align seed, environment creation or tunnel pool): the app won't restart now; when it's done, press Save again";
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

    // Closing the meeting only hides it: reopening it brings back the turn and the timer.
    // The meeting theme is changed inside the meeting, but every opening starts from the app's theme.
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

    // The menu item opens and closes the panel. The global refresh reads the PRs: opening it is
    // instant.
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

    // The card is looked up in the set being viewed (real or demo). Scrolling waits for
    // layout: the panel and the body of the card just expanded are not measured yet.
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

    // The side holds while the app stays open: at startup the panel goes back to the left. The
    // panel switches side at once and the slide starts from the old position:
    // it crosses the window speeding up and slowing down.
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

    // The Pomodoro is not hidden like the other panels: with Collapsed, Chromium treats the
    // page as hidden and after a few minutes throttles its timers, so the alarm could ring
    // late. It stays visible but transparent and not clickable, and focus goes back to the window
    // so the keyboard does not end up in a page nobody can see.
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

    // Floating panels (PRs and notifications) come in with a slight scale and a slide
    // from their side, and fade out: enough to feel smooth, not so much that you wait.
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

    // A panel already closed stays as it is: the fade would start from full opacity.
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

    // A click outside a panel just closes it: it does not reach what lies beneath, so
    // closing never triggers a card action by mistake. The button that opens it is excluded:
    // it closes the panel itself. The “Open in” menu is a popup, outside this window.
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SetupBackdrop.Visibility == Visibility.Visible || MeetingBackdrop.Visibility == Visibility.Visible)
        {
            return;
        }

        // The two panels' buttons always get through: they open their own and close the other.
        var onPanelButton = PullRequestsButton.IsMouseOver || NotificationsButton.IsMouseOver || TomatoButton.IsMouseOver || NewDockerButton.IsMouseOver;

        // The “New Docker environment” pane does not close on an outside click: the creation must
        // stay visible while you look at the columns. X, Esc and the “+” close it.

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

    // Preview: the branch drop-down is a text box and the shortcut must work
    // even when it has focus.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc closes the enlarged preview: so in Claude's browser it never reaches the page,
        // which gets it from the card.
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

    // Real reads go on underneath: coming back from the demo the window finds them up to date.
    // Panel, badge and kitten follow the notifications of the set being viewed; Windows
    // only speaks about real notifications, so it stays quiet while the demo lasts.
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

    // First row: panel buttons, chips, machine resources and commands. If they don't fit,
    // chips and resources drop together to the second row (chips on the left, resources on the right);
    // resources go to the third row only if there is no room next to the chips.
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

    // The environment chip in the header shows or hides the column; showing it scrolls it into view.
    // Scrolling waits for layout: the column just reopened is not measured yet.
    // The chip turns on itself around its vertical axis, like a card flipping over: it closes to
    // a line, the column toggles while it is edge-on (so the new look appears with the turn), and it
    // reopens with a slight overshoot. A click during the turn is ignored; without client-area
    // animations (Windows setting) the column toggles at once.
    private void StatusChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement chip)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            ToggleColumn(chip.DataContext);
            return;
        }

        if (chip.RenderTransform is ScaleTransform { HasAnimatedProperties: true })
        {
            return;
        }

        var flip = new ScaleTransform(1, 1);
        chip.RenderTransformOrigin = new Point(0.5, 0.5);
        chip.RenderTransform = flip;

        var close = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        close.Completed += (_, _) =>
        {
            ToggleColumn(chip.DataContext);
            var open = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            open.Completed += (_, _) => flip.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            flip.BeginAnimation(ScaleTransform.ScaleXProperty, open);
        };
        flip.BeginAnimation(ScaleTransform.ScaleXProperty, close);
    }

    private void ChipCardToggle_Click(object sender, RoutedEventArgs e) => ToggleColumn(ChipCard.DataContext);

    private System.Windows.Threading.DispatcherTimer? chipCardOpen;
    private System.Windows.Threading.DispatcherTimer? chipCardClose;
    private FrameworkElement? chipCardTarget;

    // The chip's card opens after the mouse rests briefly; if it is already open on another chip
    // it moves over at once, so sweeping across the chips does not make it flicker.
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

    // Leaving the chip or the card, closing waits a moment: time to move from one to the other.
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
            // Changing the offset makes it recompute the position under the new chip.
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

    // Only “Exit” on the tray icon really closes: the X hides the window and reads
    // go on. If closing is cancelled, the X goes back to hiding.
    private void Exit()
    {
        exiting = true;
        Close();
        exiting = false;
    }

    // Closing interrupts the running action: for a database update that can leave the
    // database halfway through a migration, so the choice is left to whoever closes.
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
                    "Write operation in progress",
                    "Closing now interrupts the operation: an interrupted dotnet ef database update can leave the database halfway through a migration.",
                    [new("In progress", [new(description)])],
                    "Close anyway"));

            if (!confirmed)
            {
                e.Cancel = true;
                return;
            }
        }

        viewModel.Shutdown();
    }
}
