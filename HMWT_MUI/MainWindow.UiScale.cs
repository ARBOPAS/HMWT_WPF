using System;
using System.Diagnostics;
using HMWT_MUI.Services;

namespace HMWT_MUI
{
    /// <summary>
    /// Масштаб интерфейса в главном окне.
    ///
    /// Вся логика живёт в UiScaleManager — он применяет масштаб ко ВСЕМ окнам,
    /// включая окно персонажа, заклинаний и инвентаря. Здесь только регистрация
    /// главного окна: трансформация берётся из разметки (UiScale внутри UiScaleHost).
    ///
    /// Переключатель масштаба вынесен в отдельное окно настроек: там он рядом
    /// с режимом анимации гифок и другими параметрами.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Регистрирует главное окно в менеджере масштаба.</summary>
        private void InitializeUiScale()
        {
            try
            {
                UiScaleManager.Register(this, UiScale);
                Debug.WriteLine($"[UiScale] главное окно: {UiScaleManager.CurrentPercent}%");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UiScale] запуск: {ex.Message}");
            }
        }
    }
}
