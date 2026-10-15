using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Модальный диалог в стиле приложения: затемнение + панель с золотой рамкой.
    /// Рисуется ВНУТРИ окна приложения, а не системным окном Windows.
    ///
    /// API синхронный — как у MessageBox, поэтому вызовы вида
    /// <c>if (MessageDialog.Confirm(this, "...")) { ... }</c> работают без переделки
    /// логики: пока диалог открыт, UI-поток крутит вложенный кадр диспетчера.
    /// </summary>
    public static class MessageDialog
    {
        /// <summary>Вопрос с двумя кнопками. true — подтверждение, false — отмена/ESC.</summary>
        public static bool Confirm(FrameworkElement host, string message,
            string title = "Подтверждение", string yesText = "Да", string noText = "Отмена",
            bool danger = true)
            => Show(host, message, title, yesText, noText, danger);

        /// <summary>Сообщение с одной кнопкой «Понятно».</summary>
        public static void Info(FrameworkElement host, string message, string title = "Сообщение")
            => Show(host, message, title, "Понятно", null, false);

        /// <summary>Сообщение об ошибке с одной кнопкой.</summary>
        public static void Error(FrameworkElement host, string message, string title = "Ошибка")
            => Show(host, message, title, "Понятно", null, true);

        private static bool Show(FrameworkElement host, string message, string title,
            string yesText, string noText, bool danger)
        {
            // Если передан не тот элемент — показываем системное окно,
            // чтобы сообщение не потерялось молча
            if (host == null)
                return Fallback(message, title, noText, danger);

            // Контейнер для оверлея: самый большой Grid внутри вью. Искать нужно ВНУТРЬ:
            // подъём вверх приводит к ContentPresenter окна, и диалог оказывается
            // вне видимого дерева вкладки.
            var hostRoot = FindHostPanel(host);
            if (hostRoot == null)
                return Fallback(message, title, noText, danger);

            // Ресурсы берём из области видимости хоста: дизайн-токены (B.Panel и т.п.)
            // подключаются вместе с окном
            var brushPanel = Res(host, "B.Panel", Color.FromRgb(0x15, 0x1B, 0x2E));
            var brushEdge = Res(host, "B.AccentEdge", Color.FromRgb(0x2A, 0x6A, 0x74));
            var brushAccent = Res(host, "B.Accent", Color.FromRgb(0x5F, 0xD4, 0xDD));
            var brushDanger = Res(host, "B.Danger", Color.FromRgb(0xE0, 0x6A, 0x63));
            var brushArcane = Res(host, "B.Arcane", Color.FromRgb(0xA9, 0x8B, 0xEC));
            var brushOnAccent = Res(host, "B.OnAccent", Color.FromRgb(0x04, 0x22, 0x2A));
            var brushInk = Res(host, "B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5));
            var brushLine = Res(host, "B.Line", Color.FromRgb(0x2B, 0x33, 0x52));
            var fontDisplay = FontRes(host, "F.Display", "Segoe UI Semibold");
            var fontBody = FontRes(host, "F.Body", "Segoe UI");

            bool result = false;
            bool answered = false;
            DispatcherFrame frame = null;

            // ── затемнение фона ──
            //
            // Без анимации прозрачности: пока Opacity меньше единицы, WPF рисует
            // содержимое в промежуточный буфер, и текст получается мыльным.
            // Диалог должен появляться сразу резким.
            var overlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x05, 0x07, 0x0E)),
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            Panel.SetZIndex(overlay, 4000);

            // ── панель ──
            //
            // ВАЖНО: никаких Effect (DropShadowEffect и подобных) на панели с текстом.
            // Любой эффект заставляет WPF отрисовать содержимое в промежуточный
            // битмап, и текст становится мыльным — именно это и было видно.
            // Вместо тени — рамка и более тёмное затемнение фона.
            var panel = new Border
            {
                Background = brushPanel,
                BorderBrush = brushEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(24),
                MaxWidth = 520,
                MinWidth = 340,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Заголовок
            // Текст должен быть резким: Display-режим включает хинтинг глифов,
            // а выравнивание по пикселям убирает размытие на дробных координатах
            TextOptions.SetTextFormattingMode(panel, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(panel, TextRenderingMode.ClearType);

            grid.Children.Add(new TextBlock
            {
                Text = (title ?? "").ToUpperInvariant(),
                FontFamily = fontDisplay,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = brushAccent,
                Margin = new Thickness(0, 0, 0, 14),
                // Заголовок тоже переносим: длинная строка в нём растягивала панель
                // далеко за края экрана, и текст сообщения становился нечитаемым
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460
            });

            // Значок сообщения: вектор вместо символа — предупреждение или книга
            var messageRow = new StackPanel { Orientation = Orientation.Horizontal };
            messageRow.Children.Add(new HMWT_MUI.Helpers.IconView
            {
                Kind = danger ? "warning" : "book",
                IconSize = 22,
                IconBrush = brushArcane,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 14, 0)
            });
            messageRow.Children.Add(new TextBlock
            {
                Text = message ?? "",
                FontFamily = fontBody,
                FontSize = 13,
                Foreground = brushInk,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            });
            Grid.SetRow(messageRow, 1);
            grid.Children.Add(messageRow);

            // Кнопки
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            Action<bool> finish = (answer) =>
            {
                if (answered) return;
                answered = true;
                result = answer;
                try { hostRoot.Children.Remove(overlay); } catch { }
                if (frame != null) frame.Continue = false;
            };

            if (!string.IsNullOrEmpty(noText))
            {
                var cancel = MakeButton(noText, accent: false, danger: false,
                    accentBrush: brushAccent, dangerBrush: brushDanger, onAccent: brushOnAccent,
                    ink: brushInk, line: brushLine, body: fontBody);
                cancel.Margin = new Thickness(0, 0, 10, 0);
                cancel.Click += (s, e) => finish(false);
                buttons.Children.Add(cancel);
            }

            var confirm = MakeButton(yesText, accent: true, danger: danger,
                accentBrush: brushAccent, dangerBrush: brushDanger, onAccent: brushOnAccent,
                ink: brushInk, line: brushLine, body: fontBody);
            confirm.Click += (s, e) => finish(true);
            buttons.Children.Add(confirm);

            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            panel.Child = grid;
            overlay.Children.Add(panel);

            // Клик по затемнению = отмена; ESC тоже
            overlay.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == overlay) finish(false); };
            overlay.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) finish(false); };

            hostRoot.Children.Add(overlay);

            // Корневой Grid окон разбит на строки и колонки: слева узкая рейка вкладок,
            // ниже шапка и содержимое. Без явного охвата оверлей попадает в ПЕРВУЮ
            // колонку и первую строку — то есть ровно в рейку вкладок шириной в
            // несколько десятков пикселей. Именно поэтому подтверждение выглядело
            // как окно внутри колонки вкладок.
            if (hostRoot is Grid hostGrid)
            {
                int rows = hostGrid.RowDefinitions.Count;
                int columns = hostGrid.ColumnDefinitions.Count;

                if (rows > 1) Grid.SetRowSpan(overlay, rows);
                if (columns > 1)
                {
                    Grid.SetColumn(overlay, 0);
                    Grid.SetColumnSpan(overlay, columns);
                }
            }

            overlay.Focusable = true;
            overlay.Loaded += (s, e) =>
            {
                overlay.Focus();
                confirm.Focus();
            };

            // Держим UI отзывчивым, пока диалог открыт
            frame = new DispatcherFrame();
            try { Dispatcher.PushFrame(frame); } catch { }

            return result;
        }

        private static bool Fallback(string message, string title, string noText, bool danger)
        {
            var res = MessageBox.Show(message, title,
                string.IsNullOrEmpty(noText) ? MessageBoxButton.OK : MessageBoxButton.YesNo,
                danger ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return res == MessageBoxResult.Yes || res == MessageBoxResult.OK;
        }

        /// <summary>
        /// Ищет подложку для оверлея — самый большой Panel ВСЕГО ОКНА.
        ///
        /// Искать внутри переданного элемента нельзя: у вкладки есть свой крупный Grid,
        /// и диалог ложился поверх него — то есть правее рейки вкладок, а не поверх окна.
        /// Поэтому сначала поднимаемся до корня окна, а уже от него ищем самую большую
        /// панель по площади: у окон приложения это корневой Grid.
        /// </summary>
        internal static Panel FindHostPanel(FrameworkElement host)
        {
            if (host == null) return null;

            // ── 1. Поднимаемся до корня окна ──
            DependencyObject root = host;
            var up = new Stack<DependencyObject>();
            while (root != null)
            {
                up.Push(root);
                if (root is Window) break;
                root = VisualTreeHelper.GetParent(root) ?? LogicalTreeHelper.GetParent(root);
            }

            if (root == null)
                up.Push(host);

            // ── 2. Опускаемся и выбираем панель с наибольшей площадью ──
            Panel best = null;
            double bestArea = -1;
            int guard = 0;

            while (up.Count > 0 && guard++ < 20000)
            {
                var current = up.Pop();

                if (current is Panel panel)
                {
                    double area = panel.ActualWidth * panel.ActualHeight;

                    // Площадь нужна не меньше половины окна: иначе это панель внутри
                    // вкладки, и диалог снова окажется не поверх всего содержимого
                    double minArea = 0.5 * WindowArea(current);
                    if (area > bestArea && area >= minArea)
                    {
                        bestArea = area;
                        best = panel;
                    }
                }

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; i++)
                    up.Push(VisualTreeHelper.GetChild(current, i));
            }

            return best ?? host as Panel;
        }

        /// <summary>Площадь окна, которому принадлежит элемент. 0 — если окно не найдено.</summary>
        private static double WindowArea(DependencyObject element)
        {
            DependencyObject current = element;
            int guard = 0;
            while (current != null && guard++ < 500)
            {
                if (current is Window w) return w.ActualWidth * w.ActualHeight;
                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }
            return 0;
        }

        /// <summary>Ищет ресурс-кисть в области видимости хоста, иначе отдаёт запасной цвет.</summary>
        private static Brush Res(FrameworkElement host, string key, Color fallback)
        {
            try
            {
                if (host != null && host.TryFindResource(key) is Brush b) return b;
                if (Application.Current != null && Application.Current.TryFindResource(key) is Brush ab) return ab;
            }
            catch { }
            return new SolidColorBrush(fallback);
        }

        /// <summary>Ищет шрифт в области видимости хоста, иначе отдаёт системный.</summary>
        private static FontFamily FontRes(FrameworkElement host, string key, string fallback)
        {
            try
            {
                if (host != null && host.TryFindResource(key) is FontFamily f) return f;
                if (Application.Current != null && Application.Current.TryFindResource(key) is FontFamily af) return af;
            }
            catch { }
            return new FontFamily(fallback);
        }

        /// <summary>
        /// Кнопка диалога. Обычное подтверждение красится акцентом темы, опасное —
        /// цветом опасности. Раньше было наоборот: опасное действие получало акцент,
        /// а обычное — «мороз», и красное предупреждение выглядело как согласие.
        /// </summary>
        private static Button MakeButton(string text, bool accent, bool danger,
            Brush accentBrush, Brush dangerBrush, Brush onAccent,
            Brush ink, Brush line, FontFamily body)
        {
            var brush = accent ? (danger ? dangerBrush : accentBrush) : line;

            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(18, 9, 18, 9),
                Cursor = Cursors.Hand,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = body,
                Background = accent ? brush : Brushes.Transparent,
                Foreground = accent ? onAccent : ink,
                BorderBrush = brush,
                BorderThickness = new Thickness(1)
            };

            btn.MouseEnter += (s, e) => btn.Opacity = 0.85;
            btn.MouseLeave += (s, e) => btn.Opacity = 1;
            return btn;
        }
    }
}
