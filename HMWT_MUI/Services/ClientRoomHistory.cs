using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using HMWT_MUI.Helpers;
using HMWT_MUI.Net;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Одна сохранённая комната, к которой игрок подключался.
    ///
    /// У ведущего комнаты лежат в RoomStore целиком (с историей), а гостю
    /// достаточно адреса и ключа: чтобы не вписывать их каждый раз заново.
    /// </summary>
    public sealed class ClientRoomEntry
    {
        /// <summary>Ключ записи: адрес и порт. По нему узнаём повторную комнату.</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = "";

        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("host")] public string Host { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("key")] public string KeyProtected { get; set; } = "";
        [JsonPropertyName("at")] public DateTime LastJoined { get; set; } = DateTime.Now;

        [JsonIgnore] public string Address => Host + ":" + Port;

        /// <summary>Как запись показывается в списке. Ключ не показываем.</summary>
        public override string ToString()
            => string.IsNullOrWhiteSpace(Name) ? Address : Name + " — " + Address;
    }

    /// <summary>
    /// История комнат, к которым подключался игрок.
    ///
    /// Ключ хранится зашифрованным через DPAPI текущего пользователя — как и
    /// ключ комнаты у ведущего. Файл небольшой, лежит рядом с остальными
    /// данными приложения.
    /// </summary>
    public static class ClientRoomHistory
    {
        private const int MaxEntries = 12;
        private static readonly object FileLock = new object();

        private static string FilePath
            => Path.Combine(PhotoHelper.GetWritableRoot(), "client-rooms.json");

        /// <summary>Сохранённые комнаты, свежие сверху.</summary>
        public static List<ClientRoomEntry> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<ClientRoomEntry>();

                string json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<ClientRoomEntry>>(json)
                           ?? new List<ClientRoomEntry>();

                return list
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Host) && e.Port > 0)
                    .OrderByDescending(e => e.LastJoined)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Комнаты] история: {ex.Message}");
                return new List<ClientRoomEntry>();
            }
        }

        /// <summary>
        /// Запоминает удачное подключение. Одинаковый адрес не дублируется,
        /// у записи обновляются имя комнаты, ключ и время.
        /// </summary>
        public static void Remember(string host, int port, string key, string roomName)
        {
            host = (host ?? "").Trim();
            if (host.Length == 0 || port <= 0) return;

            try
            {
                lock (FileLock)
                {
                    var list = Load();
                    string id = (host.ToLowerInvariant() + ":" + port);
                    var existing = list.FirstOrDefault(e =>
                        string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

                    if (existing == null)
                    {
                        existing = new ClientRoomEntry { Id = id, Host = host, Port = port };
                        list.Add(existing);
                    }

                    existing.Host = host;
                    existing.Port = port;
                    existing.LastJoined = DateTime.Now;
                    if (!string.IsNullOrWhiteSpace(roomName)) existing.Name = roomName.Trim();
                    existing.KeyProtected = RoomKey.Protect(key);

                    list = list.OrderByDescending(e => e.LastJoined).Take(MaxEntries).ToList();
                    Save(list);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Комнаты] запомнить: {ex.Message}");
            }
        }

        /// <summary>Убирает запись из истории.</summary>
        public static void Forget(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;

            try
            {
                lock (FileLock)
                {
                    var list = Load();
                    list.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                    Save(list);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Комнаты] забыть: {ex.Message}");
            }
        }

        /// <summary>Расшифровывает ключ записи. Пусто — ключ не читается.</summary>
        public static string GetKey(ClientRoomEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.KeyProtected)) return "";
            return RoomKey.Unprotect(entry.KeyProtected);
        }

        private static void Save(List<ClientRoomEntry> list)
        {
            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}
