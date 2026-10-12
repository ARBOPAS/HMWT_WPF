using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

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
    /// Сервис работы с характеристиками, навыками и бонусами.
    /// Весь SQL — здесь. View его не видит.
    /// </summary>
    public static class AbilityService
    {
        /// Карта навыков: имя навыка → (таблица, колонка)
        public static readonly Dictionary<string, (string Table, string Column)> SkillMap = new()
        {
            ["Athletics"] = ("CharacterStrenghtSkills", "Athletics"),
            ["Acrobatics"] = ("CharacterDexteritySkills", "Acrobatics"),
            ["Stealth"] = ("CharacterDexteritySkills", "Stealth"),
            ["SleightOfHand"] = ("CharacterDexteritySkills", "SleightOfHand"),
            ["Analysis"] = ("CharacterIntSkills", "Analysis"),
            ["History"] = ("CharacterIntSkills", "History"),
            ["Magic"] = ("CharacterIntSkills", "Magic"),
            ["Nature"] = ("CharacterIntSkills", "Nature"),
            ["Religion"] = ("CharacterIntSkills", "Religion"),
            ["Perception"] = ("CharacterWisdomSkills", "Perception"),
            ["Survival"] = ("CharacterWisdomSkills", "Survival"),
            ["Medicine"] = ("CharacterWisdomSkills", "Medicine"),
            ["Insight"] = ("CharacterWisdomSkills", "Insight"),
            ["AnimalCare"] = ("CharacterWisdomSkills", "AnimalCare"),
            ["Performance"] = ("CharacterCharismaSkills", "Performance"),
            ["Deception"] = ("CharacterCharismaSkills", "Deception"),
            ["Persuasion"] = ("CharacterCharismaSkills", "Persuasion"),
            ["Intimidation"] = ("CharacterCharismaSkills", "Intimidation")
        };

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

        /// Переключить владение навыком (0 ↔ 1)
        public static void ToggleSkillProficiency(int characterId, string skillName)
        {
            if (SkillMap.TryGetValue(skillName, out var map))
                ToggleColumn(characterId, map.Table, map.Column);
        }

        /// <summary>
        /// Прочитать флаг владения навыком. Нужен, чтобы предыстория не снимала
        /// уже отмеченные игроком навыки.
        /// </summary>
        public static bool ReadSkillProficiency(int characterId, string skillName)
        {
            if (!SkillMap.TryGetValue(skillName, out var map)) return false;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    $"SELECT {map.Column} FROM {map.Table} WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                var res = cmd.ExecuteScalar();
                return res != null && res != DBNull.Value && Convert.ToInt32(res) > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AbilityService] ReadSkillProficiency: {ex.Message}");
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