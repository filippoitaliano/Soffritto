namespace Soffritto.Presentation;

/// <summary>
/// La nota libera di un ambiente, condivisa tra exe e VSIX: un file di testo per ambiente in
/// %LOCALAPPDATA%\Soffritto\Note. Le modifiche si salvano poco dopo l'ultimo
/// tasto; quelle fatte dall'altro processo arrivano a <c>externalChanged</c>.
/// </summary>
public sealed class EnvironmentNote : IDisposable
{
    private static readonly string Folder = Path.Combine(Soffritto.Core.Configuration.ConfigurationFiles.DataFolder, "Note");

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    // Una scrittura arriva al watcher come piu' eventi, e il file puo' essere ancora aperto
    // dall'altro processo: si rilegge una volta sola, a eventi finiti.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(200);

    private readonly string path;
    private readonly Action<string> externalChanged;
    private readonly FileSystemWatcher? watcher;
    private readonly Timer saveTimer;
    private readonly Timer reloadTimer;
    private readonly object gate = new();
    private string text;
    private string saved;
    private bool savePending;
    private bool disposed;

    /// <param name="key">Il numero dell'ambiente, o il nome della cartella se non ha un numero (vedi <see cref="BackendTools.EnvironmentKey"/>).</param>
    /// <param name="externalChanged">Chiamato da un thread del pool quando il file cambia per mano dell'altro processo.</param>
    public EnvironmentNote(string key, Action<string> externalChanged)
    {
        path = Path.Combine(Folder, $"ambiente-{key}.txt");
        this.externalChanged = externalChanged;
        text = saved = ReadFile() ?? string.Empty;
        saveTimer = new Timer(_ => Flush());
        reloadTimer = new Timer(_ => Reload());

        try
        {
            Directory.CreateDirectory(Folder);
            watcher = new FileSystemWatcher(Folder, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => ScheduleReload();
            watcher.Created += (_, _) => ScheduleReload();
            watcher.Deleted += (_, _) => ScheduleReload();
            watcher.Renamed += (_, _) => ScheduleReload();
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // Cartella non creabile: la nota resta modificabile, ma non si allinea con l'altro processo.
            watcher = null;
        }
    }

    public string Text
    {
        get
        {
            lock (gate)
            {
                return text;
            }
        }
    }

    /// <summary>Testo scritto dall'utente: si salva dopo <see cref="SaveDelay"/> senza altri tasti.</summary>
    public void Edit(string value)
    {
        lock (gate)
        {
            if (disposed || value == text)
            {
                return;
            }

            text = value;
            savePending = true;
            saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Salva subito un testo ancora in attesa, per esempio alla chiusura.</summary>
    public void Flush()
    {
        string toSave;
        lock (gate)
        {
            savePending = false;
            if (text == saved)
            {
                return;
            }

            toSave = text;
            saved = text;
        }

        if (WriteFile(toSave))
        {
            return;
        }

        // File rimasto occupato: si riprova piu' tardi, finche' la nota e' aperta.
        lock (gate)
        {
            if (saved == toSave)
            {
                saved = string.Empty;
            }

            if (!disposed)
            {
                savePending = true;
                saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        watcher?.Dispose();
        reloadTimer.Dispose();
        saveTimer.Dispose();
        Flush();
    }

    private void ScheduleReload()
    {
        lock (gate)
        {
            if (!disposed)
            {
                reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Reload()
    {
        var current = ReadFile();
        if (current is null)
        {
            return;
        }

        lock (gate)
        {
            // L'eco del proprio salvataggio coincide con saved. Un testo locale ancora da salvare
            // vince: il salvataggio in arrivo riscrivera' il file.
            if (disposed || savePending || current == saved)
            {
                return;
            }

            text = saved = current;
        }

        externalChanged(current);
    }

    /// <returns>Il contenuto, vuoto se il file non c'e'; null se resta occupato dall'altro processo.</returns>
    private string? ReadFile()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private bool WriteFile(string value)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(path, value);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }
}
