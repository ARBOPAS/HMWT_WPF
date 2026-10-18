using System;
using System.Collections.Generic;
using System.Linq;

namespace HMWT_MUI.Models
{
    /// <summary>Кто это в бою: существо мастера или персонаж игрока.</summary>
    public enum CombatantKind
    {
        Creature = 0,
        Player = 1
    }

    /// <summary>
    /// Жетон в трекере инициативы.
    ///
    /// Мастер пишет имя и модификатор — больше ничего для боя не нужно.
    /// Если жетон пришёл из бестиария, помним ссылку на существо: по жетону
    /// можно открыть его карточку.
    /// </summary>
    public sealed class CombatantData
    {
        public int Id { get; set; }
        public int EncounterId { get; set; }

        /// <summary>Имя на жетоне: «Гоблин», «Гоблин 2», «Арагорн».</summary>
        public string Name { get; set; } = "";

        /// <summary>Модификатор инициативы (Ловкость существа или то, что вписал мастер).</summary>
        public int InitiativeModifier { get; set; }

        /// <summary>Что выпало на кубике. Ноль — бросок ещё не делали.</summary>
        public int InitiativeRoll { get; set; }

        /// <summary>Итог инициативы: кубик + модификатор.</summary>
        public int InitiativeTotal { get; set; }

        /// <summary>Бросали ли инициативу этому жетону.</summary>
        public bool HasRolled { get; set; }

        /// <summary>Ссылка на существо в бестиарии (0 — жетон создан вручную).</summary>
        public int MonsterId { get; set; }

        public CombatantKind Kind { get; set; } = CombatantKind.Creature;

        /// <summary>Задел на будущее: интерактивный бой с хитами.</summary>
        public int HitPoints { get; set; }
        public int MaxHitPoints { get; set; }

        public string Note { get; set; } = "";

        /// <summary>
        /// Состояния участника боя: ключи через запятую (blinded, prone…).
        /// Подписи берутся из того же списка, что и у персонажа.
        /// </summary>
        public string Conditions { get; set; } = "";

        /// <summary>Чей сейчас ход.</summary>
        public bool IsCurrent { get; set; }

        /// <summary>Подпись итога: «17 (14 + 3)» или «—».</summary>
        public string TotalLine()
        {
            if (!HasRolled) return "не брошено";
            string mod = InitiativeModifier == 0 ? "" : $" ({InitiativeRoll} {(InitiativeModifier > 0 ? "+" : "−")} {Math.Abs(InitiativeModifier)})";
            return $"{InitiativeTotal}{mod}";
        }

        public string ModifierLine()
            => InitiativeModifier == 0 ? "+0" : (InitiativeModifier > 0 ? "+" + InitiativeModifier : InitiativeModifier.ToString());

        public CombatantData Clone() => new CombatantData
        {
            Id = Id,
            EncounterId = EncounterId,
            Name = Name,
            InitiativeModifier = InitiativeModifier,
            InitiativeRoll = InitiativeRoll,
            InitiativeTotal = InitiativeTotal,
            HasRolled = HasRolled,
            MonsterId = MonsterId,
            Kind = Kind,
            HitPoints = HitPoints,
            MaxHitPoints = MaxHitPoints,
            Note = Note,
            IsCurrent = IsCurrent
        };
    }

    /// <summary>
    /// Группа боя: то, что мастер создаёт под конкретную стычку.
    /// Внутри — жетоны, раунд и чей ход.
    /// </summary>
    public sealed class EncounterData
    {
        public int Id { get; set; }
        public string Name { get; set; } = "Бой";
        public int Round { get; set; } = 1;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public List<CombatantData> Combatants { get; set; } = new List<CombatantData>();

        /// <summary>
        /// Порядок хода: больше инициатива — раньше. При равенстве выше тот, у кого
        /// больше модификатор, дальше — по имени, чтобы порядок не «прыгал» между
        /// открытиями вкладки.
        /// </summary>
        public List<CombatantData> Ordered()
        {
            return Combatants
                .Where(c => c != null)
                .OrderByDescending(c => c.HasRolled)
                .ThenByDescending(c => c.InitiativeTotal)
                .ThenByDescending(c => c.InitiativeModifier)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Id)
                .ToList();
        }

        /// <summary>Текущий жетон: помеченный флагом, иначе первый в порядке хода.</summary>
        public CombatantData Current()
        {
            var marked = Combatants.FirstOrDefault(c => c != null && c.IsCurrent);
            if (marked != null) return marked;
            var ordered = Ordered();
            return ordered.Count > 0 ? ordered[0] : null;
        }

        /// <summary>Индекс текущего жетона в порядке хода (-1, если жетонов нет).</summary>
        public int CurrentIndex()
        {
            var current = Current();
            if (current == null) return -1;
            var ordered = Ordered();
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].Id == current.Id || ReferenceEquals(ordered[i], current)) return i;
            return -1;
        }

        public bool HasAnyRoll => Combatants.Any(c => c != null && c.HasRolled);

        public string StatusLine()
        {
            if (Combatants.Count == 0) return "пустая группа";
            if (!HasAnyRoll) return $"{Combatants.Count} жетон(ов), инициатива не брошена";
            return IsActive ? $"раунд {Round} · ход: {Current()?.Name ?? "—"}"
                            : $"{Combatants.Count} жетон(ов), бой не идёт";
        }

        public EncounterData Clone()
        {
            return new EncounterData
            {
                Id = Id,
                Name = Name,
                Round = Round,
                IsActive = IsActive,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                Combatants = Combatants.Where(c => c != null).Select(c => c.Clone()).ToList()
            };
        }
    }
}
