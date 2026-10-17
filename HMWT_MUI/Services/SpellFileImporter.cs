using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Импорт справочника заклинаний из файла базы заклинаний.
    ///
    /// Файл — обычный JSON (встречается с расширением .dtn), внутри ключ allSpells,
    /// а в нём на каждое заклинание два набора полей: en и ru.
    ///
    /// Русский набор берём за основу: у него переведены школа, время, дистанция,
    /// компоненты и длительность. Английское название дописываем в скобках —
    /// так работает поиск по обоим языкам, и видно, откуда строка. Английский
    /// текст кладём в source_json: он пригодится при выгрузке в пак.
    ///
    /// Повторный импорт того же файла ничего не дублирует: строки с таким же
    /// названием пропускаются.
    /// </summary>
    public static class SpellFileImporter
    {
        // Колонку is_modified не трогаем: в старой базе она есть, в новой — нет,
        // а импорт должен работать на обеих.
        private const string InsertSql = @"
            INSERT INTO SpellReference (
                name, level, school, casting_time, [range], components, duration,
                description, at_higher_levels, classes, ritual, concentration,
                source, material, page, language, source_json)
            VALUES (
                @name, @level, @school, @casting, @range, @components, @duration,
                @description, '', '', @ritual, @concentration,
                @source, @material, '', 'ru', @source_json)";

        /// <summary>
        /// Читает файл и добавляет заклинания в справочник.
        /// Возвращает число добавленных; skipped — сколько уже было.
        /// </summary>
        public static int Import(string path, out int skipped, out string error)
        {
            skipped = 0;
            error = "";

            int added = 0;

            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    error = "Файл не найден.";
                    return 0;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("allSpells", out JsonElement spells) ||
                    spells.ValueKind != JsonValueKind.Array)
                {
                    error = "Это не файл заклинаний: внутри нет списка allSpells.";
                    return 0;
                }

                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using (var cmd = new SqliteCommand("SELECT name FROM SpellReference", conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read()) existing.Add((reader[0] as string ?? "").Trim());
                    }

                    using var tx = conn.BeginTransaction();
                    using var insert = new SqliteCommand(InsertSql, conn, tx);

                    foreach (JsonElement item in spells.EnumerateArray())
                    {
                        JsonElement ru = default;
                        JsonElement en = default;
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("ru", out JsonElement r) && r.ValueKind == JsonValueKind.Object) ru = r;
                            if (item.TryGetProperty("en", out JsonElement e) && e.ValueKind == JsonValueKind.Object) en = e;
                        }

                        string nameRu = Text(ru, "name");
                        string nameEn = Text(en, "name");
                        if (nameRu.Length == 0) nameRu = nameEn;
                        if (nameRu.Length == 0) { skipped++; continue; }

                        // «Кислотные брызги (Acid Splash)» — искать можно по любому языку
                        string name = nameRu;
                        if (nameEn.Length > 0 &&
                            nameRu.IndexOf(nameEn, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            name = nameRu + " (" + nameEn + ")";
                        }

                        if (!existing.Add(name))
                        {
                            skipped++;
                            continue;
                        }

                        int level = Number(ru, "level");
                        if (level < 0) level = Number(en, "level");
                        if (level < 0) level = 0;

                        string duration = First(Text(ru, "duration"), Text(en, "duration"));
                        bool concentration = duration.IndexOf("конц", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool ritual = Text(ru, "ritual").Length > 0 || Text(en, "ritual").Length > 0;

                        insert.Parameters.Clear();
                        insert.Parameters.AddWithValue("@name", name);
                        insert.Parameters.AddWithValue("@level", level);
                        insert.Parameters.AddWithValue("@school", First(Text(ru, "school"), Text(en, "school")));
                        insert.Parameters.AddWithValue("@casting", First(Text(ru, "castingTime"), Text(en, "castingTime")));
                        insert.Parameters.AddWithValue("@range", First(Text(ru, "range"), Text(en, "range")));
                        insert.Parameters.AddWithValue("@components", First(Text(ru, "components"), Text(en, "components")));
                        insert.Parameters.AddWithValue("@duration", duration);
                        insert.Parameters.AddWithValue("@description", First(Text(ru, "text"), Text(en, "text")));
                        insert.Parameters.AddWithValue("@ritual", ritual ? 1 : 0);
                        insert.Parameters.AddWithValue("@concentration", concentration ? 1 : 0);
                        insert.Parameters.AddWithValue("@source", First(Text(ru, "source"), Text(en, "source")));
                        insert.Parameters.AddWithValue("@material", First(Text(ru, "materials"), Text(en, "materials")));
                        insert.Parameters.AddWithValue("@source_json", BuildSourceJson(nameEn, en));

                        insert.ExecuteNonQuery();
                        added++;
                    }

                    tx.Commit();
                }

                return added;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellFileImporter] {ex}");
                error = ex.Message;
                return added;
            }
        }

        /// <summary>Строковое поле внутри объекта; "" если поля нет.</summary>
        private static string Text(JsonElement obj, string property)
        {
            try
            {
                if (obj.ValueKind != JsonValueKind.Object) return "";
                if (!obj.TryGetProperty(property, out JsonElement value)) return "";

                if (value.ValueKind == JsonValueKind.String) return (value.GetString() ?? "").Trim();
                if (value.ValueKind == JsonValueKind.Number) return value.ToString();
                if (value.ValueKind == JsonValueKind.True) return "1";
                return "";
            }
            catch { return ""; }
        }

        /// <summary>Числовое поле; -1 если поля нет.</summary>
        private static int Number(JsonElement obj, string property)
        {
            string text = Text(obj, property);
            if (int.TryParse(text, out int value)) return value;
            return -1;
        }

        private static string First(string main, string fallback) =>
            string.IsNullOrWhiteSpace(main) ? (fallback ?? "") : main;

        /// <summary>Английский оригинал кладём в source_json — пригодится в паке.</summary>
        private static string BuildSourceJson(string nameEn, JsonElement en)
        {
            try
            {
                if (en.ValueKind != JsonValueKind.Object) return "";

                var data = new Dictionary<string, string>
                {
                    ["name"] = nameEn,
                    ["school"] = Text(en, "school"),
                    ["castingTime"] = Text(en, "castingTime"),
                    ["range"] = Text(en, "range"),
                    ["components"] = Text(en, "components"),
                    ["duration"] = Text(en, "duration"),
                    ["text"] = Text(en, "text")
                };

                return JsonSerializer.Serialize(data);
            }
            catch { return ""; }
        }
    }
}
