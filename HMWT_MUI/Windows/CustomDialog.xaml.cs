using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Кастомное диалоговое окно в стиле HMWT
    /// </summary>
    public class CustomDialog : Window
    {
        private Border _mainBorder;
        private TextBlock _messageText;
        private Button _btnYes;
        private Button _btnNo;
        private bool _result = false;

        public CustomDialog(string message, string title = "Подтверждение", string yesText = "Да", string noText = "Отмена")
        {
            InitializeWindow(message, title, yesText, noText);
        }

        private void InitializeWindow(string message, string title, string yesText, string noText)
        {
            // Основные настройки окна
            this.Title = title;
            this.Width = 420;
            this.Height = 220;
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.Background = Brushes.Transparent;
            this.ResizeMode = ResizeMode.NoResize;
            this.Topmost = true;

            // Основная панель с фоном
            _mainBorder = new Border
            {
                Background = (Brush)Application.Current.FindResource("B.Panel"),
                BorderBrush = (Brush)Application.Current.FindResource("B.GoldEdge"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 30,
                    ShadowDepth = 0,
                    Opacity = 0.7
                },
                Padding = new Thickness(24)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Заголовок
            var titleText = new TextBlock
            {
                Text = title.ToUpper(),
                FontFamily = (FontFamily)Application.Current.FindResource("F.Display"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("B.Gold"),
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(titleText, 0);
            grid.Children.Add(titleText);

            // Иконка (вопрос) — вектор вместо значка-звёздочки
            var iconText = new HMWT_MUI.Helpers.IconView
            {
                Kind = "book",
                IconSize = 30,
                IconBrush = (Brush)Application.Current.FindResource("B.Arcane"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };

            // Сообщение
            var messagePanel = new StackPanel { Orientation = Orientation.Horizontal };
            messagePanel.Children.Add(iconText);

            _messageText = new TextBlock
            {
                Text = message,
                FontFamily = (FontFamily)Application.Current.FindResource("F.Body"),
                FontSize = 13,
                Foreground = (Brush)Application.Current.FindResource("B.Ink"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 16)
            };
            messagePanel.Children.Add(_messageText);

            Grid.SetRow(messagePanel, 2);
            grid.Children.Add(messagePanel);

            // Кнопки
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            _btnNo = CreateButton(noText, Brushes.Transparent, (Brush)Application.Current.FindResource("B.Line"), (Brush)Application.Current.FindResource("B.Mute"));
            _btnNo.Margin = new Thickness(0, 0, 12, 0);
            _btnNo.Click += (s, e) => { _result = false; Close(); };

            _btnYes = CreateButton(yesText, (Brush)Application.Current.FindResource("B.Danger"), Brushes.Transparent, Brushes.White);
            _btnYes.Click += (s, e) => { _result = true; Close(); };

            btnPanel.Children.Add(_btnNo);
            btnPanel.Children.Add(_btnYes);

            Grid.SetRow(btnPanel, 3);
            grid.Children.Add(btnPanel);

            _mainBorder.Child = grid;
            this.Content = _mainBorder;

            // Закрытие по ESC
            this.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };

            // Анимация появления
            this.Opacity = 0;
            this.Loaded += (s, e) =>
            {
                var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(180));
                this.BeginAnimation(OpacityProperty, fadeIn);
            };
        }

        private Button CreateButton(string text, Brush bg, Brush border, Brush fg)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(16, 8, 16, 8),
                Cursor = Cursors.Hand,
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Foreground = fg,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.FindResource("F.Body")
            };

            btn.MouseEnter += (s, e) =>
            {
                if (bg == Brushes.Transparent)
                {
                    btn.Background = (Brush)Application.Current.FindResource("B.PanelHi");
                    btn.Foreground = (Brush)Application.Current.FindResource("B.Gold");
                }
                else
                {
                    btn.Opacity = 0.85;
                }
            };

            btn.MouseLeave += (s, e) =>
            {
                if (bg == Brushes.Transparent)
                {
                    btn.Background = Brushes.Transparent;
                    btn.Foreground = (Brush)Application.Current.FindResource("B.Mute");
                }
                else
                {
                    btn.Opacity = 1;
                }
            };

            return btn;
        }

        public bool ShowDialogCustom()
        {
            this.ShowDialog();
            return _result;
        }
    }
}