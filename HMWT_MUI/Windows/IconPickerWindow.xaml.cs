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

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Выбор иконки для чата. Только иконки приложения (SVG-геометрия), без
    /// эмодзи-символов: выбранная иконка рисуется в PNG и уходит картинкой,
    /// поэтому в ленте выглядит крупно и одинаково у всех.
    /// </summary>
    public partial class IconPickerWindow : Window
    {
        private readonly List<(Border Tile, string Kind)> _tiles = new List<(Border, string)>();
        private string _selected;

        public string SelectedKind => _selected;

        public IconPickerWindow()
        {
            InitializeComponent();
            Refresh();
        }

        private void Refresh()
        {
            IconsHost.Children.Clear();
            _tiles.Clear();
            _selected = null;

            string query = (SearchBox?.Text ?? "").Trim();
            var names = IconLibrary.Names
                .Where(n => query.Length == 0 || n.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList();

            foreach (string kind in names)
            {
                var icon = new IconView
                {
                    Kind = kind,
                    IconSize = 26,
                    IconBrush = (Brush)FindResource("B.Ink"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var tile = new Border
                {
                    Width = 56,
                    Height = 56,
                    Margin = new Thickness(4),
                    Background = (Brush)FindResource("B.Slate"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Cursor = Cursors.Hand,
                    ToolTip = kind,
                    Child = icon
                };

                tile.MouseLeftButtonUp += (s, e) => Select(kind);

                _tiles.Add((tile, kind));
                IconsHost.Children.Add(tile);
            }

            StatusText.Text = names.Count == 0
                ? "Иконок по запросу не нашлось"
                : "Иконок: " + names.Count;
        }

        private void Select(string kind)
        {
            _selected = kind;
            StatusText.Text = "Выбрано: " + kind + " — нажмите «Отправить»";

            foreach (var (tile, entry) in _tiles)
            {
                bool on = string.Equals(entry, kind, StringComparison.Ordinal);
                tile.BorderBrush = (Brush)FindResource(on ? "B.Accent" : "B.Line");
                tile.BorderThickness = new Thickness(on ? 2 : 1);
            }
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selected))
            {
                StatusText.Text = "Сначала выберите иконку";
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

        /// <summary>Рисует иконку в PNG: в чате она показывается как картинка.</summary>
        public static byte[] RenderIcon(string kind, int size = 256)
        {
            try
            {
                var view = new IconView
                {
                    Kind = kind,
                    IconSize = size,
                    IconBrush = Brushes.White,
                    Width = size,
                    Height = size
                };

                view.Measure(new Size(size, size));
                view.Arrange(new Rect(0, 0, size, size));
                view.UpdateLayout();

                var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using var stream = new MemoryStream();
                encoder.Save(stream);
                return stream.ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IconPicker] RenderIcon: {ex.Message}");
                return null;
            }
        }
    }
}
