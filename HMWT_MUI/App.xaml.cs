using System;
using System.Windows;

namespace HMWT_MUI
{
    /// <summary>
    /// Точка входа приложения.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Готовит отпечаток справочников для комнаты.
        ///
        /// Считается один раз при запуске, потому что нужен при каждом подключении:
        /// если у игроков разные наборы данных, идентификаторы предметов не совпадут,
        /// и в отряде начнётся путаница. Дешёвый счётчик записей тут достаточен —
        /// читать 20 МБ файлов при старте ради этого не нужно.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                DatabaseHelper.Initialize();
                int items = Services.SrdItemImporter.GetCount();
                int spells = Services.SpellJsonImporter.GetCount();
                Net.RoomSession.DataFingerprint = $"items:{items}|spells:{spells}";

                // Масштаб интерфейса читаем один раз при запуске: все окна,
                // которые откроются позже, получат уже готовое значение
                Services.UiScaleManager.Initialize();
            }
            catch (Exception ex)
            {
                // Отпечаток не критичен: без него просто не будет предупреждения
                System.Diagnostics.Debug.WriteLine($"[App] отпечаток справочников: {ex.Message}");
            }
        }
    }
}
