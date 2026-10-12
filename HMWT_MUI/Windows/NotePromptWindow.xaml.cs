using System.Windows;
using System.Windows.Input;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Единое модальное окно для ввода строки, подтверждения и простых сообщений.
    /// Отдельное окно (HWND) гарантированно рисуется поверх содержимого вкладки.
    /// </summary>
    public partial class NotePromptWindow : Window
    {
        private NotePromptWindow()
        {
            InitializeComponent();
        }

        #region === СТАТИЧЕСКИЕ ФАБРИКИ ===

        /// <summary>Запрос строки. Возвращает null при отмене.</summary>
        public static string AskText(Window owner, string caption, string header,
            string initialText = "", string okText = "Создать")
        {
            var window = new NotePromptWindow { Owner = owner ?? Application.Current?.MainWindow, Title = caption };
            window.HeaderText.Text = header;
            window.InputBox.Text = initialText ?? "";
            window.OkButton.Content = okText;
            window.CancelButton.Content = "Отмена";
            window.Loaded += (s, e) =>
            {
                window.Activate();
                window.InputBox.Focus();
                window.InputBox.SelectAll();
            };

            return window.ShowDialog() == true ? window.InputBox.Text.Trim() : null;
        }

        /// <summary>Подтверждение действия.</summary>
        public static bool Confirm(Window owner, string header, string message,
            string okText = "Удалить", bool danger = true)
        {
            var window = new NotePromptWindow { Owner = owner ?? Application.Current?.MainWindow, Title = header };
            window.HeaderText.Text = header;
            window.HeaderText.Foreground = danger
                ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C0564F"))
                : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4FB6AC"));
            window.MessageText.Text = message;
            window.MessageText.Visibility = Visibility.Visible;
            window.InputBox.Visibility = Visibility.Collapsed;
            window.OkButton.Content = okText;
            window.OkButton.Background = danger
                ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C0564F"))
                : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4FB6AC"));
            window.OkButton.Foreground = danger
                ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0C0C12"))
                : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0C0C12"));

            window.Loaded += (s, e) => window.OkButton.Focus();
            return window.ShowDialog() == true;
        }

        /// <summary>Простое сообщение с одной кнопкой.</summary>
        public static void Message(Window owner, string header, string message, string okText = "Понятно")
        {
            var window = new NotePromptWindow { Owner = owner ?? Application.Current?.MainWindow, Title = header };
            window.HeaderText.Text = header;
            window.MessageText.Text = message;
            window.MessageText.Visibility = Visibility.Visible;
            window.InputBox.Visibility = Visibility.Collapsed;
            window.CancelButton.Visibility = Visibility.Collapsed;
            window.OkButton.Content = okText;
            window.Loaded += (s, e) => window.OkButton.Focus();
            window.ShowDialog();
        }

        #endregion

        #region === ОБРАБОТЧИКИ ===

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed && ReferenceEquals(e.OriginalSource, RootBorder))
                DragMove();
        }

        #endregion
    }
}
