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
    /// Перенос одного персонажа между установками программы.
    ///
    /// Зачем: при обновлении программы база может поменяться, а заново
    /// заполнять лист вручную — больно. Пак .hmwtpack хранит все строки
    /// персонажа (характеристики, навыки, атаки, инвентарь, заклинания,
    /// заметки, деньги, уровни) и картинки. Импорт всегда создаёт НОВОГО
    /// персонажа и не перезаписывает существующих.
    ///
    /// Внутри zip: manifest.json, character.json и images.
    /// Таблицы читаются по именам колонок: если новая версия добавила поле,
    /// оно попадёт в пак; если старая версия его не знает — просто пропустит.
    /// </summary>
    public static class CharacterPackService
    {
        public const int FormatVersion = 1;
        public const string FileExtension = ".hmwtpack";

        private const string ManifestEntry = "manifest.json";
        private const string DataEntry = "character.json";
        private const string ImagesFolder = "images";
        private const string PackImagePrefix = "@pack:";

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions CompactOptions = new JsonSerializerOptions
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

        // Таблицы, у которых есть id_character. Порядок важен для импорта.
        private static readonly string[] CharacterTables =
        {
            "CharacterCharismaSkills", "CharacterConstSkills", "CharacterDexteritySkills",
            "CharacterIntSkills", "CharacterStrenghtSkills", "CharacterWisdomSkills",
            "CharacterIdAddInfo", "GoldTable", "LevelTable", "ListOfCharacters",
            "CharacterConditions", "CharacterSkillExpertise", "CharacterAbilityBonuses",
            "CharacterAttacks", "CharacterSpellSlots", "CharacterSpells",
            "Inventory", "EquipmentGroups", "CharacterNoteGroups", "CharacterNotes"
        };

        public sealed class Manifest
        {
            public int Version { get; set; } = FormatVersion;
            public string Kind { get; set; } = "character";
            public string App { get; set; } = "HMWT Гримуар";
            public string AppVersion { get; set; } = "";
            public string CreatedAt { get; set; } = "";
            public string Name { get; set; } = "";
            public string CharacterName { get; set; } = "";
            public int OldCharacterId { get; set; }
            public string Class { get; set; } = "";
            public string Race { get; set; } = "";
            public int Level { get; set; }
            public int Images { get; set; }
            public List<string> Tables { get; set; } = new List<string>();
        }

        #region === ЭКСПОРТ ===

        public static bool Export(string path, int characterId, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(path)) { error = "Не указан файл."; return false; }
            if (characterId <= 0) { error = "Не выбран персонаж."; return false; }

            AttackService.EnsureTables();

            try
            {
                var tables = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.Ordinal);
                string name;
                string className;
                string race;
                int level;

                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();

                    var core = ReadRows(conn, "SELECT * FROM CharacterInfo WHERE id_character = @id", characterId);
                    if (core.Count == 0) { error = "Персонаж не найден в базе."; return false; }

                    var character = core[0];
                    name = AsString(character.TryGetValue("name", out var n) ? n : null);
                    className = AsString(character.TryGetValue("class", out var c) ? c : null);
                    race = AsString(character.TryGetValue("race", out var r) ? r : null);
                    level = AsInt(character.TryGetValue("MasterLVL", out var l) ? l : null);
                    tables["CharacterInfo"] = core;

                    foreach (string table in CharacterTables)
                        tables[table] = ReadRows(conn, $"SELECT * FROM \"{table}\" WHERE id_character = @id", characterId);

                    tables["EquipmentCells"] = ReadRows(conn,
                        "SELECT ec.* FROM EquipmentCells ec " +
                        "JOIN EquipmentGroups eg ON eg.id_group = ec.id_group " +
                        "WHERE eg.id_character = @id", characterId);

                    tables["ItemTagMapping"] = ReadRows(conn,
                        "SELECT itm.* FROM ItemTagMapping itm " +
                        "JOIN Inventory i ON i.id_item = itm.id_item " +
                        "WHERE i.id_character = @id", characterId);

                    tables["NoteAttachments"] = ReadRows(conn,
                        "SELECT na.* FROM NoteAttachments na " +
                        "JOIN CharacterNotes n ON n.id_note = na.id_note " +
                        "WHERE n.id_character = @id", characterId);

                    // Заклинания: сам справочник нужен, иначе у персонажа будут
                    // ссылки на несуществующие id_spell в новой установке.
                    var spellIds = tables["CharacterSpells"]
                        .Select(row => AsInt(row.TryGetValue("id_spell", out var v) ? v : null))
                        .Where(id => id > 0).Distinct().ToList();

                    tables["SpellReference"] = spellIds.Count == 0
                        ? new List<Dictionary<string, object>>()
                        : ReadRows(conn,
                            "SELECT * FROM SpellReference WHERE id_spell IN (" +
                            string.Join(",", spellIds) + ")", 0);

                    // Метки предметов.
                    var tagIds = tables["ItemTagMapping"]
                        .Select(row => AsInt(row.TryGetValue("id_tag", out var v) ? v : null))
                        .Where(id => id > 0).Distinct().ToList();

                    tables["Tags"] = tagIds.Count == 0
                        ? new List<Dictionary<string, object>>()
                        : ReadRows(conn,
                            "SELECT * FROM Tags WHERE id_tag IN (" +
                            string.Join(",", tagIds) + ")", 0);
                }

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(path)) File.Delete(path);

                int imageCount = 0;

                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    PackImageField(tables, "CharacterInfo", "PhotoPath", zip, ref imageCount);
                    PackImageField(tables, "Inventory", "PhotoPath", zip, ref imageCount);
                    PackImageField(tables, "Inventory", "list_photo_path", zip, ref imageCount);
                    PackImageField(tables, "CharacterSpells", "IndividualImagePath", zip, ref imageCount);
                    PackNoteAttachments(tables, zip, ref imageCount);

                    var manifest = new Manifest
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "Персонаж" : name.Trim(),
                        CharacterName = name,
                        OldCharacterId = characterId,
                        Class = className,
                        Race = race,
                        Level = level,
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        AppVersion = Net.NetVersions.AppVersion,
                        Images = imageCount,
                        Tables = tables.Keys.ToList()
                    };

                    WriteEntry(zip, ManifestEntry, JsonSerializer.Serialize(manifest, WriteOptions));
                    WriteEntry(zip, DataEntry, JsonSerializer.Serialize(tables, CompactOptions));
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPack] экспорт: {ex}");
                error = ex.Message;
                return false;
            }
        }

        #endregion

        #region === ИМПОРТ ===

        public static bool Import(string path, out int newCharacterId, out string characterName, out string error)
        {
            newCharacterId = 0;
            characterName = "";
            error = "";

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "Файл не найден.";
                return false;
            }

            var createdFiles = new List<string>();

            AttackService.EnsureTables();

            try
            {
                using var zip = ZipFile.OpenRead(path);
                var manifestEntry = zip.GetEntry(ManifestEntry);
                var dataEntry = zip.GetEntry(DataEntry);
                if (manifestEntry == null || dataEntry == null)
                {
                    error = "Это не файл персонажа HMWT.";
                    return false;
                }

                Manifest manifest;
                Dictionary<string, List<Dictionary<string, JsonElement>>> data;
                using (var stream = manifestEntry.Open())
                    manifest = JsonSerializer.Deserialize<Manifest>(stream, ReadOptions);
                using (var stream = dataEntry.Open())
                    data = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, JsonElement>>>>(stream, ReadOptions);

                if (manifest == null || !string.Equals(manifest.Kind, "character", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Файл не является паком персонажа.";
                    return false;
                }

                if (data == null || !data.TryGetValue("CharacterInfo", out var coreRows) || coreRows.Count == 0)
                {
                    error = "В паке нет данных персонажа.";
                    return false;
                }

                var core = ToObjectRow(coreRows[0]);

                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                // Картинки и имена файлов подготавливаем вместе с id персонажа
                int newId = InsertNewCharacter(conn, tx, core);
                newCharacterId = newId;

                string photoPack = core.TryGetValue("PhotoPath", out var photoValue)
                    ? photoValue?.ToString() ?? "" : "";
                string newPhotoPath = "";
                if (!string.IsNullOrWhiteSpace(photoPack))
                    RestoreImage(zip, photoPack, "Characters/" + newId, "Avatar", createdFiles,
                        out newPhotoPath, out string imageError);

                if (!string.IsNullOrWhiteSpace(newPhotoPath))
                {
                    using var update = new SqliteCommand(
                        "UPDATE CharacterInfo SET PhotoPath = @p WHERE id_character = @id", conn, tx);
                    update.Parameters.AddWithValue("@p", newPhotoPath);
                    update.Parameters.AddWithValue("@id", newId);
                    update.ExecuteNonQuery();
                }

                // Таблицы, которые просто копируются с новым id_character
                foreach (string table in new[]
                {
                    "CharacterCharismaSkills", "CharacterConstSkills", "CharacterDexteritySkills",
                    "CharacterIntSkills", "CharacterStrenghtSkills", "CharacterWisdomSkills",
                    "CharacterIdAddInfo", "GoldTable", "LevelTable", "CharacterConditions",
                    "CharacterSkillExpertise", "CharacterAbilityBonuses"
                })
                {
                    if (!data.TryGetValue(table, out var rows)) continue;
                    var skipAuto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (table == "CharacterAbilityBonuses") skipAuto.Add("id");
                    foreach (var jsonRow in rows)
                    {
                        var row = ToObjectRow(jsonRow);
                        row["id_character"] = newId;
                        InsertRow(conn, tx, table, row, skipAuto);
                    }
                }

                // Слоты заклинаний: у них свой автоинкрементный id_slot
                if (data.TryGetValue("CharacterSpellSlots", out var slotRows))
                {
                    foreach (var jsonRow in slotRows)
                    {
                        var row = ToObjectRow(jsonRow);
                        row.Remove("id_slot");
                        row["id_character"] = newId;
                        InsertRow(conn, tx, "CharacterSpellSlots", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_slot" });
                    }
                }

                // Порядок персонажа: новый номер, чтобы не конфликтовать со старым
                if (data.TryGetValue("ListOfCharacters", out var listRows) && listRows.Count > 0)
                {
                    var row = ToObjectRow(listRows[0]);
                    row["id_character"] = newId;
                    row["NumberOfCharacter"] = NextCharacterNumber(conn, tx);
                    InsertRow(conn, tx, "ListOfCharacters", row, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                // Инвентарь: новые id_item нужны экипировке и меткам
                var itemMap = new Dictionary<int, int>();
                if (data.TryGetValue("Inventory", out var inventoryRows))
                {
                    foreach (var jsonRow in inventoryRows)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldId = AsInt(row.TryGetValue("id_item", out var itemId) ? itemId : null);
                        row.Remove("id_item");
                        row["id_character"] = newId;
                        RestoreRowImages(zip, row, "PhotoPath", "Items", "item", createdFiles);
                        RestoreRowImages(zip, row, "list_photo_path", "Items", "item_list", createdFiles);

                        int inserted = (int)InsertRow(conn, tx, "Inventory", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_item" });
                        if (oldId > 0 && inserted > 0) itemMap[oldId] = inserted;
                    }
                }

                // Метки
                var tagMap = new Dictionary<int, int>();
                if (data.TryGetValue("Tags", out var tagRows))
                {
                    foreach (var jsonRow in tagRows)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldId = AsInt(row.TryGetValue("id_tag", out var tagId) ? tagId : null);
                        string tagName = AsString(row.TryGetValue("tag_name", out var value) ? value : null);

                        int existing = FindTag(conn, tx, tagName);
                        if (existing > 0)
                        {
                            if (oldId > 0) tagMap[oldId] = existing;
                            continue;
                        }

                        row.Remove("id_tag");
                        int inserted = (int)InsertRow(conn, tx, "Tags", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_tag" });
                        if (oldId > 0 && inserted > 0) tagMap[oldId] = inserted;
                    }
                }

                if (data.TryGetValue("ItemTagMapping", out var itemTags))
                {
                    foreach (var jsonRow in itemTags)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldItem = AsInt(row.TryGetValue("id_item", out var i) ? i : null);
                        int oldTag = AsInt(row.TryGetValue("id_tag", out var t) ? t : null);
                        if (!itemMap.TryGetValue(oldItem, out int newItem)) continue;
                        if (!tagMap.TryGetValue(oldTag, out int newTag)) continue;

                        row["id_item"] = newItem;
                        row["id_tag"] = newTag;
                        InsertRow(conn, tx, "ItemTagMapping", row, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                }

                // Экипировка
                var groupMap = new Dictionary<int, int>();
                if (data.TryGetValue("EquipmentGroups", out var groupRows))
                {
                    foreach (var jsonRow in groupRows)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldId = AsInt(row.TryGetValue("id_group", out var groupId) ? groupId : null);
                        row.Remove("id_group");
                        row["id_character"] = newId;

                        int inserted = (int)InsertRow(conn, tx, "EquipmentGroups", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_group" });
                        if (oldId > 0 && inserted > 0) groupMap[oldId] = inserted;
                    }
                }

                if (data.TryGetValue("EquipmentCells", out var cellRows))
                {
                    foreach (var jsonRow in cellRows)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldGroup = AsInt(row.TryGetValue("id_group", out var g) ? g : null);
                        int oldItem = AsInt(row.TryGetValue("id_item", out var i) ? i : null);
                        if (!groupMap.TryGetValue(oldGroup, out int newGroup)) continue;
                        if (!itemMap.TryGetValue(oldItem, out int newItem)) continue;

                        row.Remove("id_cell");
                        row["id_group"] = newGroup;
                        row["id_item"] = newItem;
                        InsertRow(conn, tx, "EquipmentCells", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_cell" });
                    }
                }

                // Атаки: сначала новые id, потом child_ids
                var attackMap = new Dictionary<int, int>();
                var attackChildren = new List<KeyValuePair<int, string>>();
                if (data.TryGetValue("CharacterAttacks", out var attackRows))
                {
                    foreach (var jsonRow in attackRows)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldId = AsInt(row.TryGetValue("id_attack", out var attackId) ? attackId : null);
                        string children = AsString(row.TryGetValue("child_ids", out var childValue) ? childValue : null);
                        row.Remove("id_attack");
                        row["id_character"] = newId;

                        int inserted = (int)InsertRow(conn, tx, "CharacterAttacks", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_attack" });
                        if (oldId > 0 && inserted > 0)
                        {
                            attackMap[oldId] = inserted;
                            if (!string.IsNullOrWhiteSpace(children))
                                attackChildren.Add(new KeyValuePair<int, string>(inserted, children));
                        }
                    }

                    foreach (var pair in attackChildren)
                    {
                        var mapped = MapIdList(pair.Value, attackMap);
                        if (string.IsNullOrWhiteSpace(mapped)) continue;
                        using var update = new SqliteCommand(
                            "UPDATE CharacterAttacks SET child_ids = @c WHERE id_attack = @id", conn, tx);
                        update.Parameters.AddWithValue("@c", mapped);
                        update.Parameters.AddWithValue("@id", pair.Key);
                        update.ExecuteNonQuery();
                    }
                }

                // Заклинания: ссылки на существующие по имени/уровню, иначе своя копия
                var spellMap = new Dictionary<int, int>();
                if (data.TryGetValue("SpellReference", out var spellRefs))
                {
                    foreach (var jsonRow in spellRefs)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldId = AsInt(row.TryGetValue("id_spell", out var spellId) ? spellId : null);
                        string name = AsString(row.TryGetValue("name", out var n) ? n : null);
                        int level = AsInt(row.TryGetValue("level", out var l) ? l : null);

                        int existing = FindSpell(conn, tx, name, level);
                        if (existing > 0)
                        {
                            if (oldId > 0) spellMap[oldId] = existing;
                            continue;
                        }

                        row.Remove("id_spell");
                        if (row.ContainsKey("is_modified")) row["is_modified"] = 1;
                        int inserted = (int)InsertRow(conn, tx, "SpellReference", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_spell" });
                        if (oldId > 0 && inserted > 0) spellMap[oldId] = inserted;
                    }
                }

                if (data.TryGetValue("CharacterSpells", out var charSpells))
                {
                    foreach (var jsonRow in charSpells)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldSpell = AsInt(row.TryGetValue("id_spell", out var s) ? s : null);
                        if (!spellMap.TryGetValue(oldSpell, out int newSpell)) continue;

                        row.Remove("id_char_spell");
                        row["id_character"] = newId;
                        row["id_spell"] = newSpell;
                        RestoreRowImages(zip, row, "IndividualImagePath", "Spells", "spell", createdFiles);
                        InsertRow(conn, tx, "CharacterSpells", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_char_spell" });
                    }
                }

                // Заметки: группы, затем заметки, затем вложения
                var groupMapNotes = new Dictionary<int, int>();
                if (data.TryGetValue("CharacterNoteGroups", out var noteGroups))
                {
                    foreach (var jsonRow in noteGroups)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldId = AsInt(row.TryGetValue("id_group", out var groupId) ? groupId : null);
                        row.Remove("id_group");
                        row["id_character"] = newId;
                        row["id_parent"] = null;

                        int inserted = (int)InsertRow(conn, tx, "CharacterNoteGroups", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_group" });
                        if (oldId > 0 && inserted > 0) groupMapNotes[oldId] = inserted;
                    }
                }

                var noteMap = new Dictionary<int, int>();
                if (data.TryGetValue("CharacterNotes", out var noteRows))
                {
                    foreach (var jsonRow in noteRows)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldId = AsInt(row.TryGetValue("id_note", out var noteId) ? noteId : null);
                        int oldGroup = AsInt(row.TryGetValue("id_group", out var groupId) ? groupId : null);
                        row.Remove("id_note");
                        row["id_character"] = newId;
                        row["id_group"] = oldGroup > 0 && groupMapNotes.TryGetValue(oldGroup, out int newGroup)
                            ? (object)newGroup : null;

                        int inserted = (int)InsertRow(conn, tx, "CharacterNotes", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_note" });
                        if (oldId > 0 && inserted > 0) noteMap[oldId] = inserted;
                    }
                }

                if (data.TryGetValue("NoteAttachments", out var attachments))
                {
                    foreach (var jsonRow in attachments)
                    {
                        var row = ToObjectRow(jsonRow);
                        int oldNote = AsInt(row.TryGetValue("id_note", out var noteId) ? noteId : null);
                        if (!noteMap.TryGetValue(oldNote, out int newNote)) continue;

                        string stored = AsString(row.TryGetValue("stored_name", out var storedValue) ? storedValue : null);
                        string original = AsString(row.TryGetValue("__stored_name", out var originalValue) ? originalValue : null);
                        if (string.IsNullOrWhiteSpace(original)) original = stored;
                        string preferred = Path.GetFileNameWithoutExtension(original);
                        if (string.IsNullOrWhiteSpace(preferred)) preferred = "file";

                        RestoreImage(zip, stored, "NoteAttachments/" + newId, preferred,
                            createdFiles, out string newRelative, out string imageError);

                        row.Remove("id_attachment");
                        row["id_note"] = newNote;
                        row["stored_name"] = Path.GetFileName(newRelative);
                        InsertRow(conn, tx, "NoteAttachments", row,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_attachment", "__stored_name" });
                    }
                }

                // Родительские группы заметок
                if (data.TryGetValue("CharacterNoteGroups", out var parentGroups))
                {
                    foreach (var jsonRow in parentGroups)
                    {
                        int oldId = AsInt(jsonRow.TryGetValue("id_group", out var idValue) ? ToObject(idValue) : null);
                        int oldParent = AsInt(jsonRow.TryGetValue("id_parent", out var parentValue) ? ToObject(parentValue) : null);
                        if (!groupMapNotes.TryGetValue(oldId, out int newIdValue)) continue;
                        if (!groupMapNotes.TryGetValue(oldParent, out int newParent)) continue;

                        using var update = new SqliteCommand(
                            "UPDATE CharacterNoteGroups SET id_parent = @p WHERE id_group = @id", conn, tx);
                        update.Parameters.AddWithValue("@p", newParent);
                        update.Parameters.AddWithValue("@id", newIdValue);
                        update.ExecuteNonQuery();
                    }
                }

                // Ссылки на старые вложения в текстах заметок
                if (manifest.OldCharacterId > 0)
                {
                    using var update = new SqliteCommand(
                        "UPDATE CharacterNotes SET " +
                        "content_md = REPLACE(content_md, 'NoteAttachments/' || @old || '/', 'NoteAttachments/' || @new || '/'), " +
                        "content_xaml = REPLACE(content_xaml, 'NoteAttachments/' || @old || '/', 'NoteAttachments/' || @new || '/'), " +
                        "content_html = REPLACE(content_html, 'NoteAttachments/' || @old || '/', 'NoteAttachments/' || @new || '/') " +
                        "WHERE id_character = @id", conn, tx);
                    update.Parameters.AddWithValue("@old", manifest.OldCharacterId);
                    update.Parameters.AddWithValue("@new", newId);
                    update.Parameters.AddWithValue("@id", newId);
                    update.ExecuteNonQuery();
                }

                tx.Commit();
                characterName = AsString(core.TryGetValue("name", out var nameValue) ? nameValue : null);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPack] импорт: {ex}");
                foreach (string file in createdFiles)
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                }
                error = ex.Message;
                return false;
            }
        }

        #endregion

        #region === БАЗА ===

        private static List<Dictionary<string, object>> ReadRows(SqliteConnection conn, string sql, int param)
        {
            var result = new List<Dictionary<string, object>>();
            using var cmd = new SqliteCommand(sql, conn);
            if (param > 0) cmd.Parameters.AddWithValue("@id", param);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object>(StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    object value = reader.GetValue(i);
                    row[reader.GetName(i)] = value is DBNull ? null : value;
                }
                result.Add(row);
            }
            return result;
        }

        private static Dictionary<string, object> ToObjectRow(Dictionary<string, JsonElement> json)
        {
            var row = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in json)
                row[pair.Key] = ToObject(pair.Value);
            return row;
        }

        private static object ToObject(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.True:
                    return 1L;
                case JsonValueKind.False:
                    return 0L;
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out long l)) return l;
                    if (value.TryGetDouble(out double d)) return d;
                    return value.GetRawText();
                default:
                    return value.GetRawText();
            }
        }

        private static int InsertNewCharacter(SqliteConnection conn, SqliteTransaction tx, Dictionary<string, object> core)
        {
            string name = AsString(core.TryGetValue("name", out var n) ? n : null);
            string unique = MakeUniqueName(conn, tx, name);
            core["name"] = unique;

            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id_character", "PhotoPath" };
            return (int)InsertRow(conn, tx, "CharacterInfo", core, skip);
        }

        private static string MakeUniqueName(SqliteConnection conn, SqliteTransaction tx, string name)
        {
            string baseName = string.IsNullOrWhiteSpace(name) ? "Импортированный персонаж" : name.Trim();
            string candidate = baseName;
            for (int i = 2; i < 500; i++)
            {
                using var cmd = new SqliteCommand(
                    "SELECT 1 FROM CharacterInfo WHERE lower(name) = lower(@name) LIMIT 1", conn, tx);
                cmd.Parameters.AddWithValue("@name", candidate);
                if (cmd.ExecuteScalar() == null) return candidate;
                candidate = baseName + " (" + i + ")";
            }
            return baseName + " (" + DateTime.Now.ToString("HHmmss") + ")";
        }

        private static int NextCharacterNumber(SqliteConnection conn, SqliteTransaction tx)
        {
            using var cmd = new SqliteCommand(
                "SELECT COALESCE(MAX(NumberOfCharacter), 0) + 1 FROM ListOfCharacters", conn, tx);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 1);
        }

        private static long InsertRow(SqliteConnection conn, SqliteTransaction tx, string table,
            Dictionary<string, object> row, HashSet<string> skip)
        {
            var columns = GetColumns(conn, tx, table);
            var names = row.Keys
                .Where(k => !k.StartsWith("__", StringComparison.Ordinal) &&
                            columns.Contains(k) && !skip.Contains(k))
                .ToList();

            if (names.Count == 0)
                throw new InvalidOperationException($"В таблице {table} нечего сохранять.");

            string sql = "INSERT INTO \"" + table + "\" (" +
                string.Join(",", names.Select(Quote)) + ") VALUES (" +
                string.Join(",", names.Select((_, i) => "@p" + i)) + ")";

            using var cmd = new SqliteCommand(sql, conn, tx);
            for (int i = 0; i < names.Count; i++)
                cmd.Parameters.AddWithValue("@p" + i, row[names[i]] ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn, tx);
            return Convert.ToInt64(idCmd.ExecuteScalar() ?? 0);
        }

        private static HashSet<string> GetColumns(SqliteConnection conn, SqliteTransaction tx, string table)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = new SqliteCommand("PRAGMA table_info(\"" + table + "\")", conn, tx);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
            return columns;
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private static int FindTag(SqliteConnection conn, SqliteTransaction tx, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            using var cmd = new SqliteCommand("SELECT id_tag FROM Tags WHERE lower(tag_name) = lower(@n) LIMIT 1", conn, tx);
            cmd.Parameters.AddWithValue("@n", name);
            var value = cmd.ExecuteScalar();
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static int FindSpell(SqliteConnection conn, SqliteTransaction tx, string name, int level)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            using var cmd = new SqliteCommand(
                "SELECT id_spell FROM SpellReference WHERE lower(name) = lower(@n) AND level = @l LIMIT 1", conn, tx);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@l", level);
            var value = cmd.ExecuteScalar();
            return value == null ? 0 : Convert.ToInt32(value);
        }

        #endregion

        #region === КАРТИНКИ ===

        private static void PackImageField(Dictionary<string, List<Dictionary<string, object>>> tables,
            string table, string field, ZipArchive zip, ref int count)
        {
            if (!tables.TryGetValue(table, out var rows)) return;

            foreach (var row in rows)
            {
                if (!row.TryGetValue(field, out var value)) continue;
                string text = value as string;
                if (string.IsNullOrWhiteSpace(text) || text.StartsWith(PackImagePrefix, StringComparison.Ordinal)) continue;

                string full = PhotoHelper.ResolvePhotoPath(text);
                if (full == null || !File.Exists(full))
                {
                    row[field] = "";
                    continue;
                }

                string entry = AddImageEntry(zip, full, ref count);
                row[field] = string.IsNullOrWhiteSpace(entry) ? "" : PackImagePrefix + entry;
            }
        }

        private static void PackNoteAttachments(Dictionary<string, List<Dictionary<string, object>>> tables,
            ZipArchive zip, ref int count)
        {
            if (!tables.TryGetValue("NoteAttachments", out var rows)) return;

            foreach (var row in rows)
            {
                string stored = AsString(row.TryGetValue("stored_name", out var value) ? value : null);
                if (string.IsNullOrWhiteSpace(stored)) continue;

                int characterId = 0;
                if (tables.TryGetValue("CharacterInfo", out var core) && core.Count > 0)
                    characterId = AsInt(core[0].TryGetValue("id_character", out var idValue) ? idValue : null);

                string relative = "NoteAttachments/" + characterId + "/" + stored;
                string full = PhotoHelper.ResolvePhotoPath(relative);
                if (full == null || !File.Exists(full))
                {
                    row["stored_name"] = "";
                    continue;
                }

                string entry = AddImageEntry(zip, full, ref count);
                row["stored_name"] = string.IsNullOrWhiteSpace(entry) ? "" : PackImagePrefix + entry;
                row["__stored_name"] = stored;
            }
        }

        private static string AddImageEntry(ZipArchive zip, string fullPath, ref int count)
        {
            try
            {
                string extension = Path.GetExtension(fullPath);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8) extension = ".img";
                extension = extension.ToLowerInvariant().Replace("/", "").Replace("\\", "");

                count++;
                string entryName = ImagesFolder + "/img" + count.ToString("0000") + extension;
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var target = entry.Open();
                using var source = File.OpenRead(fullPath);
                source.CopyTo(target);
                return entryName;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPack] картинка {fullPath}: {ex.Message}");
                return "";
            }
        }

        private static void RestoreRowImages(ZipArchive zip, Dictionary<string, object> row, string field,
            string folder, string preferredName, List<string> createdFiles)
        {
            if (!row.TryGetValue(field, out var value)) return;
            string pack = value as string;
            if (string.IsNullOrWhiteSpace(pack) || !pack.StartsWith(PackImagePrefix, StringComparison.Ordinal)) return;

            RestoreImage(zip, pack, folder, preferredName, createdFiles, out string newPath, out _);
            row[field] = newPath;
        }

        private static bool RestoreImage(ZipArchive zip, string packValue, string folder, string preferredName,
            List<string> createdFiles, out string relativePath, out string error)
        {
            relativePath = "";
            error = "";
            if (string.IsNullOrWhiteSpace(packValue) || !packValue.StartsWith(PackImagePrefix, StringComparison.Ordinal))
            {
                relativePath = packValue ?? "";
                return false;
            }

            string entryName = packValue.Substring(PackImagePrefix.Length);
            if (!entryName.StartsWith(ImagesFolder + "/", StringComparison.OrdinalIgnoreCase) ||
                entryName.Contains(".."))
            {
                error = "Некорректный путь картинки в паке.";
                return false;
            }

            var entry = zip.GetEntry(entryName);
            if (entry == null)
            {
                error = "В паке нет картинки " + entryName;
                return false;
            }

            try
            {
                string extension = Path.GetExtension(entryName);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".img";

                string safeName = string.IsNullOrWhiteSpace(preferredName) ? "image" : preferredName;
                foreach (char c in Path.GetInvalidFileNameChars())
                    safeName = safeName.Replace(c, '_');
                safeName = safeName.Trim();
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "image";

                string targetDir = Path.Combine(PhotoHelper.GetWritableRoot(), folder.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(targetDir);

                string fileName = safeName + extension.ToLowerInvariant();
                string full = Path.Combine(targetDir, fileName);
                for (int i = 2; File.Exists(full); i++)
                {
                    fileName = safeName + "_" + i + extension.ToLowerInvariant();
                    full = Path.Combine(targetDir, fileName);
                }

                using (var source = entry.Open())
                using (var target = File.Create(full))
                    source.CopyTo(target);

                createdFiles.Add(full);
                relativePath = folder.TrimEnd('/') + "/" + fileName;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[CharacterPack] восстановление картинки: {ex}");
                return false;
            }
        }

        #endregion

        #region === ВСПОМОГАТЕЛЬНОЕ ===

        private static void WriteEntry(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
            writer.Write(text);
        }

        private static string MapIdList(string csv, Dictionary<int, int> map)
        {
            if (string.IsNullOrWhiteSpace(csv)) return "";
            var result = new List<string>();
            foreach (string part in csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), out int id)) continue;
                if (map.TryGetValue(id, out int mapped)) result.Add(mapped.ToString());
            }
            return string.Join(",", result);
        }

        private static string AsString(object value) => value?.ToString() ?? "";

        private static int AsInt(object value)
        {
            if (value == null) return 0;
            if (value is long l) return (int)l;
            if (value is int i) return i;
            if (value is double d) return (int)d;
            return int.TryParse(value.ToString(), out int parsed) ? parsed : 0;
        }

        #endregion
    }
}
