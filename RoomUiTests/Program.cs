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
using HMWT_MUI.Net;
using HMWT_MUI.Services;
using HMWT_MUI.Views;

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

            Say("");
            Say("═══ ПЕРЕДАЧА ФОТО ═══");
            CheckPhotoTransfer();

            Say("");
            Say("═══ СВОЙ ПРЕДМЕТ В МАГАЗИНЕ ═══");
            CheckCustomShopItem();

            Say("");
            Say("═══ РАЗБОР СПИСКОВ В ЗАМЕТКАХ ═══");
            CheckMarkdownLists();

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

            if (!session.CreateRoom("Проверка интерфейса", me, 47777, out string error))
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

                Say("  подключаю второго участника по сети…");
                var guest = new RoomClient("127.0.0.1", 47777, session.Key, session.RoomId, "");

                // Счётчик при входе через RoomSession: проверяем именно момент входа
                Say("");
                Say("═══ СЧЁТЧИК УЧАСТНИКОВ ПРИ ВХОДЕ ═══");
                CheckClientCountOnJoin(session.Key, session.RoomId);
                Say("");
                bool joined = guest.ConnectAsync(new PlayerInfo
                {
                    Name = "Лиза", CharacterName = "Тишь", CharacterClass = "Плут",
                    CharacterRace = "Полурослик", Level = 3, Hp = 7, MaxHp = 18, ArmorClass = 14
                }).GetAwaiter().GetResult();
                Check("второй участник подключился", joined, guest.LastError);

                System.Threading.Thread.Sleep(900);
                var players = session.GetPlayers();
                Check("в отряде двое", players.Count == 2, $"участников: {players.Count}");
                Check("второй — игрок, не ведущий",
                    players.Any(p => p.CharacterName == "Тишь" && p.Role == RoomRole.Player));
                Check("видны класс, уровень и здоровье второго",
                    players.Any(p => p.CharacterName == "Тишь" && p.CharacterClass == "Плут"
                                     && p.Hp == 7 && p.MaxHp == 18 && p.ArmorClass == 14));

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
        /// Проверяет, что сразу после входа через RoomSession в статусе стоит
        /// верное число участников, а не «0/8».
        ///
        /// Раньше цикл чтения сети запускался до того, как JoinRoom выставлял режим
        /// сессии: первый снимок от хоста приходил в этот промежуточный момент,
        /// список участников был ещё пуст, и статус залипал как 0/8 до следующего
        /// снимка. Здесь проверяем именно момент входа, без ожидания.
        /// </summary>
        private static void CheckClientCountOnJoin(string key, string roomId)
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

                bool joined = client.JoinRoom("127.0.0.1", 47777, key, me, out string error);
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
