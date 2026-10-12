using System;
using System.Collections.Generic;
using System.Linq;

namespace HMWT_MUI.Models
{
    /// <summary>
    /// Раздел карточки, в котором стоит действие.
    ///
    /// Хранится строкой, а не числом: так файлы обмена остаются читаемыми,
    /// и новый раздел можно добавить, не ломая уже сохранённых существ.
    /// </summary>
    public static class ActionSections
    {
        public const string Action = "action";
        public const string Bonus = "bonus";
        public const string Reaction = "reaction";
        public const string Legendary = "legendary";
        public const string Lair = "lair";
        public const string Regional = "regional";

        /// <summary>Все разделы по порядку показа в карточке.</summary>
        public static readonly (string Key, string Title)[] All =
        {
            (Action,    "ДЕЙСТВИЯ"),
            (Bonus,     "БОНУСНЫЕ ДЕЙСТВИЯ"),
            (Reaction,  "РЕАКЦИИ"),
            (Legendary, "ЛЕГЕНДАРНЫЕ ДЕЙСТВИЯ"),
            (Lair,      "ДЕЙСТВИЯ ЛОГОВА"),
            (Regional,  "МЕСТНЫЕ ЭФФЕКТЫ")
        };

        public static string Title(string key)
        {
            foreach (var item in All)
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) return item.Title;
            return "ДЕЙСТВИЯ";
        }

