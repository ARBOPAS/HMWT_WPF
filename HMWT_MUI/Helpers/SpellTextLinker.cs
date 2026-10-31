using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;
using HMWT_MUI.Services;

namespace HMWT_MUI.Helpers
{
    /// <summary>Размечает в тексте названия заклинаний ссылками.
    /// Список названий берётся из справочника и кэшируется на сессию.</summary>
    public static class SpellTextLinker
    {
        private static List<string> _names;
        private static List<(string Key, string Open)> _fuzzy;

        public static void Invalidate() { _names = null; _fuzzy = null; }

        private static readonly Regex RussianWord = new Regex(
            @"[А-Яа-яЁё]{5,}", RegexOptions.Compiled);

        public static void Build(TextBlock target, string text, Action<string> openSpell)
            => BuildRich(target, text, openSpell, null, null, null);

        private static readonly Regex HitBonusPattern = new Regex(
            @"(?<!\d)(?<hit>[+-]\s*\d{1,2})(?=\s*(?:к\s+)?(?:попаданию|атаке|атаками))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Одновременно подсвечивает заклинания и формулы броска:
        /// в описании Лича есть и «Бросок Камня», и «1d10».</summary>
        public static void BuildRich(TextBlock target, string text,
            Action<string> openSpell, Action<FormulaPart> onDiceClick, RollTextStyle style,
            Action<int> onHitClick = null)
        {
            if (target == null) return;
            target.Inlines.Clear();
            if (string.IsNullOrWhiteSpace(text)) return;

            style = style ?? new RollTextStyle();
            if (style.Font != null) target.FontFamily = style.Font;
            if (style.FontSize > 0) target.FontSize = style.FontSize;
            if (style.Text != null) target.Foreground = style.Text;
            target.TextWrapping = TextWrapping.Wrap;

            var names = Names();
            var dice = DiceFormulaParser.ParseDamages(text) ?? new List<FormulaPart>();
            var hits = new List<(int Start, int Length, int Bonus)>();
            if (onHitClick != null)
            {
                foreach (Match m in HitBonusPattern.Matches(text))
                {
                    string raw = m.Groups["hit"].Value.Replace(" ", "");
                    if (int.TryParse(raw.Replace("+", ""), out int value))
                        hits.Add((m.Index, m.Length, raw.StartsWith("-") ? -value : value));
                }
            }

            int index = 0;
            int diceIndex = 0;
            int hitIndex = 0;

            while (index < text.Length)
            {
                while (diceIndex < dice.Count && dice[diceIndex].Start < index)
                    diceIndex++;
                while (hitIndex < hits.Count && hits[hitIndex].Start < index)
                    hitIndex++;

                int spellAt;
                string spellName;
                int spellLength;
                FindNextSpell(text, index, names, out spellAt, out spellName, out spellLength);

                int diceAt = diceIndex < dice.Count ? dice[diceIndex].Start : int.MaxValue;
                int hitAt = hitIndex < hits.Count ? hits[hitIndex].Start : int.MaxValue;
                if (spellAt < 0 && diceAt == int.MaxValue && hitAt == int.MaxValue)
                {
                    target.Inlines.Add(new Run(text.Substring(index)) { Foreground = style.Text });
                    break;
                }

                int next = Math.Min(diceAt, hitAt);
                if (spellAt >= 0 && spellAt < next) next = spellAt;
                if (next > index)
                    target.Inlines.Add(new Run(text.Substring(index, next - index)) { Foreground = style.Text });

                if (spellAt >= 0 && spellAt == next)
                {
                    string visible = text.Substring(spellAt, spellLength);
                    var link = new Hyperlink(new Run(visible))
                    {
                        // Как кнопки броска: тёмная плашка и светлый текст,
                        // чтобы заклинание было видно в длинном описании.
                        Background = (Brush)Application.Current.FindResource("B.PanelHi"),
                        Foreground = (Brush)Application.Current.FindResource("B.Frost"),
                        FontWeight = FontWeights.SemiBold,
                        TextDecorations = null,
                        Cursor = Cursors.Hand,
                        ToolTip = "Открыть заклинание «" + spellName + "»"
                    };
                    link.Click += (s, e) => openSpell?.Invoke(spellName);
                    target.Inlines.Add(link);
                    index = spellAt + spellLength;
                }
                else if (hitAt >= 0 && hitAt == next)
                {
                    var hit = hits[hitIndex];
                    var link = RollTextRenderer.BuildHitLink(
                        text.Substring(hit.Start, hit.Length).Replace(" ", ""), onHitClick, style);
                    if (link != null)
                    {
                        target.Inlines.Add(link);
                        index = hit.Start + hit.Length;
                    }
                    else
                    {
                        target.Inlines.Add(new Run(text.Substring(hit.Start, hit.Length))
                        {
                            Foreground = style.Text
                        });
                        index = hit.Start + hit.Length;
                    }
                    hitIndex++;
                }
                else
                {
                    var part = dice[diceIndex];
                    target.Inlines.Add(RollTextRenderer.BuildDiceLink(part, onDiceClick, style));
                    index = part.Start + part.Length;
                    diceIndex++;
                }
            }
        }

        private static void FindNextSpell(string text, int start, List<string> names,
            out int at, out string name, out int length)
        {
            at = -1;
            name = null;
            length = 0;

            // Сначала точные названия: они важнее любых похожих слов.
            foreach (string candidate in names)
            {
                int found = text.IndexOf(candidate, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) continue;
                int end = found + candidate.Length;
                bool leftOk = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
                bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (!leftOk || !rightOk) continue;
                if (at < 0 || found < at || (found == at && candidate.Length > length))
                {
                    at = found;
                    name = candidate;
                    length = candidate.Length;
                }
            }

            // В бестиарии встречаются русские синонимы: «контрзаклинание»
            // вместо справочного «Контрзаклятье (Counterspell)». Ищем
            // ближайшее русское название, отбросив английское в скобках.
            if (_fuzzy == null || _fuzzy.Count == 0) return;

            foreach (Match m in RussianWord.Matches(text))
            {
                if (m.Index < start) continue;
                // Точное название раньше или в этой же позиции — не трогаем.
                if (at >= 0 && m.Index >= at) break;

                string word = NormalizeRussian(m.Value);
                if (word.Length < 5) continue;

                string bestOpen = null;
                double bestScore = 0;
                int bestPrefix = 0;

                foreach (var candidate in _fuzzy)
                {
                    if (Math.Abs(word.Length - candidate.Key.Length) > 4) continue;

                    int prefix = CommonPrefix(word, candidate.Key);
                    if (prefix < 5) continue;

                    int distance = Levenshtein(word, candidate.Key);
                    double score = 1.0 - distance / (double)Math.Max(word.Length, candidate.Key.Length);
                    if (score < 0.64) continue;
                    if (score > bestScore + 0.0001 ||
                        (Math.Abs(score - bestScore) <= 0.0001 && prefix > bestPrefix))
                    {
                        bestScore = score;
                        bestPrefix = prefix;
                        bestOpen = candidate.Open;
                    }
                }

                if (bestOpen != null)
                {
                    at = m.Index;
                    name = bestOpen;
                    length = m.Length;
                    break;
                }
            }
        }

        private static string NormalizeRussian(string value)
            => (value ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');

        private static int CommonPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        private static int Levenshtein(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + cost);
                }
                var swap = previous;
                previous = current;
                current = swap;
            }
            return previous[b.Length];
        }

