using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Пакет всех данных приложения: база, карты, фото, токены, комнаты, кэш.
    ///
    /// Один файл .hmwtpack — обычный zip. Экспорт складывает туда всё, что лежит
    /// в корне данных (кроме резервных копий и журналов), импорт распаковывает
    /// пакет рядом и применяет при следующем запуске: подменять базу на ходу,
    /// пока она открыта, нельзя.
    /// </summary>
    public static class DataPackService
    {
        public const string Extension = ".hmwtpack";

        private const string MarkerFile = "restore-pending.txt";
        private const string ManifestEntry = "manifest.json";

        private static string MarkerPath => Path.Combine(AppPaths.Root, MarkerFile);

        /// <summary>Есть ли отложенное восстановление из пакета.</summary>
        public static bool HasPendingRestore => File.Exists(MarkerPath);

        /// <summary>Данные о пакете, ожидающем применения (дата и папка).</summary>
        public static string PendingRestoreInfo
        {
            get
            {
                try { return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : ""; }
                catch { return ""; }
            }
        }

        /// <summary>Складывает все данные в один .hmwtpack. Возвращает число файлов.</summary>
        public static int Export(string packPath) => Export(packPath, AppPaths.Root);

        /// <summary>Экспорт из указанного корня (нужен тестам).</summary>
        public static int Export(string packPath, string root)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(packPath) ?? root);

            int files = 0;
            using var zip = ZipFile.Open(packPath, ZipArchiveMode.Create);

            foreach (string file in EnumerateDataFiles(root))
            {
                string relative = Relative(root, file).Replace('\\', '/');
                try
                {
                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                    files++;
                }
                catch (Exception ex)
                {
                    // Занятый файл не должен срывать экспорт остальных
                    Debug.WriteLine($"[DataPack] пропущен {relative}: {ex.Message}");
                }
            }

            var manifest = new
            {
                app = "HMWT",
                version = Net.NetVersions.AppVersion,
                created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                files
            };

            var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.NoCompression);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream))
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            return files;
        }

        /// <summary>
        /// Распаковывает пакет рядом с данными и ставит метку: применим при
        /// следующем запуске. Возвращает число файлов в пакете.
        /// </summary>
        public static int StageImport(string packPath) => StageImport(packPath, AppPaths.Root);

        /// <summary>Распаковка в указанный корень (нужна тестам).</summary>
        public static int StageImport(string packPath, string root)
        {
            string staging = Path.Combine(root, ".restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            int files = 0;
            using (var zip = ZipFile.OpenRead(packPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;   // папка

                    // Защита от выхода за пределы папки: «..» в имени записи
                    string target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!target.StartsWith(Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                    files++;
                }
            }

            if (!File.Exists(Path.Combine(staging, "HMWT_app.db")))
                throw new InvalidOperationException("В пакете нет базы HMWT_app.db — это не пакет данных HMWT.");

            File.WriteAllText(Path.Combine(root, MarkerFile), staging);
            return files;
        }

        /// <summary>
        /// Применяет отложенный импорт. Вызывается ДО открытия базы: сначала
        /// сохраняем текущую базу в Backups, потом копируем файлы пакета поверх.
        /// </summary>
        public static void ApplyPendingRestore()
        {
            if (!File.Exists(MarkerPath)) return;

            try
            {
                string staging = File.ReadAllText(MarkerPath).Trim();
                string root = AppPaths.Root;

                if (!Directory.Exists(staging))
                {
                    File.Delete(MarkerPath);
                    return;
                }

                // Текущую базу — в резервные копии, чтобы можно было откатиться
                string currentDb = Path.Combine(root, "HMWT_app.db");
                if (File.Exists(currentDb))
                {
                    string backups = Path.Combine(root, "Backups");
                    Directory.CreateDirectory(backups);
                    File.Copy(currentDb,
                        Path.Combine(backups, $"HMWT_before_restore_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db"), true);
                }

                int files = 0;
                foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    string relative = Relative(staging, file);
                    if (string.Equals(relative, ManifestEntry, StringComparison.OrdinalIgnoreCase)) continue;

                    string target = Path.Combine(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(file, target, true);
                    files++;
                }

                File.AppendAllText(Path.Combine(root, "restore.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} восстановлено из {staging}: файлов {files}{Environment.NewLine}");

                try { Directory.Delete(staging, true); } catch { }
                File.Delete(MarkerPath);

                Debug.WriteLine($"[DataPack] восстановление применено, файлов: {files}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DataPack] восстановление: {ex}");
            }
        }


        /// <summary>
        /// Путь файла относительно корня. В .NET Framework 4.8 у Path нет
        /// GetRelativePath, поэтому считаем вручную.
        /// </summary>
        private static string Relative(string root, string file)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\') + '\\';
            string fullFile = Path.GetFullPath(file);
            return fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                ? fullFile.Substring(fullRoot.Length)
                : Path.GetFileName(file);
        }

        /// <summary>Файлы для пакета: без бэкапов, журналов и служебных меток.</summary>
        private static IEnumerable<string> EnumerateDataFiles(string root)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Relative(root, file);
                string name = Path.GetFileName(file);

                if (relative.StartsWith("Backups", StringComparison.OrdinalIgnoreCase)) continue;
                if (relative.StartsWith(".restore-", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, MarkerFile, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "migration.txt", StringComparison.OrdinalIgnoreCase)) continue;

                yield return file;
            }
        }
    }
}
