using HMWT_MUI.Services;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace HMWT_MUI.Helpers
{
    /// <summary>Цвета и шрифт для отрисовки текста с формулами.</summary>
    public sealed class RollTextStyle
    {
        public Brush Text { get; set; } = Brushes.Gainsboro;
        public Brush Dice { get; set; } = Brushes.Goldenrod;
        public Brush DiceBack { get; set; } = new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0xAE, 0x4E));
        public Brush Damage { get; set; } = Brushes.Gray;
        public Brush Hint { get; set; } = Brushes.Gray;
        public FontFamily Font { get; set; } = new FontFamily("Segoe UI");
        public double FontSize { get; set; } = 12;
        public bool Wrap { get; set; } = true;
    }

    /// <summary>
    /// Рисует текст с формулами броска: «2к8+6 колющим + 3к10 психическим»
    /// превращается в обычный текст, где каждая формула — подсвеченная ссылка.
    ///
    /// По нажатию вызывается переданный обработчик: он и бросает кубики.
    /// Так мастеру не нужно ничего копировать руками — увидел формулу, нажал, бросил.
    /// </summary>
    public static class RollTextRenderer
    {
        /// <summary>
        /// Строит TextBlock. Если формул в тексте нет, получится обычный текст.
        /// </summary>
        public static TextBlock Build(string text, Action<FormulaPart> onDiceClick, RollTextStyle style)
        {
            style = style ?? new RollTextStyle();

            var block = new TextBlock
            {
                FontFamily = style.Font,
                FontSize = style.FontSize,
                Foreground = style.Text,
                TextWrapping = style.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
            };

            if (string.IsNullOrEmpty(text))
            {
                block.Inlines.Add(new Run("—") { Foreground = style.Hint });
                return block;
            }

            var parts = DiceFormulaParser.ParseDamages(text);
            if (parts.Count == 0)
            {
                block.Inlines.Add(new Run(text) { Foreground = style.Text });
                return block;
            }

            int position = 0;
            foreach (var part in parts)
            {
                // Текст до формулы — обычный
                if (part.Start > position)
                {
                    string plain = text.Substring(position, part.Start - position);
                    block.Inlines.Add(new Run(plain) { Foreground = style.Text });
                }

                // Сама формула — ссылка с подсказкой
                block.Inlines.Add(BuildDiceLink(part, onDiceClick, style));

                // Слова про урон после формулы оставляем обычным текстом:
                // они и так видны в подсказке ссылки
                position = part.Start + part.Length;
            }

            if (position < text.Length)
                block.Inlines.Add(new Run(text.Substring(position)) { Foreground = style.Text });

            return block;
        }

        /// <summary>Подсвеченная формула, по нажатию — бросок.</summary>
        public static Hyperlink BuildDiceLink(FormulaPart part, Action<FormulaPart> onClick, RollTextStyle style)
        {
            var link = new Hyperlink(new Run(part.Formula))
            {
                Foreground = style.Dice,
                Background = style.DiceBack,
                FontWeight = FontWeights.Bold,
                TextDecorations = null,
                Cursor = Cursors.Hand,
                ToolTip = BuildDiceTooltip(part)
            };

            if (onClick != null)
            {
                link.Click += (s, e) =>
                {
                    e.Handled = true;
                    onClick(part);
                };
            }

            return link;
        }

        private static string BuildDiceTooltip(FormulaPart part)
        {
            string damage = part.Damage == DamageKind.None ? "" : $"\nУрон: {DiceFormulaParser.DamageWord(part.Damage)}";
            string average = part.Count > 0 ? $"\nВ среднем: {part.AverageDamage}" : "";
            return $"Нажмите, чтобы бросить {part.Formula}{damage}{average}";
        }

        /// <summary>
        /// Чип попадания: «+11» — нажатие бросает d20 с этим модификатором.
        /// Возвращает null, если в записи нет числа.
        /// </summary>
        public static Hyperlink BuildHitLink(string hitBonus, Action<int> onClick, RollTextStyle style)
        {
            if (!DiceFormulaParser.TryParseHitBonus(hitBonus, out int bonus)) return null;

            string label = bonus >= 0 ? $"+{bonus}" : bonus.ToString();
            var link = new Hyperlink(new Run(label))
            {
                Foreground = style.Dice,
                Background = style.DiceBack,
                FontWeight = FontWeights.Bold,
                TextDecorations = null,
                Cursor = Cursors.Hand,
                ToolTip = $"Нажмите, чтобы бросить атаку: d20 {label}"
            };

            if (onClick != null)
            {
                link.Click += (s, e) =>
                {
                    e.Handled = true;
                    onClick(bonus);
                };
            }

            return link;
        }

        /// <summary>
        /// Готовая строка попадания: «Попадание: [+11] (в среднем 15 урона)».
        /// Если бонуса нет, вернёт null — заголовок рисовать не нужно.
        /// </summary>
        public static TextBlock BuildHitLine(string hitBonus, string damage, Action<int> onClick, RollTextStyle style)
        {
            style = style ?? new RollTextStyle();
            var link = BuildHitLink(hitBonus, onClick, style);
            if (link == null) return null;

            var block = new TextBlock
            {
                FontFamily = style.Font,
                FontSize = style.FontSize,
                Foreground = style.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };

            block.Inlines.Add(new Run("Попадание: ") { Foreground = style.Hint });
            block.Inlines.Add(link);

            var parts = DiceFormulaParser.ParseDamages(damage);
            if (parts.Count > 0)
            {
                int total = 0;
                foreach (var part in parts) total += part.AverageDamage;
                if (total > 0)
                    block.Inlines.Add(new Run($" · в среднем {total}") { Foreground = style.Hint });
            }

            return block;
        }

        /// <summary>
        /// Разбивает текст на «обычные куски» и формулы — нужно там, где из
        /// разметки строится не TextBlock, а набор кнопок (например список атак).
        /// </summary>
        public static List<(string Text, FormulaPart Formula)> Split(string text)
        {
            var result = new List<(string, FormulaPart)>();
            if (string.IsNullOrEmpty(text)) return result;

            var parts = DiceFormulaParser.ParseDamages(text);
            int position = 0;
            foreach (var part in parts)
            {
                if (part.Start > position)
                    result.Add((text.Substring(position, part.Start - position), null));
                result.Add((part.Formula, part));
                position = part.Start + part.Length;
            }
            if (position < text.Length) result.Add((text.Substring(position), null));
            return result;
        }
    }
}
