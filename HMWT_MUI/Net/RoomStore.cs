using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMWT_MUI.Net
{
    /// <summary>Метаданные постоянной комнаты (room.json).</summary>
    public sealed class RoomManifest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
        [JsonPropertyName("lastPlayedAt")] public DateTime LastPlayedAt { get; set; } = DateTime.Now;

        /// <summary>Ключ, зашифрованный DPAPI. Открытым текстом не храним.</summary>
        [JsonPropertyName("keyProtected")] public string KeyProtected { get; set; } = "";

        /// <summary>Кто мастер: идентификатор участника.</summary>
        [JsonPropertyName("gmId")] public string GmId { get; set; } = "";

        [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
        [JsonPropertyName("dataFingerprint")] public string DataFingerprint { get; set; } = "";
    }

    /// <summary>Краткая карточка комнаты для списка «продолжить игру».</summary>
    public sealed class RoomSummary
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime LastPlayedAt { get; set; }
        public int EntryCount { get; set; }
        public int PlayerCount { get; set; }
    }

    /// <summary>
    /// Постоянное хранилище комнат в %APPDATA%\HMWT\Rooms\&lt;id&gt;\.
    ///
    /// Ленты (чат, броски) пишутся в формате JSONL — по строке на запись.
    /// Это осознанно: при внезапном завершении процесса потеряется максимум
    /// последняя строка, тогда как обычный JSON-массив потерял бы весь файл.
    /// </summary>
    public static class RoomStore
    {
        private const string ManifestFile = "room.json";
        private const string PlayersFile = "players.json";
        private const string ChatFile = "chat.jsonl";
        private const string RollsFile = "rolls.jsonl";
        private const string DiaryFile = "diary.md";

        /// <summary>Корень хранилища комнат.</summary>
        public static string RoomsRoot
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HMWT", "Rooms");
                Directory.CreateDirectory(root);
                return root;
            }
        }

        public static string GetRoomDir(string roomId)
        {
            // Защита от подстановки пути: идентификатор комнаты приходит извне
            var safe = new string((roomId ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
            if (string.IsNullOrEmpty(safe)) safe = "room";
            return Path.Combine(RoomsRoot, safe);
        }

        // ══════════════════════ МЕТАДАННЫЕ ══════════════════════

        /// <summary>Создаёт комнату: папку, метаданные и ключ (ключ шифруется DPAPI).</summary>
        public static RoomManifest CreateRoom(string name, string roomKey, string dataFingerprint)
        {
            // Идентификатор подбираем ДО записи в манифест: если поменять Id после
            // сохранения, манифест и папка разойдутся, и данные комнаты «потеряются»
            string baseId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string id = baseId;
            int suffix = 1;
            while (Directory.Exists(GetRoomDir(id)))
                id = baseId + "-" + suffix++;

            var manifest = new RoomManifest
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? "Новая комната" : name.Trim(),
                CreatedAt = DateTime.Now,
                LastPlayedAt = DateTime.Now,
                KeyProtected = RoomKey.Protect(roomKey),
                AppVersion = NetVersions.AppVersion,
                ProtocolVersion = NetVersions.ProtocolVersion,
                DataFingerprint = dataFingerprint ?? ""
            };

            var dir = GetRoomDir(manifest.Id);
            Directory.CreateDirectory(dir);
            SaveManifest(manifest);
            SavePlayers(dir, new List<PlayerInfo>());
            return manifest;
        }

        public static void SaveManifest(RoomManifest manifest)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.Id)) return;
            try
            {
                var dir = GetRoomDir(manifest.Id);
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(dir, ManifestFile), json, Encoding.UTF8);
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomStore] SaveManifest: {ex.Message}"); }
        }

        public static RoomManifest LoadManifest(string roomId)
        {
            try
            {
                var file = Path.Combine(GetRoomDir(roomId), ManifestFile);
                if (!File.Exists(file)) return null;
                return JsonSerializer.Deserialize<RoomManifest>(File.ReadAllText(file, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomStore] LoadManifest: {ex.Message}");
                return null;
            }
        }

        /// <summary>Список сохранённых комнат, свежие — сверху. Для экрана «продолжить игру».</summary>
        public static List<RoomSummary> ListRooms()
        {
            var result = new List<RoomSummary>();
            try
            {
                foreach (var dir in Directory.GetDirectories(RoomsRoot))
                {
                    var manifest = LoadManifest(Path.GetFileName(dir));
                    if (manifest == null) continue;

                    result.Add(new RoomSummary
                    {
                        Id = manifest.Id,
                        Name = manifest.Name,
                        CreatedAt = manifest.CreatedAt,
                        LastPlayedAt = manifest.LastPlayedAt,
                        EntryCount = CountLines(Path.Combine(dir, ChatFile)) + CountLines(Path.Combine(dir, RollsFile)),
                        PlayerCount = LoadPlayers(manifest.Id).Count
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomStore] ListRooms: {ex.Message}"); }
            return result.OrderByDescending(r => r.LastPlayedAt).ToList();
        }

        public static void DeleteRoom(string roomId)
        {
            try
            {
                var dir = GetRoomDir(roomId);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomStore] DeleteRoom: {ex.Message}"); }
        }

        // ══════════════════════ УЧАСТНИКИ ══════════════════════

        /// <summary>Ключ расшифровывается на этом компьютере; на чужом вернётся пустая строка.</summary>
        public static string GetRoomKey(RoomManifest manifest)
            => manifest == null ? "" : RoomKey.Unprotect(manifest.KeyProtected);

        public static void SavePlayers(string roomDir, List<PlayerInfo> players)
        {
            try
            {
                Directory.CreateDirectory(roomDir);
                var json = JsonSerializer.Serialize(players ?? new List<PlayerInfo>(),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(roomDir, PlayersFile), json, Encoding.UTF8);
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomStore] SavePlayers: {ex.Message}"); }
        }

        public static List<PlayerInfo> LoadPlayers(string roomId)
        {
            try
            {
                var file = Path.Combine(GetRoomDir(roomId), PlayersFile);
                if (!File.Exists(file)) return new List<PlayerInfo>();
                return JsonSerializer.Deserialize<List<PlayerInfo>>(File.ReadAllText(file, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<PlayerInfo>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomStore] LoadPlayers: {ex.Message}");
                return new List<PlayerInfo>();
            }
        }

        // ══════════════════════ ЛЕНТА (JSONL) ══════════════════════

        /// <summary>Дописывает запись в ленту. Одна строка — одно событие.</summary>
        public static void AppendFeed(string roomId, FeedEntry entry)
        {
            if (entry == null) return;
            try
            {
                var dir = GetRoomDir(roomId);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, entry.Kind == FeedKind.Roll ? RollsFile : ChatFile);
                var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
                File.AppendAllText(file, line, Encoding.UTF8);

                // История не должна расти вечно: держим последнюю тысячу записей.
                // Чистим, когда файл заметно переполнился, иначе перезапись
                // JSONL с картинками была бы слишком дорогой на каждом сообщении.
                TrimHistory(dir, 1000, 1100);
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomStore] AppendFeed: {ex.Message}"); }
        }

        /// <summary>Оставляет в истории последние <paramref name="keep"/> записей.</summary>
        private static void TrimHistory(string roomDir, int keep, int trimWhenAbove)
        {
            try
            {
                string chatFile = Path.Combine(roomDir, ChatFile);
                string rollsFile = Path.Combine(roomDir, RollsFile);
                int total = CountLines(chatFile) + CountLines(rollsFile);
                if (total <= trimWhenAbove) return;

                var all = new List<FeedEntry>();
                all.AddRange(ReadJsonl(chatFile));
                all.AddRange(ReadJsonl(rollsFile));
                if (all.Count <= keep) return;

                var latest = all.OrderBy(e => e.At).Skip(all.Count - keep).ToList();
                File.WriteAllLines(chatFile,
                    latest.Where(e => e.Kind != FeedKind.Roll).Select(e => JsonSerializer.Serialize(e)),
                    Encoding.UTF8);
                File.WriteAllLines(rollsFile,
                    latest.Where(e => e.Kind == FeedKind.Roll).Select(e => JsonSerializer.Serialize(e)),
                    Encoding.UTF8);
                Debug.WriteLine($"[RoomStore] история обрезана до {keep}: было {all.Count}");
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomStore] TrimHistory: {ex.Message}"); }
        }

        /// <summary>Читает последние записи ленты (для восстановления сессии).</summary>
        public static List<FeedEntry> LoadFeed(string roomId, int maxEntries = NetLimits.MaxChatHistory)
        {
            var result = new List<FeedEntry>();
            try
            {
                var dir = GetRoomDir(roomId);
                result.AddRange(ReadJsonl(Path.Combine(dir, ChatFile)));
                result.AddRange(ReadJsonl(Path.Combine(dir, RollsFile)));
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomStore] LoadFeed: {ex.Message}"); }

            var ordered = result.OrderBy(e => e.At).ToList();
            int take = Math.Max(1, maxEntries);
            if (ordered.Count > take)
                ordered = ordered.Skip(ordered.Count - take).ToList();
            return ordered;
        }

        private static string LegacyImageId(FeedEntry entry)
        {
            try
            {
                string seed = string.Join("|",
                    entry.At.Ticks.ToString(),
                    entry.AuthorId ?? "",
                    entry.Text ?? "",
                    entry.ImageName ?? "",
                    entry.ImageExtension ?? "",
                    (entry.ImageBase64 ?? "").Length.ToString());
                using var sha = SHA256.Create();
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                var sb = new StringBuilder("legacy-");
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
            catch
            {
                return "legacy-" + Guid.NewGuid().ToString("N");
            }
        }

        private static List<FeedEntry> ReadJsonl(string file)
        {
            var list = new List<FeedEntry>();
            if (!File.Exists(file)) return list;

            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<FeedEntry>(line,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry != null)
                    {
                        // В старых комнатах у картинок ещё не было id. Без него
                        // клиент после переподключения не может попросить файл.
                        if (entry.Kind == FeedKind.Image && string.IsNullOrEmpty(entry.ImageId))
                            entry.ImageId = LegacyImageId(entry);
                        list.Add(entry);
                    }
                }
                catch
                {
                    // Битую строку пропускаем: из-за одной не стоит терять всю историю
                }
            }
            return list;
        }

        // ══════════════════════ ОБЩИЙ ДНЕВНИК ══════════════════════
        public static void SaveDiary(string roomId, string text)
        {
            try
            {
                var dir = GetRoomDir(roomId);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, DiaryFile), text ?? "", Encoding.UTF8);
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomStore] SaveDiary: {ex.Message}"); }
        }

        private static int CountLines(string file)
        {
            try
            {
                if (!File.Exists(file)) return 0;
                return File.ReadLines(file).Count(l => !string.IsNullOrWhiteSpace(l));
            }
            catch { return 0; }
        }
    }
}
