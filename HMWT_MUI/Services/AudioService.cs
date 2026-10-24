using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HMWT_MUI.Models;
using Microsoft.Data.Sqlite;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Аудиотека ведущего: файлы лежат в %APPDATA%\HMWT\Audio, в базе —
    /// имя, хэш и своя громкость трека. Сцена/плейлисты появятся следующим
    /// шагом; здесь фундамент: импорт, список, громкость.
    /// </summary>
    public static class AudioService
    {
        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT", "Audio");

        public static string LibraryDir => Path.Combine(Root, "Library");
        public static string CacheDir => Path.Combine(Root, "Cache");

        private static readonly string[] Extensions =
            { ".mp3", ".ogg", ".wav", ".m4a", ".aac", ".flac", ".opus" };

        public static bool IsSupported(string path)
            => Extensions.Contains((Path.GetExtension(path) ?? "").ToLowerInvariant());

        public static void EnsureFolders()
        {
            try
            {
                Directory.CreateDirectory(LibraryDir);
                Directory.CreateDirectory(CacheDir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] папки: {ex.Message}");
            }
        }

        /// <summary>Импортирует файл в библиотеку. Возвращает запись или null.</summary>
        public static AudioTrackData Import(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
                if (!IsSupported(filePath)) return null;

                EnsureFolders();

                string hash = HashFile(filePath);
                if (string.IsNullOrWhiteSpace(hash)) return null;

                var existing = FindByHash(hash);
                if (existing != null) return existing;

                string ext = (Path.GetExtension(filePath) ?? ".mp3").ToLowerInvariant();
                string relative = Path.Combine("Library", hash + ext);
                string target = Path.Combine(Root, relative);
                if (!File.Exists(target)) File.Copy(filePath, target, true);

                var track = new AudioTrackData
                {
                    Hash = hash,
                    Path = relative,
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    Kind = "music",
                    Gain = 1.0,
                    Size = new FileInfo(target).Length,
                    AddedAt = DateTime.Now
                };
                track.Id = Insert(track);
                return track.Id > 0 ? track : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] импорт: {ex.Message}");
                return null;
            }
        }

        public static List<AudioTrackData> List()
        {
            var list = new List<AudioTrackData>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT * FROM AudioTrack ORDER BY name COLLATE NOCASE", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new AudioTrackData
                    {
                        Id = Convert.ToInt32(r["id_track"]),
                        Hash = r["hash"]?.ToString() ?? "",
                        Path = r["path"]?.ToString() ?? "",
                        Name = r["name"]?.ToString() ?? "",
                        Kind = r["kind"]?.ToString() ?? "music",
                        Gain = r["gain"] == DBNull.Value ? 1.0 : Convert.ToDouble(r["gain"]),
                        DurationMs = r["duration_ms"] == DBNull.Value ? 0 : Convert.ToInt32(r["duration_ms"]),
                        Size = r["size"] == DBNull.Value ? 0 : Convert.ToInt64(r["size"]),
                        AddedAt = DateTime.TryParse(r["added_at"]?.ToString(), out var at) ? at : DateTime.Now
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] список: {ex.Message}");
            }
            return list;
        }

        public static AudioTrackData Find(int id)
        {
            try
            {
                return List().FirstOrDefault(t => t.Id == id);
            }
            catch { return null; }
        }

        public static AudioTrackData FindByHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return null;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT id_track FROM AudioTrack WHERE hash = @h LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@h", hash);
                var value = cmd.ExecuteScalar();
                int id = value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
                return id > 0 ? Find(id) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] поиск по хэшу: {ex.Message}");
                return null;
            }
        }

        public static void SetGain(int id, double gain)
        {
            gain = Math.Max(0, Math.Min(2, gain));
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("UPDATE AudioTrack SET gain = @g WHERE id_track = @id", conn);
                cmd.Parameters.AddWithValue("@g", gain);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] громкость: {ex.Message}");
            }
        }

        public static void Delete(AudioTrackData track)
        {
            if (track == null) return;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using (var cmd = new SqliteCommand("DELETE FROM AudioTrack WHERE id_track = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", track.Id);
                    cmd.ExecuteNonQuery();
                }

                string full = ResolvePath(track);
                if (full != null && File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] удаление: {ex.Message}");
            }
        }

        /// <summary>Полный путь к файлу трека (null, если его нет).</summary>
        public static string ResolvePath(AudioTrackData track)
        {
            if (track == null || string.IsNullOrWhiteSpace(track.Path)) return null;
            try
            {
                string full = Path.GetFullPath(Path.Combine(Root, track.Path));
                return full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)
                    ? full : null;
            }
            catch { return null; }
        }

        public static string HashFile(string path)
        {
            try
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] хэш: {ex.Message}");
                return "";
            }
        }

        private static int Insert(AudioTrackData track)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    @"INSERT INTO AudioTrack (hash, path, name, kind, gain, duration_ms, size, added_at)
                      VALUES (@h, @p, @n, @k, @g, @d, @s, @at)", conn);
                cmd.Parameters.AddWithValue("@h", track.Hash);
                cmd.Parameters.AddWithValue("@p", track.Path);
                cmd.Parameters.AddWithValue("@n", track.Name);
                cmd.Parameters.AddWithValue("@k", track.Kind);
                cmd.Parameters.AddWithValue("@g", track.Gain);
                cmd.Parameters.AddWithValue("@d", track.DurationMs);
                cmd.Parameters.AddWithValue("@s", track.Size);
                cmd.Parameters.AddWithValue("@at", track.AddedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();

                using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn);
                return Convert.ToInt32(idCmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] вставка: {ex.Message}");
                return 0;
            }
        }
    }
}
