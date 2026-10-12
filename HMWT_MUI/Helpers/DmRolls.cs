using HMWT_MUI.Services;
using System;
using System.Diagnostics;
using System.Windows;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Окно, которое умеет бросать кубики для инструментов ДМа.
    ///
    /// Отдельный интерфейс нужен, чтобы бестиарий и трекер не зависели от окна
    /// персонажа напрямую: вкладки можно собрать и проверить без него (например,
    /// в тестах), а бросок всё равно идёт через тот самый DiceRoller окна — тогда
    /// результат попадает в общую панель бросков, в историю и в ленту отряда.
    /// </summary>
    public interface IDmRollHost
    {
        /// <summary>Бросок формулы от имени существа. who и what попадают в подпись.</summary>
        DiceRollResult RollExternal(string formula, string rollType, string who, string what,
            bool showPanel = true, bool publish = true);

        /// <summary>Всплывающая подсказка в окне.</summary>
        void ShowToast(string message, bool isError = false);
    }

    /// <summary>
    /// Связка инструментов ДМа с окном персонажа.
    /// </summary>
    public static class DmRolls
    {
        /// <summary>Окно, умеющее бросать кубики (в тестах может не быть — тогда null).</summary>
        public static IDmRollHost Host(FrameworkElement source)
        {
            try
            {
                if (source == null) return null;
                return Window.GetWindow(source) as IDmRollHost;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmRolls] Host: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Бросок формулы от имени существа.
        ///
        /// formula — «2к6+2», rollType — «Атака», who — «Гоблин», what — «Скимитар».
        /// Возвращает результат или null, если бросить не удалось.
        /// </summary>
        public static DiceRollResult Roll(FrameworkElement source, string formula, string rollType,
            string who, string what, bool showPanel = true, bool publish = true)
        {
            var host = Host(source);
            if (host == null)
            {
                Debug.WriteLine($"[DmRolls] окно не найдено, бросок {formula} пропущен");
                return null;
            }

            return host.RollExternal(formula, rollType, who, what, showPanel, publish);
        }

        /// <summary>Бросок атаки: попадание — это d20 плюс бонус.</summary>
        public static DiceRollResult RollAttack(FrameworkElement source, int hitBonus, string who, string what)
            => Roll(source, DiceFormulaParser.BuildAttackFormula(hitBonus), "Атака", who, what);

        /// <summary>Бросок урона по части формулы: тип урона попадает в подпись.</summary>
        public static DiceRollResult RollDamage(FrameworkElement source, string formula, DamageKind kind,
            string who, string what)
        {
            string label = what;
            string damageWord = DiceFormulaParser.DamageWord(kind);
            if (!string.IsNullOrEmpty(damageWord)) label = $"{what} — {damageWord}";

            return Roll(source, formula, "Урон", who, label);
        }

        /// <summary>Подсказка пользователю средствами окна персонажа.</summary>
        public static void Toast(FrameworkElement source, string text, bool error = false)
        {
            var host = Host(source);
            if (host != null)
            {
                host.ShowToast(text, error);
                return;
            }

            Debug.WriteLine($"[DmRolls] {text}");
        }
    }
}