        /// <summary>Приводит любое написание раздела к известному ключу.</summary>
        public static string Normalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return Action;
            string lower = key.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "bonus":
                case "bonusaction":
                case "бонусное":
                case "бонусные": return Bonus;
                case "reaction":
                case "реакция":
                case "реакции": return Reaction;
                case "legendary":
                case "легендарное":
                case "легендарные": return Legendary;
                case "lair":
                case "логово":
                case "действия логова": return Lair;
                case "regional":
                case "местные":
                case "местные эффекты": return Regional;
                default: return Action;
            }
        }

        /// <summary>Есть ли раздел с таким ключом.</summary>
        public static bool IsKnown(string key)
            => All.Any(a => string.Equals(a.Key, Normalize(key), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Одна часть урона в атаке: формула и её тип.</summary>
    public sealed class DamagePartData
    {
        /// <summary>Формула как её пишет мастер: «2к8+6».</summary>
        public string Formula { get; set; } = "";

        /// <summary>Тип урона словами: «колющий». Может быть пустым.</summary>
        public string DamageType { get; set; } = "";

        /// <summary>Уточнение: «только двумя руками», «при попадании в цель». </summary>
        public string Note { get; set; } = "";

        public DamagePartData Clone() => new DamagePartData
        {
            Formula = Formula,
            DamageType = DamageType,
            Note = Note
        };
    }

    /// <summary>Атака, реакция, легендарное действие — всё это одна запись.</summary>
    public sealed class MonsterActionData
    {
        /// <summary>Раздел карточки: см. <see cref="ActionSections"/>.</summary>
        public string Section { get; set; } = ActionSections.Action;

        /// <summary>Название: «Скимитар», «Огненное дыхание (перезарядка 5–6)».</summary>
        public string Name { get; set; } = "";

        /// <summary>Вид атаки: «Рукопашная атака оружием», «Дальнобойная атака», «Заклинание».</summary>
        public string Kind { get; set; } = "";

        /// <summary>Попадание: «+11» или «11 к попаданию». Пусто — значит атака без броска.</summary>
        public string HitBonus { get; set; } = "";

        /// <summary>Досягаемость: «5 футов».</summary>
        public string Reach { get; set; } = "";

        /// <summary>Дистанция: «80/320 футов».</summary>
        public string Range { get; set; } = "";

        /// <summary>Урон в свободной записи: «2к8+6 колющим + 3к10 психическим».</summary>
        public string Damage { get; set; } = "";

        /// <summary>Спасбросок: «Сл 17 Ловкости, половина при успехе».</summary>
        public string SaveDc { get; set; } = "";

        /// <summary>Описание действия, если это не атака: «Мультиатака: укус и два удара когтями».</summary>
        public string Description { get; set; } = "";

        /// <summary>Перезарядка отдельным полем: «5–6», «3/день».</summary>
        public string Recharge { get; set; } = "";

        /// <summary>Стоимость в легендарных действиях (по умолчанию одно).</summary>
        public int LegendaryCost { get; set; } = 1;

        public MonsterActionData Clone() => new MonsterActionData
        {
            Section = Section,
            Name = Name,
            Kind = Kind,
            HitBonus = HitBonus,
            Reach = Reach,
            Range = Range,
            Damage = Damage,
            SaveDc = SaveDc,
            Description = Description,
            Recharge = Recharge,
            LegendaryCost = LegendaryCost
        };
    }

    /// <summary>Черта (особенность) существа: «Тактика стаи.» + текст.</summary>
    public sealed class MonsterTraitData
    {
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";

        public MonsterTraitData Clone() => new MonsterTraitData { Name = Name, Text = Text };
    }

    /// <summary>
    /// Существо бестиария.
    ///
    /// Поля повторяют обычную карточку существа из книги правил: всё, что мастер
    /// видит на странице, должно быть заполняемым. Списки (черты, действия) лежат
    /// отдельными объектами и сохраняются в базу как JSON — иначе на каждую черту
    /// нужна была бы отдельная таблица.
    /// </summary>
    public sealed class MonsterData
    {
        public int Id { get; set; }

        // ── шапка ──
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string Size { get; set; } = "";
        public string CreatureType { get; set; } = "";
        public string Subtype { get; set; } = "";
        public string Alignment { get; set; } = "";
        public string PhotoPath { get; set; } = "";

        // ── защита ──
        public int ArmorClass { get; set; } = 10;
        public string ArmorClassNote { get; set; } = "";
        public int HitPoints { get; set; } = 1;
        public string HitPointsFormula { get; set; } = "";

        // ── скорости (футы) ──
        public int SpeedWalk { get; set; } = 30;
        public int SpeedFly { get; set; }
        public int SpeedSwim { get; set; }
        public int SpeedClimb { get; set; }
        public int SpeedBurrow { get; set; }
        public string SpeedNote { get; set; } = "";
        public bool Hover { get; set; }

        // ── характеристики ──
        public int Strength { get; set; } = 10;
        public int Dexterity { get; set; } = 10;
        public int Constitution { get; set; } = 10;
        public int Intelligence { get; set; } = 10;
        public int Wisdom { get; set; } = 10;
        public int Charisma { get; set; } = 10;

        /// <summary>Спасброски: «Тел +5, Мдр +3».</summary>
        public string SavingThrows { get; set; } = "";

        /// <summary>Навыки: «Восприятие +5, Скрытность +4».</summary>
        public string Skills { get; set; } = "";

        // ── урон и состояния ──
        public string DamageVulnerabilities { get; set; } = "";
        public string DamageResistances { get; set; } = "";
        public string DamageImmunities { get; set; } = "";
        public string ConditionImmunities { get; set; } = "";

        // ── прочее ──
        public string Senses { get; set; } = "";
        public string Languages { get; set; } = "";
        public string ChallengeRating { get; set; } = "0";
        public int Experience { get; set; } = 10;
        public int ProficiencyBonus { get; set; } = 2;
        public string Habitat { get; set; } = "";
        public string Treasure { get; set; } = "";
        public string Source { get; set; } = "";
        public string Tags { get; set; } = "";

        // ── большие блоки ──
        public string LegendaryIntro { get; set; } = "";
        public string LairText { get; set; } = "";
        public string Spells { get; set; } = "";
        public string Equipment { get; set; } = "";
        public string Description { get; set; } = "";

        public List<MonsterTraitData> Traits { get; set; } = new List<MonsterTraitData>();
        public List<MonsterActionData> Actions { get; set; } = new List<MonsterActionData>();

        public bool IsCustom { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        // ══════════════════════ ПРОИЗВОДНЫЕ ══════════════════════

        /// <summary>Модификатор характеристики по значению.</summary>
        public static int ModifierOf(int score) => (int)Math.Floor((score - 10) / 2.0);

        public int StrengthModifier => ModifierOf(Strength);
        public int DexterityModifier => ModifierOf(Dexterity);
        public int ConstitutionModifier => ModifierOf(Constitution);
        public int IntelligenceModifier => ModifierOf(Intelligence);
        public int WisdomModifier => ModifierOf(Wisdom);
        public int CharismaModifier => ModifierOf(Charisma);

        /// <summary>Модификатор инициативы: у существа это всегда Ловкость.</summary>
        public int InitiativeModifier => DexterityModifier;

        /// <summary>«Маленький Гуманоид (гоблиноид), нейтрально-злой» — строка под именем.</summary>
        public string TypeLine()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Size)) parts.Add(Size.Trim());
            if (!string.IsNullOrWhiteSpace(CreatureType)) parts.Add(CreatureType.Trim());
            string head = string.Join(" ", parts);
            if (!string.IsNullOrWhiteSpace(Subtype)) head += $" ({Subtype.Trim()})";
            if (!string.IsNullOrWhiteSpace(Alignment))
                head = head.Length > 0 ? $"{head}, {Alignment.Trim()}" : Alignment.Trim();
            return head;
        }

        /// <summary>Строка скорости: «40 футов, летая 80 футов».</summary>
        public string SpeedLine()
        {
            var parts = new List<string>();
            if (SpeedWalk != 0) parts.Add($"{SpeedWalk} футов");
            if (SpeedFly != 0) parts.Add($"летая {SpeedFly} футов{(Hover ? " (парение)" : "")}");
            if (SpeedSwim != 0) parts.Add($"плавая {SpeedSwim} футов");
            if (SpeedClimb != 0) parts.Add($"лазая {SpeedClimb} футов");
            if (SpeedBurrow != 0) parts.Add($"копая {SpeedBurrow} футов");
            if (!string.IsNullOrWhiteSpace(SpeedNote)) parts.Add(SpeedNote.Trim());
            return string.Join(", ", parts);
        }

        /// <summary>Хиты как в книге: «7 (2к6)». Формула может быть пустой.</summary>
        public string HitPointsLine()
        {
            string formula = (HitPointsFormula ?? "").Trim();
            return formula.Length == 0 ? HitPoints.ToString() : $"{HitPoints} ({formula})";
        }

        /// <summary>Опасность как в книге: «1/4 (50 опыта)».</summary>
        public string ChallengeLine()
        {
            string cr = string.IsNullOrWhiteSpace(ChallengeRating) ? "0" : ChallengeRating.Trim();
            return Experience > 0 ? $"{cr} ({Experience:N0} опыта)" : cr;
        }

        /// <summary>Действия выбранного раздела.</summary>
        public List<MonsterActionData> ActionsOf(string section)
        {
            string key = ActionSections.Normalize(section);
            return Actions.Where(a => a != null && ActionSections.Normalize(a.Section) == key).ToList();
        }

        /// <summary>Есть ли хоть что-то в разделе — чтобы не рисовать пустой заголовок.</summary>
        public bool HasSection(string section) => ActionsOf(section).Count > 0;

        /// <summary>Копия: нужна форме редактирования, чтобы отмена не портила список.</summary>
        public MonsterData Clone()
        {
            return new MonsterData
            {
                Id = Id,
                Name = Name,
                NameEn = NameEn,
                Size = Size,
                CreatureType = CreatureType,
                Subtype = Subtype,
                Alignment = Alignment,
                PhotoPath = PhotoPath,
                ArmorClass = ArmorClass,
                ArmorClassNote = ArmorClassNote,
                HitPoints = HitPoints,
                HitPointsFormula = HitPointsFormula,
                SpeedWalk = SpeedWalk,
                SpeedFly = SpeedFly,
                SpeedSwim = SpeedSwim,
                SpeedClimb = SpeedClimb,
                SpeedBurrow = SpeedBurrow,
                SpeedNote = SpeedNote,
                Hover = Hover,
                Strength = Strength,
                Dexterity = Dexterity,
                Constitution = Constitution,
                Intelligence = Intelligence,
                Wisdom = Wisdom,
                Charisma = Charisma,
                SavingThrows = SavingThrows,
                Skills = Skills,
                DamageVulnerabilities = DamageVulnerabilities,
                DamageResistances = DamageResistances,
                DamageImmunities = DamageImmunities,
                ConditionImmunities = ConditionImmunities,
                Senses = Senses,
                Languages = Languages,
                ChallengeRating = ChallengeRating,
                Experience = Experience,
                ProficiencyBonus = ProficiencyBonus,
                Habitat = Habitat,
                Treasure = Treasure,
                Source = Source,
                Tags = Tags,
                LegendaryIntro = LegendaryIntro,
                LairText = LairText,
                Spells = Spells,
                Equipment = Equipment,
                Description = Description,
                Traits = Traits.Where(t => t != null).Select(t => t.Clone()).ToList(),
                Actions = Actions.Where(a => a != null).Select(a => a.Clone()).ToList(),
                IsCustom = IsCustom,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt
            };
        }

        /// <summary>Готова ли запись к сохранению: имя обязательно.</summary>
        public bool Validate(out string error)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                error = "У существа должно быть название.";
                return false;
            }
            if (Name.Length > 120)
            {
                error = "Название длиннее 120 знаков — сократите.";
                return false;
            }
            if (ArmorClass < 0 || ArmorClass > 99)
            {
                error = "Класс доспеха должен быть от 0 до 99.";
                return false;
            }
            if (HitPoints < 0 || HitPoints > 99999)
            {
                error = "Хиты должны быть от 0 до 99 999.";
                return false;
            }
            foreach (var score in new[] { Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma })
            {
                if (score < 0 || score > 99)
                {
                    error = "Характеристики должны быть от 0 до 99.";
                    return false;
                }
            }
            error = "";
            return true;
        }

        public override string ToString() => $"{Name} [{TypeLine()}] ПО {ChallengeRating}";
    }
}
