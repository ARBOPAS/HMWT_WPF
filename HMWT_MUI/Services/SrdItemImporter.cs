using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HMWT_MUI.Services
{
    /// <summary>Запись каталога магазина (таблица SrdItem).</summary>
    public sealed class SrdItemRow
    {
        public int Id { get; set; }
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
        public string Source { get; set; } = "";
        public string Book { get; set; } = "";
        public string PhotoPath { get; set; } = "";

        /// <summary>
        /// Отдельное фото для строки списка.
        ///
        /// В списке картинка мелкая (44×44), в карточке предмета — крупная, поэтому
        /// это разные файлы: игрок сам решает, что показывать в каждом месте.
        /// </summary>
        public string ListPhotoPath { get; set; } = "";

        /// <summary>Исходный JSON свойств — пригодится, если понадобятся поля вне колонок.</summary>
        public string RawProperties { get; set; } = "";

        /// <summary>Подпись для карточки: тип · редкость.</summary>
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(ItemType)) parts.Add(ItemType);
                if (!string.IsNullOrWhiteSpace(Rarity)) parts.Add(Rarity);
                return string.Join(" · ", parts);
            }
        }

        /// <summary>Компактная строка характеристик для списка.</summary>
        public string StatsLine
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Damage))
                    parts.Add($"Урон {Damage}" + (string.IsNullOrWhiteSpace(DamageType) ? "" : $" ({DamageType})"));
                if (Ac > 0) parts.Add($"КД {Ac}");
                if (Weight > 0) parts.Add($"{Weight:0.##} кг");
                if (Attunement) parts.Add("настройка");
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// Импорт каталога предметов для вкладки «Магазин» из Data\srd\items.json.
    ///
    /// ВАЖНО про разделение с SrdImportService: тот пишет любые категории в
    /// generic-таблицу SrdReference и нужен для monsters/classes/species/backgrounds.
    /// Здесь отдельная таблица SrdItem с нормальными колонками — у предметов
    /// 15+ тысяч записей и фильтры идут по конкретным полям.
    ///
    /// Данные по одному предмету разбросаны по книгам НЕПОЛНО (у Battleaxe урон
    /// есть только в Free Basic Rules), поэтому записи сливаются по имени:
    /// берётся первое непустое значение каждого поля.
    /// </summary>
    public static class SrdItemImporter
    {
        /// <summary>
        /// Пометка «свой предмет» в поле книги каталога.
        ///
        /// Свои предметы лежат в ТОЙ ЖЕ таблице, что импортированные из SRD:
        /// магазин читает один каталог, и отдельная таблица только запутала бы.
        /// По этой пометке видно, откуда предмет, и её же использует
        /// обновление справочника, чтобы не затереть ручные записи.
        /// </summary>
        public const string CustomBook = "Свой предмет";

        /// <summary>Является ли предмет созданным вручную.</summary>
        public static bool IsCustom(SrdItemRow row)
            => row != null && string.Equals(row.Book, CustomBook, StringComparison.Ordinal);

        /// <summary>
        /// Сохраняет свой предмет в каталог магазина.
        ///
        /// Нужно тем, у кого нет файла справочника: без этого магазин пуст
        /// и взаимодействовать с ним невозможно. Созданный предмет ведёт себя
        /// как импортированный — его можно смотреть и покупать.
        /// </summary>
        /// <returns>Идентификатор записи либо 0 при ошибке.</returns>
        public static int SaveCustomItem(SrdItemRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Name)) return 0;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using var cmd = new SqliteCommand(@"
                    INSERT INTO SrdItem
                        (name, description, item_type, subtype, rarity, weight, damage, damage_type,
                         ac, range, properties_text, attunement, is_magic, source, book, raw_properties)
                    VALUES
                        (@name, @desc, @type, @sub, @rar, @weight, @dmg, @dmgType,
                         @ac, @range, @props, @att, @magic, @src, @book, @raw)
                    ON CONFLICT(name, book) DO UPDATE SET
                        description=excluded.description,
                        item_type=excluded.item_type,
                        subtype=excluded.subtype,
                        rarity=excluded.rarity,
                        weight=excluded.weight,
                        damage=excluded.damage,
                        damage_type=excluded.damage_type,
                        ac=excluded.ac,
                        range=excluded.range,
                        properties_text=excluded.properties_text,
                        attunement=excluded.attunement,
                        is_magic=excluded.is_magic,
                        source=excluded.source,
                        raw_properties=excluded.raw_properties;
                    SELECT last_insert_rowid();", conn);

                cmd.Parameters.AddWithValue("@name", row.Name.Trim());
                cmd.Parameters.AddWithValue("@desc", row.Description ?? "");
                cmd.Parameters.AddWithValue("@type", row.ItemType ?? "");
                cmd.Parameters.AddWithValue("@sub", row.Subtype ?? "");
                cmd.Parameters.AddWithValue("@rar", row.Rarity ?? "");
                cmd.Parameters.AddWithValue("@weight", row.Weight);
                cmd.Parameters.AddWithValue("@dmg", row.Damage ?? "");
                cmd.Parameters.AddWithValue("@dmgType", row.DamageType ?? "");
                cmd.Parameters.AddWithValue("@ac", row.Ac);
                cmd.Parameters.AddWithValue("@range", row.Range ?? "");
                cmd.Parameters.AddWithValue("@props", row.PropertiesText ?? "");
                cmd.Parameters.AddWithValue("@att", row.Attunement ? 1 : 0);
                cmd.Parameters.AddWithValue("@magic", row.IsMagic ? 1 : 0);
                cmd.Parameters.AddWithValue("@src", row.Source ?? CustomBook);
                cmd.Parameters.AddWithValue("@book", CustomBook);
                cmd.Parameters.AddWithValue("@raw", row.RawProperties ?? "{}");

                var result = cmd.ExecuteScalar();
                int id = result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);

                // Если запись обновилась, last_insert_rowid не даёт её id — ищем по имени
                if (id <= 0)
                {
                    using var find = new SqliteCommand(
                        "SELECT id_srd_item FROM SrdItem WHERE name=@n AND book=@b", conn);
                    find.Parameters.AddWithValue("@n", row.Name.Trim());
                    find.Parameters.AddWithValue("@b", CustomBook);
                    var found = find.ExecuteScalar();
                    if (found != null && found != DBNull.Value) id = Convert.ToInt32(found);
                }

                Debug.WriteLine($"[Свой предмет] сохранён «{row.Name}», id={id}");
                return id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Свой предмет] не удалось сохранить: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Сколько своих предметов создано вручную.</summary>
        public static int CountCustomItems()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM SrdItem WHERE book=@b", conn);
                cmd.Parameters.AddWithValue("@b", CustomBook);
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Свой предмет] подсчёт: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Ищет в каталоге предмет, который ничем не отличается от указанного.
        ///
        /// Защита от неумелого пользователя: нельзя завести второй предмет,
        /// повторяющий уже имеющийся. Сравниваем то, что описывает предмет:
        /// название, описание, кость и тип урона, тип, редкость, вес и бонус КД.
        /// Если отличается хоть что-то — это другой предмет.
        ///
        /// Записи справочника тоже проверяются: свой предмет, повторяющий
        /// импортированный, только запутает.
        /// </summary>
        /// <param name="candidate">Что собираемся сохранить.</param>
        /// <param name="exceptId">Эту запись не учитываем (она и есть изменяемая).</param>
        /// <returns>Найденный предмет либо null.</returns>
        public static SrdItemRow FindSimilar(SrdItemRow candidate, int exceptId = 0)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Name)) return null;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT id_srd_item, name, COALESCE(item_type,'') AS item_type,
                           COALESCE(rarity,'') AS rarity, COALESCE(damage,'') AS damage,
                           COALESCE(book,'') AS book
                    FROM SrdItem
                    WHERE name=@name
                      AND COALESCE(item_type,'')=@type
                      AND COALESCE(rarity,'')=@rarity
                      AND COALESCE(damage,'')=@damage
                      AND COALESCE(damage_type,'')=@damageType
                      AND COALESCE(ac,0)=@ac
                      AND COALESCE(weight,0)=@weight
                      AND COALESCE(description,'')=@description
                      AND id_srd_item<>@except
                    LIMIT 1", conn);

                cmd.Parameters.AddWithValue("@name", candidate.Name.Trim());
                cmd.Parameters.AddWithValue("@type", candidate.ItemType ?? "");
                cmd.Parameters.AddWithValue("@rarity", candidate.Rarity ?? "");
                cmd.Parameters.AddWithValue("@damage", candidate.Damage ?? "");
                cmd.Parameters.AddWithValue("@damageType", candidate.DamageType ?? "");
                cmd.Parameters.AddWithValue("@ac", candidate.Ac);
                cmd.Parameters.AddWithValue("@weight", candidate.Weight);
                cmd.Parameters.AddWithValue("@description", candidate.Description ?? "");
                cmd.Parameters.AddWithValue("@except", exceptId);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                return new SrdItemRow
                {
                    Id = Convert.ToInt32(reader["id_srd_item"]),
                    Name = reader["name"]?.ToString() ?? "",
                    ItemType = reader["item_type"]?.ToString() ?? "",
                    Rarity = reader["rarity"]?.ToString() ?? "",
                    Damage = reader["damage"]?.ToString() ?? "",
                    Book = reader["book"]?.ToString() ?? ""
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Свой предмет] поиск похожего: {ex.Message}");
                return null;
            }
        }

        private static string DataDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "srd");

        /// <summary>Сколько записей в каталоге магазина.</summary>
        public static int GetCount()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT COUNT(1) FROM SrdItem", conn);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SrdItemImporter] GetCount: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Ищет items.json рядом с приложением, рядом со сборкой и в AppData.</summary>
        public static string FindItemsJson()
        {
            var candidates = new List<string>
            {
                Path.Combine(DataDir, "items.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "HMWT", "Data", "srd", "items.json")
            };

            try
            {
                var asmDir = Path.GetDirectoryName(typeof(SrdItemImporter).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    candidates.Add(Path.Combine(asmDir, "Data", "srd", "items.json"));
                    candidates.Add(Path.Combine(Directory.GetParent(asmDir)?.FullName ?? asmDir, "Data", "srd", "items.json"));
                }
            }
            catch { }

            foreach (var path in candidates)
                if (File.Exists(path)) return path;

            return null;
        }

        /// <summary>
        /// Импортирует каталог, если он ещё не загружен ИЛИ если файл items.json
        /// изменился (например, в новой версии программы обновили SRD).
        /// Возвращает число записей в каталоге (0 — ничего не делали).
        /// Фото игроков при обновлении не теряются: UPDATE не трогает photo_path.
        /// </summary>
        public static int EnsureImported()
        {
            try
            {
                var path = FindItemsJson();
                if (path == null)
                {
                    Debug.WriteLine("[SrdItemImporter] items.json не найден");
                    return 0;
                }

                int count = GetCount();
                string storedHash = DatabaseHelper.GetSetting(HashSettingKey, "");

                // Считаем отпечаток файла: по нему понимаем, менялся ли документ
                string currentHash = "";
                try
                {
                    using var stream = File.OpenRead(path);
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    currentHash = Convert.ToBase64String(sha.ComputeHash(stream));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SrdItemImporter] не удалось посчитать хеш: {ex.Message}");
                }

                bool needImport = count <= 100;
                if (!needImport && currentHash.Length > 0)
                {
                    if (string.IsNullOrEmpty(storedHash))
                    {
                        // Каталог есть, но отпечатка нет (старая версия базы) — запоминаем
                        DatabaseHelper.SaveSetting(HashSettingKey, currentHash);
                        Debug.WriteLine("[SrdItemImporter] каталог уже загружен, отпечаток сохранён");
                        return count;
                    }

                    if (!string.Equals(storedHash, currentHash, StringComparison.Ordinal))
                    {
                        needImport = true;
                        Debug.WriteLine("[SrdItemImporter] items.json изменился — обновляю каталог");
                    }
                }

                if (!needImport)
                {
                    Debug.WriteLine("[SrdItemImporter] каталог актуален");
                    return count;
                }

                int imported = ImportFromFile(path);
                if (currentHash.Length > 0) DatabaseHelper.SaveSetting(HashSettingKey, currentHash);
                Debug.WriteLine($"[SrdItemImporter] импортировано: {imported}, всего в каталоге: {GetCount()}");
                return imported;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SrdItemImporter] EnsureImported: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Ключ в Settings с отпечатком импортированного items.json.</summary>
        private const string HashSettingKey = "SrdItems_SourceHash";

        /// <summary>Полный импорт файла. Существующие записи обновляются, фото сохраняется.</summary>
        public static int ImportFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return 0;

            var rows = LoadRows(filePath);
            if (rows.Count == 0) return 0;

            int written = 0;
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            using var cmd = new SqliteCommand(@"
                INSERT INTO SrdItem
                    (name, description, item_type, subtype, rarity, weight, damage, damage_type,
                     ac, range, properties_text, attunement, is_magic, source, book, raw_properties)
                VALUES
                    (@name, @desc, @type, @sub, @rar, @weight, @dmg, @dmgType,
                     @ac, @range, @props, @att, @magic, @src, @book, @raw)
                ON CONFLICT(name, book) DO UPDATE SET
                    description=excluded.description,
                    item_type=excluded.item_type,
                    subtype=excluded.subtype,
                    rarity=excluded.rarity,
                    weight=excluded.weight,
                    damage=excluded.damage,
                    damage_type=excluded.damage_type,
                    ac=excluded.ac,
                    range=excluded.range,
                    properties_text=excluded.properties_text,
                    attunement=excluded.attunement,
                    is_magic=excluded.is_magic,
                    source=excluded.source,
                    raw_properties=excluded.raw_properties", conn, tx);

            foreach (var row in rows)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@name", row.Name);
                cmd.Parameters.AddWithValue("@desc", row.Description ?? "");
                cmd.Parameters.AddWithValue("@type", row.ItemType ?? "");
                cmd.Parameters.AddWithValue("@sub", row.Subtype ?? "");
                cmd.Parameters.AddWithValue("@rar", row.Rarity ?? "");
                cmd.Parameters.AddWithValue("@weight", row.Weight);
                cmd.Parameters.AddWithValue("@dmg", row.Damage ?? "");
                cmd.Parameters.AddWithValue("@dmgType", row.DamageType ?? "");
                cmd.Parameters.AddWithValue("@ac", row.Ac);
                cmd.Parameters.AddWithValue("@range", row.Range ?? "");
                cmd.Parameters.AddWithValue("@props", row.PropertiesText ?? "");
                cmd.Parameters.AddWithValue("@att", row.Attunement ? 1 : 0);
                cmd.Parameters.AddWithValue("@magic", row.IsMagic ? 1 : 0);
                cmd.Parameters.AddWithValue("@src", row.Source ?? "");
                cmd.Parameters.AddWithValue("@book", row.Book ?? "");
                cmd.Parameters.AddWithValue("@raw", row.RawProperties ?? "");
                try { written += cmd.ExecuteNonQuery(); } catch { }
            }

            tx.Commit();
            return written;
        }

        /// <summary>
        /// Разбирает файл в строки, не трогая базу. Записи с одинаковым именем
        /// (в разных книгах) сливаются: берётся первое непустое значение поля.
        /// </summary>
        public static List<SrdItemRow> LoadRows(string filePath)
        {
            var byName = new Dictionary<string, SrdItemRow>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return new List<SrdItemRow>();

            try
            {
                using var stream = File.OpenRead(filePath);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<SrdItemRow>();

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        var row = ParseItem(element);
                        if (row == null || string.IsNullOrWhiteSpace(row.Name)) continue;

                        if (byName.TryGetValue(row.Name, out var existing))
                        {
                            MergeInto(existing, row);
                        }
                        else
                        {
                            byName[row.Name] = row;
                            order.Add(row.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SrdItemImporter] запись пропущена: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SrdItemImporter] LoadRows: {ex.Message}");
            }

            return order.Select(n => byName[n]).ToList();
        }

        /// <summary>Дополняет запись непустыми полями из другой книги и выбирает лучший источник.</summary>
        private static void MergeInto(SrdItemRow target, SrdItemRow incoming)
        {
            if (string.IsNullOrWhiteSpace(target.Description) && !string.IsNullOrWhiteSpace(incoming.Description))
                target.Description = incoming.Description;

            if (string.IsNullOrWhiteSpace(target.ItemType) && !string.IsNullOrWhiteSpace(incoming.ItemType))
                target.ItemType = incoming.ItemType;

            if (string.IsNullOrWhiteSpace(target.Subtype) && !string.IsNullOrWhiteSpace(incoming.Subtype))
                target.Subtype = incoming.Subtype;

            // Редкость: пустая или «Обычный» уступает более содержательной
            if (string.IsNullOrWhiteSpace(target.Rarity) ||
                (target.Rarity == "Обычный" && !string.IsNullOrWhiteSpace(incoming.Rarity) && incoming.Rarity != "Обычный"))
                target.Rarity = incoming.Rarity;

            if (string.IsNullOrWhiteSpace(target.Damage) && !string.IsNullOrWhiteSpace(incoming.Damage))
                target.Damage = incoming.Damage;

            if (string.IsNullOrWhiteSpace(target.DamageType) && !string.IsNullOrWhiteSpace(incoming.DamageType))
                target.DamageType = incoming.DamageType;

            if (string.IsNullOrWhiteSpace(target.Range) && !string.IsNullOrWhiteSpace(incoming.Range))
                target.Range = incoming.Range;

            if (string.IsNullOrWhiteSpace(target.PropertiesText) && !string.IsNullOrWhiteSpace(incoming.PropertiesText))
                target.PropertiesText = incoming.PropertiesText;

            if (target.Weight <= 0 && incoming.Weight > 0) target.Weight = incoming.Weight;
            if (target.Ac <= 0 && incoming.Ac > 0) target.Ac = incoming.Ac;

            // Настройка и «магичность» — объединяем по ИЛИ: если хоть где-то сказано, значит так и есть
            target.Attunement |= incoming.Attunement;
            target.IsMagic |= incoming.IsMagic;

            // Источник показываем тот, что дал больше данных (обычно первоисточник)
            int targetScore = Score(target);
            int incomingScore = Score(incoming);
            if (incomingScore > targetScore)
            {
                target.Book = incoming.Book;
                target.Source = incoming.Source;
                target.RawProperties = incoming.RawProperties;
            }
        }

        /// <summary>Насколько запись наполнена — по этому выбираем «главную» книгу.</summary>
        private static int Score(SrdItemRow r)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(r.Damage)) score += 3;
            if (r.Ac > 0) score += 3;
            if (r.Weight > 0) score += 2;
            if (!string.IsNullOrWhiteSpace(r.Rarity)) score += 2;
            if (!string.IsNullOrWhiteSpace(r.Subtype)) score += 1;
            if (!string.IsNullOrWhiteSpace(r.Description)) score += 1;
            if (r.Attunement) score += 1;
            return score;
        }

        private static SrdItemRow ParseItem(JsonElement element)
        {
            string name = GetString(element, "name");
            if (string.IsNullOrWhiteSpace(name)) return null;

            var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            bool hasProps = false;
            if (element.TryGetProperty("properties", out var propsElement) && propsElement.ValueKind == JsonValueKind.Object)
            {
                hasProps = true;
                foreach (var p in propsElement.EnumerateObject()) props[p.Name] = p.Value;
            }

            var row = new SrdItemRow
            {
                Name = name.Trim(),
                Description = GetString(element, "description"),
                Book = GetString(element, "book"),
                Source = GetString(element, "publisher"),
                RawProperties = hasProps ? propsElement.GetRawText() : "",
                ItemType = NormalizeType(GetProp(props, "Item Type")),
                Subtype = GetProp(props, "Subtype"),
                Rarity = NormalizeRarity(GetProp(props, "Item Rarity")),
                Weight = ParseDouble(GetProp(props, "Weight")),
                Damage = GetProp(props, "Damage"),
                DamageType = GetProp(props, "Damage Type"),
                Ac = ParseInt(GetProp(props, "AC")),
                Range = GetProp(props, "Range"),
                PropertiesText = GetProp(props, "Properties"),
                Attunement = IsTruthy(GetProp(props, "Requires Attunement"))
            };

            // «Weapon (Club)» -> тип «Weapon», подтип «Club»
            if (!string.IsNullOrWhiteSpace(row.ItemType))
            {
                int open = row.ItemType.IndexOf('(');
                if (open > 0)
                {
                    int close = row.ItemType.IndexOf(')', open);
                    if (close > open)
                    {
                        string inner = row.ItemType.Substring(open + 1, close - open - 1).Trim();
                        if (string.IsNullOrWhiteSpace(row.Subtype)) row.Subtype = inner;
                        row.ItemType = row.ItemType.Substring(0, open).Trim();
                    }
                }
            }

            // Магический = требует настройки либо имеет настоящую редкость
            // (проверяем уже нормализованное значение: «uncommon (...)» -> «Необычный»)
            string rar = (row.Rarity ?? "").Trim();
            row.IsMagic = row.Attunement
                          || (rar.Length > 0 && !rar.Equals("Обычный", StringComparison.OrdinalIgnoreCase));

            return row;
        }

        private static string GetString(JsonElement element, string key)
        {
            try
            {
                if (!element.TryGetProperty(key, out var v)) return "";
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            }
            catch { return ""; }
        }

        /// <summary>Значение свойства как строка: в файле встречаются и числа, и строки.</summary>
        private static string GetProp(Dictionary<string, JsonElement> props, string key)
        {
            if (props == null || !props.TryGetValue(key, out var v)) return "";
            try
            {
                if (v.ValueKind == JsonValueKind.String) return (v.GetString() ?? "").Trim();
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble().ToString(CultureInfo.InvariantCulture);
                if (v.ValueKind == JsonValueKind.True) return "Yes";
                if (v.ValueKind == JsonValueKind.False) return "No";
                return v.ToString().Trim();
            }
            catch { return ""; }
        }

        private static string NormalizeType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var t = raw.Trim();
            return t.Length <= 1 ? t : char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        /// <summary>
        /// Приводит редкость к единому виду. В файле встречаются:
        /// «Yes»/«No» (не редкость вовсе), «Standard», разные регистры и длинные
        /// строки вида «uncommon (requires attunement by a cleric)» — из них надо
        /// вытащить саму редкость, иначе фильтр превращается в свалку.
        /// </summary>
        private static string NormalizeRarity(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var r = raw.Trim().ToLowerInvariant();
            if (r == "-" || r == "any" || r == "n/a") return "";

            // Точные совпадения — самый частый случай
            switch (r)
            {
                case "no": case "none": case "yes": case "": return "";
                case "standard": case "common": return "Обычный";
                case "uncommon": return "Необычный";
                case "rare": return "Редкий";
                case "very rare": return "Очень редкий";
                case "legendary": return "Легендарный";
                case "artifact": return "Артефакт";
                case "fabled": return "Fabled";
                case "epic": return "Epic";
            }

            // Длинные строки: ищем ключевое слово редкости внутри
            if (r.Contains("very rare")) return "Очень редкий";
            if (r.Contains("legendary") || r.Contains("legenndary")) return "Легендарный";
            if (r.Contains("artifact")) return "Артефакт";
            if (r.Contains("uncommon")) return "Необычный";
            if (r.Contains("rare")) return "Редкий";
            if (r.Contains("common") || r.Contains("standard")) return "Обычный";
            if (r.Contains("varies")) return "Разное";
            if (r.Contains("unique")) return "Уникальный";
            if (r.Contains("cypheos")) return "Cypheos";
            if (r.Contains("covenant")) return "Covenant Item";
            if (r.Contains("fabled")) return "Fabled";
            if (r.Contains("epic")) return "Epic";

            // Хлам: в поле редкости иногда попадают свойства оружия или служебные метки.
            // Лучше пусто, чем значение, которое засоряет фильтр.
            var junk = new[] { "awakened", "martial", "simple", "flail", "sword", "axe",
                               "rarity by", "keyrune", "wondrous", "potion", "scroll" };
            foreach (var word in junk)
                if (r.Contains(word)) return "";

            // Совсем нестандартное — оставляем как есть, но одной строкой
            return raw.Trim().Replace("\n", " ").Replace("\r", " ");
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim().ToLowerInvariant();
            if (v == "no" || v == "none" || v == "false" || v == "0") return false;
            return v == "yes" || v == "true" || v == "1" || v.Contains("attunement");
        }

        private static double ParseDouble(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var v = value.Replace(',', '.').Trim();
            return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }

        private static int ParseInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            return int.TryParse(value.Trim(), out int i) ? i : 0;
        }
    }
}
