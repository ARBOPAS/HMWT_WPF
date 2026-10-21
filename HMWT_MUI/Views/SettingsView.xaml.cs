using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Services;
using Microsoft.Win32;

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
        /// <summary>Чтобы начальная установка значений не срабатывала как правка.</summary>
        private bool _ready;

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

            chkLightTheme.IsChecked = LightTheme;
            chkTopTabs.IsChecked = TopTabs;
            chkNotificationSound.IsChecked = UiPreferences.NotificationSoundEnabled;
            UpdateNotificationSoundHint();

            txtSrdHint.Text = $"Сейчас в базе: предметов {SrdItemImporter.GetCount()}, " +
                              $"заклинаний {SpellJsonImporter.GetCount()}, " +
                              $"разделов справочника {SrdImportService.GetCategories().Count}.";
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

        private void NotificationSound_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            UiPreferences.NotificationSoundEnabled = chkNotificationSound.IsChecked == true;
            NotificationSoundService.Reload();
            Note(UiPreferences.NotificationSoundEnabled
                ? "Звук уведомлений включён."
                : "Звук уведомлений выключен.");
        }

        private void BtnNotificationPick_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Звук уведомления",
                Filter = "Аудио (*.wav;*.mp3;*.wma;*.m4a;*.aac)|*.wav;*.mp3;*.wma;*.m4a;*.aac|" +
                         "WAV (*.wav)|*.wav|MP3 (*.mp3)|*.mp3|Все файлы (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                switch (ext)
                {
                    case ".wav":
                    case ".mp3":
                    case ".wma":
                    case ".m4a":
                    case ".aac":
                        break;
                    default:
                        Note("Поддерживаются WAV, MP3, WMA, M4A и AAC.");
                        return;
                }

                string target = Path.Combine(PhotoHelper.GetWritableRoot(), "notify-custom" + ext);
                if (!string.Equals(dialog.FileName, target, StringComparison.OrdinalIgnoreCase))
                    File.Copy(dialog.FileName, target, true);

                // Старые файлы другого формата больше не нужны
                foreach (string old in Directory.GetFiles(PhotoHelper.GetWritableRoot(), "notify-custom.*"))
                {
                    if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(old); } catch { }
                }

                // WAV проверяем сразу; MP3/WMA/M4A отдаём MediaPlayer
                if (ext == ".wav")
                {
                    using var check = new System.Media.SoundPlayer(target);
                    check.Load();
                }

                UiPreferences.NotificationSoundPath = "notify-custom" + ext;
                UiPreferences.NotificationSoundEnabled = true;
                chkNotificationSound.IsChecked = true;
                NotificationSoundService.Reload();
                UpdateNotificationSoundHint();
                NotificationSoundService.Preview();
                Note("Свой звук уведомления сохранён.");
            }
            catch (Exception ex)
            {
                Note("Не удалось прочитать звук: " + ex.Message);
            }
        }

        private void BtnNotificationPreview_Click(object sender, RoutedEventArgs e)
        {
            NotificationSoundService.Preview();
            Note("Проигрываю звук уведомления.");
        }

        private void BtnNotificationReset_Click(object sender, RoutedEventArgs e)
        {
            UiPreferences.NotificationSoundPath = "";
            NotificationSoundService.Reload();
            UpdateNotificationSoundHint();
            NotificationSoundService.Preview();
            Note("Возвращён встроенный звук.");
        }

        private void UpdateNotificationSoundHint()
        {
            if (txtNotificationSound == null) return;

            string path = UiPreferences.NotificationSoundPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                txtNotificationSound.Text = "Сейчас: встроенный звук «Гримуар».";
                return;
            }

            string full = PhotoHelper.ResolvePhotoPath(path) ?? path;
            txtNotificationSound.Text = File.Exists(full)
                ? "Сейчас: свой файл " + Path.GetFileName(full)
                : "Свой файл не найден — будет встроенный звук.";
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
