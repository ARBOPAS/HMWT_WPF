using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Services
{
    /// <summary>Умение/черта персонажа. Хранится в JSON рядом с остальными
    /// дополнительными данными листа; справочник читается из SrdReference.</summary>
    public sealed class FeatureEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "Features";
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = "";
    }

    public static class CharacterFeatureCatalog
    {
        public static List<FeatureEntry> Load(string json)
        {
            var result = new List<FeatureEntry>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                var parsed = JsonSerializer.Deserialize<List<FeatureEntry>>(json);
                if (parsed == null) return result;
                foreach (var entry in parsed)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) continue;
                    entry.Name = entry.Name.Trim();
                    entry.Source = (entry.Source ?? "").Trim();
                    if (result.Any(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(e.Source, entry.Source, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    result.Add(entry);
                }
            }
            catch { }
            return result;
        }

        public static string Save(IEnumerable<FeatureEntry> entries)
            => JsonSerializer.Serialize(entries?.ToList() ?? new List<FeatureEntry>());

        /// <summary>
        /// В импортёре издание называется «SRD 5.2» / «SRD 5.1».
        /// 2024 берём русскую локализацию, 2014 — английскую: так их и
        /// импортируют по умолчанию, и в списке не будет двух копий подряд.
        /// </summary>
        private static string BookPrefix(string edition)
            => string.Equals(edition, "2014", StringComparison.Ordinal)
                ? "SRD 5.1 · en"
                : "SRD 5.2 · ru";

        private static bool _metadataChecked;

        /// <summary>
        /// Старые импорты не сохраняли владельца и уровень. Они лежат в
        /// properties (сырой JSON), поэтому один раз разбираем и заполняем.
        /// </summary>
        private static void EnsureMetadata()
        {
            if (_metadataChecked) return;
            _metadataChecked = true;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                var rows = new List<(long Id, string Category, string Raw)>();
                using (var read = new SqliteCommand(@"
                    SELECT id_srd, category, COALESCE(properties,'')
                    FROM SrdReference
                    WHERE category IN ('Features','Traits','Feats')
                      AND (TRIM(COALESCE(owner,'')) = '' OR COALESCE(level,0) = 0)
                      AND TRIM(COALESCE(properties,'')) LIKE '{%'", conn))
                using (var r = read.ExecuteReader())
                {
                    while (r.Read())
                        rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
                }

                if (rows.Count == 0) return;

                using var tx = conn.BeginTransaction();
                using var update = conn.CreateCommand();
                update.Transaction = tx;
                update.CommandText = "UPDATE SrdReference SET owner=@owner, level=@level WHERE id_srd=@id";
                foreach (var row in rows)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(row.Raw);
                        var root = doc.RootElement;
                        string owner = OwnerOf(root);
                        int level = LevelOf(root);
                        update.Parameters.Clear();
                        update.Parameters.AddWithValue("@owner", owner);
                        update.Parameters.AddWithValue("@level", level);
                        update.Parameters.AddWithValue("@id", row.Id);
                        update.ExecuteNonQuery();
                    }
                    catch { }
                }
                tx.Commit();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Features] metadata: {ex.Message}"); }
        }

        private static string OwnerOf(JsonElement el)
        {
            string className = NestedText(el, "class", "name");
            if (className.Length > 0) return className;
            string species = NamesOf(el, "species");
            if (species.Length > 0) return species;
            string races = NamesOf(el, "races");
            if (races.Length > 0) return races;
            return TextOf(el, "type");
        }

        private static int LevelOf(JsonElement el)
        {
            if (!el.TryGetProperty("level", out var level)) return 0;
            if (level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out int number)) return number;
            string text = level.ValueKind == JsonValueKind.Object
                ? TextOf(level, "name")
                : level.ValueKind == JsonValueKind.String ? level.GetString() ?? "" : "";
            var match = System.Text.RegularExpressions.Regex.Match(text ?? "", @"(\d+)\s*$");
            return match.Success && int.TryParse(match.Groups[1].Value, out int parsed) ? parsed : 0;
        }

        private static string NestedText(JsonElement el, string property, string sub)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(property, out var value)) return "";
            return value.ValueKind == JsonValueKind.Object ? TextOf(value, sub) : "";
        }

        private static string TextOf(JsonElement el, string property)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(property, out var value)) return "";
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        }

        private static string NamesOf(JsonElement el, string property)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(property, out var value) ||
                value.ValueKind != JsonValueKind.Array) return "";
            foreach (var item in value.EnumerateArray())
            {
                string name = item.ValueKind == JsonValueKind.String
                    ? item.GetString() ?? ""
                    : TextOf(item, "name");
                if (name.Length > 0) return name;
            }
            return "";
        }

        /// <summary>Классы/виды, для которых в базе есть записи выбранного издания.</summary>
        public static List<string> LoadOwners(string edition)
        {
            var result = new List<string>();
            try
            {
                EnsureMetadata();
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT DISTINCT owner FROM SrdReference
                    WHERE category IN ('Features','Traits','Feats')
                      AND TRIM(COALESCE(owner,'')) <> ''
                      AND book LIKE @edition
                    ORDER BY owner", conn);
                cmd.Parameters.AddWithValue("@edition", BookPrefix(edition) + "%");
                using var r = cmd.ExecuteReader();
                while (r.Read()) result.Add(r.GetString(0));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Features] owners: {ex.Message}"); }
            return result;
        }

        /// <summary>Справочник умений выбранного издания с фильтрами.</summary>
        public static List<FeatureEntry> LoadCatalog(string edition, string category,
                                                     string owner, string search)
        {
            var result = new List<FeatureEntry>();
            try
            {
                EnsureMetadata();
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT category, name, COALESCE(owner,''), COALESCE(level,0),
                           COALESCE(description,'')
                    FROM SrdReference
                    WHERE category IN ('Features','Traits','Feats')
                      AND book LIKE @edition
                      AND (@cat = '' OR category = @cat)
                      AND (@owner = '' OR owner = @owner)
                      AND (@q = '' OR name LIKE @like OR description LIKE @like)
                    ORDER BY category, owner, level, name", conn);
                cmd.Parameters.AddWithValue("@edition", BookPrefix(edition) + "%");
                cmd.Parameters.AddWithValue("@cat", category ?? "");
                cmd.Parameters.AddWithValue("@owner", owner ?? "");
                cmd.Parameters.AddWithValue("@q", search ?? "");
                cmd.Parameters.AddWithValue("@like", "%" + (search ?? "") + "%");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    result.Add(new FeatureEntry
                    {
                        Kind = r.GetString(0),
                        Name = r.GetString(1),
                        Source = r.GetString(2),
                        Level = Convert.ToInt32(r.GetValue(3)),
                        Description = r.GetString(4)
                    });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Features] catalog: {ex.Message}"); }
            return result;
        }
    }
}
