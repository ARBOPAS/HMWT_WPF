namespace HMWT_MUI.Models
{
    /// <summary>
    /// Одна часть урона: формула, тип и добавка за уровень выше.
    ///
    /// Используется и заклинаниями, и атаками: у заклинания урон бывает
    /// составным («2к8+6 колющим + 3к10 психическим»), и у оружия тоже —
    /// к дробящему урону может добавляться, например, урон огнём.
    /// Все части одного действия бросаются за один раз.
    /// </summary>
    public sealed class DamagePart
    {
        /// <summary>Формула: «1к10», «2к6+3».</summary>
        public string Formula { get; set; } = "";

        /// <summary>Тип урона: «Piercing» или русское слово — как ввёл игрок.</summary>
        public string DamageType { get; set; } = "";

        /// <summary>Что добавляется за каждый уровень выше базового: «1к6».</summary>
        public string PerLevelDamage { get; set; } = "";

        /// <summary>Уточнение: «только при попадании», «половина при успехе».</summary>
        public string Note { get; set; } = "";

        /// <summary>
        /// Учитывать ли эту часть при броске урона.
        ///
        /// Нужно, когда атака бьёт по-разному: оружие плюс огонь, а огонь уместен
        /// не всегда. Выключенная часть остаётся в записи, но в бросок не идёт.
        /// В старых записях поля нет — часть считается включённой, как и раньше.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Пустая строка — её не сохраняем и не показываем.</summary>
        public bool IsEmpty
            => string.IsNullOrWhiteSpace(Formula) && string.IsNullOrWhiteSpace(DamageType);

        public DamagePart Clone() => new DamagePart
        {
            Formula = Formula,
            DamageType = DamageType,
            PerLevelDamage = PerLevelDamage,
            Note = Note,
            Enabled = Enabled
        };
    }
}
