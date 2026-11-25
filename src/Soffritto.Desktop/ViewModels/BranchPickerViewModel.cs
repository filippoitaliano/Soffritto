using System.Collections.ObjectModel;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

// Stessa macchina a stati della tendina della VSIX (UI/StatusViewModel.cs): un cambio di
// comportamento va riportato in entrambi i file. L'algoritmo delle voci e' condiviso (BranchList).
public sealed class BranchPickerViewModel : ObservableObject
{
    private readonly object branchesGate = new();
    private List<string> allBranches = [];
    private string? selectedBranch;
    private string branchFilter = string.Empty;
    private string branchText = string.Empty;
    private bool branchDropDownOpen;

    /// <summary>Le voci della tendina: il branch corrente in testa, poi quelli che passano il filtro, in ordine.</summary>
    public ObservableCollection<string> Branches { get; } = [];

    /// <summary>
    /// Testo nella casella della ComboBox editabile. Mentre si scrive filtra le voci e apre il
    /// menu; quando WPF lo riscrive con la voce scelta (o col corrente) non e' una ricerca.
    /// </summary>
    public string BranchText
    {
        get => branchText;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref branchText, text))
            {
                return;
            }

            if (text == selectedBranch || text == CurrentBranch)
            {
                ClearBranchFilter();
                return;
            }

            branchFilter = text;

            // Se la selezione e' andata persa (testo svuotato dal controllo), col filtro vuoto
            // si torna sul corrente; mentre si scrive la selezione non va toccata, altrimenti
            // WPF riscriverebbe la casella con il nome del branch selezionato.
            SyncBranches(reselect: text.Length == 0 && selectedBranch is null);

            if (text.Length > 0)
            {
                BranchDropDownOpen = true;
            }
        }
    }

    /// <summary>
    /// Aperto dal ViewModel appena si scrive, chiuso dall'utente o da una scelta. Se si chiude
    /// (Esc, click fuori, Tab) mentre nella casella c'e' ancora un testo di ricerca, la ricerca
    /// e' abbandonata: la casella torna a mostrare il branch corrente.
    /// </summary>
    public bool BranchDropDownOpen
    {
        get => branchDropDownOpen;
        set
        {
            if (!SetProperty(ref branchDropDownOpen, value))
            {
                return;
            }

            if (!value && CurrentBranch is not null && branchText != CurrentBranch && branchText != selectedBranch)
            {
                RestoreBranchText();
            }
        }
    }

    /// <summary>Branch corrente secondo l'ultima lettura: una selezione diversa da questo e' una richiesta di switch.</summary>
    public string? CurrentBranch { get; private set; }

    /// <summary>Chiamato dal setter di <see cref="SelectedBranch"/> quando l'utente sceglie un branch diverso dal corrente.</summary>
    public Action<string>? BranchSelected { get; set; }

    // Binding TwoWay dalla ComboBox: il setter arriva sia dalle scelte dell'utente sia dalle
    // riassegnazioni del sink (e con null se la voce selezionata sparisce). Solo una scelta
    // diversa dal branch corrente e' un'intenzione di cambiare. Una scelta chiude comunque la
    // ricerca: l'eco del testo che WPF manda subito dopo trova gia' il filtro vuoto.
    public string? SelectedBranch
    {
        get => selectedBranch;
        set
        {
            if (!SetProperty(ref selectedBranch, value))
            {
                return;
            }

            if (value is null)
            {
                return;
            }

            ClearBranchFilter();
            SetProperty(ref branchText, value, nameof(BranchText));
            BranchDropDownOpen = false;

            if (value != CurrentBranch)
            {
                BranchSelected?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Nuova lettura di git: fissa il corrente e l'elenco completo, poi riallinea la tendina
    /// sul corrente. <paramref name="current"/> null significa git non disponibile: tendina vuota.
    /// </summary>
    public void SetBranches(string? current, IEnumerable<string> names)
    {
        lock (branchesGate)
        {
            CurrentBranch = current;
            allBranches = names.ToList();
        }

        SyncBranches(reselect: true);
    }

    private void RestoreBranchText()
    {
        branchFilter = string.Empty;
        SyncBranches(reselect: true);
        SetProperty(ref branchText, CurrentBranch ?? string.Empty, nameof(BranchText));
    }

    private void ClearBranchFilter()
    {
        if (branchFilter.Length == 0)
        {
            return;
        }

        branchFilter = string.Empty;
        SyncBranches(reselect: false);
    }

    private void SyncBranches(bool reselect)
    {
        lock (branchesGate)
        {
            var current = CurrentBranch;

            BranchList.Sync(Branches, BranchList.Desired(current, allBranches, branchFilter));

            if (current is null)
            {
                SelectedBranch = null;
            }
            else if (reselect)
            {
                SelectedBranch = current;
            }
        }
    }
}
