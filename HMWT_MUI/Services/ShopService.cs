using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HMWT_MUI.Services
{
    /// <summary>Параметры фильтрации каталога магазина.</summary>
    public sealed class ShopFilter
    {
        public string Search { get; set; } = "";
        public string ItemType { get; set; } = "";      // пусто = все
        public string Rarity { get; set; } = "";         // пусто = все
        public bool OnlyAttunement { get; set; }         // только требующие настройки
        public bool OnlyWithStats { get; set; }          // только с уроном или КД

        /// <summary>
        /// Только предметы, созданные вручную.
        ///
        /// Нужен обязательно: в каталоге справочника десятки тысяч записей,
        /// а список показывает первые 400 по имени. Свой предмет сохранялся,
        /// но в список не попадал — выглядело как «создание не работает».
        /// </summary>
        public bool OnlyCustom { get; set; }

        public string SortBy { get; set; } = "name";     // name | weight | rarity | type
    }

    /// <summary>
    /// Вид фото предмета каталога: крупное для карточки или мелкое для строки списка.
    ///
    /// Пользователь просил разделить их: в списке картинка 44×44, в карточке крупная,
    /// и это разные файлы, а не одна картинка в двух размерах.
    /// </summary>
    public static class ShopPhotoKind
    {
        public const string Photo = "photo";
        public const string ListPhoto = "list";
    }

    /// <summary>
    /// Каталог предметов для вкладки «Магазин»: чтение SrdItem, фильтры,
    /// сортировка, покупка (добавление в инвентарь) и фото предметов каталога.
    ///
    /// Фото магазина лежат в %APPDATA%\HMWT\Items как shop_<id>.ext и НЕ связаны
    /// с фото в инвентаре: у инвентаря своя запись и свой файл item_<...>.ext.
    /// Поэтому смена фото в одном месте не влияет на другое.
    /// </summary>
    public sealed class ShopService
    {
        private readonly int _characterId;
        private readonly InventoryService _inventory;

        public ShopService(int characterId)
        {
            _characterId = characterId;
            _inventory = new InventoryService(characterId);
        }

        private const string ItemsFolder = "Items";

        /// <summary>Префикс файла крупного фото (карточка предмета).</summary>
        private const string PhotoPrefix = "shop_";

        /// <summary>Префикс файла фото для строки списка.</summary>
        private const string ListPhotoPrefix = "shoplist_";

        #region === ЧТЕНИЕ КАТАЛОГА ===

        /// <summary>Загружает отфильтрованный список каталога.</summary>
        /// <summary>
        /// Текст последней ошибки выборки каталога. Нужен для диагностики:
        /// список может быть пустым и из-за ошибки SQL, и из-за отсутствия
        /// совпадений — по одному пустому списку это не различить.
        /// </summary>
        public static string LastLoadError { get; private set; } = "";



        public List<SrdItemRow> LoadCatalog(ShopFilter filter, int limit = 400)
        {
            var result = new List<SrdItemRow>();
            filter ??= new ShopFilter();
            LastLoadError = "";

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                var sql = new StringBuilder(@"
                    SELECT id_srd_item, name, description, item_type, subtype, rarity, weight,
                           damage, damage_type, ac, range, properties_text, attunement,
                           is_magic, source, book, COALESCE(photo_path,'') AS photo_path,
                           COALESCE(list_photo_path,'') AS list_photo_path");
                var args = new List<(string, object)>();

                // ── Поиск по названию ──
                //
                // Пользователь писал: вводит «potion of healing» и получает два десятка
                // результатов, где нужное зелье где-то в середине. Причина была в
                // поиске целой строкой: «potion of healing» как одна подстрока почти
                // ни с чем не совпадает, а совпадения по описанию идут вперемешку.
                //
                // Теперь каждое слово ищется отдельно и все слова обязаны найтись.
                // Точное совпадение по названию поднимается наверх, затем совпадения
                // по началу названия, затем всё остальное.
                bool hasSearch = !string.IsNullOrWhiteSpace(filter.Search);
                int wordCount = 0;
                var wordVariants = new List<List<string>>();
                if (hasSearch)
                {
                    var words = filter.Search
                        .Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => w.Trim())
                        .Where(w => w.Length > 0)
                        .Take(6)
                        .ToList();
                    wordCount = words.Count;
                    foreach (var w in words) wordVariants.Add(CaseVariants(w));

                    sql.Append(@",
                           CASE
                             WHEN lower(name) = lower(@whole) THEN 0
                             WHEN lower(name) LIKE lower(@wholePrefix) THEN 1");

                    for (int i = 0; i < words.Count; i++)
                        sql.Append($" WHEN lower(name) LIKE lower(@w{i}_0) THEN 2");

                    sql.Append(" ELSE 3 END AS relevance");

                    args.Add(("@whole", filter.Search.Trim()));
                    args.Add(("@wholePrefix", filter.Search.Trim() + "%"));
                }
                else
                {
                    sql.Append(", 0 AS relevance");
                }

                sql.Append(" FROM SrdItem WHERE 1=1");

                if (hasSearch)
                {
                    // Все слова обязаны найтись — в названии или в описании.
                    // Ищем по описанию тоже: у части предметов нужное слово
                    // встречается только там.
                    //
                    // Условия берём по номерам слов, а не перебором параметров:
                    // у @whole и @wholePrefix тоже есть префикс «@w», и они
                    // попадали сюда как обязательные совпадения — из-за этого
                    // поиск по одному слову не находил ничего.
                    // Ищем и по типу предмета: игрок часто ищет «зелье» или
                    // «оружие», а сами слова есть только в колонке типа
                    for (int i = 0; i < wordCount; i++)
                    {
                        // Регистр проверяем вариантами написания: SQLite умеет
                        // регистронезависимый LIKE только для латиницы, а «зелье»
                        // и «Зелье» для него разные слова
                        var variants = wordVariants[i];
                        var parts = new List<string>();

                        for (int v = 0; v < variants.Count; v++)
                        {
                            string p = $"@w{i}_{v}";
                            args.Add((p, "%" + variants[v] + "%"));
                            parts.Add($"name LIKE {p} OR description LIKE {p} " +
                                      $"OR item_type LIKE {p} OR subtype LIKE {p}");
                        }

                        sql.Append(" AND (" + string.Join(" OR ", parts) + ")");
                    }
                }

                if (!string.IsNullOrWhiteSpace(filter.ItemType))
                {
                    sql.Append(" AND item_type = @type");
                    args.Add(("@type", filter.ItemType));
                }

                if (!string.IsNullOrWhiteSpace(filter.Rarity))
                {
                    sql.Append(" AND rarity = @rar");
                    args.Add(("@rar", filter.Rarity));
                }

                if (filter.OnlyAttunement)
                    sql.Append(" AND attunement = 1");

                if (filter.OnlyWithStats)
                    sql.Append(" AND (COALESCE(damage,'') <> '' OR ac > 0)");

                // Только свои предметы.
                //
                // Нужно обязательно: каталог справочника содержит десятки тысяч
                // записей, а список показывает первые 400 по имени. Свой предмет
                // сохранялся, но в список не попадал, и выглядело это как
                // «создание не работает».
                if (filter.OnlyCustom)
                    sql.Append(" AND book = @customBook");

                // При поиске сначала идут самые подходящие по названию,
                // и только потом выбранная сортировка
                string relevancePrefix = hasSearch ? "relevance, " : "";

                switch ((filter.SortBy ?? "name").ToLowerInvariant())
                {
                    case "weight": sql.Append($" ORDER BY {relevancePrefix}weight DESC, name"); break;
                    case "rarity": sql.Append($" ORDER BY {relevancePrefix}rarity, name"); break;
                    case "type": sql.Append($" ORDER BY {relevancePrefix}item_type, name"); break;
                    default: sql.Append($" ORDER BY {relevancePrefix}name"); break;
                }

                sql.Append(" LIMIT " + Math.Max(1, limit));

                using var cmd = new SqliteCommand(sql.ToString(), conn);
                foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
                if (filter.OnlyCustom) cmd.Parameters.AddWithValue("@customBook", SrdItemImporter.CustomBook);

                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(ReadRow(reader));
            }
            catch (Exception ex)
            {
                LastLoadError = ex.GetType().Name + ": " + ex.Message;
                Debug.WriteLine($"[ShopService] LoadCatalog: {ex}");
            }

            return result;
        }

        /// <summary>
        /// Варианты написания слова для регистронезависимого поиска.
        ///
        /// SQLite приводит регистр только для латиницы, поэтому «зелье» и «Зелье»
        /// для неё разные строки. Перебираем распространённые написания: как
        /// ввёл игрок, всё маленькими и всё большими.
        /// </summary>
        private static List<string> CaseVariants(string word)
        {
            var list = new List<string> { word };

            string lower = word.ToLowerInvariant();
            string upper = word.ToUpperInvariant();

            if (lower != word) list.Add(lower);
            if (upper != word && upper != lower) list.Add(upper);

            // Первая буква заглавная: «Зелье» при вводе «зелье»
            if (word.Length > 1)
            {
                string capitalized = char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant();
                if (!list.Contains(capitalized)) list.Add(capitalized);
            }

            return list;
        }

        /// <summary>Сколько всего записей подходит под фильтр (без LIMIT).</summary>
        public int CountCatalog(ShopFilter filter)
        {
            filter ??= new ShopFilter();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                var sql = new StringBuilder("SELECT COUNT(1) FROM SrdItem WHERE 1=1");
                var args = new List<(string, object)>();

                if (!string.IsNullOrWhiteSpace(filter.Search))
                {
                    sql.Append(" AND (name LIKE @q OR description LIKE @q)");
                    args.Add(("@q", "%" + filter.Search.Trim() + "%"));
                }
                if (!string.IsNullOrWhiteSpace(filter.ItemType))
                {
                    sql.Append(" AND item_type = @type");
                    args.Add(("@type", filter.ItemType));
                }
                if (!string.IsNullOrWhiteSpace(filter.Rarity))
                {
                    sql.Append(" AND rarity = @rar");
                    args.Add(("@rar", filter.Rarity));
                }
                if (filter.OnlyAttunement) sql.Append(" AND attunement = 1");
                if (filter.OnlyWithStats) sql.Append(" AND (COALESCE(damage,'') <> '' OR ac > 0)");

                using var cmd = new SqliteCommand(sql.ToString(), conn);
                foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] CountCatalog: {ex.Message}");
                return 0;
            }
        }

        public SrdItemRow GetItem(int idSrdItem)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT id_srd_item, name, description, item_type, subtype, rarity, weight,
                           damage, damage_type, ac, range, properties_text, attunement,
                           is_magic, source, book, COALESCE(photo_path,'') AS photo_path,
                           COALESCE(list_photo_path,'') AS list_photo_path
                    FROM SrdItem WHERE id_srd_item = @id", conn);
                cmd.Parameters.AddWithValue("@id", idSrdItem);
                using var reader = cmd.ExecuteReader();
                if (reader.Read()) return ReadRow(reader);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] GetItem: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Список типов предметов для выпадающего фильтра (по частоте).
        ///
        /// Длинные значения отбрасываем: в некоторых сторонних справочниках
        /// описание попало в колонку типа, и такой «тип» растягивал выпадающий
        /// список на весь экран (одна строка была 820 символов). Настоящий тип
        /// предмета — всегда короткое слово.
        /// </summary>
        public List<string> LoadItemTypes()
        {
            var list = new List<string>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT item_type, COUNT(1) AS c FROM SrdItem
                    WHERE COALESCE(item_type,'') <> '' AND length(item_type) <= 40
                    GROUP BY item_type ORDER BY c DESC, item_type", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string t = reader.GetValue(0)?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(t)) list.Add(t);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] LoadItemTypes: {ex.Message}");
            }
            return list;
        }

        /// <summary>Список редкостей в фиксированном порядке (от обычных к артефактам).</summary>
        public List<string> LoadRarities()
        {
            var order = new[] { "Обычный", "Необычный", "Редкий", "Очень редкий", "Легендарный", "Артефакт" };
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT DISTINCT rarity FROM SrdItem WHERE COALESCE(rarity,'') <> ''", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) present.Add(reader.GetValue(0)?.ToString() ?? "");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] LoadRarities: {ex.Message}");
            }

            var list = order.Where(present.Contains).ToList();
            // Нестандартные редкости (Fabled, Epic, Разное) — в конец
            list.AddRange(present.Where(p => !order.Contains(p, StringComparer.OrdinalIgnoreCase))
                                 .OrderBy(p => p));
            return list;
        }

        private static SrdItemRow ReadRow(SqliteDataReader r)
        {
            return new SrdItemRow
            {
                Id = ReadInt(r, "id_srd_item"),
                Name = ReadText(r, "name"),
                Description = ReadText(r, "description"),
                ItemType = ReadText(r, "item_type"),
                Subtype = ReadText(r, "subtype"),
                Rarity = ReadText(r, "rarity"),
                Weight = ReadDouble(r, "weight"),
                Damage = ReadText(r, "damage"),
                DamageType = ReadText(r, "damage_type"),
                Ac = ReadInt(r, "ac"),
                Range = ReadText(r, "range"),
                PropertiesText = ReadText(r, "properties_text"),
                Attunement = ReadInt(r, "attunement") == 1,
                IsMagic = ReadInt(r, "is_magic") == 1,
                Source = ReadText(r, "source"),
                Book = ReadText(r, "book"),
                PhotoPath = ReadText(r, "photo_path"),
                ListPhotoPath = ReadText(r, "list_photo_path")
            };
        }

        private static string ReadText(SqliteDataReader r, string column)
        {
            try
            {
                int i = r.GetOrdinal(column);
                return r.IsDBNull(i) ? "" : r.GetValue(i)?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static int ReadInt(SqliteDataReader r, string column)
        {
            var text = ReadText(r, column);
            return int.TryParse(text, out int v) ? v : 0;
        }

        private static double ReadDouble(SqliteDataReader r, string column)
        {
            var text = ReadText(r, column);
            return double.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        #endregion

        #region === ПОКУПКА ===

        /// <summary>
        /// Добавляет предмет каталога в инвентарь персонажа.
        /// Цены здесь нет — деньги игрок списывает сам, как договорились с DM.
        /// Возвращает id созданной записи инвентаря либо 0.
        /// </summary>
        /// <summary>
        /// Покупка: предмет уходит в инвентарь.
        ///
        /// Одинаковые предметы СТАКИВАЮТСЯ: если такой уже лежит, увеличивается
        /// только количество, а не появляется новая карточка в списке. Сравнение
        /// идёт по названию, категории, редкости, кости и типу урона, бонусу КД,
        /// весу и описанию — если отличается хоть что-то, это другой предмет.
        /// </summary>
        /// <returns>Идентификатор записи в инвентаре либо 0.</returns>
        public int BuyItem(SrdItemRow item, int quantity = 1)
        {
            if (item == null) return 0;
            try
            {
                var data = new ItemData
                {
                    Name = item.Name,
                    Category = string.IsNullOrWhiteSpace(item.ItemType) ? "Прочее" : item.ItemType,
                    Rarity = item.Rarity ?? "",
                    Description = BuildInventoryDescription(item),
                    Quantity = Math.Max(1, quantity),
                    Weight = (decimal)item.Weight,
                    DamageDice = item.Damage ?? "",
                    DamageType = item.DamageType ?? "",
                    ArmorClassBonus = item.Ac,   // КД из каталога переносим как бонус
                    IsAttuned = false,
                    Notes = "",
                    // Фото каталога НЕ копируем: у инвентаря своё фото, иначе получился бы
                    // тот самый конфликт «поменял в инвентаре — поменялось в магазине»
                    PhotoPath = ""
                };

                var result = _inventory.AddOrStack(data);
                if (result.Stacked)
                    Debug.WriteLine($"[ShopService] «{item.Name}» стакнут: стало {result.Quantity}");

                return result.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] BuyItem: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Описание для инвентаря: официальный текст + строка характеристик каталога.</summary>
        private static string BuildInventoryDescription(SrdItemRow item)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(item.Description)) sb.Append(item.Description.Trim());

            var stats = item.StatsLine;
            if (!string.IsNullOrWhiteSpace(stats))
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("Характеристики: ").Append(stats);
            }
            return sb.ToString();
        }

        #endregion

        #region === ФОТО ПРЕДМЕТОВ КАТАЛОГА ===

        /// <summary>
        /// Сохраняет фото/гифку для предмета каталога. Файл кладётся в
        /// %APPDATA%\HMWT\Items как shop_<id>.<ext> — тем же способом, что и фото
        /// предметов инвентаря, поэтому ResolvePhotoPath находит его без доработок.
        /// Прежний файл этого предмета удаляется, чтобы не копить мусор.
        /// </summary>
        public string SaveShopPhoto(int srdItemId, string sourceFilePath, string photoKind = ShopPhotoKind.Photo)
        {
            if (srdItemId <= 0 || string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return null;

            try
            {
                string extension = Path.GetExtension(sourceFilePath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), ItemsFolder);
                Directory.CreateDirectory(dir);

                bool forList = photoKind == ShopPhotoKind.ListPhoto;
                string column = forList ? "list_photo_path" : "photo_path";
                string prefix = forList ? ListPhotoPrefix : PhotoPrefix;

                // Убираем прежний файл этого вида (мог быть другого расширения)
                DeleteShopPhotoFiles(srdItemId, photoKind);

                string fileName = $"{prefix}{srdItemId}{extension.ToLowerInvariant()}";
                string destPath = Path.Combine(dir, fileName);
                File.Copy(sourceFilePath, destPath, true);

                string relative = $"{ItemsFolder}/{fileName}";
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    $"UPDATE SrdItem SET {column}=@p WHERE id_srd_item=@id", conn);
                cmd.Parameters.AddWithValue("@p", relative);
                cmd.Parameters.AddWithValue("@id", srdItemId);
                cmd.ExecuteNonQuery();

                Debug.WriteLine($"[ShopService] фото предмета каталога сохранено: {destPath}");
                return relative;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] SaveShopPhoto: {ex.Message}");
                return null;
            }
        }

        /// <summary>Удаляет фото предмета каталога (файл + ссылку в базе).</summary>
        public void ClearShopPhoto(int srdItemId, string photoKind = ShopPhotoKind.Photo)
        {
            try
            {
                bool forList = photoKind == ShopPhotoKind.ListPhoto;
                string column = forList ? "list_photo_path" : "photo_path";
                DeleteShopPhotoFiles(srdItemId, photoKind);
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    $"UPDATE SrdItem SET {column}=NULL WHERE id_srd_item=@id", conn);
                cmd.Parameters.AddWithValue("@id", srdItemId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] ClearShopPhoto: {ex.Message}");
            }
        }

        /// <summary>Удаляет все файлы shop_<id>.* из папки предметов.</summary>
        private static void DeleteShopPhotoFiles(int srdItemId, string photoKind = ShopPhotoKind.Photo)
        {
            try
            {
                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), ItemsFolder);
                if (!Directory.Exists(dir)) return;
                string prefix = photoKind == ShopPhotoKind.ListPhoto ? ListPhotoPrefix : PhotoPrefix;
                foreach (var file in Directory.GetFiles(dir, $"{prefix}{srdItemId}.*"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] DeleteShopPhotoFiles: {ex.Message}");
            }
        }

        #endregion

        #region === УБОРКА НЕИСПОЛЬЗУЕМЫХ ФОТО ===

        /// <summary>
        /// Удаляет из папки Items файлы, на которые не ссылается ни инвентарь,
        /// ни каталог магазина. Такой мусор копится при смене фото предмета.
        ///
        /// recentMinutes: файлы моложе указанного времени не трогаем — чтобы не
        /// удалить только что сохранённое фото, ещё не записанное в базу.
        /// </summary>
        public static int CleanupUnusedItemPhotos(int recentMinutes = 30)
        {
            int removed = 0;
            try
            {
                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), ItemsFolder);
                if (!Directory.Exists(dir)) return 0;

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();

                    using (var cmd = new SqliteCommand(
                        "SELECT PhotoPath FROM Inventory WHERE COALESCE(PhotoPath,'') <> ''", conn))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) AddUsed(used, r.GetValue(0)?.ToString());

                    using (var cmd = new SqliteCommand(
                        "SELECT photo_path FROM SrdItem WHERE COALESCE(photo_path,'') <> ''", conn))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) AddUsed(used, r.GetValue(0)?.ToString());

                    using (var cmd = new SqliteCommand(
                        "SELECT list_photo_path FROM SrdItem WHERE COALESCE(list_photo_path,'') <> ''", conn))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) AddUsed(used, r.GetValue(0)?.ToString());
                }

                var cutoff = DateTime.Now.AddMinutes(-Math.Abs(recentMinutes));

                foreach (var file in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    if (used.Contains(name)) continue;

                    try
                    {
                        if (File.GetLastWriteTime(file) > cutoff) continue;  // слишком свежий
                        File.Delete(file);
                        removed++;
                        Debug.WriteLine($"[ShopService] удалён неиспользуемый файл: {name}");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShopService] CleanupUnusedItemPhotos: {ex.Message}");
            }

            return removed;
        }

        private static void AddUsed(HashSet<string> set, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                // В базе путь относительный вида "Items/файл" — нам нужно имя файла
                string name = Path.GetFileName(path.Replace('\\', '/'));
                if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
            }
            catch { }
        }

        #endregion
    }
}
