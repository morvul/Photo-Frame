using System;

namespace PhotoFrame
{
    /// <summary>
    /// Ошибка получения фотографий, пригодная для показа на экране рамки.
    /// Существует, чтобы не глотать исключения молча: у устройства нет консоли,
    /// и единственный канал диагностики — текст на экране.
    /// </summary>
    public class PhotoSourceException : Exception
    {
        public PhotoSourceException(string message) : base(message)
        {
        }

        public PhotoSourceException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
