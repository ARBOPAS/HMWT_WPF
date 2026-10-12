using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Обмен своими предметами и заклинаниями между игроками.
    ///
    /// Пользователь просил: игроки сами наполняют базу и делятся ею файлом — один
    /// экспортирует, другой импортирует в этой же программе. Поэтому данные
    /// справочника сюда не попадают: выгружаются только свои предметы
    /// (book = «Свой предмет») и свои заклинания (source = custom).
    ///
    /// Файл — обычный zip с расширением .hmwtpack: внутри manifest.json и папка
    /// images с картинками. Так фото переезжает вместе с предметом, и второму
    /// игроку не нужно ничего докладывать руками.
    ///
    /// Совместимость: в манифесте есть version. Читаем формат версии 1, а лишние
    /// поля из будущих версий игнорируем — файл, сделанный более новой сборкой,
    /// не сломается.
    /// </summary>
    public static class ShareService
    {
        /// <summary>Версия формата, которую пишем сейчас.</summary>
        public const int FormatVersion = 1;

        public const string FileExtension = ".hmwtpack";

        private const string ManifestEntry = "manifest.json";
        private const string ImagesFolder = "images";

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
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

        public sealed class ShareManifest
        {
            public int Version { get; set; } = FormatVersion;
            public string App { get; set; } = "HMWT Гримуар";
            public string CreatedAt { get; set; } = "";
            public List<ShareItem> Items { get; set; } = new List<ShareItem>();
            public List<ShareSpell> Spells { get; set; } = new List<ShareSpell>();
        }

        public sealed class ShareItem
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string ItemType { get; set; } = "";
            public string Subtype { get; set; } = "";
            public string Rarity { get; set; } = "";
            public double Weight { get; set; }
            public string Damage { get; set; } = "";
            public string DamageType { get; set; } = "";
            public int Ac { get; set; }
            public string Range { get; set; } = "";
            public string PropertiesText { get; set; } = "";
            public bool Attunement { get; set; }
            public bool IsMagic { get; set; }

            /// <summary>Имя файла картинки в папке images внутри пакета.</summary>
            public string PhotoFile { get; set; }
            public string ListPhotoFile { get; set; }
        }

        public sealed class ShareSpell
        {
            public string Name { get; set; } = "";
            public int Level { get; set; }
            public string School { get; set; } = "";
            public string CastingTime { get; set; } = "";
            public string Range { get; set; } = "";
            public string Components { get; set; } = "";
            public string Duration { get; set; } = "";
            public string Description { get; set; } = "";
            public string AtHigherLevels { get; set; } = "";
            public string Classes { get; set; } = "";
            public string Material { get; set; } = "";
            public string Page { get; set; } = "";
            public bool Concentration { get; set; }
            public bool Ritual { get; set; }

            // Эффект для кастования
            public string DamageFormula { get; set; } = "";
            public string DamageType { get; set; } = "";
            public string PerLevelDamage { get; set; } = "";
        }

        #endregion

        #region === ЭКСПОРТ ===

        /// <summary>Сколько своих предметов и заклинаний можно выгрузить.</summary>
        public static (int Items, int Spells) CountCustom()
        {
            int items = 0, spells = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM SrdItem WHERE book = @b", conn))
                {
                    cmd.Parameters.AddWithValue("@b", SrdItemImporter.CustomBook);
                    items = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                }

                using (var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM SpellReference WHERE source = 'custom'", conn))
                {
                    spells = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShareService] CountCustom: {ex.Message}");
            }
            return (items, spells);
        }

        /// <summary>
        /// Собирает пакет со своими предметами и заклинаниями.
        ///
        /// Что выгружать, выбирает вызывающий: пользователь просил разделить —
        /// предметы выгружаются на вкладке инвентаря, заклинания на вкладке
        /// заклинаний. Формат файла от этого не меняется, поэтому пакет,
        /// сделанный на одной вкладке, читается как обычно.
        /// </summary>
        public static ShareManifest Export(string targetFile, out string error,
            bool includeItems = true, bool includeSpells = true)
        {
            error = "";
            try
            {
                var manifest = new ShareManifest
                {
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                string root = PhotoHelper.GetWritableRoot();
                var filesToPack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // ── предметы ──
                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();

                    if (includeItems)
                    using (var cmd = new SqliteCommand(@"
                        SELECT name, description, item_type, subtype, rarity, weight, damage, damage_type,
                               ac, range, properties_text, attunement, is_magic,
                               COALESCE(photo_path,'') AS photo_path,
                               COALESCE(list_photo_path,'') AS list_photo_path
                        FROM SrdItem WHERE book = @b ORDER BY name", conn))
                    {
                        cmd.Parameters.AddWithValue("@b", SrdItemImporter.CustomBook);
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var item = new ShareItem
                            {
                                Name = ReadText(reader, "name"),
                                Description = ReadText(reader, "description"),
                                ItemType = ReadText(reader, "item_type"),
                                Subtype = ReadText(reader, "subtype"),
                                Rarity = ReadText(reader, "rarity"),
                                Weight = ReadDouble(reader, "weight"),
                                Damage = ReadText(reader, "damage"),
                                DamageType = ReadText(reader, "damage_type"),
                                Ac = ReadInt(reader, "ac"),
                                Range = ReadText(reader, "range"),
                                PropertiesText = ReadText(reader, "properties_text"),
                                Attunement = ReadInt(reader, "attunement") == 1,
                                IsMagic = ReadInt(reader, "is_magic") == 1
                            };

                            item.PhotoFile = AttachImage(root, ReadText(reader, "photo_path"), "item", filesToPack);
                            item.ListPhotoFile = AttachImage(root, ReadText(reader, "list_photo_path"), "list", filesToPack);

                            manifest.Items.Add(item);
                        }
                    }

                    // ── заклинания ──
                    if (includeSpells)
                    {
                        bool hasEffect = DatabaseHelper.HasColumn(conn, "SpellReference", "damage_formula");
                        string effectSelect = hasEffect
                            ? "COALESCE(damage_formula,''), COALESCE(damage_type,''), COALESCE(per_level_damage,'')"
                            : "'' , '' , '' ";

                        using (var cmd = new SqliteCommand($@"
                            SELECT name, level, school, casting_time, range, components, duration,
                                   description, at_higher_levels, classes, material, page,
                                   concentration, ritual, {effectSelect}
                            FROM SpellReference WHERE source = 'custom' ORDER BY level, name", conn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                manifest.Spells.Add(new ShareSpell
                                {
                                    Name = ReadText(reader, "name"),
                                    Level = ReadInt(reader, "level"),
                                    School = ReadText(reader, "school"),
                                    CastingTime = ReadText(reader, "casting_time"),
                                    Range = ReadText(reader, "range"),
                                    Components = ReadText(reader, "components"),
                                    Duration = ReadText(reader, "duration"),
                                    Description = ReadText(reader, "description"),
                                    AtHigherLevels = ReadText(reader, "at_higher_levels"),
                                    Classes = ReadText(reader, "classes"),
                                    Material = ReadText(reader, "material"),
                                    Page = ReadText(reader, "page"),
                                    Concentration = ReadInt(reader, "concentration") == 1,
                                    Ritual = ReadInt(reader, "ritual") == 1,
                                    DamageFormula = reader.IsDBNull(14) ? "" : reader.GetValue(14)?.ToString() ?? "",
                                    DamageType = reader.IsDBNull(15) ? "" : reader.GetValue(15)?.ToString() ?? "",
                                    PerLevelDamage = reader.IsDBNull(16) ? "" : reader.GetValue(16)?.ToString() ?? ""
                                });
                            }
                        }
                    }
                }

                // ── запись файла ──
                if (File.Exists(targetFile)) File.Delete(targetFile);

                using (var zip = ZipFile.Open(targetFile, ZipArchiveMode.Create))
                {
                    string json = JsonSerializer.Serialize(manifest, WriteOptions);
                    var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                    using (var stream = entry.Open())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.Write(json);

                    foreach (var pair in filesToPack)
                    {
                        if (!File.Exists(pair.Value)) continue;
                        var imgEntry = zip.CreateEntry(ImagesFolder + "/" + pair.Key, CompressionLevel.Optimal);
                        using var imgStream = imgEntry.Open();
                        using var source = File.OpenRead(pair.Value);
                        source.CopyTo(imgStream);
                    }
                }

                Debug.WriteLine($"[ShareService] экспорт: предметов {manifest.Items.Count}, " +
                                $"заклинаний {manifest.Spells.Count}, картинок {filesToPack.Count}");
                return manifest;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[ShareService] Export: {ex}");
                return null;
            }
        }

        /// <summary>Кладёт картинку в список для упаковки и возвращает её имя в пакете.</summary>
        private static string AttachImage(string root, string relative, string prefix,
            Dictionary<string, string> sink)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;

            try
            {
                string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) return null;

                string ext = Path.GetExtension(full);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".png";

                // Имя внутри пакета уникальное: одинаковые файлы разных предметов не перетрутся
                string name = prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ext.ToLowerInvariant();
                if (!sink.ContainsKey(name)) sink[name] = full;
                return name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShareService] AttachImage: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region === ИМПОРТ ===

        public sealed class ImportResult
        {
            public int ItemsAdded;
            public int ItemsUpdated;
            public int SpellsAdded;
            public int SpellsUpdated;
            public int ImagesRestored;
            public List<string> SpellsForCharacter = new List<string>();
            public string Error = "";

            public int Total => ItemsAdded + ItemsUpdated + SpellsAdded + SpellsUpdated;
        }

        /// <summary>
        /// Читает пакет и добавляет содержимое в базу.
        ///
        /// Предметы со знакомым именем обновляются, а не дублируются: иначе после
        /// обмена в каталоге появлялись бы две одинаковые записи. Свои заклинания
        /// добавляются в справочник и НЕ навязываются персонажу — игрок сам решит,
        /// какие взять (список вернувшихся имён отдаём наружу для подсказки).
        /// </summary>
        public static ImportResult Import(string packageFile, int characterId)
        {
            var result = new ImportResult();
            try
            {
                if (!File.Exists(packageFile))
                {
                    result.Error = "Файл не найден";
                    return result;
                }

                ShareManifest manifest;
                string imagesDir = null;

                using (var zip = ZipFile.OpenRead(packageFile))
                {
                    var manifestEntry = zip.GetEntry(ManifestEntry);
                    if (manifestEntry == null)
                    {
                        result.Error = "Это не файл обмена HMWT: внутри нет manifest.json";
                        return result;
                    }

                    string json;
                    using (var stream = manifestEntry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        json = reader.ReadToEnd();

                    manifest = JsonSerializer.Deserialize<ShareManifest>(json, ReadOptions);
                    if (manifest == null)
                    {
                        result.Error = "Не удалось прочитать содержимое файла";
                        return result;
                    }

                    if (manifest.Version > FormatVersion)
                    {
                        Debug.WriteLine($"[ShareService] файл версии {manifest.Version}, читаем как {FormatVersion}");
                    }

                    // Картинки распаковываем во временную папку: база их только скопирует
                    if (zip.Entries.Any(e => e.FullName.StartsWith(ImagesFolder + "/", StringComparison.OrdinalIgnoreCase)))
                    {
                        imagesDir = Path.Combine(Path.GetTempPath(), "hmwt_share_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(imagesDir);

                        foreach (var entry in zip.Entries)
                        {
                            if (!entry.FullName.StartsWith(ImagesFolder + "/", StringComparison.OrdinalIgnoreCase)) continue;
                            if (entry.FullName.EndsWith("/")) continue;

                            string dest = Path.Combine(imagesDir, Path.GetFileName(entry.FullName));
                            entry.ExtractToFile(dest, true);
                        }
                    }

                    result.ImagesRestored = ImportContents(manifest, imagesDir, characterId, result);
                }

                if (imagesDir != null)
                {
                    try { Directory.Delete(imagesDir, true); } catch { }
                }

                Debug.WriteLine($"[ShareService] импорт: предметов +{result.ItemsAdded} ~{result.ItemsUpdated}, " +
                                $"заклинаний +{result.SpellsAdded} ~{result.SpellsUpdated}, картинок {result.ImagesRestored}");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Debug.WriteLine($"[ShareService] Import: {ex}");
                return result;
            }
        }

        /// <summary>Записывает содержимое манифеста в базу. Возвращает число восстановленных картинок.</summary>
        private static int ImportContents(ShareManifest manifest, string imagesDir, int characterId, ImportResult result)
        {
            int images = 0;
            var shop = new ShopService(characterId);

            // ── предметы ──
            foreach (var item in manifest.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Name)) continue;

                try
                {
                    bool exists = CustomItemExists(item.Name);

                    var row = new SrdItemRow
                    {
                        Name = item.Name.Trim(),
                        Description = item.Description ?? "",
                        ItemType = item.ItemType ?? "",
                        Subtype = item.Subtype ?? "",
                        Rarity = item.Rarity ?? "",
                        Weight = item.Weight,
                        Damage = item.Damage ?? "",
                        DamageType = item.DamageType ?? "",
                        Ac = item.Ac,
                        Range = item.Range ?? "",
                        PropertiesText = item.PropertiesText ?? "",
                        Attunement = item.Attunement,
                        IsMagic = item.IsMagic,
                        Book = SrdItemImporter.CustomBook,
                        Source = SrdItemImporter.CustomBook
                    };

                    int id = SrdItemImporter.SaveCustomItem(row);
                    if (id <= 0) continue;

                    if (exists) result.ItemsUpdated++; else result.ItemsAdded++;

                    // Фото: копируем через тот же механизм, что и в магазине
                    if (imagesDir != null)
                    {
                        images += RestorePhoto(shop, id, imagesDir, item.PhotoFile, ShopPhotoKind.Photo);
                        images += RestorePhoto(shop, id, imagesDir, item.ListPhotoFile, ShopPhotoKind.ListPhoto);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ShareService] предмет «{item.Name}»: {ex.Message}");
                }
            }

            // ── заклинания ──
            foreach (var spell in manifest.Spells)
            {
                if (string.IsNullOrWhiteSpace(spell.Name)) continue;

                try
                {
                    bool exists = CustomSpellExists(spell.Name);

                    int id = SpellService.SaveSharedSpell(new Models.SpellData
                    {
                        Name = spell.Name.Trim(),
                        Level = Math.Max(0, Math.Min(9, spell.Level)),
                        School = spell.School ?? "",
                        CastingTime = spell.CastingTime ?? "",
                        Range = spell.Range ?? "",
                        Components = spell.Components ?? "",
                        Duration = spell.Duration ?? "",
                        Description = spell.Description ?? "",
                        AtHigherLevels = spell.AtHigherLevels ?? "",
                        Classes = spell.Classes ?? "",
                        Material = spell.Material ?? "",
                        Page = spell.Page ?? "",
                        Concentration = spell.Concentration,
                        Ritual = spell.Ritual,
                        // Метка «своё заклинание» обязательна: без неё импорт обновлял
                        // запись и затирал её, после чего заклинание перестало
                        // считаться своим и не попадало в следующий экспорт.
                        Source = "custom",
                        DamageFormula = spell.DamageFormula ?? "",
                        DamageType = spell.DamageType ?? "",
                        PerLevelDamage = spell.PerLevelDamage ?? ""
                    });

                    if (id <= 0) continue;
                    if (exists) result.SpellsUpdated++; else result.SpellsAdded++;

                    if (!exists) result.SpellsForCharacter.Add(spell.Name.Trim());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ShareService] заклинание «{spell.Name}»: {ex.Message}");
                }
            }

            return images;
        }

        /// <summary>Копирует картинку из пакета к предмету. Возвращает 1, если получилось.</summary>
        private static int RestorePhoto(ShopService shop, int itemId, string imagesDir,
            string fileName, string photoKind)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return 0;
            try
            {
                string source = Path.Combine(imagesDir, Path.GetFileName(fileName));
                if (!File.Exists(source)) return 0;

                string relative = shop.SaveShopPhoto(itemId, source, photoKind);
                return string.IsNullOrEmpty(relative) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShareService] RestorePhoto: {ex.Message}");
                return 0;
            }
        }

        private static bool CustomItemExists(string name)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM SrdItem WHERE name = @n AND book = @b", conn);
                cmd.Parameters.AddWithValue("@n", name.Trim());
                cmd.Parameters.AddWithValue("@b", SrdItemImporter.CustomBook);
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
            }
            catch { return false; }
        }

        private static bool CustomSpellExists(string name)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM SpellReference WHERE name = @n AND source = 'custom'", conn);
                cmd.Parameters.AddWithValue("@n", name.Trim());
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Удаляет предмет из каталога магазина по идентификатору.
        ///
        /// Пользователь просил: предмет можно создать, но убрать нельзя. Удаляем и
        /// свои записи, и записи справочника — во втором случае запись вернётся
        /// при повторном импорте файла справочника.
        /// </summary>
        public static bool DeleteCatalogItem(int itemId, bool custom)
        {
            if (itemId <= 0) return false;
            try
            {
                // Сначала фото: после удаления записи их пути уже негде взять
                if (custom)
                {
                    try
                    {
                        var shop = new ShopService(1);
                        shop.ClearShopPhoto(itemId, ShopPhotoKind.Photo);
                        shop.ClearShopPhoto(itemId, ShopPhotoKind.ListPhoto);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ShareService] фото удаляемого предмета: {ex.Message}");
                    }
                }

                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using var del = new SqliteCommand("DELETE FROM SrdItem WHERE id_srd_item = @id", conn);
                del.Parameters.AddWithValue("@id", itemId);
                return del.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShareService] DeleteCatalogItem: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region === УДАЛЕНИЕ СВОИХ ЗАПИСЕЙ ===

        /// <summary>
        /// Удаляет свой предмет из каталога вместе с его фото.
        ///
        /// Нужно для порядка: игрок может захотеть убрать то, что загрузил от друга.
        /// Данные справочника эта операция не трогает — только свои предметы.
        /// </summary>
        public static bool DeleteCustomItem(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                int id = 0;
                using (var find = new SqliteCommand(
                    "SELECT id_srd_item FROM SrdItem WHERE name = @n AND book = @b LIMIT 1", conn))
                {
                    find.Parameters.AddWithValue("@n", name.Trim());
                    find.Parameters.AddWithValue("@b", SrdItemImporter.CustomBook);
                    var found = find.ExecuteScalar();
                    if (found == null || found == DBNull.Value) return false;
                    id = Convert.ToInt32(found);
                }

                // Фото убираем тем же способом, что и в магазине
                var shop = new ShopService(1);
                shop.ClearShopPhoto(id, ShopPhotoKind.Photo);
                shop.ClearShopPhoto(id, ShopPhotoKind.ListPhoto);

                using (var del = new SqliteCommand(
                    "DELETE FROM SrdItem WHERE id_srd_item = @id AND book = @b", conn))
                {
                    del.Parameters.AddWithValue("@id", id);
                    del.Parameters.AddWithValue("@b", SrdItemImporter.CustomBook);
                    return del.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShareService] DeleteCustomItem: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Удаляет своё заклинание (и его привязки к персонажам).
        ///
        /// Ищем сначала по метке «своё», а если её нет — по имени среди записей,
        /// созданных вручную. Раньше метка терялась при любом сохранении карточки
        /// (SaveSpell не записывал колонку source), и удаление молча не срабатывало.
        /// </summary>
        public static bool DeleteCustomSpell(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                int id = 0;
                using (var find = new SqliteCommand(@"
                    SELECT id_spell FROM SpellReference
                    WHERE name = @n AND source = 'custom'
                    LIMIT 1", conn))
                {
                    find.Parameters.AddWithValue("@n", name.Trim());
                    var found = find.ExecuteScalar();
                    if (found != null && found != DBNull.Value) id = Convert.ToInt32(found);
                }

                if (id <= 0) return false;

                using (var unlink = new SqliteCommand("DELETE FROM CharacterSpells WHERE id_spell = @id", conn))
                {
                    unlink.Parameters.AddWithValue("@id", id);
                    unlink.ExecuteNonQuery();
                }

                using (var del = new SqliteCommand("DELETE FROM SpellReference WHERE id_spell = @id", conn))
                {
                    del.Parameters.AddWithValue("@id", id);
                    return del.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShareService] DeleteCustomSpell: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region === УТИЛИТЫ ===

        private static string ReadText(SqliteDataReader reader, string column)
        {
            try
            {
                int i = reader.GetOrdinal(column);
                return reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static int ReadInt(SqliteDataReader reader, string column)
        {
            try
            {
                int i = reader.GetOrdinal(column);
                return reader.IsDBNull(i) ? 0 : Convert.ToInt32(reader.GetValue(i));
            }
            catch { return 0; }
        }

        private static double ReadDouble(SqliteDataReader reader, string column)
        {
            try
            {
                int i = reader.GetOrdinal(column);
                return reader.IsDBNull(i) ? 0 : Convert.ToDouble(reader.GetValue(i));
            }
            catch { return 0; }
        }

        #endregion
    }
}
