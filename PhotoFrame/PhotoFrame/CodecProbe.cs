using System;
using Android.Media;

namespace PhotoFrame
{
    /// <summary>
    /// Пишет в журнал, какие видеодекодеры есть на устройстве.
    /// </summary>
    /// <remarks>
    /// Нужна, чтобы решать по данным, а не по догадкам: системный проигрыватель рамки
    /// (вендорный FFPlayer) отказывается от HEVC и VP9, но в конфигурации устройства
    /// программные декодеры на эти форматы объявлены. Если MediaCodec их действительно
    /// создаёт, воспроизведение через MediaCodec или ExoPlayer имеет смысл; если нет —
    /// вопрос закрыт.
    ///
    /// Вызывается один раз при запуске. Ничего не решает и ни на что не влияет, кроме
    /// записей в журнале: читать через `adb logcat -s PhotoFrame`.
    /// </remarks>
    internal static class CodecProbe
    {
        private static readonly string[] InterestingTypes =
        {
            MediaFormat.MimetypeVideoAvc,
            MediaFormat.MimetypeVideoHevc,
            MediaFormat.MimetypeVideoVp9,
            MediaFormat.MimetypeVideoVp8,
        };

        public static void LogVideoDecoders()
        {
            try
            {
                foreach (string mimeType in InterestingTypes)
                {
                    LogDecodersFor(mimeType);
                }
            }
            catch (Exception probeFailure) when (probeFailure is Java.Lang.Throwable)
            {
                FrameLog.Warn($"Список декодеров не получен: {probeFailure.Message}");
            }
        }

        private static void LogDecodersFor(string mimeType)
        {
            var codecList = new MediaCodecList(MediaCodecListKind.AllCodecs);
            bool found = false;

            foreach (MediaCodecInfo codecInfo in codecList.GetCodecInfos()!)
            {
                if (codecInfo.IsEncoder)
                {
                    continue;
                }

                foreach (string supportedType in codecInfo.GetSupportedTypes()!)
                {
                    if (!supportedType.Equals(mimeType, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    found = true;
                    LogCapabilities(codecInfo, mimeType);
                }
            }

            if (!found)
            {
                FrameLog.Warn($"{mimeType}: декодера нет");
            }
        }

        private static void LogCapabilities(MediaCodecInfo codecInfo, string mimeType)
        {
            string sizes = "размеры неизвестны";

            try
            {
                MediaCodecInfo.VideoCapabilities? video =
                    codecInfo.GetCapabilitiesForType(mimeType)?.VideoCapabilities;

                if (video is not null)
                {
                    sizes = $"до {video.SupportedWidths?.Upper}x{video.SupportedHeights?.Upper}";
                }
            }
            catch (Exception capabilitiesFailure) when (capabilitiesFailure is Java.Lang.Throwable)
            {
                sizes = "размеры не читаются";
            }

            // Имена, начинающиеся с OMX.google. или c2.android., — программные декодеры.
            string name = codecInfo.Name ?? "?";
            bool likelySoftware = name.StartsWith("OMX.google.", StringComparison.OrdinalIgnoreCase)
                                  || name.StartsWith("c2.android.", StringComparison.OrdinalIgnoreCase);

            FrameLog.Info($"{mimeType}: {name} ({(likelySoftware ? "программный" : "аппаратный")}), {sizes}");
        }
    }
}
