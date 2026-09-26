using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoFrame
{
    /// <summary>
    /// Источник снимков для слайд-шоу.
    /// </summary>
    /// <remarks>
    /// Реализации отличаются тем, нужна ли им сеть: альбом Google качается в локальный кэш,
    /// а папка читается на месте, без копирования. Для страницы слайд-шоу разница не важна —
    /// ей нужен упорядоченный список путей к файлам.
    /// </remarks>
    public interface IPhotoSource
    {
        /// <summary>Человекочитаемое описание того, что настроено, — для экрана настроек.</summary>
        string DescribeConfiguration();

        /// <summary>True, если источник настроен и его можно опрашивать.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Приводит локальный набор снимков в соответствие с источником.
        /// </summary>
        /// <param name="forceRefresh">
        /// Обновлять, даже если источник выглядит неизменившимся, — для явного запроса пользователя.
        /// </param>
        Task<AlbumSyncResult> RefreshAsync(
            bool forceRefresh,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>Пути к снимкам в порядке показа.</summary>
        List<string> GetPhotoPaths();
    }
}
