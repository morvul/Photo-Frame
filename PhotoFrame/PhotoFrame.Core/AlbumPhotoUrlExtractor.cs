using System.Text.RegularExpressions;

namespace PhotoFrame
{
    /// <summary>
    /// Вытаскивает ссылки на снимки из HTML страницы расшаренного альбома Google Photos.
    /// </summary>
    /// <remarks>
    /// Самая хрупкая часть проекта: это разбор вёрстки, а не документированного API.
    /// Поэтому логика вынесена сюда и покрыта тестами — если Google поменяет разметку,
    /// падать должны тесты, а не рамка на полке.
    /// </remarks>
    public static partial class AlbumPhotoUrlExtractor
    {
        /// <summary>
        /// Прямые ссылки на снимки выглядят как
        /// https://lh3.googleusercontent.com/pw/&lt;токен&gt;.
        /// Аватары авторов лежат на /a/ и под шаблон не попадают.
        /// </summary>
        [GeneratedRegex(@"https://lh\d+\.googleusercontent\.com/pw/[A-Za-z0-9_-]+",
            RegexOptions.CultureInvariant)]
        private static partial Regex PhotoUrlPattern();

        /// <summary>
        /// Возвращает ссылки в порядке появления, без повторов.
        /// </summary>
        /// <remarks>
        /// Обложка альбома дублирует один из снимков, поэтому без устранения повторов
        /// один кадр показывался бы дважды.
        /// </remarks>
        public static List<string> Extract(string albumPageHtml)
        {
            var seenUrls = new HashSet<string>(StringComparer.Ordinal);
            var photoUrls = new List<string>();

            foreach (Match match in PhotoUrlPattern().Matches(albumPageHtml))
            {
                if (seenUrls.Add(match.Value))
                {
                    photoUrls.Add(match.Value);
                }
            }

            return photoUrls;
        }

        /// <summary>
        /// Добавляет к ссылке размер, под который Google отдаст уже отмасштабированный кадр.
        /// </summary>
        public static string WithSize(string photoUrl, int widthPixels, int heightPixels) =>
            $"{photoUrl}=w{widthPixels}-h{heightPixels}";
    }
}
