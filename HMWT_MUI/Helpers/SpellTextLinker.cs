using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HMWT_MUI.Services;

namespace HMWT_MUI.Helpers
{
    /// <summary>Размечает в тексте названия заклинаний ссылками.
    /// Список названий берётся из справочника и кэшируется на сессию.</summary>
    public static class SpellTextLinker
    {
        private static List<string> _names;

        public static void Invalidate() => _names = null;

        public static void Build(TextBlock target, string text, Action<string> openSpell)
            => BuildRich(target, text, openSpell, null, null);

        /// <summary>Одновременно подсвечивает заклинания и формулы броска:
        /// в описании Лича есть и «Бросок Камня», и «1d10».</summary>
        public static void BuildRich(TextBlock target, string text,
            Action<string> openSpell, Action<FormulaPart> onDiceClick, RollTextStyle style)
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
            int index = 0;
            int diceIndex = 0;

            while (index < text.Length)
            {
                while (diceIndex < dice.Count && dice[diceIndex].Start < index)
                    diceIndex++;

                int spellAt;
                string spellName;
                FindNextSpell(text, index, names, out spellAt, out spellName);

                int diceAt = diceIndex < dice.Count ? dice[diceIndex].Start : int.MaxValue;
                if (spellAt < 0 && diceAt == int.MaxValue)
                {
                    target.Inlines.Add(new Run(text.Substring(index)) { Foreground = style.Text });
                    break;
                }

                int next = spellAt >= 0 && spellAt <= diceAt ? spellAt : diceAt;
                if (next > index)
                    target.Inlines.Add(new Run(text.Substring(index, next - index)) { Foreground = style.Text });

                if (spellAt >= 0 && spellAt == next)
                {
                    string visible = text.Substring(spellAt, spellName.Length);
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
                    index = spellAt + spellName.Length;
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
            out int at, out string name)
        {
            at = -1;
            name = null;

            foreach (string candidate in names)
            {
                int found = text.IndexOf(candidate, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) continue;
                int end = found + candidate.Length;
                bool leftOk = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
                bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (!leftOk || !rightOk) continue;
                if (at < 0 || found < at || (found == at && candidate.Length > name.Length))
                {
                    at = found;
                    name = candidate;
                }
            }
        }

        private static List<string> Names()
        {
            if (_names != null && _names.Count > 0) return _names;

            var names = new List<string>();
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
                }
            }
            catch { }

            names.Sort((a, b) => b.Length.CompareTo(a.Length));
            _names = names;
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
