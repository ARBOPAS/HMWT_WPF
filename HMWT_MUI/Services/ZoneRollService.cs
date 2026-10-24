using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Спасброски и проверки «без листа»: нужны скриптам тёмных зон, когда
    /// жетон игрока входит в зону, а окно персонажа у него может быть закрыто.
    ///
    /// Бонусы считаются по тем же правилам, что и на листе: модификатор
    /// характеристики, владение спасброском/навыком, экспертиза и ручные
    /// бонусы из справочника. Результат уходит в ленту комнаты от имени
    /// персонажа — как обычный бросок.
    /// </summary>
    public static class ZoneRollService
    {
        private static readonly Dictionary<string, string> AbilityRussian =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["STR"] = "СИЛА",
                ["DEX"] = "ЛОВКОСТЬ",
                ["CON"] = "ТЕЛОСЛОЖЕНИЕ",
                ["INT"] = "ИНТЕЛЛЕКТ",
                ["WIS"] = "МУДРОСТЬ",
                ["CHA"] = "ХАРИЗМА"
            };

        private static readonly Dictionary<string, string> ShortName =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["STR"] = "СИЛ", ["DEX"] = "ЛОВ", ["CON"] = "ТЕЛ",
                ["INT"] = "ИНТ", ["WIS"] = "МДР", ["CHA"] = "ХАР"
            };

        private static readonly Dictionary<string, string> SaveKey =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["STR"] = "Str", ["DEX"] = "Dex", ["CON"] = "Con",
                ["INT"] = "Int", ["WIS"] = "Wis", ["CHA"] = "Cha"
            };

        public sealed class Result
        {
            public string Formula = "1d20";
            public string Description = "";
            public int Total;
            public List<int> Rolls = new List<int>();
            public bool? Success;
        }

        /// <summary>
        /// Бросок по скрипту зоны. kind: "save" или "check"; для проверки
        /// можно задать skill (ключ навыка), иначе проверка характеристики.
        /// </summary>
        public static Result Roll(int characterId, string kind, string ability, string skill, int dc,
            string effectName)
        {
            var result = new Result();
            if (characterId <= 0) return result;

            try
            {
                bool save = string.Equals(kind, "save", StringComparison.OrdinalIgnoreCase);
                int bonus;
                string label;

                if (save)
                {
                    bonus = SaveBonus(characterId, ability);
                    label = "Спасбросок " + Short(ability);
                }
                else if (!string.IsNullOrWhiteSpace(skill))
                {
                    bonus = SkillBonus(characterId, skill, out string skillName, out _);
                    label = "Проверка: " + skillName;
                }
                else
                {
                    bonus = CheckBonus(characterId, ability);
                    label = "Проверка " + Short(ability);
                }

                string formula = "1d20" + (bonus >= 0 ? "+" : "") + bonus;
                var roll = new DiceRoller().RollFormula(formula, save ? "Спасбросок" : "Проверка");

                result.Formula = formula;
                result.Total = roll.Total;
                result.Rolls = roll.IndividualRolls ?? new List<int>();

                string title = label;
                if (!string.IsNullOrWhiteSpace(effectName)) title += " · " + effectName.Trim();
                if (dc > 0)
                {
                    bool ok = result.Total >= dc;
                    result.Success = ok;
                    title += " (DC " + dc + "): " + (ok ? "успех" : "провал");
                }

                result.Description = title;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZoneRoll] бросок: {ex.Message}");
                result.Description = "Бросок зоны не удался";
            }

            return result;
        }

        private static string Short(string ability)
            => ShortName.TryGetValue(ability ?? "", out string s) ? s : (ability ?? "");

        private static string Russian(string ability)
        {
            if (string.IsNullOrWhiteSpace(ability)) return "ЛОВКОСТЬ";
            if (AbilityRussian.TryGetValue(ability, out string ru)) return ru;
            return ability;   // уже по-русски
        }

        private static int SaveBonus(int characterId, string ability)
        {
            int mod = AbilityModifier(characterId, ability);
            int prof = AbilityService.ReadSaveProficiency(characterId,
                SaveKey.TryGetValue(ability ?? "", out string key) ? key : "Dex")
                ? AttackService.ProficiencyBonus(characterId) : 0;
            int extra = AbilityService.GetBonusSum(characterId, Russian(ability), "Save");
            return mod + prof + extra;
        }

        private static int CheckBonus(int characterId, string ability)
        {
            return AbilityModifier(characterId, ability)
                   + AbilityService.GetBonusSum(characterId, Russian(ability), "Check");
        }

        private static int SkillBonus(int characterId, string skillKey,
            out string skillName, out string abilityRu)
        {
            skillName = skillKey ?? "";
            abilityRu = "ЛОВКОСТЬ";

            var skill = SkillCatalog.All.FirstOrDefault(s =>
                string.Equals(s.Key, skillKey, StringComparison.OrdinalIgnoreCase))
                ?? SkillCatalog.Resolve(skillKey);
            if (skill == null) return 0;

            skillName = string.IsNullOrWhiteSpace(skill.Russian) ? skill.Key : skill.Russian;
            abilityRu = string.IsNullOrWhiteSpace(skill.Ability) ? "ЛОВКОСТЬ" : skill.Ability;

            int mod = AbilityModifier(characterId, AbilityFromRussian(abilityRu));
            int prof = AttackService.ProficiencyBonus(characterId);
            var level = AbilityService.GetSkillLevel(characterId, skill.Key);
            int profPart = level == SkillProficiencyLevel.Expert ? prof * 2
                         : level == SkillProficiencyLevel.Proficient ? prof : 0;
            int extra = AbilityService.GetBonusSum(characterId, abilityRu, "Skill");
            return mod + profPart + extra;
        }

        private static string AbilityFromRussian(string ru)
        {
            foreach (var pair in AbilityRussian)
                if (string.Equals(pair.Value, ru, StringComparison.OrdinalIgnoreCase)) return pair.Key;
            return "DEX";
        }

        private static int AbilityModifier(int characterId, string ability)
        {
            string ru = Russian(ability);
            if (!AbilityService.AbilityColumnMap.TryGetValue(ru, out string column)) return 0;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    $"SELECT {column} FROM CharacterInfo WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                var value = cmd.ExecuteScalar();
                int score = value == null || value == DBNull.Value ? 10 : Convert.ToInt32(value);
                return (int)Math.Floor((score - 10) / 2.0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZoneRoll] характеристика: {ex.Message}");
                return 0;
            }
        }
    }
}
