namespace HMWT_MUI.Models
{
    #region === POCO ЗАКЛИНАНИЯ ===
    /// <summary>
    /// POCO для заклинания (используется в UI и сервисах).
    /// </summary>
    public sealed class SpellData
    {
        public int IdCharSpell { get; set; }
        public int IdSpell { get; set; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public string School { get; set; } = "";
        public string CastingTime { get; set; } = "";
        public string Range { get; set; } = "";
        public string Components { get; set; } = "";
        public string Duration { get; set; } = "";
        public string Description { get; set; } = "";
        public string AtHigherLevels { get; set; } = "";
        public string Classes { get; set; } = "";
        public string Source { get; set; } = "";
        public string Material { get; set; } = "";
        public string Page { get; set; } = "";
        public bool Concentration { get; set; }
        public bool Ritual { get; set; }
        public bool IsPrepared { get; set; }
        public bool IsModified { get; set; }
        public string ImagePath { get; set; } = "";

        // === Эффект для кастования ===
        //
        // Заполняет игрок в редакторе заклинания: в справочниках урон лежит внутри
        // английского текста описания, и автоматически его разобрать нельзя.
        // DamageFormula — базовый бросок урона («1d4+4»),
        // DamageType — тип урона («Thunder»),
        // PerLevelDamage — что добавляется за каждый уровень выше («+1d6»).
        public string DamageFormula { get; set; } = "";
        public string DamageType { get; set; } = "";
        public string PerLevelDamage { get; set; } = "";

        /// <summary>
        /// Дополнительные типы урона того же каста. Первая часть — основная
        /// (три поля выше), остальные лежат здесь: так старые записи в базе
        /// читаются без изменений.
        /// </summary>
        public System.Collections.Generic.List<DamagePart> ExtraDamageParts { get; set; }
            = new System.Collections.Generic.List<DamagePart>();

        /// <summary>
        /// Пометка про спасбросок цели: «Спас Ловкости Сл 15, половина при успехе».
        /// Показывается при сотворении, чтобы не искать её в описании.
        /// </summary>
        public string SaveNote { get; set; } = "";

        // === Лечение ===
        //
        // Отдельные поля, а не одно «значение»: у заклинания бывает и урон,
        // и лечение сразу, и складывать их в одну формулу нельзя. Лечение
        // бросается своим броском и, если игрок согласен, прибавляется
        // к здоровью персонажа.
        public string HealFormula { get; set; } = "";
        public string HealPerLevel { get; set; } = "";

        /// <summary>Есть ли что лечить при кастовании.</summary>
        public bool HasHealing => !string.IsNullOrWhiteSpace(HealFormula);

        /// <summary>Растёт ли лечение от уровня ячейки.</summary>
        public bool CanUpcastHealing => !string.IsNullOrWhiteSpace(HealPerLevel);

        /// <summary>Все части урона по порядку: основная, затем дополнительные.</summary>
        public System.Collections.Generic.List<DamagePart> AllDamageParts()
        {
            var parts = new System.Collections.Generic.List<DamagePart>();

            if (!string.IsNullOrWhiteSpace(DamageFormula) || !string.IsNullOrWhiteSpace(DamageType))
            {
                parts.Add(new DamagePart
                {
                    Formula = DamageFormula ?? "",
                    DamageType = DamageType ?? "",
                    PerLevelDamage = PerLevelDamage ?? ""
                });
            }

            foreach (var extra in ExtraDamageParts ?? new System.Collections.Generic.List<DamagePart>())
            {
                if (extra == null || extra.IsEmpty) continue;
                parts.Add(extra);
            }

            return parts;
        }

        /// <summary>Есть ли что бросать при кастовании.</summary>
        public bool HasDamage => AllDamageParts().Count > 0;

        /// <summary>Можно ли поднять заклинание уровнем выше с добавкой урона.</summary>
        public bool CanUpcast
        {
            get
            {
                foreach (var part in AllDamageParts())
                    if (!string.IsNullOrWhiteSpace(part.PerLevelDamage)) return true;
                return false;
            }
        }

        // Визуальные кэши (вычисляются один раз)
        public string SchoolInitial { get; set; } = "?";
        public System.Windows.Media.Brush SchoolBrush { get; set; }
        public string CastingShort { get; set; } = "";
        public System.Windows.Visibility ConcentrationVisibility { get; set; } = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility PreparedVisibility { get; set; } = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility ModifiedVisibility { get; set; } = System.Windows.Visibility.Collapsed;

        // Кэш изображений
        public System.Windows.Media.Imaging.BitmapSource CachedStatic { get; set; }
        // Кадры гифки здесь НЕ хранятся: полный набор занимает десятки мегабайт,
        // а заклинаний в списке сотни — приложение раздувалось до гигабайтов.
        // Анимацию показывает HoverGif, и только пока курсор наведён.

        // Плейсхолдер
        public System.Windows.Media.Brush PlaceholderBrush { get; set; }
        public string PlaceholderLetter { get; set; } = "";
        public string PlaceholderRune { get; set; } = "✶";
    }
    #endregion

    #region === СТРОКА СПРАВОЧНИКА ===
    /// <summary>
    /// Строка из справочника заклинаний (для SpellbookOverlay).
    /// </summary>
    public sealed class SpellRefRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public string School { get; set; } = "";
        public string Source { get; set; } = "";

        public string Meta => $"{(Level == 0 ? "заговор" : Level + " ур.")} · {(string.IsNullOrWhiteSpace(School) ? "—" : School)}";
    }
    #endregion

    #region === ТЕГ ЗАКЛИНАНИЯ ===
    /// <summary>
    /// Тег заклинания.
    /// </summary>
    public sealed class SpellTag
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
    }
    #endregion
}