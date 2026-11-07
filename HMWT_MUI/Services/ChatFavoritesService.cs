using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMWT_MUI.Services
{
    /// <summary>Избранная гифка: файл в папке данных и исходное имя.</summary>
    public sealed class ChatFavorite
    {
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("added")] public string Added { get; set; } = "";
    }

    /// <summary>
    /// Избранные гифки для чата.
    ///
    /// Когда игрок отправляет гифку, её копия ложится в %APPDATA%\HMWT\ChatFavorites:
    /// отправить её снова можно одним кликом, не разыскивая файл в проводнике.
    /// Лимита нет — игрок удаляет ненужные сам, поэтому папку не чистим.
    /// </summary>
    public static class ChatFavoritesService
    {
        private static readonly object Gate = new object();

        public static string Folder => Path.Combine(PhotoHelper.GetWritableRoot(), "ChatFavorites");

        private static string ManifestPath => Path.Combine(Folder, "favorites.json");

        /// <summary>Кладёт копию отправленной гифки в избранное. Пустая строка — не вышло.</summary>
        public static string Save(string sourceFile, string displayName = null)
        {
            if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile)) return "";

            try
            {
                Directory.CreateDirectory(Folder);

                string ext = (Path.GetExtension(sourceFile) ?? ".gif").ToLowerInvariant();
                string hash = Hash(sourceFile);
                string name = hash + ext;
                string target = Path.Combine(Folder, name);

                if (!File.Exists(target)) File.Copy(sourceFile, target, false);

                lock (Gate)
                {
                    var list = Load();
                    string shown = string.IsNullOrWhiteSpace(displayName)
                        ? Path.GetFileName(sourceFile)
                        : displayName.Trim();

                    var existing = list.FirstOrDefault(f =>
                        string.Equals(f.File, name, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        list.Add(new ChatFavorite
                        {
                            File = name,
                            Name = shown,
                            Added = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                        SaveManifest(list);
                    }
                }

                return name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatFavorites] Save: {ex.Message}");
                return "";
            }
        }

        /// <summary>Список избранного, новые сверху.</summary>
        public static List<ChatFavorite> Load()
        {
            var result = new List<ChatFavorite>();

            try
            {
                if (!File.Exists(ManifestPath)) return result;

                string json = File.ReadAllText(ManifestPath);
                var parsed = JsonSerializer.Deserialize<List<ChatFavorite>>(json);
                if (parsed == null) return result;

                foreach (var item in parsed)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.File)) continue;
                    if (!File.Exists(Path.Combine(Folder, item.File))) continue;
                    result.Add(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatFavorites] Load: {ex.Message}");
            }

            return result.OrderByDescending(f => f.Added).ToList();
        }

        /// <summary>Убирает гифку из избранного вместе с копией файла.</summary>
        public static void Remove(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;

            lock (Gate)
            {
                var list = Load();
                list.RemoveAll(f => string.Equals(f.File, fileName, StringComparison.OrdinalIgnoreCase));
                SaveManifest(list);

                try
                {
                    string full = Path.Combine(Folder, fileName);
                    if (File.Exists(full)) File.Delete(full);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ChatFavorites] Remove: {ex.Message}");
                }
            }
        }

        /// <summary>Полный путь к избранной гифке.</summary>
        public static string Resolve(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            string full = Path.Combine(Folder, fileName);
            return File.Exists(full) ? full : null;
        }

        private static void SaveManifest(List<ChatFavorite> list)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(ManifestPath,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatFavorites] SaveManifest: {ex.Message}");
            }
        }

        private static string Hash(string path)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA1.Create();
                using var stream = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
