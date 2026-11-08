using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HMWT_MUI.Helpers;
using Microsoft.Data.Sqlite;

namespace HMWT_MUI.Services
{
    /// <summary>Категория справочника добычи: оружие, зелья, безделушки…</summary>
    public sealed class LootCategory
    {
        public int Id { get; set; }
        public int CharacterId { get; set; }
        public string Name { get; set; } = "";

        public override string ToString() => Name;
    }

    /// <summary>Предмет, который генератор может положить в добычу.</summary>
    public sealed class LootItem
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string Name { get; set; } = "";
        public string Rarity { get; set; } = "";
        public int QtyMin { get; set; } = 1;
        public int QtyMax { get; set; } = 1;
        public string Note { get; set; } = "";

        public string Caption
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Rarity)) parts.Add(Rarity.Trim());
                if (QtyMax > 1) parts.Add(QtyMin == QtyMax ? $"{QtyMax} шт." : $"{QtyMin}–{QtyMax} шт.");
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>Что выпало: золото и предметы.</summary>
    public sealed class LootResult
    {
        public int Cr { get; set; }
        public string Container { get; set; } = "";
        public int Gold { get; set; }
        public List<(LootItem Item, string Category, int Qty)> Items { get; } =
            new List<(LootItem, string, int)>();

        /// <summary>Текст для чата и заметок.</summary>
        public string ToText()
        {
            var text = new StringBuilder();
            text.AppendLine($"Добыча (CR {Cr}, {Container}):");
            if (Gold > 0) text.AppendLine($"— {Gold} зм");

            foreach (var (item, category, qty) in Items)
            {
                text.Append("— ").Append(item.Name);
                if (qty > 1) text.Append($" ×{qty}");
                if (!string.IsNullOrWhiteSpace(item.Rarity)) text.Append($", {item.Rarity}");
                if (!string.IsNullOrWhiteSpace(category)) text.Append($" ({category})");
                if (!string.IsNullOrWhiteSpace(item.Note)) text.Append($": {item.Note}");
                text.AppendLine();
            }

            if (Items.Count == 0 && Gold == 0) text.AppendLine("— пусто, только пыль");
            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Генератор добычи с редактируемым справочником.
    ///
    /// Справочник свой у каждого персонажа: ведущий сам решает, что может
    /// выпасть в его мире. Генератор берёт золото по уровню и типу тайника,
    /// а предметы — из справочника, с шансом и количеством из записи.
    /// </summary>
    public static class LootService
    {
        private static readonly object Gate = new object();
        private static readonly Random Rng = new Random();
        private static bool _ready;

        public static readonly string[] Containers =
        {
            "Карман", "Сундук", "Тайник", "Сокровищница"
        };

        public static readonly string[] Rarities =
        {
            "", "Обычный", "Необычный", "Редкий", "Очень редкий", "Легендарный", "Артефакт"
        };

        private static void EnsureTables()
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
                        CREATE TABLE IF NOT EXISTS LootCategory (
                            id_loot_category INTEGER PRIMARY KEY AUTOINCREMENT,
                            id_character INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            sort_order INTEGER DEFAULT 0);
                        CREATE TABLE IF NOT EXISTS LootItem (
                            id_loot_item INTEGER PRIMARY KEY AUTOINCREMENT,
                            id_loot_category INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            rarity TEXT,
                            qty_min INTEGER DEFAULT 1,
                            qty_max INTEGER DEFAULT 1,
                            note TEXT,
                            sort_order INTEGER DEFAULT 0);
                        CREATE INDEX IF NOT EXISTS idx_loot_cat_char ON LootCategory(id_character);
                        CREATE INDEX IF NOT EXISTS idx_loot_item_cat ON LootItem(id_loot_category);", conn);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LootService] таблицы: {ex.Message}");
                    return;
                }

                _ready = true;
            }
        }

        // ══════════════════════ СПРАВОЧНИК ══════════════════════

        public static List<LootCategory> LoadCategories(int characterId)
        {
            var result = new List<LootCategory>();
            if (characterId <= 0) return result;

            EnsureTables();
            EnsureSeeded(characterId);

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT id_loot_category, id_character, name FROM LootCategory " +
                    "WHERE id_character = @id ORDER BY sort_order, name", conn);
                cmd.Parameters.AddWithValue("@id", characterId);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    result.Add(new LootCategory
                    {
                        Id = r.GetInt32(0),
                        CharacterId = r.GetInt32(1),
                        Name = r.IsDBNull(2) ? "" : r.GetString(2)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] LoadCategories: {ex.Message}");
            }

            return result;
        }

        public static int SaveCategory(int characterId, string name, int id = 0)
        {
            if (characterId <= 0 || string.IsNullOrWhiteSpace(name)) return 0;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using var cmd = id > 0
                    ? new SqliteCommand("UPDATE LootCategory SET name=@name WHERE id_loot_category=@id; SELECT @id;", conn)
                    : new SqliteCommand(
                        "INSERT INTO LootCategory (id_character, name, sort_order) VALUES (@char, @name, 0); " +
                        "SELECT last_insert_rowid();", conn);

                cmd.Parameters.AddWithValue("@name", name.Trim());
                cmd.Parameters.AddWithValue("@char", characterId);
                if (id > 0) cmd.Parameters.AddWithValue("@id", id);

                object raw = cmd.ExecuteScalar();
                return raw == null || raw == DBNull.Value ? id : Convert.ToInt32(raw);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] SaveCategory: {ex.Message}");
                return 0;
            }
        }

        public static void DeleteCategory(int categoryId)
        {
            if (categoryId <= 0) return;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "DELETE FROM LootItem WHERE id_loot_category=@id; " +
                    "DELETE FROM LootCategory WHERE id_loot_category=@id;", conn);
                cmd.Parameters.AddWithValue("@id", categoryId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] DeleteCategory: {ex.Message}");
            }
        }

        public static List<LootItem> LoadItems(int categoryId)
        {
            var result = new List<LootItem>();
            if (categoryId <= 0) return result;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT id_loot_item, id_loot_category, name, rarity, qty_min, qty_max, note " +
                    "FROM LootItem WHERE id_loot_category=@id ORDER BY sort_order, name", conn);
                cmd.Parameters.AddWithValue("@id", categoryId);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    result.Add(new LootItem
                    {
                        Id = r.GetInt32(0),
                        CategoryId = r.GetInt32(1),
                        Name = r.IsDBNull(2) ? "" : r.GetString(2),
                        Rarity = r.IsDBNull(3) ? "" : r.GetString(3),
                        QtyMin = r.IsDBNull(4) ? 1 : r.GetInt32(4),
                        QtyMax = r.IsDBNull(5) ? 1 : r.GetInt32(5),
                        Note = r.IsDBNull(6) ? "" : r.GetString(6)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] LoadItems: {ex.Message}");
            }

            return result;
        }

        public static int SaveItem(LootItem item)
        {
            if (item == null || item.CategoryId <= 0 || string.IsNullOrWhiteSpace(item.Name)) return 0;

            EnsureTables();

            int min = Math.Max(1, Math.Min(99, item.QtyMin));
            int max = Math.Max(min, Math.Min(99, item.QtyMax));

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using var cmd = item.Id > 0
                    ? new SqliteCommand(@"UPDATE LootItem SET name=@name, rarity=@rarity, qty_min=@min,
                                              qty_max=@max, note=@note WHERE id_loot_item=@id; SELECT @id;", conn)
                    : new SqliteCommand(@"INSERT INTO LootItem (id_loot_category, name, rarity, qty_min, qty_max, note)
                                          VALUES (@cat, @name, @rarity, @min, @max, @note);
                                          SELECT last_insert_rowid();", conn);

                cmd.Parameters.AddWithValue("@cat", item.CategoryId);
                cmd.Parameters.AddWithValue("@name", item.Name.Trim());
                cmd.Parameters.AddWithValue("@rarity", item.Rarity ?? "");
                cmd.Parameters.AddWithValue("@min", min);
                cmd.Parameters.AddWithValue("@max", max);
                cmd.Parameters.AddWithValue("@note", item.Note ?? "");
                if (item.Id > 0) cmd.Parameters.AddWithValue("@id", item.Id);

                object raw = cmd.ExecuteScalar();
                int id = raw == null || raw == DBNull.Value ? item.Id : Convert.ToInt32(raw);
                item.Id = id;
                item.QtyMin = min;
                item.QtyMax = max;
                return id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] SaveItem: {ex.Message}");
                return 0;
            }
        }

        public static void DeleteItem(int itemId)
        {
            if (itemId <= 0) return;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM LootItem WHERE id_loot_item=@id", conn);
                cmd.Parameters.AddWithValue("@id", itemId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] DeleteItem: {ex.Message}");
            }
        }

        // ══════════════════════ СВЯЗЬ С МАГАЗИНОМ ══════════════════════

        /// <summary>
        /// Добавляет предмет из каталога магазина в справочник добычи.
        /// Повторно один и тот же не добавляется: сверяем по имени без учёта
        /// регистра, иначе после нескольких импортов список распухал копиями.
        /// </summary>
        public static int AddFromShop(int categoryId, SrdItemRow row)
        {
            if (categoryId <= 0 || row == null || string.IsNullOrWhiteSpace(row.Name)) return 0;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var check = new SqliteCommand(
                           "SELECT COUNT(*) FROM LootItem WHERE id_loot_category=@cat AND name=@name COLLATE NOCASE",
                           conn))
                {
                    check.Parameters.AddWithValue("@cat", categoryId);
                    check.Parameters.AddWithValue("@name", row.Name.Trim());
                    if (Convert.ToInt32(check.ExecuteScalar() ?? 0) > 0) return 0;
                }

                using var cmd = new SqliteCommand(@"
                    INSERT INTO LootItem (id_loot_category, name, rarity, qty_min, qty_max, note)
                    VALUES (@cat, @name, @rarity, 1, 1, @note); SELECT last_insert_rowid();", conn);

                cmd.Parameters.AddWithValue("@cat", categoryId);
                cmd.Parameters.AddWithValue("@name", row.Name.Trim());
                cmd.Parameters.AddWithValue("@rarity", row.Rarity ?? "");
                cmd.Parameters.AddWithValue("@note", BuildShopNote(row));

                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] AddFromShop: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Короткая памятка из карточки магазина: тип, урон, КД, вес.</summary>
        private static string BuildShopNote(SrdItemRow row)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(row.ItemType)) parts.Add(row.ItemType.Trim());
            if (!string.IsNullOrWhiteSpace(row.Damage))
                parts.Add("урон " + row.Damage + (string.IsNullOrWhiteSpace(row.DamageType) ? "" : " (" + row.DamageType + ")"));
            if (row.Ac > 0) parts.Add("КД " + row.Ac);
            if (row.Weight > 0) parts.Add($"{row.Weight:0.##} кг");
            if (row.Attunement) parts.Add("настройка");
            return string.Join(", ", parts);
        }

        /// <summary>Сколько предметов в каждой категории: для сводки под списком.</summary>
        public static Dictionary<int, int> CountsByCategory(int characterId)
        {
            var result = new Dictionary<int, int>();
            if (characterId <= 0) return result;

            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT c.id_loot_category, COUNT(i.id_loot_item)
                    FROM LootCategory c
                    LEFT JOIN LootItem i ON i.id_loot_category = c.id_loot_category
                    WHERE c.id_character = @id
                    GROUP BY c.id_loot_category", conn);
                cmd.Parameters.AddWithValue("@id", characterId);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result[r.GetInt32(0)] = r.GetInt32(1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] CountsByCategory: {ex.Message}");
            }

            return result;
        }

        // ══════════════════════ ГЕНЕРАЦИЯ ══════════════════════

        /// <summary>Золото по уровню и типу тайника: базовое значение × множитель.</summary>
        public static int GoldFor(int cr, string container)
        {
            int level = Math.Max(1, Math.Min(20, cr));
            int baseGold = level switch
            {
                <= 4 => Rng.Next(5, 26),
                <= 8 => Rng.Next(30, 121),
                <= 12 => Rng.Next(150, 601),
                <= 16 => Rng.Next(700, 2501),
                _ => Rng.Next(3000, 12001)
            };

            double multiplier = container switch
            {
                "Карман" => 0.25,
                "Сундук" => 1.0,
                "Тайник" => 1.8,
                "Сокровищница" => 4.0,
                _ => 1.0
            };

            return Math.Max(1, (int)Math.Round(baseGold * multiplier / 5.0) * 5);
        }

        /// <summary>Сколько предметов взять для этого типа тайника.</summary>
        public static int ItemCountFor(string container, int cr)
        {
            int bonus = cr >= 11 ? 1 : 0;
            return container switch
            {
                "Карман" => 1,
                "Сундук" => 2 + bonus,
                "Тайник" => 3 + bonus,
                "Сокровищница" => 5 + bonus,
                _ => 2
            };
        }

        /// <summary>Собирает добычу: золото и предметы из справочника.</summary>
        public static LootResult Generate(int characterId, int cr, string container)
        {
            var result = new LootResult { Cr = Math.Max(1, Math.Min(20, cr)), Container = container ?? "Сундук" };

            var categories = LoadCategories(characterId);
            var pool = new List<(LootItem Item, string Category)>();

            foreach (var category in categories)
            {
                foreach (var item in LoadItems(category.Id))
                    pool.Add((item, category.Name));
            }

            result.Gold = GoldFor(result.Cr, result.Container);

            // Справочник пуст — берём предметы из каталога магазина: он уже
            // заполнен импортом SRD, и генератор работает сразу
            if (pool.Count == 0)
            {
                FillFromShop(result);
                return result;
            }

            int count = Math.Min(ItemCountFor(result.Container, result.Cr), pool.Count * 2);
            var used = new HashSet<int>();

            for (int i = 0; i < count; i++)
            {
                // Один и тот же предмет дважды не кладём: дубли выглядят как ошибка
                var candidates = pool.Where(p => !used.Contains(p.Item.Id)).ToList();
                if (candidates.Count == 0) break;

                var pick = candidates[Rng.Next(candidates.Count)];
                used.Add(pick.Item.Id);

                int qty = pick.Item.QtyMax > pick.Item.QtyMin
                    ? Rng.Next(pick.Item.QtyMin, pick.Item.QtyMax + 1)
                    : Math.Max(1, pick.Item.QtyMin);

                result.Items.Add((pick.Item, pick.Category, qty));
            }

            return result;
        }

        // ══════════════════════ МАГАЗИН В ДОБЫЧЕ ══════════════════════

        /// <summary>
        /// Когда справочник пуст, добыча собирается из каталога магазина:
        /// он уже заполнен импортом SRD, и не нужно заводить всё руками.
        /// Редкость подбираем по уровню — на 3-м уровне не должно падать
        /// легендарное кольцо.
        /// </summary>
        private static void FillFromShop(LootResult result)
        {
            try
            {
                var shop = new ShopService(0);
                var pool = new List<SrdItemRow>();

                foreach (string rarity in ShopRaritiesFor(result.Cr))
                {
                    var list = shop.LoadCatalog(new ShopFilter { Rarity = rarity }, 120);
                    if (list != null) pool.AddRange(list);
                }

                if (pool.Count == 0)
                {
                    // В каталоге не нашлось по редкости — берём что есть
                    var any = shop.LoadCatalog(new ShopFilter(), 120);
                    if (any != null) pool.AddRange(any);
                }

                if (pool.Count == 0) return;

                int count = Math.Min(ItemCountFor(result.Container, result.Cr), pool.Count);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < count; i++)
                {
                    var candidates = pool.Where(p => !used.Contains(p.Name ?? "")).ToList();
                    if (candidates.Count == 0) break;

                    var pick = candidates[Rng.Next(candidates.Count)];
                    used.Add(pick.Name ?? "");

                    result.Items.Add((new LootItem
                    {
                        Name = pick.Name ?? "",
                        Rarity = pick.Rarity ?? "",
                        QtyMin = 1,
                        QtyMax = 1,
                        Note = BuildShopNote(pick)
                    }, "магазин", 1));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] FillFromShop: {ex.Message}");
            }
        }

        /// <summary>Какие редкости уместны на этом уровне.</summary>
        public static string[] ShopRaritiesFor(int cr) => cr switch
        {
            <= 4 => new[] { "Обычный" },
            <= 8 => new[] { "Необычный", "Обычный" },
            <= 12 => new[] { "Редкий", "Необычный" },
            <= 16 => new[] { "Очень редкий", "Редкий" },
            _ => new[] { "Легендарный", "Очень редкий", "Артефакт" }
        };

        // ══════════════════════ ЗАПОЛНЕНИЕ ══════════════════════
        /// <summary>
        /// При первом открытии справочник пуст — заполняем его простым набором,
        /// чтобы генератор сразу что-то давал. Всё это можно удалить и заменить своим.
        /// </summary>
        public static void EnsureSeeded(int characterId)
        {
            if (characterId <= 0) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var check = new SqliteCommand(
                           "SELECT COUNT(*) FROM LootCategory WHERE id_character=@id", conn))
                {
                    check.Parameters.AddWithValue("@id", characterId);
                    var count = Convert.ToInt32(check.ExecuteScalar() ?? 0);
                    if (count > 0) return;
                }

                var seed = new (string Category, string[] Items)[]
                {
                    ("Оружие", new[] { "Кинжал", "Короткий меч", "Боевой топор", "Копьё", "Арбалет" }),
                    ("Доспехи", new[] { "Кожаный доспех", "Кольчуга", "Щит", "Стальной нагрудник" }),
                    ("Зелья и свитки", new[] { "Зелье лечения", "Свиток свет", "Противоядие", "Свиток щита" }),
                    ("Безделушки", new[] { "Странная монета", "Кости", "Ржавый ключ", "Обрывок письма" }),
                    ("Драгоценности", new[] { "Серебряное кольцо", "Малая жемчужина", "Гранат", "Статуэтка из кости" }),
                    ("Прочее", new[] { "Верёвка 50 фт", "Факел", "Мешок", "Трутница" })
                };

                foreach (var (categoryName, items) in seed)
                {
                    int categoryId;
                    using (var insert = new SqliteCommand(
                               "INSERT INTO LootCategory (id_character, name, sort_order) VALUES (@char, @name, 0); " +
                               "SELECT last_insert_rowid();", conn))
                    {
                        insert.Parameters.AddWithValue("@char", characterId);
                        insert.Parameters.AddWithValue("@name", categoryName);
                        categoryId = Convert.ToInt32(insert.ExecuteScalar());
                    }

                    foreach (string itemName in items)
                    {
                        using var insert = new SqliteCommand(
                            "INSERT INTO LootItem (id_loot_category, name, rarity, qty_min, qty_max, note) " +
                            "VALUES (@cat, @name, '', 1, 1, '');", conn);
                        insert.Parameters.AddWithValue("@cat", categoryId);
                        insert.Parameters.AddWithValue("@name", itemName);
                        insert.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LootService] Seed: {ex.Message}");
            }
        }
    }
}
