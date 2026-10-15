using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Состояние персонажа, которое видно в шапке листа: состояния, вдохновение
    /// и заклинание, на котором держится концентрация.
    ///
    /// Всё трое — про «прямо сейчас», а не про лист персонажа, поэтому живут
    /// рядом: одни и те же запросы к CharacterInfo и CharacterConditions.
    /// </summary>
    public static class CharacterStateService
    {
        #region === СОСТОЯНИЯ ===

        /// <summary>Раздел справочника, из которого берутся названия состояний.</summary>
        public const string ConditionsCategory = "Conditions";

        /// <summary>Запись списка состояний: ключ справочника и подпись для показа.</summary>
        public sealed class ConditionOption
        {
            public string Key { get; set; } = "";
            public string Name { get; set; } = "";

            /// <summary>Отмечено ли состояние у персонажа — сюда пишет флажок списка.</summary>
            public bool IsSelected { get; set; }
        }

        /// <summary>
        /// Запасной список на случай, когда справочник SRD ещё не импортирован.
        ///
        /// Ключи — те же srd_index, что и в файле 5e-SRD-Conditions.json, и
        /// порядок взят оттуда же. Благодаря этому отметки, поставленные до
        /// импорта, после него не теряются, а просто получают перевод.
        /// </summary>
        private static readonly (string Key, string Name)[] BuiltInConditions =
        {
            ("blinded",       "Ослеплённый"),
            ("charmed",       "Очарованный"),
            ("deafened",      "Оглохший"),
            ("exhaustion",    "Истощение"),
            ("frightened",    "Испуганный"),
            ("grappled",      "Схваченный"),
            ("incapacitated", "Недееспособный"),
            ("invisible",     "Невидимый"),
            ("paralyzed",     "Парализованный"),
            ("petrified",     "Окаменевший"),
            ("poisoned",      "Отравленный"),
            ("prone",         "Опрокинутый"),
            ("restrained",    "Опутанный"),
            ("stunned",       "Ошеломлённый"),
            ("unconscious",   "Бессознательный"),
        };

        /// <summary>
        /// Список состояний для выбора.
        ///
        /// Основа — справочник: игрок мог импортировать другую книгу или перевод,
        /// и спорить с ним не за чем. Записи без ключа пропускаются: без srd_index
        /// отметку не с чем связать. Если справочник пуст или в нём не хватает
        /// записей, недостающие берутся из встроенного списка — иначе после
        /// частичного импорта часть состояний просто исчезла бы из выбора.
        /// </summary>
        public static List<ConditionOption> LoadConditions(int characterId = 0)
        {
            var fromReference = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT srd_index, name
                    FROM SrdReference
                    WHERE category = @c
                      AND TRIM(COALESCE(srd_index, '')) <> ''
                      AND TRIM(COALESCE(name, '')) <> ''", conn);
                cmd.Parameters.AddWithValue("@c", ConditionsCategory);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader["srd_index"]?.ToString()?.Trim() ?? "";
                    string name = reader["name"]?.ToString()?.Trim() ?? "";
                    if (key.Length == 0 || name.Length == 0) continue;

                    // Одна и та же запись бывает и в английской базе, и в русской.
                    // Показываем русскую, если она есть: остальной интерфейс русский.
                    if (fromReference.TryGetValue(key, out string known))
                    {
                        if (!HasCyrillic(known) && HasCyrillic(name)) fromReference[key] = name;
                        continue;
                    }

                    fromReference[key] = name;
                    order.Add(key);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterState] список состояний: {ex.Message}");
            }

            var result = order
                .Select(key => new ConditionOption { Key = key, Name = fromReference[key] })
                .ToList();

            // Добор встроенными: и когда справочник пуст, и когда в нём пробел
            foreach (var (key, name) in BuiltInConditions)
            {
                if (fromReference.ContainsKey(key)) continue;
                result.Add(new ConditionOption { Key = key, Name = name });
            }

            // Хвост списка — отметки, которых нет ни в справочнике, ни во встроенном
            // списке: такое возможно, если базу переимпортировали другим набором.
            // Показывать их надо, иначе состояние выглядело бы снятым само собой.
            if (characterId > 0)
            {
                foreach (string key in LoadConditionKeys(characterId))
                {
                    if (result.Any(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase))) continue;
                    result.Add(new ConditionOption { Key = key, Name = key });
                }
            }

            return result;
        }

        /// <summary>Отмеченные состояния персонажа — ключи записей справочника.</summary>
        public static List<string> LoadConditionKeys(int characterId)
        {
            var keys = new List<string>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT condition_key FROM CharacterConditions WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader["condition_key"]?.ToString() ?? "";
                    if (key.Length > 0) keys.Add(key);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterState] отметки состояний: {ex.Message}");
            }
            return keys;
        }

        /// <summary>
        /// Записывает отметки целиком: набор небольшой, поэтому проще заменить его,
        /// чем вычислять разницу. В транзакции — иначе при сбое посередине
        /// персонаж остался бы с половиной состояний.
        /// </summary>
        public static void SaveConditionKeys(int characterId, IEnumerable<string> keys)
        {
            var clean = (keys ?? Enumerable.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                DatabaseHelper.RunInTransaction((conn, tx) =>
                {
                    using (var del = new SqliteCommand(
                        "DELETE FROM CharacterConditions WHERE id_character = @id", conn, tx))
                    {
                        del.Parameters.AddWithValue("@id", characterId);
                        del.ExecuteNonQuery();
                    }

                    using var ins = new SqliteCommand(
                        "INSERT OR IGNORE INTO CharacterConditions (id_character, condition_key) VALUES (@id, @key)",
                        conn, tx);
                    ins.Parameters.AddWithValue("@id", characterId);
                    var key = ins.Parameters.Add("@key", SqliteType.Text);

                    foreach (string k in clean)
                    {
                        key.Value = k;
                        ins.ExecuteNonQuery();
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterState] сохранение состояний: {ex.Message}");
            }
        }

        /// <summary>Строка для шапки: перечисление отметок или «нет».</summary>
        public static string DescribeConditions(IEnumerable<string> keys, IEnumerable<ConditionOption> options)
        {
            var names = new List<string>();
            var lookup = (options ?? Enumerable.Empty<ConditionOption>())
                .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

            foreach (string key in keys ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                names.Add(lookup.TryGetValue(key, out string name) && name.Length > 0 ? name : key);
            }

            return names.Count == 0 ? "нет" : string.Join(", ", names);
        }

        private static bool HasCyrillic(string text)
            => !string.IsNullOrEmpty(text) && text.Any(c => c >= 'А' && c <= 'я' || c == 'ё' || c == 'Ё');

        #endregion

        #region === ВДОХНОВЕНИЕ ===

        /// <summary>Есть ли у персонажа вдохновение.</summary>
        public static bool LoadInspiration(int characterId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COALESCE(Inspiration, 0) FROM CharacterInfo WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                var value = cmd.ExecuteScalar();
                return value != null && value != DBNull.Value && Convert.ToInt32(value) != 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterState] вдохновение: {ex.Message}");
                return false;
            }
        }

        public static void SaveInspiration(int characterId, bool hasInspiration)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "UPDATE CharacterInfo SET Inspiration = @v WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@v", hasInspiration ? 1 : 0);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterState] сохранение вдохновения: {ex.Message}");
            }
        }

        #endregion

        #region === КОНЦЕНТРАЦИЯ ===

        /// <summary>
        /// Заклинание, на котором держится концентрация. Хранится название, а не
        /// ссылка на запись: заклинание могут удалить из списка персонажа, а
        /// подпись в шапке должна остаться осмысленной. Пустая строка — нет
        /// концентрации.
        /// </summary>
        public static string LoadConcentration(int characterId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COALESCE(ConcentrationSpell, '') FROM CharacterInfo WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                return cmd.ExecuteScalar()?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterState] концентрация: {ex.Message}");
                return "";
            }
        }

        public static void SaveConcentration(int characterId, string spellName)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "UPDATE CharacterInfo SET ConcentrationSpell = @v WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@v", spellName ?? "");
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterState] сохранение концентрации: {ex.Message}");
            }
        }

        /// <summary>
        /// Заклинания персонажа, которые требуют концентрации: только из них и
        /// можно выбирать. Список идёт по уровню, как во вкладке заклинаний.
        /// </summary>
        public static List<string> LoadConcentrationSpells(int characterId)
        {
            try
            {
                var (prepared, known) = SpellService.LoadSpells(characterId);
                return prepared.Concat(known)
                    .Where(s => s.Concentration && !string.IsNullOrWhiteSpace(s.Name))
                    .OrderBy(s => s.Level)
                    .ThenBy(s => s.Name, StringComparer.CurrentCulture)
                    .Select(s => s.Name.Trim())
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterState] заклинания концентрации: {ex.Message}");
                return new List<string>();
            }
        }

        #endregion
    }
}
