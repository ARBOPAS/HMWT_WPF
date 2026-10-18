using System;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Сравнение имён участника и жетона.
    ///
    /// В трекере дубли получают номер: «Гоблин», «Гоблин 2». В отряде у игрока
    /// имя без номера, поэтому строгое сравнение отклоняло ход жетона, хотя
    /// это его персонаж. Здесь сравниваем и с номером, и без него.
    /// </summary>
    public static class NameMatch
    {
        public static bool SamePerson(string first, string second)
        {
            first = (first ?? "").Trim();
            second = (second ?? "").Trim();
            if (first.Length == 0 || second.Length == 0) return false;

            if (string.Equals(first, second, StringComparison.CurrentCultureIgnoreCase))
                return true;

            return string.Equals(WithoutNumber(first), WithoutNumber(second),
                StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>Убирает хвостовой номер: «Гоблин 2», «Гоблин (2)» → «Гоблин».</summary>
        public static string WithoutNumber(string text)
        {
            text = (text ?? "").Trim();

            while (text.Length > 0)
            {
                int end = text.Length;
                while (end > 0 &&
                       (char.IsDigit(text[end - 1]) || char.IsWhiteSpace(text[end - 1]) ||
                        text[end - 1] == ')' || text[end - 1] == '(' || text[end - 1] == '.'))
                    end--;

                if (end == text.Length || end == 0) return text;
                text = text.Substring(0, end).TrimEnd();
            }

            return text;
        }
    }
}
