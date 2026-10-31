using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Безопасная очистка кэша карт и старых фотографий комнат.
    ///
    /// Не трогает Monsters, Audio, базу и карты, на которые ссылаются сцены.
    /// В Maps удаляются только эскизы, скачанные remote-копии и дубликаты;
    /// в Rooms/*/photos — фотографии, на которые не ссылается ни один файл
    /// комнаты (включая players.json и чат).
    /// </summary>
    public static class CacheCleanupService
    {
        private static readonly Regex PhotoRef = new Regex(
            @"photos[\\/](?<name>[^""\\\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public sealed class Report
        {
            public List<string> Files { get; } = new List<string>();
            public long Bytes { get; set; }
            public int MapFiles => Files.Count(f => f.StartsWith(Path.Combine(Root, "Maps"),
                StringComparison.OrdinalIgnoreCase));
            public int PhotoFiles => Files.Count - MapFiles;
            public int Count => Files.Count;
        }

        public sealed class CleanResult
        {
            public int Deleted { get; set; }
            public long Freed { get; set; }
            public List<string> Errors { get; } = new List<string>();
        }

        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT");

        public static string MapsDir => Path.Combine(Root, "Maps");
        public static string RoomsDir => Path.Combine(Root, "Rooms");

        public static Report Scan()
        {
            var report = new Report();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ScanMaps(report, seen);
            ScanRooms(report, seen);
            report.Bytes = report.Files
                .Where(File.Exists)
                .Sum(f => new FileInfo(f).Length);
            return report;
        }

        public static CleanResult Clean()
        {
            var report = Scan();
            var result = new CleanResult();
            string mapsRoot = Path.GetFullPath(MapsDir).TrimEnd('\\') + "\\";
            string roomsRoot = Path.GetFullPath(RoomsDir).TrimEnd('\\') + "\\";

            foreach (string file in report.Files)
            {
                try
                {
                    string full = Path.GetFullPath(file);
                    if (!full.StartsWith(mapsRoot, StringComparison.OrdinalIgnoreCase) &&
                        !full.StartsWith(roomsRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    long size = new FileInfo(full).Length;
                    File.Delete(full);
                    result.Deleted++;
                    result.Freed += size;
                }
                catch (Exception ex)
                {
                    result.Errors.Add(Path.GetFileName(file) + ": " + ex.Message);
                }
            }

            return result;
        }

        private static void ScanMaps(Report report, HashSet<string> seen)
        {
            if (!Directory.Exists(MapsDir)) return;

            HashSet<string> referenced;
            try
            {
                referenced = LoadSceneMapPaths();
            }
            catch
            {
                // Без списка сцен нельзя гарантировать сохранность карт —
                // карты в такую очистку не включаем.
                return;
            }

            var files = Directory.EnumerateFiles(MapsDir, "*", SearchOption.TopDirectoryOnly).ToList();

            // Эскизы — производные, их всегда можно пересоздать.
            foreach (string file in files.Where(f =>
                         Path.GetFileName(f).StartsWith("thumb_", StringComparison.OrdinalIgnoreCase)))
                Add(report, seen, file);

            // Скачанные копии карт для клиента: если сцена на них не ссылается,
            // это кэш, который всегда можно получить заново.
            foreach (string file in files.Where(f =>
                         Path.GetFileName(f).StartsWith("remote_", StringComparison.OrdinalIgnoreCase) &&
                         !referenced.Contains(Path.GetFullPath(f))))
                Add(report, seen, file);

            // Дубликаты: у каждой группы одинакового содержимого оставляем
            // все файлы, на которые ссылаются сцены. Если ссылок нет — один,
            // самый новый. Уникальные несвязанные карты не трогаем.
            var groups = files
                .Where(f => File.Exists(f) && !IsAlreadyAdded(report, f))
                .GroupBy(f => new FileInfo(f).Length)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.GroupBy(f => Hash(f)))
                .Where(g => g.Count() > 1);

            foreach (var group in groups)
            {
                var list = group.ToList();
                var used = list.Where(f => referenced.Contains(Path.GetFullPath(f))).ToList();
                if (used.Count > 0)
                {
                    foreach (string file in list.Except(used)) Add(report, seen, file);
                }
                else
                {
                    string keep = list.OrderByDescending(File.GetLastWriteTimeUtc).First();
                    foreach (string file in list.Where(f => f != keep)) Add(report, seen, file);
                }
            }
        }

        private static void ScanRooms(Report report, HashSet<string> seen)
        {
            if (!Directory.Exists(RoomsDir)) return;

            foreach (string room in Directory.EnumerateDirectories(RoomsDir))
            {
                string photos = Path.Combine(room, "photos");
                if (!Directory.Exists(photos)) continue;

                var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Все текстовые файлы комнаты: players.json, room.json, чат.
                foreach (string textFile in Directory.EnumerateFiles(room, "*",
                             SearchOption.TopDirectoryOnly)
                             .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        string text = File.ReadAllText(textFile);
                        foreach (Match m in PhotoRef.Matches(text))
                            references.Add(Path.GetFileName(m.Groups["name"].Value));
                    }
                    catch { }
                }

                // Аватары, которые сейчас в активной комнате и ещё не попали
                // в players.json, тоже считаем используемыми.
                try
                {
                    var session = HMWT_MUI.Net.RoomSession.Instance;
                    if (session != null && session.IsActive)
                    {
                        foreach (var player in session.GetPlayers())
                        {
                            if (player == null || string.IsNullOrWhiteSpace(player.AvatarPath)) continue;
                            references.Add(Path.GetFileName(player.AvatarPath));
                        }
                    }
                }
                catch { }

                foreach (string file in Directory.EnumerateFiles(photos))
                {
                    if (!references.Contains(Path.GetFileName(file)))
                        Add(report, seen, file);
                }
            }
        }

        private static HashSet<string> LoadSceneMapPaths()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT image_path FROM MapScene", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string raw = reader.IsDBNull(0) ? "" : (reader.GetString(0) ?? "");
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string rel = raw.Replace('\\', '/').TrimStart('/');
                if (rel.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(5);

                string full = Path.GetFullPath(Path.Combine(MapsDir, rel));
                result.Add(full);
            }
            return result;
        }

        private static void Add(Report report, HashSet<string> seen, string file)
        {
            string full = Path.GetFullPath(file);
            if (seen.Add(full)) report.Files.Add(full);
        }

        private static bool IsAlreadyAdded(Report report, string file)
        {
            string full = Path.GetFullPath(file);
            return report.Files.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
        }

        private static string Hash(string file)
        {
            try
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(file);
                return BitConverter.ToString(sha.ComputeHash(stream));
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
