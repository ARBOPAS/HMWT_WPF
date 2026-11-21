using System;
using System.Diagnostics;
using System.IO;

namespace HMWT_MUI
{
    /// <summary>
    /// Где приложение хранит данные: база, фото, комнаты, кэш.
    ///
    /// Раньше всё лежало в %APPDATA%\HMWT — папку было трудно найти, а бэкап
    /// приходилось делать вручную. Теперь корень — «Документы\HMWT»: база и все
    /// файлы лежат рядом с документами пользователя, их видно в проводнике и
    /// просто скопировать на флешку.
    ///
    /// Старые данные из %APPDATA%\HMWT переносятся один раз при первом запуске
    /// (см. <see cref="MigrateFromAppData"/>). Исходная папка остаётся на месте
    /// как резервная копия — её можно удалить вручную, когда всё проверено.
    /// </summary>
    public static class AppPaths
    {
        private const string FolderName = "HMWT";
        private const string DatabaseFile = "HMWT_app.db";

        private static string _root;
        private static bool _migrated;

        /// <summary>Корень данных: «Документы\HMWT» или %APPDATA%\HMWT, если документы недоступны.</summary>
        public static string Root
        {
            get
            {
                if (_root != null) return _root;

                _root = ResolveRoot();
                return _root;
            }
        }

        /// <summary>Прежний корень в %APPDATA%\HMWT.</summary>
        public static string LegacyRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

        public static string DatabasePath => Path.Combine(Root, DatabaseFile);

        private static string ResolveRoot()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents))
            {
                string target = Path.Combine(documents, FolderName);
                try
                {
                    Directory.CreateDirectory(target);
                    return target;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AppPaths] Документы недоступны: {ex.Message}");
                }
            }

            // Запасной путь: если «Документы» отключены политикой или на диске
            // нет места, работаем как раньше — в AppData
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);
            Directory.CreateDirectory(appData);
            return appData;
        }

        /// <summary>
        /// Один раз переносит данные из %APPDATA%\HMWT в новый корень.
        /// Вызывается до открытия базы: иначе приложение создаст пустую базу
        /// в «Документах» и старые персонажи «исчезнут».
        /// </summary>
        public static void MigrateFromAppData()
        {
            if (_migrated) return;
            _migrated = true;

            try
            {
                string target = Root;
                string legacy = LegacyRoot;

                if (string.Equals(
                        Path.GetFullPath(target).TrimEnd('\\'),
                        Path.GetFullPath(legacy).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                    return;

                if (!Directory.Exists(legacy)) return;

                // В новом месте уже есть база — значит, перенос не нужен
                if (File.Exists(Path.Combine(target, DatabaseFile)))
                {
                    Debug.WriteLine("[AppPaths] данные уже в «Документах», перенос не нужен");
                    return;
                }

                Debug.WriteLine($"[AppPaths] переношу данные: {legacy} → {target}");
                int files = CopyDirectory(legacy, target);

                bool ok = File.Exists(Path.Combine(target, DatabaseFile));
                File.AppendAllText(Path.Combine(target, "migration.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} перенос из {legacy}: файлов {files}, " +
                    (ok ? "база на месте" : "БАЗА НЕ НАЙДЕНА — проверьте старую папку") + Environment.NewLine);

                Debug.WriteLine(ok
                    ? $"[AppPaths] перенос завершён, файлов: {files}"
                    : "[AppPaths] база не найдена в старой папке");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppPaths] перенос данных: {ex}");
            }
        }

        /// <summary>Копирует папку рекурсивно, не перезаписывая уже существующие файлы.</summary>
        private static int CopyDirectory(string source, string target)
        {
            int copied = 0;
            Directory.CreateDirectory(target);

            foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = dir.Substring(source.Length).TrimStart('\\');
                try { Directory.CreateDirectory(Path.Combine(target, relative)); } catch { }
            }

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length).TrimStart('\\');
                string destination = Path.Combine(target, relative);

                try
                {
                    if (File.Exists(destination)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(file, destination, false);
                    copied++;
                }
                catch (Exception ex)
                {
                    // Занятый файл (WAL, лог) не должен ломать перенос остальных
                    Debug.WriteLine($"[AppPaths] не скопирован {relative}: {ex.Message}");
                }
            }

            return copied;
        }
    }
}
