using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Модель тега
    /// </summary>
    public class ItemTag
    {
        public int Id { get; set; }
        public string TagName { get; set; }
        public string TagColor { get; set; }
        public bool IsSystem { get; set; }
    }

    /// <summary>
    /// Сервис для управления тегами предметов
    /// </summary>
    public class TagService
    {
        private readonly string _connectionString;

        public TagService()
        {
            // Используем GetConnection, чтобы гарантировать применение PRAGMA foreign_keys = ON
            _connectionString = DatabaseHelper.GetConnection().ConnectionString;
        }

        private string GenerateRandomColor()
        {
            var random = new Random();
            int r = random.Next(60, 220);
            int g = random.Next(60, 220);
            int b = random.Next(60, 220);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        /// <summary>
        /// Получить все теги
        /// </summary>
        public List<ItemTag> GetAllTags()
        {
            var tags = new List<ItemTag>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            var cmd = new SqliteCommand("SELECT id_tag, tag_name, tag_color, is_system FROM Tags ORDER BY tag_name", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tags.Add(new ItemTag
                {
                    Id = Convert.ToInt32(reader["id_tag"]),
                    TagName = reader["tag_name"].ToString(),
                    TagColor = reader["tag_color"].ToString(),
                    IsSystem = Convert.ToInt32(reader["is_system"]) == 1
                });
            }
            return tags;
        }

        /// <summary>
        /// Получить только системные теги
        /// </summary>
        public List<ItemTag> GetSystemTags()
        {
            var tags = new List<ItemTag>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            // ИСПРАВЛЕНО: запрос к таблице Tags, а не ItemTags
            string query = "SELECT id_tag, tag_name, tag_color, is_system FROM Tags WHERE is_system = 1 ORDER BY tag_name";
            using var cmd = new SqliteCommand(query, conn);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                tags.Add(new ItemTag
                {
                    Id = Convert.ToInt32(reader["id_tag"]),
                    TagName = reader["tag_name"].ToString(),
                    TagColor = reader["tag_color"].ToString(),
                    IsSystem = true
                });
            }

            return tags;
        }

        /// <summary>
        /// Создать новый тег
        /// </summary>
        public void CreateTag(string tagName)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            var cmd = new SqliteCommand(
                "INSERT INTO Tags (tag_name, tag_color, is_system) VALUES (@name, @color, 0)", conn);
            cmd.Parameters.AddWithValue("@name", tagName);
            cmd.Parameters.AddWithValue("@color", GenerateRandomColor());
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Удалить тег (только пользовательские)
        /// </summary>
        public void DeleteTag(int tagId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            // Дополнительная защита: проверяем, не системный ли это тег
            var tag = GetAllTags().FirstOrDefault(t => t.Id == tagId);
            if (tag != null && tag.IsSystem)
            {
                throw new InvalidOperationException($"Нельзя удалить системный тег '{tag.TagName}'");
            }

            using var tx = conn.BeginTransaction();
            try
            {
                // 1. Сначала удаляем ВСЕ связи с предметами из ItemTagMapping
                string deleteMappings = "DELETE FROM ItemTagMapping WHERE id_tag = @tagId";
                var cmd1 = new SqliteCommand(deleteMappings, conn, tx);
                cmd1.Parameters.AddWithValue("@tagId", tagId);
                cmd1.ExecuteNonQuery();

                // 2. Теперь удаляем сам тег из таблицы Tags
                string deleteTag = "DELETE FROM Tags WHERE id_tag = @tagId";
                var cmd2 = new SqliteCommand(deleteTag, conn, tx);
                cmd2.Parameters.AddWithValue("@tagId", tagId);
                cmd2.ExecuteNonQuery();

                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                throw new Exception($"Ошибка удаления тега: {ex.Message}");
            }
        }

        /// <summary>
        /// Привязать тег к предмету
        /// </summary>
        public void AssignTagToItem(int itemId, int tagId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = "INSERT OR IGNORE INTO ItemTagMapping (id_item, id_tag) VALUES (@itemId, @tagId)";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@itemId", itemId);
            cmd.Parameters.AddWithValue("@tagId", tagId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Удалить тег с предмета
        /// </summary>
        public void RemoveTagFromItem(int itemId, int tagId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = "DELETE FROM ItemTagMapping WHERE id_item = @itemId AND id_tag = @tagId";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@itemId", itemId);
            cmd.Parameters.AddWithValue("@tagId", tagId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Получить теги предмета
        /// </summary>
        public List<ItemTag> GetItemTags(int itemId)
        {
            var tags = new List<ItemTag>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = @"
                SELECT t.id_tag, t.tag_name, t.tag_color, t.is_system
                FROM ItemTagMapping it 
                JOIN Tags t ON it.id_tag = t.id_tag 
                WHERE it.id_item = @itemId";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@itemId", itemId);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                tags.Add(new ItemTag
                {
                    Id = Convert.ToInt32(reader["id_tag"]),
                    TagName = reader["tag_name"].ToString(),
                    TagColor = reader["tag_color"].ToString(),
                    IsSystem = Convert.ToInt32(reader["is_system"]) == 1
                });
            }

            return tags;
        }

        /// <summary>
        /// Получить предметы по тегу
        /// </summary>
        public DataTable GetItemsByTag(int tagId, int? characterId = null)
        {
            var dt = new DataTable();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = @"
                SELECT i.*
                FROM Inventory i
                INNER JOIN ItemTagMapping m ON i.id_item = m.id_item
                WHERE m.id_tag = @tagId";

            if (characterId.HasValue)
            {
                query += " AND i.id_character = @charId";
            }

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@tagId", tagId);
            if (characterId.HasValue)
            {
                cmd.Parameters.AddWithValue("@charId", characterId.Value);
            }

            using var reader = cmd.ExecuteReader();
            dt.Load(reader);

            return dt;
        }
    }
}