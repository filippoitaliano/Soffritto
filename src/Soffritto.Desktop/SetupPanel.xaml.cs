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

        // I campi legati direttamente alla configurazione non avvisano il ViewModel: la
        // validazione si rifa' a ogni tasto, da qualunque casella.
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => (DataContext as SetupViewModel)?.Revalidate()));
        DataContextChanged += (_, _) =>
        {
            PatBox.Clear();

            // Ogni apertura riparte dalla prima scheda.
            FirstTab.IsChecked = true;
            ShowTab((string)FirstTab.Tag);
            ApplyDemo();
        };

        // La demo si accende e si spegne anche dalla scheda Preferenze, con il pannello aperto.
        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.IsDemo))
            {
                ApplyDemo();
            }
        };
    }

    // In demo il setup cambia solo le preferenze: le altre schede hanno azioni che toccano davvero
    // configurazione, Docker e tunnel (Salva, Allinea seed, Pulisci, Elimina tunnel, Rilascia).
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

    // Una scheda mostra le sezioni che portano il suo Tag; le altre restano nell'albero, con i
    // loro campi e la loro validazione, ma nascoste.
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tab })
        {
            ShowTab(tab);
        }
    }

    private void ShowTab(string tab)
    {
        // Durante InitializeComponent la prima scheda si spunta prima che le sezioni esistano.
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

        // Il pool si rilegge da devtunnel quando si apre la sua scheda.
        if (tab == "Tunnel" && DataContext is SetupViewModel { Pool: { } pool })
        {
            _ = pool.RefreshPoolAsync();
        }

        // vswhere e la ricerca nel PATH costano poco, ma solo chi apre la scheda li paga.
        if (tab == "Componenti" && DataContext is SetupViewModel { Components: { } components })
        {
            components.Refresh();
        }

        // Seed (con la fetch del clone sorgente) e spazio di Docker si rileggono all'apertura della loro scheda.
        if (tab == "Docker" && DataContext is SetupViewModel { Pool: { } docker })
        {
            _ = docker.RefreshMaintenanceAsync();
        }
    }

    /// <summary>Prima apertura dopo il Setup.exe: si parte dai componenti invece che dai repository.</summary>
    public void ShowComponents()
    {
        if (!AppSettings.Current.IsDemo)
        {
            ComponentsTab.IsChecked = true;
        }
    }

    /// <summary>La X e Annulla: chi lo ospita decide come nasconderlo.</summary>
    public event Action? CloseRequested;

    /// <summary>Falso finche' non c'e' una configurazione valida: senza, dietro il pannello non c'e' niente da vedere.</summary>
    public bool CanClose
    {
        set
        {
            CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // La PasswordBox non si lega: il PAT passa al ViewModel a ogni tasto e non resta nello XAML.
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
            Title = "Cartella che contiene i cloni degli ambienti",
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
            Title = $"Checkout con i file Docker di {editor.Id}",
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
            Title = $"Seed del database di {editor.Id}",
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
            Title = "Configurazione da caricare",
            Filter = "Configurazione (*.json)|*.json",
            InitialDirectory = AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            vm.Import(dialog.FileName);
        }
    }
}
