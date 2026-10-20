using System;
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
    /// Всплывающее уведомление отряда в правом нижнем углу.
    ///
    /// Окно не забирает фокус (ShowActivated=false): можно продолжать печатать
    /// в другом окне. Клик по карточке открывает вкладку «Отряд», крестик
    /// просто закрывает. Само закрывается через несколько секунд.
    /// </summary>
    public sealed class NotificationWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private bool _closeOnly;

        public event Action Clicked;

        public NotificationWindow(string heading, string author, string text, string detail)
        {
            Title = "HMWT · уведомление";
            Width = 360;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var accent = new SolidColorBrush(Color.FromRgb(0x5F, 0xD4, 0xDD));
            var frost = new SolidColorBrush(Color.FromRgb(0x7F, 0xD9, 0xE8));
            var ink = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF8));
            var mute = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xB4));

            var close = new TextBlock
            {
                Text = "✕",
                Foreground = mute,
                FontSize = 12,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Закрыть уведомление"
            };
            close.MouseLeftButtonUp += (s, e) =>
            {
                _closeOnly = true;
                e.Handled = true;
                Close();
            };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var kind = new TextBlock
            {
                Text = (heading ?? "ОТРЯД").ToUpperInvariant(),
                Foreground = accent,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(kind, 0);
            head.Children.Add(kind);

            var who = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(author) ? "Участник" : author,
                Foreground = ink,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(who, 1);
            head.Children.Add(who);

            Grid.SetColumn(close, 2);
            head.Children.Add(close);

            var body = new TextBlock
            {
                Text = text ?? "",
                Foreground = ink,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            System.Windows.Automation.AutomationProperties.SetName(body, "Уведомление отряда: " + (text ?? ""));

            var root = new StackPanel();
            root.Children.Add(head);
            root.Children.Add(body);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                root.Children.Add(new TextBlock
                {
                    Text = detail,
                    Foreground = mute,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x15, 0x1B, 0x2E)),
                BorderBrush = frost,
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 10, 10),
                Child = root,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Opacity = 0.55
                }
            };

            Content = card;

            MouseLeftButtonUp += (s, e) =>
            {
                if (_closeOnly) return;
                try { Clicked?.Invoke(); } catch { }
                Close();
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _timer.Tick += (s, e) => { _timer.Stop(); Close(); };

            Loaded += (s, e) =>
            {
                // Позиция по правому нижнему краю рабочего стола, без панели задач
                var area = SystemParameters.WorkArea;
                Left = Math.Max(0, area.Right - ActualWidth - 16);
                Top = Math.Max(0, area.Bottom - ActualHeight - 16);

                Opacity = 0;
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                _timer.Start();
            };

            MouseEnter += (s, e) => _timer.Stop();
            MouseLeave += (s, e) => { if (IsVisible) _timer.Start(); };
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }
    }
}
