using HMWT_MUI.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Импорт заклинаний из JSON-файла справочника.
    /// </summary>
    public static class SpellJsonImporter
    {
        private static readonly string DataDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Data", "srd");

        #region === ИМПОРТ ===
        /// <summary>
        /// Импортирует заклинания из spells.json в таблицу SpellReference.
        /// </summary>
        public static int ImportSpells()
        {
            var file = Path.Combine(DataDir, "spells.json");
            return ImportFromFileInternal(file);
        }

        /// <summary>
        /// Импортирует заклинания из указанного пути к JSON.
        /// </summary>
        public static int ImportFromFile(string filePath)
        {
            return ImportFromFileInternal(filePath);
        }

        /// <summary>
        /// Полный импорт справочника: удаляет все несвои заклинания и импортирует заново.
        /// </summary>
        public static int ForceImportSRD()
        {
            Debug.WriteLine($"[SpellJsonImporter] === ПОЛНЫЙ ИМПОРТ СПРАВОЧНИКА ===");
            var file = Path.Combine(DataDir, "spells.json");
            if (!File.Exists(file))
            {
                Debug.WriteLine($"[SpellJsonImporter] Файл не найден: {file}");
                return 0;
            }
            try
            {
                var json = File.ReadAllText(file);
                var entries = JsonSerializer.Deserialize<List<SrdSpellEntry>>(json);
                if (entries == null || entries.Count == 0)
                {
                    Debug.WriteLine($"[SpellJsonImporter] JSON пуст или невалиден");
                    return 0;
                }
                Debug.WriteLine($"[SpellJsonImporter] Разобрано {entries.Count} записей");

                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var deleteCmd = new SqliteCommand(
                    "DELETE FROM SpellReference WHERE source != 'custom' AND source != ''", conn);
                int deleted = deleteCmd.ExecuteNonQuery();
                Debug.WriteLine($"[SpellJsonImporter] Удалено старых записей: {deleted}");

                int added = 0;
                using var tx = conn.BeginTransaction();
                using var cmd = new SqliteCommand(@"
                    INSERT OR IGNORE INTO SpellReference 
                    (name, level, school, casting_time, range, components, duration, 
                     description, at_higher_levels, classes, concentration, ritual, 
                     source, material, page)
                    VALUES (@n, @l, @sch, @c, @r, @co, @du, @desc, @hi, @cl, @conc, @rit, @src, @mat, @pg)", conn, tx);

                foreach (var entry in entries)
                {
                    var row = SpellRow.From(entry);

                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@n", row.Name);
                    cmd.Parameters.AddWithValue("@l", row.Level);
                    cmd.Parameters.AddWithValue("@sch", row.School);
                    cmd.Parameters.AddWithValue("@c", row.CastingTime);
                    cmd.Parameters.AddWithValue("@r", row.Range);
                    cmd.Parameters.AddWithValue("@co", row.Components);
                    cmd.Parameters.AddWithValue("@du", row.Duration);
                    cmd.Parameters.AddWithValue("@desc", row.Description);
                    cmd.Parameters.AddWithValue("@hi", row.AtHigherLevels);
                    cmd.Parameters.AddWithValue("@cl", row.Classes);
                    cmd.Parameters.AddWithValue("@conc", row.Concentration ? 1 : 0);
                    cmd.Parameters.AddWithValue("@rit", row.Ritual ? 1 : 0);
                    cmd.Parameters.AddWithValue("@src", row.Source);
                    cmd.Parameters.AddWithValue("@mat", row.Material);
                    cmd.Parameters.AddWithValue("@pg", "");
                    added += cmd.ExecuteNonQuery();
                }

                tx.Commit();
                Debug.WriteLine($"[SpellJsonImporter] Импортировано {added} заклинаний");
                return added;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellJsonImporter] Ошибка: {ex.Message}");
                return 0;
            }
        }

        private static int ImportFromFileInternal(string filePath)
        {
            Debug.WriteLine($"[SpellJsonImporter] === НАЧАЛО ИМПОРТА ===");
            Debug.WriteLine($"[SpellJsonImporter] Ищем файл: {filePath}");

            if (!File.Exists(filePath))
            {
                Debug.WriteLine($"[SpellJsonImporter] ОШИБКА: Файл НЕ НАЙДЕН.");
                return 0;
            }

            try
            {
                Debug.WriteLine($"[SpellJsonImporter] Чтение файла...");
                var json = File.ReadAllText(filePath);
                Debug.WriteLine($"[SpellJsonImporter] Размер файла: {json.Length} байт.");

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                var entries = JsonSerializer.Deserialize<List<SrdSpellEntry>>(json, options);

                if (entries == null)
                {
                    Debug.WriteLine($"[SpellJsonImporter] ОШИБКА: Десериализация вернула null.");
                    return 0;
                }
                if (entries.Count == 0)
                {
                    Debug.WriteLine($"[SpellJsonImporter] ВНИМАНИЕ: Массив в JSON пуст.");
                    return 0;
                }

                Debug.WriteLine($"[SpellJsonImporter] Успешно разобрано {entries.Count} записей. Запись в БД...");

                int added = 0;
                int skipped = 0;
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();
                using var cmd = new SqliteCommand(@"
                    INSERT OR IGNORE INTO SpellReference 
                    (name, level, school, casting_time, range, components, duration, 
                     description, at_higher_levels, classes, concentration, ritual, 
                     source, material, page)
                    VALUES (@n, @l, @sch, @c, @r, @co, @du, @desc, @hi, @cl, @conc, @rit, @src, @mat, @pg)", conn, tx);

                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    try
                    {
                        var row = SpellRow.From(entry);

                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@n", row.Name);
                        cmd.Parameters.AddWithValue("@l", row.Level);
                        cmd.Parameters.AddWithValue("@sch", row.School);
                        cmd.Parameters.AddWithValue("@c", row.CastingTime);
                        cmd.Parameters.AddWithValue("@r", row.Range);
                        cmd.Parameters.AddWithValue("@co", row.Components);
                        cmd.Parameters.AddWithValue("@du", row.Duration);
                        cmd.Parameters.AddWithValue("@desc", row.Description);
                        cmd.Parameters.AddWithValue("@hi", row.AtHigherLevels);
                        cmd.Parameters.AddWithValue("@cl", row.Classes);
                        cmd.Parameters.AddWithValue("@conc", row.Concentration ? 1 : 0);
                        cmd.Parameters.AddWithValue("@rit", row.Ritual ? 1 : 0);
                        cmd.Parameters.AddWithValue("@src", row.Source);
                        cmd.Parameters.AddWithValue("@mat", row.Material);
                        cmd.Parameters.AddWithValue("@pg", "");

                        added += cmd.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        if (skipped <= 5)
                        {
                            Debug.WriteLine($"[SpellJsonImporter] Пропущена запись #{i} «{entry.Name}»: {ex.Message}");
                        }
                    }
                }

                tx.Commit();
                Debug.WriteLine($"[SpellJsonImporter] УСПЕХ: Импортировано {added} заклинаний, пропущено {skipped}.");
                return added;
            }
            catch (JsonException jex)
            {
                Debug.WriteLine($"[SpellJsonImporter]  ОШИБКА JSON: {jex.Message}");
                Debug.WriteLine($"[SpellJsonImporter] Строка: {jex.LineNumber}, Позиция: {jex.BytePositionInLine}");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellJsonImporter] КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}\n{ex.StackTrace}");
                return 0;
            }
        }
        #endregion

        #region === ПРОВЕРКИ ===
        /// <summary>
        /// Проверяет, импортированы ли заклинания.
        /// </summary>
        public static bool IsImported()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COUNT(1) FROM SpellReference WHERE source != 'custom'", conn);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Возвращает общее количество записей в справочнике.
        /// </summary>
        public static int GetCount()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT COUNT(*) FROM SpellReference", conn);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
            catch
            {
                return 0;
            }
        }
        #endregion

        #region === ПАРСИНГ ПОЛЕЙ ===
        /// <summary>
        /// Парсит уровень заклинания из JsonElement.
        /// Обрабатывает: число (3), строку-число ("3"), "Cantrip" → 0, null → 0.
        /// </summary>
        private static int ParseLevelFromElement(JsonElement? element)
        {
            if (!element.HasValue || element.Value.ValueKind == JsonValueKind.Undefined || element.Value.ValueKind == JsonValueKind.Null)
                return 0;

            var el = element.Value;

            if (el.ValueKind == JsonValueKind.Number)
            {
                if (el.TryGetInt32(out int num)) return num;
                return 0;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(s)) return 0;
                if (int.TryParse(s, out int parsed)) return parsed;
                if (s.Equals("Cantrip", StringComparison.OrdinalIgnoreCase)) return 0;
                return 0;
            }

            return 0;
        }

        /// <summary>
        /// Парсит булево значение из строки.
        /// </summary>
        private static bool ParseBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var lower = value.ToLowerInvariant().Trim();
            return lower == "yes" || lower == "true" || lower == "1" || lower == "да";
        }
        #endregion

        #region === POCO МОДЕЛИ JSON ===
        /// <summary>
        /// POCO для записи из файла справочника.
        /// </summary>
        public sealed class SrdSpellEntry
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("description")]
            public string Description { get; set; } = "";

            [JsonPropertyName("properties")]
            public SpellProperties? Properties { get; set; }

            [JsonPropertyName("publisher")]
            public string Publisher { get; set; } = "";

            [JsonPropertyName("book")]
            public string Book { get; set; } = "";
        }

        /// <summary>
        /// Вложенный объект properties из файла справочника.
        /// ВАЖНО: в файле spells.json дистанция лежит в поле "data-RangeAoe"
        /// (например "150 feet", "Self"), а не в "Range". Из-за этого раньше
        /// дистанция у всех заклинаний оказывалась пустой.
        /// </summary>
        public sealed class SpellProperties
        {
            [JsonPropertyName("Level")]
            public JsonElement? LevelRaw { get; set; }

            [JsonPropertyName("School")]
            public string? School { get; set; }

            [JsonPropertyName("Components")]
            public string? Components { get; set; }

            [JsonPropertyName("Casting Time")]
            public string? CastingTime { get; set; }

            /// <summary>Дистанция/зона из файла справочника.</summary>
            [JsonPropertyName("data-RangeAoe")]
            public JsonElement? RangeAoe { get; set; }

            /// <summary>Числовое значение дистанции (футы), если есть.</summary>
            [JsonPropertyName("data-RangeNum")]
            public JsonElement? RangeNum { get; set; }

            /// <summary>Длительность в виде числа раундов/часов, если есть.</summary>
            [JsonPropertyName("data-DurationNum")]
            public JsonElement? DurationNum { get; set; }

            [JsonPropertyName("Duration")]
            public string? Duration { get; set; }

            /// <summary>Классы могут приходить и как строка, и как массив.</summary>
            [JsonPropertyName("Classes")]
            public JsonElement? ClassesRaw { get; set; }

            [JsonPropertyName("Concentration")]
            public JsonElement? ConcentrationRaw { get; set; }

            [JsonPropertyName("Ritual")]
            public JsonElement? RitualRaw { get; set; }

            [JsonPropertyName("Material")]
            public string? Material { get; set; }
        }
        /// <summary>
        /// Читает JSON целиком и возвращает готовые строки для импорта.
        /// Единая точка разбора: и для файла по умолчанию, и для внешнего JSON.
        /// </summary>
        public static List<SpellRow> LoadRows(string filePath)
        {
            var rows = new List<SpellRow>();
            if (!File.Exists(filePath)) return rows;

            try
            {
                var json = File.ReadAllText(filePath);
                var entries = JsonSerializer.Deserialize<List<SrdSpellEntry>>(json);
                if (entries == null) return rows;

                foreach (var entry in entries)
                {
                    rows.Add(SpellRow.From(entry));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellJsonImporter] LoadRows error: {ex.Message}");
            }

            return rows;
        }

        /// <summary>
        /// Плоская строка для вставки в SpellReference.
        /// Собирается из properties по фактическим ключам JSON, поэтому не ломается,
        /// если книга называет поля иначе (Range / data-RangeAoe, Casting Time / CastingTime).
        /// Internal — чтобы можно было проверить парсер отдельным тестом.
        /// </summary>
        public sealed class SpellRow
        {
            public string Name = "";
            public int Level;
            public string School = "";
            public string CastingTime = "";
            public string Range = "";
            public string Components = "";
            public string Duration = "";
            public string Description = "";
            public string AtHigherLevels = "";
            public string Classes = "";
            public bool Concentration;
            public bool Ritual;
            public string Source = "SRD";
            public string Material = "";

            public static SpellRow From(SrdSpellEntry entry)
            {
                var row = new SpellRow();
                var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

                // Сериализуем properties обратно в JSON и читаем по фактическим ключам книги.
                // Так разбор не зависит от того, как названо поле (Range / data-RangeAoe / Casting Time).
                if (entry?.Properties != null)
                {
                    try
                    {
                        var json = JsonSerializer.SerializeToElement(entry.Properties);
                        if (json.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var p in json.EnumerateObject()) props[p.Name] = p.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SpellJsonImporter] properties не разобраны: {ex.Message}");
                    }
                }

                string Get(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (props.TryGetValue(key, out var el))
                        {
                            var text = GetText(el);
                            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                        }
                    }
                    return "";
                }

                row.Name = (entry?.Name ?? "").Trim();
                row.Level = ParseLevelFromElement(props.TryGetValue("Level", out var lv) ? lv : (JsonElement?)null);

                row.School = Get("School", "school", "SchoolType");
                row.Components = Get("Components", "components");
                row.Material = Get("Material", "material", "Materials");
                row.CastingTime = Get("Casting Time", "CastingTime", "casting_time", "Time");

                // Главное: дистанция в справочнике лежит в data-RangeAoe, а не в Range
                row.Range = Get("data-RangeAoe", "Range", "range", "RangeAoe", "data-Range");
                if (string.IsNullOrWhiteSpace(row.Range))
                {
                    var num = Get("data-RangeNum", "RangeNum");
                    if (!string.IsNullOrWhiteSpace(num)) row.Range = num + " ft";
                }

                row.Duration = Get("Duration", "duration");
                if (string.IsNullOrWhiteSpace(row.Duration))
                {
                    var durNum = Get("data-DurationNum");
                    if (!string.IsNullOrWhiteSpace(durNum)) row.Duration = durNum;
                }

                row.Classes = Get("Classes", "classes", "Class", "SpellList");

                var conc = Get("Concentration", "concentration");
                row.Concentration = IsTruthy(conc) || row.Duration.IndexOf("concentration", StringComparison.OrdinalIgnoreCase) >= 0;

                var rit = Get("Ritual", "ritual");
                row.Ritual = IsTruthy(rit);

                // Описание: убираем «At Higher Levels» в отдельное поле
                var raw = entry?.Description ?? "";
                var (desc, higher) = SplitHigherLevels(raw);
                row.Description = desc;
                row.AtHigherLevels = higher;

                if (!string.IsNullOrWhiteSpace(higher) && !row.Concentration)
                    row.Concentration = row.Duration.IndexOf("concentration", StringComparison.OrdinalIgnoreCase) >= 0;

                row.Source = string.IsNullOrWhiteSpace(entry?.Book) ? "SRD" : entry.Book.Trim();
                if (row.Name.Length == 0) row.Name = "(без названия)";

                return row;
            }
        }

        /// <summary>Текст из JsonElement любого вида (строка, число, массив).</summary>
        private static string GetText(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString() ?? "";
                case JsonValueKind.Number:
                    return element.ToString();
                case JsonValueKind.True:
                    return "yes";
                case JsonValueKind.False:
                    return "no";
                case JsonValueKind.Array:
                    {
                        var parts = new List<string>();
                        foreach (var item in element.EnumerateArray())
                        {
                            var t = GetText(item);
                            if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                        }
                        return string.Join(", ", parts);
                    }
                default:
                    return "";
            }
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim().ToLowerInvariant();
            return v == "yes" || v == "true" || v == "1" || v == "да";
        }

        /// <summary>Отделяет блок «At Higher Levels» от основного описания.</summary>
        private static (string Description, string Higher) SplitHigherLevels(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ("", "");

            var text = raw.Trim();
            int idx = text.IndexOf("At Higher Levels", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return (text, "");

            var desc = text.Substring(0, idx).Trim();
            var higher = text.Substring(idx).Trim();
            // Убираем ведущий заголовок, оставляя суть
            higher = System.Text.RegularExpressions.Regex.Replace(higher, @"^At Higher Levels[\.:]?\s*", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return (desc, higher);
        }

        #endregion
    }
}