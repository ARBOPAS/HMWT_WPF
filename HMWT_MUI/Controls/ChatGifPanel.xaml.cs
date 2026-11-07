using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMWT_MUI.Services;
using Microsoft.Win32;

namespace HMWT_MUI.Controls
{
    /// <summary>
    /// Панель избранных гифок для всплывающего окна у кнопки «Гифки».
    /// Клик по гифке сразу отправляет её в чат — отдельная кнопка не нужна.
    /// </summary>
    public partial class ChatGifPanel : UserControl
    {
        private readonly List<(Border Tile, ChatFavorite Item)> _tiles = new List<(Border, ChatFavorite)>();

        /// <summary>Выбрали гифку: полный путь к файлу.</summary>
        public event Action<string> GifClicked;

        public ChatGifPanel()
        {
            InitializeComponent();
            Loaded += (s, e) => Refresh();
        }

        public void Refresh()
        {
            TilesHost.Children.Clear();
            _tiles.Clear();

            var items = ChatFavoritesService.Load();
            string query = (SearchBox?.Text ?? "").Trim();

            var shown = query.Length == 0
                ? items
                : items.FindAll(f => (f.Name ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);

            foreach (var item in shown)
            {
                var tile = BuildTile(item);
                _tiles.Add((tile, item));
                TilesHost.Children.Add(tile);
            }

            bool empty = shown.Count == 0;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = items.Count == 0
                ? "Пока пусто. Отправьте гифку в чат или нажмите «Добавить…»."
                : "По запросу «" + query + "» ничего не найдено.";

            StatusText.Text = items.Count == 0 ? "" : "Гифок: " + items.Count;
        }

        private Border BuildTile(ChatFavorite item)
        {
            string full = ChatFavoritesService.Resolve(item.File);

            var image = new Image { Stretch = Stretch.UniformToFill };
            if (full != null)
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(full);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image.Source = bitmap;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Гифки] превью: {ex.Message}");
                }
            }

            var preview = new Border
            {
                Height = 96,
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                ClipToBounds = true,
                Background = (Brush)FindResource("B.Slate"),
                Child = image
            };

            var caption = new TextBlock
            {
                Text = item.Name ?? item.File,
                FontSize = 10,
                Foreground = (Brush)FindResource("B.Mute"),
                Margin = new Thickness(5, 3, 5, 5),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var content = new StackPanel();
            content.Children.Add(preview);
            content.Children.Add(caption);

            var delete = new Button
            {
                Content = "✕",
                Width = 20,
                Height = 20,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Убрать из избранного",
                Background = (Brush)FindResource("B.Slate"),
                Foreground = (Brush)FindResource("B.Mute"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1)
            };
            delete.Click += (s, e) =>
            {
                e.Handled = true;
                RemoveFavorite(item);
            };

            var overlay = new Grid();
            overlay.Children.Add(content);
            overlay.Children.Add(delete);

            var tile = new Border
            {
                Width = 128,
                Background = (Brush)FindResource("B.PanelHi"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                ToolTip = "Клик — отправить в чат",
                Child = overlay
            };

            tile.MouseLeftButtonUp += (s, e) =>
            {
                if (FindButton(e.OriginalSource as DependencyObject) != null) return;
                string path = ChatFavoritesService.Resolve(item.File);
                if (path != null) GifClicked?.Invoke(path);
            };

            return tile;
        }

        private static Button FindButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button button) return button;
                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private void RemoveFavorite(ChatFavorite item)
        {
            var owner = Window.GetWindow(this);
            string text = "Убрать «" + (item.Name ?? item.File) + "» из избранного?";

            bool confirmed = owner == null
                ? MessageBox.Show(text, "Избранные гифки", MessageBoxButton.YesNo) == MessageBoxResult.Yes
                : Windows.MessageDialog.Confirm(owner, text, "Избранные гифки", "Убрать", "Отмена", true);

            if (!confirmed) return;

            ChatFavoritesService.Remove(item.File);
            Refresh();
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Гифка в избранное",
                Filter = "Гифки (*.gif;*.webp;*.apng)|*.gif;*.webp;*.apng|Все картинки|*.gif;*.webp;*.png;*.jpg;*.jpeg"
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            string saved = ChatFavoritesService.Save(dialog.FileName);
            if (string.IsNullOrEmpty(saved))
            {
                StatusText.Text = "Не удалось добавить гифку";
                return;
            }

            Refresh();
            StatusText.Text = "Добавлено: " + Path.GetFileName(dialog.FileName);
        }
    }
}
