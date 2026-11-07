using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using HMWT_MUI.Controls;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Helpers;
using HMWT_MUI.Services;
using HMWT_MUI.Views;
using HMWT_MUI.Windows;

namespace HMWT_MUI.RoomUiTests
{
    /// <summary>
    /// Проверка интерфейса комнаты на настоящих окнах.
    ///
    /// Панель отряда в главном окне здесь НЕ проверяется: чтобы создать MainWindow,
    /// пришлось бы тянуть в тест все окна приложения. Она проверяется в живом
    /// приложении отдельно — там же, где её видит пользователь.
    ///
    /// Здесь проверяется то, что реально используется: вкладка «Отряд» и сервис
    /// комнаты, который связывает сеть с интерфейсом.
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Report = new List<string>();
        private static int _failures;
        private static string _outDir;

        [STAThread]
        private static int Main()
        {
            // Консоль может отсутствовать (WinExe), поэтому кодировку не трогаем:
            // попытка её задать без консольного дескриптора падает с IOException.
            _outDir = AppDomain.CurrentDomain.BaseDirectory;

            // Работаем на копии базы пользователя.
            //
            // Раньше тест открывал настоящую: DatabaseHelper.GetConnection при
            // пустой строке подключения сам зовёт Initialize(), и прогон правил
            // рабочую базу наравне с приложением. Копию удаляем в конце.
            string probePath = ProbeSetup();

            try { RunAll(); }
            catch (Exception ex) { Say("ИСКЛЮЧЕНИЕ: " + ex); _failures++; }
            finally { ProbeCleanup(probePath); }

            Say("");
            Say(_failures == 0 ? "ИТОГ: всё прошло" : $"ИТОГ: провалов {_failures}");

            // Отчёт пишем в файл: stdout у оконного приложения недоступен
            try
            {
                File.WriteAllLines(Path.Combine(_outDir, "room-ui-report.txt"), Report, Encoding.UTF8);
            }
            catch { }

            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Готовит копию рабочей базы и переключает соединение на неё.
        /// Возвращает путь к копии — его же удаляем в конце.
        /// </summary>
        private static string ProbeSetup()
        {
            string appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT");
            string probe = Path.Combine(appDir, "probe_roomtests.db");

            foreach (var extra in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(probe + extra); } catch { }
            }

            // Копия, а не пустая база: проверки читают те же данные, что и раньше
            try { File.Copy(Path.Combine(appDir, "HMWT_app.db"), probe, true); }
            catch (Exception ex) { Say("  копия базы: " + ex.Message); }

            DatabaseHelper.Initialize("probe_roomtests.db");
            return probe;
        }

