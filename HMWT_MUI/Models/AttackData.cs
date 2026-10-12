using System;
using System.Collections.Generic;
using System.Linq;

namespace HMWT_MUI.Models
{
    /// <summary>Откуда пришла атака: собрана вручную, из предмета или из заклинания.</summary>
    public static class AttackSources
    {
        public const string Manual = "manual";
        public const string Inventory = "inventory";
        public const string Spell = "spell";

        public static string Title(string key)
        {
            switch (Normalize(key))
            {
                case Inventory: return "ПРЕДМЕТ";
                case Spell: return "ЗАКЛИНАНИЕ";
                default: return "СВОЯ";
            }
        }

        public static string Normalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return Manual;
            switch (key.Trim().ToLowerInvariant())
            {
                case "inventory":
                case "item":
                case "предмет": return Inventory;
                case "spell":
                case "заклинание": return Spell;
                default: return Manual;
            }
        }
    }

    /// <summary>Разделы, к которым относится атака: видно, чем можно ударить ещё раз.</summary>
    public static class AttackSections
    {
        public const string Action = "action";
        public const string Bonus = "bonus";
        public const string Reaction = "reaction";

        public static readonly (string Key, string Title)[] All =
        {
            (Action,   "ДЕЙСТВИЕ"),
            (Bonus,    "БОНУСНОЕ"),
            (Reaction, "РЕАКЦИЯ")
        };

        public static string Title(string key)
        {
            foreach (var item in All)
                if (string.Equals(item.Key, Normalize(key), StringComparison.OrdinalIgnoreCase)) return item.Title;
            return "ДЕЙСТВИЕ";
        }

        public static string Normalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return Action;
            switch (key.Trim().ToLowerInvariant())
            {
                case "bonus":
                case "bonusaction":
                case "бонусное": return Bonus;
                case "reaction":
                case "реакция": return Reaction;
                default: return Action;
            }
        }
    }

    /// <summary>Характеристики, от которых считается попадание и добавка к урону.</summary>
    public static class AttackAbilities
    {
        public const string None = "none";

        public static readonly (string Key, string Title)[] All =
        {
            ("str", "СИЛА"),
            ("dex", "ЛОВКОСТЬ"),
            ("con", "ТЕЛОСЛОЖЕНИЕ"),
            ("int", "ИНТЕЛЛЕКТ"),
            ("wis", "МУДРОСТЬ"),
            ("cha", "ХАРИЗМА"),
            (None, "БЕЗ ДОБАВКИ")
        };

        public static string Title(string key)
        {
            foreach (var item in All)
                if (string.Equals(item.Key, Normalize(key), StringComparison.OrdinalIgnoreCase)) return item.Title;
            return "СИЛА";
        }

        public static string Normalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "str";
            string lower = key.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "none":
                case "нет":
                case "без": return None;
                case "str": case "сила": return "str";
                case "dex": case "ловкость": return "dex";
                case "con": case "телосложение": return "con";
                case "int": case "интеллект": return "int";
                case "wis": case "мудрость": return "wis";
                case "cha": case "харизма": return "cha";
                default: return "str";
            }
        }
    }

    /// <summary>
    /// Атака персонажа — единая запись для оружия и заклинаний.
    ///
    /// Собирается из предмета инвентаря или заклинания либо создаётся вручную,
    /// а дальше её можно свободно править: попадание, несколько частей урона,
    /// дистанция и свойства. Одна вкладка вместо настроек в предметах и свитках.
    /// </summary>
    public sealed class AttackData
    {
        public int Id { get; set; }
        public int CharacterId { get; set; }

        public string Name { get; set; } = "";

        /// <summary>Откуда пришла: manual / inventory / spell.</summary>
        public string Source { get; set; } = AttackSources.Manual;

        /// <summary>Идентификатор предмета или заклинания-источника (0 — нет).</summary>
        public int SourceId { get; set; }

        /// <summary>Действие, бонусное действие или реакция.</summary>
        public string Section { get; set; } = AttackSections.Action;

        /// <summary>Считать попадание автоматически: мод. характеристики + мастерство.</summary>
        public bool AutoBonus { get; set; } = true;

        /// <summary>От какой характеристики считается попадание.</summary>
        public string Ability { get; set; } = "str";

        /// <summary>Владение оружием: добавляет бонус мастерства.</summary>
        public bool Proficient { get; set; } = true;

        /// <summary>Попадание: используется при AutoBonus = false, иначе — кэш расчёта.</summary>
        public int AttackBonus { get; set; }

        /// <summary>Что добавляется к урону: модификатор характеристики или ничего.</summary>
        public string DamageAbility { get; set; } = "str";

        public string Reach { get; set; } = "";
        public string Range { get; set; } = "";
        public string Properties { get; set; } = "";
        public string Note { get; set; } = "";

        public List<DamagePart> DamageParts { get; set; } = new List<DamagePart>();

        /// <summary>
        /// Идентификаторы атак, из которых состоит серия (мультиатака).
        ///
        /// Серия ссылается на обычные атаки, а не копирует их: одну и ту же
        /// «Атаку когтями» можно править в одном месте и использовать и саму
        /// по себе, и внутри серии. Пустой список — обычная атака.
        /// </summary>
        public List<int> ChildIds { get; set; } = new List<int>();

        /// <summary>Больше восьми атак в серии не бывает даже у легендарных существ.</summary>
        public const int MaxMultiChildren = 8;

        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        // ══════════════════════ ПРОИЗВОДНЫЕ ══════════════════════

        public string SourceTitle => AttackSources.Title(Source);
        public string SectionTitle => AttackSections.Title(Section);

        /// <summary>Это серия (мультиатака), а не одна атака.</summary>
        public bool IsMulti => CleanChildIds().Count > 0;

        /// <summary>Идентификаторы серии без пустых и повторов, порядок сохраняется.</summary>
        public List<int> CleanChildIds()
        {
            var result = new List<int>();
            foreach (int id in ChildIds ?? new List<int>())
            {
                if (id <= 0) continue;
                if (result.Contains(id)) continue;
                result.Add(id);
            }
            return result;
        }

        /// <summary>«3 атаки» — с правильным окончанием.</summary>
        public static string CountWord(int count)
        {
            int last = count % 10;
            int lastTwo = count % 100;

            if (last == 1 && lastTwo != 11) return count + " атака";
            if (last >= 2 && last <= 4 && (lastTwo < 12 || lastTwo > 14)) return count + " атаки";
            return count + " атак";
        }

        /// <summary>«+7 к попаданию» — для строки в списке.</summary>
        public string BonusLine()
            => AttackBonus >= 0 ? $"+{AttackBonus} к попаданию" : $"{AttackBonus} к попаданию";

        /// <summary>Дистанции одной строкой: «10 футов» или «80/320 футов».</summary>
        public string DistanceLine()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Reach)) parts.Add(Reach.Trim());
            if (!string.IsNullOrWhiteSpace(Range)) parts.Add(Range.Trim());
            return string.Join(" · ", parts);
        }

        /// <summary>Весь урон одной строкой — для подписи броска и подсказок.</summary>
        public string DamageLine()
        {
            var parts = CleanParts();
            if (parts.Count == 0) return "";

            return string.Join(" + ", parts.Select(p =>
                string.IsNullOrWhiteSpace(p.DamageType) ? p.Formula : $"{p.Formula} {p.DamageType}".Trim()));
        }

        /// <summary>Части урона без пустых строк.</summary>
        public List<DamagePart> CleanParts()
            => (DamageParts ?? new List<DamagePart>()).Where(p => p != null && !p.IsEmpty).ToList();

        public bool HasDamage => CleanParts().Count > 0;

        public string Describe()
        {
            if (IsMulti) return MultiLine() + " · " + SectionTitle;

            var parts = new List<string> { BonusLine() };
            string damage = DamageLine();
            if (damage.Length > 0) parts.Add(damage);
            string distance = DistanceLine();
            if (distance.Length > 0) parts.Add(distance);
            return string.Join(" · ", parts);
        }

        /// <summary>«Серия из 3 атак» — подпись мультиатаки.</summary>
        public string MultiLine()
        {
            int count = CleanChildIds().Count;
            return count == 0 ? "" : "Серия из " + CountWord(count);
        }

        public bool Validate(out string error)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                error = "У атаки должно быть название.";
                return false;
            }
            if (Name.Length > 120)
            {
                error = "Название длиннее 120 знаков — сократите.";
                return false;
            }
            if (AttackBonus < -20 || AttackBonus > 40)
            {
                error = "Попадание должно быть от −20 до +40.";
                return false;
            }
            if (CleanChildIds().Count > MaxMultiChildren)
            {
                error = $"В серии не больше {MaxMultiChildren} атак.";
                return false;
            }
            error = "";
            return true;
        }

        public AttackData Clone()
        {
            return new AttackData
            {
                Id = Id,
                CharacterId = CharacterId,
                Name = Name,
                Source = Source,
                SourceId = SourceId,
                Section = Section,
                AutoBonus = AutoBonus,
                Ability = Ability,
                Proficient = Proficient,
                AttackBonus = AttackBonus,
                DamageAbility = DamageAbility,
                Reach = Reach,
                Range = Range,
                Properties = Properties,
                Note = Note,
                DamageParts = CleanParts().Select(p => p.Clone()).ToList(),
                ChildIds = CleanChildIds(),
                SortOrder = SortOrder,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt
            };
        }

        public override string ToString() => $"{Name} [{BonusLine()}]";
    }
}
