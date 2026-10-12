using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Ввод чисел с дробной частью.
    ///
    /// Пользователь писал вес «2.5» — и значение сбрасывалось в ноль: разбор шёл через
    /// текущую культуру, а в русской настройке дробный разделитель — запятая,
    /// поэтому точка считалась мусором. Ещё «2....5» ломало разбор.
    ///
    /// Здесь и точка, и запятая означают одно и то же, а разделитель допускается
    /// только один: лишние символы просто не печатаются, поэтому сломать ввод
    /// невозможно. Подсказка в интерфейсе говорит, что можно и то, и другое.
    /// </summary>
    public static class NumberInput
    {
        /// <summary>Символы, которые считаем разделителем дробной части.</summary>
        private static bool IsSeparator(char c) => c == '.' || c == ',';

        /// <summary>
        /// Не даёт напечатать лишний разделитель и буквы. Вешается на PreviewTextInput
        /// поля ввода.
        /// </summary>
        public static void PreviewDigitsAndSeparator(object sender, TextCompositionEventArgs e)
        {
            if (!(sender is TextBox box)) return;

            string current = box.Text ?? "";
            int caret = box.CaretIndex;
            string proposed = current.Insert(Math.Min(caret, current.Length), e.Text);

            if (!IsAcceptable(proposed))
            {
                e.Handled = true;
                return;
            }

            // Разделитель всегда один и всегда точка: так значение одинаково
            // разбирается независимо от настроек языка
            if (e.Text.Length == 1 && IsSeparator(e.Text[0]) && e.Text != ".")
            {
                e.Handled = true;
                int at = Math.Min(caret, current.Length);
                box.Text = current.Insert(at, ".");
                box.CaretIndex = at + 1;
            }
        }

        /// <summary>Проверяет, что строка похожа на неотрицательное число.</summary>
        public static bool IsAcceptable(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;

            int separators = 0;
            foreach (char c in text)
            {
                if (IsSeparator(c)) { separators++; continue; }
                if (!char.IsDigit(c)) return false;
            }

            return separators <= 1;
        }

        /// <summary>
        /// Разбирает число, принимая и точку, и запятую. Возвращает false, если
        /// в строке не число, — тогда вызывающий сам решает, что показать.
        /// </summary>
        public static bool TryParse(string text, out decimal value)
        {
            value = 0;

            string raw = (text ?? "").Trim();
            if (raw.Length == 0) return false;

            // Разделителей может быть больше одного (например, при вставке из
            // другого поля) — оставляем только первый
            var sb = new StringBuilder(raw.Length);
            // Больше одного разделителя — это уже не число («2.5.5»), и молча
            // склеивать его в 2.55 нельзя: игрок увидит не то, что печатал
            int separators = 0;
            foreach (char c in raw)
                if (IsSeparator(c)) separators++;
            if (separators > 1) return false;

            string normalized = raw.Replace(',', '.');

            return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Разбор для мест, где нужен double и «мусор = ноль».</summary>
        public static double ToDouble(string text)
            => TryParse(text, out decimal value) ? (double)value : 0;

        /// <summary>Разбор для мест, где нужен decimal и «мусор = ноль».</summary>
        public static decimal ToDecimal(string text)
            => TryParse(text, out decimal value) ? value : 0;

        /// <summary>Разбор с ограничением: подходит для количества и тому подобного.</summary>
        public static bool TryParseInt(string text, out int value)
        {
            value = 0;
            decimal parsed;
            if (!TryParse(text, out parsed)) return false;
            if (parsed < int.MinValue || parsed > int.MaxValue) return false;
            value = (int)parsed;
            return true;
        }

        /// <summary>Подсказка для полей с дробным числом.</summary>
        public const string FractionHint = "Можно точку или запятую: например 2.5 или 2,5";
    }

    /// <summary>Подсказка про ввод числа, которую удобно вешать на поля в XAML.</summary>
    public static class NumberInputHints
    {
        public static string Fraction => NumberInput.FractionHint;
    }
}
