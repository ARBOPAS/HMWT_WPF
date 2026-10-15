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

            chkLightTheme.IsChecked = LightTheme;
            chkTopTabs.IsChecked = TopTabs;

            txtSrdHint.Text = $"Сейчас в базе: предметов {SrdItemImporter.GetCount()}, " +
                              $"заклинаний {SpellJsonImporter.GetCount()}, " +
                              $"разделов справочника {SrdImportService.GetCategories().Count}.";

            foreach (int seconds in RefreshSteps)
                cmbRefreshInterval.Items.Add(new ComboBoxItem { Content = seconds + " секунд" });

            cmbRefreshInterval.SelectedIndex = NearestRefreshIndex(RefreshSeconds);
        }

        /// <summary>
        /// Светлая тема. Чтение и запись живут в UiPreferences: вкладке настроек
        /// незачем зависеть от класса приложения.
        /// </summary>
        public static bool LightTheme
        {
            get { return UiPreferences.IsLightTheme; }
            set { UiPreferences.SetLightTheme(value); }
        }

        /// <summary>
        /// Вкладки сверху: полоса вкладок переезжает в верхнюю строку окна.
        /// </summary>
        public static bool TopTabs
        {
            get { return UiPreferences.TopTabs; }
            set { UiPreferences.TopTabs = value; }
        }

        /// <summary>
        /// Переключатели оформления.
        ///
        /// Применить их на лету нельзя: цвета подставлены в стили в момент загрузки
        /// словаря, поэтому уже собранные окна сохранят прежние кисти. Настройка
        /// сохраняется, а вариант вступает в силу при следующем запуске — рядом
        /// стоит кнопка перезапуска.
        /// </summary>
        private void Appearance_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            LightTheme = chkLightTheme.IsChecked == true;
            TopTabs = chkTopTabs.IsChecked == true;
            Note("Сохранено. Оформление применится после перезапуска.");
        }

        private void BtnRestartApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(exe)) System.Diagnostics.Process.Start(exe);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Note("Не удалось перезапустить: " + ex.Message);
            }
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

        /// <summary>
        /// Импорт справочников из базы 5e SRD.
        ///
        /// Выбирается любой файл из нужной папки: по нему определяется сама папка,
        /// а дальше разбираются все 5e-SRD-*.json, которые в ней лежат. Так не
        /// нужен отдельный диалог выбора папки, а промахнуться мимо языка сложно —
        /// файлы базы разложены по папкам en, ru, fr-FR.
        /// </summary>
        private void BtnImportSrd_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Укажите любой файл базы 5e SRD",
                Filter = "База 5e SRD (5e-SRD-*.json)|5e-SRD-*.json|Файлы JSON (*.json)|*.json"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string folder = System.IO.Path.GetDirectoryName(dialog.FileName);
                var result = SrdDatabaseImporter.Import(folder);

                txtSrdHint.Text = result.Describe();
                Note(result.Total > 0
                    ? $"Импортировано записей: {result.Total}"
                    : "Подходящих файлов в папке не нашлось");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] импорт SRD: {ex.Message}");
                txtSrdHint.Text = "Импорт не удался: " + ex.Message;
            }
        }

        /// <summary>
        /// Дополнение второй базой.
        ///
        /// Перевод SRD сделан не полностью: в русской папке нет заклинаний
        /// и магических предметов, а часть записей просто не переведена. Вторая
        /// база закрывает эти пробелы — совпадающие записи пропускаются по ключу
        /// index, которого язык не касается.
        /// </summary>
        private void BtnFillSrd_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Укажите файл базы, которой дополнить",
                Filter = "База 5e SRD (5e-SRD-*.json)|5e-SRD-*.json|Файлы JSON (*.json)|*.json"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string folder = System.IO.Path.GetDirectoryName(dialog.FileName);
                var result = SrdDatabaseImporter.Import(folder, fillGaps: true);

                txtSrdHint.Text = result.Describe();
                Note(result.Total > 0
                    ? $"Добавлено записей: {result.Total}"
                    : "Добавлять нечего — всё уже есть");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] дополнение SRD: {ex.Message}");
                txtSrdHint.Text = "Дополнение не удалось: " + ex.Message;
            }
        }

        private void Note(string text)
        {
            if (txtSaved != null) txtSaved.Text = text;
        }

        #endregion
    }
}
