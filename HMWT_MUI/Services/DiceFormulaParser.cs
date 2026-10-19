using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HMWT_MUI.Services
{
    /// <summary>Тип урона в терминах правил (для подписи броска и подсветки).</summary>
    public enum DamageKind
    {
        None = 0,
        Bludgeoning,
        Piercing,
        Slashing,
        Acid,
        Cold,
        Fire,
        Force,
        Lightning,
        Necrotic,
        Poison,
        Psychic,
        Radiant,
        Thunder
    }

    /// <summary>
    /// Одна найденная в тексте формула броска: сама формула, её место в строке
    /// и (если удалось понять) тип урона, который к ней относится.
    ///
    /// Пример: в строке «2к8+6 колющим + 3d10 психическим» будет две части —
    /// «2к8+6» (колющий) и «3d10» (психический).
    /// </summary>
    public sealed class FormulaPart
    {
        /// <summary>Формула как в тексте: «2к8+6».</summary>
        public string Formula { get; set; } = "";

        /// <summary>Формула, приведённая к латинице: «2d8+6». Её понимает DiceRoller.</summary>
        public string Normalized { get; set; } = "";

        /// <summary>Позиция формулы в исходном тексте.</summary>
        public int Start { get; set; }

        /// <summary>Длина формулы в исходном тексте (может включать пробелы).</summary>
        public int Length { get; set; }

        /// <summary>Тип урона, если он написан рядом с формулой.</summary>
        public DamageKind Damage { get; set; }

        /// <summary>Слова про урон, как их написал пользователь: «колющим», «урона огнём».</summary>
        public string DamageText { get; set; } = "";

        /// <summary>Среднее значение, если оно указано перед формулой: «5 (1к6+2)».</summary>
        public int? Average { get; set; }

        /// <summary>Число кубиков.</summary>
        public int Count { get; set; }

        /// <summary>Граней у кубика.</summary>
        public int Sides { get; set; }

        /// <summary>Числовой модификатор формулы.</summary>
        public int Modifier { get; set; }

        /// <summary>Подпись для окна результата: «2к8+6 колющего урона».</summary>
        public string Describe()
        {
            string dice = Formula;
            if (Damage == DamageKind.None) return dice;
            return $"{dice} ({DiceFormulaParser.DamageWord(Damage)})";
        }

        /// <summary>Средний урон этой части (для подсказки «в среднем»).</summary>
        public int AverageDamage => Count * (Sides + 1) / 2 + Modifier;

        public override string ToString() => $"{Formula} [{Damage}]";
    }

    /// <summary>
    /// Разбор «человеческих» записей броска.
    ///
    /// Нужен бестиарию: мастер пишет урон как в книге — «2к8+6 колющим +
    /// 3d10 психическим», — а программа должна подсветить обе формулы и уметь
    /// бросить каждую. Отдельно разбирается попадание: «+11» означает бросок
    /// d20 с модификатором +11.
    ///
    /// Класс намеренно терпимый: непонятный текст не ошибка, а просто текст.
    /// </summary>
    public static class DiceFormulaParser
    {
        /// <summary>
        /// Буква кубика во всех написаниях, которые встречаются на практике:
        /// латинская «d»/«D», русская «к»/«К», а также «в» и «д» — их получают,
        /// набрав «d» в русской раскладке (на клавише d стоит «в»).
        /// </summary>
        private const string DiceLetter = "[кkdКKDвВдД]";

        /// <summary>
        /// Кубик в тексте. Понимает и «2к6», и «2d6», и «2к6 + 4», и «к8» (один кубик).
        /// Слово «к»/«d» должно стоять между числами, поэтому обычный текст не задевается.
        ///
        /// Последний lookahead отсекает случай «1к10+1к6»: без него плюс и первая
        /// цифра второй кости принимались за модификатор, и вместо двух костей
        /// выходило «1к10+1» и «к6». Проверяем именно «кЦ» — иначе пострадало бы
        /// «2к8+6 колющим», где после добавки идёт слово на «к».
        /// </summary>
        private static readonly Regex DiceRegex = new Regex(
            @"(?<!\d)(?<count>\d{0,3})\s*" + DiceLetter + @"\s*(?<sides>\d{1,4})(?:\s*(?<sign>[+-])\s*(?<mod>\d{1,4})(?!\s*" + DiceLetter + @"\s*\d))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Одна кость в начале строки — для разбора выражения целиком.</summary>
        private static readonly Regex TermDiceRegex = new Regex(
            @"^(?<sign>[+-])?(?<count>\d{0,3})\s*" + DiceLetter + @"\s*(?<sides>\d{1,4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Числовая добавка в начале строки.</summary>
        private static readonly Regex TermNumberRegex = new Regex(
            @"^(?<sign>[+-])\s*(?<value>\d{1,4})(?![\d" + DiceLetter + @"])",
            RegexOptions.Compiled);

        /// <summary>Добавка-характеристика в начале строки: «+СИЛ», «+лов».</summary>
        private static readonly Regex TermAbilityRegex = new Regex(
            @"^(?<sign>[+-])\s*(?<name>[А-Яа-яЁёA-Za-z]{2,6})",
            RegexOptions.Compiled);

        /// <summary>Среднее перед формулой: «5 (1к6 + 2)».</summary>
        private static readonly Regex AveragePrefixRegex = new Regex(
            @"(?<avg>\d{1,4})\s*\(\s*$",
            RegexOptions.Compiled);

        /// <summary>Попадание вида «+11» или «-3».</summary>
        private static readonly Regex SignedNumberRegex = new Regex(
            @"^\s*(?<sign>[+-])?\s*(?<value>\d{1,3})\s*$",
            RegexOptions.Compiled);

        /// <summary>Попадание вида «11 к попаданию», «+4 к попаданию».</summary>
        private static readonly Regex HitWordsRegex = new Regex(
            @"(?<value>[+-]?\s*\d{1,3})\s*к\s*попаданию",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>«Бросок рукопашной атаки: +4» — попадание после двоеточия.</summary>
        private static readonly Regex HitAfterColonRegex = new Regex(
            @"(?:атак\w*|попадани\w*)\s*:\s*(?<value>[+-]?\s*\d{1,3})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Кости урона: «2к8 + 6». Используется там, где нужна ровно одна формула.</summary>
        private static readonly Regex WholeFormulaRegex = new Regex(
            @"^\s*(?<count>\d{0,3})\s*[кkd]\s*(?<sides>\d{1,4})\s*(?:(?<sign>[+-])\s*(?<mod>\d{1,4}))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Опасность: «1/4 (50 опыта)», «10», «17 (18 000 опыта)», «1/2 (100 опыта; БВ +2)».</summary>
        private static readonly Regex ChallengeRegex = new Regex(
            @"^\s*(?<cr>\d{1,3}\s*/\s*\d{1,3}|\d{1,3})\s*(?:\((?<xp>[\d\s\u00A0]+)\s*опыт\w*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Названия характеристик, которые можно писать в формуле: «+СИЛ», «+харизма», «+cha».
        ///
        /// Нужны, чтобы отличить характеристику от обычного слова после формулы:
        /// «1к8+СИЛ» — это добавка, а «1к8+урон» — нет.
        /// </summary>
        private static readonly string[] AbilityWords =
        {
            "сил", "сила", "силы", "лов", "ловкость", "ловкости",
            "тел", "телосложение", "инт", "интеллект",
            "мдр", "мудрость", "хар", "харизма",
            "str", "strength", "dex", "dexterity", "con", "constitution",
            "int", "intelligence", "wis", "wisdom", "cha", "charisma"
        };

        /// <summary>Похоже ли слово на название характеристики.</summary>
        public static bool IsAbilityWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return false;
            string w = word.Trim().ToLowerInvariant();
            foreach (string known in AbilityWords) if (known == w) return true;
            return false;
        }
        private static readonly string[] DamageWords =
        {
            "дробящ", "колющ", "рубящ", "кислот", "холод", "огн", "силов", "электрич",
            "некрот", "яд", "психич", "излуч", "звук", "гром"
        };

        // ══════════════════════ ФОРМУЛЫ ══════════════════════

        /// <summary>
        /// Находит в тексте все формулы кубиков вместе с типом урона рядом.
        /// Возвращает пустой список, если формул нет.
        /// </summary>
        public static List<FormulaPart> ParseDamages(string text)
        {
            var result = new List<FormulaPart>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var matches = DiceRegex.Matches(text);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var part = BuildPart(m, text);
                if (part == null) continue;

                // Хвост до следующей формулы — оттуда берём слова про урон
                int tailStart = m.Index + m.Length;
                int tailEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                string tail = text.Substring(tailStart, Math.Max(0, tailEnd - tailStart));

                var (kind, words) = DetectDamage(tail);
                part.Damage = kind;
                part.DamageText = words;

                // Если тип урона стоит ПЕРЕД формулой («колющий урон 7 (2к4+2)»)
                if (kind == DamageKind.None)
                {
                    int headStart = i == 0 ? 0 : matches[i - 1].Index + matches[i - 1].Length;
                    string head = text.Substring(headStart, Math.Max(0, m.Index - headStart));
                    var (headKind, headWords) = DetectDamage(head);
                    part.Damage = headKind;
                    part.DamageText = headWords;
                }

                result.Add(part);
            }

            return result;
        }

        /// <summary>Первая формула в тексте или null.</summary>
        public static FormulaPart ParseFirstDamage(string text)
        {
            var list = ParseDamages(text);
            return list.Count > 0 ? list[0] : null;
        }

        private static FormulaPart BuildPart(Match m, string text)
        {
            int count = 1;
            string countText = m.Groups["count"].Value;
            if (countText.Length > 0 && !int.TryParse(countText, out count)) return null;
            if (count == 0) return null;
            if (count > 999) count = 999;

            if (!int.TryParse(m.Groups["sides"].Value, out int sides)) return null;
            if (sides < 2) return null;
            if (sides > 1000) sides = 1000;

            int modifier = 0;
            if (m.Groups["mod"].Success && int.TryParse(m.Groups["mod"].Value, out int modValue))
                modifier = m.Groups["sign"].Value == "-" ? -modValue : modValue;

            // Добавка-характеристика сразу после формулы: «1к8+СИЛ», «2к6+харизма».
            // DiceRegex ловит только числа, поэтому без этого «+СИЛ» терялось —
            // и атака с такой формулой считалась без модификатора характеристики
            // Пробелы вокруг плюса — обычное дело: «1к8 + ХАР». Раньше хвост
            // сравнивался без TrimStart, знак «+» не находился, и характеристика
            // терялась — формула считалась как «1к8». Поэтому сначала срезаем
            // пробелы, а уже потом ищем знак и название.
            string abilityText = "";
            string restAfterDice = text.Substring(m.Index + m.Length).TrimStart();
            var abilityMatch = TermAbilityRegex.Match(restAfterDice);
            if (abilityMatch.Success && IsAbilityWord(abilityMatch.Groups["name"].Value))
            {
                string abilityName = abilityMatch.Groups["name"].Value.ToUpperInvariant();
                abilityText = (abilityMatch.Groups["sign"].Value == "-" ? "-" : "+") + abilityName;

                // После характеристики может стоять ещё число: «1к10 + МДР + 2».
                var tailNumber = TermNumberRegex.Match(restAfterDice.Substring(abilityMatch.Length).TrimStart());
                if (tailNumber.Success)
                    abilityText += tailNumber.Groups["sign"].Value + tailNumber.Groups["value"].Value;
            }

            var part = new FormulaPart
            {
                Formula = m.Value.Trim() + abilityText,
                Normalized = $"{count}d{sides}{(modifier > 0 ? "+" + modifier : modifier < 0 ? modifier.ToString() : "")}{abilityText}",
                Start = m.Index,
                Length = m.Length + abilityText.Length,
                Count = count,
                Sides = sides,
                Modifier = modifier
            };

            // Среднее значение перед формулой: «5 (1к6+2)»
            string head = text.Substring(0, m.Index);
            var avg = AveragePrefixRegex.Match(head);
            if (avg.Success && int.TryParse(avg.Groups["avg"].Value, out int average))
                part.Average = average;

            return part;
        }

        /// <summary>
        /// Ищет слова про урон в куске текста. Возвращает тип и сами слова
        /// (чтобы подпись броска была как у пользователя).
        /// </summary>
        private static (DamageKind Kind, string Words) DetectDamage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return (DamageKind.None, "");

            // Чистим от служебных слов, чтобы «урона» не мешало
            string lower = text.ToLowerInvariant();
            foreach (string stem in DamageWords)
            {
                int idx = lower.IndexOf(stem, StringComparison.Ordinal);
                if (idx < 0) continue;

                var kind = KindForStem(stem);

                // Берём слово целиком (до 3 слов вокруг, но без знаков «+», «,», «.»)
                int start = idx;
                while (start > 0 && !IsSeparator(text[start - 1])) start--;
                int end = idx + stem.Length;
                while (end < text.Length && !IsSeparator(text[end])) end++;

                string words = text.Substring(start, end - start).Trim();
                if (words.Length == 0) words = lower.Substring(idx, stem.Length);
                return (kind, words);
            }

            return (DamageKind.None, "");
        }

        private static bool IsSeparator(char c)
            => c == '+' || c == ',' || c == '.' || c == ';' || c == '(' || c == ')' || c == '/' || c == ':' || c == '-';

        private static DamageKind KindForStem(string stem)
        {
            switch (stem)
            {
                case "дробящ": return DamageKind.Bludgeoning;
                case "колющ": return DamageKind.Piercing;
                case "рубящ": return DamageKind.Slashing;
                case "кислот": return DamageKind.Acid;
                case "холод": return DamageKind.Cold;
                case "огн": return DamageKind.Fire;
                case "силов": return DamageKind.Force;
                case "электрич": return DamageKind.Lightning;
                case "некрот": return DamageKind.Necrotic;
                case "яд": return DamageKind.Poison;
                case "психич": return DamageKind.Psychic;
                case "излуч": return DamageKind.Radiant;
                case "звук": return DamageKind.Thunder;
                case "гром": return DamageKind.Thunder;
                default: return DamageKind.None;
            }
        }

        /// <summary>Русское название типа урона: для подписи броска.</summary>
        public static string DamageWord(DamageKind kind)
        {
            switch (kind)
            {
                case DamageKind.Bludgeoning: return "дробящий";
                case DamageKind.Piercing: return "колющий";
                case DamageKind.Slashing: return "рубящий";
                case DamageKind.Acid: return "кислота";
                case DamageKind.Cold: return "холод";
                case DamageKind.Fire: return "огонь";
                case DamageKind.Force: return "силовое поле";
                case DamageKind.Lightning: return "электричество";
                case DamageKind.Necrotic: return "некротическая энергия";
                case DamageKind.Poison: return "яд";
                case DamageKind.Psychic: return "психическая энергия";
                case DamageKind.Radiant: return "излучение";
                case DamageKind.Thunder: return "звук";
                default: return "";
            }
        }

        /// <summary>Все типы урона списком — для подсказок и выпадающих списков.</summary>
        public static List<string> AllDamageWords()
            => DamageWords.Select(stem => DamageWord(KindForStem(stem)))
                          .Distinct()
                          .Where(w => w.Length > 0)
                          .ToList();

        // ══════════════════════ ПОПАДАНИЕ ══════════════════════

        /// <summary>
        /// Разбирает запись попадания. «+11» — это бросок d20 с +11.
        /// Понимает также «11 к попаданию», «+4 к попаданию» и «атаки: +4».
        /// </summary>
        public static bool TryParseHitBonus(string text, out int bonus)
        {
            bonus = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.Trim();

            var simple = SignedNumberRegex.Match(trimmed);
            if (simple.Success)
            {
                int value = int.Parse(simple.Groups["value"].Value, CultureInfo.InvariantCulture);
                bonus = simple.Groups["sign"].Value == "-" ? -value : value;
                return true;
            }

            var words = HitWordsRegex.Match(trimmed);
            if (words.Success && TryNumber(words.Groups["value"].Value, out bonus)) return true;

            var colon = HitAfterColonRegex.Match(trimmed);
            if (colon.Success && TryNumber(colon.Groups["value"].Value, out bonus)) return true;

            return false;
        }

        private static bool TryNumber(string raw, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string cleaned = raw.Replace(" ", "").Replace("\u00A0", "");
            return int.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Формула броска атаки по бонусу попадания: 11 → «1d20+11».</summary>
        public static string BuildAttackFormula(int bonus)
            => bonus == 0 ? "1d20" : $"1d20{(bonus > 0 ? "+" : "")}{bonus}";

        // ══════════════════════ ПРОСТЫЕ ПРОВЕРКИ ══════════════════════

        /// <summary>
        /// Проверяет, что строка — одна законченная формула броска, и приводит её
        /// к латинице. Понимает «2к6+3», «1d20-2», «d8».
        /// </summary>
        public static bool TryParseWholeFormula(string text, out string normalized,
            out int count, out int sides, out int modifier)
        {
            normalized = "";
            count = 1;
            sides = 0;
            modifier = 0;

            if (string.IsNullOrWhiteSpace(text)) return false;

            var m = WholeFormulaRegex.Match(text);
            if (!m.Success) return false;

            string countText = m.Groups["count"].Value;
            if (countText.Length > 0 && !int.TryParse(countText, out count)) return false;
            if (count == 0) return false;
            if (!int.TryParse(m.Groups["sides"].Value, out sides)) return false;
            if (sides < 2) return false;

            if (m.Groups["mod"].Success && int.TryParse(m.Groups["mod"].Value, out int modValue))
                modifier = m.Groups["sign"].Value == "-" ? -modValue : modValue;

            normalized = $"{count}d{sides}{(modifier > 0 ? "+" + modifier : modifier < 0 ? modifier.ToString() : "")}";
            return true;
        }

        /// <summary>Есть ли в тексте хотя бы одна формула кубика.</summary>
        public static bool ContainsFormula(string text)
            => !string.IsNullOrWhiteSpace(text) && DiceRegex.IsMatch(text);

        // ══════════════════════ ВЫРАЖЕНИЕ ИЗ НЕСКОЛЬКИХ ГРУПП ══════════════════════

        /// <summary>
        /// Один член выражения урона: кости («1к10»), числовая добавка («+3»)
        /// или добавка-характеристика («+СИЛ»).
        /// </summary>
        public sealed class DiceTerm
        {
            /// <summary>Число костей (0 — это не кости, а добавка).</summary>
            public int Count { get; set; }

            /// <summary>Граней у кубика (0 — добавка).</summary>
            public int Sides { get; set; }

            /// <summary>Знак перед членом: 1 или −1.</summary>
            public int Sign { get; set; } = 1;

            /// <summary>Числовая добавка (без знака — знак в <see cref="Sign"/>).</summary>
            public int Modifier { get; set; }

            /// <summary>Название характеристики вместо числа («СИЛ»), иначе пусто.</summary>
            public string Ability { get; set; } = "";

            public bool IsDice => Sides >= 2 && Count > 0;

            public bool IsAbility => !IsDice && Ability.Length > 0;

            /// <summary>Средний вклад этого члена с учётом знака.</summary>
            public int Average
            {
                get
                {
                    if (IsDice) return Sign * (Count * (Sides + 1) / 2);
                    if (IsAbility) return 0;
                    return Sign * Modifier;
                }
            }

            public override string ToString()
                => IsDice ? $"{Sign:+#;-#;0}{Count}d{Sides}"
                          : (IsAbility ? $"{Sign:+#;-#;0}{Ability}" : (Sign * Modifier).ToString("+#;-#;0"));
        }

        /// <summary>
        /// Разбирает выражение урона целиком: «1к10+1к6», «2к6+3», «1к8+СИЛ», «1d20-2».
        ///
        /// Нужен там, где костей несколько: обычный разбор понимал только одну
        /// группу, и вторая кость (добавка за уровень выше) в бросок не попадала.
        /// </summary>
        public static bool TryParseExpression(string formula, out List<DiceTerm> terms, out string error)
        {
            terms = new List<DiceTerm>();
            error = null;

            if (string.IsNullOrWhiteSpace(formula))
            {
                error = "Пустое выражение";
                return false;
            }

            string text = (formula ?? "").Replace(" ", "").Replace("\u00A0", "");

            // Числа и характеристики в формулах урона пишут и без знака:
            // «1», «ЛОВ», «1+ИНТ». Раньше регулярки требовали «+»/«-» и такие
            // части уходили в запасной d20. Добавляем знак первой части.
            if (text.Length > 0 && text[0] != '+' && text[0] != '-')
                text = "+" + text;

            int position = 0;

            while (position < text.Length)
            {
                string rest = text.Substring(position);

                var dice = TermDiceRegex.Match(rest);
                if (dice.Success)
                {
                    int count = 1;
                    string countText = dice.Groups["count"].Value;
                    if (countText.Length > 0 && !int.TryParse(countText, out count)) count = 1;
                    if (count < 1) count = 1;
                    if (count > 999) count = 999;

                    if (!int.TryParse(dice.Groups["sides"].Value, out int sides) || sides < 2)
                    {
                        error = $"Не понял гранность кубика: «{dice.Value}»";
                        return false;
                    }
                    if (sides > 1000) sides = 1000;

                    terms.Add(new DiceTerm
                    {
                        Count = count,
                        Sides = sides,
                        Sign = dice.Groups["sign"].Value == "-" ? -1 : 1
                    });
                    position += dice.Length;
                    continue;
                }

                var number = TermNumberRegex.Match(rest);
                if (number.Success)
                {
                    int value = int.Parse(number.Groups["value"].Value);
                    terms.Add(new DiceTerm
                    {
                        Modifier = value,
                        Sign = number.Groups["sign"].Value == "-" ? -1 : 1
                    });
                    position += number.Length;
                    continue;
                }

                var ability = TermAbilityRegex.Match(rest);
                if (ability.Success)
                {
                    terms.Add(new DiceTerm
                    {
                        Ability = ability.Groups["name"].Value,
                        Sign = ability.Groups["sign"].Value == "-" ? -1 : 1
                    });
                    position += ability.Length;
                    continue;
                }

                error = $"Не понял «{rest}» в формуле урона";
                return false;
            }

            if (terms.Count == 0)
            {
                error = "В формуле нет ни кубиков, ни добавок";
                return false;
            }

            return true;
        }

        /// <summary>Средний урон выражения: «2к6+3» → 10, «1к10+1к6» → 9.</summary>
        public static int AverageExpression(string formula)
        {
            if (!TryParseExpression(formula, out var terms, out _)) return 0;

            int total = 0;
            foreach (var term in terms) total += term.Average;
            return total;
        }

        /// <summary>Средний урон формулы: 2к8+6 → 15. Для «пустой» формулы вернёт 0.</summary>
        public static int AverageDamage(string formula)
        {
            if (!TryParseWholeFormula(formula, out _, out int count, out int sides, out int modifier)) return 0;
            return count * (sides + 1) / 2 + modifier;
        }

        /// <summary>
        /// Приводит формулу к виду, понятному DiceRoller: меняет «к», «в» и «д»
        /// на «d» и убирает пробелы. «в» — это та же «d», набранная в русской
        /// раскладке, поэтому «2в10» и «2d10» для программы одно и то же.
        /// </summary>
        public static string Normalize(string formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return "";
            var sb = new StringBuilder(formula.Length);
            foreach (char c in formula)
            {
                if (c == ' ' || c == '\u00A0' || c == '\t') continue;
                sb.Append(IsDiceLetter(c) ? 'd' : c);
            }
            return sb.ToString();
        }

        /// <summary>Считается ли символ буквой кубика.</summary>
        public static bool IsDiceLetter(char c)
            => c == 'd' || c == 'D' || c == 'к' || c == 'К' || c == 'в' || c == 'В' || c == 'д' || c == 'Д';

        /// <summary>Подсказка про написание формул — одна на всё приложение.</summary>
        public const string FormulaHint = "Формула: 2к10, 2d10 или 2в10 — как удобно; можно 1к8+3";

        // ══════════════════════ ОПАСНОСТЬ ══════════════════════

        /// <summary>
        /// Разбирает опасность существа: «1/4 (50 опыта)» → 0.25 и 50 опыта.
        /// Если опыт не написан, считается по таблице правил.
        /// </summary>
        public static bool TryParseChallenge(string text, out double rating, out int experience)
        {
            rating = 0;
            experience = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var m = ChallengeRegex.Match(text);
            if (!m.Success) return false;

            string cr = m.Groups["cr"].Value.Replace(" ", "");
            if (cr.Contains("/"))
            {
                var parts = cr.Split('/');
                if (parts.Length != 2) return false;
                if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double num)) return false;
                if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double den)) return false;
                if (den <= 0) return false;
                rating = num / den;
            }
            else if (!double.TryParse(cr, NumberStyles.Any, CultureInfo.InvariantCulture, out rating))
            {
                return false;
            }

            if (m.Groups["xp"].Success)
            {
                string xpRaw = m.Groups["xp"].Value.Replace(" ", "").Replace("\u00A0", "");
                if (int.TryParse(xpRaw, out int xp)) experience = xp;
            }

            if (experience <= 0) experience = ExperienceForRating(rating);
            return true;
        }

        /// <summary>
        /// Опыт по опасности — таблица правил.
        ///
        /// Дробные значения (0, 1/8, 1/4, 1/2) идут отдельно: у них своя шкала,
        /// и «умножение на восемь» давало бы неверные числа.
        /// </summary>
        public static int ExperienceForRating(double rating)
        {
            if (rating < 1)
            {
                if (rating <= 0) return 10;
                if (rating <= 0.125) return 25;
                if (rating <= 0.25) return 50;
                return 100;                     // 1/2
            }

            int[] table =
            {
                200, 450, 700, 1100, 1800, 2300, 2900, 3900, 5000, 5900,          // ПО 1..10
                7200, 8400, 10000, 11500, 13000, 15000, 18000, 20000, 22000, 25000, // ПО 11..20
                33000, 41000, 50000, 62000, 75000, 90000, 105000, 120000, 135000, 155000 // ПО 21..30
            };

            int cr = (int)Math.Round(rating);
            if (cr < 1) cr = 1;
            if (cr > 30) return 155000;
            return table[cr - 1];
        }

        /// <summary>Бонус мастерства по опасности существа (общие правила игры).</summary>
        public static int ProficiencyForRating(double rating)
        {
            if (rating <= 4) return 2;
            if (rating <= 8) return 3;
            if (rating <= 12) return 4;
            if (rating <= 16) return 5;
            if (rating <= 20) return 6;
            if (rating <= 24) return 7;
            if (rating <= 28) return 8;
            return 9;
        }
    }
}