        private static List<string> Names()
        {
            if (_names != null && _names.Count > 0) return _names;

            var names = new List<string>();
            var fuzzy = new List<(string Key, string Open)>();
            var fuzzyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var row in SpellService.LoadSpellReference())
                {
                    string name = (row.Name ?? "").Trim();
                    if (name.Length < 4) continue;

                    AddName(names, name);
                    // «Подчинение монстра (Dominate Monster)» в бестиарии
                    // часто записано без скобочной части — ссылку всё равно
                    // нужно узнать.
                    string baseName = System.Text.RegularExpressions.Regex.Replace(
                        name, @"\s*[\(\[].*?[\)\]]\s*$", "").Trim();
                    if (baseName.Length >= 4) AddName(names, baseName);

                    // Для нечёткого поиска — только однословные русские
                    // названия: многословные уже ловятся точным сравнением.
                    string key = NormalizeRussian(baseName);
                    if (key.Length >= 5 && !baseName.Contains(" ") && !baseName.Contains("-") &&
                        fuzzyKeys.Add(key))
                    {
                        fuzzy.Add((key, baseName));
                    }
                }
            }
            catch { }

            names.Sort((a, b) => b.Length.CompareTo(a.Length));
            fuzzy.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _names = names;
            _fuzzy = fuzzy;
            return _names;
        }

        private static void AddName(List<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name.Trim());
        }
    }
}
