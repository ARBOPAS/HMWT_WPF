using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HMWT_MUI.Services
{
    /// <summary>Один круг заклинаний в карточке существа.</summary>
    public sealed class MonsterSpellGroup
    {
        /// <summary>Круг: 0 — заговоры, 1…9 — обычные круги.</summary>
        public int Level { get; set; }

        /// <summary>Подпись слева: «ЗАГОВОРЫ», «1 КРУГ».</summary>
        public string Title { get; set; } = "";

        /// <summary>Что стоит в скобках: «4 ячейки», «по желанию». Может быть пусто.</summary>
        public string Slots { get; set; } = "";

        /// <summary>Названия заклинаний этого круга.</summary>
        public List<string> Spells { get; } = new List<string>();

        /// <summary>Проза, попавшая в тело круга (длинная строка, а не название).</summary>
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Разобранный блок «Заклинания и псионика».
    ///
    /// В базе и в импортированных файлах это один текст: «Заговоры (по желанию):
    /// Ледянящее прикосновение, Волшебная рука. 1 круг (4): Щит, Туманный шаг.
    /// 2 круг (3): …». Одной строкой он читается как стена слов, поэтому карточка
    /// раскладывает его по кругам.
    ///
    /// Разбор нарочно снисходительный: в данных встречаются «круг» и «уровень»,
    /// «ячейки» и «слоты», переносы строк и просто точки между кругами. Если
    /// разобрать не удалось, карточка рисует текст по-старому — ничего не теряется.
    /// </summary>
    public sealed class MonsterSpellSheet
    {
        public string Intro { get; set; } = "";

        public List<MonsterSpellGroup> Groups { get; } = new List<MonsterSpellGroup>();

        /// <summary>Удалось ли выделить хотя бы один круг.</summary>
        public bool IsStructured => Groups.Count > 0;

        public int SpellCount => Groups.Sum(g => g.Spells.Count);

        /// <summary>
        /// Заголовок круга: «Заговоры», «1 круг», «2 уровень», «3 ур.».
        /// Перед заголовком допускается начало текста, перенос строки, точка,
        /// точка с запятой или запятая — в данных встречаются все варианты.
        /// </summary>
        private static readonly Regex Header = new Regex(
            @"(?<lead>^|[\r\n]+|\.\s+|;\s+|,\s+)\s*" +
            @"(?<title>заговор(?:ы|ов)?|кантрип\w*|\d{1,2}\s*(?:круг\w*|уровень\w*|ур\.?))\s*" +
            @"(?:\((?<slots>[^)\r\n]{0,60})\))?\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static MonsterSpellSheet Parse(string text)
        {
            var sheet = new MonsterSpellSheet();
            if (string.IsNullOrWhiteSpace(text)) return sheet;

            string source = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            var matches = Header.Matches(source);

            if (matches.Count == 0)
            {
                sheet.Intro = source;
                return sheet;
            }

            sheet.Intro = source.Substring(0, matches[0].Index).Trim();

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                int start = match.Index + match.Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : source.Length;
                string body = start < end ? source.Substring(start, end - start) : "";

                string title = NormalizeTitle(match.Groups["title"].Value, out int level);
                var group = new MonsterSpellGroup
                {
                    Level = level,
                    Title = title,
                    Slots = (match.Groups["slots"].Value ?? "").Trim()
                };

                foreach (string spell in SplitSpells(body))
                {
                    // Слишком длинная «строка списка» — это не название, а проза.
                    // Иначе в плашке оказался бы целый абзац.
                    if (spell.Length > 70)
                    {
                        group.Note = group.Note.Length > 0 ? group.Note + " " + spell : spell;
                        continue;
                    }
                    group.Spells.Add(spell);
                }

                if (group.Spells.Count == 0 && group.Note.Length == 0) continue;
                sheet.Groups.Add(group);
            }

            return sheet;
        }

        /// <summary>Собирает текст обратно в канонический вид: один круг — одна строка.</summary>
        public static string Format(MonsterSpellSheet sheet)
        {
            if (sheet == null || !sheet.IsStructured) return null;

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(sheet.Intro)) builder.AppendLine(sheet.Intro.Trim());

            foreach (var group in sheet.Groups)
            {
                string head = group.Level == 0 ? "Заговоры" : group.Level + " круг";
                if (group.Slots.Length > 0) head += " (" + group.Slots + ")";
                builder.Append(head).Append(": ").AppendLine(string.Join(", ", group.Spells));
                if (group.Note.Length > 0) builder.AppendLine(group.Note);
            }

            return builder.ToString().Trim();
        }

        public static string Format(string text) => Format(Parse(text));

        /// <summary>Название без английского в скобках: «Щит (Shield)» → «Щит».</summary>
        public static string DisplayName(string spell)
        {
            string value = (spell ?? "").Trim();
            if (value.Length == 0) return value;

            string baseName = Regex.Replace(value, @"\s*[\(\[].*?[\)\]]\s*$", "").Trim();
            return baseName.Length >= 2 ? baseName : value;
        }

        private static string NormalizeTitle(string raw, out int level)
        {
            string value = (raw ?? "").Trim().ToLowerInvariant();

            if (value.StartsWith("заговор", StringComparison.Ordinal) ||
                value.StartsWith("кантрип", StringComparison.Ordinal))
            {
                level = 0;
                return "ЗАГОВОРЫ";
            }

            var digits = Regex.Match(value, @"\d{1,2}");
            level = digits.Success ? Math.Max(1, Math.Min(9, int.Parse(digits.Value))) : 1;
            return level + " КРУГ";
        }

        private static IEnumerable<string> SplitSpells(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) yield break;

            foreach (string raw in Regex.Split(body, @"[,;\n]+"))
            {
                string value = (raw ?? "").Trim();

                // Маркеры списка из чужих текстов: «- Щит», «• Щит».
                value = Regex.Replace(value, @"^[\-–—•·\*\s]+", "").Trim();
                value = value.TrimEnd('.', ' ');

                if (value.Length > 0) yield return value;
            }
        }
    }
}