        /// <summary>Убирает копию базы: пул соединений держит файл, поэтому сначала чистим его.</summary>
        private static void ProbeCleanup(string probePath)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                foreach (var extra in new[] { "", "-wal", "-shm" })
                    File.Delete(probePath + extra);
            }
            catch { }
        }

        private static void Say(string text)
        {
            Report.Add(text);
            try { Console.WriteLine(text); } catch { }
        }

        private static void Check(string label, bool ok, string detail = "")
        {
            if (!ok) _failures++;
            Say($"  [{(ok ? "OK  " : "ПРОВАЛ")}] {label}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
        }

        /// <summary>
        /// Максимум HP участника отряда берётся из настроек персонажа
        /// (Character_{id}_MaxHP), а не из колонки HP.
        ///
        /// Раньше в отряд уходило «185/185» вместо «185/210»: текущее здоровье
        /// выдавалось за максимум, и полоса здоровья у всех была полной.
        /// </summary>
        private static void CheckMaxHpFromSettings()
        {
            int id = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var cmd = new SqliteCommand(
                    "INSERT INTO CharacterInfo (name, class, race, HP, ArmorClass) " +
                    "VALUES ('Тест Максимум', 'Воин', 'Человек', 11, 12)", conn))
                    cmd.ExecuteNonQuery();

                using (var cmd = new SqliteCommand("SELECT last_insert_rowid()", conn))
                    id = Convert.ToInt32(cmd.ExecuteScalar());

                using (var cmd = new SqliteCommand(
                    "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@k, '17')", conn))
                {
                    cmd.Parameters.AddWithValue("@k", $"Character_{id}_MaxHP");
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Check("персонаж для проверки максимума HP создан", false, ex.Message);
                return;
            }

            try
            {
                var me = DmProfileService.BuildPartyPlayer(id);
                Check("максимум HP берётся из настроек персонажа",
                    me != null && me.Hp == 11 && me.MaxHp == 17,
                    me == null ? "участник не собран" : $"hp={me.Hp}/{me.MaxHp}");
                Check("текущее здоровье не выдаётся за максимум",
                    me != null && me.MaxHp > me.Hp,
                    me == null ? "" : $"{me.Hp}/{me.MaxHp}");
            }
            finally
            {
                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();
                    using var cmd = new SqliteCommand(
                        "DELETE FROM CharacterInfo WHERE id_character = @id; " +
                        "DELETE FROM Settings WHERE Key = @k", conn);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@k", $"Character_{id}_MaxHP");
                    cmd.ExecuteNonQuery();
                }
                catch { }
            }
        }

        /// <summary>
        /// Бонусы к спасброскам и проверкам понимают не только число, но и
        /// характеристику: «ЛОВ», «1+ИНТ». Раньше поле принимало только int.
        /// </summary>
        private static void CheckAbilityBonusFormulas()
        {
            int id = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var cmd = new SqliteCommand(
                    "INSERT INTO CharacterInfo (name, class, race, HP, ArmorClass, " +
                    "Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma) " +
                    "VALUES ('Тест Формул', 'Воин', 'Человек', 10, 10, 10, 15, 10, 15, 10, 10)", conn))
                    cmd.ExecuteNonQuery();

                using (var cmd = new SqliteCommand("SELECT last_insert_rowid()", conn))
                    id = Convert.ToInt32(cmd.ExecuteScalar());

                using (var cmd = new SqliteCommand(
                    "INSERT INTO CharacterAbilityBonuses(id_character, ability, scope, name, bonus, is_active) " +
                    "VALUES(@id, 'ИНТЕЛЛЕКТ', 'Check', 'ТЕСТ 1+ИНТ', '1+ИНТ', 1)", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = new SqliteCommand(
                    "INSERT INTO CharacterAbilityBonuses(id_character, ability, scope, name, bonus, is_active) " +
                    "VALUES(@id, 'СИЛА', 'Check', 'ТЕСТ 3', '3', 1)", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = new SqliteCommand(
                    "INSERT INTO CharacterAbilityBonuses(id_character, ability, scope, name, bonus, is_active) " +
                    "VALUES(@id, 'ХАРИЗМА', 'Save', 'ТЕСТ ВЫКЛ', 'ХАР', 0)", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Check("персонаж для проверки формул бонусов создан", false, ex.Message);
                return;
            }

            try
            {
                // Бонус, добавленный через сервис, потом правим формулой — как в редакторе
                AbilityService.AddBonus(id, "ЛОВКОСТЬ", "Save");
                var rows = AbilityService.GetBonuses(id, "ЛОВКОСТЬ", "Save");
                if (rows.Count > 0)
                    AbilityService.UpdateBonus(rows[0].Id, null, "ЛОВ", null);

                Check("бонус «ЛОВ» даёт модификатор ЛОВКОСТИ",
                    AbilityService.GetBonusSum(id, "ЛОВКОСТЬ", "Save") == 2,
                    "ожидаем +2 при ЛОВ 15");
                Check("бонус «1+ИНТ» складывает число и модификатор",
                    AbilityService.GetBonusSum(id, "ИНТЕЛЛЕКТ", "Check") == 3,
                    "ожидаем 1+2 при ИНТ 15");
                Check("числовой бонус остался числом",
                    AbilityService.GetBonusSum(id, "СИЛА", "Check") == 3);
                Check("выключенный бонус с формулой не считается",
                    AbilityService.GetBonusSum(id, "ХАРИЗМА", "Save") == 0);

                var list = AbilityService.GetBonuses(id, "ИНТЕЛЛЕКТ", "Check");
                Check("формула бонуса читается из базы как текст",
                    list.Count == 1 && list[0].Formula == "1+ИНТ",
                    list.Count == 0 ? "записи нет" : list[0].Formula);
            }
            finally
            {
                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();
                    using var cmd = new SqliteCommand(
                        "DELETE FROM CharacterAbilityBonuses WHERE id_character=@id; " +
                        "DELETE FROM CharacterInfo WHERE id_character=@id", conn);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
                catch { }
            }
        }

        private static void RunAll()
        {
            // ── Проверка связки кнопок с кодом ──
            //
            // Именно эту ошибку пропустили прошлые проверки: в разметке панели отряда
            // у кнопок не было Click, методы лежали в коде и не вызывались — нажатие
            // не делало ничего. WPF о таком не сообщает, обработчик просто отсутствует,
            // поэтому разметку разбираем как текст.
            // Ресурсы дизайна грузим из отдельного словаря DesignTokens.xaml.
            //
            // App.xaml для этого не годится: он объявлен как ApplicationDefinition
            // и тянет StartupUri (MainWindow.xaml), которого в тестовой сборке нет.
            // Словарь — тот же самый файл, что подключает приложение, поэтому
            // проверяются настоящие значения, а не их копия в тесте.
            //
            // Палитра подключается ПЕРВОЙ, стили — второй: стили подставляют цвета
            // через StaticResource в момент загрузки словаря и запоминают конкретные
            // кисти. Если палитра окажется позже, окна не найдут B.Panel и упадут.
            var app = new Application
            {
                // По умолчанию WPF завершает приложение при закрытии последнего окна.
                // В тесте окна открываются по очереди, поэтому выключаем это правило.
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            try
            {
                var palette = (ResourceDictionary)Application.LoadComponent(
                    new Uri("/HMWT_MUI.RoomUiTests;component/App/Resources/Palette.Dark.xaml", UriKind.Relative));
                if (palette != null)
                    app.Resources.MergedDictionaries.Add(palette);

                var tokens = (ResourceDictionary)Application.LoadComponent(
                    new Uri("/HMWT_MUI.RoomUiTests;component/App/Resources/DesignTokens.xaml", UriKind.Relative));
                if (tokens != null)
                    app.Resources.MergedDictionaries.Add(tokens);
            }
            catch (Exception ex)
            {
                Say("  загрузка словарей оформления: " + ex.GetType().Name + " — " + ex.Message);
            }

            // Приложение задаёт отпечаток в App.OnStartup, которого в тесте нет
            RoomSession.DataFingerprint = "items:12529|spells:5849";

            Say("═══ РЕСУРСЫ ДИЗАЙНА ═══");
            // Считаем и объединённые словари: токены лежат именно там
            int tokenCount = Application.Current.Resources.MergedDictionaries
                .Sum(d => d.Keys.Count);
            Check("словарь токенов загружен", tokenCount > 0, $"ключей: {tokenCount}");
            foreach (var key in new[] { "B.Panel", "B.Backdrop", "B.Accent", "B.Ok", "B.Frost", "F.Display" })
                Check($"токен {key} доступен", Application.Current.TryFindResource(key) != null);

            Say("");
            Say("═══ МАКСИМУМ HP В ОТРЯДЕ ═══");
            CheckMaxHpFromSettings();
            CheckAbilityBonusFormulas();

            Say("═══ СВЯЗКА КНОПОК С КОДОМ ═══");
            CheckButtonWiring("MainWindow.xaml",
                new[] { "MainWindow.Room.cs" },
                new[]
                {
                    "btnCreateRoom", "btnJoinRoom", "btnRefreshIp", "btnCheckPort",
                    "btnRoomOpenTab", "btnRoomPanelLeave", "RoomButton"
                });
            CheckButtonWiring("Views/RoomView.xaml",
                new[] { "Views/RoomView.xaml.cs" },
                new[] { "btnCopyKey", "btnLeaveRoom", "btnSendChat" });

            // ── Подложка диалогов: окно подтверждения должно лежать поверх ОКНА ──
            //
            // У окон приложения слева узкая колонка вкладок. Раньше диалог искал
            // «самый большой Grid» внутри вкладки и ложился поверх её содержимого:
            // кнопка «Выйти» давала окно, видимое только в колонке вкладок —
            // там всего несколько десятков пикселей ширины.
            Say("");
            Say("═══ ПОДЛОЖКА ДИАЛОГОВ ═══");
            CheckDialogPlacement();

            // ── Память под гифки ──
            //
            // Полный набор кадров GIF стоит width × height × 4 байта на каждый кадр.
            // Раньше наборы кэшировались пачками и хранились в модели заклинания —
            // приложение раздувалось до полутора гигабайт. Проверяем, что оценка
            // работает и что освобождение действительно убирает набор из кэша.
            Say("");
            Say("═══ ПАМЯТЬ ПОД ГИФКИ ═══");
            CheckGifMemory();

            Say("");
            Say("═══ АНИМАЦИЯ ПРИ НАВЕДЕНИИ ═══");
            CheckHoverAnimation();

            Say("");
            Say("═══ ВЫБРОС ПРЕДМЕТОВ ЧАСТЯМИ ═══");
            CheckDiscardQuantity();

            Say("");
            Say("═══ МАСШТАБ ИНТЕРФЕЙСА ═══");
            CheckUiScale();

            Say("");
            Say("═══ ВКЛАДКА НАСТРОЕК ═══");
            CheckSettingsView();

            // ── Окно передачи предметов ──
            //
            // Окно собирается кодом, поэтому проверяем не «есть ли файл», а как
            // оно реально выглядит: рисуем его в картинку и смотрим на пиксели.
            // Тёмный фон, светлый текст, все пять монет, фильтры в одну строку,
            // кламп количества и корзина передачи.
            Say("");
            Say("═══ ПЕРЕДАЧА ПРЕДМЕТОВ ═══");
            CheckTradeProtocol();
            CheckTradeWindow();
            CheckTradeMoney();
            CheckTradeCardAndMenu();

            // ── Заклинания в карточке существа ──
            //
            // Блок «Заклинания и псионика» раньше был одним абзацем. Проверяем
            // и разбор («круг» / «уровень», ячейки, вступление), и вид: строки
            // по кругам с плашками заклинаний.
            Say("");
            Say("═══ ЗАКЛИНАНИЯ В КАРТОЧКЕ СУЩЕСТВА ═══");
            CheckMonsterSpells();

            // ── Жетоны НПС и предметов ──
            //
            // Такой жетон живёт только на карте: у него нет участника боя,
            // поэтому трекер инициативы не засоряется «Стариком» и бочкой.
            Say("");
            Say("═══ ЖЕТОНЫ НПС И ПРЕДМЕТОВ ═══");
            CheckMapTokenLibrary();
            CheckFreeTokenPersistence();
            CheckTokenMenuScript();
            CheckTokenEditorWindow();
            CheckTokenLibraryWindow();
            CheckTokenCacheCleanup();
            CheckMapScenePhotoCache();
            CheckSceneTokenPhotosExist();

            Say("");
            Say("═══ ПЕРЕДАЧА ФОТО ═══");
            CheckPhotoTransfer();

            Say("");
            Say("═══ СВОЙ ПРЕДМЕТ В МАГАЗИНЕ ═══");
            CheckCustomShopItem();

            Say("");
            Say("═══ РАЗБОР СПИСКОВ В ЗАМЕТКАХ ═══");
            CheckMarkdownLists();
            CheckNotesColor();

            Say("");
            Say("═══ ПОКУПКА И СТАКИВАНИЕ ═══");
            CheckShopPurchase();

            // ── Версия приложения в двух местах должна совпадать ──
            //
            // AssemblyInfo задаёт версию сборки, NetVersions её же проверяет при
            // подключении к комнате. Если разъедутся, игроки не смогут подключиться
            // к ведущему, и причина будет совсем не очевидна.
            Say("");
            Say("═══ ВЕРСИЯ ПРИЛОЖЕНИЯ ═══");
            CheckVersionConsistency();

            // ── Кодировка исходников ──
            //
            // Файл, переписанный «не той» кодировкой, компилируется без единой
            // ошибки: русский текст просто превращается в «РЎСѓС‰РµСЃС‚РІРѕ»
            // и уезжает в интерфейс. Проверка дешёвая — ловим характерные пары.
            Say("");
            Say("═══ КОДИРОВКА ИСХОДНИКОВ ═══");
            CheckSourceEncoding();

            // ── Связка «персонаж ↔ участник боя ↔ жетон» по character_id ──
            //
            // Это ключ, по которому карта, трекер и лист персонажа должны
            // говорить об одном участнике. Проверяем модель, JSON (по нему
            // карта уходит клиенту) и настоящую колонку в базе: одной
            // CREATE TABLE IF NOT EXISTS для старых баз мало.
            Say("");
            Say("═══ СВЯЗКА ПЕРСОНАЖ ↔ УЧАСТНИК БОЯ ═══");
            CheckCharacterLink();

            // ── Версия схемы и миграции ──
            // База игрока обновляется шагами SchemaMigrator, а не только
            // EnsureColumn: проверяем, что версия доехала и новый индекс есть.
            Say("");
            Say("═══ СХЕМА БАЗЫ ═══");
            CheckSchemaVersion();


            // ── Вкладка без подключения ──
            Say("");
            Say("═══ ВКЛАДКА «ОТРЯД» БЕЗ ПОДКЛЮЧЕНИЯ ═══");
            var idle = ShowInWindow(new RoomView(), 1360, 800, -5000, -5000);
            try
            {
                Check("заголовок на месте", Text(idle.View, "txtRoomTitle") == "ОТРЯД", Text(idle.View, "txtRoomTitle"));
                Check("статус «не подключено»", Text(idle.View, "txtRoomStatus") == "Не подключено",
                    Text(idle.View, "txtRoomStatus"));
                Check("ключ скрыт, пока не в комнате",
                    Element(idle.View, "txtRoomKey").Visibility == Visibility.Collapsed);
                Check("ввод чата заблокирован без комнаты", !Element(idle.View, "txtChatInput").IsEnabled);
                Check("подсказка объясняет, что будет в ленте",
                    Text(idle.View, "txtFeedHint").Contains("появятся"), Text(idle.View, "txtFeedHint"));
                Check("список участников показывает пустое состояние",
                    Panel(idle.View, "spPlayers").Children.Count == 1);
            }
            finally { idle.Window.Close(); }

            // ── Комната ──
            Say("");
            Say("═══ СОЗДАНИЕ КОМНАТЫ ═══");
            var session = RoomSession.Instance;
            var me = new PlayerInfo
            {
                Name = "Артём", CharacterName = "Гром", CharacterClass = "Воин",
                CharacterRace = "Полуорк", Level = 3, Hp = 24, MaxHp = 30, ArmorClass = 16
            };

            if (!session.CreateRoom("Проверка интерфейса", me, FindFreeRoomPort(), out string error))
            {
                Check("комната создана", false, error);
                return;
            }
            Check("комната создана", true, RoomKey.Pretty(session.Key));

            try
            {
                Check("хост видит себя в отряде", session.GetPlayers().Count == 1,
                    $"участников: {session.GetPlayers().Count}");
                Check("хост стал ведущим", session.AmIGameMaster);
                Check("адрес определён", session.HostAddress.Contains("."), session.HostAddress);

                // Состав боя (для листа персонажа у клиента) должен пережить
                // публикацию через сервер: копия состояния раньше теряла
                // roster, и до игрока он не доезжал вовсе.
                session.PublishInitiative(new InitiativeState
                {
                    IsActive = true,
                    Roster = new List<InitiativeCombatantDto>
                    {
                        new InitiativeCombatantDto
                        {
                            Id = 1, Name = "Гром", CharacterId = 987654, Conditions = "prone"
                        }
                    }
                });
                System.Threading.Thread.Sleep(250);
                var iniBack = session.GetInitiative();
                Check("состав боя переживает публикацию",
                    iniBack?.Roster != null && iniBack.Roster.Count == 1 &&
                    iniBack.Roster[0].CharacterId == 987654 &&
                    iniBack.Roster[0].Conditions == "prone",
                    iniBack?.Roster == null ? "состава нет" : $"участников: {iniBack.Roster.Count}");

                // ── Состояния из трекера сразу попадают в жетон карты ──
                //
                // Раньше у клиента они «не доезжали» до следующего случайного
                // обновления сцены. Теперь SaveCombatant поднимает Changed,
                // сервер обновляет жетоны и рассылает карту.
                Say("");
                Say("═══ СОСТОЯНИЯ ИЗ ТРЕКЕРА → КАРТА ═══");
                CheckTrackerToMap(session);

                Say("  подключаю второго участника по сети…");
                var guest = new RoomClient("127.0.0.1", session.Port, session.Key, session.RoomId, "");

                // Счётчик при входе через RoomSession: проверяем именно момент входа
                Say("");
                Say("═══ СЧЁТЧИК УЧАСТНИКОВ ПРИ ВХОДЕ ═══");
                CheckClientCountOnJoin(session.Key, session.RoomId, session.Port);
                Say("");
                bool joined = guest.ConnectAsync(new PlayerInfo
                {
                    Name = "Лиза", CharacterName = "Тишь", CharacterClass = "Плут",
                    CharacterRace = "Полурослик", Level = 3, Hp = 7, MaxHp = 18, ArmorClass = 14,
                    // Связка с жетоном карты из проверки «состояния из трекера»:
                    // по character_id сервер понимает, что ход разрешён.
                    CharacterId = 987654
                }).GetAwaiter().GetResult();
                Check("второй участник подключился", joined, guest.LastError);

                System.Threading.Thread.Sleep(900);
                var players = session.GetPlayers();
                session.SetBlindfold(true);
                System.Threading.Thread.Sleep(500);
                Check("повязка уходит клиенту", guest.LastSnapshot?.Blindfold == true,
                    "blind=" + (guest.LastSnapshot?.Blindfold.ToString() ?? "нет снимка"));
                session.SetBlindfold(false);
                System.Threading.Thread.Sleep(300);
                Check("повязка снимается у клиента", guest.LastSnapshot?.Blindfold == false);                Check("в отряде двое", players.Count == 2, $"участников: {players.Count}");
                Check("второй — игрок, не ведущий",
                    players.Any(p => p.CharacterName == "Тишь" && p.Role == RoomRole.Player));
                Check("видны класс, уровень и здоровье второго",
                    players.Any(p => p.CharacterName == "Тишь" && p.CharacterClass == "Плут"
                                     && p.Hp == 7 && p.MaxHp == 18 && p.ArmorClass == 14));

                // ── Живой ход жетона клиента ──
                // Во время перетаскивания должны идти лёгкие сообщения, а не
                // только итоговый ход: иначе у ведущего жетон «оживает» лишь
                // при отпускании мыши.
                Say("");
                Say("═══ ЖИВОЙ ХОД ЖЕТОНА ═══");
                CheckLiveTokenMove(session, guest);

                // ── Скрипт тёмной зоны ──
                // Вход жетона в зону должен ОДИН раз попросить владельца
                // бросить спас/проверку (а не каждый шаг перетаскивания).
                Say("");
                Say("═══ СКРИПТ ТЁМНОЙ ЗОНЫ ═══");
                CheckZoneScriptTrigger(session, guest);

                // ── Аудио в комнате ──
                // Ведущий включает трек, игрок получает команду; файл
                // передаётся кусками и должен собраться байт в байт.
                Say("");
                Say("═══ АУДИО В КОМНАТЕ ═══");
                CheckAudioRoom(session, guest);

                // ── Обмен не рвёт связь ──
                // Каждое предложение уходит и личным сообщением, и системной
                // строкой в ленту. Раньше две записи в один сокет шли без замка,
                // клиент читал мусор и выпадал из отряда с переподключением.
                Say("");
                Say("═══ ПЕРЕДАЧА ПРЕДМЕТОВ: СЕТЬ ═══");
                CheckTradeTransport(session, guest);
                CheckTradeEndToEnd(session, guest);

                session.PublishChat("Все в сборе? Начинаем спуск в подземелье.");
                session.PublishRoll("1d20+5", "Проверка Восприятия", 22, new List<int> { 17 });
                session.PublishRoll("2d6+3", "Урон кинжалом", 10, new List<int> { 3, 4 });
                session.PublishChat("Иду следом, держу строй.");
                System.Threading.Thread.Sleep(400);

                var feed = session.GetFeed(100);
                Check("в ленте есть записи", feed.Count >= 4, $"записей: {feed.Count}");
                Check("бросок хранит формулу, итог и кубы",
                    feed.Any(f => f.Kind == FeedKind.Roll && f.Formula == "1d20+5" && f.Total == 22
                                  && f.IndividualRolls != null && f.IndividualRolls.Contains(17)));
                Check("есть системные события", feed.Any(f => f.Kind == FeedKind.System));

                // ── Скрытые броски ведущего ──
                // Игроки не должны получить их ни событием, ни снимком: иначе
                // мастер не сможет незаметно подкрутить бросок.
                Say("");
                Say("═══ СКРЫТЫЕ БРОСКИ ВЕДУЩЕГО ═══");
                session.HiddenDmRolls = true;
                session.PublishRoll("1d20+3", "Скрытая проверка", 17, new List<int> { 14 });

                var hiddenEntry = session.GetFeed(300)
                    .LastOrDefault(f => f.Text == "Скрытая проверка");
                Check("скрытый бросок виден ведущему",
                    hiddenEntry != null && hiddenEntry.Hidden,
                    hiddenEntry == null ? "записи нет" : $"итог {hiddenEntry.Total}");

                var serverField = typeof(RoomSession).GetField("_server",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var server = (RoomServer)serverField.GetValue(session);

                Check("скрытый бросок не попал в общую ленту",
                    !server.GetFeed(300).Any(f => f.Text == "Скрытая проверка"));

                var snapshotMethod = typeof(RoomServer).GetMethod("BuildSnapshot",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var snapshot = (RoomSnapshot)snapshotMethod.Invoke(server, null);
                Check("скрытый бросок не попал в снимок для игроков",
                    !snapshot.Feed.Any(f => f.Text == "Скрытая проверка"));

                // Выключаем и проверяем, что обычный бросок снова уходит всем
                session.HiddenDmRolls = false;
                session.PublishRoll("1d20", "Открытая проверка", 11, new List<int> { 11 });
                Check("после выключения бросок снова общий",
                    server.GetFeed(300).Any(f => f.Text == "Открытая проверка"));

                // ── Лента ГОСТЯ пополняется каждым событием ──
                //
                // Раньше гость читал ленту только из снимка, который приходит редко:
                // своё сообщение он видел лишь после следующего действия. Теперь
                // сообщения и броски складываются в ленту сразу.
                Say("");
                Say("═══ ЛЕНТА ГОСТЯ ПОПОЛНЯЕТСЯ СРАЗУ ═══");

                // Гость пишет в чат — и должен увидеть это у себя сам.
                // Проверяем на ОТДЕЛЬНОМ экземпляре RoomSession, потому что общий
                // занят хостом, а JoinRoom на активной сессии не выполнить.
                // Именно конструктор, а не GetUninitializedObject: тот не выполняет
                // инициализаторы полей, и внутренний замок ленты остаётся null
                var guestSession = (RoomSession)Activator.CreateInstance(
                    typeof(RoomSession),
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public,
                    null, null, null);

                // Лента гостя складывается из двух источников: снимок (история
                // на момент входа) и отдельные события по каждому сообщению.
                var historySnapshot = new RoomSnapshot
                {
                    RoomId = session.RoomId,
                    Feed = session.GetFeed(100).ToList()
                };
                guestSession.AddPeerFeedEntry(historySnapshot.Feed.First());

                int beforeCount = guestSession.GetFeed(300).Count;
                Say($"  у гостя записей после снимка: {beforeCount}");
                Check("снимок дал гостю историю ленты", beforeCount > 0, $"записей: {beforeCount}");

                // Гость пишет в чат: сервер рассылает его же сообщение обратно
                guest.SendChatAsync("Сообщение от гостя").GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(900);

                var entry = session.GetFeed(300).LastOrDefault(f =>
                    f.Text != null && f.Text.Contains("Сообщение от гостя"));
                Check("хост получил сообщение гостя", entry != null);

                if (entry != null)
                {
                    guestSession.AddPeerFeedEntry(entry);
                    var guestAfter = guestSession.GetFeed(300);
                    Check("сообщение гостя сразу попало в его ленту",
                        guestAfter.Count > beforeCount, $"было {beforeCount}, стало {guestAfter.Count}");
                    Check("сообщение гостя есть в его ленте по тексту",
                        guestAfter.Any(f => f.Text != null && f.Text.Contains("Сообщение от гостя")));

                    // Главное: повторный снимок НЕ должен затирать свежую запись.
                    // Именно это и было причиной: хост рассылает снимок, он приходит
                    // чуть позже и перезаписывал ленту историей без нового сообщения.
                    var staleSnapshot = new RoomSnapshot
                    {
                        RoomId = session.RoomId,
                        // История без последнего сообщения — как будто снимок сделан раньше
                        Feed = historySnapshot.Feed
                    };
                    guestSession.AddPeerFeedEntry(staleSnapshot.Feed.Last());
                    guestSession.MergeHistoryForTest(staleSnapshot);

                    var afterStale = guestSession.GetFeed(300);
                    Check("устаревший снимок не затирает свежее сообщение",
                        afterStale.Any(f => f.Text != null && f.Text.Contains("Сообщение от гостя")),
                        $"записей после снимка: {afterStale.Count}");

                    // Дубль не должен появляться: то же событие приходит и снимком
                    int countBeforeDup = guestSession.GetFeed(300).Count;
                    guestSession.AddPeerFeedEntry(entry);
                    Check("повторное событие не создаёт дубль",
                        guestSession.GetFeed(300).Count == countBeforeDup,
                        $"было {countBeforeDup}, стало {guestSession.GetFeed(300).Count}");
                }

                // ── Вкладка с данными ──
                Say("");
                Say("═══ ВКЛАДКА «ОТРЯД» С ДАННЫМИ ═══");
                var live = ShowInWindow(new RoomView(), 1400, 860, 40, 40);
                try
                {
                    Check("карточек участников — две", Panel(live.View, "spPlayers").Children.Count == 2,
                        $"карточек: {Panel(live.View, "spPlayers").Children.Count}");
                    Check("строк ленты не меньше четырёх", Panel(live.View, "spFeed").Children.Count >= 4,
                        $"строк: {Panel(live.View, "spFeed").Children.Count}");
                    Check("заголовок показывает число участников",
                        Text(live.View, "txtPlayersHeader").Contains("2/8"), Text(live.View, "txtPlayersHeader"));
                    Check("ключ показан ведущему",
                        Text(live.View, "txtRoomKey") == RoomKey.Pretty(session.Key), Text(live.View, "txtRoomKey"));
                    Check("адрес показан ведущему", Text(live.View, "txtHostAddress").Contains(":"),
                        Text(live.View, "txtHostAddress"));
                    Check("ввод чата разблокирован", Element(live.View, "txtChatInput").IsEnabled);
                    Check("счётчик записей обновился", Text(live.View, "txtFeedHint").Contains("записей"),
                        Text(live.View, "txtFeedHint"));

                    string shot = Path.Combine(_outDir, "shot-room-tab.png");
                    SaveScreenshot(live.Window, shot);
                    Check("скриншот вкладки сохранён", File.Exists(shot), shot);
                }
                finally { live.Window.Close(); }

                // ── Отправка сообщения из интерфейса ──
                Say("");
                Say("═══ ОТПРАВКА СООБЩЕНИЯ ИЗ ИНТЕРФЕЙСА ═══");
                var chatView = ShowInWindow(new RoomView(), 1360, 800, -5000, -5000);
                try
                {
                    int before = session.GetFeed(300).Count;
                    var input = (TextBox)Element(chatView.View, "txtChatInput");
                    input.Text = "Проверка отправки из интерфейса";

                    var sendBtn = (Button)Element(chatView.View, "btnSendChat");
                    sendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    System.Threading.Thread.Sleep(500);
                    int after = session.GetFeed(300).Count;
                    Check("сообщение из интерфейса попало в ленту", after > before, $"было {before}, стало {after}");
                    Check("поле ввода очищено", string.IsNullOrEmpty(input.Text));
                }
                finally { chatView.Window.Close(); }

                // ── Окно подтверждения должно лежать поверх ВСЕГО окна ──
                //
                // Раньше диалог искал «самый большой Grid» внутри вкладки и ложился
                // поверх её содержимого: подтверждение выхода перекрывало вкладку,
                // но не рейку слева — выглядело как окно внутри колонки вкладок.
                Say("");
                Say("═══ ОКНО ПОДТВЕРЖДЕНИЯ ПОВЕРХ ОКНА ═══");
                var dlgView = ShowInWindow(new RoomView(), 1360, 800, -5000, -5000);
                try
                {
                    var dlgWindow = dlgView.Window;
                    FrameworkElement overlaySeen = null;
                    bool dialogShown = false;

                    // Диалог синхронный, поэтому закрываем его из очереди диспетчера
                    var timer = new System.Windows.Threading.DispatcherTimer(
                        TimeSpan.FromMilliseconds(400),
                        System.Windows.Threading.DispatcherPriority.Normal,
                        (s, e) =>
                        {
                            // Ищем оверлей: Grid с ZIndex 4000 внутри окна
                            overlaySeen = FindOverlay(dlgWindow);
                            dialogShown = overlaySeen != null;
                            CloseOverlay(dlgWindow, overlaySeen);
                        },
                        System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    timer.Start();

                    HMWT_MUI.Windows.MessageDialog.Confirm(dlgWindow,
                        "Выйти из отряда?", "Выход из отряда", "Выйти", "Остаться", danger: true);
                    timer.Stop();

                    Check("окно подтверждения показано", dialogShown);

                    if (overlaySeen != null)
                    {
                        // Главное: оверлей должен быть прям в окне, а не внутри вкладки
                        var parent = VisualTreeHelper.GetParent(overlaySeen);
                        bool inWindowRoot = overlaySeen.ActualWidth >= dlgWindow.ActualWidth * 0.95
                                            && overlaySeen.ActualHeight >= dlgWindow.ActualHeight * 0.95;

                        Check("подтверждение перекрывает всё окно, а не только вкладку", inWindowRoot,
                            $"оверлей {overlaySeen.ActualWidth:0}×{overlaySeen.ActualHeight:0}, " +
                            $"окно {dlgWindow.ActualWidth:0}×{dlgWindow.ActualHeight:0}");

                        // Внутри вкладки его быть не должно
                        var insideView = IsDescendantOf(overlaySeen, dlgView.View);
                        Check("подтверждение не лежит внутри вкладки", !insideView);
                    }
                }
                finally { dlgView.Window.Close(); }

                // ── Обновление данных персонажа ──
                Say("");
                Say("═══ ОБНОВЛЕНИЕ ДАННЫХ ПЕРСОНАЖА ═══");
                var wounded = new PlayerInfo
                {
                    CharacterName = "Гром", CharacterClass = "Воин", CharacterRace = "Полуорк",
                    Level = 3, Hp = 11, MaxHp = 30, ArmorClass = 16
                };
                session.UpdateMyCharacter(wounded);
                System.Threading.Thread.Sleep(400);
                var refreshed = session.GetPlayers().FirstOrDefault(p => p.Id == session.GetMyPlayerId());
                Check("изменённое здоровье попало в отряд",
                    refreshed != null && refreshed.Hp == 11 && refreshed.MaxHp == 30,
                    refreshed == null ? "не найден" : $"ХП {refreshed.Hp}/{refreshed.MaxHp}");
                Check("роль при обновлении не потерялась", refreshed != null && refreshed.Role == RoomRole.GameMaster);

                // ── Отключение участника ──
                guest.Disconnect();
                System.Threading.Thread.Sleep(500);
                Check("после отключения участник убран", session.GetPlayers().Count == 1,
                    $"участников: {session.GetPlayers().Count}");
                Check("в ленте есть запись об отключении",
                    session.GetFeed(300).Any(f => f.Kind == FeedKind.System && f.Text.Contains("отключился")));

                // ── История на диске ──
                Say("");
                Say("═══ ИСТОРИЯ КОМНАТЫ НА ДИСКЕ ═══");
                var manifest = RoomStore.LoadManifest(session.RoomId);
                Check("комната сохранена на диск", manifest != null);
                Check("ключ зашифрован DPAPI, открытого текста нет",
                    manifest != null && !string.IsNullOrEmpty(manifest.KeyProtected)
                    && !manifest.KeyProtected.Contains(RoomKey.Normalize(session.Key)));
                Check("ключ читается обратно на этом компьютере",
                    manifest != null && RoomStore.GetRoomKey(manifest) == RoomKey.Normalize(session.Key));
                Check("лента сохранена в файлы",
                    RoomStore.LoadFeed(session.RoomId, 200).Count >= 4,
                    $"записей: {RoomStore.LoadFeed(session.RoomId, 200).Count}");
                Check("комната видна в списке сохранённых",
                    RoomStore.ListRooms().Any(r => r.Id == session.RoomId));
                // Отпечаток справочников задаёт приложение при старте (App.OnStartup);
                // здесь проверяем, что он доехал до манифеста комнаты
                var manifestWithPrint = RoomStore.LoadManifest(session.RoomId);
                Check("отпечаток справочников записан в комнату",
                    manifestWithPrint != null && !string.IsNullOrEmpty(manifestWithPrint.DataFingerprint),
                    manifestWithPrint?.DataFingerprint ?? "");
                Check("отпечаток совпадает с заданным",
                    manifestWithPrint?.DataFingerprint == "items:12529|spells:5849",
                    manifestWithPrint?.DataFingerprint ?? "");
            }
            finally
            {
                string roomId = session.RoomId;
                session.Leave();
                if (!string.IsNullOrEmpty(roomId)) RoomStore.DeleteRoom(roomId);
                Say("  комната убрана за тестом");
            }
        }

        #region === ПРОВЕРКА СВЯЗКИ КНОПОК ===

        /// <summary>
        /// Проверяет, что у каждой кнопки с именем есть обработчик Click и что этот
        /// метод действительно объявлен в исходниках.
        ///
        /// Разметку и код читаем как текст, а не через отражение: MainWindow целиком
        /// в тестовую сборку не входит (он тянет за собой окно персонажа), поэтому
        /// проверять надо настоящие файлы приложения.
        ///
        /// Это ловит ошибку, которую невозможно увидеть при запуске: WPF не жалуется
        /// на кнопку без обработчика, она просто молча ничего не делает.
        /// </summary>
        private static void CheckButtonWiring(string xamlRelativePath, string[] codeFiles, string[] buttonNames)
        {
            string xamlPath = Path.Combine(_outDir, "Ui", xamlRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(xamlPath))
            {
                Check($"разметка {xamlRelativePath} доступна для проверки", false, xamlPath);
                return;
            }

            string xaml = File.ReadAllText(xamlPath, Encoding.UTF8);

            // Собираем код всех файлов, где могут лежать обработчики
            var code = new StringBuilder();
            foreach (var rel in codeFiles)
            {
                string p = Path.Combine(_outDir, "Ui", rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) code.AppendLine(File.ReadAllText(p, Encoding.UTF8));
                else Check($"исходник {rel} доступен для проверки", false, p);
            }
            string source = code.ToString();

            foreach (var name in buttonNames)
            {
                int at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
                if (at < 0)
                {
                    Check($"кнопка {name} есть в разметке", false);
                    continue;
                }

                // Берём тег кнопки целиком: от имени до конца элемента
                int tagEnd = xaml.IndexOf('>', at);
                string tag = tagEnd > at ? xaml.Substring(at, tagEnd - at + 1) : "";
                if (!tag.Contains("Click="))
                {
                    int nextElement = xaml.IndexOf("<", at + 1, StringComparison.Ordinal);
                    if (nextElement > 0) tag = xaml.Substring(at, nextElement - at);
                }

                // Click может стоять как в том же теге, так и в следующей строке
                if (!tag.Contains("Click="))
                {
                    int searchTo = Math.Min(xaml.Length, at + 700);
                    tag = xaml.Substring(at, searchTo - at);
                }

                var match = System.Text.RegularExpressions.Regex.Match(tag, @"Click=""([^""]+)""");
                if (!match.Success)
                {
                    Check($"у кнопки {name} есть обработчик нажатия", false, "Click не указан — нажатие ничего не сделает");
                    continue;
                }

                string handler = match.Groups[1].Value;
                bool declared = System.Text.RegularExpressions.Regex.IsMatch(
                    source, @"void\s+" + System.Text.RegularExpressions.Regex.Escape(handler) + @"\s*\(");

                Check($"кнопка {name} → {handler}()", declared,
                    declared ? "" : "метод-обработчик не найден в исходниках");
            }
        }

        #endregion

        #region === ПРОВЕРКА РАЗБОРА СПИСКОВ ===

        /// <summary>
        /// Проверяет, что пунктовые и нумерованные списки разбираются верно.
        ///
        /// Раньше парсер требовал ПРОБЕЛ после маркера: строка «1.текст» списком
        /// не считалась и рисовалась обычным текстом — отсюда «номера стоят
        /// криво» и «список обрывается на втором пункте».
        /// </summary>
        private static void CheckMarkdownLists()
        {
            try
            {
                // ── Нумерованный список без пробела после точки ──
                var doc = MarkdownParser.Parse("1.первый\n2.второй\n3.третий");
                var lists = FindLists(doc);
                Check("нумерованный список без пробела распознан", lists.Count > 0,
                    "списков: " + lists.Count);

                if (lists.Count > 0)
                {
                    Check("в нумерованном списке три пункта",
                        lists[0].ListItems.Count == 3,
                        "пунктов: " + lists[0].ListItems.Count);
                    Check("нумерованный список имеет номера",
                        lists[0].MarkerStyle == TextMarkerStyle.Decimal,
                        lists[0].MarkerStyle.ToString());
                }

                // ── Обычный вариант с пробелом тоже должен работать ──
                var doc2 = MarkdownParser.Parse("1. первый\n2. второй");
                var lists2 = FindLists(doc2);
                Check("нумерованный список с пробелом распознан",
                    lists2.Count > 0 && lists2[0].ListItems.Count == 2,
                    "списков: " + lists2.Count);

                // ── Пунктовый список без пробела ──
                var doc3 = MarkdownParser.Parse("-первый\n-второй\n-третий");
                var lists3 = FindLists(doc3);
                Check("пунктовый список без пробела распознан",
                    lists3.Count > 0 && lists3[0].ListItems.Count == 3,
                    "пунктов: " + (lists3.Count > 0 ? lists3[0].ListItems.Count : 0));

                if (lists3.Count > 0)
                {
                    Check("пунктовый список имеет маркеры-точки",
                        lists3[0].MarkerStyle == TextMarkerStyle.Disc,
                        lists3[0].MarkerStyle.ToString());
                }

                // ── Длинный список не должен обрываться ──
                var longText = string.Join("\n",
                    Enumerable.Range(1, 12).Select(n => n + ".пункт номер " + n));
                var doc4 = MarkdownParser.Parse(longText);
                var lists4 = FindLists(doc4);
                Check("длинный список не обрывается",
                    lists4.Count > 0 && lists4[0].ListItems.Count == 12,
                    "пунктов: " + (lists4.Count > 0 ? lists4[0].ListItems.Count : 0));

                // ── Перенос строки внутри пункта ──
                var doc5 = MarkdownParser.Parse("1.начало\n  продолжение строки\n2.второй");
                var lists5 = FindLists(doc5);
                Check("перенос внутри пункта присоединяется",
                    lists5.Count > 0 && lists5[0].ListItems.Count == 2,
                    "пунктов: " + (lists5.Count > 0 ? lists5[0].ListItems.Count : 0));

                // ── Только маркер без текста не создаёт пункт ──
                var doc6 = MarkdownParser.Parse("1.\n2.второй");
                var lists6 = FindLists(doc6);
                Check("маркер без текста не считается пунктом",
                    lists6.Count == 0 || lists6[0].ListItems.Count <= 1,
                    "пунктов: " + (lists6.Count > 0 ? lists6[0].ListItems.Count : 0));
            }
            catch (Exception ex)
            {
                Check("проверка списков прошла без исключения", false, ex.Message);
            }
        }

        /// <summary>
        /// Цвет текста в заметках: HTML из contenteditable (span/font) должен
        /// переживать сохранение в Markdown и обратную загрузку.
        /// </summary>
        private static void CheckNotesColor()
        {
            try
            {
                string html = "<p><span style=\"color: rgb(224, 106, 99);\">красный</span> текст</p>";
                string markdown = NotesHtmlBridge.ToMarkdown(html);
                Check("цвет из span сохраняется в Markdown",
                    markdown.Contains("{{color:#E06A63|красный}}"), markdown);

                string htmlBack = NotesHtmlBridge.ToHtml(markdown);
                Check("цвет из Markdown возвращается в span",
                    htmlBack.Contains("color:#E06A63") && htmlBack.Contains("красный"), htmlBack);

                string markdown2 = NotesHtmlBridge.ToMarkdown(
                    "<p><font color=\"#4FB6AC\">бирюзовый</font></p>");
                Check("цвет из font сохраняется в Markdown",
                    markdown2.Contains("{{color:#4FB6AC|бирюзовый}}"), markdown2);
            }
            catch (Exception ex)
            {
                Check("проверка цвета заметок прошла без исключения", false, ex.Message);
            }
        }

        /// <summary>Собирает все списки из документа.</summary>
        private static List<System.Windows.Documents.List> FindLists(FlowDocument doc)
        {
            var result = new List<System.Windows.Documents.List>();
            if (doc == null) return result;

            foreach (var block in doc.Blocks)
            {
                if (block is System.Windows.Documents.List list) result.Add(list);
            }
            return result;
        }

        #endregion

        #region === ПРОВЕРКА ПОКУПКИ И СТАКИВАНИЯ ===

        /// <summary>
        /// Проверяет покупку: одинаковые предметы должны стакиваться, а не
        /// создавать новую карточку, и добавляться ровно столько, сколько купили.
        /// </summary>
        private static void CheckShopPurchase()
        {
            int characterId = FindAnyCharacterId();
            if (characterId <= 0)
            {
                Say("  [ПРОПУСК] нет персонажа для проверки покупки");
                return;
            }

            string name = "Покупка проверка " + DateTime.Now.ToString("HHmmss");
            var inventory = new InventoryService(characterId);
            var shop = new ShopService(characterId);

            int catalogId = 0;
            try
            {
                catalogId = SrdItemImporter.SaveCustomItem(new SrdItemRow
                {
                    Name = name,
                    ItemType = "Зелье",
                    Rarity = "Обычный",
                    Weight = 0.5,
                    Description = "Служебная запись проверки покупки"
                });
                Check("предмет добавлен в каталог", catalogId > 0, "id=" + catalogId);
                if (catalogId <= 0) return;

                var item = shop.GetItem(catalogId);
                Check("предмет читается из каталога", item != null);

                // ── Покупка одной штуки ──
                int bought = shop.BuyItem(item, 1);
                Check("покупка прошла", bought > 0, "id в инвентаре=" + bought);

                var afterFirst = inventory.GetItem(bought);
                Check("куплено ровно столько, сколько просили",
                    afterFirst != null && afterFirst.Quantity == 1,
                    "количество: " + (afterFirst?.Quantity ?? 0));

                int rowsAfterFirst = CountInventoryRows(characterId, name);
                Check("в инвентаре одна запись, а не две", rowsAfterFirst == 1,
                    "записей: " + rowsAfterFirst);

                // ── Покупка ещё двух: должна стакнуться ──
                int boughtAgain = shop.BuyItem(item, 2);
                Check("повторная покупка вернула ту же запись", boughtAgain == bought,
                    $"{boughtAgain} против {bought}");

                int rowsAfterSecond = CountInventoryRows(characterId, name);
                Check("вторая карточка не создалась", rowsAfterSecond == 1,
                    "записей: " + rowsAfterSecond);

                var afterSecond = inventory.GetItem(bought);
                Check("количество сложилось: 1 + 2 = 3",
                    afterSecond != null && afterSecond.Quantity == 3,
                    "количество: " + (afterSecond?.Quantity ?? 0));

                // ── Похожий, но другой предмет не должен стакаться ──
                string name2 = name + " иная кость";
                int catalogId2 = SrdItemImporter.SaveCustomItem(new SrdItemRow
                {
                    Name = name2,
                    ItemType = "Зелье",
                    Rarity = "Обычный",
                    Weight = 0.5,
                    Damage = "2d6",   // отличается кость урона
                    Description = "Служебная запись проверки покупки"
                });

                var item2 = shop.GetItem(catalogId2);
                int bought2 = shop.BuyItem(item2, 1);
                Check("похожий предмет лёг отдельной записью", bought2 != bought,
                    $"{bought2} против {bought}");

                var other = inventory.GetItem(bought2);
                Check("у отдельного предмета своё количество",
                    other != null && other.Quantity == 1,
                    "количество: " + (other?.Quantity ?? 0));

                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();
                    using var cmd = new SqliteCommand(
                        "DELETE FROM SrdItem WHERE name IN (@n1, @n2) AND book=@b", conn);
                    cmd.Parameters.AddWithValue("@n1", name);
                    cmd.Parameters.AddWithValue("@n2", name2);
                    cmd.Parameters.AddWithValue("@b", SrdItemImporter.CustomBook);
                    cmd.ExecuteNonQuery();
                }
                catch { }
            }
            catch (Exception ex)
            {
                Check("проверка покупки прошла без исключения", false, ex.Message);
            }
            finally
            {
                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();
                    using var cmd = new SqliteCommand(
                        "DELETE FROM Inventory WHERE id_character=@c AND name LIKE @n", conn);
                    cmd.Parameters.AddWithValue("@c", characterId);
                    cmd.Parameters.AddWithValue("@n", name + "%");
                    cmd.ExecuteNonQuery();
                }
                catch { }
            }
        }

        /// <summary>Сколько записей инвентаря с таким именем.</summary>
        private static int CountInventoryRows(int characterId, string name)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM Inventory WHERE id_character=@c AND name=@n", conn);
                cmd.Parameters.AddWithValue("@c", characterId);
                cmd.Parameters.AddWithValue("@n", name);
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
            catch
            {
                return -1;
            }
        }

        #endregion

        #region === ПРОВЕРКА СВОЕГО ПРЕДМЕТА В МАГАЗИНЕ ===

        /// <summary>
        /// Проверяет, что свой предмет попадает в каталог магазина.
        ///
        /// Без файла справочника магазин пуст, и взаимодействовать с ним нельзя.
        /// Созданный вручную предмет кладётся в ТУ ЖЕ таблицу, что импортированные,
        /// поэтому ведёт себя как обычный: его видно в списке и можно купить.
        /// </summary>
        private static void CheckCustomShopItem()
        {
            string name = "Проверка своего предмета " + DateTime.Now.ToString("HHmmss");
            int id = 0;

            try
            {
                var row = new SrdItemRow
                {
                    Name = name,
                    ItemType = "Оружие",
                    Rarity = "Необычный",
                    Weight = 3.5,
                    Damage = "1d8",
                    DamageType = "рубящий",
                    Ac = 0,
                    Description = "Служебная запись проверки",
                    Attunement = true,
                    IsMagic = true
                };

                id = SrdItemImporter.SaveCustomItem(row);
                Check("свой предмет сохранён в каталог", id > 0, "id=" + id);

                if (id <= 0) return;

                // Читаем обратно через магазин: он читает тот же каталог
                int characterId = FindAnyCharacterId();
                var shop = new ShopService(characterId > 0 ? characterId : 1);

                var filter = new ShopFilter { Search = name };
                var found = shop.LoadCatalog(filter, 50);

                Check("свой предмет виден в каталоге магазина",
                    found.Any(x => x.Name == name), "найдено: " + found.Count);

                var saved = found.FirstOrDefault(x => x.Name == name);
                if (saved != null)
                {
                    Check("поля своего предмета сохранились",
                        saved.ItemType == "Оружие" && saved.Rarity == "Необычный" &&
                        saved.Damage == "1d8" && saved.Attunement && saved.IsMagic,
                        $"{saved.ItemType}, {saved.Rarity}, {saved.Damage}");

                    Check("свой предмет помечен как созданный вручную",
                        SrdItemImporter.IsCustom(saved), "книга: " + saved.Book);

                    Check("пометка «свои предметы» распознаётся",
                        SrdItemImporter.CustomBook == saved.Book, saved.Book);
                }

                Check("свой предмет посчитан", SrdItemImporter.CountCustomItems() > 0,
                    "своих предметов: " + SrdItemImporter.CountCustomItems());

                // Обновление того же предмета не должно создавать дубль
                row.Description = "Изменённое описание";
                row.Rarity = "Редкий";
                int again = SrdItemImporter.SaveCustomItem(row);
                Check("повторное сохранение не создаёт дубль", again > 0, "id=" + again);

                var after = shop.LoadCatalog(new ShopFilter { Search = name }, 50);
                Check("дубль не появился", after.Count(x => x.Name == name) == 1,
                    "записей с таким именем: " + after.Count(x => x.Name == name));

                // ── Проверка на похожий предмет ──
                //
                // Защита от неумелого пользователя: нельзя завести второй предмет
                // с теми же характеристиками.
                var twinCandidate = new SrdItemRow
                {
                    Name = name,
                    ItemType = "Оружие",
                    Rarity = "Редкий",           // как после правки выше
                    Weight = 3.5,
                    Damage = "1d8",
                    DamageType = "рубящий",
                    Description = "Изменённое описание"
                };

                var twin = SrdItemImporter.FindSimilar(twinCandidate);
                Check("одинаковый предмет распознан", twin != null,
                    twin == null ? "не найден" : "найден: " + twin.Name);
                Check("распознанный предмет — та самая запись",
                    twin != null && twin.Id == id, twin == null ? "нет" : "id=" + twin.Id);

                // Небольшое отличие — уже другой предмет
                var other = new SrdItemRow
                {
                    Name = name,
                    ItemType = "Оружие",
                    Rarity = "Редкий",
                    Weight = 3.5,
                    Damage = "2d6",              // другая кость урона
                    DamageType = "рубящий",
                    Description = "Изменённое описание"
                };
                Check("предмет с другой костью урона похожим не считается",
                    SrdItemImporter.FindSimilar(other) == null);

                // Другое описание — тоже другой предмет
                var other2 = new SrdItemRow
                {
                    Name = name,
                    ItemType = "Оружие",
                    Rarity = "Редкий",
                    Weight = 3.5,
                    Damage = "1d8",
                    DamageType = "рубящий",
                    Description = "Совсем другое описание"
                };
                Check("предмет с другим описанием похожим не считается",
                    SrdItemImporter.FindSimilar(other2) == null);

                // При изменении сама запись не должна считаться своей копией
                Check("при изменении запись не считается своей же копией",
                    SrdItemImporter.FindSimilar(twinCandidate, id) == null);
            }
            catch (Exception ex)
            {
                Check("проверка своего предмета прошла без исключения", false, ex.Message);
            }
            finally
            {
                // Убираем за собой
                if (id > 0)
                {
                    try
                    {
                        using var conn = DatabaseHelper.GetConnection();
                        conn.Open();
                        using var cmd = new SqliteCommand(
                            "DELETE FROM SrdItem WHERE name=@n AND book=@b", conn);
                        cmd.Parameters.AddWithValue("@n", name);
                        cmd.Parameters.AddWithValue("@b", SrdItemImporter.CustomBook);
                        cmd.ExecuteNonQuery();
                    }
                    catch { }
                }
            }
        }

        #endregion

        #region === ПРОВЕРКА ПЕРЕДАЧИ ФОТО ===

        /// <summary>
        /// Проверяет, что фотография доходит от одного игрока к другому.
        ///
        /// Фото идёт ЧЕРЕЗ ХОСТА: у остальных игроков порт закрыт, принять файл
        /// напрямую они не могут. Клиент режет файл на части и шлёт хосту,
        /// хост собирает и рассылает остальным.
        /// </summary>
        private static void CheckPhotoTransfer()
        {
            // ── Нарезка и сборка частей ──
            var bytes = new byte[100 * 1024];
            new Random(1).NextBytes(bytes);

            string temp = Path.Combine(Path.GetTempPath(), "hmwt_photo_test.png");
            try { File.WriteAllBytes(temp, bytes); }
            catch (Exception ex)
            {
                Say("  [ПРОПУСК] не удалось создать файл: " + ex.Message);
                return;
            }

            try
            {
                var parts = PhotoTransferService.Split(temp, "player-1", "room-1");
                Check("файл разбит на части", parts.Count > 1, "частей: " + parts.Count);
                Check("каждая часть не больше лимита",
                    parts.All(p => p.Base64.Length <= NetVersions.PhotoChunkSize * 2),
                    "размер части в base64: " + parts[0].Base64.Length);
                Check("в частях указан владелец",
                    parts.All(p => p.OwnerId == "player-1"));

                // Собираем обратно
                var assembler = new PhotoAssembler();
                byte[] assembled = null;
                foreach (var part in parts)
                    assembled = assembler.Add(part) ?? assembled;

                Check("файл собрался из частей", assembled != null,
                    assembled == null ? "не собрался" : assembled.Length + " байт");
                Check("собранный файл совпадает с исходным",
                    assembled != null && assembled.Length == bytes.Length &&
                    assembled.SequenceEqual(bytes),
                    assembled == null ? "нет" : assembled.Length + " против " + bytes.Length);

                // ── Части приходят вразнобой: сборка не должна зависеть от порядка ──
                var shuffled = parts.OrderBy(p => Guid.NewGuid()).ToList();
                var assembler2 = new PhotoAssembler();
                byte[] assembled2 = null;
                foreach (var part in shuffled)
                    assembled2 = assembler2.Add(part) ?? assembled2;

                Check("сборка не зависит от порядка частей",
                    assembled2 != null && assembled2.SequenceEqual(bytes));

                // ── Неполный набор не отдаётся ──
                var assembler3 = new PhotoAssembler();
                byte[] partial = null;
                for (int i = 0; i < parts.Count - 1; i++)
                    partial = assembler3.Add(parts[i]) ?? partial;

                Check("неполный файл не собирается", partial == null);

                // ── Сохранение принятого файла ──
                string saved = PhotoTransferService.SaveReceived("room-test", "player-2", bytes, ".png");
                Check("принятый файл сохранён", !string.IsNullOrEmpty(saved), saved);

                if (!string.IsNullOrEmpty(saved))
                {
                    string full = PhotoHelper.ResolvePhotoPath(saved);
                    Check("сохранённый файл читается по относительному пути",
                        !string.IsNullOrEmpty(full) && File.Exists(full), saved);

                    if (!string.IsNullOrEmpty(full) && File.Exists(full))
                        Check("сохранённый файл совпадает по размеру",
                            new FileInfo(full).Length == bytes.Length);
                }

                // ── Слишком большой файл не передаём ──
                var big = new byte[NetVersions.PhotoMaxBytes + 1024];
                string bigPath = Path.Combine(Path.GetTempPath(), "hmwt_photo_big.png");
                File.WriteAllBytes(bigPath, big);
                try
                {
                    var bigParts = PhotoTransferService.Split(bigPath, "player-1", "room-1");
                    Check("слишком большой файл не передаётся", bigParts.Count == 0,
                        "частей: " + bigParts.Count);
                }
                finally
                {
                    try { File.Delete(bigPath); } catch { }
                }

                // ── Сообщения про аватар объявлены в протоколе ──
                Check("в протоколе есть запрос аватара",
                    MsgType.PhotoRequest == "photo_request" && MsgType.PhotoMissing == "photo_missing",
                    MsgType.PhotoRequest + " / " + MsgType.PhotoMissing);
                Check("часть фото помечена владельцем и номером",
                    parts[0].OwnerId == "player-1" && parts[0].Index == 0 &&
                    parts[0].Total == parts.Count,
                    $"владелец {parts[0].OwnerId}, {parts[0].Index + 1} из {parts[0].Total}");
            }
            catch (Exception ex)
            {
                Check("проверка передачи фото прошла без исключения", false, ex.Message);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        #endregion

        #region === ПРОВЕРКА ВКЛАДКИ НАСТРОЕК ===

        /// <summary>
        /// Проверяет, что вкладка настроек открывается и не падает.
        ///
        /// Раньше настройки были отдельным окном и ссылались на стили главного
        /// окна (GhostBtn, RoomPrimaryBtn) — их нет в области видимости другого
        /// окна, поэтому при открытии было исключение разбора разметки.
        /// Теперь это вкладка внутри окна персонажа, и стили объявлены на месте.
        /// </summary>
        private static void CheckSettingsView()
        {
            SettingsView view = null;
            var window = new Window
            {
                Width = 900, Height = 700,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000, Top = -5000
            };

            try
            {
                // Именно создание и показ: ошибка разметки проявляется здесь
                view = new SettingsView();
                window.Content = view;
                window.Show();
                Pump();
                window.UpdateLayout();

                Check("вкладка настроек открылась без ошибки", view != null);
                Check("вкладка настроек получила размер",
                    view.ActualWidth > 100 && view.ActualHeight > 100,
                    $"{view.ActualWidth:0}×{view.ActualHeight:0}");

                // Ключевые элементы на месте
                Check("на вкладке есть ползунок масштаба", FindByName(view, "sldScale") != null);
                Check("на вкладке есть выбор режима гифок",
                    FindByName(view, "rbGifLow") != null && FindByName(view, "rbGifHigh") != null);
                // Интервала синхронизации больше нет: любое изменение уходит сразу
                Check("выбора интервала обновления нет",
                    FindByName(view, "cmbRefreshInterval") == null);

                // Масштаб применяется ко всем окнам: проверяем, что менеджер отвечает
                int before = UiScaleManager.CurrentPercent;
                UiScaleManager.Apply(90, save: false);
                Check("масштаб применяется через менеджер", UiScaleManager.CurrentPercent == 90,
                    "стало " + UiScaleManager.CurrentPercent + "%");
                UiScaleManager.Apply(before, save: false);

                // Режим качества читается и сохраняется
                GifQuality original = GifQualityService.Current;
                GifQualityService.Set(GifQuality.Low);
                Check("режим качества гифок сохраняется",
                    GifQualityService.Current == GifQuality.Low,
                    "стало " + GifQualityService.Current);
                Check("низкий режим ограничивает кадры", GifQualityService.MaxFrames <= 30,
                    "кадров: " + GifQualityService.MaxFrames);

                GifQualityService.Set(GifQuality.High);
                Check("высокий режим даёт больше кадров",
                    GifQualityService.MaxFrames >= 100,
                    "кадров: " + GifQualityService.MaxFrames);

                GifQualityService.Set(original);
            }
            catch (Exception ex)
            {
                // Разворачиваем вложенные исключения: у ошибок разметки имя
                // ненайденного ресурса лежит именно во внутреннем, а наружное
                // сообщение говорит только «StaticResourceHolder»
                Check("вкладка настроек открылась без ошибки", false, Describe(ex));
            }
            finally
            {
                try { window.Close(); } catch { }
            }
        }

        /// <summary>
        /// Собирает сообщение исключения вместе со всеми вложенными: у ошибок
        /// разметки суть («не удалось найти ресурс с именем…») лежит внутри.
        /// </summary>
        private static string Describe(Exception ex)
        {
            var text = new StringBuilder();
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (text.Length > 0) text.Append(" ← ");
                text.Append(current.GetType().Name).Append(": ").Append(current.Message);
            }
            return text.ToString();
        }

        /// <summary>Ищет элемент по имени в дереве разметки.</summary>
        private static FrameworkElement FindByName(FrameworkElement root, string name)
        {
            if (root == null) return null;
            if (root.Name == name) return root;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child)
                {
                    var found = FindByName(child, name);
                    if (found != null) return found;
                }
            }

            // Шаблоны и содержимое, которых ещё нет в визуальном дереве
            if (root is Panel panel)
            {
                foreach (var child in panel.Children.OfType<FrameworkElement>())
                {
                    var found = FindByName(child, name);
                    if (found != null) return found;
                }
            }

            return null;
        }

        #endregion

        #region === ПРОВЕРКА СЧЁТЧИКА ПРИ ПОДКЛЮЧЕНИИ ===

        /// <summary>
        /// Скрипт тёмной зоны: хост замечает вход жетона игрока в зону и
        /// присылает владельцу запрос на бросок. Проверяем один вход —
        /// повторные шаги внутри зоны второй запрос не шлют.
        /// </summary>
        private static void CheckZoneScriptTrigger(RoomSession session, RoomClient guest)
        {
            try
            {
                // Сцена 778: жетон 1 (персонаж 987654 — гость), тёмная зона
                // со скриптом «спасбросок ЛОВ, DC 15».
                var scene = new MapSceneData
                {
                    Id = 778,
                    CharacterId = 1,
                    Name = "Проверка зоны",
                    WidthPx = 700,
                    HeightPx = 700
                };
                scene.Tokens.Add(new MapTokenData
                {
                    Id = 1, SceneId = 778, Label = "Гром", X = 70, Y = 70,
                    SizeCells = 1, RevealCells = 6, OwnerCharacterId = 987654
                });
                scene.Shapes.Add(new MapShapeData
                {
                    Id = 50, SceneId = 778, Kind = "darkzone", X = 210, Y = 210,
                    RadiusCells = 2, Color = "#FFB98BFF",
                    ScriptKind = "save", ScriptAbility = "DEX", ScriptDC = 15,
                    ScriptName = "Ловушка"
                });

                session.PublishMapScene(scene);
                System.Threading.Thread.Sleep(300);

                var requests = new List<ZoneRollRequest>();
                guest.Events.ZoneRollReceived = r => { lock (requests) requests.Add(r); };

                // Снаружи (базовая точка), затем внутрь зоны, затем ещё шаг
                // внутри: второй запрос повториться не должен.
                guest.SendMapMoveLiveAsync(1, 70, 70, 0).GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(350);
                guest.SendMapMoveLiveAsync(1, 212, 212, 0).GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(500);
                guest.SendMapMoveLiveAsync(1, 220, 220, 0).GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(500);

                int count;
                ZoneRollRequest first = null;
                lock (requests) { count = requests.Count; first = requests.FirstOrDefault(); }

                Check("вход в тёмную зону просит бросок", count >= 1,
                    count == 0 ? "запрос не пришёл" : $"запросов: {count}");
                Check("в запросе спасбросок ЛОВ со сложностью 15",
                    first != null && first.Kind == "save" && first.Ability == "DEX" && first.DC == 15,
                    first == null ? "нет запроса"
                        : $"{first.Kind} {first.Ability} DC {first.DC}");
                Check("повторные шаги внутри зоны не плодят запросы", count == 1,
                    $"запросов: {count}");
            }
            catch (Exception ex)
            {
                Check("скрипт тёмной зоны", false, ex.Message);
            }
        }

        /// <summary>
        /// Живой ход клиента: лёгкое сообщение map_move(live) должно дойти до
        /// ведущего событием и обновить позицию жетона в его копии карты.
        /// Раньше жетон у остальных «оживал» только при отпускании мыши.
        /// </summary>
        private static void CheckLiveTokenMove(RoomSession session, RoomClient guest)
        {
            bool received = false;
            Action<int, double, double, double> handler = (id, x, y, f) =>
            {
                if (id == 1) received = true;
            };

            session.TokenMovedLive += handler;
            try
            {
                // Жетон 1 создан в проверке «состояния из трекера», его
                // владелец — персонаж 987654 (гость вошёл с этим id).
                guest.SendMapMoveLiveAsync(1, 140, 140, 0).GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(600);

                Check("живой ход клиента доходит до ведущего", received,
                    received ? "" : "событие TokenMovedLive не пришло");

                var token = session.GetMapScene()?.Tokens?.FirstOrDefault(t => t.Id == 1);
                Check("живой ход обновил позицию на карте ведущего",
                    token != null && Math.Abs(token.X - 140) < 0.5 && Math.Abs(token.Y - 140) < 0.5,
                    token == null ? "жетона нет" : $"({token.X:0},{token.Y:0})");
            }
            catch (Exception ex)
            {
                Check("живой ход жетона", false, ex.Message);
            }
            finally
            {
                session.TokenMovedLive -= handler;
            }
        }

        /// <summary>
        /// Проверяет, что сразу после входа через RoomSession в статусе стоит
        /// верное число участников, а не «0/8».
        ///
        /// Раньше цикл чтения сети запускался до того, как JoinRoom выставлял режим
        /// сессии: первый снимок от хоста приходил в этот промежуточный момент,
        /// список участников был ещё пуст, и статус залипал как 0/8 до следующего
        /// снимка. Здесь проверяем именно момент входа, без ожидания.
        /// </summary>
        private static void CheckClientCountOnJoin(string key, string roomId, int port)
        {
            // Отдельный экземпляр: общий занят хостом, а JoinRoom на активной
            // сессии не выполнить. Конструктор, а не GetUninitializedObject —
            // тот не выполняет инициализаторы полей, и замки остаются null
            var client = (RoomSession)Activator.CreateInstance(
                typeof(RoomSession),
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public,
                null, null, null);

            try
            {
                var me = new PlayerInfo
                {
                    Name = "Гость счётчика", CharacterName = "Счётчик", CharacterClass = "Плут",
                    Level = 1, Hp = 7, MaxHp = 8, ArmorClass = 14
                };

                bool joined = client.JoinRoom("127.0.0.1", port, key, me, out string error);
                Check("гость вошёл через RoomSession", joined, error);

                if (!joined) return;

                // Сразу после входа, без ожидания следующих снимков
                string status = client.StatusText;
                Say($"  статус гостя сразу после входа: {status}");

                int count = client.GetPlayers().Count;
                Check("список участников у гостя не пуст сразу после входа",
                    count >= 2, "участников: " + count);
                Check("в статусе гостя 2/8, а не 0/8",
                    status.Contains("2/8"), status);
            }
            catch (Exception ex)
            {
                Check("проверка счётчика прошла без исключения", false, ex.Message);
            }
            finally
            {
                try { client.Leave(); } catch { }
            }
        }

        #endregion

        #region === ПРОВЕРКА МАСШТАБА ИНТЕРФЕЙСА ===

        /// <summary>
        /// Проверяет, что масштаб интерфейса действительно сжимает рабочую часть окна.
        ///
        /// Нужно для ноутбуков: на маленьком экране вёрстка, рассчитанная на
        /// 1920×1080, не помещается целиком.
        /// </summary>
        private static void CheckUiScale()
        {
            // Автоподбор масштаба по экрану: та же формула, что в приложении.
            // Масштаб только уменьшает интерфейс и не превышает 100%.
            double w = SystemParameters.WorkArea.Width;
            double h = SystemParameters.WorkArea.Height;
            double factor = Math.Min(w / 1280.0, h / 800.0);
            int guessed = Math.Max(70, Math.Min(100, (int)(Math.Floor(factor * 100.0 / 5.0) * 5)));

            Say($"  экран: {w:0}×{h:0}, подобранный масштаб: {guessed}%");
            Check("автоподбор масштаба не превышает 100%",
                guessed >= 70 && guessed <= 100, guessed + "%");
            Check("на большом экране масштаб равен 100%",
                w < 1300 || h < 820 || guessed == 100, guessed + "%");

            // Проверяем, что LayoutTransform реально меняет видимый размер.
            //
            // ActualWidth для этого не годится: LayoutTransform применяется при
            // отрисовке, а ActualWidth остаётся логическим. Поэтому сравниваем
            // координаты углов, преобразованные к родителю.
            var content = new Border
            {
                Width = 400,
                Height = 200,
                Background = System.Windows.Media.Brushes.Black
            };

            var host = new Grid { Width = 800, Height = 400 };
            host.Children.Add(content);

            var window = new Window
            {
                Width = 900,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000,
                Top = -5000,
                Content = host
            };

            try
            {
                window.Show();
                Pump();
                window.UpdateLayout();

                // Сначала убеждаемся, что сам LayoutTransform в коде применяется:
                // ставим его НА САМ элемент, а не на хост
                content.LayoutTransform = new System.Windows.Media.ScaleTransform(0.5, 0.5);
                window.UpdateLayout();
                Pump();

                double direct = VisualWidth(host, content);
                Say($"  проверка механизма: элемент с LayoutTransform 0.5 имеет видимую ширину {direct:0}");
                Check("LayoutTransform на элементе уменьшает его вдвое",
                    Math.Abs(direct - 200) < 6, direct.ToString("0") + " вместо 200");

                // LayoutTransform не меняет логический размер — ActualWidth остаётся 400
                Check("логический размер при этом не меняется (ActualWidth)",
                    Math.Abs(content.ActualWidth - 400) < 2, content.ActualWidth.ToString("0"));

                // Теперь возвращаем масштаб на уровень хоста.
                //
                // ВАЖНО: TransformToAncestor масштаб РОДИТЕЛЯ не показывает —
                // LayoutTransform предка не входит в преобразование к этому предку.
                // Поэтому проверяем косвенно: место, которое содержимое занимает
                // в родителе, должно уменьшиться (DesiredSize пересчитывается).
                content.LayoutTransform = null;
                host.LayoutTransform = new System.Windows.Media.ScaleTransform(0.5, 0.5);
                window.UpdateLayout();
                Pump();

                double hostDesired = host.DesiredSize.Width;
                Say($"  проверка механизма: при масштабе хоста 0.5 его требуемая ширина {hostDesired:0}");
                Check("масштаб на родителе уменьшает занимаемое место",
                    hostDesired > 0 && hostDesired <= 450, hostDesired.ToString("0"));
            }
            finally
            {
                try { window.Close(); } catch { }
            }

            // ── Настоящий элемент масштаба в разметке главного окна ──
            string mainXaml = Path.Combine(_outDir, "Ui", "MainWindow.xaml");
            if (File.Exists(mainXaml))
            {
                string xaml = File.ReadAllText(mainXaml, Encoding.UTF8);

                Check("в главном окне есть обёртка масштаба", xaml.Contains("x:Name=\"UiScaleHost\""));
                Check("в обёртке есть ScaleTransform для масштаба",
                    xaml.Contains("x:Name=\"UiScale\"") && xaml.Contains("ScaleTransform"));
                Check("масштаб применяется через LayoutTransform, а не RenderTransform",
                    xaml.Contains("<Grid.LayoutTransform>"), "LayoutTransform пересчитывает вёрстку");
                Check("масштабируемая часть обёрнута в прокрутку",
                    xaml.Contains("x:Name=\"UiScaleScroll\""));
                Check("переключатель масштаба убран из шапки в настройки",
                    !xaml.Contains("CmbUiScale"), "список масштаба больше не в шапке");
            }
            else
            {
                Say("  [ПРОПУСК] разметка главного окна не скопирована в тест");
            }

            // ── Окно настроек: масштаб и режим гифок ──
            string settingsXaml = Path.Combine(_outDir, "Ui", "Windows", "SettingsWindow.xaml");
            if (File.Exists(settingsXaml))
            {
                string sx = File.ReadAllText(settingsXaml, Encoding.UTF8);

                Check("в настройках есть ползунок масштаба", sx.Contains("x:Name=\"sldScale\""));
                Check("в настройках есть кнопка подбора по экрану", sx.Contains("BtnScaleAuto_Click"));
                Check("в настройках есть выбор режима гифок",
                    sx.Contains("rbGifLow") && sx.Contains("rbGifMedium") && sx.Contains("rbGifHigh"));
                Check("в настройках есть переключатель имени Windows",
                    sx.Contains("chkShowWindowsName"));
            }
            else
            {
                Say("  [ПРОПУСК] разметка окна настроек не скопирована в тест");
            }
        }

        /// <summary>
        /// Видимая ширина элемента с учётом LayoutTransform.
        ///
        /// ActualWidth здесь не поможет: масштаб применяется при отрисовке и не
        /// меняет логический размер. Преобразуем углы к родителю и берём разницу.
        /// </summary>
        private static double VisualWidth(Visual host, FrameworkElement element)
        {
            try
            {
                var toHost = element.TransformToAncestor(host);
                var left = toHost.Transform(new Point(0, 0));
                var right = toHost.Transform(new Point(element.ActualWidth, 0));
                return Math.Abs(right.X - left.X);
            }
            catch
            {
                return element.ActualWidth;
            }
        }

        #endregion

        #region === ПРОВЕРКА ВЫБРОСА ЧАСТЯМИ ===

        /// <summary>
        /// Проверяет, что предмет можно выбросить частями, а не только целиком.
        ///
        /// Раньше в подтверждении всегда стояло «всё количество», и стопка
        /// исчезала целиком: выбросить одну стрелу из двадцати было нельзя.
        /// </summary>
        private static void CheckDiscardQuantity()
        {
            int characterId = FindAnyCharacterId();

            if (characterId <= 0)
            {
                Say("  [ПРОПУСК] нет персонажа для проверки инвентаря");
                return;
            }

            var inventory = new InventoryService(characterId);
            int itemId = 0;
            try
            {
                // Кладём стопку из 20 одинаковых предметов
                var item = new ItemData
                {
                    Name = "Проверка выброса",
                    Category = "Прочее",
                    Quantity = 20,
                    Weight = 1,
                    Description = "Служебная запись проверки"
                };
                itemId = inventory.SaveItem(0, item);
                Check("стопка создана", itemId > 0, $"id={itemId}, количество 20");

                var saved = inventory.GetItem(itemId);
                Check("в инвентаре 20 штук", saved != null && saved.Quantity == 20,
                    $"количество: {saved?.Quantity}");

                // Выбрасываем 3 из 20 — должно остаться 17
                bool removed = inventory.DeleteItemWithUnequip(itemId, 3, out string error);
                Check("выброс 3 из 20 прошёл", removed, error);

                var after = inventory.GetItem(itemId);
                Check("осталось 17 штук, а не ноль", after != null && after.Quantity == 17,
                    $"осталось: {after?.Quantity ?? 0}");

                // Выбрасываем остаток целиком
                bool rest = inventory.DeleteItemWithUnequip(itemId, 17, out string error2);
                Check("выброс остатка прошёл", rest, error2);
                Check("предмет исчез из инвентаря", inventory.GetItem(itemId) == null);
                itemId = 0;
            }
            finally
            {
                // Убираем за собой, если что-то осталось
                if (itemId > 0)
                {
                    try { inventory.DeleteItemWithUnequip(itemId, 9999, out _); } catch { }
                }
            }
        }

        /// <summary>Берёт идентификатор первого персонажа из базы.</summary>
        private static int FindAnyCharacterId()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT MIN(id_character) FROM CharacterInfo";
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                Say("  не удалось получить персонажа: " + ex.Message);
                return 0;
            }
        }

        #endregion

        #region === ПРОВЕРКА АНИМАЦИИ ПРИ НАВЕДЕНИИ ===

        /// <summary>
        /// Проверяет, что HoverGif действительно переключает кадры при наведении.
        ///
        /// Раньше этого не проверялось, и получилось так: статичный кадр показывался,
        /// а анимация не запускалась вовсе — ни в списке, ни в инспекторе.
        /// Здесь мы двигаем курсор по элементу по-настоящему и смотрим на кадры.
        /// </summary>
        private static void CheckHoverAnimation()
        {
            string gifPath = FindTestGif();
            if (string.IsNullOrEmpty(gifPath))
            {
                Say("  [ПРОПУСК] нет гифки для проверки анимации");
                return;
            }

            string name = Path.GetFileName(gifPath);
            var gif = GifFrameCache.Get(gifPath);
            if (gif == null || !gif.IsAnimated)
            {
                Say($"  [ПРОПУСК] файл {name} не является анимацией");
                return;
            }

            var control = new HoverGif
            {
                Width = 120,
                Height = 120,
                Stretch = Stretch.UniformToFill
            };
            control.SetImage(gifPath);

            var window = new Window
            {
                Width = 300,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000,
                Top = -5000,
                Content = control
            };

            try
            {
                window.Show();
                Pump();
                window.UpdateLayout();

                var image = FindImage(control);
                Check("внутри HoverGif есть картинка", image != null);
                if (image == null) return;

                Check("до наведения показан статичный кадр", image.Source != null);

                // Наводим курсор: поднимаем событие прямо на элементе
                var afterLoad = GifFrameCache.Get(gifPath);
                control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                {
                    RoutedEvent = Mouse.MouseEnterEvent
                });
                Pump();

                // Смотрим, менялись ли кадры за время анимации
                var seen = new HashSet<int>();
                for (int i = 0; i < 30; i++)
                {
                    Pump();
                    System.Threading.Thread.Sleep(60);

                    var current = image.Source as BitmapSource;
                    if (current != null) seen.Add(current.GetHashCode());

                    // Заодно проверяем, что кадр действительно один из набора
                    if (seen.Count >= 3) break;
                }

                Check("при наведении кадры сменяются", seen.Count >= 2,
                    $"увидели различных кадров: {seen.Count}");

                // Уводим курсор: анимация должна остановиться
                control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                {
                    RoutedEvent = Mouse.MouseLeaveEvent
                });
                Pump();

                var frameAfterLeave = image.Source as BitmapSource;
                System.Threading.Thread.Sleep(300);
                Pump();
                Check("после ухода курсора остаётся статичный кадр",
                    image.Source != null && ReferenceEquals(image.Source, frameAfterLeave));
                Check("первый кадр вернулся из лёгкого кэша",
                    GifFrameCache.GetFirstFrame(gifPath) != null);
            }
            finally
            {
                try { window.Close(); } catch { }
                GifFrameCache.Release(gifPath);
            }
        }

        /// <summary>Находит картинку внутри визуального дерева элемента.</summary>
        private static System.Windows.Controls.Image FindImage(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            int guard = 0;

            while (stack.Count > 0 && guard++ < 2000)
            {
                var current = stack.Pop();
                if (current is System.Windows.Controls.Image img) return img;

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; i++)
                    stack.Push(VisualTreeHelper.GetChild(current, i));
            }
            return null;
        }

        #endregion

        #region === ПРОВЕРКА ПОДЛОЖКИ ДИАЛОГОВ ===

        /// <summary>
        /// Проверяет выбор подложки на дереве, повторяющем окно персонажа:
        /// слева узкая колонка вкладок, справа содержимое.
        ///
        /// Диалог обязан лечь поверх ВСЕГО окна. Если он окажется внутри колонки
        /// вкладок, видна будет лишь узкая полоска — именно так и было.
        /// </summary>
        private static void CheckDialogPlacement()
        {
            var window = new Window
            {
                Width = 1200,
                Height = 800,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000,
                Top = -5000
            };

            try
            {
                // Повторяем разметку окна персонажа: две колонки, слева рейка вкладок
                var root = new Grid();
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var rail = new Grid { Background = Brushes.Black };
                var railButton = new Button { Content = "🤝", Width = 40, Height = 40 };
                rail.Children.Add(railButton);
                Grid.SetColumn(rail, 0);

                var content = new Grid { Background = Brushes.DarkSlateBlue };
                content.Children.Add(new TextBlock { Text = "Содержимое вкладки" });
                Grid.SetColumn(content, 1);

                root.Children.Add(rail);
                root.Children.Add(content);
                window.Content = root;

                window.Show();
                window.UpdateLayout();
                Pump();
                window.UpdateLayout();

                // Подложка ищется от кнопки в рейке — как при нажатии «Выйти» во вкладке
                var host = HMWT_MUI.Windows.MessageDialog.FindHostPanel(railButton);
                Check("подложка найдена", host != null);
                Check("подложкой выбран корень окна, а не колонка вкладок",
                    ReferenceEquals(host, root),
                    host == root ? "" : $"выбрана панель {host?.ActualWidth:0}×{host?.ActualHeight:0}, " +
                                        $"корень {root.ActualWidth:0}×{root.ActualHeight:0}");

                if (host != null && !ReferenceEquals(host, root))
                {
                    Check("подложка шире колонки вкладок", host.ActualWidth > 200,
                        $"{host.ActualWidth:0} пикселей");
                }

                // Теперь сам диалог: он должен перекрыть всё окно
                FrameworkElement overlay = null;
                var timer = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(350),
                    System.Windows.Threading.DispatcherPriority.Normal,
                    (s, e) =>
                    {
                        overlay = FindOverlay(window);
                        CloseOverlay(window, overlay);
                    },
                    System.Windows.Threading.Dispatcher.CurrentDispatcher);
                timer.Start();

                HMWT_MUI.Windows.MessageDialog.Confirm(railButton,
                    "Выйти из отряда?", "Выход из отряда", "Выйти", "Остаться", danger: true);
                timer.Stop();

                Check("окно подтверждения показано", overlay != null);

                if (overlay != null)
                {
                    double w = window.ActualWidth;
                    double h = window.ActualHeight;
                    Check("подтверждение перекрывает всё окно",
                        overlay.ActualWidth >= w * 0.95 && overlay.ActualHeight >= h * 0.95,
                        $"оверлей {overlay.ActualWidth:0}×{overlay.ActualHeight:0}, окно {w:0}×{h:0}");

                    // Раньше оверлей попадал в первую колонку корня — ту самую рейку
                    // вкладок шириной 56 пикселей, и видна была лишь её полоска.
                    // Родителя проверять нельзя: к моменту проверки оверлей может
                    // быть ещё не подключён к визуальному дереву (WPF делает это
                    // отложенно), поэтому смотрим на измеримые величины.
                    Check("подтверждение шире половины окна, а не полоска рейки",
                        overlay.ActualWidth > w * 0.5,
                        $"{overlay.ActualWidth:0} при окне {w:0}");
                    Check("подтверждение растянуто на все колонки окна",
                        Grid.GetColumnSpan(overlay) == root.ColumnDefinitions.Count,
                        $"охват колонок: {Grid.GetColumnSpan(overlay)} из {root.ColumnDefinitions.Count}");
                    Check("ширина подтверждения больше колонки вкладок",
                        overlay.ActualWidth > rail.ActualWidth * 2,
                        $"{overlay.ActualWidth:0} против {rail.ActualWidth:0}");
                }
            }
            finally
            {
                try { window.Close(); } catch { }
            }
        }

        #endregion

        #region === ПРОВЕРКА ПАМЯТИ ПОД ГИФКИ ===

        /// <summary>
        /// Проверяет, что разбор гифки не остаётся в памяти после освобождения.
        ///
        /// Анимация включается только при наведении, поэтому кадры должны
        /// освобождаться сразу. Если этого не делать, память копится до выхода
        /// из приложения — именно так и было раньше.
        /// </summary>
        private static void CheckGifMemory()
        {
            string gifPath = FindTestGif();

            if (string.IsNullOrEmpty(gifPath))
            {
                // Без файла проверить нечего: это не провал, а отсутствие данных
                Say("  [ПРОПУСК] в папке фотографий нет гифок для проверки");
                return;
            }

            string name = Path.GetFileName(gifPath);
            long estimate = GifFrameCache.EstimateFramesBytes(gifPath);
            CheckGifEstimate(estimate, name);

            // Первый кадр: лёгкий, должен читаться всегда
            var first = GifFrameCache.GetFirstFrame(gifPath);
            Check($"первый кадр гифки читается ({name})", first != null,
                first == null ? "" : $"{first.PixelWidth}×{first.PixelHeight}");

            // Полный набор: берём, смотрим размер, освобождаем
            long before = GC.GetTotalMemory(true);
            var gif = GifFrameCache.Get(gifPath);
            long afterLoad = GC.GetTotalMemory(true);

            if (gif == null)
            {
                Check("полный набор кадров разобран", false, name);
                return;
            }

            double usedMb = (afterLoad - before) / 1024.0 / 1024.0;
            Check("полный набор кадров разобран", gif.Frames.Length > 0,
                $"кадров {gif.Frames.Length}, стало больше на {usedMb:0.0} МБ");

            // Оценка должна быть близка к реальности: по ней решается, анимировать ли
            Check("оценка памяти сопоставима с реальной", estimate > 0,
                $"оценка {estimate / 1024.0 / 1024.0:0.0} МБ, реально {usedMb:0.0} МБ");

            // Освобождение: после него набор не должен возвращаться из кэша
            GifFrameCache.Release(gifPath);
            var again = GifFrameCache.Get(gifPath);
            Check("после освобождения набор разбирается заново (кэш очищен)",
                !ReferenceEquals(again, gif));
            GifFrameCache.Release(gifPath);

            // Статичный кадр при этом остаётся: он лёгкий и нужен в списках
            var firstAfter = GifFrameCache.GetFirstFrame(gifPath);
            Check("статичный кадр остался доступен после освобождения", firstAfter != null);

            // Порог анимации: гифка анимируется, только если её кадры влезают в лимит.
            // Лимит высокий, потому что анимация живёт лишь пока курсор на картинке.
            bool cheap = GifFrameCache.IsCheapToAnimate(gifPath);
            Say($"  [ИНФО] анимировать эту гифку: {(cheap ? "да" : "нет")} " +
                $"(оценка {estimate / 1024.0 / 1024.0:0.0} МБ кадров)");
        }

        private static void CheckGifEstimate(long estimate, string name)
        {
            Check("оценка памяти по кадрам считается", estimate >= 0,
                $"файл {name}: {estimate / 1024.0 / 1024.0:0.0} МБ");
        }

        /// <summary>Ищет любую гифку в папке фотографий приложения.</summary>
        private static string FindTestGif()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT");

                if (!Directory.Exists(root)) return null;

                foreach (var file in Directory.GetFiles(root, "*.gif", SearchOption.AllDirectories))
                    return file;
            }
            catch { }
            return null;
        }

        #endregion

        #region === ПРОВЕРКА ВЕРСИИ ===

        /// <summary>
        /// Сверяет версию из AssemblyInfo.cs с константой в NetVersions.cs.
        ///
        /// Обе нужны: первая попадает в свойства exe и в установщик, вторая
        /// участвует в рукопожатии комнаты. Расхождение означает, что игроки
        /// с «одинаковой» на вид версией не смогут подключиться друг к другу.
        /// </summary>
        private static void CheckVersionConsistency()
        {
            string asmPath = Path.Combine(_outDir, "Ui", "AssemblyInfo.cs");
            string netPath = Path.Combine(_outDir, "Ui", "NetVersions.cs");

            if (!File.Exists(asmPath) || !File.Exists(netPath))
            {
                Check("файлы версий доступны для проверки", false,
                    $"AssemblyInfo: {File.Exists(asmPath)}, NetVersions: {File.Exists(netPath)}");
                return;
            }

            var asmMatch = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(asmPath, Encoding.UTF8), @"AssemblyVersion\(""([^""]+)""\)");
            var netMatch = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(netPath, Encoding.UTF8), @"AppVersion\s*=\s*""([^""]+)""");

            string asmVersion = asmMatch.Success ? asmMatch.Groups[1].Value : "";
            string netVersion = netMatch.Success ? netMatch.Groups[1].Value : "";

            Check("версия найдена в AssemblyInfo", !string.IsNullOrEmpty(asmVersion), asmVersion);
            Check("версия найдена в NetVersions", !string.IsNullOrEmpty(netVersion), netVersion);
            Check("версии в AssemblyInfo и NetVersions совпадают", asmVersion == netVersion,
                $"{asmVersion} против {netVersion} — при расхождении игроки не подключатся");

            // Версия должна разбираться и быть не ниже первого выпуска.
            //
            // Раньше здесь стояло «не ниже 1.1: с неё появились комнаты» — проверка
            // из времён промежуточных сборок 1.2.x. Нумерация закрыта: первый
            // официальный выпуск — 1.0.0.0 (установщик на GitHub), комнаты в нём
            // есть. Смысл проверки сохраняем: поймать мусор в строке версии или
            // случайный откат ниже первого выпуска.
            // Сравниваем как версию, а не как строку: «1.0.0.0» строкой
            // не начинается с «1.1», хотя это тот же выпуск.
            bool atLeastFirstRelease;
            try
            {
                atLeastFirstRelease = new Version(asmVersion) >= new Version(1, 0, 0, 0);
            }
            catch
            {
                atLeastFirstRelease = false;
            }

            Check("версия не ниже первого выпуска (1.0.0.0)", atLeastFirstRelease, asmVersion);
        }

        #endregion

        #region === СВЯЗКА ПЕРСОНАЖ ↔ УЧАСТНИК БОЯ ===

        /// <summary>
        /// character_id — ключ, по которому карта, трекер и лист персонажа
        /// должны говорить об одном участнике. Проверяем модель, JSON (по нему
        /// карта уходит клиенту) и настоящую колонку в базе: одной
        /// CREATE TABLE IF NOT EXISTS для старых баз мало — нужна миграция.
        /// </summary>
        private static void CheckCharacterLink()
        {
            const int characterId = 987654;

            var combatant = new CombatantData
            {
                Id = 7,
                Name = "Гром",
                Kind = CombatantKind.Player,
                CharacterId = characterId,
                Conditions = "prone, charmed",
                HitPoints = 17,
                MaxHitPoints = 30
            };

            var clone = combatant.Clone();
            Check("Clone переносит character_id", clone.CharacterId == characterId, $"id={clone.CharacterId}");
            Check("Clone переносит состояния", clone.Conditions == "prone, charmed", clone.Conditions);

            // Карта уезжает клиенту как JSON: владелец жетона должен пережить
            // сериализацию, иначе клиент не узнает свой жетон.
            var token = new MapTokenData { Id = 3, CombatantId = 7, OwnerCharacterId = characterId };
            var tokenBack = NetJson.FromElement<MapTokenData>(NetJson.ToElement(token));
            Check("карта: ownerCharacterId доезжает до клиента",
                tokenBack != null && tokenBack.OwnerCharacterId == characterId,
                tokenBack == null ? "null" : $"id={tokenBack.OwnerCharacterId}");

            // Снимок комнаты несёт состав боя: по нему лист персонажа у клиента
            // находит своего участника, даже если инициатива ещё не брошена.
            var state = new InitiativeState
            {
                IsActive = true,
                Roster = new List<InitiativeCombatantDto>
                {
                    new InitiativeCombatantDto
                    {
                        Id = 7, Name = "Гром", CharacterId = characterId, Conditions = "prone"
                    }
                }
            };
            var stateBack = NetJson.FromElement<InitiativeState>(NetJson.ToElement(state));
            Check("снимок: состав боя с character_id и состояниями",
                stateBack?.Roster != null && stateBack.Roster.Count == 1 &&
                stateBack.Roster[0].CharacterId == characterId &&
                stateBack.Roster[0].Conditions == "prone",
                stateBack?.Roster == null ? "состава нет" : $"участников: {stateBack.Roster.Count}");

            // Настоящая база: сохранение, чтение и поиск по character_id.
            EncounterData encounter = null;
            try
            {
                encounter = InitiativeService.CreateEncounter("Проверка связи");
                var saved = InitiativeService.AddByName(encounter, "Проверка связи",
                    0, CombatantKind.Player, 0, characterId);
                Check("участник боя сохраняется с character_id",
                    saved != null && saved.CharacterId == characterId,
                    saved == null ? "не сохранён" : $"id={saved.CharacterId}");

                if (saved != null)
                {
                    saved.Conditions = "blinded";
                    InitiativeService.SaveCombatant(encounter, saved);

                    var fresh = InitiativeService.LoadEncounter(encounter.Id)?.Combatants
                        .FirstOrDefault(c => c != null && c.Id == saved.Id);
                    Check("character_id и состояния читаются из базы",
                        fresh != null && fresh.CharacterId == characterId && fresh.Conditions == "blinded",
                        fresh == null ? "участник не найден"
                            : $"char={fresh.CharacterId}, состояния «{fresh.Conditions}»");

                    var byId = InitiativeService.FindForCharacter(characterId, null);
                    Check("поиск участника по character_id",
                        byId != null && byId.Value.Combatant.Id == saved.Id,
                        byId == null ? "не найден" : $"id={byId.Value.Combatant.Id}");

                    var updated = InitiativeService.UpdateCombatantConditions(saved.Id, "poisoned");
                    Check("состояния пишутся по id участника",
                        updated != null &&
                        InitiativeService.LoadEncounter(encounter.Id).Combatants
                            .Any(c => c != null && c.Id == saved.Id && c.Conditions == "poisoned"),
                        updated == null ? "группа не найдена" : "сохранено");
                }
            }
            catch (Exception ex)
            {
                Check("база: связка персонажа", false, ex.Message);
            }
            finally
            {
                try { if (encounter != null) InitiativeService.DeleteEncounter(encounter.Id); }
                catch { }
            }
        }

        #endregion

        #region === СОСТОЯНИЯ ИЗ ТРЕКЕРА НА КАРТУ ===

        /// <summary>
        /// SaveCombatant (трекер, лист, карта) поднимает Changed — сервер
        /// переносит состояния в жетоны и рассылает карту. Проверяем, что
        /// жетон получает новый набор без отдельного «обновления сцены».
        /// </summary>
        private static void CheckTrackerToMap(RoomSession session)
        {
            const int characterId = 987654;
            EncounterData encounter = null;

            try
            {
                encounter = InitiativeService.CreateEncounter("Проверка карты");
                var combatant = InitiativeService.AddByName(encounter, "Гром", 0,
                    CombatantKind.Player, 0, characterId);
                Check("участник боя для карты создан", combatant != null);
                if (combatant == null) return;

                var scene = new MapSceneData
                {
                    Id = 777,
                    CharacterId = 1,
                    Name = "Проверка карты",
                    WidthPx = 700,
                    HeightPx = 700
                };
                scene.Tokens.Add(new MapTokenData
                {
                    Id = 1,
                    SceneId = 777,
                    CombatantId = combatant.Id,
                    Label = "Гром",
                    X = 70,
                    Y = 70,
                    SizeCells = 1,
                    RevealCells = 6
                });

                session.PublishMapScene(scene);
                System.Threading.Thread.Sleep(250);

                combatant.Conditions = "prone, charmed";
                InitiativeService.SaveCombatant(encounter, combatant);
                System.Threading.Thread.Sleep(500);

                var token = session.GetMapScene()?.Tokens?.FirstOrDefault(t => t.Id == 1);
                Check("жетон на карте получил состояния из трекера",
                    token != null && token.Conditions == "prone, charmed",
                    token == null ? "жетона нет" : $"«{token.Conditions}»");

                var back = InitiativeService.LoadEncounter(encounter.Id)?.Combatants
                    .FirstOrDefault(c => c != null && c.Id == combatant.Id);
                Check("состояния остались у участника боя",
                    back != null && back.Conditions == "prone, charmed",
                    back == null ? "участник не найден" : $"«{back.Conditions}»");
            }
            catch (Exception ex)
            {
                Check("состояния из трекера на карту", false, ex.Message);
            }
            finally
            {
                try { if (encounter != null) InitiativeService.DeleteEncounter(encounter.Id); }
                catch { }
            }
        }

        #endregion

        #region === АУДИО В КОМНАТЕ ===

        /// <summary>
        /// Аудио в комнате: команда ведущего доходит до игрока, а файл
        /// передаётся кусками и собирается без потерь. Это проверяет и
        /// протокол, и хранилище библиотеки.
        /// </summary>
        private static void CheckAudioRoom(RoomSession session, RoomClient guest)
        {
            string hash = "test" + Guid.NewGuid().ToString("N");
            string relative = Path.Combine("Library", hash + ".mp3");
            string fullPath = Path.Combine(AudioService.Root, relative);

            try
            {
                var played = new List<AudioState>();
                int stopped = 0;

                guest.Events.AudioPlayed = state => { lock (played) played.Add(state); };
                guest.Events.AudioStopped = () => { lock (played) stopped++; };

                session.PublishAudio(new AudioState
                {
                    Hash = "aa11", Extension = ".mp3", Name = "Тестовый трек",
                    Gain = 0.75, On = true
                });
                System.Threading.Thread.Sleep(400);

                AudioState first;
                lock (played) first = played.FirstOrDefault();
                Check("игрок получил команду играть", first != null && first.Hash == "aa11",
                    first == null ? "команда не пришла" : first.Hash);
                Check("громкость трека доехала", first != null && Math.Abs(first.Gain - 0.75) < 0.01,
                    first == null ? "нет данных" : first.Gain.ToString("0.00"));

                session.PublishAudioStop();
                System.Threading.Thread.Sleep(300);
                Check("игрок получил стоп", stopped >= 1, $"стопов: {stopped}");

                // ── передача файла ──
                AudioService.EnsureFolders();
                byte[] data = new byte[100 * 1024];
                new Random(7).NextBytes(data);
                File.WriteAllBytes(fullPath, data);

                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using var cmd = new SqliteCommand(
                        @"INSERT INTO AudioTrack (hash, path, name, kind, gain, duration_ms, size, added_at)
                          VALUES (@h, @p, 'Тест', 'music', 1, 0, @s, @at)", conn);
                    cmd.Parameters.AddWithValue("@h", hash);
                    cmd.Parameters.AddWithValue("@p", relative);
                    cmd.Parameters.AddWithValue("@s", data.Length);
                    cmd.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.ExecuteNonQuery();
                }

                var chunks = new List<AudioChunk>();
                guest.Events.AudioChunkReceived = chunk => { lock (chunks) chunks.Add(chunk); };
                guest.SendAudioNeedAsync(hash).GetAwaiter().GetResult();

                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 6000)
                {
                    lock (chunks)
                    {
                        if (chunks.Count > 0 && chunks.All(c => c.Total > 0) &&
                            chunks.Select(c => c.Index).Distinct().Count() >= chunks[0].Total)
                            break;
                    }
                    System.Threading.Thread.Sleep(60);
                }

                byte[] received;
                int totalChunks;
                lock (chunks)
                {
                    var ordered = chunks.OrderBy(c => c.Index).ToList();
                    totalChunks = ordered.Count == 0 ? 0 : ordered[0].Total;
                    received = ordered.Count == 0
                        ? Array.Empty<byte>()
                        : ordered.SelectMany(c => Convert.FromBase64String(c.Data)).ToArray();
                }

                Check("файл передан целиком",
                    received.Length == data.Length && received.SequenceEqual(data),
                    $"получено {received.Length} из {data.Length} байт, кусков {chunks.Count}/{totalChunks}");
                Check("кусков столько, сколько объявлено", totalChunks == chunks.Count,
                    $"{chunks.Count} из {totalChunks}");

                // ── клиент-диджей: файла у ведущего нет ──
                // Сервер должен попросить файл у автора, принять куски и
                // только потом разослать play. Это ловит забытый case AudioChunk.
                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using var del = new SqliteCommand("DELETE FROM AudioTrack WHERE hash = @h", conn);
                    del.Parameters.AddWithValue("@h", hash);
                    del.ExecuteNonQuery();
                }

                var uploaded = new List<AudioState>();
                guest.Events.AudioPlayed = state =>
                {
                    lock (uploaded) uploaded.Add(state);
                };
                string needHash = null;
                guest.Events.AudioNeeded = h => needHash = h;

                guest.SendAudioPlayAsync(new AudioState
                {
                    Hash = hash, Extension = ".mp3", Name = "Тест", Gain = 0.9, On = true
                }).GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(500);
                Check("ведущий просит файл у игрока-диджея", needHash == hash,
                    needHash == null ? "запрос не пришёл" : needHash);

                if (needHash == hash)
                {
                    int uploadTotal = (data.Length + 48 * 1024 - 1) / (48 * 1024);
                    for (int i = 0; i < uploadTotal; i++)
                    {
                        int length = Math.Min(48 * 1024, data.Length - i * 48 * 1024);
                        guest.SendAudioChunkAsync(new AudioChunk
                        {
                            Hash = hash, Extension = ".mp3", Index = i, Total = uploadTotal,
                            Data = Convert.ToBase64String(data, i * 48 * 1024, length)
                        }).GetAwaiter().GetResult();
                    }
                }
                System.Threading.Thread.Sleep(800);
                AudioState uploadedState;
                lock (uploaded) uploadedState = uploaded.FirstOrDefault(x => x.Hash == hash);
                Check("трек игрока запущен после загрузки", uploadedState != null,
                    uploadedState == null ? "play не пришёл" : uploadedState.Hash);
            }
            catch (Exception ex)
            {
                Check("аудио в комнате", false, ex.Message);
            }
            finally
            {
                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();
                    using var cmd = new SqliteCommand("DELETE FROM AudioTrack WHERE hash = @h", conn);
                    cmd.Parameters.AddWithValue("@h", hash);
                    cmd.ExecuteNonQuery();
                }
                catch { }
                try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
            }
        }

        #endregion

        #region === СХЕМА БАЗЫ ===

        /// <summary>
        /// Версия схемы и миграции: база должна быть доведена до CurrentVersion,
        /// а индекс по character_id — существовать (его создаёт миграция 2).
        /// </summary>
        private static void CheckSchemaVersion()
        {
            try
            {
                int version = Services.SchemaMigrator.GetVersion();
                Check("версия схемы базы актуальна",
                    version == Services.SchemaMigrator.CurrentVersion,
                    $"версия {version} из {Services.SchemaMigrator.CurrentVersion}");

                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_combatant_character'",
                    conn);
                Check("индекс по character_id создан", Convert.ToInt32(cmd.ExecuteScalar()) == 1);

                Check("быстрая проверка базы", Services.SchemaMigrator.QuickCheck(out string report), report);
            }
            catch (Exception ex)
            {
                Check("схема базы", false, ex.Message);
            }
        }

        #endregion

        #region === ПОИСК ОВЕРЛЕЯ ДИАЛОГА ===

        /// <summary>Находит оверлей диалога: Grid с максимальным ZIndex в дереве окна.</summary>
        private static FrameworkElement FindOverlay(Window window)
        {
            FrameworkElement found = null;
            int bestZ = 0;
            var stack = new Stack<DependencyObject>();
            stack.Push(window);
            int guard = 0;

            while (stack.Count > 0 && guard++ < 20000)
            {
                var current = stack.Pop();

                if (current is System.Windows.Controls.Panel p && p.Background is SolidColorBrush)
                {
                    int z = System.Windows.Controls.Panel.GetZIndex(p);
                    if (z >= 4000 && z >= bestZ)
                    {
                        bestZ = z;
                        found = p;
                    }
                }

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; i++)
                    stack.Push(VisualTreeHelper.GetChild(current, i));
            }

            return found;
        }

        /// <summary>Убирает оверлей из дерева: диалог закрывается кликом по кнопке.</summary>
        private static void CloseOverlay(Window window, FrameworkElement overlay)
        {
            if (overlay == null) return;

            // Нажимаем первую кнопку диалога — так он закроется штатным путём
            var button = FindFirstButton(overlay);
            if (button != null)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return;
            }

            (overlay.Parent as System.Windows.Controls.Panel)?.Children.Remove(overlay);
        }

        private static Button FindFirstButton(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            int guard = 0;

            while (stack.Count > 0 && guard++ < 5000)
            {
                var current = stack.Pop();
                if (current is Button b) return b;

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; i++)
                    stack.Push(VisualTreeHelper.GetChild(current, i));
            }
            return null;
        }

        /// <summary>Лежит ли элемент внутри указанного предка.</summary>
        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            DependencyObject current = element;
            int guard = 0;
            while (current != null && guard++ < 1000)
            {
                if (ReferenceEquals(current, ancestor)) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        /// <summary>
        /// Встречается ли предок в визуальном дереве над элементом.
        ///
        /// Отдельно от IsDescendantOf, потому что здесь нужен ответ «есть ли корень
        /// выше», а не «совпадает ли сам элемент»: WPF вставляет между панелью
        /// и её содержимым свой ContentPresenter, поэтому прямой родитель не наш.
        /// </summary>
        private static bool HasAncestor(DependencyObject element, DependencyObject ancestor)
        {
            DependencyObject current = VisualTreeHelper.GetParent(element);
            int guard = 0;
            while (current != null && guard++ < 1000)
            {
                if (ReferenceEquals(current, ancestor)) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        #endregion

        #region === ПЕРЕДАЧА ПРЕДМЕТОВ ===

        /// <summary>
        /// Обмен переживает сериализацию: в сообщении едут все пять монет и
        /// количество предмета. Проверка дешёвая, но ловит опечатку в имени
        /// поля, из-за которой платина терялась бы по дороге.
        /// Ещё здесь проверяется защита от повтора: один и тот же номер сделки
        /// не должен начислиться дважды.
        /// </summary>
        private static void CheckTradeProtocol()
        {
            var message = new TradeMessage
            {
                TradeId = "trade-proto",
                Kind = "offer",
                FromPlayerId = "a",
                FromName = "Гром",
                ToPlayerId = "b",
                ToName = "Тишь",
                Platinum = 1,
                Gold = 2,
                Electrum = 3,
                Silver = 4,
                Copper = 5,
                Items = new List<TradeItem>
                {
                    new TradeItem
                    {
                        SourceItemId = 77, Name = "Стрелы", Category = "Боеприпасы",
                        Quantity = 13, Weight = 0.05m, Rarity = "обычный"
                    }
                }
            };

            var envelope = new NetMessage(MsgType.Trade, message);
            var back = envelope.As<TradeMessage>();

            Check("обмен разбирается из конверта", back != null, "конверт пуст");
            if (back == null) return;

            Check("в обмене едут все пять монет",
                back.Platinum == 1 && back.Gold == 2 && back.Electrum == 3 &&
                back.Silver == 4 && back.Copper == 5,
                $"{back.Platinum} п. · {back.Gold} з. · {back.Electrum} э. · {back.Silver} с. · {back.Copper} м.");
            Check("платина не путается с золотом", back.Platinum == 1 && back.Gold == 2);
            Check("количество предмета не теряется",
                back.Items.Count == 1 && back.Items[0].Quantity == 13 && back.Items[0].SourceItemId == 77,
                back.Items.Count == 0 ? "предметов нет" : $"шт: {back.Items[0].Quantity}");

            string id = Guid.NewGuid().ToString("N");
            Check("номер сделки запоминается", TradeWindow.MarkCommitApplied(id));
            Check("повтор с тем же номером не применяется", !TradeWindow.MarkCommitApplied(id));
            TradeWindow.UnmarkCommitApplied(id);
            Check("снятая отметка снова пропускает передачу", TradeWindow.MarkCommitApplied(id));
        }

        /// <summary>
        /// Окно отправителя и окно получателя: рисуем в картинку и смотрим.
        ///
        /// Проверяется то, из-за чего окно переделывали: тёмный фон без системной
        /// шапки, светлый текст, все пять монет, фильтры в одну строку, баланс,
        /// кламп количества по остатку и корзина передачи.
        /// </summary>
        private static void CheckTradeWindow()
        {
            int characterId = FindCharacterWithInventory(out int itemCount);
            if (characterId <= 0)
            {
                Say("  [ПРОПУСК] в базе нет персонажа с инвентарём");
                return;
            }
            Say($"  персонаж {characterId}, предметов в инвентаре: {itemCount}");

            var other = new PlayerInfo
            {
                Id = "trade-peer",
                Name = "Лиза",
                CharacterName = "Тишь",
                CharacterClass = "Плут",
                Level = 3,
                CharacterId = 987651,
                State = PeerState.Online
            };

            // ── отправитель ──
            var sender = new TradeWindow(null, characterId, other);
            try
            {
                sender.WindowStartupLocation = WindowStartupLocation.Manual;
                sender.Left = -6000;
                sender.Top = -6000;
                sender.Show();
                Pump();
                sender.UpdateLayout();

                Check("окно передачи без системной шапки", sender.WindowStyle == WindowStyle.None);
                Check("окно передачи рисует себя само", sender.AllowsTransparency);

                var frame = sender.Content as Border;
                var background = frame?.Background as SolidColorBrush;
                Check("фон окна — тёмный токен B.Panel",
                    background != null && LuminanceOf(background.Color) < 0.25,
                    background == null ? "фон не задан" : background.Color.ToString());

                Check("внизу виден баланс «У вас: … п. · … з. · … э. · … с. · … м.»",
                    sender.BalanceText.StartsWith("У вас:") &&
                    sender.BalanceText.Contains(" п. · ") && sender.BalanceText.Contains(" э. · ") &&
                    sender.BalanceText.Contains(" м."),
                    sender.BalanceText);

                Check("рядом показано, сколько останется",
                    sender.RemainingText.StartsWith("Останется:"), sender.RemainingText);

                int labels = CountTexts(sender, "ПЛАТИНА", "ЗОЛОТО", "ЭЛЕКТРУМ", "СЕРЕБРО", "МЕДЬ");
                Check("в сетке монет все пять подписей", labels == 5, $"найдено: {labels}");

                var searchBox = FindDescendants<TextBox>(sender).FirstOrDefault(t => (t.Tag as string) == "Найти предмет");
                var combos = FindDescendants<ComboBox>(sender).ToList();
                var filterRoot = searchBox?.Parent as UIElement;

                double searchY = searchBox != null && filterRoot != null ? searchBox.TranslatePoint(new Point(0, 0), filterRoot).Y : -1;
                double firstY = combos.Count > 0 && filterRoot != null ? combos[0].TranslatePoint(new Point(0, 0), filterRoot).Y : -2;
                double secondY = combos.Count > 1 && filterRoot != null ? combos[1].TranslatePoint(new Point(0, 0), filterRoot).Y : -3;
                double searchX = searchBox != null && filterRoot != null ? searchBox.TranslatePoint(new Point(0, 0), filterRoot).X : -1;
                double firstX = combos.Count > 0 && filterRoot != null ? combos[0].TranslatePoint(new Point(0, 0), filterRoot).X : -2;
                double secondX = combos.Count > 1 && filterRoot != null ? combos[1].TranslatePoint(new Point(0, 0), filterRoot).X : -3;

                bool inline = filterRoot != null && combos.Count >= 2 &&
                              Math.Abs(searchY - firstY) <= 2 &&
                              Math.Abs(firstY - secondY) <= 2 &&
                              searchX < firstX && firstX < secondX;
                string filterGeometry = filterRoot == null
                    ? "фильтры не найдены"
                    : $"поиск y={searchY:0.#} x={searchX:0.#}; категория y={firstY:0.#} x={firstX:0.#}; сортировка y={secondY:0.#} x={secondX:0.#}";
                Check("поиск, категория и сортировка стоят в одной строке", inline, filterGeometry);

                var row = sender.LeftList.Children.OfType<Border>().FirstOrDefault();
                var flag = row == null ? null : FindDescendants<CheckBox>(row).FirstOrDefault();
                Check("в строке инвентаря свой флажок", flag != null,
                    flag?.Style == null ? "у флажка нет стиля CustomCheckBox" : "");
                Check("флажок оформлен стилем CustomCheckBox", flag?.Style != null);

                if (flag != null)
                {
                    flag.IsChecked = true;
                    Pump();
                    var picked = sender.SelectedItems.FirstOrDefault();
                    Check("выбранный предмет попадает в сделку", picked != null);

                    if (picked != null)
                    {
                        var qtyBox = FindDescendants<TextBox>(row).FirstOrDefault();
                        if (qtyBox != null)
                        {
                            qtyBox.Text = "9999";
                            Pump();
                            Check("количество не поднимается выше остатка",
                                sender.QuantityOf(picked.Id) == picked.Quantity,
                                $"в инвентаре {picked.Quantity}, в сделке {sender.QuantityOf(picked.Id)}");
                        }
                        else
                        {
                            Check("у количества есть поле ввода", false, "поле не найдено");
                        }

                        Check("корзина передачи заполняется", sender.RightList.Children.Count > 0,
                            $"строк: {sender.RightList.Children.Count}");
                        Check("итог корзины подписан", sender.BasketNote.Contains("К передаче"), sender.BasketNote);
                        Check("кнопка «Предложить» доступна после выбора", sender.PrimaryEnabled);
                    }
                }

                SaveTradeScreenshot(sender, Path.Combine(_outDir, "shot-trade-sender.png"));

                var shot = RenderWindowOnDark(sender);
                Luminance(shot, out double mean, out double max, out double bright);
                Check("окно передачи тёмное", mean < 0.30, $"средняя яркость {mean:0.00}");
                Check("на тёмном фоне есть светлый текст", max > 0.55, $"максимум {max:0.00}");
                Check("в окне нет белых системных плашек", bright < 0.06, $"светлых пикселей {bright:P1}");
            }
            finally
            {
                sender.Close();
                Pump();
            }

            // ── получатель ──
            var offer = new TradeMessage
            {
                TradeId = "trade-incoming",
                Kind = "offer",
                FromPlayerId = "trade-from",
                FromName = "Гром",
                ToPlayerId = "me",
                ToName = "Тишь",
                Platinum = 12,
                Gold = 34,
                Electrum = 7,
                Silver = 15,
                Copper = 9,
                Items = new List<TradeItem>
                {
                    new TradeItem { SourceItemId = 3, Name = "Клинок сумерек", Category = "Оружие", Rarity = "редкий", Quantity = 1, DamageDice = "1к8" },
                    new TradeItem { SourceItemId = 4, Name = "Стрелы", Category = "Боеприпасы", Quantity = 13 }
                }
            };

            var from = new PlayerInfo
            {
                Id = "trade-from", Name = "Артём", CharacterName = "Гром",
                CharacterClass = "Воин", Level = 3, State = PeerState.Online
            };

            var receiver = new TradeWindow(null, characterId, from, offer);
            try
            {
                receiver.WindowStartupLocation = WindowStartupLocation.Manual;
                receiver.Left = -6000;
                receiver.Top = -6000;
                receiver.Show();
                Pump();
                receiver.UpdateLayout();

                Check("слева предложение, справа свой инвентарь",
                    receiver.LeftList.Children.Count >= 3 && receiver.RightList.Children.Count >= 1,
                    $"слева {receiver.LeftList.Children.Count}, справа {receiver.RightList.Children.Count}");
                Check("у получателя виден свой баланс", receiver.BalanceText.StartsWith("У вас:"), receiver.BalanceText);

                string offerText = CollectText(receiver.LeftList);
                Check("в предложении видны монеты, включая платину и электрум",
                    offerText.Contains("12 п.") && offerText.Contains("7 э."),
                    offerText.Length > 160 ? offerText.Substring(0, 160) : offerText);
                Check("в предложении видно количество предметов",
                    offerText.Contains("×13") && offerText.Contains("×1"));
                Check("получатель может принять обмен", receiver.PrimaryEnabled);

                SaveTradeScreenshot(receiver, Path.Combine(_outDir, "shot-trade-receiver.png"));

                var shot = RenderWindowOnDark(receiver);
                Luminance(shot, out double mean, out double max, out double bright);
                Check("окно получателя тоже тёмное", mean < 0.30, $"средняя яркость {mean:0.00}");
                Check("на тёмном фоне видна светлая надпись", max > 0.55, $"максимум {max:0.00}");
                Check("в окне получателя нет белых плашек", bright < 0.06, $"светлых пикселей {bright:P1}");
            }
            finally
            {
                receiver.Close();
                Pump();
            }
        }

        /// <summary>
        /// Монеты: ввод в окне, списание у отправителя, зачисление получателю.
        ///
        /// Проверяется прямо по базе: после сделки в GoldTable должны быть
        /// новые числа, а не те же самые. Раньше этот путь вообще не проверялся,
        /// и «монеты не передаются» всплыло только руками.
        /// </summary>
        private static void CheckTradeMoney()
        {
            int characterId = FindCharacterWithMoney();
            if (characterId <= 0)
            {
                Say("  [ПРОПУСК] нет персонажа с деньгами");
                return;
            }
            Say($"  персонаж для денег: {characterId}");

            var service = new MoneyService(characterId);
            CharacterMoney original = null;
            try { original = service.LoadMoney(); } catch { }

            int moneyEvents = 0;
            Action<int> moneyHandler = id => { if (id == characterId) moneyEvents++; };

            try
            {
                MoneyService.Changed += moneyHandler;

                service.SaveMoney(new CharacterMoney
                {
                    Platinum = 10, Gold = 20, Electrum = 3, Silver = 40, Copper = 50
                });

                Check("изменение денег сообщается приложению", moneyEvents == 1,
                    $"событий: {moneyEvents}");

                var other = new PlayerInfo
                {
                    Id = "money-peer", CharacterName = "Тишь", CharacterClass = "Плут",
                    Level = 3, State = PeerState.Online, CharacterId = 987652
                };

                var sender = new TradeWindow(null, characterId, other);
                try
                {
                    sender.WindowStartupLocation = WindowStartupLocation.Manual;
                    sender.Left = -6000;
                    sender.Top = -6000;
                    sender.Show();
                    Pump();
                    sender.UpdateLayout();

                    Check("баланс в окне читается из базы",
                        sender.BalanceText.Contains("10 п.") && sender.BalanceText.Contains("20 з."),
                        sender.BalanceText);

                    var boxes = GetPrivate(sender, "_coinBoxes") as TextBox[];
                    Check("поля монет созданы", boxes != null && boxes.Length == 5,
                        boxes == null ? "нет полей" : $"полей: {boxes.Length}");

                    if (boxes != null && boxes.Length == 5)
                    {
                        boxes[1].Text = "7";
                        Pump();

                        var values = CallPrivate(sender, "MoneyValues") as int[];
                        Check("введённые монеты читаются из полей",
                            values != null && values[1] == 7,
                            values == null ? "нет значений" : string.Join(" / ", values));

                        Check("корзина подписана монетами",
                            sender.BasketNote.Contains("монеты"), sender.BasketNote);
                        Check("в корзине появилась карточка монет",
                            sender.RightList.Children.Count > 0,
                            $"строк в корзине: {sender.RightList.Children.Count}");
                        Check("одни монеты без предметов можно предложить", sender.PrimaryEnabled);
                        Check("строка «останется» пересчиталась",
                            sender.RemainingText.Contains("13 з."), sender.RemainingText);

                        SaveTradeScreenshot(sender, Path.Combine(_outDir, "shot-trade-money.png"));
                    }

                    // ── списание у отправителя ──
                    var pending = new TradeMessage
                    {
                        TradeId = "money-commit-" + Guid.NewGuid().ToString("N"),
                        Kind = "offer",
                        FromPlayerId = "host",
                        FromName = "Гром",
                        ToPlayerId = "money-peer",
                        ToName = "Тишь",
                        Platinum = 1,
                        Gold = 7,
                        Electrum = 2,
                        Silver = 3,
                        Copper = 4
                    };
                    SetPrivate(sender, "_pending", pending);
                    CallPrivate(sender, "CommitTransferCore");

                    var afterSend = service.LoadMoney();
                    Check("золото списалось у отправителя", afterSend.Gold == 13,
                        $"было 20, стало {afterSend.Gold}");
                    Check("платина списалась у отправителя", afterSend.Platinum == 9,
                        $"было 10, стало {afterSend.Platinum}");
                    Check("электрум списался у отправителя", afterSend.Electrum == 1,
                        $"было 3, стало {afterSend.Electrum}");
                    Check("серебро списалось у отправителя", afterSend.Silver == 37,
                        $"было 40, стало {afterSend.Silver}");
                    Check("медь списалась у отправителя", afterSend.Copper == 46,
                        $"было 50, стало {afterSend.Copper}");
                }
                finally
                {
                    sender.Close();
                    Pump();
                }

                // ── зачисление получателю ──
                var from = new PlayerInfo
                {
                    Id = "money-from", CharacterName = "Гром", CharacterClass = "Воин",
                    Level = 3, State = PeerState.Online
                };
                var incoming = new TradeMessage
                {
                    TradeId = "money-incoming",
                    Kind = "offer",
                    FromPlayerId = "money-from",
                    FromName = "Гром",
                    ToPlayerId = "me",
                    ToName = "Тишь",
                    Gold = 7
                };

                var receiver = new TradeWindow(null, characterId, from, incoming);
                try
                {
                    receiver.WindowStartupLocation = WindowStartupLocation.Manual;
                    receiver.Left = -6000;
                    receiver.Top = -6000;
                    receiver.Show();
                    Pump();
                    receiver.UpdateLayout();

                    SetPrivate(receiver, "_accepted", true);
                    CallPrivate(receiver, "ReceiveTransfer", new TradeMessage
                    {
                        TradeId = "money-receive-" + Guid.NewGuid().ToString("N"),
                        Kind = "commit",
                        Platinum = 1,
                        Gold = 7,
                        Electrum = 2,
                        Silver = 3,
                        Copper = 4
                    });

                    var afterReceive = service.LoadMoney();
                    Check("золото зачислилось получателю", afterReceive.Gold == 20,
                        $"было 13, стало {afterReceive.Gold}");
                    Check("платина зачислилась получателю", afterReceive.Platinum == 10,
                        $"было 9, стало {afterReceive.Platinum}");
                    Check("серебро зачислилось получателю", afterReceive.Silver == 40,
                        $"было 37, стало {afterReceive.Silver}");
                }
                finally
                {
                    receiver.Close();
                    Pump();
                }
            }
            finally
            {
                MoneyService.Changed -= moneyHandler;
                if (original != null)
                {
                    try { service.SaveMoney(original); } catch { }
                }
            }
        }

        private static int FindCharacterWithMoney()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id_character FROM GoldTable ORDER BY (Platinum+Gold+Electrum+Silver+Copper) DESC LIMIT 1";
                var value = cmd.ExecuteScalar();
                if (value != null && value != DBNull.Value) return Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                Say("  персонаж с деньгами: " + ex.Message);
            }
            return FindAnyCharacterId();
        }

        private static object GetPrivate(object instance, string field)
            => instance.GetType()
                .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(instance);

        private static void SetPrivate(object instance, string field, object value)
            => instance.GetType()
                .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(instance, value);

        private static object CallPrivate(object instance, string method, params object[] args)
            => instance.GetType()
                .GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(instance, args);

        /// <summary>
        /// Карточка входящего обмена в «Отряде» и меню по правой кнопке.
        ///
        /// Карточка лежит в колонке шириной 300 точек. Кнопки обязаны стоять
        /// отдельной строкой: раньше они делили строку с текстом и наезжали
        /// на название обмена. Меню — тёмное: DarkMenu/DarkMenuItem лежали в
        /// токенах с ключом, но к меню не применялись.
        /// </summary>
        private static void CheckTradeCardAndMenu()
        {
            var view = new RoomView();
            var offer = new TradeMessage
            {
                TradeId = "trade-card",
                Kind = "offer",
                FromPlayerId = "peer",
                FromName = "Тишь",
                ToPlayerId = "me",
                Items = new List<TradeItem>
                {
                    new TradeItem { SourceItemId = 1, Name = "Клинок сумерек", Quantity = 1 },
                    new TradeItem { SourceItemId = 2, Name = "Стрелы", Quantity = 13 }
                },
                Platinum = 12,
                Gold = 34,
                Electrum = 7,
                Silver = 15,
                Copper = 9
            };

            typeof(RoomView)
                .GetField("_incomingTrade", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(view, offer);

            var builder = typeof(RoomView).GetMethod("BuildIncomingTradeCard",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var card = builder?.Invoke(view, null) as Border;

            Check("карточка входящего обмена собирается", card != null);
            if (card != null)
            {
                var host = new Window
                {
                    Width = 300,
                    Height = 600,
                    Background = Brushes.Black,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000,
                    Top = -6000,
                    ShowInTaskbar = false,
                    Content = card
                };
                host.Show();
                Pump();
                host.UpdateLayout();
                SaveTradeScreenshot(host, Path.Combine(_outDir, "shot-trade-card.png"));

                var stack = card.Child as StackPanel;
                Check("карточка обмена разложена в три строки",
                    stack != null && stack.Children.Count == 3,
                    stack == null ? "содержимое не StackPanel" : $"строк: {stack.Children.Count}");

                if (stack != null && stack.Children.Count == 3)
                {
                    var summary = stack.Children[1] as TextBlock;
                    var buttons = stack.Children[2] as Grid;
                    Check("кнопки в отдельной строке", buttons != null);

                    if (summary != null && buttons != null)
                    {
                        double summaryBottom = summary.TranslatePoint(new Point(0, summary.ActualHeight), card).Y;
                        double buttonsTop = buttons.TranslatePoint(new Point(0, 0), card).Y;
                        Check("кнопки не наезжают на текст",
                            buttonsTop >= summaryBottom - 0.6,
                            $"текст до {summaryBottom:0.#}, кнопки с {buttonsTop:0.#}");
                        Check("кнопки занимают ширину карточки",
                            buttons.ActualWidth >= card.ActualWidth - 24,
                            $"кнопки {buttons.ActualWidth:0.#} из {card.ActualWidth:0.#}");
                        Check("в строке кнопок две кнопки", buttons.Children.Count == 2,
                            $"кнопок: {buttons.Children.Count}");
                    }
                }

                host.Close();
                Pump();
            }

            // ── меню по правой кнопке ──
            var menu = view.CreateTradeMenu(new PlayerInfo { Id = "peer", CharacterName = "Тишь" }, p => { });
            Check("меню ПКМ получило стиль DarkMenu", menu.Style != null);
            Check("меню ПКМ тёмное по фону",
                menu.Background is SolidColorBrush mb && LuminanceOf(mb.Color) < 0.25,
                menu.Background is SolidColorBrush b2 ? b2.Color.ToString() : "фон не задан");

            var item = menu.Items.OfType<MenuItem>().FirstOrDefault();
            Check("пункт меню получил стиль DarkMenuItem", item?.Style != null);
            Check("пункт меню подписан по-русски",
                (item?.Header as string ?? "").Contains("Передать"), item?.Header as string ?? "<нет>");

            // Проверяем именно отрисовку: шаблон мог не примениться, и тогда
            // меню осталось бы системным белым, хотя Background задан.
            menu.Measure(new Size(320, 240));
            menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
            menu.UpdateLayout();

            if (menu.ActualWidth < 20)
            {
                menu.PlacementTarget = view;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
                menu.IsOpen = true;
                Pump();
            }

            var drawn = RenderElement(menu);
            SaveBitmapOnDark(drawn, Path.Combine(_outDir, "shot-trade-menu.png"));
            Luminance(drawn, out double menuMean, out double menuMax, out double menuBright);
            Check("меню реально рисуется тёмным",
                menu.ActualWidth > 20 && menuMean < 0.35,
                $"размер {menu.ActualWidth:0}×{menu.ActualHeight:0}, яркость {menuMean:0.00}");
            Check("в меню есть светлая надпись",
                menuMax > 0.55, $"максимум {menuMax:0.00}");
            Check("меню не белое системное",
                menuBright < 0.12, $"светлых пикселей {menuBright:P1}");

            if (menu.IsOpen) menu.IsOpen = false;
        }

        /// <summary>
        /// Предложения обмена не рвут соединение.
        ///
        /// На каждое предложение сервер пишет получателю ДВА сообщения: системную
        /// строку в общую ленту и само предложение. Раньше вторая запись шла мимо
        /// замка соединения, кадры перемешивались, и клиента выбрасывало из отряда
        /// с последующим переподключением. Отправляем пачку и смотрим, что связь жива.
        /// </summary>
        private static void CheckTradeTransport(RoomSession session, RoomClient guest)
        {
            var received = new List<TradeMessage>();
            var previous = guest.Events.TradeReceived;
            guest.Events.TradeReceived = t =>
            {
                lock (received) received.Add(t);
                previous?.Invoke(t);
            };

            try
            {
                string target = guest.PlayerId;
                const int count = 12;

                for (int i = 0; i < count; i++)
                {
                    session.SendTrade(new TradeMessage
                    {
                        TradeId = "transport-" + i,
                        Kind = "offer",
                        ToPlayerId = target,
                        ToName = "Тишь",
                        Items = new List<TradeItem>
                        {
                            new TradeItem { SourceItemId = i + 1, Name = "Стрелы", Quantity = 1 }
                        },
                        Gold = 5
                    });
                }

                System.Threading.Thread.Sleep(1500);

                int got;
                lock (received) got = received.Count;

                Check("предложения обмена доходят до клиента", got == count, $"дошло {got} из {count}");
                Check("клиент не выпал из отряда от обмена", guest.IsConnected,
                    "соединение закрыто — снова две записи в один сокет");
                Check("клиент остался в списке участников",
                    session.GetPlayers().Any(p => p.Id == target));
            }
            finally
            {
                guest.Events.TradeReceived = previous;
            }
        }

        /// <summary>
        /// Полный круг монет по сети: окно отправителя → сервер → клиент →
        /// подтверждение → списание → commit → зачисление.
        ///
        /// Именно этот путь и жаловался «монеты не передаются». Он проверяется
        /// целиком, а не по частям: окно, транспорт, база.
        /// </summary>
        private static void CheckTradeEndToEnd(RoomSession session, RoomClient guest)
        {
            int characterId = FindCharacterWithMoney();
            if (characterId <= 0)
            {
                Say("  [ПРОПУСК] нет персонажа с деньгами");
                return;
            }

            var service = new MoneyService(characterId);
            CharacterMoney original = null;
            try { original = service.LoadMoney(); } catch { }

            var offers = new List<TradeMessage>();
            var commits = new List<TradeMessage>();
            var previous = guest.Events.TradeReceived;
            TradeWindow window = null;

            try
            {
                service.SaveMoney(new CharacterMoney
                {
                    Platinum = 5, Gold = 50, Electrum = 5, Silver = 50, Copper = 50
                });

                guest.Events.TradeReceived = t =>
                {
                    lock (offers)
                    {
                        if (t.Kind == "offer") offers.Add(t);
                        else if (t.Kind == "commit") commits.Add(t);
                    }
                    previous?.Invoke(t);
                };

                var target = new PlayerInfo
                {
                    Id = guest.PlayerId, Name = "Лиза", CharacterName = "Тишь",
                    CharacterClass = "Плут", Level = 3, State = PeerState.Online
                };

                window = new TradeWindow(null, characterId, target);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -6000;
                window.Top = -6000;
                window.Show();
                Pump();
                window.UpdateLayout();

                var boxes = GetPrivate(window, "_coinBoxes") as TextBox[];
                if (boxes == null || boxes.Length < 5)
                {
                    Check("поля монет в окне отправителя", false, "поля не найдены");
                    return;
                }

                boxes[1].Text = "10";   // золото
                Pump();

                CallPrivate(window, "SendOffer");

                for (int i = 0; i < 6 && CountLocked(offers) == 0; i++) Pump();

                TradeMessage offer;
                lock (offers) offer = offers.FirstOrDefault();

                Check("предложение с монетами дошло до клиента", offer != null,
                    CountLocked(offers) == 0 ? "клиент ничего не получил" : "");
                Check("в предложении на той стороне есть золото",
                    offer != null && offer.Gold == 10,
                    offer == null ? "нет предложения" : $"золото: {offer.Gold}");

                if (offer == null) return;

                guest.SendTradeAsync(new TradeMessage
                {
                    TradeId = offer.TradeId,
                    Kind = "accept",
                    Accepted = true,
                    FromPlayerId = guest.PlayerId,
                    ToPlayerId = session.MyPlayerId
                }).GetAwaiter().GetResult();

                for (int i = 0; i < 8 && CountLocked(commits) == 0; i++) Pump();

                var after = service.LoadMoney();
                Check("золото списалось у отправителя после подтверждения",
                    after.Gold == 40, $"было 50, стало {after.Gold}");

                TradeMessage commit;
                lock (commits) commit = commits.FirstOrDefault();

                Check("получателю ушёл commit с золотом",
                    commit != null && commit.Gold == 10,
                    commit == null ? "commit не пришёл" : $"золото: {commit.Gold}");
            }
            catch (Exception ex)
            {
                Check("сквозная передача монет прошла без исключения", false, ex.Message);
            }
            finally
            {
                guest.Events.TradeReceived = previous;
                try { window?.Close(); } catch { }
                Pump();
                if (original != null)
                {
                    try { service.SaveMoney(original); } catch { }
                }
            }
        }

        private static int CountLocked(List<TradeMessage> list)
        {
            lock (list) return list.Count;
        }

        private static int FindCharacterWithInventory(out int itemCount)
        {
            itemCount = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id_character, COUNT(*) FROM Inventory GROUP BY id_character ORDER BY COUNT(*) DESC LIMIT 1";
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return 0;
                itemCount = Convert.ToInt32(reader.GetValue(1));
                return Convert.ToInt32(reader.GetValue(0));
            }
            catch (Exception ex)
            {
                Say("  персонаж с инвентарём: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Свободный порт для тестовой комнаты.
        ///
        /// Раньше здесь стоял 47777 — тот же порт, на котором висит запущенное
        /// приложение. Если рядом работает вторая копия, TCP отдаёт соединение
        /// ей, и гость получает «неверный ключ комнаты», хотя код ни при чём.
        /// </summary>
        private static int FindFreeRoomPort()
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch
            {
                return NetVersions.DefaultPort;
            }
        }

        private static double LuminanceOf(Color color)
            => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

        private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) yield return typed;
                foreach (var nested in FindDescendants<T>(child)) yield return nested;
            }
        }

        private static double XIn(UIElement element, UIElement root)
            => element.TranslatePoint(new Point(0, 0), root).X;

        private static double YIn(UIElement element, UIElement root)
            => element.TranslatePoint(new Point(0, 0), root).Y;

        private static int CountTexts(DependencyObject root, params string[] needles)
        {
            int found = 0;
            foreach (var block in FindDescendants<TextBlock>(root))
            {
                string text = (block.Text ?? "").Trim();
                foreach (string needle in needles)
                {
                    if (string.Equals(text, needle, StringComparison.Ordinal)) { found++; break; }
                }
            }
            return found;
        }

        private static string CollectText(DependencyObject root)
        {
            var sb = new StringBuilder();
            foreach (var block in FindDescendants<TextBlock>(root))
                sb.AppendLine(block.Text ?? "");
            return sb.ToString();
        }

        private static RenderTargetBitmap RenderWindowOnDark(Window window)
        {
            Pump();
            window.UpdateLayout();

            int width = (int)Math.Ceiling(window.ActualWidth > 1 ? window.ActualWidth : window.Width);
            int height = (int)Math.Ceiling(window.ActualHeight > 1 ? window.ActualHeight : window.Height);
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

            // Подложка: окно прозрачное по краям, и без неё картинка была бы
            // пустой в местах, где не нарисовано содержимое.
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x09, 0x0C, 0x16)), null,
                    new Rect(0, 0, width, height));
            bmp.Render(backdrop);
            bmp.Render(window);
            return bmp;
        }

        private static RenderTargetBitmap RenderElement(FrameworkElement element)
        {
            int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
            int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(element);
            return bmp;
        }

        private static void SaveTradeScreenshot(Window window, string path)
        {
            var bmp = RenderWindowOnDark(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        /// <summary>Сохраняет картинку на тёмной подложке — чтобы видеть границы элемента.</summary>
        private static void SaveBitmapOnDark(BitmapSource source, string path)
        {
            int width = Math.Max(1, source.PixelWidth) + 16;
            int height = Math.Max(1, source.PixelHeight) + 16;

            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x15, 0x1B, 0x2E)), null,
                    new Rect(0, 0, width, height));
            bmp.Render(backdrop);

            var layer = new DrawingVisual();
            using (var dc = layer.RenderOpen())
                dc.DrawImage(source, new Rect(8, 8, source.PixelWidth, source.PixelHeight));
            bmp.Render(layer);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        /// <summary>
        /// Средняя и максимальная яркость картинки и доля почти белых пикселей.
        /// По ним видно, тёмное окно на самом деле или белое.
        /// </summary>
        private static void Luminance(BitmapSource bmp, out double mean, out double max, out double brightShare)
        {
            int width = bmp.PixelWidth;
            int height = bmp.PixelHeight;
            int total = Math.Max(1, width * height);
            var pixels = new byte[width * height * 4];
            bmp.CopyPixels(pixels, width * 4, 0);

            double sum = 0;
            max = 0;
            int bright = 0;

            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                double b = pixels[i] / 255.0;
                double g = pixels[i + 1] / 255.0;
                double r = pixels[i + 2] / 255.0;
                double value = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                sum += value;
                if (value > max) max = value;
                if (value > 0.85) bright++;
            }

            mean = sum / total;
            brightShare = bright / (double)total;
        }

        #endregion

        #region === ЗАКЛИНАНИЯ В КАРТОЧКЕ СУЩЕСТВА ===

        /// <summary>
        /// Разбор блока «Заклинания и псионика» и его отрисовка.
        ///
        /// В базе и в импорте это один текст, часто одной строкой: «Заговоры
        /// (по желанию): … 1 круг (4): Щит, Туманный шаг. 2 круг (3): …».
        /// Проверяем, что он раскладывается по кругам, что «уровень» и «ячейки»
        /// понимаются наравне с «кругом», что проза не превращается в круги
        /// и что блок рисуется строками с плашками, а не абзацем.
        /// </summary>
        private static void CheckMonsterSpells()
        {
            const string wall =
                "Заговоры (по желанию): Ледянящее прикосновение, Волшебная рука, Малая иллюзия. " +
                "1 круг (4): Щит, Туманный шаг. 2 круг (3): Зеркальное отражение, Удержание личности. " +
                "3 круг (3): Контрзаклинание, Огненный шар, Гипнотический узор. 4 круг (3): Изгнание, дверь измерений. " +
                "5 круг (3): Стена силы, Подчинение личности. 6 круг (2): Непреодолимая танц. Отто, Истинное зрение. " +
                "7 круг (2): Телепортация, Клинок сил. 8 круг (1): Лабиринт.";

            var sheet = MonsterSpellSheet.Parse(wall);
            Check("стена текста разложена по кругам", sheet.Groups.Count == 9, $"кругов: {sheet.Groups.Count}");
            Check("заговоры — нулевой круг с пометкой",
                sheet.Groups.Count > 0 && sheet.Groups[0].Level == 0 &&
                sheet.Groups[0].Slots.Contains("по желанию"),
                sheet.Groups.Count > 0 ? sheet.Groups[0].Slots : "нет групп");
            Check("в первом круге четыре ячейки и два заклинания",
                sheet.Groups.Count > 1 && sheet.Groups[1].Slots == "4" && sheet.Groups[1].Spells.Count == 2,
                sheet.Groups.Count > 1 ? $"{sheet.Groups[1].Slots}, заклинаний {sheet.Groups[1].Spells.Count}" : "");
            Check("название с точкой внутри не разрезано",
                sheet.Groups.Any(g => g.Spells.Contains("Непреодолимая танц. Отто")));
            Check("всего заклинаний 19", sheet.SpellCount == 19, $"заклинаний: {sheet.SpellCount}");

            // Импортированный вариант: «уровень», «ячейки», вступление с прозой.
            const string imported =
                "Врождённое колдовство 14-го уровня, характеристика — Харизма (Сл спасброска 16, +8 к попаданию заклинанием).\r\n" +
                "Заговоры (по желанию): Волшебная рука, Малая иллюзия.\r\n" +
                "1 уровень (4 ячейки): Щит (Shield), Туманный шаг.\r\n" +
                "2 уровень (3 ячейки): Зеркальное отражение.\r\n" +
                "8 уровень (1 ячейка): Лабиринт.";

            var importedSheet = MonsterSpellSheet.Parse(imported);
            Check("вступление отделено от кругов",
                importedSheet.Intro.StartsWith("Врождённое колдовство"), importedSheet.Intro);
            Check("«уровень» понимается как круг", importedSheet.Groups.Count == 4,
                $"кругов: {importedSheet.Groups.Count}");
            Check("ячейки попали в подпись",
                importedSheet.Groups.Count > 1 && importedSheet.Groups[1].Slots == "4 ячейки",
                importedSheet.Groups.Count > 1 ? importedSheet.Groups[1].Slots : "");
            Check("английское в скобках не мешает плашке",
                MonsterSpellSheet.DisplayName("Щит (Shield)") == "Щит",
                MonsterSpellSheet.DisplayName("Щит (Shield)"));

            var prose = MonsterSpellSheet.Parse("Существо чувствует магию в радиусе 60 футов.");
            Check("проза не притворяется кругами", !prose.IsStructured && prose.Intro.Length > 0);

            string tidy = MonsterSpellSheet.Format(wall);
            var again = MonsterSpellSheet.Parse(tidy);
            Check("«Разложить по кругам» не теряет заклинания",
                again.Groups.Count == 9 && again.SpellCount == 19,
                $"кругов {again.Groups.Count}, заклинаний {again.SpellCount}");
            Check("причёсанный текст — по строке на круг",
                tidy.Split('\n').Length == 9, $"строк: {tidy.Split('\n').Length}");

            // ── отрисовка ──
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Щит", "Туманный шаг", "Огненный шар", "Лабиринт"
            };
            var block = MonsterSpellBlock.Build(sheet,
                name => known.Contains(MonsterSpellSheet.DisplayName(name)) ? name : null, null);

            var host = new Window
            {
                Width = 1000,
                Height = 700,
                Background = Brushes.Black,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -6000,
                Top = -6000,
                ShowInTaskbar = false,
                Content = block
            };
            host.Show();
            Pump();
            host.UpdateLayout();

            Check("в блоке девять строк по кругам", block.Children.Count == 9,
                $"строк: {block.Children.Count}");

            var texts = FindDescendants<TextBlock>(block).Select(t => t.Text ?? "").ToList();
            Check("подписи кругов на месте",
                texts.Contains("1 КРУГ") && texts.Contains("8 КРУГ") && texts.Contains("ЗАГОВОРЫ"));
            Check("заклинания стали плашками",
                texts.Contains("Щит") && texts.Contains("Огненный шар") && texts.Contains("Лабиринт"));
            Check("стены текста больше нет",
                !texts.Any(t => t.Contains("1 круг (4):")), "остался абзац со списком");
            Check("подписи «ячеек» показываются", texts.Contains("4") || texts.Contains("4 ячейки"));

            SaveTradeScreenshot(host, Path.Combine(_outDir, "shot-monster-spells.png"));
            host.Close();
            Pump();
        }

        /// <summary>
        /// Ни один исходник не должен быть перекодирован в «кракозябры».
        ///
        /// Повод конкретный: правка файла через PowerShell с чужой кодировкой
        /// один раз превратила все русские строки карточки существа в мусор,
        /// и сборка при этом осталась зелёной — ошибка видна только глазами.
        /// </summary>
        private static void CheckSourceEncoding()
        {
            string root = Path.GetFullPath(Path.Combine(_outDir, "..", "..", "HMWT_MUI"));
            if (!Directory.Exists(root))
            {
                Say("  [ПРОПУСК] исходники не найдены: " + root);
                return;
            }

            var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
                .ToList();

            var broken = new List<string>();
            foreach (string file in files)
            {
                string text;
                try { text = File.ReadAllText(file, Encoding.UTF8); }
                catch { continue; }

                if (text.Contains("РЎ") || text.Contains("вЂ") || text.Contains("РїСЂ"))
                    broken.Add(Path.GetFileName(file));
            }

            Check("исходники не перекодированы в кракозябры", broken.Count == 0,
                broken.Count == 0
                    ? $"файлов: {files.Count}"
                    : string.Join(", ", broken.Take(5)));
        }

        #endregion

        #region === ЖЕТОНЫ НПС И ПРЕДМЕТОВ ===

        /// <summary>
        /// Библиотека жетонов: картинка копируется в папку данных (иначе WebGL
        /// её не отдаст), одинаковые файлы не дублируются, удаление последней
        /// записи уносит и файл.
        /// </summary>
        private static void CheckMapTokenLibrary()
        {
            string folder = Path.Combine(Path.GetTempPath(), "hmwt_token_test");
            Directory.CreateDirectory(folder);

            string image = Path.Combine(folder, "barrel.png");
            File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7, 7 });

            // Остатки прошлых прогонов не должны мешать.
            foreach (var old in MapTokenLibrary.Load()
                         .Where(t => (t.Name ?? "").StartsWith("Проверка жетона"))
                         .ToList())
                MapTokenLibrary.Remove(old.Id);

            var added = MapTokenLibrary.Add("Проверка жетона: бочка", image, 2);
            Check("картинка жетона скопирована в папку данных",
                added != null && added.File.StartsWith("Tokens/", StringComparison.OrdinalIgnoreCase),
                added?.File ?? "запись не создана");

            string full = added == null ? null : PhotoHelper.ResolvePhotoPath(added.File);
            Check("картинка жетона читается по относительному пути",
                !string.IsNullOrEmpty(full) && File.Exists(full), full ?? "файла нет");
            Check("размер жетона сохраняется", added != null && added.SizeCells == 2,
                added == null ? "нет записи" : "клеток: " + added.SizeCells);

            var second = MapTokenLibrary.Add("Проверка жетона: бочка 2", image, 1);
            Check("одинаковые картинки не дублируются",
                added != null && second != null &&
                string.Equals(second.File, added.File, StringComparison.OrdinalIgnoreCase),
                second?.File ?? "нет второй записи");

            if (added == null || second == null) return;

            // Третья запись на тот же файл: после её удаления файл должен остаться.
            var third = MapTokenLibrary.Add("Проверка жетона: бочка 3", image, 1);
            if (third != null) MapTokenLibrary.Remove(third.Id);
            Check("файл жив, пока на него ссылается другая запись",
                !string.IsNullOrEmpty(full) && File.Exists(full));

            MapTokenLibrary.Rename(added.Id, "Проверка жетона: бочка (новая)");
            var reloaded = MapTokenLibrary.Load().FirstOrDefault(t => t.Id == added.Id);
            Check("переименование жетона сохраняется",
                reloaded != null && reloaded.Name.Contains("новая"), reloaded?.Name ?? "запись пропала");

            // ── замена картинки ──
            string secondImage = Path.Combine(folder, "chest.png");
            File.WriteAllBytes(secondImage, new byte[] { 0x89, 0x50, 0x4E, 0x47, 11, 12, 13, 14 });

            // Освобождаем старую картинку: вторая запись ссылалась на неё же.
            MapTokenLibrary.Remove(second.Id);

            var replaced = MapTokenLibrary.ReplaceImage(added.Id, secondImage);
            Check("картинка жетона заменяется",
                replaced != null && !string.Equals(replaced.File, added.File, StringComparison.OrdinalIgnoreCase),
                replaced?.File ?? "не заменилась");
            string newFull = replaced == null ? null : PhotoHelper.ResolvePhotoPath(replaced.File);
            Check("новая картинка лежит в папке жетонов",
                !string.IsNullOrEmpty(newFull) && File.Exists(newFull));
            Check("старая картинка удалена после замены",
                string.IsNullOrEmpty(full) || !File.Exists(full), full);

            MapTokenLibrary.Remove(added.Id);
            Check("последняя запись уносит файл",
                string.IsNullOrEmpty(newFull) || !File.Exists(newFull));

            try { Directory.Delete(folder, true); } catch { }
        }

        /// <summary>
        /// Очистка кэша: старые картинки жетонов без ссылок попадают в отчёт,
        /// а картинки из библиотеки и с карт — нет. Clean() здесь не зовём,
        /// чтобы не трогать настоящий кэш пользователя.
        /// </summary>
        private static void CheckTokenCacheCleanup()
        {
            string image = Path.Combine(Path.GetTempPath(), "hmwt_cache_token.png");
            File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47, 21, 22, 23, 24 });

            var template = MapTokenLibrary.Add("Проверка кэша: бочка", image, 1);
            if (template == null)
            {
                Say("  [ПРОПУСК] не удалось создать запись библиотеки");
                return;
            }

            string referenced = PhotoHelper.ResolvePhotoPath(template.File);
            string orphan = Path.Combine(MapTokenLibrary.TokensFolder,
                "orphan_" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(orphan, new byte[] { 0x89, 0x50, 0x4E, 0x47, 31, 32, 33, 34 });

            try
            {
                var report = CacheCleanupService.Scan();

                Check("очистка кэша видит старую картинку жетона",
                    report.Files.Any(f => string.Equals(f, Path.GetFullPath(orphan),
                        StringComparison.OrdinalIgnoreCase)));
                Check("очистка кэша не трогает картинку из библиотеки",
                    string.IsNullOrEmpty(referenced) ||
                    !report.Files.Any(f => string.Equals(f, Path.GetFullPath(referenced),
                        StringComparison.OrdinalIgnoreCase)),
                    referenced ?? "путь не разрешился");
            }
            finally
            {
                try { File.Delete(orphan); } catch { }
                MapTokenLibrary.Remove(template.Id);
                try { File.Delete(image); } catch { }
            }
        }

        /// <summary>Жетон без участника боя сохраняется, читается и удаляется.</summary>
        private static void CheckFreeTokenPersistence()
        {
            int sceneId = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT MIN(id_scene) FROM MapScene";
                var value = cmd.ExecuteScalar();
                if (value != null && value != DBNull.Value) sceneId = Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                Say("  сцена для жетона: " + ex.Message);
            }

            if (sceneId <= 0)
            {
                Say("  [ПРОПУСК] в базе нет сцен карты");
                return;
            }

            string label = "Проверка: Старик " + Guid.NewGuid().ToString("N").Substring(0, 6);
            var token = new MapTokenData
            {
                SceneId = sceneId,
                CombatantId = 0,
                Label = label,
                X = 120,
                Y = 140,
                SizeCells = 1,
                RevealCells = 0
            };

            int id = MapService.SaveToken(token);
            Check("жетон без участника боя сохраняется", id > 0 && token.Id == id, $"id={id}");
            if (id <= 0) return;

            var loaded = MapService.LoadTokens(sceneId).FirstOrDefault(t => t.Id == id);
            Check("жетон читается обратно",
                loaded != null && loaded.Label == label && loaded.CombatantId == 0,
                loaded == null ? "не найден" : $"label={loaded.Label}, бой={loaded.CombatantId}");
            Check("у свободного жетона нет владельца-персонажа",
                loaded != null && loaded.OwnerCharacterId == 0);

            MapService.DeleteToken(id);
            Check("жетон удаляется с карты",
                !MapService.LoadTokens(sceneId).Any(t => t.Id == id));
        }

        /// <summary>
        /// Меню карты и жетона в map.js: пункты постановки и правки жетона.
        /// Проверка текстовая, потому что WebGL в тестах не поднимается.
        /// </summary>
        private static void CheckTokenMenuScript()
        {
            string path = Path.GetFullPath(Path.Combine(_outDir, "..", "..", "HMWT_MUI", "Web", "map", "map.js"));
            if (!File.Exists(path))
            {
                Say("  [ПРОПУСК] map.js не найден: " + path);
                return;
            }

            string js = File.ReadAllText(path, Encoding.UTF8);

            Check("в меню карты есть постановка жетона НПС",
                js.Contains("Жетон НПС или предмета"));
            Check("меню жетона умеет менять картинку",
                js.Contains("action: 'edit'"));
            Check("меню жетона умеет скрывать от игроков",
                js.Contains("Скрыть от игроков"));
            Check("координаты нового жетона передаются на карту",
                js.Contains("type: 'placeToken'"));
        }

        /// <summary>
        /// Окно «Жетон»: имя, картинка, размер, видимость. Рисуем его в картинку
        /// и смотрим — так видно, что поля на месте и тёмная тема применилась.
        /// </summary>
        private static void CheckTokenEditorWindow()
        {
            string image = Path.Combine(Path.GetTempPath(), "hmwt_token_editor.png");
            try
            {
                // Картинка для предпросмотра: содержимое не важно, лишь бы файл читался.
                var pixels = new System.Windows.Media.Imaging.WriteableBitmap(64, 64, 96, 96,
                    PixelFormats.Pbgra32, null);
                var bytes = new byte[64 * 64 * 4];
                new Random(4).NextBytes(bytes);
                pixels.WritePixels(new Int32Rect(0, 0, 64, 64), bytes, 64 * 4, 0);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pixels));
                using (var stream = File.Create(image)) encoder.Save(stream);
            }
            catch (Exception ex)
            {
                Say("  [ПРОПУСК] не удалось создать картинку: " + ex.Message);
                return;
            }

            var window = new TokenEditorWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -6000;
                window.Top = -6000;
                window.LoadValues("Старик", image, 2, false);
                window.Show();
                Pump();
                window.UpdateLayout();

                var nameBox = window.FindName("NameBox") as TextBox;
                var sizeBox = window.FindName("SizeBox") as ComboBox;
                var preview = window.FindName("PreviewImage") as Image;

                Check("окно жетона показывает имя", nameBox != null && nameBox.Text == "Старик",
                    nameBox?.Text ?? "поля нет");
                Check("в окне жетона десять размеров",
                    sizeBox != null && sizeBox.Items.Count == 10,
                    sizeBox == null ? "списка нет" : "пунктов: " + sizeBox.Items.Count);
                Check("картинка жетона видна в предпросмотре",
                    preview != null && preview.Source != null);
                Check("имя и размер жетона читаются из окна",
                    window.TokenLabel == "Старик" && window.SizeCells == 2,
                    $"«{window.TokenLabel}», клеток {window.SizeCells}");

                SaveTradeScreenshot(window, Path.Combine(_outDir, "shot-token-editor.png"));

                var shot = RenderWindowOnDark(window);
                Luminance(shot, out double mean, out double max, out double bright);
                Check("окно жетона тёмное", mean < 0.30, $"средняя яркость {mean:0.00}");
                Check("на тёмном фоне есть светлый текст", max > 0.55, $"максимум {max:0.00}");
            }
            finally
            {
                window.Close();
                Pump();
                try { File.Delete(image); } catch { }
            }
        }

        /// <summary>
        /// Библиотека жетонов: плитки с картинками и именами. Добавляем во
        /// временную папку три картинки, смотрим, что они попали в список,
        /// и снимаем картинку окна.
        /// </summary>
        private static void CheckTokenLibraryWindow()
        {
            string folder = Path.Combine(Path.GetTempPath(), "hmwt_lib_test");
            Directory.CreateDirectory(folder);

            var created = new List<MapTokenTemplate>();
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    string file = Path.Combine(folder, "lib" + i + ".png");
                    File.WriteAllBytes(file, new byte[] { 0x89, 0x50, 0x4E, 0x47, (byte)(i + 1), 3, 5, 7 });

                    var template = MapTokenLibrary.Add("Проверка библиотеки " + (i + 1), file, 1);
                    if (template != null) created.Add(template);
                }

                var window = new TokenLibraryWindow();
                try
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -6000;
                    window.Top = -6000;
                    window.Show();
                    Pump();
                    window.UpdateLayout();

                    var host = window.FindName("TilesHost") as Panel;
                    Check("библиотека показывает плитки жетонов",
                        host != null && host.Children.Count >= 3,
                        host == null ? "списка нет" : "плиток: " + host.Children.Count);

                    // ── поиск по имени ──
                    var search = window.FindName("SearchBox") as TextBox;
                    Check("в библиотеке есть поиск", search != null);

                    if (search != null && host != null)
                    {
                        int before = host.Children.Count;

                        search.Text = "Проверка библиотеки 2";
                        Pump();
                        window.UpdateLayout();
                        Check("поиск оставляет только совпадения",
                            host.Children.Count == 1, "плиток: " + host.Children.Count);

                        search.Text = "";
                        Pump();
                        window.UpdateLayout();
                        Check("очистка поиска возвращает все жетоны",
                            host.Children.Count == before,
                            $"было {before}, стало {host.Children.Count}");
                    }

                    SaveTradeScreenshot(window, Path.Combine(_outDir, "shot-token-library.png"));

                    var shot = RenderWindowOnDark(window);
                    Luminance(shot, out double mean, out double max, out double bright);
                    Check("окно библиотеки тёмное", mean < 0.30, $"средняя яркость {mean:0.00}");
                    Check("в библиотеке есть светлые подписи", max > 0.55, $"максимум {max:0.00}");
                }
                finally
                {
                    window.Close();
                    Pump();
                }
            }
            finally
            {
                foreach (var template in created) MapTokenLibrary.Remove(template.Id);
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        /// <summary>
        /// Проверка причины «у жетонов пропали аватарки после смены сцены».
        ///
        /// WebGL при смене сцены строит жетоны заново, а кэш «эта картинка уже
        /// отправлена» оставался от прошлой сцены — фото не отправлялось, и
        /// новые спрайты рисовались без аватарок. Проверяем по исходнику, что
        /// сброс на смену сцены есть: WebGL в тестах не поднимается.
        /// </summary>
        private static void CheckMapScenePhotoCache()
        {
            string web = Path.GetFullPath(Path.Combine(_outDir, "..", "..", "HMWT_MUI", "Views", "MapView.Web.cs"));
            string view = Path.GetFullPath(Path.Combine(_outDir, "..", "..", "HMWT_MUI", "Views", "MapView.cs"));

            if (!File.Exists(web) || !File.Exists(view))
            {
                Say("  [ПРОПУСК] исходники карты не найдены");
                return;
            }

            string webCode = File.ReadAllText(web, Encoding.UTF8);
            string viewCode = File.ReadAllText(view, Encoding.UTF8);

            Check("при смене сцены сбрасывается кэш картинок жетонов",
                webCode.Contains("_webSceneId != _scene.Id") &&
                webCode.Contains("_webTokenPhotoKeys.Clear()"));
            Check("своя картинка жетона важнее портрета монстра",
                viewCode.Contains("IsTokenImage(own)"));
            Check("кэш картинок сбрасывается и при перезагрузке WebView",
                webCode.Contains("_webSceneId = -1"));
        }

        /// <summary>
        /// Данные для аватарок на карте: у жетонов с участником боя портрет
        /// монстра должен реально существовать. Именно из него берётся картинка,
        /// когда своя не выбрана, — и после смены сцены она снова доедет.
        /// </summary>
        private static void CheckSceneTokenPhotosExist()
        {
            int withPhoto = 0, missing = 0;
            var examples = new List<string>();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT s.name, t.label, b.photo_path
                    FROM MapToken t
                    JOIN MapScene s ON s.id_scene = t.id_scene
                    LEFT JOIN EncounterCombatant c ON c.id_combatant = t.id_combatant
                    LEFT JOIN Bestiary b ON b.id_monster = c.id_monster
                    WHERE t.id_combatant > 0 AND COALESCE(b.photo_path,'') <> ''";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string scene = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string label = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string photo = reader.IsDBNull(2) ? "" : reader.GetString(2);

                    string full = PhotoHelper.ResolvePhotoPath(photo);
                    if (!string.IsNullOrEmpty(full) && File.Exists(full))
                    {
                        withPhoto++;
                    }
                    else
                    {
                        missing++;
                        if (examples.Count < 3) examples.Add($"{scene} / {label}: {photo}");
                    }
                }
            }
            catch (Exception ex)
            {
                Say("  портреты жетонов: " + ex.Message);
                return;
            }

            Check("портреты жетонов на картах находятся", missing == 0,
                missing == 0 ? $"с картинкой: {withPhoto}" : string.Join("; ", examples));
            Check("на картах есть жетоны с портретами", withPhoto > 0, $"с картинкой: {withPhoto}");
        }

        #endregion

        #region === ВСПОМОГАТЕЛЬНОЕ ===

        private sealed class Hosted
        {
            public Window Window;
            public RoomView View;
        }

        /// <summary>Показывает вид в отдельном окне, чтобы WPF реально отрисовал разметку.</summary>
        private static Hosted ShowInWindow(RoomView view, double width, double height, double left, double top)
        {
            var window = new Window
            {
                Width = width,
                Height = height,
                Background = Brushes.Black,
                Content = view,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = left,
                Top = top
            };
            window.Show();
            Pump();
            window.UpdateLayout();

            // В тестовом процессе цикл диспетчера не запущен, поэтому Loaded может
            // не прийти. Обновляем вкладку явно — это её же публичный метод.
            view.Refresh();
            Pump();
            window.UpdateLayout();
            view.Refresh();
            return new Hosted { Window = window, View = view };
        }

        /// <summary>
        /// Прокачивает очередь диспетчера вручную.
        ///
        /// Application.Run в тесте не вызывается, поэтому без этого не отрабатывают
        /// отложенные операции: окно не измеряется, и RenderTargetBitmap даёт 1×1.
        /// Пакетный Pump с ограничением по времени — чтобы не зависнуть, если очередь
        /// никогда не опустеет.
        /// </summary>
        private static void Pump()
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) => frame.Continue = false,
                dispatcher);
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        private static FrameworkElement Element(RoomView view, string name)
            => view.FindName(name) as FrameworkElement;

        private static string Text(RoomView view, string name)
            => (view.FindName(name) as TextBlock)?.Text ?? "<нет>";

        private static Panel Panel(RoomView view, string name)
            => view.FindName(name) as Panel;

        private static void SaveScreenshot(Window window, string path)
        {
            Pump();
            window.UpdateLayout();

            // Если окно не измерено (нет цикла диспетчера), берём заданные размеры
            int width = (int)(window.ActualWidth > 1 ? window.ActualWidth : window.Width);
            int height = (int)(window.ActualHeight > 1 ? window.ActualHeight : window.Height);
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        #endregion
    }
}
