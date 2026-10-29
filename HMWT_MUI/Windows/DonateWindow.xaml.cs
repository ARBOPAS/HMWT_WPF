using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Окно добровольной поддержки проекта: показывает реквизиты из
    /// <see cref="DonateInfo"/> и умеет их копировать.
    ///
    /// Логики здесь намеренно минимум: ни одной проверки «заплатил или нет»,
    /// ни одного обращения к сети, ни одной записи в базу. Окно только
    /// показывает текст и кладёт номер в буфер обмена.
    /// </summary>
    public partial class DonateWindow : Window
    {
        /// <summary>Сколько держится надпись «Скопировано» после нажатия.</summary>
        private static readonly TimeSpan CopyFeedbackTime = TimeSpan.FromSeconds(1.6);

        public DonateWindow()
        {
            InitializeComponent();
            FillFromSettings();
        }

        /// <summary>Раскладывает реквизиты по полям: пустые строки просто скрываются.</summary>
        private void FillFromSettings()
        {
            bool any = DonateInfo.IsConfigured;

            CardRow.Visibility = DonateInfo.HasCard ? Visibility.Visible : Visibility.Collapsed;
            SbpRow.Visibility = DonateInfo.HasSbp ? Visibility.Visible : Visibility.Collapsed;
            btnOpenLink.Visibility = DonateInfo.HasLink ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            RequisitesBlock.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

            txtCard.Text = DonateInfo.CardNumber;
            txtSbp.Text = DonateInfo.SbpPhone;

            // Подпись получателя нужна только вместе с номером карты: у СБП
            // имя всё равно покажет банк, а без карты строка висела бы одна
            RecipientText.Visibility = DonateInfo.HasRecipient ? Visibility.Visible : Visibility.Collapsed;
            RecipientText.Text = DonateInfo.HasRecipient ? "Получатель: " + DonateInfo.Recipient : "";

            btnOpenLink.Content = string.IsNullOrWhiteSpace(DonateInfo.LinkTitle)
                ? "Открыть страницу поддержки"
                : DonateInfo.LinkTitle;

            txtLinkUrl.Text = DonateInfo.LinkUrl;
            txtLinkUrl.Visibility = DonateInfo.HasLink ? Visibility.Visible : Visibility.Collapsed;

            // Подвал говорит только о том, что в окне реально есть. При одной
            // ссылке текст про буфер обмена сбивал бы с толку: копировать нечего.
            if (DonateInfo.HasCard || DonateInfo.HasSbp)
            {
                FooterNote.Text = "Реквизиты копируются в буфер обмена: вставлять их нужно " +
                                  "в приложении банка, а не в программе.";
            }
            else
            {
                FooterNote.Text = "Кнопка откроет страницу поддержки в браузере — там можно " +
                                  "поддержать разово или подпиской.";
            }
        }

        #region === КОПИРОВАНИЕ ===

        private void BtnCopyCard_Click(object sender, RoutedEventArgs e)
            => CopyToClipboard(DonateInfo.CardForCopy, btnCopyCard);

        private void BtnCopySbp_Click(object sender, RoutedEventArgs e)
            => CopyToClipboard(DonateInfo.SbpForCopy, btnCopySbp);

        /// <summary>
        /// Копирует значение и на пару секунд меняет подпись кнопки, чтобы
        /// нажатие было видно. Буфер обмена — общий ресурс системы: если его
        /// сейчас держит другая программа, Clipboard бросает исключение, и
        /// молча «не сработавшая» кнопка выглядела бы поломкой.
        /// </summary>
        private void CopyToClipboard(string value, Button button)
        {
            if (string.IsNullOrWhiteSpace(value) || button == null) return;

            try
            {
                Clipboard.SetText(value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Donate] буфер обмена: {ex.Message}");
                MessageBox.Show(this,
                    "Не удалось скопировать автоматически. Выделите номер в поле и нажмите Ctrl+C.",
                    "Буфер обмена занят", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string caption = button.Content as string ?? "Скопировать";
            button.Content = "Скопировано ✓";
            button.IsEnabled = false;

            var timer = new DispatcherTimer { Interval = CopyFeedbackTime };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (button.IsLoaded)
                {
                    button.Content = caption;
                    button.IsEnabled = true;
                }
            };
            timer.Start();
        }

        #endregion

        private void BtnOpenLink_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(DonateInfo.LinkUrl)) return;

            try
            {
                Process.Start(new ProcessStartInfo(DonateInfo.LinkUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Donate] ссылка: {ex.Message}");
                MessageBox.Show(this, "Не удалось открыть ссылку: " + DonateInfo.LinkUrl,
                    "Ссылка", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }
    }
}
