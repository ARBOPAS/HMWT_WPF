using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMWT_MUI.Services
{
    public sealed class SrdEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("properties")]
        public Dictionary<string, JsonElement> Properties { get; set; } = new();

        [JsonPropertyName("publisher")]
        public string Publisher { get; set; } = "";

        [JsonPropertyName("book")]
        public string Book { get; set; } = "";
    }

    public static class SrdImportService
    {
        private static readonly string DataDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Data", "srd");

        /// <summary>
        /// Импортирует JSON-файл категории в таблицу SrdReference.
        /// Возвращает количество добавленных записей.
        /// </summary>
        public static int Import(string category)
        {
            var file = Path.Combine(DataDir, category + ".json");
            if (!File.Exists(file)) return 0;

            var entries = JsonSerializer.Deserialize<List<SrdEntry>>(File.ReadAllText(file));
            if (entries == null || entries.Count == 0) return 0;

            int added = 0;
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using (var tx = conn.BeginTransaction())
            {
                using var cmd = new SqliteCommand(@"
            INSERT OR IGNORE INTO SrdReference 
            (category, name, description, properties, publisher, book)
            VALUES (@c, @n, @d, @p, @pub, @b)", conn, tx);

                foreach (var entry in entries)
                {
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@c", category);
                    cmd.Parameters.AddWithValue("@n", entry.Name ?? "");
                    cmd.Parameters.AddWithValue("@d", entry.Description ?? "");
                    cmd.Parameters.AddWithValue("@p", JsonSerializer.Serialize(entry.Properties));
                    cmd.Parameters.AddWithValue("@pub", entry.Publisher ?? "");
                    cmd.Parameters.AddWithValue("@b", entry.Book ?? "");

                    added += cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return added;
        }

        /// <summary>
        /// Получает все записи категории из БД.
        /// </summary>
        public static List<SrdEntry> GetAll(string category)
        {
            var result = new List<SrdEntry>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(@"
                SELECT name, description, properties, publisher, book
                FROM SrdReference
                WHERE category = @c
                ORDER BY name", conn);
            cmd.Parameters.AddWithValue("@c", category);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var propsJson = reader["properties"]?.ToString() ?? "{}";
                var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(propsJson)
                         ?? new Dictionary<string, JsonElement>();

                result.Add(new SrdEntry
                {
                    Name = reader["name"]?.ToString() ?? "",
                    Description = reader["description"]?.ToString() ?? "",
                    Properties = props,
                    Publisher = reader["publisher"]?.ToString() ?? "",
                    Book = reader["book"]?.ToString() ?? ""
                });
            }
            return result;
        }

        /// <summary>
        /// Поиск записей категории по имени (case-insensitive).
        /// </summary>
        public static List<SrdEntry> Search(string category, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return GetAll(category);

            var result = new List<SrdEntry>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(@"
                SELECT name, description, properties, publisher, book
                FROM SrdReference
                WHERE category = @c AND name LIKE @q
                ORDER BY name", conn);
            cmd.Parameters.AddWithValue("@c", category);
            cmd.Parameters.AddWithValue("@q", $"%{query}%");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var propsJson = reader["properties"]?.ToString() ?? "{}";
                var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(propsJson)
                         ?? new Dictionary<string, JsonElement>();

                result.Add(new SrdEntry
                {
                    Name = reader["name"]?.ToString() ?? "",
                    Description = reader["description"]?.ToString() ?? "",
                    Properties = props,
                    Publisher = reader["publisher"]?.ToString() ?? "",
                    Book = reader["book"]?.ToString() ?? ""
                });
            }
            return result;
        }

        /// <summary>
        /// Проверяет, импортирована ли категория.
        /// </summary>
        public static bool IsImported(string category)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(
                "SELECT COUNT(1) FROM SrdReference WHERE category = @c", conn);
            cmd.Parameters.AddWithValue("@c", category);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }
}