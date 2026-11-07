using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HMWT_MUI.Helpers;
using Microsoft.Data.Sqlite;

namespace HMWT_MUI.Services
{
    /// <summary>Краткий лист существа-призыва.</summary>
    public sealed class SummonData
    {
        public int Id { get; set; }
        public int CharacterId { get; set; }
        public string Name { get; set; } = "";
        public string CreatureType { get; set; } = "";

        /// <summary>Размер в клетках: 0.5 — мелкое существо, 2 — большое.</summary>
        public double SizeCells { get; set; } = 1;

        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Ac { get; set; }
        public string Speed { get; set; } = "";

        public int Str { get; set; } = 10;
        public int Dex { get; set; } = 10;
        public int Con { get; set; } = 10;
        public int Int { get; set; } = 10;
        public int Wis { get; set; } = 10;
        public int Cha { get; set; } = 10;

        public string Attacks { get; set; } = "";
        public string Abilities { get; set; } = "";
        public string PhotoPath { get; set; } = "";

        /// <summary>Подпись для карточки: тип · ХП · КД.</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(CreatureType)) parts.Add(CreatureType.Trim());
                if (MaxHp > 0) parts.Add($"ХП {MaxHp}");
                if (Ac > 0) parts.Add($"КД {Ac}");
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// Библиотека призывов персонажа: водный элементаль, волк, скелет — то,
    /// чем игрок управляет на карте. Хранится в своей таблице, чтобы не
    /// заводить существо в бестиарий и не смешивать с монстрами мастера.
    /// </summary>
    public static class SummonService
    {
        private static bool _ready;
        private static readonly object Gate = new object();

        public static string SummonsFolder => Path.Combine(PhotoHelper.GetWritableRoot(), "Summons");

        private static void EnsureTable()
        {
            if (_ready) return;

            lock (Gate)
            {
                if (_ready) return;

                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();
                    using var cmd = new SqliteCommand(@"
                        CREATE TABLE IF NOT EXISTS CharacterSummon (
                            id_summon INTEGER PRIMARY KEY AUTOINCREMENT,
                            id_character INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            creature_type TEXT,
                            size_cells REAL DEFAULT 1,
                            hp INTEGER DEFAULT 0,
                            max_hp INTEGER DEFAULT 0,
                            ac INTEGER DEFAULT 0,
                            speed TEXT,
                            str INTEGER DEFAULT 10,
                            dex INTEGER DEFAULT 10,
                            con INTEGER DEFAULT 10,
                            int INTEGER DEFAULT 10,
                            wis INTEGER DEFAULT 10,
                            cha INTEGER DEFAULT 10,
                            attacks TEXT,
                            abilities TEXT,
                            photo_path TEXT,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                            updated_at TEXT DEFAULT CURRENT_TIMESTAMP);", conn);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SummonService] таблица: {ex.Message}");
                    return;
                }

                _ready = true;
            }
        }

        public static List<SummonData> Load(int characterId)
        {
            var result = new List<SummonData>();
            if (characterId <= 0) return result;

            EnsureTable();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT id_summon, id_character, name, creature_type, size_cells, hp, max_hp, ac, speed,
                           str, dex, con, int, wis, cha, attacks, abilities, photo_path
                    FROM CharacterSummon WHERE id_character = @id ORDER BY name COLLATE NOCASE", conn);
                cmd.Parameters.AddWithValue("@id", characterId);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    result.Add(new SummonData
                    {
                        Id = r.GetInt32(0),
                        CharacterId = r.GetInt32(1),
                        Name = Text(r, 2),
                        CreatureType = Text(r, 3),
                        SizeCells = r.IsDBNull(4) ? 1 : r.GetDouble(4),
                        Hp = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                        MaxHp = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                        Ac = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                        Speed = Text(r, 8),
                        Str = r.IsDBNull(9) ? 10 : r.GetInt32(9),
                        Dex = r.IsDBNull(10) ? 10 : r.GetInt32(10),
                        Con = r.IsDBNull(11) ? 10 : r.GetInt32(11),
                        Int = r.IsDBNull(12) ? 10 : r.GetInt32(12),
                        Wis = r.IsDBNull(13) ? 10 : r.GetInt32(13),
                        Cha = r.IsDBNull(14) ? 10 : r.GetInt32(14),
                        Attacks = Text(r, 15),
                        Abilities = Text(r, 16),
                        PhotoPath = Text(r, 17)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SummonService] Load: {ex.Message}");
            }

            return result;
        }

