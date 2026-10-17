using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Просмотр ссылки из заметки внутри приложения.
    ///
    /// Нужен, когда в панели заметок выбран режим «открывать ссылки внутри»:
    /// страница заметки не уходит на чужой сайт (иначе редактор пришлось бы
    /// перезагружать), а ссылка показывается этим окном. Здесь есть кнопки
    /// «Назад», «Вперёд» и «Закрыть», поэтому в чужую страницу не «проваливаешься»
    /// без возврата.
    ///
    /// Окно одно: при переходе к другой заметке вкладка его закрывает.
    /// </summary>
    public partial class NoteLinkWindow : Window
    {
        private readonly string _startUrl;
        private bool _initialized;

        /// <summary>Окно с указанным адресом. Заголовок — по названию страницы.</summary>
        public NoteLinkWindow(string url, Window owner)
        {
            InitializeComponent();

            _startUrl = url ?? "";
            Owner = owner;

            AddressText.Text = _startUrl;
            BtnBack.IsEnabled = false;
            BtnForward.IsEnabled = false;
            BtnBack.Opacity = 0.45;
            BtnForward.Opacity = 0.45;

            Loaded += NoteLinkWindow_Loaded;
        }

        private async void NoteLinkWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                // Движок общий с редактором заметок: второе окно не ждёт его запуска
                var environment = await Helpers.WebView2Host.GetEnvironmentAsync();
                await Web.EnsureCoreWebView2Async(environment);

                var core = Web.CoreWebView2;
                if (core == null) return;

                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsZoomControlEnabled = true;

                // Сайт не должен открывать свои окна поверх заметок
                core.NewWindowRequested += (s, args) =>
                {
                    args.Handled = true;
                    OpenExternal(args.Uri);
                };

                core.NavigationStarting += Core_NavigationStarting;
                core.NavigationCompleted += Core_NavigationCompleted;
                core.DocumentTitleChanged += (s, args) => UpdateTitle(core.DocumentTitle);

                core.Navigate(_startUrl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Просмотр ссылки] движок не поднялся: " + ex);
                AddressText.Text = "Не удалось открыть страницу: " + ex.Message;
            }
        }

        /// <summary>
        /// В окне показываем только обычные сайты. Локальные файлы и хитрые схемы
        /// (mailto:, file:) отдаём Windows — иначе окно просто останется пустым.
        /// </summary>
        private void Core_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Uri)) return;

            if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;

            e.Cancel = true;
            OpenExternal(e.Uri);
        }

        private void Core_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var core = Web.CoreWebView2;
            if (core == null) return;

            BtnBack.IsEnabled = core.CanGoBack;
            BtnForward.IsEnabled = core.CanGoForward;
            AddressText.Text = core.Source;
            BtnBack.Opacity = core.CanGoBack ? 1 : 0.45;
            BtnForward.Opacity = core.CanGoForward ? 1 : 0.45;

            if (!e.IsSuccess) AddressText.Text = core.Source + "  (страница не открылась)";
        }

        private void UpdateTitle(string title)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Ссылка из заметки" : title;
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            var core = Web.CoreWebView2;
            if (core != null && core.CanGoBack) core.GoBack();
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            var core = Web.CoreWebView2;
            if (core != null && core.CanGoForward) core.GoForward();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }

            if (e.Key == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                e.Handled = true;
                BtnBack_Click(sender, e);
            }
            else if (e.Key == Key.Right && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                e.Handled = true;
                BtnForward_Click(sender, e);
            }
        }

        private static void OpenExternal(string url)
        {
            Helpers.NoteLinks.Open(url);
        }
    }
}
