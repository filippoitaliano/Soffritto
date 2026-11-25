namespace Soffritto.Presentation;

// Colori dei pallini di stato, gli stessi per ogni pallino di VSIX ed exe: tinte morbide, come
// le icone della testata (stile Animal Crossing), che si leggono sia su tema chiaro sia su scuro.
// Verde a posto (acceso, allineato, approvata), arancio serve attenzione, rosso errore, blu in
// corso (nell'exe pulsa: StatusDot), grigio spento o in attesa. Solo per i pallini: per i testi
// sul fondo del tema serve piu' contrasto (vedi MigrationColors).
public static class StatusBadge
{
    public const string OkColor = "#6BB36B";
    public const string WarningColor = "#F4A261";
    public const string ErrorColor = "#E76F6F";
    public const string InfoColor = "#5AA9E6";
    public const string OffColor = "#B8B8B8";
}

/// <summary>Etichette di stato di una migrazione, come compaiono a fianco del nome.</summary>
public static class MigrationLabels
{
    public const string Applied = "applicata";
    public const string Missing = "mancante";
    public const string OutOfOrder = "fuori ordine";
    public const string Unknown = "sconosciuta";
    public const string NotRead = "non letta";
}

// Le pendenti non hanno piu' un elenco dedicato: e' l'etichetta a segnalarle, quindi
// "mancante" e' in rosso e tutto il resto in grigio traslucido (leggibile su chiaro e scuro).
// Non e' il rosso dei badge: quello e' pensato per il bianco su tinta piena, qui il testo sta
// sul fondo del tema e serve un tono che tenga il contrasto da entrambe le parti.
public static class MigrationColors
{
    public const string Missing = "#D13438";
    public const string Neutral = "#99808080";

    public static string For(string label) => label == MigrationLabels.Missing ? Missing : Neutral;
}
