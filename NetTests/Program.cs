using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HMWT_MUI.Net;

namespace HMWT_MUI.NetTests
{
    /// <summary>
    /// Проверка комнаты на реальном сетевом обмене: хост и клиенты в ОДНОМ процессе,
    /// но общаются честно по TCP на 127.0.0.1. Так удобно ловить ошибки протокола
    /// без запуска нескольких окон приложения.
    ///
    /// Запуск: HMWT_MUI.NetTests.exe [роль] [порт]
    ///   (без аргументов) — полный сценарий: хост + мастер + игрок + гость
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Report = new List<string>();
        private static int _failures;

        private static void Say(string text)
        {
            Report.Add(text);
            Console.WriteLine(text);
        }

        private static void Check(string label, bool condition, string detail = "")
        {
            if (!condition) _failures++;
            Say($"  [{(condition ? "OK  " : "ПРОВАЛ")}] {label}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                RunAll().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Say("ИСКЛЮЧЕНИЕ: " + ex);
                _failures++;
            }

            Say("");
            Say(_failures == 0 ? "ИТОГ: всё прошло" : $"ИТОГ: провалов {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static async Task RunAll()
        {
            const int port = 47911;
            var key = RoomKey.Generate();

            Say("═══ ПОДГОТОВКА ═══");
            var manifest = RoomStore.CreateRoom("Тестовая сессия", key, "");
            Say($"  комната {manifest.Id}, ключ {key}");

            // Ленту очищаем, чтобы проверки не зависели от прошлых запусков
            RoomStore.SaveDiary(manifest.Id, "");

            var server = new RoomServer(manifest.Id, key, port, "");
            var serverFeed = new List<FeedEntry>();
            var serverNotices = new List<string>();
            server.Events.FeedAdded = e => { lock (serverFeed) serverFeed.Add(e); };
            server.Events.SystemNotice = (t, id) => { lock (serverNotices) serverNotices.Add(t); };

            Say("");
            Say("═══ ЗАПУСК ХОСТА ═══");
            if (!server.Start(out string startError))
            {
                Check("сервер запустился", false, startError);
                return;
            }
            Check("сервер запустился", true, $"порт {port}");

            // ─────────────── подключение трёх участников ───────────────
            Say("");
            Say("═══ ПОДКЛЮЧЕНИЕ УЧАСТНИКОВ ═══");

            var gm = await ConnectAsync(port, key, manifest.Id, new PlayerInfo
            {
                Name = "Артём", CharacterName = "Гром", CharacterClass = "Воин",
                CharacterRace = "Полуорк", Level = 3, Hp = 24, MaxHp = 30, ArmorClass = 16, CharacterId = 1
            }, "мастер");

            var player = await ConnectAsync(port, key, manifest.Id, new PlayerInfo
            {
                Name = "Лиза", CharacterName = "Тишь", CharacterClass = "Плут",
                CharacterRace = "Полурослик", Level = 3, Hp = 18, MaxHp = 18, ArmorClass = 14, CharacterId = 2
            }, "игрок");

            var guest = await ConnectAsync(port, key, manifest.Id, new PlayerInfo
            {
                Name = "Гость", CharacterName = "Зритель", CharacterClass = "Бард",
                CharacterRace = "Эльф", Level = 1, Hp = 9, MaxHp = 9, ArmorClass = 12, CharacterId = 3
            }, "гость");

            if (gm == null || player == null || guest == null) { server.Stop(); return; }

            await Task.Delay(700);
            var players = server.GetPlayers();
            Check("хост видит трёх участников", players.Count == 3, $"видит {players.Count}");
            Check("первый вошедший стал мастером",
                players.FirstOrDefault(p => p.Id == gm.PlayerId)?.Role == RoomRole.GameMaster);
            Check("остальные — игроки",
                players.Where(p => p.Id != gm.PlayerId).All(p => p.Role == RoomRole.Player));
            Check("имена с кириллицей дошли без искажений",
                players.Any(p => p.CharacterName == "Гром" && p.CharacterClass == "Воин"));
            Check("характеристики перенеслись",
                players.First(p => p.CharacterName == "Гром").Hp == 24 &&
                players.First(p => p.CharacterName == "Гром").MaxHp == 30);

            // ─────────────── чат ───────────────
            Say("");
            Say("═══ ЧАТ ═══");
            gm.Chat.Clear(); player.Chat.Clear(); guest.Chat.Clear();
            await gm.Client.SendChatAsync("Мы входим в подземелье, держим строй.");
            await Task.Delay(600);
            await player.Client.SendChatAsync("Принято, иду следом.");
            await Task.Delay(800);

            Check("все три участника получили обе реплики",
                gm.Chat.Count == 2 && player.Chat.Count == 2 && guest.Chat.Count == 2,
                $"мастер {gm.Chat.Count}, игрок {player.Chat.Count}, гость {guest.Chat.Count}");
            Check("у реплик правильный автор",
                gm.Chat.Any(c => c.AuthorName == "Тишь"),
                string.Join(" | ", gm.Chat.Select(c => c.AuthorName + ": " + c.Text)));

            // ─────────────── броски ───────────────
            Say("");
            Say("═══ БРОСКИ ═══");
            gm.Chat.Clear(); player.Chat.Clear(); guest.Chat.Clear();

            await gm.Client.SendRollAsync("1d20+5", "Проверка Восприятия", 22, new List<int> { 17 });
            await Task.Delay(600);
            await player.Client.SendRollAsync("2d6+3", "Урон кинжалом", 18, new List<int> { 12, 6 });
            await Task.Delay(800);

            Check("обычные броски видны всем",
                gm.Chat.Count == 2 && player.Chat.Count == 2 && guest.Chat.Count == 2,
                $"мастер {gm.Chat.Count}, игрок {player.Chat.Count}, гость {guest.Chat.Count}");
            Check("формула и итог дошли целиком",
                gm.Chat.Any(r => r.Formula == "2d6+3" && r.Total == 18),
                string.Join(" | ", gm.Chat.Select(r => r.Formula + "=" + r.Total)));
            Check("отдельные кубы сохранены",
                gm.Chat.Any(r => r.IndividualRolls != null && r.IndividualRolls.SequenceEqual(new[] { 12, 6 })));

            // ─────────────── скрытый бросок мастера ───────────────
            Say("");
            Say("═══ СКРЫТЫЙ БРОСОК МАСТЕРА ═══");
            gm.Chat.Clear(); player.Chat.Clear(); guest.Chat.Clear();

            await gm.Client.SendRollAsync("1d20+2", "Тайная проверка", 21, new List<int> { 19 }, hidden: true);
            await Task.Delay(900);

            Check("мастер видит свой скрытый бросок", gm.Chat.Count == 1, $"получено {gm.Chat.Count}");
            Check("игрок НЕ получает скрытый бросок", player.Chat.Count == 0, $"получено {player.Chat.Count}");
            Check("гость НЕ получает скрытый бросок", guest.Chat.Count == 0, $"получено {guest.Chat.Count}");
            Check("в ленте хоста бросок помечен скрытым",
                serverFeed.Any(f => f.Hidden && f.Formula == "1d20+2"));

            // ─────────────── игрок пытается подделать скрытый бросок ───────────────
            Say("");
            Say("═══ ПОПЫТКА ИГРОКА ОТПРАВИТЬ СКРЫТЫЙ БРОСОК ═══");
            gm.Chat.Clear(); player.Chat.Clear(); guest.Chat.Clear();

            await player.Client.SendRollAsync("1d20", "Якобы тайный", 20, new List<int> { 20 }, hidden: true);
            await Task.Delay(900);

            Check("попытка игрока не стала скрытой — видят все",
                gm.Chat.Count == 1 && player.Chat.Count == 1 && guest.Chat.Count == 1,
                $"мастер {gm.Chat.Count}, игрок {player.Chat.Count}, гость {guest.Chat.Count}");
            Check("хост снял флаг скрытности с броска игрока",
                serverFeed.Where(f => f.Formula == "1d20").All(f => !f.Hidden));

            // ─────────────── снимок состояния ───────────────
            Say("");
            Say("═══ СНИМОК СОСТОЯНИЯ ═══");
            await server.BroadcastSnapshotAsync();
            await Task.Delay(500);

            Check("клиенты получили снимки", gm.Snapshots > 0 && player.Snapshots > 0);
            Check("в снимке есть лента", gm.LastSnapshot != null && gm.LastSnapshot.Feed.Count > 0,
                $"записей {gm.LastSnapshot?.Feed.Count}");
            Check("версия протокола в снимке совпадает",
                gm.LastSnapshot?.ProtocolVersion == NetVersions.ProtocolVersion);

            // ─────────────── передача роли мастера ───────────────
            Say("");
            Say("═══ ПЕРЕДАЧА РОЛИ МАСТЕРА ═══");
            bool transferred = server.TransferGameMaster(player.PlayerId);
            await Task.Delay(600);
            Check("роль передана", transferred);
            Check("новый мастер назначен",
                server.IsGameMaster(player.PlayerId) && !server.IsGameMaster(gm.PlayerId));

            // теперь уже игрок имеет право на скрытый бросок
            gm.Chat.Clear(); player.Chat.Clear(); guest.Chat.Clear();
            await player.Client.SendRollAsync("1d20+1", "Тайная проверка нового мастера", 15, new List<int> { 14 }, hidden: true);
            await Task.Delay(900);
            Check("новый мастер может бросать скрыто", player.Chat.Count == 1, $"получено {player.Chat.Count}");
            Check("гость по-прежнему не видит скрытое", guest.Chat.Count == 0, $"получено {guest.Chat.Count}");

            // ─────────────── отключение и уведомление ───────────────
            Say("");
            Say("═══ ОТКЛЮЧЕНИЕ УЧАСТНИКА ═══");
            guest.Client.Disconnect();
            await Task.Delay(1500);

            Check("хост убрал отключившегося", server.GetPlayers().Count == 2,
                $"осталось {server.GetPlayers().Count}");
            Check("ушло системное уведомление", serverNotices.Any(n => n.Contains("Зритель")),
                string.Join(" | ", serverNotices));
            Check("в ленте есть запись об отключении",
                serverFeed.Any(f => f.Kind == FeedKind.System && f.Text.Contains("Зритель")));

            // ─────────────── переподключение ───────────────
            Say("");
            Say("═══ ПЕРЕПОДКЛЮЧЕНИЕ ═══");
            var guest2 = await ConnectAsync(port, key, manifest.Id, new PlayerInfo
            {
                Name = "Гость", CharacterName = "Зритель", CharacterClass = "Бард",
                CharacterRace = "Эльф", Level = 1, Hp = 9, MaxHp = 9, ArmorClass = 12, CharacterId = 3
            }, "гость (повторно)");

            Check("повторное подключение прошло", guest2 != null);
            if (guest2 != null)
            {
                await Task.Delay(700);
                Check("участников снова трое", server.GetPlayers().Count == 3,
                    $"сейчас {server.GetPlayers().Count}");
                Check("история ленты доступна после переподключения",
                    guest2.LastSnapshot != null && guest2.LastSnapshot.Feed.Count > 0,
                    $"записей {guest2.LastSnapshot?.Feed.Count}");
            }

            // ─────────────── версия и ключ ───────────────
            Say("");
            Say("═══ ОТКАЗЫ: ВЕРСИЯ И КЛЮЧ ═══");
            var wrongKey = await ConnectAsync(port, RoomKey.Generate(), manifest.Id, new PlayerInfo { Name = "Чужак" }, "неверный ключ", expectFailure: true);
            Check("неверный ключ отклонён", wrongKey == null);

            // ─────────────── сохранение на диск ───────────────
            Say("");
            Say("═══ ИСТОРИЯ НА ДИСКЕ ═══");
            await server.BroadcastSnapshotAsync();
            var diskFeed = RoomStore.LoadFeed(manifest.Id, 500);
            Check("лента сохранена в файлы", diskFeed.Count > 0, $"записей {diskFeed.Count}");
            Check("скрытый бросок тоже сохранён (для мастера)",
                diskFeed.Any(f => f.Hidden));

            var reloaded = RoomStore.LoadManifest(manifest.Id);
            Check("комната читается заново", reloaded != null && reloaded.Id == manifest.Id);
            Check("ключ комнаты расшифровывается на этом компьютере",
                RoomStore.GetRoomKey(reloaded) == RoomKey.Normalize(key));

            // ─────────────── уборка ───────────────
            Say("");
            Say("═══ ОСТАНОВКА ═══");
            gm.Client.Disconnect();
            player.Client.Disconnect();
            guest2?.Client.Disconnect();
            await Task.Delay(500);
            server.Stop();
            Check("хост остановился и отпустил участников", server.GetPlayers().Count == 0,
                $"осталось {server.GetPlayers().Count}");

            RoomStore.DeleteRoom(manifest.Id);
            Say("  тестовая комната удалена");
        }

        private sealed class Peer
        {
            public RoomClient Client;
            public string PlayerId => Client?.PlayerId ?? "";
            public int Snapshots;
            public RoomSnapshot LastSnapshot;
            public readonly List<FeedEntry> Chat = new List<FeedEntry>();
            public readonly List<string> Log = new List<string>();
        }

        /// <summary>Подключает участника и навешивает безопасные (не UI) обработчики.</summary>
        private static async Task<Peer> ConnectAsync(int port, string key, string roomId,
            PlayerInfo me, string label, bool expectFailure = false)
        {
            var peer = new Peer();
            var client = new RoomClient("127.0.0.1", port, key, roomId, "");
            peer.Client = client;

            client.Events.SnapshotReceived = s => { peer.Snapshots++; peer.LastSnapshot = s; };
            client.Events.FeedReceived = e => { lock (peer.Chat) peer.Chat.Add(e); };
            client.Events.Log = t => { lock (peer.Log) peer.Log.Add(t); };
            client.Events.Failed = (code, text) =>
            {
                lock (peer.Log) peer.Log.Add($"ОТКАЗ [{code}] {text}");
            };

            bool ok = await client.ConnectAsync(me);

            if (expectFailure)
            {
                if (!ok) Say($"  {label}: отказ получен — {client.LastError}");
                return ok ? peer : null;
            }

            Check($"{label} подключился", ok, ok ? $"id {client.PlayerId}, роль {client.Role}" : client.LastError);
            return ok ? peer : null;
        }
    }
}
