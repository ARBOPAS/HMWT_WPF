using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMWT_MUI.Helpers;
using HMWT_MUI.Services;
using Microsoft.Win32;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Библиотека готовых жетонов: бочка, стол, сундук, «Старик».
    ///
    /// Нужна, чтобы не искать картинку заново каждый раз. Файлы лежат в папке
    /// данных приложения (Tokens), поэтому их видит и WebGL карты.
    ///
    /// Окно возвращает выбранный шаблон, а жетон на карту ставит MapView:
    /// у библиотеки нет доступа к сцене, и это правильно — она только склад.
    /// </summary>
    public partial class TokenLibraryWindow : Window
    {
        private readonly List<Border> _tiles = new List<Border>();
        private MapTokenTemplate _selected;

        public TokenLibraryWindow()
        {
            InitializeComponent();

            RefreshTiles();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) DialogResult = false;
            };
        }

        /// <summary>Выбранный жетон; null — окно закрыли без выбора.</summary>
        public MapTokenTemplate Selected => _selected;

        private void RefreshTiles()
        {
            string keepId = _selected?.Id;
            _selected = null;

            TilesHost.Children.Clear();
            _tiles.Clear();

            var all = MapTokenLibrary.Load();
            string query = (SearchBox?.Text ?? "").Trim();
            var templates = query.Length == 0
                ? all
                : all.Where(t => (t.Name ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                     .ToList();

            foreach (var template in templates)
            {
                var tile = BuildTile(template);
                _tiles.Add(tile);
                TilesHost.Children.Add(tile);
            }

            bool nothing = templates.Count == 0;
            EmptyText.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;

            if (nothing)
            {
                EmptyText.Text = all.Count == 0
                    ? "Пока пусто. Нажмите «Добавить…» и выберите картинку — бочку, сундук, портрет НПС."
                    : "По запросу «" + query + "» ничего не найдено.";
            }

            StatusText.Text = all.Count == 0
                ? ""
                : (query.Length == 0
                    ? "Жетонов: " + all.Count
                    : "Найдено: " + templates.Count + " из " + all.Count);

            // Возвращаем выбор, если запись осталась в списке.
            if (!string.IsNullOrEmpty(keepId))
            {
                var again = templates.FirstOrDefault(t => string.Equals(t.Id, keepId, StringComparison.Ordinal));
                if (again != null) Select(again);
            }
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => RefreshTiles();

        private void ReplaceImage_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;

            var dialog = new OpenFileDialog
            {
                Title = "Новая картинка жетона",
                Filter = "Картинки|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Все файлы|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            var updated = MapTokenLibrary.ReplaceImage(_selected.Id, dialog.FileName);
            if (updated == null)
            {
                StatusText.Text = "Не удалось заменить картинку";
                return;
            }

            RefreshTiles();
            StatusText.Text = "Картинка обновлена";
        }

        private Border BuildTile(MapTokenTemplate template)
        {
            var tile = new Border
            {
                Width = 142,
                Height = 178,
                Margin = new Thickness(0, 0, 10, 10),
                CornerRadius = new CornerRadius(10),
                Background = Res("B.PanelHi", Color.FromRgb(0x1E, 0x25, 0x40)),
                BorderBrush = Res("B.Line", Color.FromRgb(0x2B, 0x33, 0x52)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = template,
                Padding = new Thickness(7)
            };

            var stack = new StackPanel();

            var picture = new Border
            {
                Height = 116,
                CornerRadius = new CornerRadius(8),
                Background = Res("B.Slate", Color.FromRgb(0x11, 0x17, 0x29)),
                ClipToBounds = true,
                Child = BuildImage(template)
            };
            stack.Children.Add(picture);

            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(template.Name) ? "Без названия" : template.Name,
                FontSize = 11.5,
                Foreground = Res("B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5)),
                Margin = new Thickness(2, 7, 2, 0),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34,
                ToolTip = template.Name
            });

            tile.Child = stack;

            tile.MouseLeftButtonDown += (s, e) =>
            {
                Select(template);
                if (e.ClickCount >= 2) Accept();
            };

            return tile;
        }

        private UIElement BuildImage(MapTokenTemplate template)
        {
            string full = MapTokenLibrary.Resolve(template);
            if (string.IsNullOrEmpty(full))
            {
                return new TextBlock
                {
                    Text = "нет файла",
                    FontSize = 10.5,
                    Foreground = Res("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 220;
                image.StreamSource = new MemoryStream(File.ReadAllBytes(full));
                image.EndInit();
                image.Freeze();

                return new Image
                {
                    Source = image,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(3)
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TokenLibrary] картинка: {ex.Message}");
                return new TextBlock
                {
                    Text = "битая картинка",
                    FontSize = 10.5,
                    Foreground = Res("B.Danger", Color.FromRgb(0xE0, 0x6A, 0x63)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        private void Select(MapTokenTemplate template)
        {
            _selected = template;

            foreach (Border tile in _tiles)
            {
                bool on = ReferenceEquals(tile.Tag, template);
                tile.BorderBrush = on
                    ? Res("B.Accent", Color.FromRgb(0x5F, 0xD4, 0xDD))
                    : Res("B.Line", Color.FromRgb(0x2B, 0x33, 0x52));
                tile.BorderThickness = new Thickness(on ? 2 : 1);
            }
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Картинка для жетона",
                Filter = "Картинки|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Все файлы|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            string suggested = Path.GetFileNameWithoutExtension(dialog.FileName);
            string name = NotePromptWindow.AskText(this, "Новый жетон",
                "Как назвать жетон?", suggested, "Добавить");
            if (name == null) return;

            var template = MapTokenLibrary.Add(name, dialog.FileName, 1);
            if (template == null)
            {
                StatusText.Text = "Не удалось добавить картинку";
                return;
            }

            RefreshTiles();
            Select(template);
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;

            string name = NotePromptWindow.AskText(this, "Переименовать жетон",
                "Новое название", _selected.Name, "Сохранить");
            if (string.IsNullOrWhiteSpace(name)) return;

            MapTokenLibrary.Rename(_selected.Id, name);
            string id = _selected.Id;
            RefreshTiles();
            Select(MapTokenLibrary.Load().FirstOrDefault(t => t.Id == id));
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;

            bool ok = NotePromptWindow.Confirm(this, "Удалить жетон из библиотеки",
                "«" + _selected.Name + "» пропадёт из библиотеки. Поставленные на карту жетоны останутся.");
            if (!ok) return;

            MapTokenLibrary.Remove(_selected.Id);
            _selected = null;
            RefreshTiles();
        }

        private void Place_Click(object sender, RoutedEventArgs e) => Accept();

        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Accept()
        {
            if (_selected == null) return;
            DialogResult = true;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Тянем окно за пустую шапку, но не за кнопки и плитки.
            if (e.OriginalSource is Button) return;
            try { DragMove(); } catch { }
        }

        private static Brush Res(string key, Color fallback) => PaletteBrush.Get(key, fallback);
    }
}
