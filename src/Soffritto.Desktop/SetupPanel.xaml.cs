using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

public partial class SetupPanel : UserControl
{
    public SetupPanel()
    {
        InitializeComponent();

        // Fields bound directly to the configuration don't notify the ViewModel: validation
        // runs again on every keystroke, from any box.
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => (DataContext as SetupViewModel)?.Revalidate()));
        DataContextChanged += (_, _) =>
        {
            PatBox.Clear();

            // Every opening starts again from the first tab.
            FirstTab.IsChecked = true;
            ShowTab((string)FirstTab.Tag);
            ApplyDemo();
        };

        // The demo can also be turned on and off from the Preferences tab, with the panel open.
        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.IsDemo))
            {
                ApplyDemo();
            }
        };
    }

    // In demo the setup changes only preferences: the other tabs have actions that really touch
    // configuration, Docker and tunnels (Save, Align seed, Clean up, Delete tunnel, Release).
    private void ApplyDemo()
    {
        var isDemo = AppSettings.Current.IsDemo;
        var locked = isDemo ? Visibility.Collapsed : Visibility.Visible;
        foreach (var tab in Tabs.Children.OfType<RadioButton>().Where(t => t != PreferencesTab))
        {
            tab.Visibility = locked;
        }

        ImportButton.Visibility = locked;
        SaveButton.Visibility = locked;
        ConfigurationHint.Visibility = locked;
        DemoHint.Visibility = isDemo ? Visibility.Visible : Visibility.Collapsed;

        if (isDemo && PreferencesTab.IsChecked != true)
        {
            PreferencesTab.IsChecked = true;
        }
    }

    // A tab shows the sections carrying its Tag; the others stay in the tree, with their
    // fields and their validation, but hidden.
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tab })
        {
            ShowTab(tab);
        }
    }

    private void ShowTab(string tab)
    {
        // During InitializeComponent the first tab gets checked before the sections exist.
        if (Sections is null)
        {
            return;
        }

        foreach (var section in Sections.Children.OfType<FrameworkElement>())
        {
            section.Visibility = Equals(section.Tag, tab) ? Visibility.Visible : Visibility.Collapsed;
        }

        SectionsScroll.ScrollToTop();
        if (AppSettings.Current.IsDemo)
        {
            return;
        }

        // The pool is reloaded from devtunnel when its tab opens.
        if (tab == "Tunnel" && DataContext is SetupViewModel { Pool: { } pool })
        {
            _ = pool.RefreshPoolAsync();
        }

        // vswhere and the PATH search are cheap, but only whoever opens the tab pays for them.
        if (tab == "Componenti" && DataContext is SetupViewModel { Components: { } components })
        {
            components.Refresh();
        }

        // Seed (with the source clone fetch) and Docker disk space are reloaded when their tab opens.
        if (tab == "Docker" && DataContext is SetupViewModel { Pool: { } docker })
        {
            _ = docker.RefreshMaintenanceAsync();
        }
    }

    /// <summary>First opening after Setup.exe: start from the components instead of the repositories.</summary>
    public void ShowComponents()
    {
        if (!AppSettings.Current.IsDemo)
        {
            ComponentsTab.IsChecked = true;
        }
    }

    /// <summary>The X and Cancel: the host decides how to hide it.</summary>
    public event Action? CloseRequested;

    /// <summary>False until there is a valid configuration: without one, there is nothing to see behind the panel.</summary>
    public bool CanClose
    {
        set
        {
            CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // The PasswordBox is not bound: the PAT goes to the ViewModel on every keystroke and never sits in the XAML.
    private void Pat_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm)
        {
            vm.NewPat = PatBox.Password;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupViewModel vm)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Folder containing the environment clones",
            InitialDirectory = System.IO.Directory.Exists(vm.Root) ? vm.Root : string.Empty,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            vm.Root = dialog.FolderName;
        }
    }

    private void BrowseFallback_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RepositoryCardEditor editor)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = $"Checkout with the Docker files of {editor.Id}",
            InitialDirectory = System.IO.Directory.Exists(editor.DockerFilesFallback) ? editor.DockerFilesFallback : string.Empty,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            editor.DockerFilesFallback = dialog.FolderName;
        }
    }

    private void BrowseSeed_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RepositoryCardEditor editor)
        {
            return;
        }

        var folder = System.IO.Path.GetDirectoryName(editor.SeedPath);
        var dialog = new OpenFileDialog
        {
            Title = $"Database seed of {editor.Id}",
            Filter = "Seed (*.bak;*.7z)|*.bak;*.7z",
            InitialDirectory = folder is { Length: > 0 } && System.IO.Directory.Exists(folder) ? folder : string.Empty,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            editor.SeedPath = dialog.FileName;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Configuration to load",
            Filter = "Configuration (*.json)|*.json",
            InitialDirectory = AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            vm.Import(dialog.FileName);
        }
    }
}
