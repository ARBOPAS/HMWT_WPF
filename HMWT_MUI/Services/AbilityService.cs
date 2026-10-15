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
        public int Value { get; set; }
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

        /// Карта спасбросков: сокращение → таблица
        public static readonly Dictionary<string, string> SaveTableMap = new()
        {
            ["Str"] = "CharacterStrenghtSkills",
            ["Dex"] = "CharacterDexteritySkills",
            ["Con"] = "CharacterConstSkills",
            ["Int"] = "CharacterIntSkills",
            ["Wis"] = "CharacterWisdomSkills",
            ["Cha"] = "CharacterCharismaSkills"
        };

        /// Карта характеристик: РУС → колонка БД
        public static readonly Dictionary<string, string> AbilityColumnMap = new()
        {
            ["СИЛА"] = "Strength",
            ["ЛОВКОСТЬ"] = "Dexterity",
            ["ТЕЛОСЛОЖЕНИЕ"] = "Constitution",
            ["ИНТЕЛЛЕКТ"] = "Intelligence",
            ["МУДРОСТЬ"] = "Wisdom",
            ["ХАРИЗМА"] = "Charisma"
        };

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
                    Value = r.GetInt32(2),
                    Active = r.GetInt32(3) == 1
                });
            }
            return list;
        }

        /// Сумма активных бонусов (для пересчёта модификатора)
        public static int GetBonusSum(int characterId, string ability, string scope)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                "SELECT SUM(bonus) FROM CharacterAbilityBonuses " +
                "WHERE id_character=@id AND ability=@a AND scope=@s AND is_active=1", conn);
            cmd.Parameters.AddWithValue("@id", characterId);
            cmd.Parameters.AddWithValue("@a", ability);
            cmd.Parameters.AddWithValue("@s", scope);
            var res = cmd.ExecuteScalar();
            return (res == null || res == DBNull.Value) ? 0 : Convert.ToInt32(res);
        }

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
        public static void UpdateBonus(int id, string name, int? value, bool? active)
        {
            var sets = new List<string>();
            if (name != null) sets.Add("name=@name");
            if (value.HasValue) sets.Add("bonus=@bonus");
            if (active.HasValue) sets.Add("is_active=@active");
            if (sets.Count == 0) return;

            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                $"UPDATE CharacterAbilityBonuses SET {string.Join(",", sets)} WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            if (name != null) cmd.Parameters.AddWithValue("@name", name);
            if (value.HasValue) cmd.Parameters.AddWithValue("@bonus", value.Value);
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