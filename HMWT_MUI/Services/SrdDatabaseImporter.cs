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
            public int References;
            public int Backgrounds;
            public string Edition = "";
            public string Language = "";

            /// <summary>Импорт был дополнением: совпадающие записи пропускались.</summary>
            public bool FillGaps;

            public readonly List<string> Skipped = new List<string>();
            public readonly List<string> Files = new List<string>();

            public int Total => Items + Spells + References + Backgrounds;

            /// <summary>Строка для интерфейса: что и откуда перенесено.</summary>
            public string Describe()
            {
                if (Files.Count == 0) return "В папке нет файлов 5e-SRD-*.json.";

                var text = new StringBuilder();
                text.Append($"База: {Edition} ({Language}).\n");
                text.Append($"Предметов: {Items}, заклинаний: {Spells}, статей справочника: {References}, " +
                            $"предысторий: {Backgrounds}.\n");

                if (FillGaps && Total == 0)
                    text.Append("Всё это уже есть в базе — добавлено ничего не было.\n");
                else if (FillGaps)
                    text.Append("Это только то, чего не хватало в первой базе.\n");

                text.Append("Файлы: " + string.Join(", ", Files) + ".");

                if (Skipped.Count > 0)
                    text.Append("\nПропущено: " + string.Join("; ", Skipped));

                return text.ToString();
            }
        }

        /// <summary>
        /// Файлы, которые ложатся в общую таблицу справочника: состояния, правила,
        /// навыки, языки, школы магии, расы, классы, черты и прочие списки.
        ///
        /// Раздел берётся из имени файла, поэтому новые файлы базы подхватываются
        /// простым добавлением имени сюда.
        /// </summary>
        private static readonly HashSet<string> ReferenceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "5e-SRD-Ability-Scores.json",
            "5e-SRD-Alignments.json",
            "5e-SRD-Classes.json",
            "5e-SRD-Conditions.json",
            "5e-SRD-Damage-Types.json",
            "5e-SRD-Equipment-Categories.json",
            "5e-SRD-Feats.json",
            "5e-SRD-Features.json",
            "5e-SRD-Languages.json",
            "5e-SRD-Levels.json",
            "5e-SRD-Magic-Schools.json",
            "5e-SRD-Poisons.json",
            "5e-SRD-Proficiencies.json",
            "5e-SRD-Races.json",
            "5e-SRD-Rules.json",
            "5e-SRD-Skills.json",
            "5e-SRD-Species.json",
            "5e-SRD-Subclasses.json",
            "5e-SRD-Subraces.json",
            "5e-SRD-Subspecies.json",
            "5e-SRD-Traits.json",
            "5e-SRD-Weapon-Mastery-Properties.json",
            "5e-SRD-Weapon-Properties.json"
        };

        /// <summary>
        /// Читает все файлы 5e-SRD-*.json из папки и переносит их в базу.
        /// Папку выбирает игрок, поэтому отсутствие файла — не ошибка:
        /// импортируется то, что нашлось.
        ///
        /// <paramref name="fillGaps"/> включает дополнение: записи, которые уже
        /// есть (по ключу index), пропускаются. Так вторая база закрывает пробелы
        /// первой — перевод SRD сделан не полностью, и без этого половина списка
        /// осталась бы пустой.
        /// </summary>
        public static ImportResult Import(string folder, bool fillGaps = false)
        {
            var result = new ImportResult();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return result;

            result.Edition = GuessEdition(folder);
            result.Language = GuessLanguage(folder);
            result.FillGaps = fillGaps;

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
                        result.Items += ImportEquipment(root, conn, tx, result, fillGaps);
                    else if (Same(fileName, "5e-SRD-Magic-Items.json"))
                        result.Items += ImportMagicItems(root, conn, tx, result, fillGaps);
                    else if (Same(fileName, "5e-SRD-Spells.json"))
                        result.Spells += ImportSpells(root, conn, tx, result, fillGaps);
                    else if (Same(fileName, "5e-SRD-Backgrounds.json"))
                        result.Backgrounds += ImportBackgrounds(root, conn, tx, result);
                    else if (ReferenceFiles.Contains(fileName))
                        result.References += ImportReference(root, conn, tx, fileName, result, fillGaps);
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

        /// <summary>
        /// Есть ли уже запись с таким ключом. Нужно при дополнении второй базой:
        /// переведено не всё, и английские файлы должны закрывать только пробелы.
        /// </summary>
        private static bool IndexExists(SqliteConnection conn, SqliteTransaction tx,
            string table, string index)
        {
            if (string.IsNullOrEmpty(index)) return false;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT COUNT(1) FROM {table} WHERE srd_index = @i";
            cmd.Parameters.AddWithValue("@i", index);

            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        #region === ПРЕДМЕТЫ ===

        private static int ImportEquipment(JsonElement root, SqliteConnection conn,
            SqliteTransaction tx, ImportResult result, bool fillGaps)
        {
            using var cmd = PrepareItemCommand(conn, tx);
            int count = 0;

            foreach (var el in root.EnumerateArray())
            {
                string name = Text(el, "name");
                if (name.Length == 0) continue;

                if (fillGaps && IndexExists(conn, tx, "SrdItem", Text(el, "index"))) continue;

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
                         ac, range, properties, false, false, el.GetRawText(), result,
                         Text(el, "index"));
                count++;
            }

            return count;
        }

        private static int ImportMagicItems(JsonElement root, SqliteConnection conn,
            SqliteTransaction tx, ImportResult result, bool fillGaps)
        {
            using var cmd = PrepareItemCommand(conn, tx);
            int count = 0;

            foreach (var el in root.EnumerateArray())
            {
                string name = Text(el, "name");
                if (name.Length == 0) continue;

                if (fillGaps && IndexExists(conn, tx, "SrdItem", Text(el, "index"))) continue;

                string description = Prose(el, "desc", "description");
                string rarity = NestedText(el, "rarity", "name");
                string type = CategoryName(el);

                SaveItem(cmd, name, description, type, "", rarity, 0, "", "", 0, "",
                         "", RequiresAttunement(el, description), true, el.GetRawText(), result,
                         Text(el, "index"));
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
            SqliteTransaction tx, ImportResult result, bool fillGaps)
        {
            string book = Book(result);

            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = @"
                UPDATE SpellReference SET
                    level=@level, school=@school, casting_time=@cast, range=@range,
                    components=@comp, duration=@dur, description=@desc, at_higher_levels=@higher,
                    classes=@classes, ritual=@ritual, concentration=@conc, source=@src,
                    material=@material, source_json=@raw, language=@lang, srd_index=@index
                WHERE name=@name AND language=@lang";

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
                INSERT INTO SpellReference
                    (name, level, school, casting_time, range, components, duration, description,
                     at_higher_levels, classes, ritual, concentration, source, material,
                     source_json, language, srd_index)
                VALUES
                    (@name, @level, @school, @cast, @range, @comp, @dur, @desc,
                     @higher, @classes, @ritual, @conc, @src, @material, @raw, @lang, @index)";

            int count = 0;
            foreach (var el in root.EnumerateArray())
            {
                string name = Text(el, "name");
                if (name.Length == 0) continue;

                if (fillGaps && IndexExists(conn, tx, "SpellReference", Text(el, "index"))) continue;

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
                    cmd.Parameters.AddWithValue("@index", Text(el, "index"));
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

        #region === ПРЕДЫСТОРИИ ===

        /// <summary>
        /// Предыстории идут не в общий справочник, а в свою таблицу: их показывает
        /// выбор предыстории в листе персонажа.
        ///
        /// Записи с уже существующим названием не трогаем. Встроенные предыстории
        /// игрок мог править под свою игру, и импорт не должен затирать его текст —
        /// добавляем только то, чего в списке ещё нет.
        /// </summary>
        private static int ImportBackgrounds(JsonElement root, SqliteConnection conn,
            SqliteTransaction tx, ImportResult result)
        {
            using var exists = conn.CreateCommand();
            exists.Transaction = tx;
            exists.CommandText = "SELECT COUNT(1) FROM BackgroundReference WHERE name = @n";

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
                INSERT INTO BackgroundReference
                    (name, skill_proficiencies, tool_proficiencies, languages,
                     equipment, feature, description, is_custom)
                VALUES (@n, @sk, @tl, @lg, @eq, @ft, @ds, 0)";

            int count = 0;
            foreach (var el in root.EnumerateArray())
            {
                string name = Text(el, "name");
                if (name.Length == 0) continue;

                exists.Parameters.Clear();
                exists.Parameters.AddWithValue("@n", name.Trim());
                if (Convert.ToInt32(exists.ExecuteScalar()) > 0) continue;

                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("@n", name.Trim());
                insert.Parameters.AddWithValue("@sk", MarkedNames(el, "Skill:"));
                insert.Parameters.AddWithValue("@tl", MarkedNames(el, "Tool:"));
                insert.Parameters.AddWithValue("@lg", MarkedNames(el, "Language:"));
                insert.Parameters.AddWithValue("@eq", StartingEquipmentOf(el));
                insert.Parameters.AddWithValue("@ft", FeatureOf(el));
                insert.Parameters.AddWithValue("@ds", Prose(el, "desc", "description"));
                insert.ExecuteNonQuery();
                count++;
            }

            return count;
        }

        /// <summary>
        /// Владения предыстории. Две выгрузки пишут их по-разному:
        ///
        /// 2014 — список starting_proficiencies, где вид владения стоит приставкой
        /// в названии: «Skill: Insight», «Tool: Calligrapher's supplies».
        /// 2024 — список proficiencies, где вид виден по ключу записи: «skill-insight»,
        /// «tool-calligraphers-supplies», а название идёт без приставки.
        ///
        /// Раньше читался только первый вариант, поэтому у предысторий из базы 2024
        /// навыков не оказывалось вовсе.
        /// </summary>
        private static string MarkedNames(JsonElement el, string prefix)
        {
            string kind = prefix.TrimEnd(':').ToLowerInvariant();
            var names = new List<string>();

            // Старая выгрузка: вид владения приписан к названию
            if (el.TryGetProperty("starting_proficiencies", out var marked) &&
                marked.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in marked.EnumerateArray())
                {
                    string name = Text(item, "name");
                    if (name.Length == 0) continue;
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    names.Add(CleanProficiencyName(name.Substring(prefix.Length)));
                }
            }

            // Новая выгрузка: вид владения виден по ключу записи
            if (el.TryGetProperty("proficiencies", out var keyed) &&
                keyed.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in keyed.EnumerateArray())
                {
                    string key = Text(item, "index");
                    if (key.Length == 0 || !key.StartsWith(kind + "-", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = CleanProficiencyName(Text(item, "name"));
                    if (name.Length > 0) names.Add(name);
                }
            }

            var unique = names.Where(n => n.Length > 0).Distinct().ToList();

            // Навыки приводим к тому написанию, что и на листе персонажа: иначе
            // в списке предысторий рядом стояли бы «Insight» и «Проницательность»
            if (kind == "skill")
            {
                return string.Join(", ", unique.Select(SkillCatalog.RussianOf));
            }

            return string.Join(", ", unique);
        }

        /// <summary>Убирает пояснения в скобках: «Calligrapher's supplies (pen)».</summary>
        private static string CleanProficiencyName(string name)
        {
            string value = (name ?? "").Trim();
            int bracket = value.IndexOf('(');
            if (bracket > 0) value = value.Substring(0, bracket).Trim();
            return value;
        }

        /// <summary>Умение предыстории: название и текст одним значением.</summary>
        private static string FeatureOf(JsonElement el)
        {
            if (!el.TryGetProperty("feature", out var feature) || feature.ValueKind != JsonValueKind.Object)
                return "";

            string name = Text(feature, "name");
            string text = string.Join(" ", Lines(feature, "desc"));

            if (name.Length == 0) return text;
            return text.Length == 0 ? name : $"{name}. {text}";
        }

        /// <summary>Снаряжение предыстории: предметы с количеством и золото.</summary>
        private static string StartingEquipmentOf(JsonElement el)
        {
            var parts = new List<string>();

            if (el.TryGetProperty("starting_equipment", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                {
                    string name = NestedText(item, "item", "name");
                    if (name.Length == 0) continue;

                    int quantity = Whole(item, "quantity");
                    parts.Add(quantity > 1 ? $"{name} ×{quantity}" : name);
                }

            int gold = Whole(el, "starting_gold");
            if (gold > 0) parts.Add($"{gold} зм");

            return string.Join(", ", parts);
        }

        #endregion

        #region === СПРАВОЧНИК ===

        /// <summary>
        /// Статьи справочника: состояния, правила, навыки, языки, школы магии,
        /// расы, виды, классы, черты и остальные списки базы.
        ///
        /// Все они ложатся в одну таблицу SrdReference, а раздел хранится в поле
        /// category — по нему вкладка «Справка» строит подразделы, и оттуда же
        /// карточка персонажа берёт расы и классы для выпадающих списков.
        /// </summary>
        private static int ImportReference(JsonElement root, SqliteConnection conn,
            SqliteTransaction tx, string fileName, ImportResult result, bool fillGaps)
        {
            string category = Path.GetFileNameWithoutExtension(fileName).Replace("5e-SRD-", "");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO SrdReference (category, name, description, properties, publisher, book, srd_index)
                VALUES (@cat, @name, @desc, @props, @pub, @book, @index)
                ON CONFLICT(category, name, book) DO UPDATE SET
                    description=excluded.description,
                    properties=excluded.properties,
                    publisher=excluded.publisher,
                    srd_index=excluded.srd_index";

            // Названия, уже занятые в этом файле: пара «название и книга» в таблице
            // уникальна, и без разведения повторы схлопнулись бы в одну строку
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int count = 0;
            foreach (var el in root.EnumerateArray())
            {
                string name = QualifiedName(el);
                if (name.Length == 0) continue;

                // Совпадение всё же возможно (одно умение на двух уровнях класса):
                // в этом случае дописываем ключ записи — он уникален всегда
                if (!used.Add(name))
                {
                    string key = Text(el, "index");
                    if (key.Length > 0)
                    {
                        name = $"{name} ({key})";
                        used.Add(name);
                    }
                }

                if (fillGaps && IndexExists(conn, tx, "SrdReference", Text(el, "index"))) continue;

                string description = Prose(el, "desc", "description");
                if (description.Length == 0) description = Summarize(el);

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@cat", category);
                cmd.Parameters.AddWithValue("@name", name.Trim());
                cmd.Parameters.AddWithValue("@desc", description);
                cmd.Parameters.AddWithValue("@props", el.GetRawText());
                // Источник подписан прямо в записи: тексты SRD выпущены под
                // CC-BY-4.0, и авторство должно быть видно там, где показан текст
                cmd.Parameters.AddWithValue("@pub", "Wizards of the Coast (SRD, CC-BY-4.0)");
                cmd.Parameters.AddWithValue("@book", Book(result));
                cmd.Parameters.AddWithValue("@index", Text(el, "index"));
                cmd.ExecuteNonQuery();
                count++;
            }

            return count;
        }

        /// <summary>
        /// Название статьи с уточнением владельца.
        ///
        /// В базе одни и те же названия повторяются: «Защита без доспехов» есть
        /// и у варвара, и у монаха, а у уровней записи класса и его подкласса
        /// называются одинаково («Варвар 3»). Пара «название и книга» в таблице
        /// справочника уникальна, поэтому без уточнения часть записей схлопнулась бы.
        /// </summary>
        private static string QualifiedName(JsonElement el)
        {
            string name = Text(el, "name");
            if (name.Length == 0) return "";

            // Подкласс проверяем первым: он точнее класса
            string owner = NestedText(el, "subclass", "name");
            if (owner.Length == 0) owner = NestedText(el, "class", "name");
            if (owner.Length == 0) owner = NestedText(el, "subspecies", "name");
            if (owner.Length == 0) owner = NestedText(el, "species", "name");
            if (owner.Length == 0) owner = NestedText(el, "race", "name");
            if (owner.Length == 0) return name;

            // «Варвар 3» уже называет класс — дописывать нечего
            if (name.IndexOf(owner, StringComparison.OrdinalIgnoreCase) >= 0) return name;

            return $"{name} — {owner}";
        }

        /// <summary>
        /// Короткая выжимка для записей без готового описания: у рас, видов
        /// и классов в базе лежат только разобранные поля.
        /// </summary>
        private static string Summarize(JsonElement el)
        {
            var parts = new List<string>();

            string type = Text(el, "type");
            if (type.Length > 0) parts.Add($"Тип: {type}");

            string size = Text(el, "size");
            if (size.Length > 0) parts.Add($"Размер: {size}");

            string speed = SpeedOf(el);
            if (speed.Length > 0) parts.Add($"Скорость: {speed}");

            string hitDie = Text(el, "hit_die");
            if (hitDie.Length > 0) parts.Add($"Кость хитов: 1d{hitDie}");

            string ability = NamesOf(el, "primary_ability");
            if (ability.Length > 0) parts.Add($"Основная характеристика: {ability}");

            string bonuses = AbilityBonuses(el);
            if (bonuses.Length > 0) parts.Add($"Бонусы характеристик: {bonuses}");

            string saves = NamesOf(el, "saving_throws");
            if (saves.Length > 0) parts.Add($"Спасброски: {saves}");

            string proficiencies = NamesOf(el, "proficiencies");
            if (proficiencies.Length > 0) parts.Add($"Владения: {proficiencies}");

            string language = Text(el, "language_desc");
            if (language.Length > 0) parts.Add(language);

            string traits = NamesOf(el, "traits");
            if (traits.Length > 0) parts.Add($"Умения: {traits}");

            string subspecies = NamesOf(el, "subspecies");
            if (subspecies.Length > 0) parts.Add($"Подвиды: {subspecies}");

            string subclasses = NamesOf(el, "subclasses");
            if (subclasses.Length > 0) parts.Add($"Подклассы: {subclasses}");

            return string.Join("\n", parts);
        }

        /// <summary>Скорость: у видов строка, у рас число.</summary>
        private static string SpeedOf(JsonElement el)
        {
            if (!el.TryGetProperty("speed", out var speed)) return "";

            if (speed.ValueKind == JsonValueKind.String) return speed.GetString() ?? "";
            if (speed.ValueKind != JsonValueKind.Number) return "";

            int feet = Whole(el, "speed");
            return feet > 0 ? $"{feet} футов" : "";
        }

        /// <summary>Бонусы характеристик расы: «Сила +2, Телосложение +1».</summary>
        private static string AbilityBonuses(JsonElement el)
        {
            if (!el.TryGetProperty("ability_bonuses", out var list) || list.ValueKind != JsonValueKind.Array)
                return "";

            var parts = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                string ability = NestedText(item, "ability_score", "name");
                if (ability.Length == 0) continue;

                int bonus = Whole(item, "bonus");
                parts.Add(bonus >= 0 ? $"{ability} +{bonus}" : $"{ability} {bonus}");
            }

            return string.Join(", ", parts);
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
                     ac, range, properties_text, attunement, is_magic, source, book, raw_properties,
                     srd_index)
                VALUES
                    (@name, @desc, @type, @sub, @rar, @weight, @dmg, @dmgType,
                     @ac, @range, @props, @att, @magic, @src, @book, @raw,
                     @index)
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
                    raw_properties=excluded.raw_properties,
                    srd_index=excluded.srd_index";
            return cmd;
        }

        private static void SaveItem(SqliteCommand cmd, string name, string description,
            string type, string subtype, string rarity, double weight, string damage,
            string damageType, int ac, string range, string properties, bool attunement,
            bool magic, string raw, ImportResult result, string index)
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
            cmd.Parameters.AddWithValue("@index", index ?? "");
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
