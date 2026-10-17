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
        /// <summary>Номер записи в базе: по нему правим и удаляем.</summary>
        public long Id { get; set; }

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
        /// Разделы, которые реально лежат в базе, по алфавиту.
        ///
        /// Нужны вкладке «Справочники»: она показывает только то, что игрок
        /// импортировал, а не жёсткий список ожидаемых файлов.
        /// </summary>
        public static List<string> GetCategories()
        {
            var result = new List<string>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT DISTINCT category FROM SrdReference WHERE COALESCE(category, '') <> '' ORDER BY category", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string category = reader[0]?.ToString() ?? "";
                    if (category.Length > 0) result.Add(category);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SrdImportService] разделы: {ex.Message}");
            }
            return result;
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
                SELECT id_srd, name, description, properties, publisher, book
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
                    Id = Convert.ToInt64(reader["id_srd"]),
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
                SELECT id_srd, name, description, properties, publisher, book
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
                    Id = Convert.ToInt64(reader["id_srd"]),
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
        /// Меняет статью справочника: игрок правит текст под свою игру.
        /// </summary>
        public static void UpdateEntry(long id, string name, string description, string book, string publisher)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"
                UPDATE SrdReference
                SET name = @n, description = @d, book = @b, publisher = @p
                WHERE id_srd = @id", conn);
            cmd.Parameters.AddWithValue("@n", name ?? "");
            cmd.Parameters.AddWithValue("@d", description ?? "");
            cmd.Parameters.AddWithValue("@b", book ?? "");
            cmd.Parameters.AddWithValue("@p", publisher ?? "");
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Добавляет свою статью в раздел. Возвращает её номер.</summary>
        public static long AddEntry(string category, string name, string description, string book, string publisher)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"
                INSERT INTO SrdReference (category, name, description, properties, publisher, book)
                VALUES (@c, @n, @d, '{}', @p, @b);
                SELECT last_insert_rowid();", conn);
            cmd.Parameters.AddWithValue("@c", category ?? "");
            cmd.Parameters.AddWithValue("@n", name ?? "");
            cmd.Parameters.AddWithValue("@d", description ?? "");
            cmd.Parameters.AddWithValue("@p", publisher ?? "");
            cmd.Parameters.AddWithValue("@b", book ?? "");
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        /// <summary>Удаляет статью справочника.</summary>
        public static void DeleteEntry(long id)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("DELETE FROM SrdReference WHERE id_srd = @id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
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
