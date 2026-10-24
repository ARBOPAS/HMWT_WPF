using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Окно выбора состояний жетона: галочки из справочника трекера.
    /// Одна функция «состояния» на всё приложение, поэтому и список тот же.
    /// </summary>
    public sealed class ConditionsWindow : Window
    {
        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF5));
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x1F));
        private static readonly Brush ButtonBg = new SolidColorBrush(Color.FromRgb(0x22, 0x2B, 0x45));

        private readonly List<(string Key, CheckBox Box)> _boxes = new List<(string, CheckBox)>();
        private List<string> _result;

        private ConditionsWindow()
        {
            Title = "Состояния";
            Width = 320;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Bg;
            Foreground = Ink;
        }

        /// <summary>Выбранные ключи или null, если окно закрыли без сохранения.</summary>
        public static List<string> Pick(Window owner, string title, IEnumerable<string> selected)
        {
            var window = new ConditionsWindow();
            if (owner != null && owner.IsVisible) window.Owner = owner;
            if (!string.IsNullOrWhiteSpace(title)) window.Title = title;

            var current = new HashSet<string>(
                (selected ?? Enumerable.Empty<string>())
                    .Select(x => (x ?? "").Trim())
                    .Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            var list = new StackPanel { Margin = new Thickness(16, 12, 16, 4) };
            foreach (var option in CharacterStateService.LoadConditions(0))
            {
                if (option == null || string.IsNullOrWhiteSpace(option.Key)) continue;

                var box = new CheckBox
                {
                    Content = option.Name,
                    IsChecked = current.Contains(option.Key),
                    Margin = new Thickness(0, 5, 0, 5),
                    FontSize = 13,
                    Foreground = Ink,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                window._boxes.Add((option.Key, box));
                list.Children.Add(box);
            }

            var scroll = new ScrollViewer
            {
                Content = list,
                MaxHeight = 430,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var ok = MakeButton("Сохранить", 110);
            ok.Click += (s, e) =>
            {
                window._result = window._boxes
                    .Where(b => b.Box.IsChecked == true)
                    .Select(b => b.Key)
                    .ToList();
                window.DialogResult = true;
            };

            var cancel = MakeButton("Отмена", 90);
            cancel.Click += (s, e) => window.DialogResult = false;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 8, 16, 14)
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new StackPanel();
            root.Children.Add(scroll);
            root.Children.Add(buttons);
            window.Content = root;

            return window.ShowDialog() == true ? (window._result ?? new List<string>()) : null;
        }

        private static Button MakeButton(string text, double width) => new Button
        {
            Content = text,
            MinWidth = width,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Background = ButtonBg,
            Foreground = Ink,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x68)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }
}
