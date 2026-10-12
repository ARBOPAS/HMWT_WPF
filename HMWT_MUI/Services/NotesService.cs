using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HMWT_MUI.Models;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Работа с заметками персонажа: иерархия групп, заметки в Markdown,
    /// картинки, вложения, поиск и wiki-ссылки.
    /// </summary>
    public class NotesService
    {
        private readonly int _characterId;

        public NotesService(int characterId)
        {
            _characterId = characterId;
        }

        public int CharacterId => _characterId;

        #region === ПУТИ ===

        public string GetImagesPath() => NotePaths.GetImagesFolder(_characterId);

        public string GetAttachmentsPath() => NotePaths.GetAttachmentsFolder(_characterId);

        #endregion

        #region === ГРУППЫ ===

        /// <summary>Создаёт группу; parentGroupId — куда вложить (null — корень).</summary>
        public long CreateGroup(string name, long? parentGroupId = null)
        {
            return DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                int sort = GetNextSortOrder(conn, tx, "CharacterNoteGroups", "id_group", parentGroupId);

                using var cmd = new SqliteCommand(@"
                    INSERT INTO CharacterNoteGroups (id_character, name, id_parent, sort_order, created_at, updated_at)
                    VALUES (@charId, @name, @parentId, @sort, datetime('now'), datetime('now'));
                    SELECT last_insert_rowid();", conn, tx);
                cmd.Parameters.AddWithValue("@charId", _characterId);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@parentId", parentGroupId.HasValue ? (object)parentGroupId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@sort", sort);
                return Convert.ToInt64(cmd.ExecuteScalar());
            });
        }

        public void RenameGroup(long groupId, string newName)
        {
            ExecuteNonQuery(@"
                UPDATE CharacterNoteGroups 
                SET name = @name, updated_at = datetime('now')
                WHERE id_group = @id",
                ("@name", newName), ("@id", groupId));
        }

        /// <summary>
        /// Удаляет группу вместе с подгруппами и заметками внутри.
        /// Файлы заметок удаляются, чтобы не оставалось мусора.
        /// </summary>
        public void DeleteGroup(long groupId)
        {
            var groupIds = CollectGroupIds(groupId);
            var noteIds = GetNoteIdsInGroups(groupIds);

            // Сначала чистим файлы, потом записи
            foreach (var noteId in noteIds)
                DeleteNoteFiles(noteId);

            DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                foreach (var noteId in noteIds)
                {
                    using var cmdNote = new SqliteCommand("DELETE FROM CharacterNotes WHERE id_note = @id", conn, tx);
                    cmdNote.Parameters.AddWithValue("@id", noteId);
                    cmdNote.ExecuteNonQuery();
                }

                // Дочерние группы удаляем раньше родителей
                foreach (var id in groupIds.OrderByDescending(g => DepthOf(g, groupIds)))
                {
                    using var cmdGroup = new SqliteCommand("DELETE FROM CharacterNoteGroups WHERE id_group = @id", conn, tx);
                    cmdGroup.Parameters.AddWithValue("@id", id);
                    cmdGroup.ExecuteNonQuery();
                }
            });
        }

        /// <summary>Перемещает заметку в другую группу (null — корень).</summary>
        public void MoveNote(long noteId, long? newGroupId, int sortOrder = 0)
        {
            ExecuteNonQuery(@"
                UPDATE CharacterNotes 
                SET id_group = @groupId, sort_order = @sort, updated_at = datetime('now')
                WHERE id_note = @id",
                ("@groupId", newGroupId.HasValue ? (object)newGroupId.Value : DBNull.Value),
                ("@sort", sortOrder),
                ("@id", noteId));
        }

        /// <summary>Перемещает группу вместе со всем содержимым, защищаясь от циклов.</summary>
        public bool MoveGroup(long groupId, long? newParentId, int sortOrder = 0)
        {
            if (newParentId.HasValue)
            {
                if (newParentId.Value == groupId) return false;
                if (CollectGroupIds(groupId).Contains(newParentId.Value)) return false;
            }

            ExecuteNonQuery(@"
                UPDATE CharacterNoteGroups 
                SET id_parent = @parentId, sort_order = @sort, updated_at = datetime('now')
                WHERE id_group = @id",
                ("@parentId", newParentId.HasValue ? (object)newParentId.Value : DBNull.Value),
                ("@sort", sortOrder),
                ("@id", groupId));
            return true;
        }

        private List<long> CollectGroupIds(long rootId)
        {
            var all = new List<(long Id, long? Parent)>();
            using (var conn = DatabaseHelper.GetConnection())
            {
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT id_group, id_parent FROM CharacterNoteGroups WHERE id_character = @charId", conn);
                cmd.Parameters.AddWithValue("@charId", _characterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    all.Add((reader.GetInt64(0), reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1)));
                }
            }

            var result = new List<long> { rootId };
            var queue = new Queue<long>();
            queue.Enqueue(rootId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var child in all.Where(g => g.Parent == current))
                {
                    if (result.Contains(child.Id)) continue;
                    result.Add(child.Id);
                    queue.Enqueue(child.Id);
                }
            }
            return result;
        }

        private static int DepthOf(long groupId, List<long> within)
        {
            // Для порядка удаления достаточно исходной последовательности обхода (BFS)
            return within.IndexOf(groupId);
        }

        private List<long> GetNoteIdsInGroups(List<long> groupIds)
        {
            var ids = new List<long>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                "SELECT id_note, id_group FROM CharacterNotes WHERE id_character = @charId", conn);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1) && groupIds.Contains(reader.GetInt64(1)))
                    ids.Add(reader.GetInt64(0));
            }
            return ids;
        }

        #endregion

        #region === ЗАМЕТКИ ===

        public long CreateNote(string title, long? groupId = null)
        {
            return DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                int sort = GetNextSortOrder(conn, tx, "CharacterNotes", "id_note", groupId);

                using var cmd = new SqliteCommand(@"
                    INSERT INTO CharacterNotes (id_character, id_group, title, content_md, created_at, updated_at, sort_order)
                    VALUES (@charId, @groupId, @title, '', datetime('now'), datetime('now'), @sort);
                    SELECT last_insert_rowid();", conn, tx);
                cmd.Parameters.AddWithValue("@charId", _characterId);
                cmd.Parameters.AddWithValue("@groupId", groupId.HasValue ? (object)groupId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@sort", sort);
                return Convert.ToInt64(cmd.ExecuteScalar());
            });
        }

        /// <summary>Удаляет заметку вместе с её файлами.</summary>
        public void DeleteNote(long noteId)
        {
            DeleteNoteFiles(noteId);
            ExecuteNonQuery("DELETE FROM CharacterNotes WHERE id_note = @id", ("@id", noteId));
        }

        public void UpdateNoteTitle(long noteId, string newTitle)
        {
            ExecuteNonQuery(@"
                UPDATE CharacterNotes 
                SET title = @title, updated_at = datetime('now')
                WHERE id_note = @id",
                ("@title", newTitle), ("@id", noteId));
        }

        /// <summary>Первый идентификатор заметки с таким заголовком (для wiki-ссылок).</summary>
        public long? FindNoteIdByTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"
                SELECT id_note FROM CharacterNotes 
                WHERE id_character = @charId AND lower(trim(title)) = lower(trim(@title))
                LIMIT 1", conn);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            cmd.Parameters.AddWithValue("@title", title);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? (long?)null : Convert.ToInt64(result);
        }

        public List<string> GetAllNoteTitles()
        {
            var titles = new List<string>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"
                SELECT title FROM CharacterNotes 
                WHERE id_character = @charId 
                ORDER BY title", conn);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                titles.Add(reader.GetString(0));
            return titles;
        }

        #endregion

        #region === СОДЕРЖИМОЕ ===

        /// <summary>Содержимое заметки в Markdown (пустая строка, если пусто).</summary>
        public string GetNoteContent(long noteId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"
                SELECT COALESCE(content_md, '') FROM CharacterNotes WHERE id_note = @id", conn);
            cmd.Parameters.AddWithValue("@id", noteId);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? "" : result.ToString();
        }

        public void SaveNoteContent(long noteId, string markdown)
        {
            ExecuteNonQuery(@"
                UPDATE CharacterNotes 
                SET content_md = @content, updated_at = datetime('now')
                WHERE id_note = @id",
                ("@content", markdown ?? ""), ("@id", noteId));
        }

        /// <summary>Совместимость со старым API (HTML-колонка).</summary>
        public string GetNoteContentHtml(long noteId) => GetNoteContent(noteId);

        /// <summary>Совместимость со старым API (HTML-колонка).</summary>
        public void SaveNoteContentHtml(long noteId, string content) => SaveNoteContent(noteId, content);

        #endregion

        #region === ДЕРЕВО И ПОИСК ===

        /// <summary>Строит дерево групп и заметок.</summary>
        public ObservableCollection<NoteTreeItem> GetTree()
        {
            var groups = new List<NoteTreeItem>();
            var groupParents = new Dictionary<long, long?>();
            var root = new ObservableCollection<NoteTreeItem>();

            using (var conn = DatabaseHelper.GetConnection())
            {
                conn.Open();

                using (var cmd = new SqliteCommand(@"
                    SELECT id_group, name, id_parent, COALESCE(sort_order, 0)
                    FROM CharacterNoteGroups 
                    WHERE id_character = @charId", conn))
                {
                    cmd.Parameters.AddWithValue("@charId", _characterId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var id = reader.GetInt64(0);
                        groupParents[id] = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                        groups.Add(new NoteTreeItem
                        {
                            Id = id,
                            Title = reader.GetString(1),
                            ParentId = groupParents[id],
                            SortOrder = reader.GetInt32(3),
                            IsGroup = true
                        });
                    }
                }

                // Заметки + признак вложений
                var withAttachments = new HashSet<long>();
                using (var cmdAtt = new SqliteCommand(
                    "SELECT DISTINCT id_note FROM NoteAttachments", conn))
                {
                    using var reader = cmdAtt.ExecuteReader();
                    while (reader.Read()) withAttachments.Add(reader.GetInt64(0));
                }

                var notes = new List<NoteTreeItem>();
                using (var cmd = new SqliteCommand(@"
                    SELECT id_note, id_group, title, COALESCE(sort_order, 0)
                    FROM CharacterNotes 
                    WHERE id_character = @charId", conn))
                {
                    cmd.Parameters.AddWithValue("@charId", _characterId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var id = reader.GetInt64(0);
                        notes.Add(new NoteTreeItem
                        {
                            Id = id,
                            ParentId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1),
                            Title = reader.GetString(2),
                            SortOrder = reader.GetInt32(3),
                            IsGroup = false,
                            HasAttachment = withAttachments.Contains(id)
                        });
                    }
                }

                // Собираем иерархию
                var byId = groups.ToDictionary(g => g.Id);
                foreach (var group in groups)
                {
                    if (group.ParentId.HasValue && byId.TryGetValue(group.ParentId.Value, out var parent) &&
                        !ReferenceEquals(parent, group))
                        parent.Children.Add(group);
                    else
                        root.Add(group);
                }

                foreach (var note in notes)
                {
                    if (note.ParentId.HasValue && byId.TryGetValue(note.ParentId.Value, out var parent))
                        parent.Children.Add(note);
                    else
                        root.Add(note);
                }

                SortChildren(root);
            }

            return root;
        }

        private static void SortChildren(ObservableCollection<NoteTreeItem> items)
        {
            var ordered = items
                .OrderBy(i => i.IsGroup ? 0 : 1)
                .ThenBy(i => i.SortOrder)
                .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            items.Clear();
            foreach (var item in ordered)
            {
                SortChildren(item.Children);
                items.Add(item);
            }
        }

        /// <summary>
        /// Поиск по заголовкам и тексту заметок. Возвращает множество
        /// идентификаторов подходящих заметок и их групп-родителей.
        /// </summary>
        public HashSet<long> SearchNotes(string query, out HashSet<long> matchedGroups)
        {
            matchedGroups = new HashSet<long>();
            var matchedNotes = new HashSet<long>();

            if (string.IsNullOrWhiteSpace(query)) return matchedNotes;
            var needle = query.Trim().ToLowerInvariant();

            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            var noteGroup = new Dictionary<long, long?>();
            var groupParent = new Dictionary<long, long?>();

            using (var cmd = new SqliteCommand(@"
                SELECT id_note, id_group, title, lower(COALESCE(content_md, ''))
                FROM CharacterNotes WHERE id_character = @charId", conn))
            {
                cmd.Parameters.AddWithValue("@charId", _characterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    noteGroup[id] = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                    var title = reader.GetString(2).ToLowerInvariant();
                    var content = reader.GetString(3);

                    if (title.Contains(needle) || content.Contains(needle))
                        matchedNotes.Add(id);
                }
            }

            using (var cmd = new SqliteCommand(@"
                SELECT id_group, name, id_parent FROM CharacterNoteGroups WHERE id_character = @charId", conn))
            {
                cmd.Parameters.AddWithValue("@charId", _characterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    groupParent[id] = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                    if (reader.GetString(1).ToLowerInvariant().Contains(needle))
                        matchedGroups.Add(id);
                }
            }

            // Поднимаем совпадения по дереву, чтобы найти путь к ним
            foreach (var noteId in matchedNotes)
            {
                var parent = noteGroup.TryGetValue(noteId, out var g) ? g : null;
                int guard = 0;
                while (parent.HasValue && guard++ < 64)
                {
                    if (!matchedGroups.Add(parent.Value)) break;
                    parent = groupParent.TryGetValue(parent.Value, out var p) ? p : null;
                }
            }

            // Дочерние подгруппы найденных групп тоже показываем
            foreach (var groupId in matchedGroups.ToList())
            {
                foreach (var child in groupParent.Where(kv => kv.Value == groupId).Select(kv => kv.Key))
                    matchedGroups.Add(child);
            }

            return matchedNotes;
        }

        #endregion

        #region === ВЛОЖЕНИЯ ===

        public long AddAttachment(long noteId, string originalName, string storedName)
        {
            return DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                using var cmd = new SqliteCommand(@"
                    INSERT INTO NoteAttachments (id_note, original_name, stored_name)
                    VALUES (@noteId, @originalName, @storedName);
                    SELECT last_insert_rowid();", conn, tx);
                cmd.Parameters.AddWithValue("@noteId", noteId);
                cmd.Parameters.AddWithValue("@originalName", originalName);
                cmd.Parameters.AddWithValue("@storedName", storedName);
                return Convert.ToInt64(cmd.ExecuteScalar());
            });
        }

        public List<(long Id, string OriginalName, string StoredName)> GetAttachments(long noteId)
        {
            var list = new List<(long, string, string)>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"
                SELECT id_attachment, original_name, stored_name
                FROM NoteAttachments WHERE id_note = @noteId", conn);
            cmd.Parameters.AddWithValue("@noteId", noteId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            return list;
        }

        public void DeleteAttachment(long attachmentId, string storedName)
        {
            ExecuteNonQuery("DELETE FROM NoteAttachments WHERE id_attachment = @id", ("@id", attachmentId));

            try
            {
                var path = Path.Combine(GetAttachmentsPath(), storedName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesService] Не удалось удалить файл вложения: {ex.Message}");
            }
        }

        #endregion

        #region === ВСПОМОГАТЕЛЬНОЕ ===

        private void DeleteNoteFiles(long noteId)
        {
            try
            {
                foreach (var attachment in GetAttachments(noteId))
                {
                    var path = Path.Combine(GetAttachmentsPath(), attachment.StoredName);
                    if (File.Exists(path)) File.Delete(path);
                }

                // Картинки, на которые ссылается заметка
                var content = GetNoteContent(noteId);
                foreach (var image in ExtractImagePaths(content))
                {
                    var path = NotePaths.Resolve(image);
                    if (path != null && File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch { /* файл может использоваться — не критично */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesService] Ошибка очистки файлов заметки: {ex.Message}");
            }
        }

        /// <summary>Вытаскивает пути картинок заметки из Markdown.</summary>
        public static List<string> ExtractImagePaths(string markdown)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(markdown)) return result;

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(markdown, @"!\[[^\]]*\]\(([^\)]+)\)"))
            {
                var path = match.Groups[1].Value.Trim().Trim('"', '\'');
                if (path.Length == 0) continue;

                if (path.StartsWith("NoteImages", StringComparison.OrdinalIgnoreCase))
                    result.Add(path);
            }

            return result;
        }

        private static int GetNextSortOrder(SqliteConnection conn, SqliteTransaction tx,
            string table, string idColumn, long? parentId)
        {
            // Колонка родителя называется по-разному: id_parent у групп, id_group у заметок
            var parentColumn = table == "CharacterNoteGroups" ? "id_parent" : "id_group";

            using var cmd = new SqliteCommand(
                $"SELECT COALESCE(MAX(COALESCE(sort_order, 0)), 0) + 1 FROM {table} WHERE {parentColumn} IS @parentId",
                conn, tx);
            cmd.Parameters.AddWithValue("@parentId", parentId.HasValue ? (object)parentId.Value : DBNull.Value);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? 1 : Convert.ToInt32(result);
        }

        private void ExecuteNonQuery(string sql, params (string name, object value)[] parameters)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            foreach (var p in parameters)
                cmd.Parameters.AddWithValue(p.name, p.value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        #endregion
    }
}
