using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoFrame
{
    /// <summary>Клип, стоящий за показанным кадром.</summary>
    /// <param name="DurationMilliseconds">Длительность; 0 — неизвестна.</param>
    /// <param name="IsMotionPhoto">
    /// Живое фото: играется один раз, молча и без отметки, после чего кадр остаётся
    /// обычным снимком.
    /// </param>
    internal sealed record SlideClip(int DurationMilliseconds, bool IsMotionPhoto);

    /// <summary>
    /// Один вход для клипов из любого сетевого источника.
    /// </summary>
    /// <remarks>
    /// Слайд-шоу оперирует путями к файлам и об источнике знать не должно: и у альбома
    /// Google, и у Immich кадр видео — это заставка в кэше, а сам клип забирается,
    /// когда до кадра дошла очередь. Различаются только адреса и способ их узнать,
    /// поэтому выбор делается здесь, по тому, в чьём кэше лежит файл.
    /// </remarks>
    internal static class SlideClips
    {
        /// <summary>Клип за этим кадром либо null, если это обычный снимок.</summary>
        public static SlideClip? Find(string mediaPath)
        {
            ImmichSlideInfo? immichSlide = ImmichSidecar.Find(mediaPath);

            if (immichSlide is not null)
            {
                // Пустой ClipAssetId — обычный снимок Immich, играть нечего.
                if (immichSlide.ClipAssetId.Length == 0
                    || ImmichVideoCache.IsUnplayable(mediaPath))
                {
                    return null;
                }

                return new SlideClip(
                    immichSlide.DurationMilliseconds, immichSlide.IsMotionPhoto);
            }

            AlbumVideoCache.AlbumClip? albumClip = AlbumVideoCache.FindClip(mediaPath);

            return albumClip is null
                ? null
                : new SlideClip(albumClip.DurationMilliseconds, albumClip.IsMotionPhoto);
        }

        /// <summary>Путь к уже скачанному клипу либо null.</summary>
        public static string? FindReady(string mediaPath) =>
            MediaTrash.IsImmichPhoto(mediaPath)
                ? ImmichVideoCache.FindReadyVideo(mediaPath)
                : AlbumVideoCache.FindReadyVideo(mediaPath);

        /// <summary>Возвращает клип, при необходимости скачав его. Null — не удалось.</summary>
        public static Task<string?> TryGetAsync(
            string mediaPath,
            IProgress<double>? downloadProgress = null,
            CancellationToken cancellationToken = default)
        {
            ImmichSlideInfo? immichSlide = ImmichSidecar.Find(mediaPath);

            return immichSlide is null
                ? AlbumVideoCache.TryGetVideoAsync(mediaPath, downloadProgress, cancellationToken)
                : ImmichVideoCache.TryGetVideoAsync(
                    mediaPath, immichSlide.ClipAssetId, downloadProgress, cancellationToken);
        }

        /// <summary>Помечает клип непроигрываемым: кадр остаётся заставкой.</summary>
        public static void MarkUnplayable(string mediaPath)
        {
            if (MediaTrash.IsImmichPhoto(mediaPath))
            {
                ImmichVideoCache.MarkUnplayable(mediaPath);
                return;
            }

            AlbumVideoCache.MarkUnplayable(mediaPath);
        }
    }
}
