using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// DTO для бонуса характеристики
    /// </summary>
    public class AbilityBonus
    {
        public int Id { get; set; }
        public string Name { get; set; }

        /// <summary>
        /// Число или формула с характеристикой: «3», «ХАР», «1+ИНТ».
        /// Раньше здесь было только целое, и бонус от ХАР приходилось
        /// пересчитывать в голове.
        /// </summary>
        public string Formula { get; set; } = "0";

        public bool Active { get; set; }
    }

    /// <summary>
    /// Насколько персонаж владеет навыком.
    ///
    /// В книге правил этих ступеней две: владение даёт один бонус мастерства,
    /// экспертиза — второй (то же, что удвоенный бонус). Раньше была только
    /// галочка «владеет», и экспертизу — а её дают плут и бард — отметить было
    /// нечем.
    /// </summary>
    public enum SkillProficiencyLevel
    {
        /// <summary>Не владеет.</summary>
        None = 0,

        /// <summary>Владеет: бонус мастерства прибавляется один раз.</summary>
        Proficient = 1,

        /// <summary>Экспертиза: бонус мастерства прибавляется дважды.</summary>
        Expert = 2
    }

    /// <summary>
    /// Сервис работы с характеристиками, навыками и бонусами.
    /// Весь SQL — здесь. View его не видит.
    /// </summary>
    public static class AbilityService
    {
        /// <summary>
        /// Карта навыков: ключ → (таблица, колонка). Собирается из общего списка
        /// навыков, чтобы ключ в интерфейсе и колонка в базе не разъезжались.
        /// </summary>
        public static readonly Dictionary<string, (string Table, string Column)> SkillMap =
            SkillCatalog.All.ToDictionary(
                s => s.Key,
                s => (s.Table, s.Column),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>Разбирает название навыка в любом написании.</summary>
        private static bool TryGetSkill(string skillName, out SkillInfo skill, out (string Table, string Column) map)
        {
            skill = SkillCatalog.Resolve(skillName);
            map = default;
            return skill != null && SkillMap.TryGetValue(skill.Key, out map);
        }

        /// <summary>Карта характеристик: РУС → таблица, где лежат её навыки и спасбросок.</summary>
        public static readonly Dictionary<string, string> AbilityTableMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["СИЛА"] = "CharacterStrenghtSkills",
            ["ЛОВКОСТЬ"] = "CharacterDexteritySkills",
            ["ТЕЛОСЛОЖЕНИЕ"] = "CharacterConstSkills",
            ["ИНТЕЛЛЕКТ"] = "CharacterIntSkills",
            ["МУДРОСТЬ"] = "CharacterWisdomSkills",
            ["ХАРИЗМА"] = "CharacterCharismaSkills"
        };

        /// <summary>
        /// Карта спасбросков: сокращение → таблица. Собирается из карты
        /// характеристик, чтобы имя таблицы не приходилось держать в двух видах.
        /// </summary>
        public static readonly Dictionary<string, string> SaveTableMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Str"] = AbilityTableMap["СИЛА"],
                ["Dex"] = AbilityTableMap["ЛОВКОСТЬ"],
                ["Con"] = AbilityTableMap["ТЕЛОСЛОЖЕНИЕ"],
                ["Int"] = AbilityTableMap["ИНТЕЛЛЕКТ"],
                ["Wis"] = AbilityTableMap["МУДРОСТЬ"],
                ["Cha"] = AbilityTableMap["ХАРИЗМА"]
            };

        /// <summary>Карта характеристик: РУС → колонка БД</summary>
        public static readonly Dictionary<string, string> AbilityColumnMap = new()
        {
            ["СИЛА"] = "Strength",
            ["ЛОВКОСТЬ"] = "Dexterity",
            ["ТЕЛОСЛОЖЕНИЕ"] = "Constitution",
            ["ИНТЕЛЛЕКТ"] = "Intelligence",
            ["МУДРОСТЬ"] = "Wisdom",
            ["ХАРИЗМА"] = "Charisma"
        };

        /// <summary>Навыки и спасбросок одной характеристики — то, что нужно листу персонажа.</summary>
        public sealed class AbilitySkillGroup
        {
            /// <summary>Характеристика: «ЛОВКОСТЬ».</summary>
            public string Ability { get; set; } = "";

            /// <summary>Отмечено ли владение спасброском этой характеристики.</summary>
            public bool SaveProficient { get; set; }

            /// <summary>Навыки характеристики: ключ и галочка владения (0 или 1).</summary>
            public List<(string Key, int Flag)> Skills { get; } = new List<(string, int)>();
        }

        /// <summary>
        /// Читает владения навыками и спасбросками по всем характеристикам.
        ///
        /// Одним вызовом, а не шестью запросами в разных местах: раньше этот SQL
        /// лежал прямо в листе персонажа, и пересчёт бонуса мастерства означал
        /// шесть одинаковых блоков с подстановкой имён колонок.
        /// </summary>
        public static List<AbilitySkillGroup> LoadAbilitySkills(int characterId)
        {
            var groups = new List<AbilitySkillGroup>();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                foreach (string ability in SkillCatalog.AbilityOrder)
                {
                    var group = new AbilitySkillGroup { Ability = ability };
                    var skills = SkillCatalog.ForAbility(ability);

                    // Характеристика без таблицы — это ошибка в списке, а не в
                    // базе: пропускаем её, чтобы остальные всё равно прочитались
                    if (!AbilityTableMap.TryGetValue(ability, out string table))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AbilityService] нет таблицы для «{ability}»");
                        continue;
                    }

                    string columns = string.Join(", ", skills.Select(s => s.Column));
                    string sql = columns.Length == 0
                        ? $"SELECT SaveThrow FROM {table} WHERE id_character = @id"
                        : $"SELECT SaveThrow, {columns} FROM {table} WHERE id_character = @id";

                    using (var cmd = new SqliteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@id", characterId);
                        using var reader = cmd.ExecuteReader();
                        if (reader.Read())
                        {
                            group.SaveProficient = !reader.IsDBNull(0) && Convert.ToInt32(reader.GetValue(0)) > 0;

                            for (int i = 0; i < skills.Count; i++)
                            {
                                int index = i + 1;
                                int flag = reader.IsDBNull(index) ? 0 : Convert.ToInt32(reader.GetValue(index));
                                group.Skills.Add((skills[i].Key, flag));
                            }
                        }
                    }

                    // Строки в таблице могло не быть: навыки всё равно нужны,
                    // иначе лист персонажа остался бы без значений
                    if (group.Skills.Count == 0)
                        foreach (var skill in skills)
                            group.Skills.Add((skill.Key, 0));

                    groups.Add(group);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AbilityService] навыки персонажа: {ex.Message}");
            }

            return groups;
        }

        // ========================= НАВЫКИ И СПАСБРОСКИ =========================

        /// <summary>
        /// Круг ступеней владения навыком: нет → владение → экспертиза → нет.
        ///
        /// Так обе ступени помещаются на одну кнопку листа персонажа, и не
        /// приходится добавлять вторую рядом.
        /// </summary>
        public static SkillProficiencyLevel NextLevel(SkillProficiencyLevel level)
        {
            switch (level)
            {
                case SkillProficiencyLevel.None: return SkillProficiencyLevel.Proficient;
                case SkillProficiencyLevel.Proficient: return SkillProficiencyLevel.Expert;
                default: return SkillProficiencyLevel.None;
            }
        }

        /// <summary>Переключить владение навыком: нет → владение → экспертиза → нет.</summary>
        public static void ToggleSkillProficiency(int characterId, string skillName)
            => SetSkillLevel(characterId, skillName, NextLevel(GetSkillLevel(characterId, skillName)));

        /// <summary>
        /// Записать ступень владения навыком.
        ///
        /// Владение и экспертиза живут в разных местах: галочка — в таблице
        /// характеристик, экспертиза — отдельной строкой. Пишем их одной
        /// транзакцией, иначе персонаж мог бы остаться с экспертизой без владения.
        /// </summary>
        public static void SetSkillLevel(int characterId, string skillName, SkillProficiencyLevel level)
        {
            if (!TryGetSkill(skillName, out var skill, out var map)) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                using (var flag = new SqliteCommand(
                    $"UPDATE {map.Table} SET {map.Column}=@v WHERE id_character=@id", conn, tx))
                {
                    flag.Parameters.AddWithValue("@v", level == SkillProficiencyLevel.None ? 0 : 1);
                    flag.Parameters.AddWithValue("@id", characterId);
                    flag.ExecuteNonQuery();
                }

                // Экспертиза без владения смысла не имеет: снимается вместе с ним
                if (level == SkillProficiencyLevel.Expert)
                {
                    using var add = new SqliteCommand(
                        "INSERT OR IGNORE INTO CharacterSkillExpertise (id_character, skill_key) VALUES (@id, @key)",
                        conn, tx);
                    add.Parameters.AddWithValue("@id", characterId);
                    add.Parameters.AddWithValue("@key", skill.Key);
                    add.ExecuteNonQuery();
                }
                else
                {
                    using var del = new SqliteCommand(
                        "DELETE FROM CharacterSkillExpertise WHERE id_character=@id AND skill_key=@key", conn, tx);
                    del.Parameters.AddWithValue("@id", characterId);
                    del.Parameters.AddWithValue("@key", skill.Key);
                    del.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AbilityService] ступень навыка «{skillName}»: {ex.Message}");
            }
        }

        /// <summary>Ступень владения навыком.</summary>
        public static SkillProficiencyLevel GetSkillLevel(int characterId, string skillName)
        {
            if (!TryGetSkill(skillName, out var skill, out var map)) return SkillProficiencyLevel.None;
            if (!ReadFlag(characterId, map.Table, map.Column)) return SkillProficiencyLevel.None;

            return LoadExpertSkills(characterId).Contains(skill.Key)
                ? SkillProficiencyLevel.Expert
                : SkillProficiencyLevel.Proficient;
        }

        /// <summary>
        /// Навыки с экспертизой. Читается целиком: лист персонажа показывает все
        /// восемнадцать навыков сразу, и спрашивать про каждый по отдельности —
        /// это восемнадцать запросов на одну отрисовку.
        /// </summary>
        public static HashSet<string> LoadExpertSkills(int characterId)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT skill_key FROM CharacterSkillExpertise WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string key = r.IsDBNull(0) ? "" : r.GetString(0);
                    if (key.Length > 0) keys.Add(key);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AbilityService] экспертиза: {ex.Message}");
            }
            return keys;
        }

        /// <summary>
        /// Прочитать флаг владения навыком. Нужен, чтобы предыстория не снимала
        /// уже отмеченные игроком навыки.
        /// </summary>
        public static bool ReadSkillProficiency(int characterId, string skillName)
            => GetSkillLevel(characterId, skillName) != SkillProficiencyLevel.None;

        /// <summary>Читает колонку-флаг 0/1 из таблицы персонажа.</summary>
        private static bool ReadFlag(int characterId, string table, string column)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    $"SELECT {column} FROM {table} WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                var res = cmd.ExecuteScalar();
                return res != null && res != DBNull.Value && Convert.ToInt32(res) > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AbilityService] ReadFlag {table}.{column}: {ex.Message}");
                return false;
            }
        }

        /// Переключить владение спасброском (0 ↔ 1)
        public static void ToggleSaveThrowProficiency(int characterId, string abilityKey)
        {
            if (SaveTableMap.TryGetValue(abilityKey, out var table))
                ToggleColumn(characterId, table, "SaveThrow");
        }

        /// Прочитать флаг владения спасброском
        public static bool ReadSaveProficiency(int characterId, string abilityKey)
        {
            if (!SaveTableMap.TryGetValue(abilityKey, out var table)) return false;
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand($"SELECT SaveThrow FROM {table} WHERE id_character=@id", conn);
            cmd.Parameters.AddWithValue("@id", characterId);
            var res = cmd.ExecuteScalar();
            return res != null && res != DBNull.Value && Convert.ToInt32(res) > 0;
        }

        /// Общий переключатель колонки 0↔1
        private static void ToggleColumn(int characterId, string table, string column)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var sel = new SqliteCommand($"SELECT {column} FROM {table} WHERE id_character=@id", conn);
            sel.Parameters.AddWithValue("@id", characterId);
            int cur = Convert.ToInt32(sel.ExecuteScalar() ?? 0);

            using var upd = new SqliteCommand($"UPDATE {table} SET {column}=@v WHERE id_character=@id", conn);
            upd.Parameters.AddWithValue("@v", cur > 0 ? 0 : 1);
            upd.Parameters.AddWithValue("@id", characterId);
            upd.ExecuteNonQuery();
        }

        // ========================= БОНУСЫ К ХАРАКТЕРИСТИКАМ =========================

        /// Получить список бонусов характеристики
        public static List<AbilityBonus> GetBonuses(int characterId, string ability, string scope)
        {
            var list = new List<AbilityBonus>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                "SELECT id, name, bonus, is_active FROM CharacterAbilityBonuses " +
                "WHERE id_character=@id AND ability=@a AND scope=@s ORDER BY id", conn);
            cmd.Parameters.AddWithValue("@id", characterId);
            cmd.Parameters.AddWithValue("@a", ability);
            cmd.Parameters.AddWithValue("@s", scope);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new AbilityBonus
                {
                    Id = r.GetInt32(0),
                    Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    Formula = r.IsDBNull(2) ? "0" : (Convert.ToString(r.GetValue(2)) ?? "0"),
                    Active = r.GetInt32(3) == 1
                });
            }
            return list;
        }

        /// Сумма активных бонусов. Формула может быть числом или ссылкой
        /// на характеристику: «ХАР», «1+ИНТ», «-МДР».
        public static int GetBonusSum(int characterId, string ability, string scope)
        {
            var bonuses = GetBonuses(characterId, ability, scope).Where(b => b.Active).ToList();
            if (bonuses.Count == 0) return 0;

            var scores = ReadAbilityScores(characterId);

            int sum = 0;
            foreach (var bonus in bonuses)
                sum += EvaluateBonusFormula(bonus.Formula, scores);

            return sum;
        }

        /// <summary>Оценки характеристик персонажа для формул бонусов.</summary>
        private static Dictionary<string, int> ReadAbilityScores(int characterId)
        {
            var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma " +
                    "FROM CharacterInfo WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return scores;

                scores["СИЛА"] = r.GetInt32(0);
                scores["ЛОВКОСТЬ"] = r.GetInt32(1);
                scores["ТЕЛОСЛОЖЕНИЕ"] = r.GetInt32(2);
                scores["ИНТЕЛЛЕКТ"] = r.GetInt32(3);
                scores["МУДРОСТЬ"] = r.GetInt32(4);
                scores["ХАРИЗМА"] = r.GetInt32(5);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AbilityService] оценки для бонусов: {ex.Message}");
            }

            return scores;
        }

        /// <summary>Считает формулу бонуса: число и/или модификатор характеристики.</summary>
        private static int EvaluateBonusFormula(string formula, Dictionary<string, int> scores)
        {
            if (string.IsNullOrWhiteSpace(formula)) return 0;
            if (!DiceFormulaParser.TryParseExpression(formula, out var terms, out _)) return 0;

            int total = 0;
            foreach (var term in terms)
            {
                // Кубик в постоянном бонусе не имеет смысла — не угадываем
                if (term.IsDice) return 0;

                if (term.IsAbility)
                {
                    int score = ScoreOf(term.Ability, scores);
                    if (score <= 0) continue;
                    total += term.Sign * AbilityModifier(score);
                }
                else
                {
                    total += term.Sign * term.Modifier;
                }
            }
            return total;
        }

        private static int ScoreOf(string ability, Dictionary<string, int> scores)
        {
            string name = (ability ?? "").Trim().ToLowerInvariant();
            string key;
            switch (name)
            {
                case "сил": case "сила": case "силы": case "str": case "strength":
                    key = "СИЛА"; break;
                case "лов": case "ловкость": case "ловкости": case "dex": case "dexterity":
                    key = "ЛОВКОСТЬ"; break;
                case "тел": case "телосложение": case "con": case "constitution":
                    key = "ТЕЛОСЛОЖЕНИЕ"; break;
                case "инт": case "интеллект": case "int": case "intelligence":
                    key = "ИНТЕЛЛЕКТ"; break;
                case "мдр": case "мудрость": case "wis": case "wisdom":
                    key = "МУДРОСТЬ"; break;
                case "хар": case "харизма": case "cha": case "charisma":
                    key = "ХАРИЗМА"; break;
                default:
                    return 0;
            }

            return scores.TryGetValue(key, out int score) ? score : 0;
        }

        /// <summary>Модификатор характеристики; для отрицательных — вниз, как в правилах.</summary>
        private static int AbilityModifier(int score)
            => score >= 10 ? (score - 10) / 2 : -((11 - score) / 2);

        /// Добавить пустой бонус
        public static void AddBonus(int characterId, string ability, string scope)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                "INSERT INTO CharacterAbilityBonuses(id_character, ability, scope, name, bonus, is_active) " +
                "VALUES(@id, @a, @s, 'Новый бонус', 0, 1)", conn);
            cmd.Parameters.AddWithValue("@id", characterId);
            cmd.Parameters.AddWithValue("@a", ability);
            cmd.Parameters.AddWithValue("@s", scope);
            cmd.ExecuteNonQuery();
        }

        /// Обновить поля бонуса (только переданные — остальное не трогаем)
        public static void UpdateBonus(int id, string name, string formula, bool? active)
        {
            var sets = new List<string>();
            if (name != null) sets.Add("name=@name");
            if (formula != null) sets.Add("bonus=@bonus");
            if (active.HasValue) sets.Add("is_active=@active");
            if (sets.Count == 0) return;

            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                $"UPDATE CharacterAbilityBonuses SET {string.Join(",", sets)} WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            if (name != null) cmd.Parameters.AddWithValue("@name", name);
            if (formula != null) cmd.Parameters.AddWithValue("@bonus",
                string.IsNullOrWhiteSpace(formula) ? "0" : formula.Trim());
            if (active.HasValue) cmd.Parameters.AddWithValue("@active", active.Value ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        /// Удалить бонус
        public static void DeleteBonus(int id)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("DELETE FROM CharacterAbilityBonuses WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        /// Обновить базовое значение характеристики
        public static void SetBaseScore(int characterId, string ability, int value)
        {
            if (!AbilityColumnMap.TryGetValue(ability, out var column)) return;
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                $"UPDATE CharacterInfo SET {column}=@v WHERE id_character=@id", conn);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.Parameters.AddWithValue("@id", characterId);
            cmd.ExecuteNonQuery();
        }
    }
}