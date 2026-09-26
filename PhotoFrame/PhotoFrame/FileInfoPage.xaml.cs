using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace PhotoFrame
{
    /// <summary>
    /// Всё, что известно о показанном кадре: файл, размеры, EXIF или метаданные видео.
    /// </summary>
    public partial class FileInfoPage : ContentPage
    {
        /// <summary>
        /// Путь к файлу, о котором показываются сведения.
        /// </summary>
        /// <remarks>
        /// Передаётся статикой, а не параметром маршрута: путь содержит слэши и пробелы,
        /// и протаскивать его через query-строку Shell означало бы возиться с
        /// экранированием без всякой пользы.
        /// </remarks>
        public static string? MediaPath { get; set; }

        public FileInfoPage()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            DetailsLayout.Clear();

            string? mediaPath = MediaPath;
            if (string.IsNullOrEmpty(mediaPath))
            {
                DetailsLayout.Add(BuildMessage("Кадр не выбран."));
                return;
            }

            DetailsLayout.Add(BuildMessage("Чтение сведений..."));

            // EXIF и метаданные контейнера читаются с диска: на рамке это заметно,
            // поэтому не держим UI-поток.
            List<MediaDetailsReader.DetailRow> rows =
                await Task.Run(() => MediaDetailsReader.Read(mediaPath)).ConfigureAwait(true);

            // Про кадр Immich файл в кэше знает мало: это превью с сервера, EXIF из него
            // вырезан, а имя — хэш. Всё остальное сервер отдал при синхронизации, и эти
            // строки идут первыми: они и отвечают на вопрос «что это за снимок».
            rows.InsertRange(0, BuildImmichRows(MediaPath));

            DetailsLayout.Clear();

            if (rows.Count == 0)
            {
                DetailsLayout.Add(BuildMessage("Сведения недоступны."));
                return;
            }

            foreach (MediaDetailsReader.DetailRow row in rows)
            {
                DetailsLayout.Add(BuildRow(row.Label, row.Value));
            }
        }

        /// <summary>
        /// Сведения о кадре Immich, взятые из списка рядом с кэшем.
        /// </summary>
        /// <remarks>
        /// Для кадра клипа сведения ищутся по заставке: страница показывает уже сам
        /// клип, а лежит он в общем каталоге, где списка Immich нет. Имя у клипа то же,
        /// что у заставки, поэтому поиск идёт по нему.
        /// </remarks>
        private static List<MediaDetailsReader.DetailRow> BuildImmichRows(string? mediaPath)
        {
            var rows = new List<MediaDetailsReader.DetailRow>();

            if (mediaPath is null)
            {
                return rows;
            }

            ImmichSlideInfo? slide = ImmichSidecar.FindByAnyPath(mediaPath);
            if (slide is null)
            {
                return rows;
            }

            rows.Add(new MediaDetailsReader.DetailRow("Источник", "Immich"));
            rows.Add(new MediaDetailsReader.DetailRow("Имя на сервере", slide.FileName));

            if (slide.CameraName.Length > 0)
            {
                rows.Add(new MediaDetailsReader.DetailRow("Снято на", slide.CameraName));
            }

            if (slide.TakenAt is { } takenAt)
            {
                rows.Add(new MediaDetailsReader.DetailRow(
                    "Дата съёмки", takenAt.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture)));
            }

            return rows;
        }

        private static Label BuildMessage(string text) => new()
        {
            Text = text,
            TextColor = Colors.Gray,
            FontSize = 15,
        };

        private static View BuildRow(string label, string value)
        {
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(260) },
                    new ColumnDefinition { Width = GridLength.Star },
                },
                Padding = new Thickness(0, 10),
                ColumnSpacing = 16,
            };

            row.Add(new Label
            {
                Text = label,
                TextColor = Colors.Gray,
                FontSize = 15,
            });

            var valueLabel = new Label
            {
                Text = value,
                TextColor = Colors.White,
                FontSize = 15,

                // Пути и модели камер бывают длинными: переносим, а не обрезаем.
                LineBreakMode = LineBreakMode.WordWrap,
            };

            row.Add(valueLabel, column: 1);
            return row;
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
