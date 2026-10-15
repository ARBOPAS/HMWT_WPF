using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Импорт справочников из открытой базы 5e SRD — той, что лежит в основе
    /// сайта dnd5eapi.co (репозиторий 5e-bits/5e-srd-api).
    ///
    /// База разложена по папкам вида src\2014\en (английский SRD 5.1) и
    /// src\2024\ru (русский SRD 5.2); файлы называются 5e-SRD-*.json.
    /// Разбор рассчитан именно на них, поэтому справочники не лежат внутри
    /// программы: что и на каком языке подключать, решает сам игрок.
    ///
    /// Переносится:
    ///   Equipment   → SrdItem (оружие, броня, снаряжение)
    ///   Magic-Items → SrdItem (магические предметы)
    ///   Spells      → SpellReference
    ///
    /// Набор полей в этих файлах разный. В английской базе 2014 у оружия есть
    /// урон, вес, дистанция и свойства, а в русской 2024 лежит готовое описание
    /// без разобранных полей. Разбор понимает оба вида, поэтому одна и та же
    /// кнопка годится и для английской базы, и для русской.
    ///
    /// Записи помечаются книгой вида «SRD 5.1 · ru»: по ней справочник потом
    /// можно отфильтровать и она же не даёт импорту затереть свои предметы.
    /// </summary>
    public static class SrdDatabaseImporter
    {
        /// <summary>Что получилось после импорта.</summary>
        public sealed class ImportResult
        {
            public int Items;
            public int Spells;
            public string Edition = "";
            public string Language = "";
            public readonly List<string> Skipped = new List<string>();
            public readonly List<string> Files = new List<string>();

            public int Total => Items + Spells;

            /// <summary>Строка для интерфейса: что и откуда перенесено.</summary>
            public string Describe()
            {
                if (Files.Count == 0) return "В папке нет файлов 5e-SRD-*.json.";

                var text = new StringBuilder();
                text.Append($"База: {Edition} ({Language}).\n");
                text.Append($"Предметов: {Items}, заклинаний: {Spells}.\n");
                text.Append("Файлы: " + string.Join(", ", Files) + ".");

                if (Skipped.Count > 0)
                    text.Append("\nПропущено: " + string.Join("; ", Skipped));

                return text.ToString();
            }
        }

        /// <summary>
        /// Читает все файлы 5e-SRD-*.json из папки и переносит их в базу.
        /// Папку выбирает игрок, поэтому отсутствие файла — не ошибка:
        /// импортируется то, что нашлось.
        /// </summary>
        public static ImportResult Import(string folder)
        {
            var result = new ImportResult();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return result;

            result.Edition = GuessEdition(folder);
            result.Language = GuessLanguage(folder);

            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            // Один проход по папке: каждый файл разбирается своим методом
            foreach (var file in Directory.GetFiles(folder, "5e-SRD-*.json"))
            {
                string fileName = Path.GetFileName(file);

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                    var root = doc.RootElement;

                    if (Same(fileName, "5e-SRD-Equipment.json"))
                        result.Items += ImportEquipment(root, conn, tx, result);
                    else if (Same(fileName, "5e-SRD-Magic-Items.json"))
                        result.Items += ImportMagicItems(root, conn, tx, result);
                    else if (Same(fileName, "5e-SRD-Spells.json"))
                        result.Spells += ImportSpells(root, conn, tx, result);
                    else
                        continue;   // остальные файлы базы программе пока не нужны

                    result.Files.Add(fileName);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SRD] {fileName}: {ex.Message}");
                    result.Skipped.Add($"{fileName} — {ex.Message}");
                }
            }

            tx.Commit();
            return result;
        }

        #region === ПРЕДМЕТЫ ===

        private static int ImportEquipment(JsonElement root, SqliteConnection conn,
            SqliteTransaction tx, ImportResult result)
        {
            using var cmd = PrepareItemCommand(conn, tx);
            int count = 0;

            foreach (var el in root.EnumerateArray())
            {
                string name = Text(el, "name");
                if (name.Length == 0) continue;

                string type = CategoryName(el);
                string subtype = Text(el, "category_range");
                if (subtype.Length == 0) subtype = Text(el, "weapon_category");

                // В английской базе 2014 описания нет — собираем его из разобранных
                // полей, иначе карточка предмета осталась бы пустой. В базе 2024
                // описание уже готовым текстом.
                string description = Prose(el, "description", "desc");
                if (description.Length == 0) description = DescribeEquipment(el);

                double weight = Number(el, "weight");
                string damage = DamageOf(el);
                string damageType = DamageTypeOf(el);
                int ac = Whole(el, "armor_class", "base");
                string range = RangeOf(el);
                string properties = NamesOf(el, "properties");

                SaveItem(cmd, name, description, type, subtype, "", weight, damage, damageType,
                         ac, range, properties, false, false, el.GetRawText(), result);
                count++;
            }

            return count;
        }

        private static int ImportMagicItems(JsonElement root, SqliteConnection conn,
            SqliteTransaction tx, ImportResult result)
        {
            using var cmd = PrepareItemCommand(conn, tx);
            int count = 0;

            foreach (var el in root.EnumerateArray())
            {
                string name = Text(el, "name");
                if (name.Length == 0) continue;

                string description = Prose(el, "desc", "description");
                string rarity = NestedText(el, "rarity", "name");
                string type = CategoryName(el);

                SaveItem(cmd, name, description, type, "", rarity, 0, "", "", 0, "",
                         "", RequiresAttunement(el, description), true, el.GetRawText(), result);
                count++;
            }

            return count;
        }

        /// <summary>Описание предмета из его же полей — для базы без готового текста.</summary>
        private static string DescribeEquipment(JsonElement el)
        {
            var parts = new List<string>();

            string damage = DamageOf(el);
            if (damage.Length > 0)
            {
                string type = DamageTypeOf(el);
                parts.Add(type.Length > 0 ? $"Урон: {damage} ({type})" : $"Урон: {damage}");
            }

            int ac = Whole(el, "armor_class", "base");
            if (ac > 0)
            {
                int maxBonus = Whole(el, "armor_class", "max_bonus");
                parts.Add(maxBonus > 0 ? $"Класс брони: {ac} (макс. Ловк. +{maxBonus})" : $"Класс брони: {ac}");
            }

            string properties = NamesOf(el, "properties");
            if (properties.Length > 0) parts.Add("Свойства: " + properties);

            double weight = Number(el, "weight");
            if (weight > 0) parts.Add($"Вес: {weight:0.###}");

            string cost = CostOf(el);
            if (cost.Length > 0) parts.Add("Цена: " + cost);

            return string.Join("\n", parts);
        }

        private static string CostOf(JsonElement el)
        {
            if (!el.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
                return "";

            double quantity = Number(cost, "quantity");
            string unit = Text(cost, "unit");
            if (quantity <= 0 || unit.Length == 0) return "";

            return $"{quantity:0.##} {unit}";
        }

        private static string DamageOf(JsonElement el)
        {
            string dice = NestedText(el, "damage", "damage_dice");

            // Двуручный хват — часть описания оружия, иначе «1d8» теряет вторую цифру
            string twoHanded = NestedText(el, "two_handed_damage", "damage_dice");
            if (dice.Length > 0 && twoHanded.Length > 0 && twoHanded != dice)
                return $"{dice} ({twoHanded} двумя руками)";

            return dice;
        }

        private static string DamageTypeOf(JsonElement el)
            => NestedText(el, "damage", "damage_type", "name");

        private static string RangeOf(JsonElement el)
        {
            string weaponRange = Text(el, "weapon_range");
            if (!el.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object)
                return weaponRange;

            int normal = Whole(range, "normal");
            int far = Whole(range, "long");
            if (normal <= 0) return weaponRange;

            string distance = far > 0 ? $"{normal}/{far} футов" : $"{normal} футов";
            return weaponRange.Length > 0 ? $"{weaponRange}, {distance}" : distance;
        }

        /// <summary>
        /// Настройка на предмет. В базе 2024 это отдельное поле attunement,
        /// в базе 2014 — слова в первой строке описания.
        /// </summary>
        private static bool RequiresAttunement(JsonElement el, string description)
        {
            foreach (var property in new[] { "attunement", "requires_attunement" })
            {
                if (!el.TryGetProperty(property, out var flag)) continue;
                if (flag.ValueKind == JsonValueKind.True) return true;
                if (flag.ValueKind == JsonValueKind.String && (flag.GetString() ?? "").Length > 0) return true;
            }

            var first = Lines(el, "desc");
            string text = ((first.Count > 0 ? first[0] : "") + " " + description).ToLowerInvariant();

            return text.Contains("requires attunement") || text.Contains("требует настройки");
        }

        #endregion

        #region === ЗАКЛИНАНИЯ ===

        private static int ImportSpells(JsonElement root, SqliteConnection conn,
            SqliteTransaction tx, ImportResult result)
        {
            string book = Book(result);

            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = @"
                UPDATE SpellReference SET
                    level=@level, school=@school, casting_time=@cast, range=@range,
                    components=@comp, duration=@dur, description=@desc, at_higher_levels=@higher,
                    classes=@classes, ritual=@ritual, concentration=@conc, source=@src,
                    material=@material, source_json=@raw, language=@lang
                WHERE name=@name AND language=@lang";

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
                INSERT INTO SpellReference
                    (name, level, school, casting_time, range, components, duration, description,
                     at_higher_levels, classes, ritual, concentration, source, material,
                     source_json, language)
                VALUES
                    (@name, @level, @school, @cast, @range, @comp, @dur, @desc,
                     @higher, @classes, @ritual, @conc, @src, @material, @raw, @lang)";

            int count = 0;
            foreach (var el in root.EnumerateArray())
            {
                string name = Text(el, "name");
                if (name.Length == 0) continue;

                int level = (int)Number(el, "level");
                string school = NestedText(el, "school", "name");
                string components = ComponentsOf(el);
                string material = Text(el, "material");
                string description = Prose(el, "desc", "description");
                string higher = Prose(el, "higher_level", "at_higher_levels");
                string classes = NamesOf(el, "classes");

                foreach (var cmd in new[] { update, insert })
                {
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@level", level);
                    cmd.Parameters.AddWithValue("@school", school);
                    cmd.Parameters.AddWithValue("@cast", Text(el, "casting_time"));
                    cmd.Parameters.AddWithValue("@range", Text(el, "range"));
                    cmd.Parameters.AddWithValue("@comp", components);
                    cmd.Parameters.AddWithValue("@dur", Text(el, "duration"));
                    cmd.Parameters.AddWithValue("@desc", description);
                    cmd.Parameters.AddWithValue("@higher", higher);
                    cmd.Parameters.AddWithValue("@classes", classes);
                    cmd.Parameters.AddWithValue("@ritual", Bool(el, "ritual") ? 1 : 0);
                    cmd.Parameters.AddWithValue("@conc", Bool(el, "concentration") ? 1 : 0);
                    cmd.Parameters.AddWithValue("@src", result.Edition);
                    cmd.Parameters.AddWithValue("@material", material);
                    cmd.Parameters.AddWithValue("@raw", el.GetRawText());
                    cmd.Parameters.AddWithValue("@lang", result.Language);
                }

                // Сначала пробуем обновить: повторный импорт не должен плодить
                // дубли, а уникального ключа у таблицы заклинаний нет
                int changed = update.ExecuteNonQuery();
                if (changed == 0) insert.ExecuteNonQuery();

                count++;
            }

            return count;
        }

        /// <summary>Компоненты заклинания: буквы и, если есть, сам материал.</summary>
        private static string ComponentsOf(JsonElement el)
        {
            string letters = NamesOf(el, "components");
            string material = Text(el, "material");

            if (letters.Length == 0) return material.Length > 0 ? material : "";
            return material.Length > 0 ? $"{letters} ({material})" : letters;
        }

        #endregion

        #region === ЗАПИСЬ В БАЗУ ===

        private static SqliteCommand PrepareItemCommand(SqliteConnection conn, SqliteTransaction tx)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
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
                    raw_properties=excluded.raw_properties";
            return cmd;
        }

        private static void SaveItem(SqliteCommand cmd, string name, string description,
            string type, string subtype, string rarity, double weight, string damage,
            string damageType, int ac, string range, string properties, bool attunement,
            bool magic, string raw, ImportResult result)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@name", name.Trim());
            cmd.Parameters.AddWithValue("@desc", description ?? "");
            cmd.Parameters.AddWithValue("@type", type ?? "");
            cmd.Parameters.AddWithValue("@sub", subtype ?? "");
            cmd.Parameters.AddWithValue("@rar", rarity ?? "");
            cmd.Parameters.AddWithValue("@weight", weight);
            cmd.Parameters.AddWithValue("@dmg", damage ?? "");
            cmd.Parameters.AddWithValue("@dmgType", damageType ?? "");
            cmd.Parameters.AddWithValue("@ac", ac);
            cmd.Parameters.AddWithValue("@range", range ?? "");
            cmd.Parameters.AddWithValue("@props", properties ?? "");
            cmd.Parameters.AddWithValue("@att", attunement ? 1 : 0);
            cmd.Parameters.AddWithValue("@magic", magic ? 1 : 0);
            cmd.Parameters.AddWithValue("@src", result.Edition);
            cmd.Parameters.AddWithValue("@book", Book(result));
            cmd.Parameters.AddWithValue("@raw", string.IsNullOrEmpty(raw) ? "{}" : raw);
            cmd.ExecuteNonQuery();
        }

        #endregion

        #region === ЧТЕНИЕ JSON ===

        private static string Book(ImportResult result)
        {
            string language = result.Language.Length > 0 ? $" · {result.Language}" : "";
            return result.Edition + language;
        }

        private static bool Same(string left, string right)
            => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        /// <summary>Название категории предмета: «Weapon», «Armor», «Снаряжение»…</summary>
        private static string CategoryName(JsonElement el)
        {
            string single = NestedText(el, "equipment_category", "name");
            if (single.Length > 0) return single;

            if (el.TryGetProperty("equipment_categories", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var category in list.EnumerateArray())
                {
                    string name = Text(category, "name");
                    if (name.Length > 0) return name;
                }

            return "";
        }

        private static string Text(JsonElement el, string property)
        {
            if (el.ValueKind != JsonValueKind.Object) return "";
            if (!el.TryGetProperty(property, out var value)) return "";

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                _ => ""
            };
        }

        private static string NestedText(JsonElement el, params string[] path)
        {
            var current = el;
            foreach (var step in path)
            {
                if (current.ValueKind != JsonValueKind.Object) return "";
                if (!current.TryGetProperty(step, out var next)) return "";
                current = next;
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : "";
        }

        private static double Number(JsonElement el, params string[] path)
        {
            var current = el;
            foreach (var step in path)
            {
                if (current.ValueKind != JsonValueKind.Object) return 0;
                if (!current.TryGetProperty(step, out var next)) return 0;
                current = next;
            }

            if (current.ValueKind == JsonValueKind.Number && current.TryGetDouble(out double value))
                return value;

            // В части записей вес и цена записаны строкой, например «1.5»
            if (current.ValueKind == JsonValueKind.String)
            {
                string text = current.GetString() ?? "";
                int dot = text.IndexOf('.');
                if (dot > 0) text = text.Substring(0, dot);
                if (double.TryParse(text, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                    return parsed;
            }

            return 0;
        }

        /// <summary>Целое значение по пути: класс брони, дистанция, уровень.</summary>
        private static int Whole(JsonElement el, params string[] path)
            => (int)Math.Round(Number(el, path), MidpointRounding.AwayFromZero);

        private static bool Bool(JsonElement el, string property)
        {
            if (!el.TryGetProperty(property, out var value)) return false;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.String)
                return string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static List<string> Lines(JsonElement el, string property)
        {
            var lines = new List<string>();
            if (el.ValueKind != JsonValueKind.Object) return lines;
            if (!el.TryGetProperty(property, out var value)) return lines;

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) lines.Add(item.GetString() ?? "");
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                lines.Add(value.GetString() ?? "");
            }

            return lines;
        }

        /// <summary>
        /// Описание из файла базы.
        ///
        /// Издания разошлись в названиях полей: в английской базе 2014 описание
        /// лежит массивом строк в desc, в базе 2024 — одной строкой в description.
        /// Проверяем оба, иначе половина записей приходит без текста.
        /// </summary>
        private static string Prose(JsonElement el, params string[] properties)
        {
            foreach (var property in properties)
            {
                var lines = Lines(el, property);
                if (lines.Count == 0) continue;

                string text = string.Join("\n\n", lines).Trim();
                if (text.Length > 0) return text;
            }

            return "";
        }

        /// <summary>Имена из массива объектов: свойства оружия, классы заклинания.</summary>
        private static string NamesOf(JsonElement el, string property)
        {
            if (el.ValueKind != JsonValueKind.Object) return "";
            if (!el.TryGetProperty(property, out var value)) return "";
            if (value.ValueKind != JsonValueKind.Array) return "";

            var names = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    names.Add(item.GetString() ?? "");
                    continue;
                }

                string name = Text(item, "name");
                if (name.Length > 0) names.Add(name);
            }

            names.RemoveAll(string.IsNullOrWhiteSpace);
            return string.Join(", ", names);
        }

        #endregion

        #region === ОПРЕДЕЛЕНИЕ ИЗДАНИЯ И ЯЗЫКА ===

        /// <summary>
        /// Издание по пути: база раскладывает файлы по папкам 2014 и 2024.
        /// Если папка называется иначе, оставляем пометку SRD, чтобы записи
        /// всё равно были отличимы от своих предметов.
        /// </summary>
        public static string GuessEdition(string folder)
        {
            var parts = SplitPath(folder);
            foreach (var part in parts)
            {
                if (part == "2014") return "SRD 5.1";
                if (part == "2024") return "SRD 5.2";
            }

            return "SRD";
        }

        /// <summary>Язык по последней папке пути: en, ru, fr-FR, pt-BR.</summary>
        public static string GuessLanguage(string folder)
        {
            var parts = SplitPath(folder);

            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string part = parts[i];
                if (part.Length is 2 or 5 && part.All(char.IsLetter))
                    return part.ToLowerInvariant();
            }

            return "";
        }

        private static string[] SplitPath(string path)
        {
            try { return path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries); }
            catch { return new string[0]; }
        }

        #endregion
    }
}
