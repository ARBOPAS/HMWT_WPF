using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Пак бестиария — тот же .hmwtpack, что и обмен предметами, но с существами.
    ///
    /// Зачем: бестиарий в программе пустой, и наполнять его руками долго.
    /// Готовый набор существ удобно держать отдельным файлом (например на GitHub),
    /// а не тащить в установщик: кому нужно — импортирует, кому нет — не качает.
    ///
    /// Внутри zip: manifest.json (что за пак, откуда данные, лицензия),
    /// monsters.json (сами существа) и папка images с картинками.
    /// Манифест специально спрашивает источник и лицензию при выгрузке: по нему
    /// видно, откуда взяты данные, — это нужно и для себя, и для тех, кто скачает.
    ///
    /// Совместимость: читаем формат версии 1, лишние поля из будущих версий
    /// игнорируем, чтобы пак новой сборки не ломал старую программу.
    /// </summary>
    public static class BestiaryPackService
    {
        public const int FormatVersion = 1;

        public const string FileExtension = ".hmwtpack";

        private const string ManifestEntry = "manifest.json";
        private const string MonstersEntry = "monsters.json";
        private const string ImagesFolder = "images";

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        #region === МОДЕЛЬ ФАЙЛА ===

        public sealed class Manifest
        {
            /// <summary>Версия формата пака.</summary>
            public int Version { get; set; } = FormatVersion;

            /// <summary>Что за пак: bestiary. Оставлено на будущее для предметов и заклинаний.</summary>
            public string Kind { get; set; } = "bestiary";

            public string App { get; set; } = "HMWT Гримуар";
            public string AppVersion { get; set; } = "";
            public string CreatedAt { get; set; } = "";

            /// <summary>Название пака — его же предлагаем как имя файла.</summary>
            public string Name { get; set; } = "";

            public int Count { get; set; }

            /// <summary>Коды источников, которые встретились в паке (MM, VGtM, SRD…).</summary>
            public List<string> Sources { get; set; } = new List<string>();

            /// <summary>Откуда взяты данные: книги, перевод, ссылка. Заполняет автор пака.</summary>
            public string SourceNote { get; set; } = "";

            /// <summary>Лицензия, под которой автор выкладывает пак.</summary>
            public string License { get; set; } = "";

            /// <summary>Что изменено по сравнению с источником.</summary>
            public string Changed { get; set; } = "";

            public bool HasImages { get; set; }

            public string Program { get; set; } = "";
        }

        /// <summary>Существо в паке: сама запись и имя файла картинки внутри архива.</summary>
        public sealed class PackMonster
        {
            public MonsterData Monster { get; set; }
            public string PhotoFile { get; set; } = "";
        }

        #endregion

        #region === ЭКСПОРТ ===

        /// <summary>
        /// Сохраняет существ в пак. Возвращает false и текст ошибки, если не вышло.
        /// </summary>
        public static bool Export(string path, IList<MonsterData> monsters, string name,
            string sourceNote, out int images, out string error)
        {
            images = 0;
            error = "";

            try
            {
                if (string.IsNullOrWhiteSpace(path)) { error = "Не указан файл."; return false; }
                if (monsters == null || monsters.Count == 0) { error = "Бестиарий пуст — нечего выгружать."; return false; }

                var pack = new List<PackMonster>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string root = PhotoHelper.GetWritableRoot();

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(path)) File.Delete(path);

                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    int index = 0;
                    foreach (var monster in monsters)
                    {
                        if (monster == null) continue;
                        index++;

                        var entry = new PackMonster { Monster = monster, PhotoFile = "" };

                        // картинка: путь в базе относительный от папки данных
                        if (!string.IsNullOrWhiteSpace(monster.PhotoPath))
                        {
                            try
                            {
                                string full = Path.Combine(root, monster.PhotoPath.Replace('/', Path.DirectorySeparatorChar));
                                if (File.Exists(full))
                                {
                                    string extension = Path.GetExtension(full);
                                    string fileName = $"m{index:0000}{extension.ToLowerInvariant()}";
                                    while (usedNames.Contains(fileName)) fileName = $"m{index:0000}_{usedNames.Count}{extension.ToLowerInvariant()}";
                                    usedNames.Add(fileName);

                                    var imageEntry = zip.CreateEntry(ImagesFolder + "/" + fileName, CompressionLevel.Optimal);
                                    using (var target = imageEntry.Open())
                                    {
                                        // Картинки из книг весят по несколько мегабайт: пак на 550
                                        // существ выходил 145 МБ. Уменьшаем до 1024 точек по большей
                                        // стороне и пережимаем в JPEG — пак становится в разы легче.
                                        byte[] prepared = PrepareImage(full, 1024, 85);
                                        if (prepared != null) target.Write(prepared, 0, prepared.Length);
                                        else
                                        {
                                            using var source = File.OpenRead(full);
                                            source.CopyTo(target);
                                        }
                                    }

                                    entry.PhotoFile = ImagesFolder + "/" + fileName;
                                    images++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[BestiaryPack] картинка {monster.PhotoPath}: {ex.Message}");
                            }
                        }

                        pack.Add(entry);
                    }

                    var manifest = new Manifest
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "Monster" : name.Trim(),
                        Count = pack.Count,
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        AppVersion = Net.NetVersions.AppVersion,
                        Sources = monsters.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Source))
                                          .Select(m => m.Source.Trim())
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                                          .ToList(),
                        SourceNote = (sourceNote ?? "").Trim(),
                        License = "",
                        Changed = "переформатировано в бестиарий HMWT: убраны теги оформления, " +
                                  "скорость переведена из клеток в футы, черты и действия разложены по разделам",
                        HasImages = images > 0
                    };

                    WriteJson(zip, ManifestEntry, manifest);
                    WriteJson(zip, MonstersEntry, pack);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryPack] Export error: {ex.Message}");
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Готовит картинку для пака: уменьшает до maxSide по большей стороне
        /// и пережимает в JPEG. Возвращает null, если файл не картинка —
        /// тогда вызывающий положит его как есть.
        /// </summary>
        private static byte[] PrepareImage(string path, int maxSide, int quality)
        {
            try
            {
                var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);

                double scale = Math.Min(1.0, maxSide / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
                BitmapSource output = frame;
                if (scale < 0.999)
                {
                    output = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                }

                var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(BitmapFrame.Create(output));

                using var stream = new MemoryStream();
                encoder.Save(stream);
                return stream.ToArray();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryPack] сжатие картинки {path}: {ex.Message}");
                return null;
            }
        }
        private static void WriteJson<T>(ZipArchive zip, string entryName, T value)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(value, WriteOptions));
        }

        #endregion

        #region === ИМПОРТ ===

        /// <summary>
        /// Добавляет существ из пака. Совпадающие по названию пропускает,
        /// чтобы двойной импорт не плодил копии.
        /// </summary>
        public static bool Import(string path, out int added, out int skipped, out string error)
        {
            added = 0;
            skipped = 0;
            error = "";

            string tempDir = null;
            try
            {
                if (!File.Exists(path)) { error = "Файл не найден."; return false; }

                tempDir = Path.Combine(Path.GetTempPath(), "hmwt-pack-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(path, tempDir);

                string manifestPath = Path.Combine(tempDir, ManifestEntry);
                if (!File.Exists(manifestPath)) { error = "Это не пак HMWT: внутри нет manifest.json"; return false; }

                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), ReadOptions);
                if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Kind) &&
                    !string.Equals(manifest.Kind, "bestiary", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Это пак «" + manifest.Kind + "», а не бестиарий.";
                    return false;
                }

                string monstersPath = Path.Combine(tempDir, MonstersEntry);
                if (!File.Exists(monstersPath)) { error = "В паке нет списка существ (monsters.json)."; return false; }

                var pack = JsonSerializer.Deserialize<List<PackMonster>>(File.ReadAllText(monstersPath), ReadOptions)
                           ?? new List<PackMonster>();

                var existing = new HashSet<string>(
                    BestiaryService.LoadAllBrief().Select(m => (m.Name ?? "").Trim().ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var entry in pack)
                {
                    if (entry?.Monster == null) continue;

                    string key = (entry.Monster.Name ?? "").Trim().ToLowerInvariant();
                    if (key.Length == 0) { skipped++; continue; }
                    if (existing.Contains(key)) { skipped++; continue; }

                    var monster = entry.Monster;
                    monster.Id = 0;
                    // флаг «своё» сохраняем как в паке: своё существо остаётся своим,
                    // а существо из справочника — нет
                    monster.CreatedAt = default;
                    monster.UpdatedAt = default;
                    monster.PhotoPath = "";

                    int id = BestiaryService.Save(monster);
                    if (id <= 0) { skipped++; continue; }

                    monster.Id = id;
                    added++;
                    existing.Add(key);

                    if (!string.IsNullOrWhiteSpace(entry.PhotoFile))
                    {
                        try
                        {
                            string imagePath = Path.Combine(tempDir, entry.PhotoFile.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(imagePath)) BestiaryService.AttachPhoto(monster, imagePath);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[BestiaryPack] картинка пака: {ex.Message}");
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryPack] Import error: {ex.Message}");
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion
    }
}
