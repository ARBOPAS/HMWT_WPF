using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HMWT_MUI.Services
{
    /// <summary>Готовый жетон библиотеки: бочка, стол, «Старик» — что угодно с картинкой.</summary>
    public sealed class MapTokenTemplate
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>Путь к картинке относительно папки данных приложения.</summary>
        public string File { get; set; } = "";

        public int SizeCells { get; set; } = 1;
    }

    /// <summary>
    /// Папка картинок жетонов и библиотека готовых жетонов.
    ///
    /// Зачем отдельно от бестиария: НПС вроде «Старика» и предметы вроде бочки
    /// в трекере инициативы не нужны, а на карте — нужны. Раньше жетон можно
    /// было поставить только из боя, поэтому временные метки приходилось
    /// заводить через трекер.
    ///
    /// Картинки лежат под папкой данных приложения: WebView2 раздаёт файлы
    /// только из неё (https://hmwt.assets/...), иначе аватар жетона не увидит
    /// ни ведущий, ни игроки.
    /// </summary>
    public static class MapTokenLibrary
    {
        private static readonly object Gate = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly string[] AllowedExtensions =
        {
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
        };

        /// <summary>Папка с картинками жетонов (внутри корня данных приложения).</summary>
        public static string TokensFolder
        {
            get
            {
                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Tokens");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string ManifestPath => Path.Combine(TokensFolder, "tokens.json");

        /// <summary>Список готовых жетонов. Битый файл не роняет окно — просто пустой список.</summary>
        public static List<MapTokenTemplate> Load()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(ManifestPath)) return new List<MapTokenTemplate>();

                    string json = File.ReadAllText(ManifestPath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<MapTokenTemplate>();

                    var list = JsonSerializer.Deserialize<List<MapTokenTemplate>>(json, JsonOptions)
                               ?? new List<MapTokenTemplate>();

                    return list.Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id)).ToList();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MapTokenLibrary] чтение: {ex.Message}");
                    return new List<MapTokenTemplate>();
                }
            }
        }

        /// <summary>Добавляет жетон в библиотеку: копирует картинку и пишет запись.</summary>
        public static MapTokenTemplate Add(string name, string sourceImage, int sizeCells)
        {
            string relative = Import(sourceImage);
            if (string.IsNullOrEmpty(relative)) return null;

            var template = new MapTokenTemplate
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "Жетон" : name.Trim(),
                File = relative,
                SizeCells = Math.Max(1, Math.Min(10, sizeCells))
            };

            lock (Gate)
            {
                var list = Load();
                list.Add(template);
                Save(list);
            }

            return template;
        }

        public static void Rename(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return;

            lock (Gate)
            {
                var list = Load();
                var item = list.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
                if (item == null) return;

                item.Name = name.Trim();
                Save(list);
            }
        }

        /// <summary>
        /// Меняет картинку записи библиотеки. Старый файл удаляется, если на него
        /// больше никто не ссылается: иначе в Tokens копился бы мусор.
        /// </summary>
        public static MapTokenTemplate ReplaceImage(string id, string sourceImage)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;

            string relative = Import(sourceImage);
            if (string.IsNullOrEmpty(relative)) return null;

            lock (Gate)
            {
                var list = Load();
                var item = list.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
                if (item == null) return null;

                string oldFile = item.File;
                if (string.Equals(oldFile, relative, StringComparison.OrdinalIgnoreCase)) return item;

                item.File = relative;
                Save(list);

                bool used = list.Any(t => string.Equals(t.File, oldFile, StringComparison.OrdinalIgnoreCase));
                if (!used)
                {
                    try
                    {
                        string full = PhotoHelper.ResolvePhotoPath(oldFile);
                        if (!string.IsNullOrEmpty(full) && File.Exists(full)) File.Delete(full);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MapTokenLibrary] старый файл: {ex.Message}");
                    }
                }

                return item;
            }
        }

        public static void Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;

            lock (Gate)
            {
                var list = Load();
                var item = list.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
                if (item == null) return;

                list.Remove(item);
                Save(list);

                // Файл удаляем, только если на него больше никто не ссылается:
                // картинки дедуплицируются по хешу, и один файл может быть
                // у нескольких записей.
                bool used = list.Any(t => string.Equals(t.File, item.File, StringComparison.OrdinalIgnoreCase));
                if (used) return;

                try
                {
                    string full = PhotoHelper.ResolvePhotoPath(item.File);
                    if (!string.IsNullOrEmpty(full) && File.Exists(full)) File.Delete(full);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MapTokenLibrary] удаление файла: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Копирует картинку в папку данных (с дедупликацией по содержимому)
        /// и возвращает путь относительно неё: «Tokens/ab12….png».
        /// </summary>
        public static string Import(string sourceImage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourceImage) || !File.Exists(sourceImage)) return "";

                string extension = Path.GetExtension(sourceImage).ToLowerInvariant();
                if (!AllowedExtensions.Contains(extension))
                {
                    Debug.WriteLine($"[MapTokenLibrary] не картинка: {extension}");
                    return "";
                }

                string hash = HashFile(sourceImage);
                if (hash.Length == 0) return "";

                string fileName = hash + extension;
                string folder = TokensFolder;
                string target = Path.Combine(folder, fileName);

                if (!File.Exists(target)) File.Copy(sourceImage, target, true);

                return "Tokens/" + fileName;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapTokenLibrary] импорт: {ex.Message}");
                return "";
            }
        }

        /// <summary>Полный путь по записи библиотеки; null — файла нет.</summary>
        public static string Resolve(MapTokenTemplate template)
            => template == null ? null : PhotoHelper.ResolvePhotoPath(template.File);

        public static string HashFile(string path)
        {
            try
            {
                using var sha = SHA1.Create();
                using var stream = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static void Save(List<MapTokenTemplate> list)
        {
            try
            {
                Directory.CreateDirectory(TokensFolder);
                File.WriteAllText(ManifestPath,
                    JsonSerializer.Serialize(list ?? new List<MapTokenTemplate>(), JsonOptions),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapTokenLibrary] запись: {ex.Message}");
            }
        }
    }
}
