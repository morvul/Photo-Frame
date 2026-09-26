using System.Text.RegularExpressions;

namespace PhotoFrame
{
    /// <summary>Кадр общего альбома: снимок или видео.</summary>
    /// <param name="ItemId">
    /// Идентификатор кадра. По нему собирается адрес страницы отдельного кадра —
    /// только там лежит ссылка на само видео.
    /// </param>
    /// <param name="PosterUrl">Ссылка на изображение: сам снимок либо кадр-заставка видео.</param>
    /// <param name="VideoDurationMilliseconds">Длительность видео; 0 — это снимок.</param>
    /// <param name="MotionDurationMilliseconds">
    /// Длительность клипа «живого фото» — снимка с приложенными секундами движения.
    /// 0 — обычный снимок.
    /// </param>
    public sealed record AlbumItem(
        string ItemId,
        string PosterUrl,
        int VideoDurationMilliseconds,
        int MotionDurationMilliseconds = 0)
    {
        public bool IsVideo => VideoDurationMilliseconds > 0;

        /// <summary>
        /// Живое фото: в слайд-шоу это снимок, который может на секунду ожить.
        /// </summary>
        public bool IsMotionPhoto => VideoDurationMilliseconds == 0
                                     && MotionDurationMilliseconds > 0;
    }

    /// <summary>
    /// Разбирает список кадров страницы общего альбома.
    /// </summary>
    /// <remarks>
    /// Страница отдаёт данные одним JS-массивом, где у каждого кадра есть идентификатор,
    /// ссылка на изображение и словарь свойств. Видео опознаётся по полю "76647426":
    /// его первое число — длительность в миллисекундах.
    ///
    /// Поле "146008172" — это не видео, а «живое фото»: короткий клип, приложенный
    /// к снимку. В слайд-шоу такой кадр остаётся снимком; оживлять его или нет,
    /// решает настройка, иначе показ заполнился бы секундными обрывками.
    ///
    /// Разбор держится на недокументированной вёрстке, поэтому пустой результат
    /// вызывающий код обязан трактовать как ошибку, а не как «альбом пуст».
    /// </remarks>
    public static partial class AlbumItemExtractor
    {
        /// <summary>
        /// Сколько символов записи разбирать, если следующая запись не нашлась.
        /// Записи короче тысячи символов; запас нужен на редкие длинные метаданные.
        /// </summary>
        private const int MaxEntryLength = 2000;

        /// <summary>Поле с параметрами видео: первое число — длительность в мс.</summary>
        private const string VideoFieldPrefix = "\"76647426\":[";

        /// <summary>
        /// Поле живого фото: длительность стоит вторым значением, первое всегда null.
        /// </summary>
        private const string MotionFieldPrefix = "\"146008172\":[null,";

        [GeneratedRegex(
            @"\[""(?<id>AF1Qip[A-Za-z0-9_-]+)"",\[""(?<url>https://lh\d+\.googleusercontent\.com/pw/[A-Za-z0-9_-]+)""")]
        private static partial Regex ItemStartRegex();

        /// <summary>
        /// Возвращает кадры в том порядке, в котором их перечисляет Google, без повторов.
        /// </summary>
        public static List<AlbumItem> Extract(string albumPageHtml)
        {
            var items = new List<AlbumItem>();

            if (string.IsNullOrEmpty(albumPageHtml))
            {
                return items;
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            MatchCollection starts = ItemStartRegex().Matches(albumPageHtml);

            for (int index = 0; index < starts.Count; index++)
            {
                Match start = starts[index];

                // Запись кончается там, где начинается следующая: разбирать вложенные
                // скобки ради одного поля незачем.
                int entryEnd = index + 1 < starts.Count
                    ? starts[index + 1].Index
                    : Math.Min(albumPageHtml.Length, start.Index + MaxEntryLength);

                string entry = albumPageHtml[start.Index..entryEnd];
                string itemId = start.Groups["id"].Value;

                if (!seenIds.Add(itemId))
                {
                    continue;
                }

                items.Add(new AlbumItem(
                    itemId,
                    start.Groups["url"].Value,
                    ReadDuration(entry, VideoFieldPrefix),
                    ReadDuration(entry, MotionFieldPrefix)));
            }

            return items;
        }

        /// <summary>Длительность из указанного поля записи; 0, если поля нет.</summary>
        private static int ReadDuration(string entry, string fieldPrefix)
        {
            int fieldIndex = entry.IndexOf(fieldPrefix, StringComparison.Ordinal);
            if (fieldIndex < 0)
            {
                return 0;
            }

            int digitsStart = fieldIndex + fieldPrefix.Length;
            int digitsEnd = digitsStart;

            while (digitsEnd < entry.Length && char.IsAsciiDigit(entry[digitsEnd]))
            {
                digitsEnd++;
            }

            return int.TryParse(entry[digitsStart..digitsEnd], out int durationMilliseconds)
                ? durationMilliseconds
                : 0;
        }
    }
}
