using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoFrame
{
    /// <summary>
    /// Строка в списке папок на экране выбора.
    /// </summary>
    public sealed class FolderEntry : INotifyPropertyChanged
    {
        private bool _isSelected;

        public FolderEntry(string fullPath, string displayName, ImageTally imageTally)
        {
            FullPath = fullPath;
            DisplayName = displayName;
            ImageTally = imageTally;
        }

        public string FullPath { get; }

        public string DisplayName { get; }

        public ImageTally ImageTally { get; }

        /// <summary>Подпись под именем папки.</summary>
        public string ImageCountText => ImageTally.Describe();

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(SelectionGlyph));
                RaisePropertyChanged(nameof(SelectionColor));
            }
        }

        /// <summary>Галочка у выбранной папки, плюс — у невыбранной.</summary>
        public string SelectionGlyph => IsSelected ? "✓" : "+";

        public string SelectionColor => IsSelected ? "#4FC3F7" : "#555555";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Сколько изображений в папке: отдельно в ней самой и отдельно во вложенных.
    /// </summary>
    /// <param name="DirectCount">Лежат непосредственно в папке.</param>
    /// <param name="NestedCount">Лежат во вложенных папках.</param>
    /// <param name="WasCapped">
    /// Обход был прерван по лимиту — значит, реальные числа могут быть больше.
    /// </param>
    public readonly record struct ImageTally(int DirectCount, int NestedCount, bool WasCapped)
    {
        /// <summary>Человекочитаемая подпись под именем папки.</summary>
        public string Describe()
        {
            string suffix = WasCapped ? "+" : string.Empty;

            if (DirectCount == 0 && NestedCount == 0)
            {
                return "нет изображений";
            }

            if (DirectCount == 0)
            {
                return $"во вложенных: {NestedCount}{suffix}";
            }

            return NestedCount == 0
                ? $"изображений: {DirectCount}{suffix}"
                : $"изображений: {DirectCount}, во вложенных: {NestedCount}{suffix}";
        }
    }
}
