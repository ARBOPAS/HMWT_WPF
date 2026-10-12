using HMWT_MUI.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Атаки персонажа: сбор из предметов и заклинаний, ручные записи, расчёт попадания.
    ///
    /// Раньше урон оружия жил в предмете, а урон заклинания — в карточке заклинания,
    /// и чтобы получить два типа урона, приходилось заводить второй предмет.
    /// Теперь всё сводится в одну запись атаки со списком частей урона, а источник
    /// (предмет или заклинание) лишь подсказывает значения по умолчанию.
    /// </summary>
    public static class AttackService
    {
        private static readonly JsonSerializerOptions PartJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private static bool _tablesChecked;

        /// <summary>
        /// Последняя ошибка сбора атак. Нужна, чтобы неудача не оставалась
        /// молчаливой: сбор возвращает 0 и раньше никто не знал почему.
        /// </summary>
        public static string LastError { get; private set; } = "";

        /// <summary>Сколько атак убрано при последнем сборе: заклинание перестало быть подготовленным.</summary>
        public static int LastRemovedSpells { get; private set; }

        #region === СХЕМА ===

        public static void EnsureTables()
        {
            if (_tablesChecked) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    CREATE TABLE IF NOT EXISTS CharacterAttacks (
                        id_attack INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_character INTEGER NOT NULL,
                        name TEXT NOT NULL DEFAULT '',
                        source TEXT DEFAULT 'manual',
                        source_id INTEGER DEFAULT 0,
                        section TEXT DEFAULT 'action',
                        auto_bonus INTEGER DEFAULT 1,
                        ability TEXT DEFAULT 'str',
                        proficient INTEGER DEFAULT 1,
                        attack_bonus INTEGER DEFAULT 0,
                        damage_ability TEXT DEFAULT 'str',
                        reach TEXT DEFAULT '',
                        attack_range TEXT DEFAULT '',
                        properties TEXT DEFAULT '',
                        note TEXT DEFAULT '',
                        damage_parts TEXT DEFAULT '',
                        child_ids TEXT DEFAULT '',
                        sort_order INTEGER DEFAULT 0,
                        created_at TEXT DEFAULT '',
                        updated_at TEXT DEFAULT '',
                        FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS idx_attacks_char ON CharacterAttacks(id_character);", conn);
                cmd.ExecuteNonQuery();

                // Мультиатака: список атак серии в JSON. Колонка добавлена позже,
                // поэтому у старых баз её нет — доводим схему здесь
                using (var tx = conn.BeginTransaction())
                {
                    DatabaseHelper.EnsureColumn(conn, tx, "CharacterAttacks", "child_ids TEXT DEFAULT ''");
                    tx.Commit();
                }

                _tablesChecked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] EnsureTables error: {ex.Message}");
            }
        }

        public static void ResetSchemaCache() => _tablesChecked = false;

        #endregion

        #region === ЧТЕНИЕ И ЗАПИСЬ ===

        public static List<AttackData> LoadAll(int characterId)
        {
            var result = new List<AttackData>();
            if (characterId <= 0) return result;
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT * FROM CharacterAttacks WHERE id_character = @id ORDER BY sort_order, id_attack", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(ReadAttack(reader));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] LoadAll error: {ex.Message}");
            }

            return result;
        }

        public static AttackData Load(int id)
        {
            if (id <= 0) return null;
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT * FROM CharacterAttacks WHERE id_attack = @id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (reader.Read()) return ReadAttack(reader);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] Load error: {ex.Message}");
            }

            return null;
        }

        public static int Save(AttackData attack)
        {
            if (attack == null) return 0;
            if (!attack.Validate(out string error))
            {
                Debug.WriteLine($"[AttackService] Save отклонён: {error}");
                return 0;
            }

            EnsureTables();
            attack.Name = attack.Name.Trim();
            attack.Source = AttackSources.Normalize(attack.Source);
            attack.Section = AttackSections.Normalize(attack.Section);
            attack.Ability = AttackAbilities.Normalize(attack.Ability);
            attack.DamageAbility = AttackAbilities.Normalize(attack.DamageAbility);
            attack.UpdatedAt = DateTime.Now;
            if (attack.CreatedAt == default(DateTime)) attack.CreatedAt = DateTime.Now;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                string sql = attack.Id > 0
                    ? @"UPDATE CharacterAttacks SET name=@name, source=@source, source_id=@sourceId,
                              section=@section, auto_bonus=@auto, ability=@ability, proficient=@prof,
                              attack_bonus=@bonus, damage_ability=@dmgAbility, reach=@reach,
                              attack_range=@range, properties=@props, note=@note,
                              damage_parts=@parts, child_ids=@children, sort_order=@order, updated_at=@updated
                        WHERE id_attack=@id"
                    : @"INSERT INTO CharacterAttacks (id_character, name, source, source_id, section,
                              auto_bonus, ability, proficient, attack_bonus, damage_ability, reach,
                              attack_range, properties, note, damage_parts, child_ids, sort_order, created_at, updated_at)
                        VALUES (@char, @name, @source, @sourceId, @section, @auto, @ability, @prof,
                              @bonus, @dmgAbility, @reach, @range, @props, @note, @parts, @children, @order, @created, @updated)";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@char", attack.CharacterId);
                cmd.Parameters.AddWithValue("@name", attack.Name);
                cmd.Parameters.AddWithValue("@source", attack.Source);
                cmd.Parameters.AddWithValue("@sourceId", attack.SourceId);
                cmd.Parameters.AddWithValue("@section", attack.Section);
                cmd.Parameters.AddWithValue("@auto", attack.AutoBonus ? 1 : 0);
                cmd.Parameters.AddWithValue("@ability", attack.Ability);
                cmd.Parameters.AddWithValue("@prof", attack.Proficient ? 1 : 0);
                cmd.Parameters.AddWithValue("@bonus", attack.AttackBonus);
                cmd.Parameters.AddWithValue("@dmgAbility", attack.DamageAbility);
                cmd.Parameters.AddWithValue("@reach", attack.Reach ?? "");
                cmd.Parameters.AddWithValue("@range", attack.Range ?? "");
                cmd.Parameters.AddWithValue("@props", attack.Properties ?? "");
                cmd.Parameters.AddWithValue("@note", attack.Note ?? "");
                cmd.Parameters.AddWithValue("@parts", SerializeParts(attack.DamageParts));
                cmd.Parameters.AddWithValue("@children", SerializeChildIds(attack.ChildIds));
                cmd.Parameters.AddWithValue("@order", attack.SortOrder);
                cmd.Parameters.AddWithValue("@created", attack.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@updated", attack.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                if (attack.Id > 0) cmd.Parameters.AddWithValue("@id", attack.Id);
                cmd.ExecuteNonQuery();

                if (attack.Id <= 0)
                {
                    using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn);
                    attack.Id = Convert.ToInt32(idCmd.ExecuteScalar());
                }

                return attack.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] Save error: {ex.Message}");
                return 0;
            }
        }

        public static bool Delete(int id)
        {
            if (id <= 0) return false;
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM CharacterAttacks WHERE id_attack = @id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                bool deleted = cmd.ExecuteNonQuery() > 0;

                // Удалённая атака могла входить в чьи-то серии: убираем ссылки,
                // иначе в мультиатаке останется «призрак»
                if (deleted) RemoveFromSeries(conn, id);

                return deleted;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] Delete error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Убирает ссылку на удалённую атаку из всех серий персонажа.</summary>
        private static void RemoveFromSeries(SqliteConnection conn, int childId)
        {
            try
            {
                var series = new List<(int Id, List<int> Children)>();

                using (var read = new SqliteCommand(
                    "SELECT id_attack, child_ids FROM CharacterAttacks WHERE child_ids LIKE @mask", conn))
                {
                    read.Parameters.AddWithValue("@mask", "%" + childId + "%");
                    using var reader = read.ExecuteReader();
                    while (reader.Read())
                    {
                        int id = ReadInt(reader, "id_attack");
                        var children = DeserializeChildIds(ReadText(reader, "child_ids"));
                        if (children.Contains(childId)) series.Add((id, children));
                    }
                }

                foreach (var item in series)
                {
                    item.Children.Remove(childId);
                    using var update = new SqliteCommand(
                        "UPDATE CharacterAttacks SET child_ids=@children WHERE id_attack=@id", conn);
                    update.Parameters.AddWithValue("@children", SerializeChildIds(item.Children));
                    update.Parameters.AddWithValue("@id", item.Id);
                    update.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] RemoveFromSeries: {ex.Message}");
            }
        }

        /// <summary>Убирает атаки, собранные из указанного источника (предмет удалён и т.п.).</summary>
        public static int DeleteBySource(int characterId, string source, int sourceId)
        {
            if (characterId <= 0 || sourceId <= 0) return 0;
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "DELETE FROM CharacterAttacks WHERE id_character=@char AND source=@src AND source_id=@sid", conn);
                cmd.Parameters.AddWithValue("@char", characterId);
                cmd.Parameters.AddWithValue("@src", AttackSources.Normalize(source));
                cmd.Parameters.AddWithValue("@sid", sourceId);
                return cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] DeleteBySource error: {ex.Message}");
                return 0;
            }
        }

        public static int Count(int characterId)
        {
            EnsureTables();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT COUNT(*) FROM CharacterAttacks WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] Count error: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region === РАСЧЁТ ПОПАДАНИЯ ===

        /// <summary>Модификатор характеристики по значению.</summary>
        public static int AbilityModifier(int score) => (int)Math.Floor((score - 10) / 2.0);

        /// <summary>Бонус мастерства персонажа: из листа, иначе по уровню.</summary>
        public static int ProficiencyBonus(int characterId)
        {
            int level = 1, stored = 0;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var cmd = new SqliteCommand("SELECT MasterLVL FROM CharacterInfo WHERE id_character=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", characterId);
                    var value = cmd.ExecuteScalar();
                    if (value != null && value != DBNull.Value) level = Math.Max(1, Convert.ToInt32(value));
                }

                using (var cmd = new SqliteCommand("SELECT ProficiencyBonus FROM LevelTable WHERE id_character=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", characterId);
                    var value = cmd.ExecuteScalar();
                    if (value != null && value != DBNull.Value) stored = Convert.ToInt32(value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] ProficiencyBonus: {ex.Message}");
            }

            // В листе мог быть выставлен вручную — он важнее расчёта
            if (stored > 0) return stored;
            return 2 + (Math.Max(1, level) - 1) / 4;
        }

        /// <summary>Значение характеристики персонажа.</summary>
        public static int AbilityScore(int characterId, string ability)
        {
            string column = ColumnFor(ability);
            if (column.Length == 0) return 10;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand($"SELECT {column} FROM CharacterInfo WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                var value = cmd.ExecuteScalar();
                if (value != null && value != DBNull.Value) return Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] AbilityScore: {ex.Message}");
            }

            return 10;
        }

        private static string ColumnFor(string ability)
        {
            switch (AttackAbilities.Normalize(ability))
            {
                case "str": return "Strength";
                case "dex": return "Dexterity";
                case "con": return "Constitution";
                case "int": return "Intelligence";
                case "wis": return "Wisdom";
                case "cha": return "Charisma";
                default: return "";
            }
        }

        /// <summary>Пересчитывает попадание всем атакам персонажа и сохраняет изменения.</summary>
        public static int Recalculate(List<AttackData> attacks, int proficiencyBonus)
        {
            if (attacks == null) return 0;
            int changed = 0;

            foreach (var attack in attacks)
            {
                if (attack == null || !attack.AutoBonus) continue;

                // У серии своего попадания нет: она состоит из атак, и у каждой своё
                if (attack.IsMulti)
                {
                    if (attack.AttackBonus == 0) continue;
                    attack.AttackBonus = 0;
                    changed++;
                    continue;
                }

                int score = AbilityScore(attack.CharacterId, attack.Ability);
                int bonus = AbilityModifier(score) + (attack.Proficient ? proficiencyBonus : 0);
                if (bonus == attack.AttackBonus) continue;

                attack.AttackBonus = bonus;
                changed++;
            }

            foreach (var attack in attacks.Where(a => a != null && a.AutoBonus)) Save(attack);
            return changed;
        }

        /// <summary>Добавка к урону от характеристики (или 0, если добавки нет).</summary>
        public static int DamageBonus(AttackData attack)
        {
            if (attack == null) return 0;
            if (string.Equals(AttackAbilities.Normalize(attack.DamageAbility), AttackAbilities.None, StringComparison.OrdinalIgnoreCase))
                return 0;

            return AbilityModifier(AbilityScore(attack.CharacterId, attack.DamageAbility));
        }

        #endregion

        #region === СБОР ИЗ ПРЕДМЕТОВ И ЗАКЛИНАНИЙ ===

        /// <summary>
        /// Добавляет атаки по оружию из инвентаря. Уже собранные не трогаем:
        /// игрок мог их переименовать и дописать свои части урона.
        /// </summary>
        public static int CollectFromInventory(int characterId) => CollectInventoryWeapons(characterId, 0);

        /// <summary>
        /// Переносит во вкладку «Атаки» одно оружие из инвентаря.
        ///
        /// Нужно кнопке «В атаки» в предмете: урон оружия бросается в одном
        /// месте, а не отдельной кнопкой в инвентаре.
        /// </summary>
        public static int CollectOneFromInventory(int characterId, int itemId)
        {
            if (characterId <= 0 || itemId <= 0) return 0;
            return CollectInventoryWeapons(characterId, itemId);
        }

        private static int CollectInventoryWeapons(int characterId, int onlyItemId)
        {
            EnsureTables();
            LastError = "";
            int added = 0;
            try
            {
                var existing = new HashSet<int>(LoadAll(characterId)
                    .Where(a => a.Source == AttackSources.Inventory)
                    .Select(a => a.SourceId));

                // Сначала читаем предметы в список и закрываем соединение, и только
                // потом пишем атаки. Если писать, пока открыт читатель, SQLite
                // отвечает «database is locked», и сбор молча ничего не добавлял.
                var weapons = new List<(int ItemId, string Name, string Dice, string Type, bool Worn)>();

                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using var cmd = new SqliteCommand(@"
                        SELECT i.id_item, i.name, i.damage_dice, i.damage_type, i.category,
                               CASE WHEN EXISTS (SELECT 1 FROM EquipmentCells ec WHERE ec.id_item = i.id_item)
                                    THEN 1 ELSE 0 END AS worn
                        FROM Inventory i
                        WHERE i.id_character = @id
                          AND (@itemId = 0 OR i.id_item = @itemId)
                          AND (LOWER(COALESCE(i.category,'')) LIKE '%оруж%'
                               OR LOWER(COALESCE(i.category,'')) LIKE '%weapon%'
                               OR TRIM(COALESCE(i.damage_dice,'')) <> '')
                        ORDER BY worn DESC, i.name COLLATE NOCASE", conn);
                    cmd.Parameters.AddWithValue("@id", characterId);
                    cmd.Parameters.AddWithValue("@itemId", onlyItemId);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        weapons.Add((
                            ReadInt(reader, "id_item"),
                            ReadText(reader, "name"),
                            ReadText(reader, "damage_dice"),
                            ReadText(reader, "damage_type"),
                            ReadInt(reader, "worn") != 0));
                    }
                }

                int order = LoadAll(characterId).Count;

                foreach (var weapon in weapons)
                {
                    if (existing.Contains(weapon.ItemId)) continue;

                    var attack = new AttackData
                    {
                        CharacterId = characterId,
                        Name = string.IsNullOrWhiteSpace(weapon.Name) ? "Оружие" : weapon.Name,
                        Source = AttackSources.Inventory,
                        SourceId = weapon.ItemId,
                        // Дальний бой считаем по Ловкости, ближний — по Силе
                        Ability = IsRanged(weapon.Type) ? "dex" : "str",
                        DamageAbility = IsRanged(weapon.Type) ? "dex" : "str",
                        Section = AttackSections.Action,
                        Note = weapon.Worn ? "надето" : "",
                        SortOrder = order++
                    };

                    string dice = (weapon.Dice ?? "").Trim();
                    if (dice.Length > 0)
                        attack.DamageParts.Add(new DamagePart { Formula = dice, DamageType = (weapon.Type ?? "").Trim() });

                    if (Save(attack) > 0) added++;
                }
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"[AttackService] CollectInventoryWeapons: {ex.Message}");
            }

            return added;
        }

        private static bool IsRanged(string category)
        {
            string text = (category ?? "").ToLowerInvariant();
            return text.Contains("дальн") || text.Contains("ranged") || text.Contains("лук") || text.Contains("арбалет");
        }

        /// <summary>Добавляет атаки по заклинаниям, у которых указан урон.</summary>
        public static int CollectFromSpells(int characterId)
        {
            EnsureTables();
            LastError = "";
            LastRemovedSpells = 0;
            int added = 0;

            try
            {
                var existing = new HashSet<int>(LoadAll(characterId)
                    .Where(a => a.Source == AttackSources.Spell)
                    .Select(a => a.SourceId));

                int prof = ProficiencyBonus(characterId);
                // Заклинательная характеристика по умолчанию — Харизма:
                // в настройках вкладки заклинаний она выбирается так же
                int castingMod = AbilityModifier(AbilityScore(characterId, "cha"));

                var (prepared, known) = SpellService.LoadSpells(characterId);

                // В атаки идут ТОЛЬКО подготовленные заклинания, а из известных —
                // одни заговоры: их готовить не нужно. Иначе теряется смысл
                // подготовки («на рассвете выбрал, чем пользуешься весь день»).
                var allowed = new HashSet<int>();
                var spells = new List<SpellData>();

                foreach (var spell in prepared)
                {
                    if (spell == null || spell.IdSpell <= 0) continue;
                    allowed.Add(spell.IdSpell);
                    spells.Add(spell);
                }

                foreach (var spell in known)
                {
                    if (spell == null || spell.IdSpell <= 0) continue;
                    if (spell.Level != 0) continue;          // заговор готовить не надо
                    if (!allowed.Add(spell.IdSpell)) continue;
                    spells.Add(spell);
                }

                // Заклинание перестало быть подготовленным (или его убрали из листа) —
                // атака по нему тоже уходит, иначе подготовка ничего не значит
                LastRemovedSpells = 0;
                foreach (var attack in LoadAll(characterId).Where(a => a.Source == AttackSources.Spell))
                {
                    if (attack.SourceId <= 0 || allowed.Contains(attack.SourceId)) continue;
                    if (Delete(attack.Id)) LastRemovedSpells++;
                }

                int order = LoadAll(characterId).Count;
                foreach (var spell in spells)
                {
                    if (spell == null || spell.IdSpell <= 0) continue;
                    if (existing.Contains(spell.IdSpell)) continue;
                    if (!spell.HasDamage) continue;

                    var attack = new AttackData
                    {
                        CharacterId = characterId,
                        Name = string.IsNullOrWhiteSpace(spell.Name) ? "Заклинание" : spell.Name,
                        Source = AttackSources.Spell,
                        SourceId = spell.IdSpell,
                        Section = AttackSections.Action,
                        Ability = "cha",
                        Proficient = true,
                        AttackBonus = prof + castingMod,
                        DamageAbility = AttackAbilities.None,   // у заклинаний мод. к урону не добавляется
                        Range = spell.Range ?? "",
                        Note = spell.Level == 0 ? "заговор" : $"{spell.Level} ур.",
                        SortOrder = order++
                    };

                    foreach (var part in spell.AllDamageParts())
                        attack.DamageParts.Add(part.Clone());

                    if (Save(attack) > 0) added++;
                }
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"[AttackService] CollectFromSpells: {ex.Message}");
            }

            return added;
        }

        #endregion

        #region === JSON И ЧТЕНИЕ СТРОКИ ===

        public static string SerializeParts(List<DamagePart> parts)
        {
            try
            {
                var clean = (parts ?? new List<DamagePart>())
                    .Where(p => p != null && !p.IsEmpty)
                    .ToList();

                return clean.Count == 0 ? "" : JsonSerializer.Serialize(clean, PartJsonOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] SerializeParts: {ex.Message}");
                return "";
            }
        }

        public static List<DamagePart> DeserializeParts(string json)
        {
            var result = new List<DamagePart>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                var parsed = JsonSerializer.Deserialize<List<DamagePart>>(json, PartJsonOptions);
                if (parsed != null)
                {
                    foreach (var part in parsed)
                        if (part != null && !part.IsEmpty) result.Add(part);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] DeserializeParts: {ex.Message}");
            }

            return result;
        }

        /// <summary>Список атак серии — в JSON. Пустая серия хранится как пустая строка.</summary>
        public static string SerializeChildIds(List<int> ids)
        {
            try
            {
                var clean = (ids ?? new List<int>()).Where(id => id > 0).Distinct().ToList();
                return clean.Count == 0 ? "" : JsonSerializer.Serialize(clean);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] SerializeChildIds: {ex.Message}");
                return "";
            }
        }

        /// <summary>Разбор серии. Испорченный JSON не роняет загрузку — серия просто пустая.</summary>
        public static List<int> DeserializeChildIds(string json)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                var parsed = JsonSerializer.Deserialize<List<int>>(json);
                if (parsed != null)
                    foreach (int id in parsed)
                        if (id > 0 && !result.Contains(id)) result.Add(id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttackService] DeserializeChildIds: {ex.Message}");
            }

            return result;
        }

        private static AttackData ReadAttack(SqliteDataReader reader)        {
            var attack = new AttackData
            {
                Id = ReadInt(reader, "id_attack"),
                CharacterId = ReadInt(reader, "id_character"),
                Name = ReadText(reader, "name"),
                Source = AttackSources.Normalize(ReadText(reader, "source")),
                SourceId = ReadInt(reader, "source_id"),
                Section = AttackSections.Normalize(ReadText(reader, "section")),
                AutoBonus = ReadInt(reader, "auto_bonus") != 0,
                Ability = AttackAbilities.Normalize(ReadText(reader, "ability")),
                Proficient = ReadInt(reader, "proficient") != 0,
                AttackBonus = ReadInt(reader, "attack_bonus"),
                DamageAbility = AttackAbilities.Normalize(ReadText(reader, "damage_ability")),
                Reach = ReadText(reader, "reach"),
                Range = ReadText(reader, "attack_range"),
                Properties = ReadText(reader, "properties"),
                Note = ReadText(reader, "note"),
                SortOrder = ReadInt(reader, "sort_order"),
                DamageParts = DeserializeParts(ReadText(reader, "damage_parts")),
                ChildIds = DeserializeChildIds(ReadText(reader, "child_ids"))
            };

            DateTime created = ParseDate(ReadText(reader, "created_at"));
            DateTime updated = ParseDate(ReadText(reader, "updated_at"));
            attack.CreatedAt = created == default(DateTime) ? DateTime.Now : created;
            attack.UpdatedAt = updated == default(DateTime) ? attack.CreatedAt : updated;
            return attack;
        }

        private static string ReadText(SqliteDataReader reader, string column)
        {
            try
            {
                int index = reader.GetOrdinal(column);
                return reader.IsDBNull(index) ? "" : (reader.GetValue(index)?.ToString() ?? "");
            }
            catch
            {
                return "";
            }
        }

        private static int ReadInt(SqliteDataReader reader, string column)
        {
            try
            {
                int index = reader.GetOrdinal(column);
                if (reader.IsDBNull(index)) return 0;
                return Convert.ToInt32(reader.GetValue(index));
            }
            catch
            {
                return 0;
            }
        }

        private static DateTime ParseDate(string text)
            => DateTime.TryParse(text, out DateTime value) ? value : default(DateTime);

        #endregion
    }
}
