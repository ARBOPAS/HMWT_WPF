using HMWT_MUI.Helpers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Пак справочника заклинаний (.hmwtpack).
    ///
    /// Выгружает справочник целиком: и созданные вручную (source = custom),
    /// и строки из книг. Нужен, чтобы наполнить справочник на другой машине
    /// одним файлом, не перебивая его руками.
    ///
    /// Внутри zip: manifest.json (что за пак, откуда, лицензия) и spells.json.
    ///
    /// Важно про схему: у таблицы SpellReference в старых базах есть колонки
    /// is_modified и damage_formula, а в новой базе их нет. Поэтому и чтение,
    /// и запись строятся по фактическому списку колонок из PRAGMA table_info —
    /// иначе импорт на чистой базе падал с «no column named is_modified».
    /// </summary>
    public static class SpellPackService
    {
        public const int FormatVersion = 1;

        public const string FileExtension = ".hmwtpack";

        private const string ManifestEntry = "manifest.json";
        private const string SpellsEntry = "spells.json";

        /// <summary>Колонки, которые умеет носить пак. Порядок — как в базе.</summary>
        private static readonly string[] WantedColumns =
        {
            "name", "level", "school", "casting_time", "range", "components", "duration",
            "description", "at_higher_levels", "classes", "ritual", "concentration",
            "source", "material", "page", "language", "archetype", "domains",
            "damage_formula", "source_json", "is_modified"
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        #region === МОДЕЛЬ ФАЙЛА ===

        public sealed class Manifest
        {
            public int Version { get; set; } = FormatVersion;
            public string Kind { get; set; } = "spells";
            public string App { get; set; } = "HMWT Гримуар";
            public string AppVersion { get; set; } = "";
            public string CreatedAt { get; set; } = "";
            public string Name { get; set; } = "";
            public int Count { get; set; }
            public int CustomCount { get; set; }
            public List<string> Sources { get; set; } = new List<string>();
            public string SourceNote { get; set; } = "";
            public string License { get; set; } = "";
            public string Changed { get; set; } = "";
        }

        public sealed class PackSpell
        {
            public string Name { get; set; } = "";
            public int Level { get; set; }
            public string School { get; set; } = "";
            public string CastingTime { get; set; } = "";
            public string Range { get; set; } = "";
            public string Components { get; set; } = "";
            public string Duration { get; set; } = "";
            public string Description { get; set; } = "";
            public string AtHigherLevels { get; set; } = "";
            public string Classes { get; set; } = "";
            public bool Ritual { get; set; }
            public bool Concentration { get; set; }
            public string Source { get; set; } = "";
            public string Material { get; set; } = "";
            public string Page { get; set; } = "";
            public string Language { get; set; } = "";
            public string Archetype { get; set; } = "";
            public string Domains { get; set; } = "";
            public string DamageFormula { get; set; } = "";
            public string SourceJson { get; set; } = "";
        }

        #endregion

        #region === СХЕМА ===

        /// <summary>Фактический список колонок таблицы справочника.</summary>
        private static HashSet<string> Columns(SqliteConnection conn)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var cmd = new SqliteCommand("PRAGMA table_info(SpellReference)", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) set.Add(Convert.ToString(reader["name"]) ?? "");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellPack] Columns: {ex.Message}");
            }
            return set;
        }

        private static string SqlName(string column) =>
            string.Equals(column, "range", StringComparison.OrdinalIgnoreCase) ? "[range]" : column;

        #endregion

        #region === ЭКСПОРТ ===

        /// <summary>Сколько всего строк в справочнике и сколько из них «свои».</summary>
        public static (int Total, int Custom) CountSpells()
        {
            int total = 0, custom = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM SpellReference", conn))
                    total = Convert.ToInt32(cmd.ExecuteScalar());
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM SpellReference WHERE source = 'custom'", conn))
                    custom = Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellPack] CountSpells: {ex.Message}");
            }
            return (total, custom);
        }

        /// <summary>Сохраняет справочник в пак. onlyCustom — только созданные вручную.</summary>
        public static bool Export(string path, bool onlyCustom, string name, string sourceNote,
            out int count, out int customCount, out string error)
        {
            count = 0;
            customCount = 0;
            error = "";

            try
            {
                if (string.IsNullOrWhiteSpace(path)) { error = "Не указан файл."; return false; }

                var spells = new List<PackSpell>();

                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    var columns = Columns(conn);
                    var present = WantedColumns.Where(c => columns.Contains(c)).ToList();
                    if (present.Count == 0) { error = "В базе нет таблицы справочника заклинаний."; return false; }

                    string sql = "SELECT " + string.Join(", ", present.Select(SqlName)) + " FROM SpellReference" +
                                 (onlyCustom ? " WHERE source = 'custom'" : "") + " ORDER BY name";

                    using var cmd = new SqliteCommand(sql, conn);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        spells.Add(new PackSpell
                        {
                            Name = Text(reader, "name"),
                            Level = Int(reader, "level"),
                            School = Text(reader, "school"),
                            CastingTime = Text(reader, "casting_time"),
                            Range = Text(reader, "range"),
                            Components = Text(reader, "components"),
                            Duration = Text(reader, "duration"),
                            Description = Text(reader, "description"),
                            AtHigherLevels = Text(reader, "at_higher_levels"),
                            Classes = Text(reader, "classes"),
                            Ritual = Int(reader, "ritual") == 1,
                            Concentration = Int(reader, "concentration") == 1,
                            Source = Text(reader, "source"),
                            Material = Text(reader, "material"),
                            Page = Text(reader, "page"),
                            Language = Text(reader, "language"),
                            Archetype = Text(reader, "archetype"),
                            Domains = Text(reader, "domains"),
                            DamageFormula = Text(reader, "damage_formula"),
                            SourceJson = Text(reader, "source_json")
                        });
                    }
                }

                if (spells.Count == 0) { error = "Справочник пуст — нечего выгружать."; return false; }

                var manifest = new Manifest
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Spells" : name.Trim(),
                    Count = spells.Count,
                    CustomCount = spells.Count(s => string.Equals(s.Source, "custom", StringComparison.OrdinalIgnoreCase)),
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    AppVersion = Net.NetVersions.AppVersion,
                    Sources = spells.Select(s => (s.Source ?? "").Trim())
                                    .Where(s => s.Length > 0)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                                    .ToList(),
                    SourceNote = (sourceNote ?? "").Trim(),
                    License = "",
                    Changed = "переформатировано в справочник заклинаний HMWT"
                };

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(path)) File.Delete(path);

                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    WriteJson(zip, ManifestEntry, manifest);
                    WriteJson(zip, SpellsEntry, spells);
                }

                count = spells.Count;
                customCount = manifest.CustomCount;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellPack] Export error: {ex.Message}");
                error = ex.Message;
                return false;
            }
        }

        private static void WriteJson<T>(ZipArchive zip, string entryName, T value)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(value, WriteOptions));
        }

        #endregion

        #region === ИМПОРТ ===

        /// <summary>Добавляет заклинания из пака. Совпадающие по названию и источнику пропускает.</summary>
        public static bool Import(string path, out int added, out int skipped, out string error)
        {
            added = 0;
            skipped = 0;
            error = "";

            string tempDir = null;

            try
            {
                if (!File.Exists(path)) { error = "Файл не найден."; return false; }

                tempDir = Path.Combine(Path.GetTempPath(), "hmwt-spellpack-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(path, tempDir);

                string manifestPath = Path.Combine(tempDir, ManifestEntry);
                if (!File.Exists(manifestPath)) { error = "Это не пак HMWT: внутри нет manifest.json"; return false; }

                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), ReadOptions);
                if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Kind) &&
                    !string.Equals(manifest.Kind, "spells", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Это пак «" + manifest.Kind + "», а не справочник заклинаний.";
                    return false;
                }

                string spellsPath = Path.Combine(tempDir, SpellsEntry);
                if (!File.Exists(spellsPath)) { error = "В паке нет списка заклинаний (spells.json)."; return false; }

                var pack = JsonSerializer.Deserialize<List<PackSpell>>(File.ReadAllText(spellsPath), ReadOptions)
                           ?? new List<PackSpell>();

                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                var columns = Columns(conn);
                var present = WantedColumns.Where(c => columns.Contains(c)).ToList();
                if (present.Count == 0) { error = "В базе нет таблицы справочника заклинаний."; return false; }

                // Каждой колонке — свой параметр. Вставляем только существующие колонки,
                // поэтому пак одинаково ложится и в старую базу, и в новую.
                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                string insertSql = "INSERT INTO SpellReference (" + string.Join(", ", present.Select(SqlName)) +
                                   ") VALUES (" + string.Join(", ", present.Select(c => "@" + c)) + ")";

                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqliteCommand("SELECT name, source FROM SpellReference", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        existing.Add((reader[0] as string ?? "").Trim() + "|" + (reader[1] as string ?? "").Trim());
                    }
                }

                using var tx = conn.BeginTransaction();
                using var insert = new SqliteCommand(insertSql, conn, tx);

                foreach (var spell in pack)
                {
                    if (spell == null) continue;

                    string name = (spell.Name ?? "").Trim();
                    string source = (spell.Source ?? "").Trim();
                    if (name.Length == 0) { skipped++; continue; }

                    string key = name + "|" + source;
                    if (!existing.Add(key)) { skipped++; continue; }

                    values.Clear();
                    values["name"] = name;
                    values["level"] = spell.Level;
                    values["school"] = spell.School ?? "";
                    values["casting_time"] = spell.CastingTime ?? "";
                    values["range"] = spell.Range ?? "";
                    values["components"] = spell.Components ?? "";
                    values["duration"] = spell.Duration ?? "";
                    values["description"] = spell.Description ?? "";
                    values["at_higher_levels"] = spell.AtHigherLevels ?? "";
                    values["classes"] = spell.Classes ?? "";
                    values["ritual"] = spell.Ritual ? 1 : 0;
                    values["concentration"] = spell.Concentration ? 1 : 0;
                    values["source"] = source;
                    values["material"] = spell.Material ?? "";
                    values["page"] = spell.Page ?? "";
                    values["language"] = spell.Language ?? "";
                    values["archetype"] = spell.Archetype ?? "";
                    values["domains"] = spell.Domains ?? "";
                    values["damage_formula"] = spell.DamageFormula ?? "";
                    values["source_json"] = spell.SourceJson ?? "";
                    values["is_modified"] = 0;

                    insert.Parameters.Clear();
                    foreach (string column in present)
                    {
                        insert.Parameters.AddWithValue("@" + column,
                            values.TryGetValue(column, out object value) ? (value ?? "") : "");
                    }

                    insert.ExecuteNonQuery();
                    added++;
                }

                tx.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellPack] Import error: {ex.Message}");
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion

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
    }
}
