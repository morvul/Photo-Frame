using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Обзор файловой системы для выбора папок со снимками.
    /// </summary>
    /// <remarks>
    /// Своя реализация, а не системный выбор папки (ACTION_OPEN_DOCUMENT_TREE), потому что:
    /// SAF возвращает content://-URI, который нельзя отдать в Directory.GetFiles; за один
    /// вызов выбирается ровно одна папка, а нужен список; и здесь можно сразу показать,
    /// сколько изображений лежит в каждой папке.
    /// </remarks>
    public partial class FolderPickerPage : ContentPage
    {
        /// <summary>Внутренняя память — единственный корень на этой рамке.</summary>
        private const string PrimaryStorageRoot = "/storage/emulated/0";

        /// <summary>
        /// Сколько файлов максимум просматриваем ради счётчика. Обход рекурсивный, а
        /// каталоги вроде DCIM бывают огромными: без лимита открытие папки заметно тормозит.
        /// </summary>
        private const int ImageCountScanLimit = 1500;

        private readonly ObservableCollection<FolderEntry> _visibleFolders = new();
        private readonly HashSet<string> _selectedFolders =
            new(StringComparer.OrdinalIgnoreCase);

        private string _currentDirectory = PrimaryStorageRoot;

        /// <summary>Растёт при каждом открытии папки: отбрасывает результаты устаревших обходов.</summary>
        private int _scanGeneration;

        public FolderPickerPage()
        {
            InitializeComponent();
            FolderList.ItemsSource = _visibleFolders;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            _selectedFolders.Clear();
            foreach (string folderPath in FrameSettings.LocalFolderPaths)
            {
                _selectedFolders.Add(folderPath);
            }

            if (!await EnsureStoragePermissionAsync().ConfigureAwait(true))
            {
                CurrentPathLabel.Text = "Нет доступа к памяти устройства";
                CurrentFolderInfoLabel.Text =
                    "Выдайте приложению разрешение на чтение файлов в настройках Android.";
                return;
            }

            // Если сохранённая папка ещё существует, открываем сразу её — так проще
            // поправить список, чем каждый раз идти от корня.
            string[] savedFolders = FrameSettings.LocalFolderPaths;
            _currentDirectory = savedFolders.Length > 0 && Directory.Exists(savedFolders[0])
                ? savedFolders[0]
                : PrimaryStorageRoot;

            ShowDirectory(_currentDirectory);
        }

        private static async Task<bool> EnsureStoragePermissionAsync()
        {
            PermissionStatus permissionStatus =
                await Permissions.CheckStatusAsync<Permissions.StorageRead>().ConfigureAwait(true);

            if (permissionStatus != PermissionStatus.Granted)
            {
                permissionStatus =
                    await Permissions.RequestAsync<Permissions.StorageRead>().ConfigureAwait(true);
            }

            return permissionStatus == PermissionStatus.Granted;
        }

        /// <summary>
        /// Открывает папку. Подсчёт изображений рекурсивный, поэтому выполняется в фоне:
        /// на каталоге с тысячами файлов синхронный обход подвешивал бы интерфейс.
        /// </summary>
        private async void ShowDirectory(string directoryPath)
        {
            _currentDirectory = directoryPath;
            int scanGeneration = ++_scanGeneration;

            CurrentPathLabel.Text = directoryPath;
            CurrentFolderInfoLabel.Text = "подсчёт изображений...";
            _visibleFolders.Clear();
            UpdateAddCurrentFolderButton();
            UpdateSelectionSummary();

            DirectoryScan scan = await Task.Run(() => ScanDirectory(directoryPath))
                .ConfigureAwait(true);

            // Пользователь мог уже уйти в другую папку, пока считались файлы.
            if (scanGeneration != _scanGeneration)
            {
                return;
            }

            if (!scan.IsAccessible)
            {
                CurrentFolderInfoLabel.Text = "папка недоступна для чтения";
                return;
            }

            foreach ((string subdirectoryPath, string folderName, ImageTally tally) in scan.Subfolders)
            {
                _visibleFolders.Add(new FolderEntry(subdirectoryPath, folderName, tally)
                {
                    IsSelected = _selectedFolders.Contains(subdirectoryPath),
                });
            }

            CurrentFolderInfoLabel.Text = scan.CurrentFolderTally.Describe();
        }

        private static DirectoryScan ScanDirectory(string directoryPath)
        {
            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directoryPath);
            }
            catch (Exception listingFailure) when (
                listingFailure is IOException or UnauthorizedAccessException)
            {
                return new DirectoryScan(false, new List<(string, string, ImageTally)>(), default);
            }

            Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);
            var subfolders = new List<(string Path, string Name, ImageTally Tally)>(subdirectories.Length);

            foreach (string subdirectory in subdirectories)
            {
                string folderName = Path.GetFileName(subdirectory);

                // Скрытые служебные каталоги только засоряют список.
                if (folderName.StartsWith('.'))
                {
                    continue;
                }

                subfolders.Add((subdirectory, folderName, CountImages(subdirectory)));
            }

            return new DirectoryScan(true, subfolders, CountImages(directoryPath));
        }

        private sealed record DirectoryScan(
            bool IsAccessible,
            List<(string Path, string Name, ImageTally Tally)> Subfolders,
            ImageTally CurrentFolderTally);

        private void UpdateAddCurrentFolderButton()
        {
            AddCurrentFolderButton.Text = _selectedFolders.Contains(_currentDirectory)
                ? "Убрать эту папку"
                : "Добавить эту папку";
        }

        /// <summary>
        /// Считает изображения в папке и во всех вложенных.
        /// </summary>
        /// <remarks>
        /// Считать только верхний уровень было недостаточно: у папки вроде «Camera»
        /// снимки часто лежат в подкаталогах, и она выглядела пустой.
        /// </remarks>
        private static ImageTally CountImages(string directoryPath)
        {
            try
            {
                int directCount = 0;
                int nestedCount = 0;
                int inspectedCount = 0;

                // Тот же обход, что и у самого источника, поэтому счётчики здесь совпадают
                // с тем, что реально попадёт в слайд-шоу: каталоги с миниатюрами пропущены.
                foreach (string filePath in
                         MediaFileScanner.EnumerateMediaFiles(directoryPath, recurse: true))
                {
                    if (++inspectedCount > ImageCountScanLimit)
                    {
                        return new ImageTally(directCount, nestedCount, WasCapped: true);
                    }

                    if (string.Equals(
                            Path.GetDirectoryName(filePath),
                            directoryPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        directCount++;
                    }
                    else
                    {
                        nestedCount++;
                    }
                }

                return new ImageTally(directCount, nestedCount, WasCapped: false);
            }
            catch (Exception scanFailure) when (
                scanFailure is IOException or UnauthorizedAccessException)
            {
                return new ImageTally(0, 0, WasCapped: false);
            }
        }

        private void OnFolderSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.Count == 0 || e.CurrentSelection[0] is not FolderEntry entry)
            {
                return;
            }

            // Снимаем выделение, иначе повторный вход в ту же папку не сработает.
            FolderList.SelectedItem = null;
            ShowDirectory(entry.FullPath);
        }

        private void OnToggleFolderClicked(object? sender, EventArgs e)
        {
            if (sender is not Button { CommandParameter: FolderEntry entry })
            {
                return;
            }

            if (!_selectedFolders.Remove(entry.FullPath))
            {
                _selectedFolders.Add(entry.FullPath);
            }

            entry.IsSelected = _selectedFolders.Contains(entry.FullPath);
            UpdateSelectionSummary();
        }

        private void OnAddCurrentFolderClicked(object? sender, EventArgs e)
        {
            if (!_selectedFolders.Remove(_currentDirectory))
            {
                _selectedFolders.Add(_currentDirectory);
            }

            UpdateAddCurrentFolderButton();
            UpdateSelectionSummary();
        }

        private void OnGoUpClicked(object? sender, EventArgs e)
        {
            string? parentDirectory = Path.GetDirectoryName(_currentDirectory);

            // Выше внутренней памяти подниматься некуда: там всё равно ничего не прочитать.
            if (string.IsNullOrEmpty(parentDirectory)
                || !_currentDirectory.StartsWith(PrimaryStorageRoot, StringComparison.Ordinal)
                || _currentDirectory.Equals(PrimaryStorageRoot, StringComparison.Ordinal))
            {
                return;
            }

            ShowDirectory(parentDirectory);
        }

        private void UpdateSelectionSummary()
        {
            SelectionSummaryLabel.Text = $"Выбрано папок: {_selectedFolders.Count}";
            SelectionDetailLabel.Text = _selectedFolders.Count == 0
                ? "Отметьте папки кнопкой + справа"
                : string.Join("\n", _selectedFolders);
        }

        private async void OnDoneClicked(object? sender, EventArgs e)
        {
            var selectedFolders = new string[_selectedFolders.Count];
            _selectedFolders.CopyTo(selectedFolders);
            FrameSettings.LocalFolderPaths = selectedFolders;

            await Shell.Current.GoToAsync("..");
        }
    }
}
