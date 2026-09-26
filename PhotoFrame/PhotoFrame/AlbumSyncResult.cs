namespace PhotoFrame
{
    /// <summary>
    /// Итог обновления набора снимков.
    /// </summary>
    /// <param name="TotalPhotoCount">Сколько снимков готово к показу после обновления.</param>
    /// <param name="DownloadedCount">Сколько снимков появилось (скачано либо найдено впервые).</param>
    /// <param name="ReusedCount">Сколько взято из локального кэша без обращения к сети.</param>
    /// <param name="RemovedCount">
    /// Сколько снимков перестало показываться. Для общего альбома это ещё и удаление
    /// файла из кэша, для папок на устройстве — только исключение из списка: сами файлы
    /// приложение не трогает.
    /// </param>
    /// <param name="AvailableCount">
    /// Сколько кадров нашлось до применения лимита. Больше <paramref name="TotalPhotoCount"/>
    /// означает, что часть отброшена, и об этом нужно сказать вслух.
    /// </param>
    /// <param name="Warning">
    /// Заполняется, когда часть источников не ответила, но показывать всё равно есть что.
    /// </param>
    public sealed record AlbumSyncResult(
        int TotalPhotoCount,
        int DownloadedCount,
        int ReusedCount,
        int RemovedCount,
        int AvailableCount = 0,
        string? Warning = null);
}
