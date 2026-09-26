using Android.Media;

// Stream есть и в Android.Media, и в System.IO: нужен однозначный псевдоним.
using AudioStream = Android.Media.Stream;
using VolumeAdjust = Android.Media.Adjust;

namespace PhotoFrame
{
    /// <summary>
    /// Громкость воспроизведения на устройстве.
    /// </summary>
    /// <remarks>
    /// Меняется именно системная громкость мультимедиа, а не громкость проигрывателя:
    /// та лишь доля от системной, и при системных 5 из 15 громче уже не сделать — а на
    /// рамке эти 15 шагов и есть весь запас. Значение системное, поэтому переживает
    /// перезапуск приложения само, без своей настройки.
    ///
    /// Права на это не нужны: приложение меняет громкость того потока, в который само
    /// и играет.
    /// </remarks>
    internal static class SystemVolume
    {
        private const AudioStream MediaStream = AudioStream.Music;

        private static AudioManager? Manager =>
            Android.App.Application.Context.GetSystemService(
                Android.Content.Context.AudioService) as AudioManager;

        /// <summary>Громкость в процентах от предельной; -1, если узнать не удалось.</summary>
        public static int Percent
        {
            get
            {
                AudioManager? manager = Manager;
                if (manager is null)
                {
                    return -1;
                }

                int maximum = manager.GetStreamMaxVolume(MediaStream);
                if (maximum <= 0)
                {
                    return -1;
                }

                return manager.GetStreamVolume(MediaStream) * 100 / maximum;
            }
        }

        /// <summary>Прибавить шаг громкости.</summary>
        public static void Raise() => Adjust(VolumeAdjust.Raise);

        /// <summary>Убавить шаг громкости.</summary>
        public static void Lower() => Adjust(VolumeAdjust.Lower);

        private static void Adjust(VolumeAdjust direction)
        {
            try
            {
                // ShowUi не просим: у рамки свой вид, и системная плашка поверх снимка
                // выглядела бы чужеродно.
                Manager?.AdjustStreamVolume(MediaStream, direction, 0);
            }
            catch (Java.Lang.Throwable volumeFailure)
            {
                FrameLog.Warn($"Громкость не изменена: {volumeFailure.Message}");
            }
        }
    }
}
