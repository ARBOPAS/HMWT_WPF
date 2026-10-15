using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Модель группы экипировки
    /// </summary>
    public class EquipmentGroup
    {
        public int Id { get; set; }
        public int CharacterId { get; set; }
        public int SlotId { get; set; }
        public string SlotName { get; set; }
        public string SlotCode { get; set; }
        public string GroupName { get; set; }
        public double PositionX { get; set; }
        public double PositionY { get; set; }
        public double RotationAngle { get; set; }
        public int CellCount { get; set; }
        public bool IsExpanded { get; set; }

        public double GroupSize { get; set; } = 100;
        public List<EquipmentCell> Cells { get; set; } = new List<EquipmentCell>();
    }

    /// <summary>
    /// Модель ячейки в группе
    /// </summary>
    public class EquipmentCell
    {
        public int Id { get; set; }
        public int GroupId { get; set; }
        public int? ItemId { get; set; }
        public int CellIndex { get; set; }
        public bool IsLocked { get; set; }
        public string ItemName { get; set; }
        public string ItemPhotoPath { get; set; }
        public int ArmorClassBonus { get; set; }
        public string DamageDice { get; set; }
    }

    /// <summary>
    /// Модель слота экипировки
    /// </summary>
    public class EquipmentSlot
    {
        public int Id { get; set; }
        public string SlotName { get; set; }
        public string SlotCode { get; set; }
        public string IconPath { get; set; }
        public int SortOrder { get; set; }
    }




    /// <summary>
    /// Сервис для управления экипировкой персонажа
    /// </summary>
    public class EquipmentService
    {
        private readonly int _characterId;

        public EquipmentService(int characterId)
        {
            _characterId = characterId;
            // Строку подключения здесь больше не «одалживаем» у GetConnection().
            //
            // Раньше сервис брал строку подключения и открывал собственные
            // SqliteConnection — без PRAGMA foreign_keys=ON и busy_timeout,
            // которые GetConnection() навешивает на каждое соединение. То есть
            // работал без контроля внешних ключей и без ожидания блокировки.
            // Теперь все соединения идут через DatabaseHelper.
        }

        /// <summary>
        /// Получить все слоты экипировки
        /// </summary>
        public List<EquipmentSlot> GetAllSlots()
        {
            var slots = new List<EquipmentSlot>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = "SELECT id_slot, slot_name, slot_code, icon_path, sort_order FROM EquipmentSlots ORDER BY sort_order";
            using var cmd = new SqliteCommand(query, conn);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                slots.Add(new EquipmentSlot
                {
                    Id = Convert.ToInt32(reader["id_slot"]),
                    SlotName = reader["slot_name"].ToString(),
                    SlotCode = reader["slot_code"].ToString(),
                    IconPath = reader["icon_path"]?.ToString(),
                    SortOrder = Convert.ToInt32(reader["sort_order"])
                });
            }

            return slots;
        }

        /// <summary>
        /// Получить все группы экипировки персонажа
        /// </summary>
        public List<EquipmentGroup> GetCharacterGroups()
        {
            var groups = new List<EquipmentGroup>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = @"
        SELECT g.id_group, g.id_slot, s.slot_name, COALESCE(g.group_name, '') AS group_name,
               g.position_x, g.position_y, 
               g.is_expanded, COALESCE(g.group_size, 100) as group_size
        FROM EquipmentGroups g
        LEFT JOIN EquipmentSlots s ON g.id_slot = s.id_slot
        WHERE g.id_character = @charId
        ORDER BY g.id_group";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var group = new EquipmentGroup
                {
                    Id = Convert.ToInt32(reader["id_group"]),
                    SlotId = Convert.ToInt32(reader["id_slot"]),
                    SlotName = reader["slot_name"]?.ToString() ?? $"Слот {reader["id_slot"]}",
                    GroupName = reader["group_name"]?.ToString() ?? "",
                    PositionX = Convert.ToDouble(reader["position_x"]),
                    PositionY = Convert.ToDouble(reader["position_y"]),
                    IsExpanded = Convert.ToInt32(reader["is_expanded"]) == 1,
                    GroupSize = Convert.ToDouble(reader["group_size"])
                };

                // Загружаем ячейки
                group.Cells = GetGroupCells(group.Id);
                groups.Add(group);
            }

            return groups;
        }

        /// <summary>
        /// Получить ячейки группы
        /// </summary>
        private List<EquipmentCell> GetGroupCells(int groupId)
        {
            var cells = new List<EquipmentCell>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = @"
        SELECT c.id_cell, c.id_group, c.id_item, c.cell_index,
               i.name as item_name, i.PhotoPath as item_photo, i.armor_class_bonus, i.damage_dice
        FROM EquipmentCells c
        LEFT JOIN Inventory i ON c.id_item = i.id_item
        WHERE c.id_group = @groupId
        ORDER BY c.cell_index";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@groupId", groupId);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var cell = new EquipmentCell
                {
                    Id = Convert.ToInt32(reader["id_cell"]),
                    GroupId = Convert.ToInt32(reader["id_group"]),
                    ItemId = reader["id_item"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["id_item"]),
                    CellIndex = Convert.ToInt32(reader["cell_index"]),
                    ItemName = reader["item_name"]?.ToString(),
                    ItemPhotoPath = reader["item_photo"]?.ToString(),
                    ArmorClassBonus = reader["armor_class_bonus"] == DBNull.Value ? 0 : Convert.ToInt32(reader["armor_class_bonus"]),
                    DamageDice = reader["damage_dice"]?.ToString()
                };
                cells.Add(cell);
            }

            return cells;
        }

        /// <summary>
        /// Создать новую группу экипировки
        /// </summary>
        public int CreateGroup(int slotId, double positionX, double positionY, string groupName = null)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = @"
                INSERT INTO EquipmentGroups (id_character, id_slot, group_name, position_x, position_y, cell_count)
                VALUES (@charId, @slotId, @groupName, @posX, @posY, 1);
                SELECT last_insert_rowid();";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            cmd.Parameters.AddWithValue("@slotId", slotId);
            cmd.Parameters.AddWithValue("@groupName", groupName ?? "");
            cmd.Parameters.AddWithValue("@posX", positionX);
            cmd.Parameters.AddWithValue("@posY", positionY);

            int groupId = Convert.ToInt32(cmd.ExecuteScalar());

            // Создаём первую ячейку
            CreateCell(groupId, 0);

            return groupId;
        }

        /// <summary>
        /// Удалить группу вместе с экипировкой: предметы из её ячеек сначала
        /// снимаются (в Inventory сбрасывается is_equipped), потом удаляются
        /// ячейки и сама группа. Одной транзакцией, чтобы не осталось «фантомной»
        /// экипировки без ячейки.
        /// </summary>
        public bool DeleteGroup(int groupId)
        {
            return DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                // 1. Снимаем предметы со всех ячеек группы
                using (var unequip = new SqliteCommand(@"
                    UPDATE Inventory SET is_equipped = 0
                    WHERE id_item IN (SELECT id_item FROM EquipmentCells
                                      WHERE id_group = @groupId AND id_item IS NOT NULL)", conn, tx))
                {
                    unequip.Parameters.AddWithValue("@groupId", groupId);
                    unequip.ExecuteNonQuery();
                }

                // 2. Удаляем ячейки группы
                using (var cells = new SqliteCommand(
                    "DELETE FROM EquipmentCells WHERE id_group = @groupId", conn, tx))
                {
                    cells.Parameters.AddWithValue("@groupId", groupId);
                    cells.ExecuteNonQuery();
                }

                // 3. Удаляем саму группу
                using var group = new SqliteCommand(
                    "DELETE FROM EquipmentGroups WHERE id_group = @groupId AND id_character = @charId", conn, tx);
                group.Parameters.AddWithValue("@groupId", groupId);
                group.Parameters.AddWithValue("@charId", _characterId);
                return group.ExecuteNonQuery() > 0;
            });
        }

        /// <summary>
        /// Удалить ячейку, сняв предмет, который в ней стоял.
        /// </summary>
        public bool DeleteCell(int cellId)
        {
            return DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                using (var unequip = new SqliteCommand(@"
                    UPDATE Inventory SET is_equipped = 0
                    WHERE id_item IN (SELECT id_item FROM EquipmentCells
                                      WHERE id_cell = @cellId AND id_item IS NOT NULL)", conn, tx))
                {
                    unequip.Parameters.AddWithValue("@cellId", cellId);
                    unequip.ExecuteNonQuery();
                }

                using var cmd = new SqliteCommand("DELETE FROM EquipmentCells WHERE id_cell = @cellId", conn, tx);
                cmd.Parameters.AddWithValue("@cellId", cellId);
                return cmd.ExecuteNonQuery() > 0;
            });
        }
        /// <summary>Сколько предметов надето через ячейки указанной группы.</summary>
        public int CountEquippedInGroup(int groupId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COUNT(*) FROM EquipmentCells
                                WHERE id_group = @groupId AND id_item IS NOT NULL";
            cmd.Parameters.AddWithValue("@groupId", groupId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>Переименовать группу.</summary>
        public void RenameGroup(int groupId, string newName)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE EquipmentGroups SET group_name = @name
                                WHERE id_group = @groupId AND id_character = @charId";
            cmd.Parameters.AddWithValue("@name", newName ?? "");
            cmd.Parameters.AddWithValue("@groupId", groupId);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Обновить позицию группы (для drag-and-drop)
        /// </summary>
        public void UpdateGroupPosition(int groupId, double positionX, double positionY)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = "UPDATE EquipmentGroups SET position_x = @posX, position_y = @posY WHERE id_group = @groupId AND id_character = @charId";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@posX", positionX);
            cmd.Parameters.AddWithValue("@posY", positionY);
            cmd.Parameters.AddWithValue("@groupId", groupId);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            cmd.ExecuteNonQuery();
        }
        /// <summary>
        /// Добавить ячейку в группу
        /// </summary>
        public int CreateCell(int groupId, int cellIndex)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = "INSERT INTO EquipmentCells (id_group, cell_index) VALUES (@groupId, @cellIndex); SELECT last_insert_rowid();";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@groupId", groupId);
            cmd.Parameters.AddWithValue("@cellIndex", cellIndex);

            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>
        /// Привязать предмет к ячейке: предмет становится экипированным,
        /// прежний предмет ячейки — снятым.
        /// </summary>
        public void AssignItemToCell(int cellId, int? itemId)
        {
            DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                // Снимаем предмет, который стоял в ячейке раньше
                using (var clearOld = new SqliteCommand(@"
                    UPDATE Inventory SET is_equipped = 0
                    WHERE id_item IN (SELECT id_item FROM EquipmentCells
                                      WHERE id_cell = @cellId AND id_item IS NOT NULL)", conn, tx))
                {
                    clearOld.Parameters.AddWithValue("@cellId", cellId);
                    clearOld.ExecuteNonQuery();
                }

                using (var assign = new SqliteCommand(
                    "UPDATE EquipmentCells SET id_item = @itemId WHERE id_cell = @cellId", conn, tx))
                {
                    assign.Parameters.AddWithValue("@itemId", itemId.HasValue ? (object)itemId.Value : DBNull.Value);
                    assign.Parameters.AddWithValue("@cellId", cellId);
                    assign.ExecuteNonQuery();
                }

                // Новый предмет считаем экипированным
                if (itemId.HasValue)
                {
                    using var equip = new SqliteCommand(
                        "UPDATE Inventory SET is_equipped = 1 WHERE id_item = @itemId", conn, tx);
                    equip.Parameters.AddWithValue("@itemId", itemId.Value);
                    equip.ExecuteNonQuery();
                }
            });
        }
        /// <summary>
        /// Получить список слотов, где экипирован предмет
        /// </summary>
        public List<string> GetItemEquipmentLocations(int itemId)
        {
            var locations = new List<string>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            string query = @"
        SELECT s.slot_name 
        FROM EquipmentCells c
        INNER JOIN EquipmentGroups g ON c.id_group = g.id_group
        INNER JOIN EquipmentSlots s ON g.id_slot = s.id_slot
        WHERE c.id_item = @itemId AND g.id_character = @charId
        ORDER BY s.slot_name";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@itemId", itemId);
            cmd.Parameters.AddWithValue("@charId", _characterId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                locations.Add(reader["slot_name"].ToString());
            }

            return locations;
        }

        /// <summary>
        /// Создать новый кастомный слот экипировки
        /// </summary>
        public int CreateCustomSlot(string slotName)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();

            // Проверяем, существует ли уже такой слот
            string checkQuery = "SELECT id_slot FROM EquipmentSlots WHERE slot_name = @slotName";
            using var checkCmd = new SqliteCommand(checkQuery, conn);
            checkCmd.Parameters.AddWithValue("@slotName", slotName);
            var existingId = checkCmd.ExecuteScalar();

            if (existingId != null)
            {
                return Convert.ToInt32(existingId);
            }

            // Генерируем slot_code из имени (транслит + lowercase + без пробелов)
            string slotCode = GenerateSlotCode(slotName);

            // Создаём новый слот с slot_code
            string insertQuery = "INSERT INTO EquipmentSlots (slot_name, slot_code) VALUES (@slotName, @slotCode); SELECT last_insert_rowid();";
            using var cmd = new SqliteCommand(insertQuery, conn);
            cmd.Parameters.AddWithValue("@slotName", slotName);
            cmd.Parameters.AddWithValue("@slotCode", slotCode);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private string GenerateSlotCode(string name)
        {
            // Простой транслит + очистка
            var translit = new Dictionary<char, string>
    {
        {'а', "a"}, {'б', "b"}, {'в', "v"}, {'г', "g"}, {'д', "d"},
        {'е', "e"}, {'ё', "yo"}, {'ж', "zh"}, {'з', "z"}, {'и', "i"},
        {'й', "y"}, {'к', "k"}, {'л', "l"}, {'м', "m"}, {'н', "n"},
        {'о', "o"}, {'п', "p"}, {'р', "r"}, {'с', "s"}, {'т', "t"},
        {'у', "u"}, {'ф', "f"}, {'х', "kh"}, {'ц', "ts"}, {'ч', "ch"},
        {'ш', "sh"}, {'щ', "sch"}, {'ъ', ""}, {'ы', "y"}, {'ь', ""},
        {'э', "e"}, {'ю', "yu"}, {'я', "ya"}, {' ', "_"}
    };

            var result = new System.Text.StringBuilder();
            foreach (char c in name.ToLower())
            {
                if (translit.ContainsKey(c))
                    result.Append(translit[c]);
                else if (char.IsLetterOrDigit(c))
                    result.Append(c);
            }

            string code = result.ToString();
            return string.IsNullOrEmpty(code) ? "custom" : code;
        }
        public bool IsItemEquipped(int itemId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM EquipmentCells WHERE id_item = @itemId";
            cmd.Parameters.AddWithValue("@itemId", itemId);
            int count = Convert.ToInt32(cmd.ExecuteScalar());
            return count > 0;
        }
    }
}
