using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Хранит порядок показа и последний показанный кадр между запусками.
    /// </summary>
    /// <remarks>
    /// Порядок лежит в файле, а не в настройках: в нём бывает несколько тысяч путей.
    /// Последний кадр — в настройках: он меняется на каждом слайде, и перезаписывать
    /// ради одной строки весь список незачем.
    /// </remarks>
    internal static class SlideshowStateStore
    {
        private const string OrderFileName = "slideshow.order";
        private const string LastShownKey = "last_shown_photo";

        private static string OrderFilePath =>
            IoPath.Combine(FileSystem.AppDataDirectory, OrderFileName);

        /// <summary>Запоминает порядок показа. Вызывается только когда порядок изменился.</summary>
        public static void SaveOrder(List<string> photoPaths)
        {
            // Копия: список показа продолжает жить своей жизнью, пока идёт запись.
            var savedPaths = new List<string>(photoPaths);

            _ = Task.Run(() => WriteOrder(savedPaths, onlyIfChanged: false));
        }

        /// <summary>
        /// Сохраняет порядок, только если на диске лежит другой.
        /// </summary>
        /// <remarks>
        /// Файл — это четыре тысячи строк, и переписывать его при каждом изменении
        /// набора незачем: с сервера пропала пара кадров, а очередь осталась той же.
        /// Чтение для сравнения дешевле записи, а рамке лишние обращения к памяти
        /// устройства не бесплатны.
        /// </remarks>
        public static void SaveOrderIfChanged(List<string> photoPaths)
        {
            var savedPaths = new List<string>(photoPaths);

            _ = Task.Run(() => WriteOrder(savedPaths, onlyIfChanged: true));
        }

        private static void WriteOrder(List<string> savedPaths, bool onlyIfChanged)
        {
            string temporaryPath = OrderFilePath + ".tmp";

            try
            {
                if (onlyIfChanged && IsSameOrderOnDisk(savedPaths))
                {
                    return;
                }

                File.WriteAllLines(temporaryPath, savedPaths);
                File.Move(temporaryPath, OrderFilePath, overwrite: true);
            }
            catch (Exception saveFailure) when (
                saveFailure is IOException or UnauthorizedAccessException)
            {
                // Не сохранилось — после перезапуска порядок просто задастся заново.
                System.Diagnostics.Debug.WriteLine(
                    $"Порядок показа не сохранён: {saveFailure.Message}");
            }
        }

        private static bool IsSameOrderOnDisk(List<string> savedPaths)
        {
            if (!File.Exists(OrderFilePath))
            {
                return false;
            }

            string[] existingPaths = File.ReadAllLines(OrderFilePath);
            if (existingPaths.Length != savedPaths.Count)
            {
                return false;
            }

            for (int index = 0; index < existingPaths.Length; index++)
            {
                if (!string.Equals(existingPaths[index], savedPaths[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Запоминает показанный кадр.</summary>
        public static void SaveLastShown(string photoPath) =>
            Preferences.Default.Set(LastShownKey, photoPath);

        /// <summary>
        /// Раскладывает кадры в сохранённом порядке и возвращает место, с которого
        /// продолжать показ; -1 — сохранённый порядок не подошёл.
        /// </summary>
        public static int TryRestoreOrder(List<string> photoPaths)
        {
            List<string>? savedOrder = ReadOrder();
            string lastShownPath = Preferences.Default.Get(LastShownKey, string.Empty);

            return SlideshowOrder.TryApplySavedOrder(photoPaths, savedOrder, lastShownPath);
        }

        private static List<string>? ReadOrder()
        {
            try
            {
                if (!File.Exists(OrderFilePath))
                {
                    return null;
                }

                var savedPaths = new List<string>();
                foreach (string line in File.ReadAllLines(OrderFilePath))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        savedPaths.Add(line);
                    }
                }

                return savedPaths;
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Порядок показа не прочитан: {readFailure.Message}");
                return null;
            }
        }
    }
}
