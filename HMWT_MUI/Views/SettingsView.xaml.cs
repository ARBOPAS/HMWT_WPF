using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Helpers;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Вкладка настроек внутри окна персонажа.
    ///
    /// Раньше настройки были отдельным окном, но оно ссылалось на стили главного
    /// окна (GhostBtn, RoomPrimaryBtn) — их нет в области видимости другого окна,
    /// и оно падало при разборе разметки. Здесь стили объявлены на месте,
    /// а сама вкладка живёт рядом с остальными вкладками персонажа.
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private const string ShowWindowsNameKey = "Room_ShowWindowsName";
        private const string RefreshIntervalKey = "Room_RefreshSeconds";

        /// <summary>Чтобы начальная установка значений не срабатывала как правка.</summary>
        private bool _ready;

        /// <summary>Интервалы обновления данных отряда, секунды.</summary>
        private static readonly int[] RefreshSteps = { 5, 10, 15, 30, 60 };

        public SettingsView()
        {
            InitializeComponent();
            LoadCurrentValues();
            _ready = true;
        }

        private void LoadCurrentValues()
        {
            int percent = UiScaleManager.CurrentPercent;
            sldScale.Value = Math.Max(sldScale.Minimum, Math.Min(sldScale.Maximum, percent));
            txtScaleValue.Text = percent + "%";
            UpdateScaleHint(percent);

            switch (GifQualityService.Current)
            {
                case GifQuality.Low: rbGifLow.IsChecked = true; break;
                case GifQuality.High: rbGifHigh.IsChecked = true; break;
                default: rbGifMedium.IsChecked = true; break;
            }

            txtGifHint.Text = $"Сейчас: {GifQualityService.Describe(GifQualityService.Current)}. " +
                              $"Кадров на анимацию: до {GifQualityService.MaxFrames}.";

            chkShowWindowsName.IsChecked = ShowWindowsName;

            foreach (int seconds in RefreshSteps)
                cmbRefreshInterval.Items.Add(new ComboBoxItem { Content = seconds + " секунд" });

            cmbRefreshInterval.SelectedIndex = NearestRefreshIndex(RefreshSeconds);
        }

        /// <summary>
        /// Показывать в отряде имя учётной записи Windows вместо имени персонажа.
        ///
        /// По умолчанию выключено: в отряде видно имя персонажа из листа. Имя
        /// учётной записи показывается, если у игрока персонаж не открыт.
        /// Раньше значение по умолчанию было «включено», и в отряде вместо
        /// персонажей мелькали имена учётных записей.
        /// </summary>
        public static bool ShowWindowsName
        {
            get
            {
                try { return DatabaseHelper.GetSetting(ShowWindowsNameKey, "0") == "1"; }
                catch { return false; }
            }
            set
            {
                try { DatabaseHelper.SaveSetting(ShowWindowsNameKey, value ? "1" : "0"); }
                catch (Exception ex) { Debug.WriteLine($"[Settings] имя Windows: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Как часто сверяются данные других игроков (аватары, имена, здоровье).
        /// </summary>
        public static int RefreshSeconds
        {
            get
            {
                try
                {
                    string raw = DatabaseHelper.GetSetting(RefreshIntervalKey, "10");
                    if (int.TryParse(raw, out int value) && value >= 3 && value <= 120) return value;
                }
                catch { }
                return 10;
            }
            set
            {
                try { DatabaseHelper.SaveSetting(RefreshIntervalKey, value.ToString()); }
                catch (Exception ex) { Debug.WriteLine($"[Settings] интервал: {ex.Message}"); }
            }
        }

        private static int NearestRefreshIndex(int seconds)
        {
            int best = 1;
            int bestDiff = int.MaxValue;
            for (int i = 0; i < RefreshSteps.Length; i++)
            {
                int diff = Math.Abs(RefreshSteps[i] - seconds);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }

        #region === ДЕЙСТВИЯ ===

        private void SldScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;

            int percent = (int)Math.Round(e.NewValue);
            txtScaleValue.Text = percent + "%";
            UpdateScaleHint(percent);

            // Применяется ко всем окнам сразу
            UiScaleManager.Apply(percent, save: true);
            Note("Масштаб сохранён");
        }

        private void BtnScaleAuto_Click(object sender, RoutedEventArgs e)
        {
            int percent = UiScaleManager.GuessForScreen();
            sldScale.Value = percent;
            Note($"Подобрано по экрану: {percent}%");
        }

        private void UpdateScaleHint(int percent)
        {
            if (txtScaleHint == null) return;

            double screenW = SystemParameters.WorkArea.Width;
            double screenH = SystemParameters.WorkArea.Height;

            txtScaleHint.Text =
                $"Экран {screenW:0}×{screenH:0} при масштабе {percent}% даёт примерно " +
                $"{screenW / (percent / 100.0):0}×{screenH / (percent / 100.0):0} логических точек. " +
                "Вёрстка рассчитана на 1280×800.";
        }

        private void GifQuality_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            GifQuality quality = GifQuality.Medium;
            if (rbGifLow.IsChecked == true) quality = GifQuality.Low;
            else if (rbGifHigh.IsChecked == true) quality = GifQuality.High;

            GifQualityService.Set(quality);

            txtGifHint.Text = $"Установлено: {GifQualityService.Describe(quality)}. " +
                              $"Кадров на анимацию: до {GifQualityService.MaxFrames}. " +
                              "Кэш сброшен, картинки перечитаются при следующем наведении.";

            Note("Режим анимации сохранён");
        }

        private void ChkShowWindowsName_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            ShowWindowsName = chkShowWindowsName.IsChecked == true;
            Note("Сохранено");
        }

        private void CmbRefreshInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;

            int index = Math.Max(0, Math.Min(RefreshSteps.Length - 1, cmbRefreshInterval.SelectedIndex));
            RefreshSeconds = RefreshSteps[index];
            Note($"Обновление отряда: раз в {RefreshSeconds} секунд");
        }

        private void Note(string text)
        {
            if (txtSaved != null) txtSaved.Text = text;
        }

        #endregion
    }
}
