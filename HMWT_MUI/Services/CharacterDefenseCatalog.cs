using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMWT_MUI.Services
{
    /// <summary>Тип защиты персонажа. Хранится строкой в JSON — так проще
    /// переживать старые базы и не ломать формат при добавлении новых видов.</summary>
    public static class DefenseKind
    {
        public const string Vulnerability = "vulnerability";
        public const string Resistance = "resistance";
        public const string Immunity = "immunity";
        public const string ConditionImmunity = "condition_immunity";

        public static string Label(string kind) => kind switch
        {
            Vulnerability => "Уязвимость",
            Resistance => "Сопротивление",
            Immunity => "Иммунитет",
            ConditionImmunity => "Иммунитет к состоянию",
            _ => "Защита"
        };

        public static readonly string[] All =
        {
            Vulnerability, Resistance, Immunity, ConditionImmunity
        };
    }

    public sealed class DefenseEntry
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = DefenseKind.Resistance;
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    public static class DefenseCatalog
    {
        /// <summary>Физический урон — он нужен чаще всего, поэтому идёт первым.</summary>
        public static readonly string[] PhysicalDamageTypes =
        {
            "Дробящий", "Колющий", "Рубящий"
        };

        /// <summary>
        /// Типы урона SRD 5.1: сначала физический, дальше остальные по алфавиту.
        /// В справочнике на первом месте то, что выбирают чаще: раньше список
        /// начинался с «Кислоты», и до «Дробящего» приходилось искать глазами.
        /// </summary>
        public static readonly string[] DamageTypes = PhysicalDamageTypes
            .Concat(new[]
            {
                "Кислота", "Холод", "Огонь", "Сила", "Электричество",
                "Некротический", "Яд", "Психический", "Излучение", "Звук"
            }
            .OrderBy(t => t, StringComparer.CurrentCulture))
            .ToArray();

        /// <summary>Состояния SRD 5.1 по алфавиту.</summary>
        public static readonly string[] Conditions = new[]
        {
            "Ослеплён", "Очарован", "Оглох", "Истощён", "Испуган",
            "Схвачен", "Недееспособен", "Невидим", "Парализован",
            "Окаменел", "Отравлен", "Сбит с ног", "Опутан", "Оглушён",
            "Без сознания"
        }.OrderBy(t => t, StringComparer.CurrentCulture).ToArray();

        public static string[] ForKind(string kind)
            => kind == DefenseKind.ConditionImmunity ? Conditions : DamageTypes;

        public static List<DefenseEntry> Load(string json)
        {
            var result = new List<DefenseEntry>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                var parsed = JsonSerializer.Deserialize<List<DefenseEntry>>(json);
                if (parsed == null) return result;
                foreach (var entry in parsed)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) continue;
                    if (!DefenseKind.All.Contains(entry.Kind)) entry.Kind = DefenseKind.Resistance;
                    entry.Name = entry.Name.Trim();
                    if (result.Any(e => e.Kind == entry.Kind &&
                                        string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    result.Add(entry);
                }
            }
            catch { }
            return result;
        }

        public static string Save(IEnumerable<DefenseEntry> entries)
            => JsonSerializer.Serialize(entries?.ToList() ?? new List<DefenseEntry>());
    }
}
