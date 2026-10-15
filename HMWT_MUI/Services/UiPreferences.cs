using HMWT_MUI.Helpers;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Настройки внешнего вида: тема оформления и положение полосы вкладок.
    ///
    /// Раньше они жили прямо в App — классе приложения. Из-за этого всякому, кому
    /// нужна была тема, приходилось тянуть за собой всё WPF-приложение: вкладку
    /// настроек нельзя было собрать и проверить отдельно от запуска программы.
    /// </summary>
    public static class UiPreferences
    {
        /// <summary>Ключ настройки с темой оформления.</summary>
        public const string ThemeSettingKey = "Ui_Theme";

        /// <summary>Ключ настройки «вкладки сверху» (вместо левой рейки).</summary>
        public const string TopTabsSettingKey = "Ui_TopTabs";

        /// <summary>
        /// Текущая тема: «dark» (по умолчанию) или «light».
        /// Неизвестное значение приводим к тёмной: без палитры интерфейс не соберётся.
        /// </summary>
        public static string CurrentTheme
        {
            get
            {
                try
                {
                    string value = DatabaseHelper.GetSetting(ThemeSettingKey, "dark").Trim().ToLowerInvariant();
                    return value == "light" ? "light" : "dark";
                }
                catch
                {
                    return "dark";
                }
            }
        }

        /// <summary>Включена ли светлая тема.</summary>
        public static bool IsLightTheme => CurrentTheme == "light";

        /// <summary>Светлая тема: запись настройки.</summary>
        public static void SetLightTheme(bool light)
        {
            try
            {
                DatabaseHelper.SaveSetting(ThemeSettingKey, light ? "light" : "dark");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Настройки] тема: {ex.Message}");
            }
        }

        /// <summary>
        /// Вкладки сверху: полоса вкладок переезжает в верхнюю строку окна.
        /// </summary>
        public static bool TopTabs
        {
            get
            {
                try
                {
                    return DatabaseHelper.GetSetting(TopTabsSettingKey, "0").Trim() == "1";
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    DatabaseHelper.SaveSetting(TopTabsSettingKey, value ? "1" : "0");
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Настройки] вкладки: {ex.Message}");
                }
            }
        }
    }
}
