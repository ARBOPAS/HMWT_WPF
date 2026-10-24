using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace HMWT_MUI
{
    /// <summary>
    /// Точка входа приложения.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Замер запуска: от инициализации приложения до конца сборки главного окна.
        ///
        /// Нужен, чтобы «долго запускается» превращалось в число в журнале
        /// %APPDATA%\HMWT\db-trace.log, а не в догадки: непонятно ведь, тормозит
        /// база, чтение персонажей или разбор разметки.
        /// </summary>
        public static readonly System.Diagnostics.Stopwatch StartupWatch =
            System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Ключ настройки с темой оформления.</summary>
        public const string ThemeSettingKey = Services.UiPreferences.ThemeSettingKey;

        /// <summary>Ключ настройки «вкладки сверху» (вместо левой рейки).</summary>
        public const string TopTabsSettingKey = Services.UiPreferences.TopTabsSettingKey;

        /// <summary>
        /// Текущая тема: "dark" (по умолчанию) или "light".
        ///
        /// Само чтение живёт в UiPreferences: оно нужно и вкладке настроек,
        /// которой незачем зависеть от класса приложения.
        /// </summary>
        public static string CurrentTheme => Services.UiPreferences.CurrentTheme;

        /// <summary>
        /// Собирает ресурсы приложения с нужной палитрой.
        ///
        /// Тема меняется подменой ПАЛИТРЫ, а стили остаются одни и те же. Порядок
        /// здесь принципиален: палитра подключается первой, стили — второй, потому
        /// что стили подставляют цвета через StaticResource в момент загрузки
        /// словаря и «запоминают» конкретные кисти. Если палитра окажется позже,
        /// стили останутся от прежней темы.
        ///
        /// Поэтому ресурсы приложения пересобираются целиком и до создания окон:
        /// вызывается из OnStartup, пока главное окно ещё не разобрано.
        /// </summary>
        public static void ApplyTheme()
        {
            string palette = CurrentTheme == "light" ? "Palette.Light.xaml" : "Palette.Dark.xaml";

            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("Resources/" + palette, UriKind.Relative)
            });
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("Resources/DesignTokens.xaml", UriKind.Relative)
            });

            Application.Current.Resources = resources;

            System.Diagnostics.Debug.WriteLine($"[App] тема: {CurrentTheme} ({palette})");
        }

        /// <summary>
        /// Готовит отпечаток справочников для комнаты.
        ///
        /// Считается один раз при запуске, потому что нужен при каждом подключении:
        /// если у игроков разные наборы данных, идентификаторы предметов не совпадут,
        /// и в отряде начнётся путаница. Дешёвый счётчик записей тут достаточен —
        /// читать 20 МБ файлов при старте ради этого не нужно.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Пока главное окно собирается — инициализация базы, чтение персонажей,
            // разбор разметки, — на экране нет вообще ничего, и это выглядит как
            // «программа не запустилась». Курсор «занято» показывает, что работа
            // идёт. Снимает его главное окно в конце своего конструктора.
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                DatabaseHelper.Initialize();

                // Страховка при каждом запуске: три последних снимка базы.
                try { DatabaseHelper.CreateAutoBackup(3); }
                catch (Exception backupEx) { System.Diagnostics.Debug.WriteLine($"[App] автобэкап: {backupEx.Message}"); }

                // Тему выбираем до создания окон: окна и вложенные виды разбирают
                // разметку позже и должны получить уже нужную палитру
                ApplyTheme();

                int items = Services.SrdItemImporter.GetCount();
                int spells = Services.SpellJsonImporter.GetCount();
                Net.RoomSession.DataFingerprint = $"items:{items}|spells:{spells}";

                // Масштаб интерфейса читаем один раз при запуске: все окна,
                // которые откроются позже, получат уже готовое значение
                Services.UiScaleManager.Initialize();

                // Скрипты тёмных зон: ведущий просит бросить спас/проверку,
                // игрок бросает от своего имени. Подписка живёт всё приложение,
                // поэтому работает и со свёрнутым окном.
                Net.RoomSession.Instance.ZoneRollRequested += OnZoneRollRequested;
            }
            catch (Exception ex)
            {
                // Отпечаток не критичен: без него просто не будет предупреждения
                System.Diagnostics.Debug.WriteLine($"[App] отпечаток справочников: {ex.Message}");
            }

        }

        /// <summary>
        /// Скрипт тёмной зоны: жетон игрока вошёл в зону — бросаем спас или
        /// проверку по его листу и отправляем в общую ленту от его имени.
        /// </summary>
        private void OnZoneRollRequested(Net.ZoneRollRequest request)
        {
            if (request == null) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var session = Net.RoomSession.Instance;
                    var me = session.GetPlayers().FirstOrDefault(p => p.Id == session.MyPlayerId);
                    int characterId = me?.CharacterId ?? 0;
                    if (characterId <= 0) return;
                    if (request.CharacterId > 0 && request.CharacterId != characterId) return;

                    var result = Services.ZoneRollService.Roll(characterId, request.Kind,
                        request.Ability, request.Skill, request.DC, request.Name);
                    if (string.IsNullOrWhiteSpace(result.Formula)) return;

                    session.PublishRoll(result.Formula, result.Description, result.Total, result.Rolls);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Зона] бросок: {ex.Message}");
                }
            }));
        }
    }
}
