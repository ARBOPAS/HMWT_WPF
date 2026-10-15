using HMWT_MUI.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Сервис для работы с заклинаниями персонажа.
    /// Все операции с БД проходят через этот класс.
    /// </summary>
    public static class SpellService
    {
        #region === ЗАГРУЗКА ЗАКЛИНАНИЙ ===
        /// <summary>
        /// Загружает все заклинания персонажа из БД.
        /// </summary>
        public static (List<SpellData> Prepared, List<SpellData> Known) LoadSpells(int characterId)
        {
            var prepared = new List<SpellData>();
            var known = new List<SpellData>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                bool hasMod = DatabaseHelper.HasColumn(conn, "SpellReference", "is_modified");
                string modSel = hasMod ? "sr.is_modified" : "0 AS is_modified";
                // Колонки эффекта появились позже: на старой базе их может не быть.
                // Псевдонимы обязательны: без них SQLite называет колонку по всему
                // выражению, и чтение по имени возвращало пустоту.
                bool hasEffect = DatabaseHelper.HasColumn(conn, "SpellReference", "damage_formula");
                string effSel = hasEffect
                    ? "COALESCE(sr.damage_formula,'') AS damage_formula, " +
                      "COALESCE(sr.damage_type,'') AS damage_type, " +
                      "COALESCE(sr.per_level_damage,'') AS per_level_damage"
                    : "'' AS damage_formula, '' AS damage_type, '' AS per_level_damage";

                // Лечение появилось позже урона: на старой базе колонок ещё нет
                bool hasHeal = DatabaseHelper.HasColumn(conn, "SpellReference", "heal_formula");
                string healSel = hasHeal
                    ? "COALESCE(sr.heal_formula,'') AS heal_formula, " +
                      "COALESCE(sr.heal_per_level,'') AS heal_per_level"
                    : "'' AS heal_formula, '' AS heal_per_level";

                bool hasParts = DatabaseHelper.HasColumn(conn, "SpellReference", "extra_damage_parts");
                string partsSel = hasParts ? "COALESCE(sr.extra_damage_parts,'') AS extra_damage_parts"
                                           : "'' AS extra_damage_parts";
                bool hasSave = DatabaseHelper.HasColumn(conn, "SpellReference", "save_note");
                string saveSel = hasSave ? "COALESCE(sr.save_note,'') AS save_note" : "'' AS save_note";

                string sql = $@"
                    SELECT cs.id_char_spell, cs.id_spell, cs.is_prepared, cs.IndividualImagePath,
                           sr.name, sr.level, sr.school, sr.casting_time, sr.range, sr.components,
                           sr.duration, sr.description, sr.at_higher_levels, sr.classes,
                           sr.concentration, sr.ritual, sr.source, sr.material, sr.page, {modSel} AS is_modified,
                           {effSel}, {healSel}, {partsSel}, {saveSel}
                    FROM CharacterSpells cs
                    LEFT JOIN SpellReference sr ON sr.id_spell = cs.id_spell
                    WHERE cs.id_character = @id
                    ORDER BY sr.level, sr.name";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var spell = new SpellData
                    {
                        IdCharSpell = ReadInt(reader, "id_char_spell"),
                        IdSpell = ReadInt(reader, "id_spell"),
                        IsPrepared = ReadInt(reader, "is_prepared") == 1,
                        ImagePath = ReadText(reader, "IndividualImagePath"),
                        Name = ReadText(reader, "name"),
                        Level = ReadInt(reader, "level"),
                        School = ReadText(reader, "school"),
                        CastingTime = ReadText(reader, "casting_time"),
                        Range = ReadText(reader, "range"),
                        Components = ReadText(reader, "components"),
                        Duration = ReadText(reader, "duration"),
                        Description = ReadText(reader, "description"),
                        AtHigherLevels = ReadText(reader, "at_higher_levels"),
                        Classes = ReadText(reader, "classes"),
                        Source = ReadText(reader, "source"),
                        Material = ReadText(reader, "material"),
                        Page = ReadText(reader, "page"),
                        Concentration = ReadInt(reader, "concentration") == 1,
                        Ritual = ReadInt(reader, "ritual") == 1,
                        IsModified = ReadInt(reader, "is_modified") == 1,
                        DamageFormula = ReadText(reader, "damage_formula"),
                        DamageType = ReadText(reader, "damage_type"),
                        PerLevelDamage = ReadText(reader, "per_level_damage"),
                        HealFormula = ReadText(reader, "heal_formula"),
                        HealPerLevel = ReadText(reader, "heal_per_level"),
                        ExtraDamageParts = DeserializeParts(ReadText(reader, "extra_damage_parts")),
                        SaveNote = ReadText(reader, "save_note"),
                    };
                    (spell.IsPrepared ? prepared : known).Add(spell);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] LoadSpells error: {ex.Message}");
            }
            return (prepared, known);
        }

        /// <summary>
        /// Загружает все записи из справочника заклинаний.
        /// </summary>
        public static List<SpellRefRow> LoadSpellReference()
        {
            var rows = new List<SpellRefRow>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT id_spell, name, level, school, source FROM SpellReference ORDER BY name", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new SpellRefRow
                    {
                        Id = ReadInt(reader, "id_spell"),
                        Name = ReadText(reader, "name"),
                        Level = ReadInt(reader, "level"),
                        School = ReadText(reader, "school"),
                        Source = ReadText(reader, "source")
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] LoadSpellReference error: {ex.Message}");
            }
            return rows;
        }

        /// <summary>
        /// Возвращает список уникальных источников заклинаний.
        /// </summary>
        public static List<string> LoadDistinctSources()
        {
            var list = new List<string>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                // Известные источники — вверх списка, дальше по алфавиту.
                // Иначе Player's Handbook теряется среди десятков импортированных книг.
                using var cmd = new SqliteCommand(@"
                    SELECT DISTINCT source FROM SpellReference
                    WHERE source IS NOT NULL AND source <> ''
                    ORDER BY
                        CASE
                            WHEN lower(source) LIKE '%player%handbook%' THEN 0
                            WHEN lower(source) LIKE '%phb%' THEN 1
                            WHEN lower(source) LIKE '%srd%' THEN 2
                            WHEN lower(source) LIKE '%xanathar%' THEN 3
                            WHEN lower(source) LIKE '%tasha%' THEN 4
                            WHEN lower(source) LIKE '%custom%' THEN 8
                            ELSE 5
                        END,
                        source", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(r.GetValue(0).ToString());
            }
            catch (Exception ex) { Debug.WriteLine($"[SpellService] LoadDistinctSources error: {ex.Message}"); }
            return list;
        }
        #endregion

        #region === CRUD ОПЕРАЦИИ ===
        /// <summary>
        /// Добавляет заклинание из справочника к персонажу (в известные).
        /// </summary>
        public static bool AddSpellToCharacter(int characterId, int spellId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var chk = new SqliteCommand(
                    "SELECT 1 FROM CharacterSpells WHERE id_character=@c AND id_spell=@s", conn);
                chk.Parameters.AddWithValue("@c", characterId);
                chk.Parameters.AddWithValue("@s", spellId);
                if (chk.ExecuteScalar() != null)
                    return false;
                using var ins = new SqliteCommand(
                    "INSERT INTO CharacterSpells (id_character, id_spell, is_prepared) VALUES (@c,@s,0)", conn);
                ins.Parameters.AddWithValue("@c", characterId);
                ins.Parameters.AddWithValue("@s", spellId);
                ins.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] AddSpellToCharacter error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Удаляет заклинание из списка персонажа.
        /// </summary>
        public static void RemoveSpellFromCharacter(int characterId, int idCharSpell)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "DELETE FROM CharacterSpells WHERE id_char_spell=@id AND id_character=@cid", conn);
                cmd.Parameters.AddWithValue("@id", idCharSpell);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] RemoveSpellFromCharacter error: {ex.Message}");
            }
        }

        /// <summary>
        /// Переключает статус подготовки заклинания.
        /// </summary>
        public static void TogglePrepared(int characterId, int idCharSpell, bool makePrepared)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "UPDATE CharacterSpells SET is_prepared=@p WHERE id_char_spell=@id AND id_character=@cid", conn);
                cmd.Parameters.AddWithValue("@p", makePrepared ? 1 : 0);
                cmd.Parameters.AddWithValue("@id", idCharSpell);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] TogglePrepared error: {ex.Message}");
            }
        }

        /// <summary>
        /// Сохраняет изменения в карточке заклинания.
        /// </summary>
        public static void SaveSpell(SpellData spell)
        {
            EnsureEffectColumns();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    UPDATE SpellReference
                    SET name=@n, level=@l, school=@sch, casting_time=@c, range=@r, components=@co, duration=@du,
                        description=@desc, at_higher_levels=@hi, classes=@cl, concentration=@conc, ritual=@rit,
                        source=@src, page=@pg, material=@mat, is_modified=@mod,
                        damage_formula=@dmg, damage_type=@dtype, per_level_damage=@plvl,
                        heal_formula=@heal, heal_per_level=@heallvl,
                        extra_damage_parts=@parts, save_note=@save
                    WHERE id_spell=@id", conn);
                cmd.Parameters.AddWithValue("@n", spell.Name);
                cmd.Parameters.AddWithValue("@l", spell.Level);
                cmd.Parameters.AddWithValue("@sch", spell.School);
                cmd.Parameters.AddWithValue("@c", spell.CastingTime);
                cmd.Parameters.AddWithValue("@r", spell.Range);
                cmd.Parameters.AddWithValue("@co", spell.Components);
                cmd.Parameters.AddWithValue("@du", spell.Duration);
                cmd.Parameters.AddWithValue("@desc", spell.Description);
                cmd.Parameters.AddWithValue("@hi", spell.AtHigherLevels ?? "");
                cmd.Parameters.AddWithValue("@cl", spell.Classes);
                cmd.Parameters.AddWithValue("@conc", spell.Concentration ? 1 : 0);
                cmd.Parameters.AddWithValue("@rit", spell.Ritual ? 1 : 0);

                // Пустой источник не записываем поверх существующего: иначе терялась
                // метка «своё заклинание» (source = custom), и запись переставала
                // считаться созданной вручную — в том числе при обмене файлами.
                cmd.Parameters.AddWithValue("@src", string.IsNullOrWhiteSpace(spell.Source)
                    ? ReadSourceFor(spell.IdSpell)
                    : spell.Source);
                cmd.Parameters.AddWithValue("@pg", spell.Page ?? "");
                cmd.Parameters.AddWithValue("@mat", spell.Material ?? "");
                cmd.Parameters.AddWithValue("@mod", spell.IsModified ? 1 : 0);
                cmd.Parameters.AddWithValue("@dmg", spell.DamageFormula ?? "");
                cmd.Parameters.AddWithValue("@dtype", spell.DamageType ?? "");
                cmd.Parameters.AddWithValue("@plvl", spell.PerLevelDamage ?? "");
                cmd.Parameters.AddWithValue("@heal", spell.HealFormula ?? "");
                cmd.Parameters.AddWithValue("@heallvl", spell.HealPerLevel ?? "");
                cmd.Parameters.AddWithValue("@parts", SerializeParts(spell.ExtraDamageParts));
                cmd.Parameters.AddWithValue("@save", spell.SaveNote ?? "");
                cmd.Parameters.AddWithValue("@id", spell.IdSpell);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] SaveSpell error: {ex.Message}");
            }
        }

        /// <summary>
        /// Возвращает текущий источник заклинания — нужен, когда сохраняют карточку
        /// без источника, чтобы не потерять метку «своё заклинание».
        /// </summary>
        private static string ReadSourceFor(int spellId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT source FROM SpellReference WHERE id_spell = @id", conn);
                cmd.Parameters.AddWithValue("@id", spellId);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? "" : value.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] ReadSourceFor: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Сохраняет заклинание, пришедшее в файле обмена.
        ///
        /// Отличие от CreateNewSpell: заклинание НЕ привязывается к персонажу —
        /// игрок сам решит, добавлять ли его себе. Если такое имя уже есть среди
        /// своих, запись обновляется (иначе после обмена копились бы дубликаты).
        /// </summary>
        public static int SaveSharedSpell(SpellData spell)
        {
            if (spell == null || string.IsNullOrWhiteSpace(spell.Name)) return 0;

            EnsureEffectColumns();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                // Уже есть своё заклинание с таким именем?
                int existingId = 0;
                using (var find = new SqliteCommand(
                    "SELECT id_spell FROM SpellReference WHERE name = @n AND source = 'custom' LIMIT 1", conn))
                {
                    find.Parameters.AddWithValue("@n", spell.Name.Trim());
                    var found = find.ExecuteScalar();
                    if (found != null && found != DBNull.Value) existingId = Convert.ToInt32(found);
                }

                if (existingId > 0)
                {
                    spell.IdSpell = existingId;
                    SaveSpell(spell);
                    return existingId;
                }

                using var cmd = new SqliteCommand(@"
                    INSERT INTO SpellReference (name, level, school, casting_time, range, components, duration,
                        description, at_higher_levels, classes, concentration, ritual, source, page, material,
                        is_modified, damage_formula, damage_type, per_level_damage,
                        heal_formula, heal_per_level,
                        extra_damage_parts, save_note)
                    VALUES (@n,@l,@sch,@c,@r,@co,@du,@desc,@hi,@cl,@conc,@rit,'custom',@pg,@mat,0,
                        @dmg,@dtype,@plvl,@heal,@heallvl,@parts,@save);
                    SELECT last_insert_rowid();", conn);
                cmd.Parameters.AddWithValue("@n", spell.Name.Trim());
                cmd.Parameters.AddWithValue("@l", Math.Max(0, Math.Min(9, spell.Level)));
                cmd.Parameters.AddWithValue("@sch", spell.School ?? "");
                cmd.Parameters.AddWithValue("@c", spell.CastingTime ?? "");
                cmd.Parameters.AddWithValue("@r", spell.Range ?? "");
                cmd.Parameters.AddWithValue("@co", spell.Components ?? "");
                cmd.Parameters.AddWithValue("@du", spell.Duration ?? "");
                cmd.Parameters.AddWithValue("@desc", spell.Description ?? "");
                cmd.Parameters.AddWithValue("@hi", spell.AtHigherLevels ?? "");
                cmd.Parameters.AddWithValue("@cl", spell.Classes ?? "");
                cmd.Parameters.AddWithValue("@conc", spell.Concentration ? 1 : 0);
                cmd.Parameters.AddWithValue("@rit", spell.Ritual ? 1 : 0);
                cmd.Parameters.AddWithValue("@pg", spell.Page ?? "");
                cmd.Parameters.AddWithValue("@mat", spell.Material ?? "");
                cmd.Parameters.AddWithValue("@dmg", spell.DamageFormula ?? "");
                cmd.Parameters.AddWithValue("@dtype", spell.DamageType ?? "");
                cmd.Parameters.AddWithValue("@plvl", spell.PerLevelDamage ?? "");
                cmd.Parameters.AddWithValue("@heal", spell.HealFormula ?? "");
                cmd.Parameters.AddWithValue("@heallvl", spell.HealPerLevel ?? "");
                cmd.Parameters.AddWithValue("@parts", SerializeParts(spell.ExtraDamageParts));
                cmd.Parameters.AddWithValue("@save", spell.SaveNote ?? "");
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] SaveSharedSpell: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Создаёт новое заклинание в справочнике и добавляет к персонажу.
        /// </summary>
        public static int CreateNewSpell(SpellData spell, int characterId)
        {
            EnsureEffectColumns();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    INSERT INTO SpellReference (name, level, school, casting_time, range, components, duration, 
                        description, at_higher_levels, classes, concentration, ritual, source, page, material, is_modified,
                        damage_formula, damage_type, per_level_damage, heal_formula, heal_per_level,
                        extra_damage_parts, save_note)
                    VALUES (@n,@l,@sch,@c,@r,@co,@du,@desc,@hi,@cl,@conc,@rit,@src,@pg,@mat,0,
                        @dmg,@dtype,@plvl,@heal,@heallvl,@parts,@save);
                    SELECT last_insert_rowid();", conn);
                cmd.Parameters.AddWithValue("@n", spell.Name);
                cmd.Parameters.AddWithValue("@l", spell.Level);
                cmd.Parameters.AddWithValue("@sch", spell.School);
                cmd.Parameters.AddWithValue("@c", spell.CastingTime);
                cmd.Parameters.AddWithValue("@r", spell.Range);
                cmd.Parameters.AddWithValue("@co", spell.Components);
                cmd.Parameters.AddWithValue("@du", spell.Duration);
                cmd.Parameters.AddWithValue("@desc", spell.Description);
                cmd.Parameters.AddWithValue("@hi", spell.AtHigherLevels ?? "");
                cmd.Parameters.AddWithValue("@cl", spell.Classes);
                cmd.Parameters.AddWithValue("@conc", spell.Concentration ? 1 : 0);
                cmd.Parameters.AddWithValue("@rit", spell.Ritual ? 1 : 0);
                cmd.Parameters.AddWithValue("@src", "custom");
                cmd.Parameters.AddWithValue("@pg", spell.Page ?? "");
                cmd.Parameters.AddWithValue("@mat", spell.Material ?? "");
                cmd.Parameters.AddWithValue("@dmg", spell.DamageFormula ?? "");
                cmd.Parameters.AddWithValue("@dtype", spell.DamageType ?? "");
                cmd.Parameters.AddWithValue("@plvl", spell.PerLevelDamage ?? "");
                cmd.Parameters.AddWithValue("@parts", SerializeParts(spell.ExtraDamageParts));
                cmd.Parameters.AddWithValue("@save", spell.SaveNote ?? "");
                int newSpellId = Convert.ToInt32(cmd.ExecuteScalar());

                using var link = new SqliteCommand(
                    "INSERT INTO CharacterSpells (id_character, id_spell, is_prepared) VALUES (@cid,@sid,0)", conn);
                link.Parameters.AddWithValue("@cid", characterId);
                link.Parameters.AddWithValue("@sid", newSpellId);
                link.ExecuteNonQuery();
                return newSpellId;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] CreateNewSpell error: {ex.Message}");
                return 0;
            }
        }
        #endregion

        #region === СЛОТЫ ЗАКЛИНАНИЙ ===
        /// <summary>
        /// Загружает слоты заклинаний для конкретного уровня.
        /// </summary>
        public static (int Total, int Used) LoadSlots(int characterId, int level)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT total_slots, used_slots FROM CharacterSpellSlots WHERE id_character=@id AND slot_level=@lvl", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.Parameters.AddWithValue("@lvl", level);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    int total = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                    int used = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                    return (total, used);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] LoadSlots error: {ex.Message}");
            }
            return (0, 0);
        }

        /// <summary>
        /// Изменяет количество использованных слотов.
        /// </summary>
        public static void ChangeUsedSlots(int characterId, int level, int delta)
        {
            try
            {
                var (total, used) = LoadSlots(characterId, level);
                int available = total - used;
                int newAvailable = Math.Max(0, Math.Min(total, available + delta));
                int newUsed = total - newAvailable;
                if (newUsed == used) return;
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "UPDATE CharacterSpellSlots SET used_slots=@u WHERE id_character=@id AND slot_level=@lvl", conn);
                cmd.Parameters.AddWithValue("@u", newUsed);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.Parameters.AddWithValue("@lvl", level);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] ChangeUsedSlots error: {ex.Message}");
            }
        }

        /// <summary>
        /// Восполняет все слоты (длинный отдых).
        /// </summary>
        public static void RestSlots(int characterId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "UPDATE CharacterSpellSlots SET used_slots=0 WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] RestSlots error: {ex.Message}");
            }
        }

        /// <summary>
        /// Восполняет слоты одного уровня.
        ///
        /// Нужно потому, что отдых бывает разным: некоторые предметы и умения
        /// восстанавливают ячейки только конкретного уровня, а не все сразу.
        /// </summary>
        public static void RestSlotsForLevel(int characterId, int level)
        {
            if (level < 0) return;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                // Колонка называется slot_level: запрос с прежним именем
                // (spell_level) падал на «no such column», ошибку глотал catch, и
                // кнопка отдыха по уровню молча не делала ничего
                using var cmd = new SqliteCommand(
                    "UPDATE CharacterSpellSlots SET used_slots=0 WHERE id_character=@id AND slot_level=@lvl", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.Parameters.AddWithValue("@lvl", level);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] RestSlotsForLevel error: {ex.Message}");
            }
        }

        #region === КАСТОВАНИЕ ===

        /// <summary>
        /// Типы урона по рулбуку: английское название (как в справочнике) и
        /// русская подпись для интерфейса.
        /// </summary>
        public static readonly (string En, string Ru)[] DamageTypes =
        {
            ("Bludgeoning", "дробящий"), ("Piercing", "колющий"), ("Slashing", "рубящий"),
            ("Acid", "кислота"), ("Cold", "холод"), ("Fire", "огонь"), ("Force", "силовой"),
            ("Lightning", "электричество"), ("Necrotic", "некротический"), ("Poison", "яд"),
            ("Psychic", "психический"), ("Radiant", "излучение"), ("Thunder", "звук")
        };

        /// <summary>Русская подпись типа урона по английскому названию.</summary>
        public static string DamageTypeLabel(string damageType)
        {
            string raw = (damageType ?? "").Trim();
            if (raw.Length == 0) return "";

            // В базе может лежать текст вида «Fire (огонь)» — берём английскую часть
            int bracket = raw.IndexOf('(');
            if (bracket > 0) return raw.Substring(0, bracket).Trim();

            foreach (var (en, ru) in DamageTypes)
                if (string.Equals(en, raw, StringComparison.OrdinalIgnoreCase)) return ru;

            return raw;
        }

        /// <summary>
        /// Приводит введённый тип урона к тому виду, что хранится в базе.
        ///
        /// Игрок может выбрать «колющий» из списка, вписать «колющий урон» или
        /// английское «Piercing» — в базу уходит английское название, чтобы
        /// подписи бросков и типы не расползались по написанию.
        /// </summary>
        public static string NormalizeDamageType(string text)
        {
            string raw = (text ?? "").Trim();
            if (raw.Length == 0) return "";

            int bracket = raw.IndexOf('(');
            if (bracket > 0) raw = raw.Substring(0, bracket).Trim();

            foreach (var (en, ru) in DamageTypes)
            {
                if (string.Equals(en, raw, StringComparison.OrdinalIgnoreCase)) return en;
                if (string.Equals(ru, raw, StringComparison.OrdinalIgnoreCase)) return en;

                // «колющий урон», «урон огнём» — узнаём по началу слова
                if (ru.Length > 3 && raw.StartsWith(ru.Substring(0, ru.Length - 2), StringComparison.OrdinalIgnoreCase))
                    return en;
            }

            return raw;
        }

        /// <summary>Одна часть урона при конкретном сотворении: формула уже с добавками.</summary>
        public sealed class CastDamagePart
        {
            /// <summary>Итоговая формула: «1к10+1к6».</summary>
            public string Formula = "";

            /// <summary>Тип урона как в базе: «Psychic» или слово игрока.</summary>
            public string DamageType = "";

            /// <summary>Уточнение: «половина при успехе».</summary>
            public string Note = "";

            /// <summary>Русская подпись типа: «психический».</summary>
            public string TypeLabel => DamageTypeLabel(DamageType);
        }

        /// <summary>Результат попытки сотворить заклинание.</summary>
        public sealed class CastResult        {
            public bool Success;
            public string Message = "";

            /// <summary>Уровень ячейки, которой сотворили.</summary>
            public int SlotLevel;

            /// <summary>Сколько ячеек этого уровня осталось после каста.</summary>
            public int SlotsLeft;

            /// <summary>Формула броска урона с учётом подъёма уровня (пусто, если урона нет).</summary>
            public string DamageFormula = "";

            /// <summary>
            /// Формула лечения с учётом подъёма уровня. Пусто, если заклинание
            /// не лечит: так их и различает окно заклинаний.
            /// </summary>
            public string HealFormula = "";

            public string DamageType = "";

            /// <summary>
            /// Части урона одного каста: у заклинания бывает несколько типов
            /// («дробящий + психический»), и каждая часть бросается отдельно.
            /// </summary>
            public List<CastDamagePart> Parts = new List<CastDamagePart>();

            /// <summary>Пометка про спасбросок цели на время каста.</summary>
            public string SaveNote = "";

            /// <summary>Название заклинания для подписи броска.</summary>
            public string SpellName = "";

            /// <summary>Ячейка не потрачена (заговор или урон не бросается) — не ошибка.</summary>
            public bool NoSlotSpent;
        }

        /// <summary>
        /// Считает, какими формулами бросать урон при сотворении.
        ///
        /// За каждый уровень выше базового добавляется своя PerLevelDamage —
        /// у каждой части урона она может быть своей: «1к10» с «+1к6» и
        /// отдельная часть «1к4» психического урона с собственной добавкой.
        /// </summary>
        public static List<CastDamagePart> BuildCastParts(SpellData spell, int slotLevel)
        {
            var parts = new List<CastDamagePart>();
            if (spell == null) return parts;

            int steps = Math.Max(0, slotLevel - Math.Max(0, spell.Level));

            foreach (var part in spell.AllDamageParts())
            {
                string formula = (part.Formula ?? "").Trim();
                if (formula.Length == 0) continue;

                string perLevel = (part.PerLevelDamage ?? "").Trim();
                if (steps > 0 && perLevel.Length > 0)
                {
                    string add = perLevel.StartsWith("+") ? perLevel : "+" + perLevel;
                    for (int i = 0; i < steps; i++) formula += add;
                }

                parts.Add(new CastDamagePart
                {
                    Formula = formula,
                    DamageType = part.DamageType ?? "",
                    Note = part.Note ?? ""
                });
            }

            return parts;
        }

        /// <summary>
        /// Считает, какой формулой бросать урон при сотворении.
        ///
        /// За каждый уровень выше базового добавляется PerLevelDamage: «1d4+4» с
        /// «+1d6» на втором уровне даёт «1d4+4+1d6». Если частей урона несколько,
        /// они склеиваются — это подпись; для броска берите <see cref="BuildCastParts"/>.
        /// </summary>
        public static string BuildCastFormula(SpellData spell, int slotLevel)
        {
            var parts = BuildCastParts(spell, slotLevel);
            return parts.Count == 0 ? "" : string.Join("+", parts.Select(p => p.Formula));
        }

        /// <summary>
        /// Формула лечения при конкретном уровне ячейки.
        ///
        /// За каждый уровень выше базового добавляется HealPerLevel — как у урона.
        /// «Исцеляющее слово» лечит 2к4, а ячейкой выше — 2к4+1к4.
        /// </summary>
        public static string BuildCastHealFormula(SpellData spell, int slotLevel)
        {
            if (spell == null) return "";

            string formula = (spell.HealFormula ?? "").Trim();
            if (formula.Length == 0) return "";

            int steps = Math.Max(0, slotLevel - Math.Max(0, spell.Level));
            string perLevel = (spell.HealPerLevel ?? "").Trim();

            if (steps > 0 && perLevel.Length > 0)
            {
                string add = perLevel.StartsWith("+") ? perLevel : "+" + perLevel;
                for (int i = 0; i < steps; i++) formula += add;
            }

            return formula;
        }

        /// <summary>
        /// Пробует сотворить заклинание: проверяет ячейку и тратит её.
        ///
        /// Ячейки нет — каст запрещён, и никаких предложений «сотворить выше»
        /// не показываем: игрок сам решает, поднимать ли уровень.
        /// Заговоры (уровень 0) ячейку не тратят.
        /// </summary>
        public static CastResult TryCast(int characterId, SpellData spell, int slotLevel)
        {
            var result = new CastResult();
            if (spell == null)
            {
                result.Message = "Заклинание не выбрано";
                return result;
            }

            result.SpellName = spell.Name ?? "";
            int baseLevel = Math.Max(0, spell.Level);
            int useLevel = Math.Max(baseLevel, slotLevel);
            result.SlotLevel = useLevel;
            result.DamageType = spell.DamageType ?? "";
            result.SaveNote = spell.SaveNote ?? "";

            // Заговор: ячейка не нужна
            if (baseLevel == 0)
            {
                result.Success = true;
                result.NoSlotSpent = true;
                result.Parts = BuildCastParts(spell, 0);
                result.DamageFormula = BuildCastFormula(spell, 0);
                result.HealFormula = BuildCastHealFormula(spell, 0);
                result.Message = $"«{spell.Name}» сотворён (заговор, ячейка не тратится)";
                return result;
            }

            var (total, used) = LoadSlots(characterId, useLevel);
            int available = total - used;
            if (available <= 0)
            {
                result.Success = false;
                result.SlotsLeft = 0;
                result.Message = total <= 0
                    ? $"Ячеек {useLevel}-го уровня нет — сотворить «{spell.Name}» нельзя"
                    : $"Ячейки {useLevel}-го уровня закончились — сотворить «{spell.Name}» нельзя";
                return result;
            }

            ChangeUsedSlots(characterId, useLevel, -1);
            var after = LoadSlots(characterId, useLevel);

            result.Success = true;
            result.SlotsLeft = after.Total - after.Used;
            result.Parts = BuildCastParts(spell, useLevel);
            result.DamageFormula = BuildCastFormula(spell, useLevel);
            result.HealFormula = BuildCastHealFormula(spell, useLevel);
            result.Message = $"«{spell.Name}» сотворён ячейкой {useLevel}-го уровня (осталось {result.SlotsLeft})";
            return result;
        }

        #endregion

        /// <summary>
        /// Загружает все слоты персонажа (для настроек).
        /// </summary>
        public static Dictionary<int, int> LoadAllSlots(int characterId)
        {
            var slots = new Dictionary<int, int>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT slot_level, total_slots FROM CharacterSpellSlots WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int level = reader.IsDBNull(0) ? -1 : Convert.ToInt32(reader.GetValue(0));
                    int total = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                    if (level >= 0) slots[level] = total;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] LoadAllSlots error: {ex.Message}");
            }
            return slots;
        }

        /// <summary>
        /// Сохраняет количество слотов для уровня.
        /// </summary>
        public static void SaveSlotCount(int characterId, int level, int total)
        {
            DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                using var cmd = new SqliteCommand(@"
                    UPDATE CharacterSpellSlots SET total_slots=@tot WHERE id_character=@cid AND slot_level=@lvl;
                    INSERT INTO CharacterSpellSlots (id_character, slot_level, total_slots, used_slots)
                    SELECT @cid, @lvl, @tot, 0
                    WHERE changes()=0 AND NOT EXISTS (SELECT 1 FROM CharacterSpellSlots WHERE id_character=@cid AND slot_level=@lvl);", conn, tx);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lvl", level);
                cmd.Parameters.AddWithValue("@tot", total);
                cmd.ExecuteNonQuery();
            });
        }

        /// <summary>
        /// Суммарные слоты (для режима «ВСЕ»).
        /// </summary>
        public static (int Total, int Used) LoadSlotsSummary(int characterId)
        {
            int total = 0, used = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT total_slots, used_slots FROM CharacterSpellSlots WHERE id_character=@id AND slot_level>=1", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    total += r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0));
                    used += r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[SpellService] LoadSlotsSummary error: {ex.Message}"); }
            return (total, used);
        }

        /// <summary>
        /// Детализация слотов по уровням (для обзора ячеек).
        /// </summary>
        public static List<(int Level, int Total, int Used)> LoadSlotsDetails(int characterId)
        {
            var rows = new List<(int, int, int)>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT slot_level, total_slots, used_slots FROM CharacterSpellSlots WHERE id_character=@id ORDER BY slot_level", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((
                        r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0)),
                        r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1)),
                        r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2))));
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[SpellService] LoadSlotsDetails error: {ex.Message}"); }
            return rows;
        }
        #endregion

        #region === ТЕГИ ===
        /// <summary>
        /// Загружает все теги заклинаний.
        /// </summary>
        public static List<SpellTag> LoadAllTags()
        {
            var tags = new List<SpellTag>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT id_tag, tag_name, tag_category FROM SpellTags ORDER BY tag_name", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tags.Add(new SpellTag
                    {
                        Id = ReadInt(reader, "id_tag"),
                        Name = ReadText(reader, "tag_name"),
                        Category = ReadText(reader, "tag_category")
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] LoadAllTags error: {ex.Message}");
            }
            return tags;
        }

        /// <summary>
        /// Загружает карту spell→tags.
        /// </summary>
        public static Dictionary<int, HashSet<int>> LoadSpellTagMap()
        {
            var map = new Dictionary<int, HashSet<int>>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT id_spell, id_tag FROM SpellTagRelations", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int spellId = ReadInt(reader, "id_spell");
                    int tagId = ReadInt(reader, "id_tag");
                    if (!map.TryGetValue(spellId, out var set))
                    {
                        set = new HashSet<int>();
                        map[spellId] = set;
                    }
                    set.Add(tagId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] LoadSpellTagMap error: {ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// Сохраняет теги для заклинания.
        /// </summary>
        public static void SaveSpellTags(int spellId, IEnumerable<int> tagIds)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var del = new SqliteCommand("DELETE FROM SpellTagRelations WHERE id_spell=@s", conn);
                del.Parameters.AddWithValue("@s", spellId);
                del.ExecuteNonQuery();
                foreach (var tagId in tagIds)
                {
                    using var ins = new SqliteCommand(
                        "INSERT OR IGNORE INTO SpellTagRelations (id_spell, id_tag) VALUES (@s,@t)", conn);
                    ins.Parameters.AddWithValue("@s", spellId);
                    ins.Parameters.AddWithValue("@t", tagId);
                    ins.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] SaveSpellTags error: {ex.Message}");
            }
        }

        /// <summary>
        /// Создаёт новый тег.
        /// </summary>
        public static bool CreateTag(string name, string category)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "INSERT OR IGNORE INTO SpellTags (tag_name, tag_category) VALUES (@n,@c)", conn);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@c", category);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] CreateTag error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Удаляет тег.
        /// </summary>
        public static void DeleteTag(int tagId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM SpellTags WHERE id_tag=@id", conn);
                cmd.Parameters.AddWithValue("@id", tagId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] DeleteTag error: {ex.Message}");
            }
        }

        /// <summary>
        /// Загружает ID тегов, присвоенных заклинанию.
        /// </summary>
        public static HashSet<int> LoadAssignedTagIds(int spellId)
        {
            var set = new HashSet<int>();
            if (spellId <= 0) return set;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT id_tag FROM SpellTagRelations WHERE id_spell=@s", conn);
                cmd.Parameters.AddWithValue("@s", spellId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) set.Add(ReadInt(r, "id_tag"));
            }
            catch (Exception ex) { Debug.WriteLine($"[SpellService] LoadAssignedTagIds error: {ex.Message}"); }
            return set;
        }
        #endregion

        #region === УДАЛЕНИЕ С ЛОГИКОЙ ВОЗВРАТА ===
        /// <summary>
        /// Удаляет заклинание с правильной логикой:
        /// - Из подготовленных → возвращает в известные (is_prepared = 0)
        /// - Из известных → удаляет из CharacterSpells
        /// - Если source == "custom" → удаляет из SpellReference
        /// </summary>
        public static void RemoveSpellSmart(int characterId, int idCharSpell)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                // Получаем информацию о заклинании
                SpellData? spell = null;
                using (var cmd = new SqliteCommand(@"
            SELECT cs.is_prepared, sr.source 
            FROM CharacterSpells cs
            LEFT JOIN SpellReference sr ON sr.id_spell = cs.id_spell
            WHERE cs.id_char_spell = @id AND cs.id_character = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@id", idCharSpell);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        spell = new SpellData
                        {
                            IsPrepared = reader.GetInt32(0) == 1,
                            Source = reader.IsDBNull(1) ? "" : reader.GetString(1)
                        };
                    }
                }

                if (spell == null) return;

                if (spell.IsPrepared)
                {
                    // Из подготовленных → в известные
                    using var cmd = new SqliteCommand(
                        "UPDATE CharacterSpells SET is_prepared = 0 WHERE id_char_spell = @id AND id_character = @cid", conn);
                    cmd.Parameters.AddWithValue("@id", idCharSpell);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.ExecuteNonQuery();
                    Debug.WriteLine($"[SpellService] Заклинание перемещено из подготовленных в известные");
                }
                else
                {
                    // Из известных → удаляем из CharacterSpells
                    using var cmd = new SqliteCommand(
                        "DELETE FROM CharacterSpells WHERE id_char_spell = @id AND id_character = @cid", conn);
                    cmd.Parameters.AddWithValue("@id", idCharSpell);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.ExecuteNonQuery();

                    // Если кастомное — удаляем из справочника
                    if (spell.Source.Equals("custom", StringComparison.OrdinalIgnoreCase))
                    {
                        using var cmd2 = new SqliteCommand(
                            "DELETE FROM SpellReference WHERE id_spell = (SELECT id_spell FROM CharacterSpells WHERE id_char_spell = @id)", conn);
                        cmd2.Parameters.AddWithValue("@id", idCharSpell);
                        cmd2.ExecuteNonQuery();
                        Debug.WriteLine($"[SpellService] Кастомное заклинание удалено из справочника");
                    }
                    else
                    {
                        Debug.WriteLine($"[SpellService] Заклинание удалено из CharacterSpells, осталось в справочнике");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] RemoveSpellSmart error: {ex.Message}");
            }
        }
        #endregion

        #region === ФОРМУЛЫ ЗАКЛИНАНИЙ ===
        /// <summary>
        /// Считает Spell Save DC и бонус атаки заклинаний.
        /// </summary>
        public static (int SaveDC, int Attack) GetSpellFormulas(int characterId, string statColumn)
        {
            string col = statColumn switch
            {
                "Intelligence" => "Intelligence",
                "Wisdom" => "Wisdom",
                _ => "Charisma"
            };
            int prof = 2, score = 10;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using (var c = new SqliteCommand("SELECT ProficiencyBonus FROM LevelTable WHERE id_character=@id", conn))
                {
                    c.Parameters.AddWithValue("@id", characterId);
                    var o = c.ExecuteScalar();
                    if (o != null && o != DBNull.Value) prof = Convert.ToInt32(o);
                }
                using (var c = new SqliteCommand($"SELECT {col} FROM CharacterInfo WHERE id_character=@id", conn))
                {
                    c.Parameters.AddWithValue("@id", characterId);
                    var o = c.ExecuteScalar();
                    if (o != null && o != DBNull.Value) score = Convert.ToInt32(o);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[SpellService] GetSpellFormulas error: {ex.Message}"); }
            int mod = (int)Math.Floor((score - 10) / 2.0);
            return (8 + prof + mod, prof + mod);
        }
        #endregion

        #region === ТАБЛИЦЫ И МИГРАЦИИ ===
        /// <summary>
        /// Создаёт таблицы тегов и миграции, специфичные для SpellsView.
        /// </summary>
        public static void EnsureTables()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS SpellTags (
                        id_tag INTEGER PRIMARY KEY AUTOINCREMENT,
                        tag_name TEXT NOT NULL UNIQUE, tag_category TEXT, description TEXT);
                    CREATE TABLE IF NOT EXISTS SpellTagRelations (
                        id_relation INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_spell INTEGER NOT NULL, id_tag INTEGER NOT NULL,
                        UNIQUE(id_spell, id_tag));
                    CREATE INDEX IF NOT EXISTS idx_cs_char ON CharacterSpells(id_character);
                    CREATE INDEX IF NOT EXISTS idx_css_char ON CharacterSpellSlots(id_character);";
                cmd.ExecuteNonQuery();

                if (!DatabaseHelper.HasColumn(conn, "SpellReference", "is_modified"))
                {
                    using var a = new SqliteCommand(
                        "ALTER TABLE SpellReference ADD COLUMN is_modified INTEGER DEFAULT 0", conn);
                    a.ExecuteNonQuery();
                }

                // Эффект заклинания для кастования.
                //
                // В справочниках урон лежит внутри текста описания на английском,
                // и разбирать его автоматически нельзя — слишком много исключений.
                // Поэтому игрок сам указывает формулу в редакторе заклинания
                // («1d4+4»), тип урона и добавку за уровень выше («+1к6»).
                AddSpellColumnIfMissing(conn, "damage_formula", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "damage_type", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "per_level_damage", "TEXT DEFAULT ''");

                // Составной урон: у одного каста бывает несколько типов урона
                // («2к8+6 колющим + 3к10 психическим»). Дополнительные части
                // лежат списком в JSON, основная — в трёх колонках выше: так
                // старые записи читаются без переделки.
                AddSpellColumnIfMissing(conn, "extra_damage_parts", "TEXT DEFAULT ''");

                // Пометка про спасбросок цели: «Спас Ловкости Сл 15, половина при успехе»
                AddSpellColumnIfMissing(conn, "save_note", "TEXT DEFAULT ''");

                // Лечение: у заклинания бывает и урон, и лечение сразу
                // («Исцеляющее слово» лечит 2к4 и урона не наносит вовсе),
                // поэтому это отдельная пара полей, а не одно «значение»
                AddSpellColumnIfMissing(conn, "heal_formula", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "heal_per_level", "TEXT DEFAULT ''");
                _effectColumnsChecked = true;

                SeedDefaultTags(conn);
            }
            catch (Exception ex) { Debug.WriteLine($"[SpellService] EnsureTables error: {ex.Message}"); }
        }

        #region === СОСТАВНОЙ УРОН (JSON) ===

        private static readonly JsonSerializerOptions PartJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private static bool _effectColumnsChecked;

        /// <summary>
        /// Догоняет колонки эффекта перед записью. Нужно, если база старая,
        /// а EnsureTables в этой сессии ещё не вызывался.
        /// </summary>
        private static void EnsureEffectColumns()
        {
            if (_effectColumnsChecked) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                AddSpellColumnIfMissing(conn, "damage_formula", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "damage_type", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "per_level_damage", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "extra_damage_parts", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "save_note", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "heal_formula", "TEXT DEFAULT ''");
                AddSpellColumnIfMissing(conn, "heal_per_level", "TEXT DEFAULT ''");
                _effectColumnsChecked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] EnsureEffectColumns: {ex.Message}");
            }
        }

        /// <summary>Складывает дополнительные части урона в JSON.</summary>
        public static string SerializeParts(List<DamagePart> parts)
        {
            try
            {
                var clean = (parts ?? new List<DamagePart>())
                    .Where(part => part != null && !part.IsEmpty)
                    .ToList();

                return clean.Count == 0 ? "" : JsonSerializer.Serialize(clean, PartJsonOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] SerializeParts: {ex.Message}");
                return "";
            }
        }

        /// <summary>Читает дополнительные части урона. Битый JSON не роняет список.</summary>
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
                Debug.WriteLine($"[SpellService] DeserializeParts: {ex.Message}");
            }

            return result;
        }

        #endregion

        /// <summary>Добавляет колонку в SpellReference, если её ещё нет.</summary>
        private static void AddSpellColumnIfMissing(SqliteConnection conn, string column, string definition)        {
            try
            {
                if (DatabaseHelper.HasColumn(conn, "SpellReference", column)) return;
                using var cmd = new SqliteCommand(
                    $"ALTER TABLE SpellReference ADD COLUMN {column} {definition}", conn);
                cmd.ExecuteNonQuery();
                Debug.WriteLine($"[SpellService] добавлена колонка {column}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] колонка {column}: {ex.Message}");
            }
        }

        /// <summary>
        /// Сидит стандартные теги при первом запуске.
        /// </summary>
        private static void SeedDefaultTags(SqliteConnection conn)
        {
            var defaultTags = new[] { "огонь", "холод", "электричество", "кислота", "яд", "звук", "сила", "некротическая энергия", "излучение", "перемещение", "иллюзия", "исцеление", "контроль" };
            foreach (var tag in defaultTags)
            {
                using var cmd = new SqliteCommand("INSERT OR IGNORE INTO SpellTags (tag_name) VALUES (@n)", conn);
                cmd.Parameters.AddWithValue("@n", tag);
                cmd.ExecuteNonQuery();
            }
        }
        #endregion

        #region === ИМПОРТ JSON ===
        /// <summary>
        /// Проверяет, есть ли заклинания в справочнике, и импортирует JSON если пусто.
        /// Ищет файл по нескольким путям (BaseDirectory, AppData).
        /// Возвращает количество импортированных записей (0 если уже было).
        /// </summary>
        public static int EnsureImported()
        {
            try
            {
                if (SpellJsonImporter.GetCount() > 10)
                {
                    // Данные уже есть, но раньше дистанция не разбиралась — чиним пустые поля
                    return BackfillEmptyFields();
                }

                var path = FindSpellsJson();
                if (path != null)
                {
                    Debug.WriteLine($"[SpellService] Найден JSON: {path}");
                    int imported = SpellJsonImporter.ImportFromFile(path);
                    Debug.WriteLine($"[SpellService] Импортировано: {imported}");
                    return imported;
                }

                Debug.WriteLine("[SpellService] JSON не найден ни в одном из путей");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] EnsureImported error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Ищет spells.json: рядом с приложением, рядом со сборкой приложения и в AppData.
        /// </summary>
        private static string FindSpellsJson()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "srd", "spells.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT", "Data", "srd", "spells.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "spells.json")
            };

            // Рядом с самой сборкой приложения — на случай, если BaseDirectory подменён хостом
            try
            {
                var asmDir = Path.GetDirectoryName(typeof(SpellService).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    candidates.Add(Path.Combine(asmDir, "Data", "srd", "spells.json"));
                    candidates.Add(Path.Combine(Directory.GetParent(asmDir)?.FullName ?? asmDir, "Data", "srd", "spells.json"));
                }
            }
            catch { }

            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>
        /// Разово дополняет уже импортированный справочник недостающими полями
        /// (дистанция, длительность, материал, «на больших уровнях»), которые раньше
        /// не разбирались из-за неверного имени поля в JSON. Пользовательские правки
        /// не затираются: заполняются только пустые значения.
        /// </summary>
        public static int BackfillEmptyFields()
        {
            try
            {
                // Разовая операция: отметку храним в Settings, иначе каждый вход во вкладку
                // будет прогонять тысячи UPDATE (в исходном справочнике много полей реально пустых).
                const string markerKey = "Spells_FieldsBackfilled_v2";
                if (DatabaseHelper.GetSetting(markerKey, "") == "1")
                    return 0;

                DatabaseHelper.SaveSetting(markerKey, "1");

                int emptyRanges;
                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using var check = new SqliteCommand(
                        "SELECT COUNT(*) FROM SpellReference WHERE (range IS NULL OR range = '') AND source <> 'custom'", conn);
                    emptyRanges = Convert.ToInt32(check.ExecuteScalar());
                }

                if (emptyRanges == 0) return 0; // Всё уже разобрано
                Debug.WriteLine($"[SpellService] Backfill: пустых дистанций {emptyRanges}");

                // Путь ищем по тем же местам, что и EnsureImported
                var jsonPath = FindSpellsJson();
                if (jsonPath == null)
                {
                    Debug.WriteLine("[SpellService] Backfill: файл spells.json не найден");
                    return 0;
                }

                var rows = SpellJsonImporter.LoadRows(jsonPath);
                if (rows.Count == 0)
                {
                    Debug.WriteLine($"[SpellService] Backfill: JSON не прочитан ({jsonPath})");
                    return 0;
                }

                // Одно имя может встречаться у нескольких книг — берём первое непустое значение
                var byName = new Dictionary<string, SpellJsonImporter.SpellRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.Name)) continue;
                    if (!byName.ContainsKey(row.Name)) byName[row.Name] = row;
                }

                int updated = 0;
                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using var tx = conn.BeginTransaction();
                    using var cmd = new SqliteCommand(@"
                        UPDATE SpellReference SET
                            range = CASE WHEN (range IS NULL OR range = '') AND @range IS NOT NULL THEN @range ELSE range END,
                            duration = CASE WHEN (duration IS NULL OR duration = '') AND @duration IS NOT NULL THEN @duration ELSE duration END,
                            material = CASE WHEN (material IS NULL OR material = '') AND @material IS NOT NULL THEN @material ELSE material END,
                            at_higher_levels = CASE WHEN (at_higher_levels IS NULL OR at_higher_levels = '') AND @higher IS NOT NULL THEN @higher ELSE at_higher_levels END,
                            concentration = CASE WHEN COALESCE(concentration, 0) = 0 AND @conc = 1 THEN 1 ELSE concentration END
                        WHERE name = @name AND source <> 'custom'", conn, tx);

                    foreach (var pair in byName)
                    {
                        var row = pair.Value;
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@name", pair.Key);
                        cmd.Parameters.AddWithValue("@range", string.IsNullOrWhiteSpace(row.Range) ? (object)DBNull.Value : row.Range);
                        cmd.Parameters.AddWithValue("@duration", string.IsNullOrWhiteSpace(row.Duration) ? (object)DBNull.Value : row.Duration);
                        cmd.Parameters.AddWithValue("@material", string.IsNullOrWhiteSpace(row.Material) ? (object)DBNull.Value : row.Material);
                        cmd.Parameters.AddWithValue("@higher", string.IsNullOrWhiteSpace(row.AtHigherLevels) ? (object)DBNull.Value : row.AtHigherLevels);
                        cmd.Parameters.AddWithValue("@conc", row.Concentration ? 1 : 0);
                        updated += cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }

                Debug.WriteLine($"[SpellService] Backfill: обновлено {updated} записей (пустых дистанций было {emptyRanges})");
                return updated;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] BackfillEmptyFields error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return 0;
            }
        }

        /// <summary>
        /// Возвращает количество записей в справочнике.
        /// </summary>
        public static int GetSpellReferenceCount()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT COUNT(*) FROM SpellReference", conn);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpellService] GetSpellReferenceCount error: {ex.Message}");
                return 0;
            }
        }
        #endregion

        #region === УТИЛИТЫ ===
        private static string ReadText(SqliteDataReader reader, string column)
        {
            try
            {
                int ordinal = reader.GetOrdinal(column);
                return reader.IsDBNull(ordinal) ? "" : (reader.GetValue(ordinal)?.ToString() ?? "");
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
                int ordinal = reader.GetOrdinal(column);
                return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
            }
            catch
            {
                return 0;
            }
        }
        #endregion


    }
}