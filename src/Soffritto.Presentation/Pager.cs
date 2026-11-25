namespace Soffritto.Presentation;

/// <summary>Finestra di una pagina sulle migrazioni di un context, gia' ordinate dalla piu' recente.</summary>
public static class Pager
{
    /// <summary>Migrazioni mostrate per pagina: oltre questa soglia compare il pager.</summary>
    public const int PageSize = 8;

    public readonly record struct PageWindow(int Page, int Start, int Count, string Label, bool CanGoPrevious, bool CanGoNext);

    public static PageWindow Compute(int total, int requestedPage)
    {
        var lastPage = total == 0 ? 0 : (total - 1) / PageSize;
        var page = Math.Min(Math.Max(requestedPage, 0), lastPage);
        var start = page * PageSize;
        var count = Math.Max(0, Math.Min(PageSize, total - start));

        var label = total > 0
            ? $"{start + 1}-{start + count} di {total}"
            : string.Empty;

        return new PageWindow(page, start, count, label, page > 0, page < lastPage);
    }
}
