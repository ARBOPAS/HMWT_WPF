using System;
using System.Collections.Generic;
using System.Linq;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Навык: как он называется в интерфейсе, в справочнике и в базе.
    ///
    /// Один навык в программе зовётся по-разному: на листе персонажа —
    /// «Скрытность», в файлах SRD — «Stealth», у записи справочника — ключ
    /// «skill-stealth», а в таблице характеристик он лежит колонкой Stealth.
    /// Раньше эти имена жили порознь, и предыстория с русскими названиями
    /// навыков не отмечала ничего: таблица навыков знала только английские ключи,
    /// а ошибка молча проглатывалась. Теперь все написания сведены сюда.
    /// </summary>
    public sealed class SkillInfo
    {
        /// <summary>Ключ приложения и колонка в таблице навыков: «Stealth».</summary>
        public string Key { get; set; } = "";

        /// <summary>Название на листе персонажа: «Скрытность».</summary>
        public string Russian { get; set; } = "";

        /// <summary>Название в справочнике SRD: «Stealth».</summary>
        public string English { get; set; } = "";

        /// <summary>Ключ записи справочника: «skill-stealth».</summary>
        public string SrdIndex { get; set; } = "";

        /// <summary>Характеристика, к которой относится навык: «ЛОВКОСТЬ».</summary>
        public string Ability { get; set; } = "";

        /// <summary>Таблица, где хранится владение навыком.</summary>
        public string Table { get; set; } = "";

        /// <summary>Колонка в этой таблице.</summary>
        public string Column { get; set; } = "";
    }

    /// <summary>
    /// Список навыков игры и разбор их названий.
    ///
    /// Имена навыков приходят из разных мест: игрок печатает по-русски,
    /// справочник SRD отдаёт английские названия и ключи вида «skill-stealth»,
    /// а предыстории в базе хранят то, что записал импорт. Чтобы отметка
    /// владения работала всегда, любое из этих написаний приводится к одному
    /// ключу — <see cref="SkillInfo.Key"/>.
    /// </summary>
    public static class SkillCatalog
    {
        /// <summary>
        /// Восемнадцать навыков в порядке книги правил: по характеристикам,
        /// внутри — как на листе персонажа.
        /// </summary>
        public static readonly IReadOnlyList<SkillInfo> All = new[]
        {
            new SkillInfo { Key = "Athletics",     Russian = "Атлетика",           English = "Athletics",       SrdIndex = "skill-athletics",       Ability = "СИЛА",         Table = "CharacterStrenghtSkills",  Column = "Athletics" },

            new SkillInfo { Key = "Acrobatics",    Russian = "Акробатика",          English = "Acrobatics",      SrdIndex = "skill-acrobatics",      Ability = "ЛОВКОСТЬ",     Table = "CharacterDexteritySkills", Column = "Acrobatics" },
            new SkillInfo { Key = "SleightOfHand", Russian = "Ловкость рук",        English = "Sleight of Hand", SrdIndex = "skill-sleight-of-hand", Ability = "ЛОВКОСТЬ",     Table = "CharacterDexteritySkills", Column = "SleightOfHand" },
            new SkillInfo { Key = "Stealth",       Russian = "Скрытность",          English = "Stealth",         SrdIndex = "skill-stealth",         Ability = "ЛОВКОСТЬ",     Table = "CharacterDexteritySkills", Column = "Stealth" },

            // «Анализ» — это Investigation, «Магия» — Arcana: в переводе книги
            // навыки названы иначе, чем в файлах базы
            new SkillInfo { Key = "Analysis",      Russian = "Анализ",             English = "Investigation",   SrdIndex = "skill-investigation",   Ability = "ИНТЕЛЛЕКТ",    Table = "CharacterIntSkills",       Column = "Analysis" },
            new SkillInfo { Key = "History",       Russian = "История",            English = "History",         SrdIndex = "skill-history",         Ability = "ИНТЕЛЛЕКТ",    Table = "CharacterIntSkills",       Column = "History" },
            new SkillInfo { Key = "Magic",         Russian = "Магия",              English = "Arcana",          SrdIndex = "skill-arcana",          Ability = "ИНТЕЛЛЕКТ",    Table = "CharacterIntSkills",       Column = "Magic" },
            new SkillInfo { Key = "Nature",        Russian = "Природа",            English = "Nature",          SrdIndex = "skill-nature",          Ability = "ИНТЕЛЛЕКТ",    Table = "CharacterIntSkills",       Column = "Nature" },
            new SkillInfo { Key = "Religion",      Russian = "Религия",            English = "Religion",        SrdIndex = "skill-religion",        Ability = "ИНТЕЛЛЕКТ",    Table = "CharacterIntSkills",       Column = "Religion" },

            new SkillInfo { Key = "Perception",    Russian = "Восприятие",         English = "Perception",      SrdIndex = "skill-perception",      Ability = "МУДРОСТЬ",     Table = "CharacterWisdomSkills",    Column = "Perception" },
            new SkillInfo { Key = "Survival",      Russian = "Выживание",          English = "Survival",        SrdIndex = "skill-survival",        Ability = "МУДРОСТЬ",     Table = "CharacterWisdomSkills",    Column = "Survival" },
            new SkillInfo { Key = "Medicine",      Russian = "Медицина",           English = "Medicine",        SrdIndex = "skill-medicine",        Ability = "МУДРОСТЬ",     Table = "CharacterWisdomSkills",    Column = "Medicine" },
            new SkillInfo { Key = "Insight",       Russian = "Проницательность",   English = "Insight",         SrdIndex = "skill-insight",         Ability = "МУДРОСТЬ",     Table = "CharacterWisdomSkills",    Column = "Insight" },
            new SkillInfo { Key = "AnimalCare",    Russian = "Уход за животными",  English = "Animal Handling", SrdIndex = "skill-animal-handling", Ability = "МУДРОСТЬ",     Table = "CharacterWisdomSkills",    Column = "AnimalCare" },

            new SkillInfo { Key = "Performance",   Russian = "Выступление",        English = "Performance",     SrdIndex = "skill-performance",     Ability = "ХАРИЗМА",      Table = "CharacterCharismaSkills",  Column = "Performance" },
            new SkillInfo { Key = "Deception",     Russian = "Обман",              English = "Deception",       SrdIndex = "skill-deception",       Ability = "ХАРИЗМА",      Table = "CharacterCharismaSkills",  Column = "Deception" },
            new SkillInfo { Key = "Persuasion",    Russian = "Убеждение",          English = "Persuasion",      SrdIndex = "skill-persuasion",      Ability = "ХАРИЗМА",      Table = "CharacterCharismaSkills",  Column = "Persuasion" },
            new SkillInfo { Key = "Intimidation",  Russian = "Запугивание",        English = "Intimidation",    SrdIndex = "skill-intimidation",    Ability = "ХАРИЗМА",      Table = "CharacterCharismaSkills",  Column = "Intimidation" },
        };

        /// <summary>Характеристики в порядке листа персонажа.</summary>
        public static readonly string[] AbilityOrder =
        {
            "СИЛА", "ЛОВКОСТЬ", "ТЕЛОСЛОЖЕНИЕ", "ИНТЕЛЛЕКТ", "МУДРОСТЬ", "ХАРИЗМА"
        };

        /// <summary>Навыки одной характеристики — для списков с группировкой.</summary>
        public static List<SkillInfo> ForAbility(string ability)
            => All.Where(s => string.Equals(s.Ability, ability, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>
        /// Приводит любое написание навыка к описанию из списка.
        /// Возвращает null, если название не распознано.
        ///
        /// Понимает «Скрытность», «Stealth», «sleight of hand», «skill-stealth»,
        /// «Skill: Stealth» — то есть всё, что встречается в предысториях,
        /// справочнике SRD и в ручных записях игрока.
        /// </summary>
        public static SkillInfo Resolve(string name)
        {
            string normalized = Normalize(name);
            if (normalized.Length == 0) return null;

            foreach (var skill in All)
            {
                if (normalized == Normalize(skill.Key)) return skill;
                if (normalized == Normalize(skill.Russian)) return skill;
                if (normalized == Normalize(skill.English)) return skill;
                if (normalized == Normalize(skill.SrdIndex)) return skill;
            }

            return null;
        }

        /// <summary>Ключ навыка по любому написанию; null, если не распознали.</summary>
        public static string KeyOf(string name) => Resolve(name)?.Key;

        /// <summary>
        /// Русское название по любому написанию. Незнакомый текст возвращается
        /// как есть: показать непонятую запись лучше, чем потерять её.
        /// </summary>
        public static string RussianOf(string name)
        {
            string trimmed = (name ?? "").Trim();
            return Resolve(trimmed)?.Russian ?? trimmed;
        }

        /// <summary>
        /// Разбирает строку владений навыками («Обман, Скрытность») в ключи.
        /// Незнакомые слова пропускаются: строка могла быть написана руками
        /// и содержать что угодно.
        /// </summary>
        public static List<string> KeysFrom(string text)
        {
            var keys = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return keys;

            foreach (string piece in text.Split(new[] { ',', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string key = KeyOf(piece);
                if (key != null && !keys.Contains(key)) keys.Add(key);
            }
            return keys;
        }

        /// <summary>Собирает строку владений из ключей — в том виде, в каком её хранит база.</summary>
        public static string JoinRussian(IEnumerable<string> keys)
        {
            var names = new List<string>();
            foreach (string key in keys ?? Enumerable.Empty<string>())
            {
                var skill = Resolve(key);
                if (skill != null && !names.Contains(skill.Russian)) names.Add(skill.Russian);
            }
            return string.Join(", ", names);
        }

        /// <summary>
        /// Ключ для сравнения: без регистра, пробелов, дефисов и служебной
        /// приставки «skill». Так «Sleight of Hand» и «skill-sleight-of-hand»
        /// становятся одним и тем же.
        /// </summary>
        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string value = text.Trim().ToLowerInvariant();

            // «Skill: Insight» из старой выгрузки справочника
            int colon = value.IndexOf(':');
            if (colon >= 0) value = value.Substring(colon + 1);

            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }

            string compact = sb.ToString();
            if (compact.StartsWith("skill", StringComparison.Ordinal)) compact = compact.Substring(5);

            // «ё» в названиях навыков не встречается, но пусть будет
            return compact.Replace('ё', 'е');
        }
    }
}
