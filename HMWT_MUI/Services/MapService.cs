using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Карты боёв: сцены, картинки и жетоны.
    ///
    /// Картинка копируется в папку данных приложения (Maps/…), а не читается
    /// по исходному пути: сцена должна открываться, даже если файл из Загрузок
    /// потом удалили. Рядом хранится хэш — по нему клиенты в сетевой игре
    /// поймут, что карту качать заново не нужно.
    ///
    /// Жетон ссылается на участника боя (EncounterCombatant), поэтому хиты
    /// и состояния у него общие с трекером инициативы.
    /// </summary>
    public static class MapService
    {
        private static bool _tablesChecked;

        public static void ResetSchemaCache() => _tablesChecked = false;

        #region === ТАБЛИЦЫ ===

        public static void EnsureTables()
        {
            if (_tablesChecked) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();
                using var cmd = new SqliteCommand(@"
                    CREATE TABLE IF NOT EXISTS MapScene (
                        id_scene INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_character INTEGER NOT NULL,
                        id_campaign INTEGER DEFAULT 0,
                        name TEXT NOT NULL DEFAULT 'Карта',
                        image_path TEXT DEFAULT '',
                        image_hash TEXT DEFAULT '',
                        width_px INTEGER DEFAULT 0,
                        height_px INTEGER DEFAULT 0,
                        grid_px REAL DEFAULT 70,
                        grid_on INTEGER DEFAULT 1,
                        grid_snap INTEGER DEFAULT 1,
                        grid_offset_x REAL DEFAULT 0,
                        grid_offset_y REAL DEFAULT 0,
                        fog_on INTEGER DEFAULT 1,
                        fog_mode TEXT DEFAULT 'combined',
                        grid_color TEXT DEFAULT '#33FFFFFF',
                        trail_color TEXT DEFAULT '#FF9B8BFF',
                        grid_thickness REAL DEFAULT 1,
                        rotation INTEGER DEFAULT 0,
                        flip_h INTEGER DEFAULT 0,
                        flip_v INTEGER DEFAULT 0,
                        created_at TEXT DEFAULT '',
                        updated_at TEXT DEFAULT ''
                    );
                    CREATE TABLE IF NOT EXISTS MapCampaign (
                        id_campaign INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_character INTEGER NOT NULL,
                        name TEXT NOT NULL DEFAULT 'Кампания',
                        sort INTEGER DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS MapToken (
                        id_token INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_scene INTEGER NOT NULL,
                        id_combatant INTEGER DEFAULT 0,
                        label TEXT DEFAULT '',
                        x REAL DEFAULT 0,
                        y REAL DEFAULT 0,
                        size_cells REAL DEFAULT 1,
                        color TEXT DEFAULT '',
                        hidden INTEGER DEFAULT 0,
                        is_player INTEGER DEFAULT 0,
                        photo_hash TEXT DEFAULT '',
                        photo_path TEXT DEFAULT '',
                        facing REAL DEFAULT 0,
                        view_angle REAL DEFAULT 360,
                        frame_color TEXT DEFAULT '',
                        view_color TEXT DEFAULT ''
                    );
                    CREATE TABLE IF NOT EXISTS MapStroke (
                        id_stroke INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_scene INTEGER NOT NULL,
                        color TEXT DEFAULT '#FFE06A63',
                        thickness REAL DEFAULT 4,
                        points TEXT DEFAULT '',
                        is_eraser INTEGER DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS MapWall (
                        id_wall INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_scene INTEGER NOT NULL,
                        x1 REAL DEFAULT 0,
                        y1 REAL DEFAULT 0,
                        x2 REAL DEFAULT 0,
                        y2 REAL DEFAULT 0,
                        kind TEXT DEFAULT 'wall',
                        is_open INTEGER DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS MapSeen (
                        id_seen INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_scene INTEGER NOT NULL,
                        points TEXT DEFAULT '',
                        created_at TEXT DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_map_seen_scene ON MapSeen(id_scene);
                    CREATE INDEX IF NOT EXISTS idx_map_wall_scene ON MapWall(id_scene);

                    CREATE TABLE IF NOT EXISTS MapShape (
                        id_shape INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_scene INTEGER NOT NULL,
                        kind TEXT DEFAULT 'circle',
                        x REAL DEFAULT 0,
                        y REAL DEFAULT 0,
                        radius_cells REAL DEFAULT 4,
                        angle REAL DEFAULT 0,
                        color TEXT DEFAULT '#FFB98BFF',
                        label TEXT DEFAULT '',
                        script_kind TEXT DEFAULT '',
                        script_ability TEXT DEFAULT '',
                        script_skill TEXT DEFAULT '',
                        script_dc INTEGER DEFAULT 0,
                        script_name TEXT DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_map_token_scene ON MapToken(id_scene);
                    CREATE INDEX IF NOT EXISTS idx_map_shape_scene ON MapShape(id_scene);
                    CREATE INDEX IF NOT EXISTS idx_map_stroke_scene ON MapStroke(id_scene);", conn, tx);
                cmd.ExecuteNonQuery();

                // Колонки, которые могли не появиться в базе старше этой версии
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "grid_color TEXT DEFAULT '#33FFFFFF'");
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "trail_color TEXT DEFAULT '#FF9B8BFF'");
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "grid_thickness REAL DEFAULT 1");
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "grid_snap INTEGER DEFAULT 1");
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "fog_mode TEXT DEFAULT 'combined'");
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "id_campaign INTEGER DEFAULT 0");
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "rotation INTEGER DEFAULT 0");
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "flip_h INTEGER DEFAULT 0");
                DatabaseHelper.EnsureColumn(conn, tx, "MapScene", "flip_v INTEGER DEFAULT 0");
                DatabaseHelper.EnsureColumn(conn, tx, "MapWall", "is_open INTEGER DEFAULT 0");
                // Скрипты тёмных зон (1.3.9.0): старые базы догоняем ALTER'ом.
                DatabaseHelper.EnsureColumn(conn, tx, "MapShape", "script_kind TEXT DEFAULT ''");
                DatabaseHelper.EnsureColumn(conn, tx, "MapShape", "script_ability TEXT DEFAULT ''");
                DatabaseHelper.EnsureColumn(conn, tx, "MapShape", "script_skill TEXT DEFAULT ''");
                DatabaseHelper.EnsureColumn(conn, tx, "MapShape", "script_dc INTEGER DEFAULT 0");
                DatabaseHelper.EnsureColumn(conn, tx, "MapShape", "script_name TEXT DEFAULT ''");
                DatabaseHelper.EnsureColumn(conn, tx, "MapToken", "is_player INTEGER DEFAULT 0");
                DatabaseHelper.EnsureColumn(conn, tx, "MapToken", "reveal_cells REAL DEFAULT 6");
                DatabaseHelper.EnsureColumn(conn, tx, "MapToken", "photo_hash TEXT DEFAULT ''");
                DatabaseHelper.EnsureColumn(conn, tx, "MapToken", "photo_path TEXT DEFAULT ''");
                DatabaseHelper.EnsureColumn(conn, tx, "MapToken", "facing REAL DEFAULT 0");
                DatabaseHelper.EnsureColumn(conn, tx, "MapToken", "view_angle REAL DEFAULT 360");
                DatabaseHelper.EnsureColumn(conn, tx, "MapToken", "frame_color TEXT DEFAULT ''");
                DatabaseHelper.EnsureColumn(conn, tx, "MapToken", "view_color TEXT DEFAULT ''");

                // Версия формы «разведанного». До 1.2.5 полигон сохранялся без
                // центра сектора и выглядел обрезанными кусками, а не конусом.
                // Один раз чистим старые записи: пусть игроки разведают заново.
                string seenVersion = "";
                using (var check = new SqliteCommand(
                    "SELECT Value FROM Settings WHERE Key = 'MapSeenShapeVersion'", conn, tx))
                    seenVersion = Convert.ToString(check.ExecuteScalar()) ?? "";

                if (seenVersion != "2")
                {
                    using (var clear = new SqliteCommand("DELETE FROM MapSeen", conn, tx))
                        clear.ExecuteNonQuery();

                    using (var mark = new SqliteCommand(
                        "INSERT OR REPLACE INTO Settings (Key, Value) VALUES ('MapSeenShapeVersion', '2')", conn, tx))
                        mark.ExecuteNonQuery();
                }

                tx.Commit();
                _tablesChecked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] EnsureTables: {ex.Message}");
            }
        }

        #endregion

        #region === СЦЕНЫ ===

        /// <summary>Все сцены персонажа, свежие сверху по имени.</summary>
        public static List<MapSceneData> LoadScenes(int characterId)
        {
            EnsureTables();
            var list = new List<MapSceneData>();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT * FROM MapScene WHERE id_character = @char ORDER BY name COLLATE NOCASE", conn);
                cmd.Parameters.AddWithValue("@char", characterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadScene(reader));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] LoadScenes: {ex.Message}");
            }

            return list;
        }

        public static MapSceneData LoadScene(int sceneId)
        {
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT * FROM MapScene WHERE id_scene = @id", conn);
                cmd.Parameters.AddWithValue("@id", sceneId);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                var scene = ReadScene(reader);
                reader.Close();
                scene.Tokens = LoadTokens(conn, scene.Id);
                scene.Strokes = LoadStrokes(conn, scene.Id);
                scene.Shapes = LoadShapes(conn, scene.Id);
                scene.Walls = LoadWalls(conn, scene.Id);
                return scene;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] LoadScene: {ex.Message}");
                return null;
            }
        }

        /// <summary>Создаёт пустую сцену и возвращает её.</summary>
        public static MapSceneData CreateScene(int characterId, string name, int campaignId = 0)
        {
            EnsureTables();

            var scene = new MapSceneData
            {
                CharacterId = characterId,
                CampaignId = campaignId,
                Name = string.IsNullOrWhiteSpace(name) ? "Новая сцена" : name.Trim()
            };

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    INSERT INTO MapScene (id_character, id_campaign, name, created_at, updated_at)
                    VALUES (@char, @campaign, @name, @now, @now);
                    SELECT last_insert_rowid();", conn);
                cmd.Parameters.AddWithValue("@char", characterId);
                cmd.Parameters.AddWithValue("@campaign", campaignId);
                cmd.Parameters.AddWithValue("@name", scene.Name);
                cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                scene.Id = Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] CreateScene: {ex.Message}");
            }

            return scene;
        }

        public static bool SaveScene(MapSceneData scene)
        {
            if (scene == null || scene.Id <= 0) return false;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    UPDATE MapScene SET
                        name = @name, id_campaign = @campaign, image_path = @image, image_hash = @hash,
                        width_px = @w, height_px = @h, grid_px = @grid, grid_on = @gridOn,
                        grid_offset_x = @gx, grid_offset_y = @gy, fog_on = @fog,
                        grid_color = @gridColor, trail_color = @trailColor,
                        rotation = @rotation, flip_h = @flipH, flip_v = @flipV, fog_mode = @fogMode,
                        updated_at = @now
                    WHERE id_scene = @id", conn);
                cmd.Parameters.AddWithValue("@name", scene.Name ?? "");
                cmd.Parameters.AddWithValue("@campaign", scene.CampaignId);
                cmd.Parameters.AddWithValue("@image", scene.ImagePath ?? "");
                cmd.Parameters.AddWithValue("@hash", scene.ImageHash ?? "");
                cmd.Parameters.AddWithValue("@w", scene.WidthPx);
                cmd.Parameters.AddWithValue("@h", scene.HeightPx);
                cmd.Parameters.AddWithValue("@grid", scene.GridPx);
                cmd.Parameters.AddWithValue("@gridOn", scene.GridOn ? 1 : 0);
                cmd.Parameters.AddWithValue("@gridSnap", scene.SnapToGrid ? 1 : 0);
                cmd.Parameters.AddWithValue("@gx", scene.GridOffsetX);
                cmd.Parameters.AddWithValue("@gy", scene.GridOffsetY);
                cmd.Parameters.AddWithValue("@fog", scene.FogOn ? 1 : 0);
                cmd.Parameters.AddWithValue("@gridColor", string.IsNullOrWhiteSpace(scene.GridColor) ? "#33FFFFFF" : scene.GridColor);
                cmd.Parameters.AddWithValue("@trailColor", string.IsNullOrWhiteSpace(scene.TrailColor) ? "#FF9B8BFF" : scene.TrailColor);
                cmd.Parameters.AddWithValue("@gridThickness", Math.Max(0.5, Math.Min(6, scene.GridThickness)));
                cmd.Parameters.AddWithValue("@fogMode", string.IsNullOrWhiteSpace(scene.FogMode) ? "combined" : scene.FogMode);
                cmd.Parameters.AddWithValue("@rotation", scene.Rotation);
                cmd.Parameters.AddWithValue("@flipH", scene.FlipH ? 1 : 0);
                cmd.Parameters.AddWithValue("@flipV", scene.FlipV ? 1 : 0);
                cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@id", scene.Id);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] SaveScene: {ex.Message}");
                return false;
            }
        }

        public static void DeleteScene(int sceneId)
        {
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM MapToken WHERE id_scene = @id; DELETE FROM MapScene WHERE id_scene = @id;", conn);
                cmd.Parameters.AddWithValue("@id", sceneId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] DeleteScene: {ex.Message}");
            }
        }

        private static MapSceneData ReadScene(SqliteDataReader reader)
        {
            return new MapSceneData
            {
                Id = Int(reader, "id_scene"),
                CharacterId = Int(reader, "id_character"),
                CampaignId = Int(reader, "id_campaign"),
                Name = Text(reader, "name"),
                ImagePath = Text(reader, "image_path"),
                ImageHash = Text(reader, "image_hash"),
                WidthPx = Int(reader, "width_px"),
                HeightPx = Int(reader, "height_px"),
                GridPx = Double(reader, "grid_px", 70),
                GridOn = Int(reader, "grid_on") != 0,
                SnapToGrid = Int(reader, "grid_snap") != 0,
                GridOffsetX = Double(reader, "grid_offset_x", 0),
                GridOffsetY = Double(reader, "grid_offset_y", 0),
                FogOn = Int(reader, "fog_on") != 0,
                FogMode = Text(reader, "fog_mode") == "own" ? "own" : "combined",
                GridColor = Text(reader, "grid_color"),
                TrailColor = Text(reader, "trail_color"),
                GridThickness = Double(reader, "grid_thickness", 1),
                Rotation = Int(reader, "rotation"),
                FlipH = Int(reader, "flip_h") != 0,
                FlipV = Int(reader, "flip_v") != 0
            };
        }

        #endregion

        #region === КАМПАНИИ И ПРЕВЬЮ ===

        public static List<MapCampaignData> LoadCampaigns(int characterId)
        {
            EnsureTables();
            var list = new List<MapCampaignData>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT * FROM MapCampaign WHERE id_character = @char ORDER BY sort, id_campaign", conn);
                cmd.Parameters.AddWithValue("@char", characterId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new MapCampaignData
                    {
                        Id = Int(r, "id_campaign"),
                        CharacterId = Int(r, "id_character"),
                        Name = Text(r, "name"),
                        Sort = Int(r, "sort")
                    });
            }
            catch (Exception ex) { Debug.WriteLine($"[MapService] кампании: {ex.Message}"); }
            return list;
        }

        public static int CreateCampaign(int characterId, string name)
        {
            EnsureTables();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    @"INSERT INTO MapCampaign (id_character, name, sort)
                      VALUES (@char, @name,
                        COALESCE((SELECT MAX(sort) FROM MapCampaign WHERE id_character = @char), 0) + 1);
                      SELECT last_insert_rowid();", conn);
                cmd.Parameters.AddWithValue("@char", characterId);
                cmd.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(name) ? "Кампания" : name.Trim());
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex) { Debug.WriteLine($"[MapService] кампания: {ex.Message}"); return 0; }
        }

        public static void RenameCampaign(int id, string name)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("UPDATE MapCampaign SET name = @n WHERE id_campaign = @id", conn);
                cmd.Parameters.AddWithValue("@n", name ?? "");
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Debug.WriteLine($"[MapService] переименовать кампанию: {ex.Message}"); }
        }

        public static void DeleteCampaign(int id)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();
                using (var cmd = new SqliteCommand("UPDATE MapScene SET id_campaign = 0 WHERE id_campaign = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SqliteCommand("DELETE FROM MapCampaign WHERE id_campaign = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch (Exception ex) { Debug.WriteLine($"[MapService] удалить кампанию: {ex.Message}"); }
        }

        /// <summary>Миниатюра сцены для карточки. WebP/WebM WPF не декодирует — вернём null.</summary>
        public static string ThumbnailPath(MapSceneData scene)
        {
            if (scene == null || string.IsNullOrWhiteSpace(scene.ImageHash)) return null;

            try
            {
                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Maps");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "thumb_" + scene.ImageHash + ".jpg");
                if (File.Exists(file)) return file;

                string image = ResolveImagePath(scene);
                if (image == null) return null;

                string ext = (Path.GetExtension(image) ?? "").ToLowerInvariant();
                if (ext == ".webp" || ext == ".webm") return null;

                var source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.UriSource = new Uri(image);
                source.EndInit();

                if (source.PixelWidth <= 0) return null;
                double scale = 240.0 / source.PixelWidth;
                var thumb = new TransformedBitmap(source, new ScaleTransform(scale, scale));

                var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                encoder.Frames.Add(BitmapFrame.Create(thumb));
                using (var stream = File.Create(file)) encoder.Save(stream);
                return file;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] миниатюра: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region === КАРТИНКА ===

        /// <summary>
        /// Копирует картинку карты в папку данных и запоминает размеры и хэш.
        /// Возвращает false, если файл не картинка.
        /// </summary>
        public static bool ImportImage(MapSceneData scene, string sourcePath, out string error)
        {
            error = "";

            try
            {
                if (scene == null || scene.Id <= 0) { error = "Сцена не выбрана."; return false; }
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) { error = "Файл не найден."; return false; }

                if (!TryReadImageSize(sourcePath, out int width, out int height, out error))
                    return false;

                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Maps");
                Directory.CreateDirectory(dir);

                string extension = Path.GetExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                foreach (string old in Directory.GetFiles(dir, $"map_{scene.Id}_*"))
                {
                    try { File.Delete(old); } catch { }
                }

                string fileName = $"map_{scene.Id}_{DateTime.Now:yyyyMMddHHmmss}{extension.ToLowerInvariant()}";
                string target = Path.Combine(dir, fileName);
                File.Copy(sourcePath, target, true);

                scene.ImagePath = "Maps/" + fileName;
                scene.ImageHash = HashOf(target);
                scene.WidthPx = width;
                scene.HeightPx = height;

                SaveScene(scene);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] ImportImage: {ex.Message}");
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Размер карты. PNG/JPEG/BMP/GIF читает WPF, а WebP и WebM — свои
        /// разборщики: системный декодер их не знает, а размер нужен сетке,
        /// жетонам и туману.
        /// </summary>
        private static bool TryReadImageSize(string path, out int width, out int height, out string error)
        {
            width = height = 0;
            error = "";
            try
            {
                string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (ext == ".webp") return TryReadWebP(path, out width, out height, out error);
                if (ext == ".webm") return TryReadWebM(path, out width, out height, out error);

                var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.OnLoad);
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadWebP(string path, out int width, out int height, out string error)
        {
            width = height = 0;
            error = "";
            try
            {
                byte[] b = new byte[40];
                using (var fs = File.OpenRead(path))
                {
                    if (fs.Read(b, 0, b.Length) < 30) { error = "Файл WebP слишком короткий."; return false; }
                }

                if (b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' ||
                    b[8] != 'W' || b[9] != 'E' || b[10] != 'B' || b[11] != 'P')
                {
                    error = "Это не WebP.";
                    return false;
                }

                string chunk = System.Text.Encoding.ASCII.GetString(b, 12, 4);
                if (chunk == "VP8 ")
                {
                    if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A)
                    {
                        error = "Повреждённый WebP (VP8).";
                        return false;
                    }
                    width = (b[26] | (b[27] << 8)) & 0x3FFF;
                    height = (b[28] | (b[29] << 8)) & 0x3FFF;
                }
                else if (chunk == "VP8L")
                {
                    if (b[20] != 0x2F) { error = "Повреждённый WebP (VP8L)."; return false; }
                    uint bits = (uint)(b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24));
                    width = (int)(bits & 0x3FFF) + 1;
                    height = (int)((bits >> 14) & 0x3FFF) + 1;
                }
                else if (chunk == "VP8X")
                {
                    width = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
                    height = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
                }
                else
                {
                    error = "Неизвестный вариант WebP: " + chunk;
                    return false;
                }

                if (width <= 0 || height <= 0) { error = "Не удалось определить размер WebP."; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// WebM — контейнер EBML. Идём по дереву (Segment → Tracks →
        /// TrackEntry → Video) и читаем PixelWidth/PixelHeight. Простой
        /// поиск байтов 0xB0/0xBA даёт ложные совпадения в первых же
        /// мегабайтах, поэтому разбираем структуру.
        /// </summary>
        private static bool TryReadWebM(string path, out int width, out int height, out string error)
        {
            width = height = 0;
            error = "";
            try
            {
                const int limit = 4 * 1024 * 1024;
                byte[] data;
                using (var fs = File.OpenRead(path))
                {
                    int size = (int)Math.Min(limit, fs.Length);
                    data = new byte[size];
                    int read = 0;
                    while (read < size)
                    {
                        int n = fs.Read(data, read, size - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }

                int parsedWidth = 0, parsedHeight = 0;
                ParseEbml(data, 0, data.Length, 0, ref parsedWidth, ref parsedHeight);

                if (parsedWidth <= 0 || parsedHeight <= 0)
                {
                    error = "Не удалось найти размеры WebM.";
                    return false;
                }

                width = parsedWidth;
                height = parsedHeight;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private const long EbmlHeader = 0x1A45DFA3;
        private const long EbmlSegment = 0x18538067;
        private const long EbmlTracks = 0x1654AE6B;
        private const long EbmlTrackEntry = 0xAE;
        private const long EbmlVideo = 0xE0;

        private static void ParseEbml(byte[] data, int start, int end, int depth,
            ref int width, ref int height)
        {
            if (depth > 10 || start < 0 || start >= data.Length) return;

            int pos = Math.Max(0, start);
            end = Math.Min(end, data.Length);
            while (pos < end)
            {
                if (!ReadEbmlVint(data, pos, true, out long id, out int idLength, out _))
                    return;
                int sizePos = pos + idLength;
                if (!ReadEbmlVint(data, sizePos, false, out long size, out int sizeLength, out bool unknown))
                    return;

                int dataStart = sizePos + sizeLength;
                if (dataStart >= data.Length) return;

                long endLong = unknown ? end : Math.Min(end, dataStart + size);
                int dataEnd = (int)Math.Min(data.Length, Math.Max(dataStart, endLong));

                if (id == EbmlHeader || id == EbmlSegment || id == EbmlTracks ||
                    id == EbmlTrackEntry || id == EbmlVideo)
                {
                    ParseEbml(data, dataStart, dataEnd, depth + 1, ref width, ref height);
                }
                else if ((id == 0xB0 || id == 0xBA) && size >= 1 && size <= 4)
                {
                    long value = 0;
                    for (int i = 0; i < size && dataStart + i < data.Length; i++)
                        value = (value << 8) | data[dataStart + i];

                    if (value >= 16 && value <= 20000)
                    {
                        if (id == 0xB0 && width == 0) width = (int)value;
                        else if (id == 0xBA && height == 0) height = (int)value;
                    }
                }

                if (dataEnd <= pos) return;
                pos = dataEnd;
            }
        }

        private static bool ReadEbmlVint(byte[] data, int pos, bool keepMarker,
            out long value, out int length, out bool unknown)
        {
            value = 0;
            length = 0;
            unknown = false;
            if (pos < 0 || pos >= data.Length) return false;

            byte first = data[pos];
            int len = 0;
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((first & (1 << bit)) != 0) { len = 8 - bit; break; }
            }
            if (len < 1 || pos + len > data.Length) return false;

            if (keepMarker)
            {
                for (int i = 0; i < len; i++) value = (value << 8) | data[pos + i];
            }
            else
            {
                long mask = (1L << (8 - len)) - 1;
                value = first & mask;
                for (int i = 1; i < len; i++) value = (value << 8) | data[pos + i];

                long all = mask;
                for (int i = 1; i < len; i++) all = (all << 8) | 255;
                if (value == all) unknown = true;
            }

            length = len;
            return true;
        }

        /// <summary>Абсолютный путь к картинке сцены (или null).</summary>
        public static string ResolveImagePath(MapSceneData scene)
        {
            if (scene == null || string.IsNullOrWhiteSpace(scene.ImagePath)) return null;
            try
            {
                string full = Path.Combine(PhotoHelper.GetWritableRoot(),
                    scene.ImagePath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        private static string HashOf(string path)
        {
            try
            {
                using var sha = SHA1.Create();
                using var stream = File.OpenRead(path);
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }

        #endregion

        #region === ЖЕТОНЫ ===

        public static List<MapTokenData> LoadTokens(int sceneId)
        {
            var list = new List<MapTokenData>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                list = LoadTokens(conn, sceneId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] LoadTokens: {ex.Message}");
            }
            return list;
        }

        private static List<MapTokenData> LoadTokens(SqliteConnection conn, int sceneId)
        {
            var list = new List<MapTokenData>();

            using var cmd = new SqliteCommand("SELECT * FROM MapToken WHERE id_scene = @id ORDER BY id_token", conn);
            cmd.Parameters.AddWithValue("@id", sceneId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MapTokenData
                {
                    Id = Int(reader, "id_token"),
                    SceneId = Int(reader, "id_scene"),
                    CombatantId = Int(reader, "id_combatant"),
                    Label = Text(reader, "label"),
                    X = Double(reader, "x", 0),
                    Y = Double(reader, "y", 0),
                    SizeCells = Double(reader, "size_cells", 1),
                    Color = Text(reader, "color"),
                    Hidden = Int(reader, "hidden") != 0,
                    RevealCells = Double(reader, "reveal_cells", 6),
                    IsPlayer = Int(reader, "is_player") != 0,
                    PhotoHash = Text(reader, "photo_hash"),
                    PhotoPath = Text(reader, "photo_path"),
                    Facing = Double(reader, "facing", 0),
                    ViewAngle = Double(reader, "view_angle", 360),
                    FrameColor = Text(reader, "frame_color"),
                    ViewColor = Text(reader, "view_color")
                });
            }

            return list;
        }

        public static int SaveToken(MapTokenData token)
        {
            if (token == null || token.SceneId <= 0) return 0;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                string sql = token.Id > 0
                    ? @"UPDATE MapToken SET id_combatant = @combatant, label = @label, x = @x, y = @y,
                              size_cells = @size, color = @color, hidden = @hidden, facing = @facing, view_angle = @angle, reveal_cells = @reveal, is_player = @isPlayer,
                              photo_hash = @photoHash, photo_path = @photoPath, frame_color = @frameColor, view_color = @viewColor
                        WHERE id_token = @id"
                    : @"INSERT INTO MapToken (id_scene, id_combatant, label, x, y, size_cells, color, hidden, facing, view_angle, reveal_cells, is_player, photo_hash, photo_path, frame_color, view_color)
                        VALUES (@scene, @combatant, @label, @x, @y, @size, @color, @hidden, @facing, @angle, @reveal, @isPlayer, @photoHash, @photoPath, @frameColor, @viewColor)";

                using var cmd = new SqliteCommand(sql, conn);
                if (token.Id > 0) cmd.Parameters.AddWithValue("@id", token.Id);
                cmd.Parameters.AddWithValue("@scene", token.SceneId);
                cmd.Parameters.AddWithValue("@combatant", token.CombatantId);
                cmd.Parameters.AddWithValue("@label", token.Label ?? "");
                cmd.Parameters.AddWithValue("@x", token.X);
                cmd.Parameters.AddWithValue("@y", token.Y);
                cmd.Parameters.AddWithValue("@size", token.SizeCells <= 0 ? 1 : token.SizeCells);
                cmd.Parameters.AddWithValue("@color", token.Color ?? "");
                cmd.Parameters.AddWithValue("@hidden", token.Hidden ? 1 : 0);
                cmd.Parameters.AddWithValue("@facing", token.Facing);
                cmd.Parameters.AddWithValue("@angle", token.ViewAngle);
                // 0 — обзор выключен (жетон не развеивает туман), это не «по умолчанию 6»
                cmd.Parameters.AddWithValue("@reveal", Math.Max(0, token.RevealCells));
                cmd.Parameters.AddWithValue("@isPlayer", token.IsPlayer ? 1 : 0);
                cmd.Parameters.AddWithValue("@photoHash", token.PhotoHash ?? "");
                cmd.Parameters.AddWithValue("@photoPath", token.PhotoPath ?? "");
                cmd.Parameters.AddWithValue("@frameColor", token.FrameColor ?? "");
                cmd.Parameters.AddWithValue("@viewColor", token.ViewColor ?? "");
                cmd.ExecuteNonQuery();

                if (token.Id <= 0)
                {
                    using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn);
                    token.Id = Convert.ToInt32(idCmd.ExecuteScalar());
                }

                return token.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] SaveToken: {ex.Message}");
                return 0;
            }
        }

        public static void DeleteToken(int tokenId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM MapToken WHERE id_token = @id", conn);
                cmd.Parameters.AddWithValue("@id", tokenId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] DeleteToken: {ex.Message}");
            }
        }

        public static List<MapWallData> LoadWalls(int sceneId)
        {
            var list = new List<MapWallData>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                list = LoadWalls(conn, sceneId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] LoadWalls: {ex.Message}");
            }
            return list;
        }

        private static List<MapWallData> LoadWalls(SqliteConnection conn, int sceneId)
        {
            var list = new List<MapWallData>();

            using var cmd = new SqliteCommand("SELECT * FROM MapWall WHERE id_scene = @id ORDER BY id_wall", conn);
            cmd.Parameters.AddWithValue("@id", sceneId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MapWallData
                {
                    Id = Int(reader, "id_wall"),
                    SceneId = Int(reader, "id_scene"),
                    X1 = Double(reader, "x1", 0),
                    Y1 = Double(reader, "y1", 0),
                    X2 = Double(reader, "x2", 0),
                    Y2 = Double(reader, "y2", 0),
                    Kind = Text(reader, "kind"),
                    IsOpen = Int(reader, "is_open") != 0
                });
            }

            return list;
        }

        public static int SaveWall(MapWallData wall)
        {
            if (wall == null || wall.SceneId <= 0) return 0;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "INSERT INTO MapWall (id_scene, x1, y1, x2, y2, kind, is_open) VALUES (@scene, @x1, @y1, @x2, @y2, @kind, @open)", conn);
                cmd.Parameters.AddWithValue("@scene", wall.SceneId);
                cmd.Parameters.AddWithValue("@x1", wall.X1);
                cmd.Parameters.AddWithValue("@y1", wall.Y1);
                cmd.Parameters.AddWithValue("@x2", wall.X2);
                cmd.Parameters.AddWithValue("@y2", wall.Y2);
                cmd.Parameters.AddWithValue("@kind", string.IsNullOrWhiteSpace(wall.Kind) ? "wall" : wall.Kind);
                cmd.Parameters.AddWithValue("@open", wall.IsOpen ? 1 : 0);
                cmd.ExecuteNonQuery();

                using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn);
                wall.Id = Convert.ToInt32(idCmd.ExecuteScalar());
                return wall.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] SaveWall: {ex.Message}");
                return 0;
            }
        }

        public static void UpdateWall(MapWallData wall)
        {
            if (wall == null || wall.Id <= 0) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "UPDATE MapWall SET kind = @kind, is_open = @open WHERE id_wall = @id", conn);
                cmd.Parameters.AddWithValue("@kind", string.IsNullOrWhiteSpace(wall.Kind) ? "wall" : wall.Kind);
                cmd.Parameters.AddWithValue("@open", wall.IsOpen ? 1 : 0);
                cmd.Parameters.AddWithValue("@id", wall.Id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] UpdateWall: {ex.Message}");
            }
        }

        /// <summary>Разведанные области сцены: точки полигонов, которые уже видели.</summary>
        public static List<string> LoadSeen(int sceneId)
        {
            var list = new List<string>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT points FROM MapSeen WHERE id_scene = @id ORDER BY id_seen", conn);
                cmd.Parameters.AddWithValue("@id", sceneId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string points = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(points)) list.Add(points);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] LoadSeen: {ex.Message}");
            }
            return list;
        }

        public static void AddSeen(int sceneId, string points)
            => AddSeenBatch(sceneId, new[] { points });

        /// <summary>Добавляет несколько секторов одной транзакцией (синхронизация с хостом).</summary>
        public static void AddSeenBatch(int sceneId, IEnumerable<string> points)
        {
            if (sceneId <= 0 || points == null) return;

            var list = points.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (list.Count == 0) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                using (var cmd = new SqliteCommand(
                    "INSERT INTO MapSeen (id_scene, points, created_at) VALUES (@id, @points, @now)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@id", sceneId);
                    cmd.Parameters.AddWithValue("@points", "");
                    cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                    foreach (string pointsText in list)
                    {
                        cmd.Parameters["@points"].Value = pointsText;
                        cmd.ExecuteNonQuery();
                    }
                }

                // Держим запас с большим запасом: раньше лимит 240 срезал
                // старые участки, и уже разведанная зона снова закрывалась
                // туманом, как только добавлялся следующий сектор.
                using (var trim = new SqliteCommand(
                    "DELETE FROM MapSeen WHERE id_scene = @id AND id_seen NOT IN " +
                    "(SELECT id_seen FROM MapSeen WHERE id_scene = @id ORDER BY id_seen DESC LIMIT 2000)", conn, tx))
                {
                    trim.Parameters.AddWithValue("@id", sceneId);
                    trim.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] AddSeenBatch: {ex.Message}");
            }
        }

        public static void ClearSeen(int sceneId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM MapSeen WHERE id_scene = @id", conn);
                cmd.Parameters.AddWithValue("@id", sceneId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] ClearSeen: {ex.Message}");
            }
        }
        public static void DeleteWall(int wallId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM MapWall WHERE id_wall = @id", conn);
                cmd.Parameters.AddWithValue("@id", wallId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] DeleteWall: {ex.Message}");
            }
        }
        public static List<MapShapeData> LoadShapes(int sceneId)
        {
            var list = new List<MapShapeData>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                list = LoadShapes(conn, sceneId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] LoadShapes: {ex.Message}");
            }
            return list;
        }

        private static List<MapShapeData> LoadShapes(SqliteConnection conn, int sceneId)
        {
            var list = new List<MapShapeData>();

            using var cmd = new SqliteCommand("SELECT * FROM MapShape WHERE id_scene = @id ORDER BY id_shape", conn);
            cmd.Parameters.AddWithValue("@id", sceneId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MapShapeData
                {
                    Id = Int(reader, "id_shape"),
                    SceneId = Int(reader, "id_scene"),
                    Kind = Text(reader, "kind"),
                    X = Double(reader, "x", 0),
                    Y = Double(reader, "y", 0),
                    RadiusCells = Double(reader, "radius_cells", 4),
                    Angle = Double(reader, "angle", 0),
                    Color = Text(reader, "color"),
                    Label = Text(reader, "label"),
                    ScriptKind = Text(reader, "script_kind"),
                    ScriptAbility = Text(reader, "script_ability"),
                    ScriptSkill = Text(reader, "script_skill"),
                    ScriptDC = Int(reader, "script_dc"),
                    ScriptName = Text(reader, "script_name")
                });
            }

            return list;
        }

        public static int SaveShape(MapShapeData shape)
        {
            if (shape == null || shape.SceneId <= 0) return 0;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                string sql = shape.Id > 0
                    ? @"UPDATE MapShape SET kind = @kind, x = @x, y = @y, radius_cells = @radius,
                              angle = @angle, color = @color, label = @label,
                              script_kind = @scriptKind, script_ability = @scriptAbility,
                              script_skill = @scriptSkill, script_dc = @scriptDC,
                              script_name = @scriptName WHERE id_shape = @id"
                    : @"INSERT INTO MapShape (id_scene, kind, x, y, radius_cells, angle, color, label,
                              script_kind, script_ability, script_skill, script_dc, script_name)
                        VALUES (@scene, @kind, @x, @y, @radius, @angle, @color, @label,
                              @scriptKind, @scriptAbility, @scriptSkill, @scriptDC, @scriptName)";

                using var cmd = new SqliteCommand(sql, conn);
                if (shape.Id > 0) cmd.Parameters.AddWithValue("@id", shape.Id);
                cmd.Parameters.AddWithValue("@scene", shape.SceneId);
                cmd.Parameters.AddWithValue("@kind", shape.Kind ?? "circle");
                cmd.Parameters.AddWithValue("@x", shape.X);
                cmd.Parameters.AddWithValue("@y", shape.Y);
                cmd.Parameters.AddWithValue("@radius", shape.RadiusCells);
                cmd.Parameters.AddWithValue("@angle", shape.Angle);
                cmd.Parameters.AddWithValue("@color", shape.Color ?? "");
                cmd.Parameters.AddWithValue("@label", shape.Label ?? "");
                cmd.Parameters.AddWithValue("@scriptKind", shape.ScriptKind ?? "");
                cmd.Parameters.AddWithValue("@scriptAbility", shape.ScriptAbility ?? "");
                cmd.Parameters.AddWithValue("@scriptSkill", shape.ScriptSkill ?? "");
                cmd.Parameters.AddWithValue("@scriptDC", shape.ScriptDC);
                cmd.Parameters.AddWithValue("@scriptName", shape.ScriptName ?? "");
                cmd.ExecuteNonQuery();

                if (shape.Id <= 0)
                {
                    using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn);
                    shape.Id = Convert.ToInt32(idCmd.ExecuteScalar());
                }

                return shape.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] SaveShape: {ex.Message}");
                return 0;
            }
        }

        public static void DeleteShape(int shapeId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM MapShape WHERE id_shape = @id", conn);
                cmd.Parameters.AddWithValue("@id", shapeId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] DeleteShape: {ex.Message}");
            }
        }
        public static List<MapStrokeData> LoadStrokes(int sceneId)
        {
            var list = new List<MapStrokeData>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                list = LoadStrokes(conn, sceneId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] LoadStrokes: {ex.Message}");
            }
            return list;
        }

        private static List<MapStrokeData> LoadStrokes(SqliteConnection conn, int sceneId)
        {
            var list = new List<MapStrokeData>();

            using var cmd = new SqliteCommand("SELECT * FROM MapStroke WHERE id_scene = @id ORDER BY id_stroke", conn);
            cmd.Parameters.AddWithValue("@id", sceneId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MapStrokeData
                {
                    Id = Int(reader, "id_stroke"),
                    SceneId = Int(reader, "id_scene"),
                    Color = Text(reader, "color"),
                    Thickness = Double(reader, "thickness", 4),
                    Points = Text(reader, "points"),
                    IsEraser = Int(reader, "is_eraser") != 0
                });
            }

            return list;
        }

        public static int SaveStroke(MapStrokeData stroke)
        {
            if (stroke == null || stroke.SceneId <= 0) return 0;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                string sql = stroke.Id > 0
                    ? "UPDATE MapStroke SET color = @color, thickness = @thickness, points = @points, is_eraser = @eraser WHERE id_stroke = @id"
                    : "INSERT INTO MapStroke (id_scene, color, thickness, points, is_eraser) VALUES (@scene, @color, @thickness, @points, @eraser)";

                using var cmd = new SqliteCommand(sql, conn);
                if (stroke.Id > 0) cmd.Parameters.AddWithValue("@id", stroke.Id);
                cmd.Parameters.AddWithValue("@scene", stroke.SceneId);
                cmd.Parameters.AddWithValue("@color", stroke.Color ?? "");
                cmd.Parameters.AddWithValue("@thickness", stroke.Thickness);
                cmd.Parameters.AddWithValue("@points", stroke.Points ?? "");
                cmd.Parameters.AddWithValue("@eraser", stroke.IsEraser ? 1 : 0);
                cmd.ExecuteNonQuery();

                if (stroke.Id <= 0)
                {
                    using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn);
                    stroke.Id = Convert.ToInt32(idCmd.ExecuteScalar());
                }

                return stroke.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] SaveStroke: {ex.Message}");
                return 0;
            }
        }

        public static void DeleteStroke(int strokeId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM MapStroke WHERE id_stroke = @id", conn);
                cmd.Parameters.AddWithValue("@id", strokeId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] DeleteStroke: {ex.Message}");
            }
        }

        /// <summary>Стирает все штрихи сцены.</summary>
        public static void ClearStrokes(int sceneId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM MapStroke WHERE id_scene = @id", conn);
                cmd.Parameters.AddWithValue("@id", sceneId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapService] ClearStrokes: {ex.Message}");
            }
        }
        /// <summary>
        /// Раскладывает жетоны из трекера инициативы: на каждого участника боя,
        /// у которого ещё нет жетона, ставится новый. Уже стоящие не двигаются.
        /// </summary>
        public static int SyncTokensFromEncounter(MapSceneData scene, EncounterData encounter)
        {
            if (scene == null || encounter == null) return 0;

            int added = 0;
            var existing = new HashSet<int>(scene.Tokens.Where(t => t.CombatantId > 0).Select(t => t.CombatantId));

            double cell = scene.GridPx <= 1 ? 70 : scene.GridPx;
            int index = 0;

            foreach (var combatant in encounter.Combatants)
            {
                if (combatant == null || combatant.Id <= 0) continue;
                if (existing.Contains(combatant.Id)) { index++; continue; }

                var token = new MapTokenData
                {
                    SceneId = scene.Id,
                    CombatantId = combatant.Id,
                    Label = combatant.Name,
                    X = cell * (1.5 + (index % 6)),
                    Y = cell * (1.5 + (index / 6)),
                    SizeCells = 1
                };

                if (SaveToken(token) > 0)
                {
                    scene.Tokens.Add(token);
                    added++;
                }

                index++;
            }

            return added;
        }

        #endregion

        #region === ЧТЕНИЕ ПОЛЕЙ ===

        private static string Text(SqliteDataReader reader, string column)
        {
            try
            {
                object value = reader[column];
                return value == null || value == DBNull.Value ? "" : Convert.ToString(value) ?? "";
            }
            catch { return ""; }
        }

        private static int Int(SqliteDataReader reader, string column)
        {
            try
            {
                object value = reader[column];
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
            catch { return 0; }
        }

        private static double Double(SqliteDataReader reader, string column, double fallback)
        {
            try
            {
                object value = reader[column];
                return value == null || value == DBNull.Value ? fallback : Convert.ToDouble(value);
            }
            catch { return fallback; }
        }

        #endregion
    }
}
