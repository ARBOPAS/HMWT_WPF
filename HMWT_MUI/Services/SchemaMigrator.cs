using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Версия схемы базы и последовательные миграции.
    ///
    /// Раньше все изменения схемы были набором `EnsureColumn` в разных местах:
    /// работало, но нельзя было понять, на какой версии база у игрока, и
    /// невозможно было сделать шаг, который нельзя просто повторить (например,
    /// перелить данные). Теперь у базы есть номер версии в таблице SchemaInfo,
    /// а новые изменения оформляются шагами миграции.
    ///
    /// Первая версия (1) — «базовая»: всё, что создаётся при старте
    /// (таблицы и EnsureColumn). Миграции с 2 идут по порядку и применяются
    /// один раз; перед ними делается копия файла базы.
    /// </summary>
    public static class SchemaMigrator
    {
        /// <summary>Текущая версия схемы, которую ждёт приложение.</summary>
        public const int CurrentVersion = 5;

        private sealed class Migration
        {
            public int Version;
            public string Title;
            public Action<SqliteConnection, SqliteTransaction> Apply;
        }

        private static readonly Migration[] Migrations =
        {
            new Migration
            {
                Version = 2,
                Title = "индекс по character_id у участников боя",
                // По character_id ищут карта, трекер и лист персонажа;
                // без индекса каждый поиск перебирает всю таблицу.
                Apply = (conn, tx) => Exec(conn, tx,
                    "CREATE INDEX IF NOT EXISTS idx_combatant_character ON EncounterCombatant(id_character);")
            },
            new Migration
            {
                Version = 3,
                Title = "аудиотека ведущего",
                // Громкость у каждого трека своя: файлы из разных источников
                // звучат по-разному, и выравнивать приходится вручную.
                Apply = (conn, tx) =>
                {
                    Exec(conn, tx,
                        @"CREATE TABLE IF NOT EXISTS AudioTrack (
                            id_track INTEGER PRIMARY KEY AUTOINCREMENT,
                            hash TEXT NOT NULL DEFAULT '',
                            path TEXT NOT NULL DEFAULT '',
                            name TEXT NOT NULL DEFAULT '',
                            kind TEXT NOT NULL DEFAULT 'music',
                            gain REAL NOT NULL DEFAULT 1,
                            duration_ms INTEGER NOT NULL DEFAULT 0,
                            size INTEGER NOT NULL DEFAULT 0,
                            added_at TEXT DEFAULT ''
                        );");
                    Exec(conn, tx,
                        "CREATE INDEX IF NOT EXISTS idx_audio_hash ON AudioTrack(hash);");
                }
            },
            new Migration
            {
                Version = 4,
                Title = "плейлисты аудиотеки",
                Apply = (conn, tx) =>
                {
                    Exec(conn, tx,
                        @"CREATE TABLE IF NOT EXISTS AudioPlaylist (
                            id_playlist INTEGER PRIMARY KEY AUTOINCREMENT,
                            name TEXT NOT NULL DEFAULT '',
                            created_at TEXT DEFAULT ''
                        );");
                    Exec(conn, tx,
                        @"CREATE TABLE IF NOT EXISTS AudioPlaylistItem (
                            id_item INTEGER PRIMARY KEY AUTOINCREMENT,
                            id_playlist INTEGER NOT NULL,
                            id_track INTEGER NOT NULL,
                            sort INTEGER NOT NULL DEFAULT 0
                        );");
                    Exec(conn, tx,
                        "CREATE INDEX IF NOT EXISTS idx_audio_playlist_item ON AudioPlaylistItem(id_playlist, sort);");
                }
            },
            new Migration
            {
                Version = 5,
                Title = "размер группы экипировки",
                // Колонка group_size появилась в DDL таблицы EquipmentGroups, но
                // базам, созданным раньше, её никто не добавил: CREATE TABLE IF NOT
                // EXISTS старую таблицу не меняет, а отдельного шага не было.
                //
                // Из-за этого запрос групп экипировки падал с «no such column:
                // g.group_size», и вкладка «Инвентарь» закрывала всю программу.
                // EnsureColumn добавляет колонку только если её нет, поэтому шаг
                // безопасно повторять и на уже починенных базах.
                Apply = (conn, tx) => DatabaseHelper.EnsureColumn(conn, tx,
                    "EquipmentGroups", "group_size REAL NOT NULL DEFAULT 100")
            }
        };

        /// <summary>Прочитать версию схемы (0 — таблицы ещё нет).</summary>
        public static int GetVersion()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                EnsureTable(conn);
                return ReadVersion(conn);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Schema] версия: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Довести схему до <see cref="CurrentVersion"/>. Возвращает true, если
        /// что-то применили. Ошибки не валят запуск приложения: база остаётся
        /// на прежней версии, в лог пишется причина.
        /// </summary>
        public static bool Ensure()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                EnsureTable(conn);

                int current = ReadVersion(conn);
                if (current == 0)
                {
                    // База уже создана DDL и EnsureColumn — это базовая версия.
                    WriteVersion(conn, null, 1);
                    current = 1;
                }

                if (current >= CurrentVersion) return false;

                Backup(conn, current);

                using var tx = conn.BeginTransaction();
                foreach (var migration in Migrations.Where(m => m.Version > current)
                             .OrderBy(m => m.Version))
                {
                    migration.Apply(conn, tx);
                    WriteVersion(conn, tx, migration.Version);
                    Debug.WriteLine($"[Schema] миграция {migration.Version}: {migration.Title}");
                }
                tx.Commit();

                Debug.WriteLine($"[Schema] схема обновлена: {current} -> {CurrentVersion}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Schema] миграция не удалась: {ex.Message}");
                return false;
            }
        }

        /// <summary>Быстрая проверка целостности файла базы (PRAGMA quick_check).</summary>
        public static bool QuickCheck(out string report)
        {
            report = "";
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("PRAGMA quick_check", conn);
                report = Convert.ToString(cmd.ExecuteScalar()) ?? "";
                return string.Equals(report, "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                report = ex.Message;
                return false;
            }
        }

        /// <summary>Записать в журнал версию схемы и результат быстрой проверки.</summary>
        public static void LogHealth()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT");
                Directory.CreateDirectory(dir);
                bool ok = QuickCheck(out string report);
                File.WriteAllText(Path.Combine(dir, "db-check.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} версия схемы {GetVersion()} из {CurrentVersion}, " +
                    $"quick_check: {(ok ? "ok" : report)}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private static void EnsureTable(SqliteConnection conn)
        {
            using var cmd = new SqliteCommand(
                @"CREATE TABLE IF NOT EXISTS SchemaInfo (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    version INTEGER NOT NULL DEFAULT 1,
                    applied_at TEXT DEFAULT ''
                );", conn);
            cmd.ExecuteNonQuery();
        }

        private static int ReadVersion(SqliteConnection conn)
        {
            using var cmd = new SqliteCommand("SELECT version FROM SchemaInfo WHERE id = 1", conn);
            var value = cmd.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static void WriteVersion(SqliteConnection conn, SqliteTransaction tx, int version)
        {
            using var cmd = new SqliteCommand(
                @"INSERT INTO SchemaInfo (id, version, applied_at) VALUES (1, @v, @at)
                  ON CONFLICT(id) DO UPDATE SET version = @v, applied_at = @at;", conn, tx);
            cmd.Parameters.AddWithValue("@v", version);
            cmd.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }

        /// <summary>Копия базы перед миграцией: откатиться всегда проще, чем чинить.</summary>
        private static void Backup(SqliteConnection conn, int fromVersion)
        {
            try
            {
                // Сливаем WAL в основной файл, иначе копия будет неполной.
                using (var check = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", conn))
                {
                    try { check.ExecuteNonQuery(); } catch { }
                }

                string path = conn.DataSource;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

                string dir = Path.Combine(Path.GetDirectoryName(path) ?? "", "Backups");
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir,
                    $"schema_v{fromVersion}_{DateTime.Now:yyyyMMdd_HHmmss}.db");
                File.Copy(path, target, true);

                // Держим только последние пять копий схемы.
                var old = Directory.GetFiles(dir, "schema_v*.db")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Skip(5)
                    .ToList();
                foreach (string file in old) { try { File.Delete(file); } catch { } }

                Debug.WriteLine($"[Schema] копия перед миграцией: {target}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Schema] копия не сделана: {ex.Message}");
            }
        }

        private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            using var cmd = new SqliteCommand(sql, conn, tx);
            cmd.ExecuteNonQuery();
        }
    }
}
