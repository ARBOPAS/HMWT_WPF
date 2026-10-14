using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Data;

namespace HMWT_MUI.Services
{
    public class ItemData
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public decimal Weight { get; set; } = 0;
        public int CostGold { get; set; } = 0;
        public int CostSilver { get; set; } = 0;
        public int CostCopper { get; set; } = 0;
        public string DamageDice { get; set; } = "";
        public string DamageType { get; set; } = "";
        public int ArmorClassBonus { get; set; } = 0;
        public bool IsEquipped { get; set; } = false;
        public bool IsAttuned { get; set; } = false;
        public int MaxCharges { get; set; } = 0;
        public int Charges { get; set; } = 0;
        public bool IsStackable { get; set; } = false;
        public string Rarity { get; set; } = "";
        public string Notes { get; set; } = "";
        public string PhotoPath { get; set; } = "";

        /// <summary>
        /// Отдельное фото для строки списка. Надо разделить: в списке
        /// показывается выбранная картинка (первый кадр гифки бывает некрасивым),
        /// а в инспекторе — другая, которой можно «хвастаться».
        /// </summary>
        public string ListPhotoPath { get; set; } = "";
    }

    public class InventoryService
    {
        private readonly int _characterId;

        /// <summary>Ключ настройки с максимальным весом персонажа.</summary>
        private string MaxWeightKey => "Character_" + _characterId + "_MaxCarryWeight";

        public InventoryService(int characterId)
        {
            _characterId = characterId;

            // Колонки Inventory досоздаёт DatabaseHelper на инициализации базы.
            //
            // Раньше конструктор на каждое создание сервиса выполнял три
            // ALTER TABLE, которые заведомо падали с «duplicate column name»
            // (исключение глушилось). Сервис же создаётся не только при открытии
            // инвентаря: его создаёт сводка снаряжения на листе персонажа, то
            // есть работа выполнялась при каждом открытии листа и при каждой
            // смене характеристики. Миграции должны идти в одном месте — в
            // DatabaseHelper.CreateAllTablesIfNotExists.
        }

        // ==================== ЧТЕНИЕ ====================
        public DataTable GetFilteredInventory(string searchTerm = null, string category = "Все", bool equippedOnly = false)
        {
            var dt = new DataTable();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            string query = @"
                SELECT i.*,
                       CASE WHEN EXISTS (SELECT 1 FROM EquipmentCells ec WHERE ec.id_item = i.id_item) THEN 1 ELSE 0 END as is_equipped
                FROM Inventory i
                WHERE i.id_character = @charId";
            if (!string.IsNullOrWhiteSpace(searchTerm))
                query += " AND (i.name LIKE @search OR i.category LIKE @search OR i.description LIKE @search)";
            if (category != "Все")
                query += " AND i.category = @category";
            if (equippedOnly)
                query += " AND EXISTS (SELECT 1 FROM EquipmentCells ec WHERE ec.id_item = i.id_item)";
            query += " ORDER BY i.name";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            if (!string.IsNullOrWhiteSpace(searchTerm)) cmd.Parameters.AddWithValue("@search", $"%{searchTerm}%");
            if (category != "Все") cmd.Parameters.AddWithValue("@category", category);
            using var reader = cmd.ExecuteReader();
            dt.Load(reader);
            return dt;
        }

        public ItemData GetItem(int itemId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT * FROM Inventory WHERE id_item=@id AND id_character=@charId", conn);
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var item = MapReaderToItemData(reader);
                item.IsEquipped = IsItemEquippedInCells(itemId, conn);
                return item;
            }
            return null;
        }

        private bool IsItemEquippedInCells(int itemId, SqliteConnection conn)
        {
            using var cmd = new SqliteCommand(@"SELECT COUNT(*) FROM EquipmentCells c
                INNER JOIN EquipmentGroups g ON c.id_group = g.id_group
                WHERE c.id_item=@itemId AND g.id_character=@charId", conn);
            cmd.Parameters.AddWithValue("@itemId", itemId);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        private static bool HasColumn(SqliteDataReader r, string col)
        {
            for (int i = 0; i < r.FieldCount; i++) if (r.GetName(i) == col) return true;
            return false;
        }

        private ItemData MapReaderToItemData(SqliteDataReader r) => new ItemData
        {
            Id = Convert.ToInt32(r["id_item"]),
            Name = r["name"]?.ToString() ?? "",
            Category = HasColumn(r, "category") ? r["category"]?.ToString() ?? "" : "",
            Description = HasColumn(r, "description") ? r["description"]?.ToString() ?? "" : "",
            Quantity = Convert.ToInt32(r["quantity"]),
            Weight = Convert.ToDecimal(r["weight"]),
            CostGold = Convert.ToInt32(r["cost_gold"]),
            CostSilver = Convert.ToInt32(r["cost_silver"]),
            CostCopper = Convert.ToInt32(r["cost_copper"]),
            DamageDice = HasColumn(r, "damage_dice") ? r["damage_dice"]?.ToString() ?? "" : "",
            DamageType = HasColumn(r, "damage_type") ? r["damage_type"]?.ToString() ?? "" : "",
            ArmorClassBonus = Convert.ToInt32(r["armor_class_bonus"]),
            IsEquipped = Convert.ToInt32(r["is_equipped"]) == 1,
            IsAttuned = Convert.ToInt32(r["is_attuned"]) == 1,
            Charges = HasColumn(r, "charges") ? Convert.ToInt32(r["charges"]) : 0,
            MaxCharges = HasColumn(r, "max_charges") ? Convert.ToInt32(r["max_charges"]) : 0,
            IsStackable = HasColumn(r, "is_stackable") && Convert.ToInt32(r["is_stackable"]) == 1,
            Rarity = HasColumn(r, "rarity") ? r["rarity"]?.ToString() ?? "" : "",
            Notes = HasColumn(r, "notes") ? r["notes"]?.ToString() ?? "" : "",
            PhotoPath = HasColumn(r, "PhotoPath") ? r["PhotoPath"]?.ToString() ?? "" : "",
            ListPhotoPath = HasColumn(r, "list_photo_path") ? r["list_photo_path"]?.ToString() ?? "" : ""
        };

        // ==================== ЗАПИСЬ ====================

        /// <summary>
        /// Ищет в инвентаре предмет, полностью совпадающий с указанным.
        ///
        /// Нужно для стакивания: при покупке одинаковые предметы должны
        /// увеличивать количество в одной карточке, а не создавать новую.
        /// Сравниваем всё, что описывает предмет: название, категорию, редкость,
        /// кость и тип урона, бонус КД, вес и описание. Если отличается хоть
        /// что-то — это другой предмет, и складывать их нельзя.
        /// </summary>
        /// <returns>Идентификатор подходящей записи либо 0.</returns>
        public int FindStackableItem(ItemData d)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.Name)) return 0;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT id_item FROM Inventory
                    WHERE id_character=@charId
                      AND name=@name
                      AND COALESCE(category,'')=@category
                      AND COALESCE(rarity,'')=@rarity
                      AND COALESCE(damage_dice,'')=@damageDice
                      AND COALESCE(damage_type,'')=@damageType
                      AND armor_class_bonus=@acBonus
                      AND weight=@weight
                      AND COALESCE(description,'')=@description
                      AND is_equipped=0
                    ORDER BY id_item LIMIT 1", conn);

                cmd.Parameters.AddWithValue("@charId", _characterId);
                cmd.Parameters.AddWithValue("@name", d.Name.Trim());
                cmd.Parameters.AddWithValue("@category", d.Category ?? "");
                cmd.Parameters.AddWithValue("@rarity", d.Rarity ?? "");
                cmd.Parameters.AddWithValue("@damageDice", d.DamageDice ?? "");
                cmd.Parameters.AddWithValue("@damageType", d.DamageType ?? "");
                cmd.Parameters.AddWithValue("@acBonus", d.ArmorClassBonus);
                cmd.Parameters.AddWithValue("@weight", d.Weight);
                cmd.Parameters.AddWithValue("@description", d.Description ?? "");

                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InventoryService] поиск одинакового: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Добавляет предмет в инвентарь, стакивая одинаковые.
        ///
        /// Если такой предмет уже есть, увеличивается только количество —
        /// вторая карточка в списке не появляется.
        /// </summary>
        /// <returns>Идентификатор записи и её итоговое количество.</returns>
        public (int Id, int Quantity, bool Stacked) AddOrStack(ItemData d)
        {
            if (d == null) return (0, 0, false);

            int existing = FindStackableItem(d);
            if (existing <= 0)
            {
                int created = SaveItem(0, d);
                return (created, Math.Max(1, d.Quantity), false);
            }

            // Такой предмет уже лежит: увеличиваем количество
            int current = GetItem(existing)?.Quantity ?? 0;
            int total = current + Math.Max(1, d.Quantity);

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "UPDATE Inventory SET quantity=@q WHERE id_item=@id AND id_character=@charId", conn);
                cmd.Parameters.AddWithValue("@q", total);
                cmd.Parameters.AddWithValue("@id", existing);
                cmd.Parameters.AddWithValue("@charId", _characterId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InventoryService] стакивание: {ex.Message}");
                return (existing, current, false);
            }

            return (existing, total, true);
        }

        public int SaveItem(int itemId, ItemData d)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            if (itemId == 0)
            {
                using var cmd = new SqliteCommand(@"
                    INSERT INTO Inventory
                    (id_character, name, category, description, quantity, weight,
                     cost_gold, cost_silver, cost_copper, damage_dice, damage_type,
                     armor_class_bonus, is_equipped, is_attuned, charges, max_charges, rarity, notes, PhotoPath, list_photo_path)
                    VALUES
                    (@charId, @name, @category, @description, @quantity, @weight,
                     @costGold, @costSilver, @costCopper, @damageDice, @damageType,
                     @acBonus, 0, @attuned, @charges, @maxCharges, @rarity, @notes, @photoPath, @listPhotoPath);
                    SELECT last_insert_rowid();", conn);
                AddItemParams(cmd, d);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            using var upd = new SqliteCommand(@"
                UPDATE Inventory SET
                    name=@name, category=@category, description=@description, quantity=@quantity, weight=@weight,
                    cost_gold=@costGold, cost_silver=@costSilver, cost_copper=@costCopper,
                    damage_dice=@damageDice, damage_type=@damageType, armor_class_bonus=@acBonus,
                    is_attuned=@attuned, charges=@charges, max_charges=@maxCharges, rarity=@rarity, notes=@notes,
                    PhotoPath=@photoPath, list_photo_path=@listPhotoPath
                WHERE id_item=@itemId AND id_character=@charId", conn);
            AddItemParams(upd, d);
            upd.Parameters.AddWithValue("@itemId", itemId);
            int rows = upd.ExecuteNonQuery();
            if (rows == 0) throw new Exception($"Предмет {itemId} не найден или не принадлежит персонажу");
            return itemId;
        }

        private void AddItemParams(SqliteCommand cmd, ItemData d)
        {
            cmd.Parameters.AddWithValue("@charId", _characterId);
            cmd.Parameters.AddWithValue("@name", d.Name ?? "");
            cmd.Parameters.AddWithValue("@category", d.Category ?? "");
            cmd.Parameters.AddWithValue("@description", d.Description ?? "");
            cmd.Parameters.AddWithValue("@quantity", d.Quantity);
            cmd.Parameters.AddWithValue("@weight", d.Weight);
            cmd.Parameters.AddWithValue("@costGold", d.CostGold);
            cmd.Parameters.AddWithValue("@costSilver", d.CostSilver);
            cmd.Parameters.AddWithValue("@costCopper", d.CostCopper);
            cmd.Parameters.AddWithValue("@damageDice", d.DamageDice ?? "");
            cmd.Parameters.AddWithValue("@damageType", d.DamageType ?? "");
            cmd.Parameters.AddWithValue("@acBonus", d.ArmorClassBonus);
            cmd.Parameters.AddWithValue("@attuned", d.IsAttuned ? 1 : 0);
            cmd.Parameters.AddWithValue("@charges", d.Charges);
            cmd.Parameters.AddWithValue("@maxCharges", d.MaxCharges);
            cmd.Parameters.AddWithValue("@rarity", d.Rarity ?? "");
            cmd.Parameters.AddWithValue("@notes", d.Notes ?? "");
            cmd.Parameters.AddWithValue("@photoPath", d.PhotoPath ?? "");
            cmd.Parameters.AddWithValue("@listPhotoPath", d.ListPhotoPath ?? "");
        }

        public bool DeleteItem(int itemId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("DELETE FROM Inventory WHERE id_item=@id AND id_character=@charId", conn);
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public bool DeleteItemWithUnequip(int itemId, int quantityToRemove, out string errorMessage)
        {
            errorMessage = "";
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var unequip = new SqliteCommand("UPDATE EquipmentCells SET id_item=NULL WHERE id_item=@itemId", conn, tx))
                {
                    unequip.Parameters.AddWithValue("@itemId", itemId);
                    unequip.ExecuteNonQuery();
                }
                using (var get = new SqliteCommand("SELECT quantity FROM Inventory WHERE id_item=@id AND id_character=@charId", conn, tx))
                {
                    get.Parameters.AddWithValue("@id", itemId);
                    get.Parameters.AddWithValue("@charId", _characterId);
                    var res = get.ExecuteScalar();
                    if (res == null) { errorMessage = "Предмет не найден"; tx.Rollback(); return false; }
                    int newQty = Convert.ToInt32(res) - quantityToRemove;
                    if (newQty <= 0)
                    {
                        using var del = new SqliteCommand("DELETE FROM Inventory WHERE id_item=@id AND id_character=@charId", conn, tx);
                        del.Parameters.AddWithValue("@id", itemId);
                        del.Parameters.AddWithValue("@charId", _characterId);
                        del.ExecuteNonQuery();
                    }
                    else
                    {
                        using var upd = new SqliteCommand("UPDATE Inventory SET quantity=@q WHERE id_item=@id AND id_character=@charId", conn, tx);
                        upd.Parameters.AddWithValue("@q", newQty);
                        upd.Parameters.AddWithValue("@id", itemId);
                        upd.Parameters.AddWithValue("@charId", _characterId);
                        upd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
                return true;
            }
            catch (Exception ex) { tx.Rollback(); errorMessage = ex.Message; return false; }
        }

        // ==================== КОЛИЧЕСТВО / ДУБЛИКАТЫ ====================
        public (bool exists, int existingItemId, int currentQuantity) CheckDuplicateItem(string name, string category)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT id_item, quantity FROM Inventory WHERE name=@name AND category=@category AND id_character=@charId", conn);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@category", category);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            using var r = cmd.ExecuteReader();
            if (r.Read()) return (true, Convert.ToInt32(r["id_item"]), Convert.ToInt32(r["quantity"]));
            return (false, 0, 0);
        }

        public void IncreaseItemQuantity(int itemId, int amount)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Inventory SET quantity=quantity+@a WHERE id_item=@id AND id_character=@charId", conn);
            cmd.Parameters.AddWithValue("@a", amount);
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            cmd.ExecuteNonQuery();
        }

        public bool UseCharge(int itemId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Inventory SET charges=MAX(0, charges-1) WHERE id_item=@id AND charges>0", conn);
            cmd.Parameters.AddWithValue("@id", itemId);
            return cmd.ExecuteNonQuery() > 0;
        }

        // ==================== ЭКИПИРОВКА-ПРОВЕРКИ ====================
        public (bool isEquipped, int equippedCount) CheckIfItemEquipped(int itemId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"SELECT COUNT(*) FROM EquipmentCells c
                INNER JOIN EquipmentGroups g ON c.id_group=g.id_group
                WHERE c.id_item=@itemId AND g.id_character=@charId", conn);
            cmd.Parameters.AddWithValue("@itemId", itemId);
            cmd.Parameters.AddWithValue("@charId", _characterId);
            int n = Convert.ToInt32(cmd.ExecuteScalar());
            return (n > 0, n);
        }

        public (bool canEquip, string warning, int equippedCount) CheckEquipAvailability(int itemId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var get = new SqliteCommand("SELECT quantity, name FROM Inventory WHERE id_item=@id AND id_character=@charId", conn);
            get.Parameters.AddWithValue("@id", itemId);
            get.Parameters.AddWithValue("@charId", _characterId);
            using var r = get.ExecuteReader();
            if (!r.Read()) return (false, "Предмет не найден", 0);
            int total = Convert.ToInt32(r["quantity"]);
            string name = r["name"]?.ToString();
            r.Close();
            using var eq = new SqliteCommand(@"SELECT COUNT(*) FROM EquipmentCells c
                INNER JOIN EquipmentGroups g ON c.id_group=g.id_group
                WHERE c.id_item=@itemId AND g.id_character=@charId", conn);
            eq.Parameters.AddWithValue("@itemId", itemId);
            eq.Parameters.AddWithValue("@charId", _characterId);
            int equipped = Convert.ToInt32(eq.ExecuteScalar());
            if (equipped >= total) return (false, $"Все экземпляры «{name}» уже экипированы ({equipped}/{total})", equipped);
            if (equipped > 0) return (true, $"Уже экипировано: {equipped} из {total}", equipped);
            return (true, "", 0);
        }

        // ==================== ВЕС / СТОИМОСТЬ / СОКРОВИЩА ====================
        public (decimal totalWeight, int totalItems) CalculateTotalWeight()
        {
            decimal w = 0; int n = 0;
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT quantity, weight FROM Inventory WHERE id_character=@id", conn);
            cmd.Parameters.AddWithValue("@id", _characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { int q = Convert.ToInt32(r["quantity"]); w += q * Convert.ToDecimal(r["weight"]); n += q; }
            return (w, n);
        }

        public class ItemSummary
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int Quantity { get; set; }
            public decimal TotalValue { get; set; }
        }

        public (decimal Total, List<ItemSummary> Items) GetTreasureSummary(bool treasureOnly)
        {
            decimal total = 0;
            var items = new List<ItemSummary>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT id_item, name, quantity, cost_gold, cost_silver, cost_copper FROM Inventory WHERE id_character=@id ORDER BY name", conn);
            cmd.Parameters.AddWithValue("@id", _characterId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = Convert.ToInt32(r["id_item"]);
                bool isTreasure = HasTreasureTag(id, conn);
                if (treasureOnly && !isTreasure) continue;
                int q = Convert.ToInt32(r["quantity"]);
                decimal v = Convert.ToInt32(r["cost_gold"]) + Convert.ToInt32(r["cost_silver"]) / 10m + Convert.ToInt32(r["cost_copper"]) / 100m;
                decimal tv = v * q;
                total += tv;
                items.Add(new ItemSummary { Id = id, Name = r["name"]?.ToString(), Quantity = q, TotalValue = tv });
            }
            return (total, items);
        }

        private static bool HasTreasureTag(int itemId, SqliteConnection conn)
        {
            using var cmd = new SqliteCommand(@"SELECT COUNT(*) FROM ItemTagMapping m
        INNER JOIN Tags t ON t.id_tag = m.id_tag
        WHERE m.id_item = @id AND (t.tag_name LIKE '%сокровищ%' OR t.tag_name LIKE '%treasure%')", conn);
            cmd.Parameters.AddWithValue("@id", itemId);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        // ==================== ТЕГИ ПРЕДМЕТОВ ====================
        public List<string> GetAllTagNames()
        {
            var list = new List<string>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT tag_name FROM Tags ORDER BY tag_name", conn);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }

        public List<string> GetTagsForItem(int itemId)
        {
            var list = new List<string>();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"SELECT t.tag_name FROM Tags t
        INNER JOIN ItemTagMapping m ON m.id_tag = t.id_tag
        WHERE m.id_item = @id ORDER BY t.tag_name", conn);
            cmd.Parameters.AddWithValue("@id", itemId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }

        public void CreateTag(string name)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("INSERT INTO  Tags(tag_name) SELECT @n WHERE NOT EXISTS (SELECT 1 FROM  Tags WHERE tag_name=@n)", conn);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.ExecuteNonQuery();
        }

        public void DeleteTag(string name)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            using (var sel = new SqliteCommand("SELECT id_tag FROM  Tags WHERE tag_name=@n", conn, tx))
            {
                sel.Parameters.AddWithValue("@n", name);
                var id = sel.ExecuteScalar();
                if (id != null)
                {
                    using var delM = new SqliteCommand("DELETE FROM ItemTagMapping WHERE id_tag=@id", conn, tx);
                    delM.Parameters.AddWithValue("@id", id); delM.ExecuteNonQuery();
                    using var delT = new SqliteCommand("DELETE FROM  Tags WHERE id_tag=@id", conn, tx);
                    delT.Parameters.AddWithValue("@id", id); delT.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }

        public void SetItemTags(int itemId, List<string> tagNames)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            using (var del = new SqliteCommand("DELETE FROM ItemTagMapping WHERE id_item=@id", conn, tx))
            {
                del.Parameters.AddWithValue("@id", itemId); del.ExecuteNonQuery();
            }
            foreach (var name in tagNames)
            {
                using (var ensure = new SqliteCommand("INSERT INTO  Tags(tag_name) SELECT @n WHERE NOT EXISTS (SELECT 1 FROM  Tags WHERE tag_name=@n)", conn, tx))
                { ensure.Parameters.AddWithValue("@n", name); ensure.ExecuteNonQuery(); }
                using (var get = new SqliteCommand("SELECT id_tag FROM  Tags WHERE tag_name=@n", conn, tx))
                {
                    get.Parameters.AddWithValue("@n", name);
                    var tid = get.ExecuteScalar();
                    if (tid != null)
                    {
                        using var ins = new SqliteCommand("INSERT INTO ItemTagMapping (id_item, id_tag) VALUES (@i, @t)", conn, tx);
                        ins.Parameters.AddWithValue("@i", itemId); ins.Parameters.AddWithValue("@t", tid);
                        ins.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
        }

        // ==================== СИЛУЭТ / ГРУЗОПОДЪЁМНОСТЬ ====================
        public void SaveSilhouettePath(int characterId, string path)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@k, @v)", conn);
            cmd.Parameters.AddWithValue("@k", $"Character_{characterId}_SilhouettePath");
            cmd.Parameters.AddWithValue("@v", path);
            cmd.ExecuteNonQuery();
        }

        public string LoadSilhouettePath(int characterId)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT Value FROM Settings WHERE Key=@k", conn);
            cmd.Parameters.AddWithValue("@k", $"Character_{characterId}_SilhouettePath");
            var res = cmd.ExecuteScalar()?.ToString();
            return (!string.IsNullOrEmpty(res) && System.IO.File.Exists(res)) ? res : null;
        }

        public int GetCharacterStrength()
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT Strength FROM CharacterInfo WHERE id_character=@id", conn);
            cmd.Parameters.AddWithValue("@id", _characterId);
            var res = cmd.ExecuteScalar();
            return res != null ? Convert.ToInt32(res) : 10;
        }

        /// <summary>
        /// Максимальный вес, который персонаж может нести.
        ///
        /// Значение задаёт сам игрок: в настольной игре его меняют раса, класс с
        /// «Мощным телосложением», магические предметы и решение ведущего, и
        /// автоматический подсчёт от Силы в этом только мешал. Пока игрок ничего
        /// не ввёл, возвращаем 0 — это значит «предел не задан», и полоса веса не
        /// показывает ложную загрузку.
        /// </summary>
        public decimal GetMaxCarryingCapacity()
        {
            try
            {
                string raw = DatabaseHelper.GetSetting(MaxWeightKey, "");
                if (!string.IsNullOrWhiteSpace(raw) &&
                    decimal.TryParse(raw.Replace(',', '.'),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out decimal saved))
                {
                    return Math.Max(0, saved);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InventoryService] чтение макс. веса: {ex.Message}");
            }

            return 0;
        }

        /// <summary>Сохраняет максимальный вес, заданный игроком.</summary>
        public void SetMaxCarryingCapacity(decimal weight)
        {
            try
            {
                decimal value = Math.Max(0, weight);
                DatabaseHelper.SaveSetting(MaxWeightKey,
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InventoryService] сохранение макс. веса: {ex.Message}");
            }
        }

        /// <summary>Задавал ли игрок максимальный вес вручную.</summary>
        public bool HasCustomMaxWeight()
        {
            try { return !string.IsNullOrWhiteSpace(DatabaseHelper.GetSetting(MaxWeightKey, "")); }
            catch { return false; }
        }
    }
}