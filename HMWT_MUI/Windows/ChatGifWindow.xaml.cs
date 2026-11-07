using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMWT_MUI.Services;
using Microsoft.Win32;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Избранные гифки: плитки с превью, клик — отправить в чат, крестик —
    /// убрать. Копии лежат в %APPDATA%\HMWT\ChatFavorites, поэтому отправить
    /// гифку второй раз можно без проводника.
    /// </summary>
    public partial class ChatGifWindow : Window
    {
        private readonly List<(Border Tile, ChatFavorite Item)> _tiles =
            new List<(Border, ChatFavorite)>();

        private ChatFavorite _selected;

        /// <summary>Выбранная гифка (после «Отправить» или двойного клика).</summary>
        public ChatFavorite Selected => _selected;

        /// <summary>Полный путь к выбранной гифке.</summary>
        public string SelectedPath => _selected == null ? null : ChatFavoritesService.Resolve(_selected.File);

        public ChatGifWindow()
        {
            InitializeComponent();
            Refresh();
        }

        private void Refresh()
        {
            TilesHost.Children.Clear();
            _tiles.Clear();
            _selected = null;

            var items = ChatFavoritesService.Load();
            foreach (var item in items)
            {
                var tile = BuildTile(item);
                _tiles.Add((tile, item));
                TilesHost.Children.Add(tile);
            }

            bool empty = items.Count == 0;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = empty ? "" : "Гифок в избранном: " + items.Count;
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
                Height = 110,
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                ClipToBounds = true,
                Background = (Brush)FindResource("B.Slate"),
                Child = image
            };

            var caption = new TextBlock
            {
                Text = item.Name ?? item.File,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("B.Mute"),
                Margin = new Thickness(6, 4, 6, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Name
            };

            var content = new StackPanel();
            content.Children.Add(preview);
            content.Children.Add(caption);

            var delete = new Button
            {
                Content = "✕",
                Width = 22,
                Height = 22,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
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
                Width = 150,
                Background = (Brush)FindResource("B.PanelHi"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                ToolTip = "Клик — отправить в чат",
                Child = overlay
            };

            tile.MouseLeftButtonUp += (s, e) =>
            {
                if (FindButton(e.OriginalSource as DependencyObject) != null) return;
                Select(item);
            };

            tile.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2 && FindButton(e.OriginalSource as DependencyObject) == null)
                    SendSelected();
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

        private void Select(ChatFavorite item)
        {
            _selected = item;
            StatusText.Text = "Выбрано: " + (item.Name ?? item.File);

            foreach (var (tile, entry) in _tiles)
            {
                bool on = ReferenceEquals(entry, item);
                tile.BorderBrush = (Brush)FindResource(on ? "B.Accent" : "B.Line");
                tile.BorderThickness = new Thickness(on ? 2 : 1);
            }
        }

        private void RemoveFavorite(ChatFavorite item)
        {
            if (!MessageDialog.Confirm(this, "Убрать «" + (item.Name ?? item.File) + "» из избранного?",
                    "Избранные гифки", "Убрать", "Отмена", true))
                return;

            ChatFavoritesService.Remove(item.File);
            Refresh();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Гифка в избранное",
                Filter = "Гифки (*.gif;*.webp;*.apng)|*.gif;*.webp;*.apng|Все картинки|*.gif;*.webp;*.png;*.jpg;*.jpeg"
            };

            if (dialog.ShowDialog(this) != true) return;

            string saved = ChatFavoritesService.Save(dialog.FileName);
            if (string.IsNullOrEmpty(saved))
            {
                StatusText.Text = "Не удалось добавить гифку";
                return;
            }

            Refresh();
            StatusText.Text = "Добавлено: " + Path.GetFileName(dialog.FileName);
        }

        private void Send_Click(object sender, RoutedEventArgs e) => SendSelected();

        private void SendSelected()
        {
            if (_selected == null)
            {
                StatusText.Text = "Сначала выберите гифку";
                return;
            }

            if (string.IsNullOrEmpty(SelectedPath))
            {
                StatusText.Text = "Файл гифки не найден";
                return;
            }

            DialogResult = true;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
        }
    }
}
