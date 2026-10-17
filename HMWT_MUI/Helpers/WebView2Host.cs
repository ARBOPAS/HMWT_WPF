using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Один общий движок WebView2 на всё приложение.
    ///
    /// Зачем один: на запуск движка (создание профиля, старт браузерного
    /// процесса) уходят сотни миллисекунд. Если каждое окно создаёт свой
    /// движок заново, заметки открываются «с задержкой», а второй экземпляр
    /// на том же профиле вообще может не подняться. Общий движок поднимается
    /// один раз, дальше окна только просят у него страницу.
    ///
    /// Профиль держим в %LOCALAPPDATA%\HMWT\WebView2: рядом с программой
    /// писать нельзя (после установки это Program Files), и создание папки
    /// по умолчанию падало с «Access is denied».
    /// </summary>
    public static class WebView2Host
    {
        private static Task<CoreWebView2Environment> _environment;
        private static string _userDataFolder;

        /// <summary>Папка профиля движка.</summary>
        public static string UserDataFolder
        {
            get
            {
                if (_userDataFolder != null) return _userDataFolder;

                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HMWT", "WebView2");
                Directory.CreateDirectory(folder);

                _userDataFolder = folder;
                return folder;
            }
        }

        /// <summary>
        /// Возвращает готовый движок. При сбое первого запуска следующий вызов
        /// попробует поднять его заново, а не будет отдавать ту же ошибку.
        /// </summary>
        public static async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_environment != null)
            {
                try { return await _environment; }
                catch
                {
                    // Прошлая попытка не удалась — пробуем ещё раз
                    _environment = null;
                }
            }

            var attempt = CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            _environment = attempt;
            return await attempt;
        }
    }
}
