using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Своя шапка окна вместо системной: приложение тёмное, и белая полоса
    /// Windows выбивается. Перетаскивание и изменение размера остаются.
    /// </summary>
    public static class DarkWindowChrome
    {
        public static void Apply(Window window, string title, UIElement body,
            Brush panelHi, Brush line, Brush ink)
        {
            if (window == null || body == null) return;

            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.CanResize;
            window.Background = window.Background ?? Brushes.Black;

            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(window, chrome);

            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Border
            {
                Background = panelHi,
                BorderBrush = line,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var headerGrid = new Grid();

            headerGrid.Children.Add(new TextBlock
            {
                Text = title ?? "",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = ink,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            });

            var close = DmUiKit.Icon("✕", (s, e) => window.Close(), "Закрыть", 30,
                DmUiKit.ButtonKind.Ghost);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Center;
            close.Margin = new Thickness(0, 0, 4, 0);
            headerGrid.Children.Add(close);

            header.Child = headerGrid;
            header.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    window.WindowState = window.WindowState == WindowState.Maximized
                        ? WindowState.Normal : WindowState.Maximized;
                    return;
                }
                try { window.DragMove(); } catch { }
            };

            shell.Children.Add(header);
            Grid.SetRow(body, 1);
            shell.Children.Add(body);

            window.Content = new Border
            {
                BorderBrush = line,
                BorderThickness = new Thickness(1),
                Background = window.Background,
                Child = shell
            };
        }
    }
}
