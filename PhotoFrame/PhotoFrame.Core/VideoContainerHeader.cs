using System;

namespace PhotoFrame
{
    /// <summary>
    /// Похож ли файл на видеоконтейнер по своим первым байтам.
    /// </summary>
    /// <remarks>
    /// Проверка нужна потому, что сервер отдаёт ошибку и с кодом 200: вместо клипа
    /// приходит страница входа или JSON, а проигрыватель на таком файле сообщает
    /// лишь «не удалось», и кадр молча остаётся снимком.
    ///
    /// Проверяется любое из известных имён атомов, а не только «ftyp»: mp4 из общего
    /// альбома Google начинаются с «ftyp», а клипы живых фото из Immich сервер отдаёт
    /// как video/quicktime, и файл начинается с атома «wide».
    /// </remarks>
    public static class VideoContainerHeader
    {
        /// <summary>Сколько байт нужно для проверки: размер атома и его имя.</summary>
        public const int RequiredBytes = 8;

        /// <summary>
        /// Имена атомов, с которых начинаются файлы MP4 и QuickTime.
        /// </summary>
        /// <remarks>
        /// ftyp — обычное начало mp4; moov и mdat встречаются первыми у файлов,
        /// записанных потоком; wide, free и skip — заполнители, которые ставит
        /// в начало камера Apple.
        /// </remarks>
        private static readonly string[] KnownBoxTypes =
        {
            "ftyp", "moov", "mdat", "wide", "free", "skip", "pnot", "uuid",
        };

        /// <summary>
        /// True, если начало файла похоже на видеоконтейнер.
        /// </summary>
        /// <param name="header">Первые байты файла, не меньше <see cref="RequiredBytes"/>.</param>
        public static bool LooksLikeContainer(ReadOnlySpan<byte> header)
        {
            if (header.Length < RequiredBytes)
            {
                return false;
            }

            Span<char> boxType = stackalloc char[4];
            for (int index = 0; index < 4; index++)
            {
                byte value = header[4 + index];

                // Имя атома — четыре печатных латинских символа. Всё прочее означает,
                // что это не контейнер, и сравнивать со списком уже незачем.
                if (value < 32 || value > 126)
                {
                    return false;
                }

                boxType[index] = (char)value;
            }

            foreach (string knownType in KnownBoxTypes)
            {
                if (boxType.SequenceEqual(knownType))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