        public static int Save(SummonData summon)
        {
            if (summon == null || summon.CharacterId <= 0 || string.IsNullOrWhiteSpace(summon.Name)) return 0;

            EnsureTable();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                string sql = summon.Id > 0
                    ? @"UPDATE CharacterSummon SET
                            name=@name, creature_type=@type, size_cells=@size, hp=@hp, max_hp=@max,
                            ac=@ac, speed=@speed, str=@str, dex=@dex, con=@con, int=@int, wis=@wis,
                            cha=@cha, attacks=@attacks, abilities=@abilities, photo_path=@photo,
                            updated_at=CURRENT_TIMESTAMP
                        WHERE id_summon=@id; SELECT @id;"
                    : @"INSERT INTO CharacterSummon
                            (id_character, name, creature_type, size_cells, hp, max_hp, ac, speed,
                             str, dex, con, int, wis, cha, attacks, abilities, photo_path)
                        VALUES
                            (@char, @name, @type, @size, @hp, @max, @ac, @speed,
                             @str, @dex, @con, @int, @wis, @cha, @attacks, @abilities, @photo);
                        SELECT last_insert_rowid();";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@char", summon.CharacterId);
                cmd.Parameters.AddWithValue("@name", summon.Name.Trim());
                cmd.Parameters.AddWithValue("@type", summon.CreatureType ?? "");
                cmd.Parameters.AddWithValue("@size", Math.Max(0.5, Math.Min(10, summon.SizeCells)));
                cmd.Parameters.AddWithValue("@hp", summon.Hp);
                cmd.Parameters.AddWithValue("@max", summon.MaxHp);
                cmd.Parameters.AddWithValue("@ac", summon.Ac);
                cmd.Parameters.AddWithValue("@speed", summon.Speed ?? "");
                cmd.Parameters.AddWithValue("@str", summon.Str);
                cmd.Parameters.AddWithValue("@dex", summon.Dex);
                cmd.Parameters.AddWithValue("@con", summon.Con);
                cmd.Parameters.AddWithValue("@int", summon.Int);
                cmd.Parameters.AddWithValue("@wis", summon.Wis);
                cmd.Parameters.AddWithValue("@cha", summon.Cha);
                cmd.Parameters.AddWithValue("@attacks", summon.Attacks ?? "");
                cmd.Parameters.AddWithValue("@abilities", summon.Abilities ?? "");
                cmd.Parameters.AddWithValue("@photo", summon.PhotoPath ?? "");
                if (summon.Id > 0) cmd.Parameters.AddWithValue("@id", summon.Id);

                object raw = cmd.ExecuteScalar();
                int id = raw == null || raw == DBNull.Value ? 0 : Convert.ToInt32(raw);
                if (summon.Id > 0 && id <= 0) id = summon.Id;
                summon.Id = id;
                return id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SummonService] Save: {ex.Message}");
                return 0;
            }
        }

        public static void Delete(int id)
        {
            if (id <= 0) return;

            EnsureTable();

            try
            {
                string photo = null;
                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using (var find = new SqliteCommand(
                               "SELECT photo_path FROM CharacterSummon WHERE id_summon=@id", conn))
                    {
                        find.Parameters.AddWithValue("@id", id);
                        photo = find.ExecuteScalar()?.ToString();
                    }

                    using var cmd = new SqliteCommand("DELETE FROM CharacterSummon WHERE id_summon=@id", conn);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }

                // Картинка своя (лежит в Summons) — убираем вместе с записью
                if (!string.IsNullOrWhiteSpace(photo))
                {
                    string full = PhotoHelper.ResolvePhotoPath(photo);
                    if (!string.IsNullOrEmpty(full) && File.Exists(full)) File.Delete(full);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SummonService] Delete: {ex.Message}");
            }
        }

        /// <summary>Копирует выбранную картинку в папку данных и возвращает путь.</summary>
        public static string ImportPhoto(int summonId, string sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile)) return "";

            try
            {
                Directory.CreateDirectory(SummonsFolder);

                string ext = (Path.GetExtension(sourceFile) ?? ".png").ToLowerInvariant();
                string name = $"summon_{summonId}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}".Substring(0, 40) + ext;
                string target = Path.Combine(SummonsFolder, name);

                File.Copy(sourceFile, target, true);
                return "Summons/" + name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SummonService] ImportPhoto: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Готовый лист водного элементаля (SRD 5.1) — пример заполнения:
        /// игроку не нужно искать цифры в книге, чтобы попробовать функцию.
        /// </summary>
        public static SummonData WaterElemental(int characterId) => new SummonData
        {
            CharacterId = characterId,
            Name = "Водный элементаль",
            CreatureType = "Элементаль, большой",
            SizeCells = 2,
            Hp = 114,
            MaxHp = 114,
            Ac = 14,
            Speed = "30 фт, плавание 90 фт",
            Str = 18, Dex = 14, Con = 18, Int = 5, Wis = 10, Cha = 8,
            Attacks =
                "Мультиатака: две атаки «Удар».\n" +
                "Удар: +7 к попаданию, досягаемость 5 фт, одна цель. " +
                "Попадание: 2к8+4 дробящего урона.",
            Abilities =
                "Водная форма: может проходить через пространство шириной 1 дюйм, не сжимаясь.\n" +
                "Захват водой (1/ход): цель размера не больше Большого должна пройти спас. " +
                "СИЛ СЛ 14, иначе схвачена и опутана, пока элементаль не уйдёт."
        };

        private static string Text(SqliteDataReader r, int index)
            => r.IsDBNull(index) ? "" : r.GetValue(index)?.ToString() ?? "";
    }
}
