using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoFrame
{
    /// <summary>
    /// Показывает снимки из всех включённых источников одним слайд-шоу.
    /// </summary>
    /// <remarks>
    /// Источники независимы: если Wi-Fi лежит, а папки на устройстве доступны, рамка
    /// продолжает показывать локальные снимки и лишь сообщает, что альбом не прочитан.
    /// Полная ошибка возникает только когда показывать нечего вообще.
    /// </remarks>
    public class CompositePhotoSource : IPhotoSource
    {
        private readonly SharedAlbumPhotoSource _sharedAlbumSource;
        private readonly ImmichPhotoSource _immichSource;
        private readonly LocalFolderPhotoSource _localFolderSource;

        public CompositePhotoSource(
            SharedAlbumPhotoSource sharedAlbumSource,
            ImmichPhotoSource immichSource,
            LocalFolderPhotoSource localFolderSource)
        {
            _sharedAlbumSource = sharedAlbumSource;
            _immichSource = immichSource;
            _localFolderSource = localFolderSource;
        }

        /// <inheritdoc />
        public bool IsConfigured
        {
            get
            {
                foreach (IPhotoSource source in EnabledSources())
                {
                    if (source.IsConfigured)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <inheritdoc />
        public string DescribeConfiguration()
        {
            var descriptions = new List<string>();

            if (FrameSettings.UseSharedAlbum)
            {
                descriptions.Add($"Google Photos: {_sharedAlbumSource.DescribeConfiguration()}");
            }

            if (FrameSettings.UseImmich)
            {
                descriptions.Add($"Immich: {_immichSource.DescribeConfiguration()}");
            }

            if (FrameSettings.UseLocalFolders)
            {
                descriptions.Add($"Папки: {_localFolderSource.DescribeConfiguration()}");
            }

            return descriptions.Count == 0 ? "Источники не включены" : string.Join("; ", descriptions);
        }

        /// <summary>
        /// Источники в порядке показа: альбом Google, затем Immich, затем локальные папки.
        /// </summary>
        private List<IPhotoSource> EnabledSources()
        {
            var enabledSources = new List<IPhotoSource>(3);

            if (FrameSettings.UseSharedAlbum)
            {
                enabledSources.Add(_sharedAlbumSource);
            }

            if (FrameSettings.UseImmich)
            {
                enabledSources.Add(_immichSource);
            }

            if (FrameSettings.UseLocalFolders)
            {
                enabledSources.Add(_localFolderSource);
            }

            return enabledSources;
        }

        /// <inheritdoc />
        public async Task<AlbumSyncResult> RefreshAsync(
            bool forceRefresh,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            List<IPhotoSource> enabledSources = EnabledSources();
            if (enabledSources.Count == 0)
            {
                throw new PhotoSourceException(
                    "Не включён ни один источник снимков. Откройте настройки (⚙).");
            }

            int totalPhotoCount = 0;
            int downloadedCount = 0;
            int reusedCount = 0;
            int removedCount = 0;
            int availableCount = 0;
            var failureMessages = new List<string>();

            foreach (IPhotoSource source in enabledSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    AlbumSyncResult sourceResult = await source
                        .RefreshAsync(forceRefresh, progress, cancellationToken)
                        .ConfigureAwait(false);

                    totalPhotoCount += sourceResult.TotalPhotoCount;
                    downloadedCount += sourceResult.DownloadedCount;
                    reusedCount += sourceResult.ReusedCount;
                    removedCount += sourceResult.RemovedCount;
                    availableCount += Math.Max(
                        sourceResult.AvailableCount, sourceResult.TotalPhotoCount);
                }
                catch (PhotoSourceException sourceFailure)
                {
                    // Отказ одного источника не должен гасить остальные.
                    failureMessages.Add(sourceFailure.Message);
                }
            }

            if (totalPhotoCount == 0)
            {
                throw new PhotoSourceException(failureMessages.Count > 0
                    ? string.Join(" ", failureMessages)
                    : "Ни в одном источнике не нашлось снимков.");
            }

            return new AlbumSyncResult(
                totalPhotoCount,
                downloadedCount,
                reusedCount,
                removedCount,
                availableCount,
                failureMessages.Count > 0 ? string.Join(" ", failureMessages) : null);
        }

        /// <inheritdoc />
        public List<string> GetPhotoPaths()
        {
            var allPhotoPaths = new List<string>();

            foreach (IPhotoSource source in EnabledSources())
            {
                allPhotoPaths.AddRange(source.GetPhotoPaths());
            }

            return allPhotoPaths;
        }
    }
}
