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
    /// Бестиарий: справочник существ, который мастер ведёт сам.
    ///
    /// Схема — одна таблица: списки (черты, действия) сохраняются как JSON.
    /// Так проще добавлять новые поля существа: не нужны ни новые таблицы, ни
    /// миграции на каждую мелочь, а старые записи остаются читаемыми.
    ///
    /// Бестиарий никуда не передаётся: у каждого мастера он свой. По сети идёт
    /// только имя и результат инициативы (см. InitiativeService).
    /// </summary>
    public static class BestiaryService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private static bool _tablesChecked;

        #region === СХЕМА ===

        /// <summary>
        /// Создаёт таблицу бестиария. Вызывается лениво, при первом обращении:
        /// так миграция не тормозит запуск приложения.
        /// </summary>
        public static void EnsureTables()
        {
            if (_tablesChecked) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                using var cmd = new SqliteCommand(@"
                    CREATE TABLE IF NOT EXISTS Bestiary (
                        id_monster INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL DEFAULT '',
                        name_en TEXT DEFAULT '',
                        size TEXT DEFAULT '',
                        creature_type TEXT DEFAULT '',
                        subtype TEXT DEFAULT '',
                        alignment TEXT DEFAULT '',
                        photo_path TEXT DEFAULT '',
                        armor_class INTEGER DEFAULT 10,
                        armor_class_note TEXT DEFAULT '',
                        hit_points INTEGER DEFAULT 1,
                        hit_points_formula TEXT DEFAULT '',
                        speed_walk INTEGER DEFAULT 30,
                        speed_fly INTEGER DEFAULT 0,
                        speed_swim INTEGER DEFAULT 0,
                        speed_climb INTEGER DEFAULT 0,
                        speed_burrow INTEGER DEFAULT 0,
                        speed_note TEXT DEFAULT '',
                        hover INTEGER DEFAULT 0,
                        strength INTEGER DEFAULT 10,
                        dexterity INTEGER DEFAULT 10,
                        constitution INTEGER DEFAULT 10,
                        intelligence INTEGER DEFAULT 10,
                        wisdom INTEGER DEFAULT 10,
                        charisma INTEGER DEFAULT 10,
                        saving_throws TEXT DEFAULT '',
                        skills TEXT DEFAULT '',
                        damage_vulnerabilities TEXT DEFAULT '',
                        damage_resistances TEXT DEFAULT '',
                        damage_immunities TEXT DEFAULT '',
                        condition_immunities TEXT DEFAULT '',
                        senses TEXT DEFAULT '',
                        languages TEXT DEFAULT '',
                        challenge_rating TEXT DEFAULT '0',
                        experience INTEGER DEFAULT 10,
                        proficiency_bonus INTEGER DEFAULT 2,
                        habitat TEXT DEFAULT '',
                        treasure TEXT DEFAULT '',
                        source TEXT DEFAULT '',
                        tags TEXT DEFAULT '',
                        legendary_intro TEXT DEFAULT '',
                        lair_text TEXT DEFAULT '',
                        spells TEXT DEFAULT '',
                        equipment TEXT DEFAULT '',
                        description TEXT DEFAULT '',
                        traits_json TEXT DEFAULT '[]',
                        actions_json TEXT DEFAULT '[]',
                        is_custom INTEGER DEFAULT 1,
                        created_at TEXT DEFAULT '',
                        updated_at TEXT DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_bestiary_name ON Bestiary(name);
                    CREATE INDEX IF NOT EXISTS idx_bestiary_type ON Bestiary(creature_type);", conn, tx);
                cmd.ExecuteNonQuery();

                tx.Commit();
                _tablesChecked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] EnsureTables error: {ex.Message}");
            }
        }

        /// <summary>Сбрасывает флаг «схема проверена» — нужно тестам после смены базы.</summary>
        public static void ResetSchemaCache() => _tablesChecked = false;

        #endregion

        #region === ЧТЕНИЕ ===

        /// <summary>
        /// Загружает весь бестиарий. Существ бывает немного (десятки, редко сотни),
        /// поэтому фильтры применяются в памяти — это проще и без риска в SQL.
        /// </summary>
        public static List<MonsterData> LoadAll()
        {
            var list = new List<MonsterData>();
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT * FROM Bestiary ORDER BY name COLLATE NOCASE", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadMonster(reader));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] LoadAll error: {ex.Message}");
            }

            return list;
        }

        /// <summary>
        /// Список для левой колонки: только то, что видно в строке.
        ///
        /// Черты и действия не разбираются: на пятистах существах разбор JSON
        /// занимает заметную часть времени открытия вкладки, а в списке они
        /// не нужны. Полная карточка читается через Load(id).
        /// </summary>
        public static List<MonsterData> LoadAllBrief()
        {
            var list = new List<MonsterData>();
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT id_monster, name, size, creature_type, subtype, alignment, armor_class, " +
                    "hit_points, challenge_rating, source, is_custom, photo_path " +
                    "FROM Bestiary ORDER BY name COLLATE NOCASE", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new MonsterData
                    {
                        Id = reader["id_monster"] == DBNull.Value ? 0 : Convert.ToInt32(reader["id_monster"]),
                        Name = reader["name"] as string ?? "",
                        Size = reader["size"] as string ?? "",
                        CreatureType = reader["creature_type"] as string ?? "",
                        Subtype = reader["subtype"] as string ?? "",
                        Alignment = reader["alignment"] as string ?? "",
                        ArmorClass = reader["armor_class"] == DBNull.Value ? 10 : Convert.ToInt32(reader["armor_class"]),
                        HitPoints = reader["hit_points"] == DBNull.Value ? 1 : Convert.ToInt32(reader["hit_points"]),
                        ChallengeRating = reader["challenge_rating"] as string ?? "",
                        Source = reader["source"] as string ?? "",
                        IsCustom = reader["is_custom"] != DBNull.Value && Convert.ToInt32(reader["is_custom"]) == 1,
                        PhotoPath = reader["photo_path"] as string ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] LoadAllBrief error: {ex.Message}");
            }

            return list;
        }
        /// <summary>Одно существо по идентификатору или null.</summary>
        public static MonsterData Load(int id)
        {
            if (id <= 0) return null;
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT * FROM Bestiary WHERE id_monster = @id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (reader.Read()) return ReadMonster(reader);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] Load error: {ex.Message}");
            }

            return null;
        }

        public static int Count()
        {
            EnsureTables();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT COUNT(*) FROM Bestiary", conn);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] Count error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Список типов существ, которые уже встречаются в бестиарии.</summary>
        public static List<string> LoadUsedTypes()
        {
            var result = new List<string>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT DISTINCT creature_type AS t FROM Bestiary WHERE TRIM(COALESCE(creature_type,'')) <> '' ORDER BY t COLLATE NOCASE", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string value = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (value.Length > 0) result.Add(value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] LoadUsedTypes error: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Поиск: все слова запроса обязаны найтись (как в магазине), точное
        /// совпадение имени поднимается наверх.
        /// </summary>
        public static List<MonsterData> Filter(List<MonsterData> source, string search,
            string creatureType = "", string size = "", bool onlyCustom = false,
            string challengeFrom = "", string challengeTo = "", string bookSource = "")
        {
            IEnumerable<MonsterData> query = source ?? new List<MonsterData>();

            if (onlyCustom) query = query.Where(m => m.IsCustom);

            // Фильтр по источнику: MM, VGtM, SRD…  (в базе код хранится как есть)
            if (!string.IsNullOrWhiteSpace(bookSource))
                query = query.Where(m => string.Equals((m.Source ?? "").Trim(), bookSource.Trim(), StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(creatureType))
                query = query.Where(m => string.Equals(m.CreatureType?.Trim(), creatureType.Trim(), StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(size))
                query = query.Where(m => string.Equals(m.Size?.Trim(), size.Trim(), StringComparison.OrdinalIgnoreCase));

            double from = 0, to = 0;
            bool hasFrom = DiceFormulaParser.TryParseChallenge(challengeFrom, out from, out _);
            bool hasTo = DiceFormulaParser.TryParseChallenge(challengeTo, out to, out _);
            if (hasFrom) query = query.Where(m => RatingOf(m) >= from);
            if (hasTo) query = query.Where(m => RatingOf(m) <= to);

            var words = (search ?? "")
                .Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim().ToLowerInvariant())
                .Where(w => w.Length > 0)
                .ToList();

            if (words.Count == 0) return query.ToList();

            var matched = new List<MonsterData>();
            foreach (var monster in query)
            {
                string haystack = string.Join(" ",
                    monster.Name, monster.NameEn, monster.CreatureType, monster.Subtype,
                    monster.Size, monster.Tags, monster.Source, monster.Habitat, monster.Alignment,
                    monster.Description).ToLowerInvariant();

                if (words.All(w => haystack.Contains(w))) matched.Add(monster);
            }

            // Точное совпадение имени — наверх, чтобы «волк» не терялся среди «волколаков»
            string whole = (search ?? "").Trim().ToLowerInvariant();
            return matched
                .OrderByDescending(m => string.Equals((m.Name ?? "").Trim().ToLowerInvariant(), whole, StringComparison.Ordinal))
                .ThenByDescending(m => (m.Name ?? "").ToLowerInvariant().StartsWith(whole, StringComparison.Ordinal))
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Опасность существ числом: «1/4» → 0.25. Непонятный текст даёт 0.</summary>
        public static double RatingOf(MonsterData monster)
        {
            if (monster == null) return 0;
            return DiceFormulaParser.TryParseChallenge(monster.ChallengeRating, out double rating, out _) ? rating : 0;
        }

        #endregion

        #region === ЗАПИСЬ ===

        /// <summary>
        /// Сохраняет существо. Новое получает идентификатор, существующее
        /// обновляется. Возвращает идентификатор или 0 при ошибке.
        /// </summary>
        public static int Save(MonsterData monster)
        {
            if (monster == null) return 0;
            if (!monster.Validate(out string error))
            {
                Debug.WriteLine($"[BestiaryService] Save отклонён: {error}");
                return 0;
            }

            EnsureTables();
            monster.Name = monster.Name.Trim();
            monster.UpdatedAt = DateTime.Now;
            if (monster.CreatedAt == default(DateTime)) monster.CreatedAt = DateTime.Now;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                string sql = monster.Id > 0
                    ? UpdateSql
                    : InsertSql;

                using var cmd = new SqliteCommand(sql, conn, tx);
                FillParameters(cmd, monster);
                if (monster.Id > 0) cmd.Parameters.AddWithValue("@id", monster.Id);

                cmd.ExecuteNonQuery();

                if (monster.Id <= 0)
                {
                    using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn, tx);
                    monster.Id = Convert.ToInt32(idCmd.ExecuteScalar());
                }

                tx.Commit();
                return monster.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] Save error: {ex.Message}");
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
                using var cmd = new SqliteCommand("DELETE FROM Bestiary WHERE id_monster = @id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] Delete error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Полная копия существа: удобно делать вариант «вождь гоблинов».</summary>
        public static int Duplicate(int id, string newName)
        {
            var source = Load(id);
            if (source == null) return 0;

            var copy = source.Clone();
            copy.Id = 0;
            copy.IsCustom = true;
            copy.Name = string.IsNullOrWhiteSpace(newName) ? source.Name + " (копия)" : newName.Trim();
            copy.CreatedAt = DateTime.Now;
            copy.UpdatedAt = DateTime.Now;
            return Save(copy);
        }

        /// <summary>
        /// Прикрепляет картинку к существу.
        ///
        /// Файл кладём в AppData\HMWT\Monsters — ровно туда, откуда его находит
        /// PhotoHelper.ResolvePhotoPath. Существо сначала сохраняется: имя файла
        /// содержит идентификатор, а у нового существа его ещё нет.
        /// </summary>
        public static bool AttachPhoto(MonsterData monster, string sourceFilePath)
        {
            if (monster == null) return false;
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !System.IO.File.Exists(sourceFilePath)) return false;

            try
            {
                if (monster.Id <= 0 && Save(monster) <= 0) return false;

                string extension = System.IO.Path.GetExtension(sourceFilePath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                string dir = System.IO.Path.Combine(PhotoHelper.GetWritableRoot(), "Monsters");
                System.IO.Directory.CreateDirectory(dir);

                // Прежние картинки этого существа убираем: расширение могло измениться
                try
                {
                    foreach (string old in System.IO.Directory.GetFiles(dir, $"monster_{monster.Id}_*"))
                    {
                        try { System.IO.File.Delete(old); } catch { }
                    }
                }
                catch { }

                string fileName = $"monster_{monster.Id}_{DateTime.Now:yyyyMMddHHmmss}{extension.ToLowerInvariant()}";
                System.IO.File.Copy(sourceFilePath, System.IO.Path.Combine(dir, fileName), true);

                monster.PhotoPath = "Monsters/" + fileName;
                return Save(monster) > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] AttachPhoto error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Убирает картинку существа с диска и из записи.</summary>
        public static bool RemovePhoto(MonsterData monster)
        {
            if (monster == null) return false;
            try
            {
                if (!string.IsNullOrWhiteSpace(monster.PhotoPath)) PhotoHelper.DeletePhoto(monster.PhotoPath);
                monster.PhotoPath = "";
                return Save(monster) > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] RemovePhoto error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Убирает картинки существ, которых уже нет в бестиарии.</summary>
        public static int CleanupOrphanPhotos()
        {
            int removed = 0;
            try
            {
                string dir = System.IO.Path.Combine(PhotoHelper.GetWritableRoot(), "Monsters");
                if (!System.IO.Directory.Exists(dir)) return 0;

                var used = new HashSet<string>(
                    LoadAll().Select(m => (m.PhotoPath ?? "").Replace('\\', '/')).Where(p => p.Length > 0),
                    StringComparer.OrdinalIgnoreCase);

                foreach (string file in System.IO.Directory.GetFiles(dir))
                {
                    string relative = "Monsters/" + System.IO.Path.GetFileName(file);
                    if (used.Contains(relative)) continue;
                    try
                    {
                        System.IO.File.Delete(file);
                        removed++;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] CleanupOrphanPhotos error: {ex.Message}");
            }
            return removed;
        }

        #endregion

        #region === ПРИМЕРЫ ===

        /// <summary>
        /// Добавляет двух существ-образцов. Нужно, чтобы пустой бестиарий не был
        /// тупиком: видно, как заполнять атаки и формулы.
        /// </summary>
        public static int AddSamples()
        {
            int added = 0;
            foreach (var sample in Samples())
            {
                if (Save(sample) > 0) added++;
            }
            return added;
        }

        private static IEnumerable<MonsterData> Samples()
        {
            yield return new MonsterData
            {
                Name = "Гоблин",
                NameEn = "Goblin",
                Size = "Маленький",
                CreatureType = "Гуманоид",
                Subtype = "гоблиноид",
                Alignment = "нейтрально-злой",
                ArmorClass = 15,
                ArmorClassNote = "кожаный доспех, щит",
                HitPoints = 7,
                HitPointsFormula = "2к6",
                SpeedWalk = 30,
                Strength = 8,
                Dexterity = 14,
                Constitution = 10,
                Intelligence = 10,
                Wisdom = 8,
                Charisma = 8,
                Skills = "Скрытность +6",
                Senses = "тёмное зрение 60 футов, пассивное Восприятие 9",
                Languages = "Общий, Гоблинский",
                ChallengeRating = "1/4",
                Experience = 50,
                ProficiencyBonus = 2,
                Habitat = "лес, луг, подземье, холмы",
                Source = "MM14",
                Tags = "пример",
                Traits = new List<MonsterTraitData>
                {
                    new MonsterTraitData { Name = "Ловкий побег", Text = "Гоблин может бонусным действием совершить действие Отход или Засаду." }
                },
                Actions = new List<MonsterActionData>
                {
                    new MonsterActionData
                    {
                        Section = ActionSections.Action,
                        Name = "Скимитар",
                        Kind = "Рукопашная атака оружием",
                        HitBonus = "+4",
                        Reach = "5 футов",
                        Damage = "1к6+2 рубящим"
                    },
                    new MonsterActionData
                    {
                        Section = ActionSections.Action,
                        Name = "Короткий лук",
                        Kind = "Дальнобойная атака оружием",
                        HitBonus = "+4",
                        Range = "80/320 футов",
                        Damage = "1к6+2 колющим"
                    }
                }
            };

            yield return new MonsterData
            {
                Name = "Волк",
                NameEn = "Wolf",
                Size = "Средний",
                CreatureType = "Зверь",
                Alignment = "без мировоззрения",
                ArmorClass = 13,
                ArmorClassNote = "природный доспех",
                HitPoints = 11,
                HitPointsFormula = "2к8+2",
                SpeedWalk = 40,
                Strength = 12,
                Dexterity = 15,
                Constitution = 12,
                Intelligence = 3,
                Wisdom = 12,
                Charisma = 6,
                Skills = "Восприятие +3, Скрытность +4",
                Senses = "пассивное Восприятие 13",
                Languages = "—",
                ChallengeRating = "1/4",
                Experience = 50,
                ProficiencyBonus = 2,
                Habitat = "лес, луг, холмы",
                Source = "MM14",
                Tags = "пример",
                Traits = new List<MonsterTraitData>
                {
                    new MonsterTraitData { Name = "Острый слух и тонкий нюх", Text = "Преимущество на проверки Восприятия, опирающиеся на слух или нюх." },
                    new MonsterTraitData { Name = "Тактика стаи", Text = "Преимущество на бросок атаки по существу, если рядом с ним есть дееспособный союзник волка." }
                },
                Actions = new List<MonsterActionData>
                {
                    new MonsterActionData
                    {
                        Section = ActionSections.Action,
                        Name = "Укус",
                        Kind = "Рукопашная атака оружием",
                        HitBonus = "+4",
                        Reach = "5 футов",
                        Damage = "2к4+2 колющим",
                        SaveDc = "Сл 11 Силы или цель сбита с ног"
                    }
                }
            };
        }

        #endregion

        #region === SQL И РАЗБОР СТРОКИ ===

        private const string InsertSql = @"
            INSERT INTO Bestiary (
                name, name_en, size, creature_type, subtype, alignment, photo_path,
                armor_class, armor_class_note, hit_points, hit_points_formula,
                speed_walk, speed_fly, speed_swim, speed_climb, speed_burrow, speed_note, hover,
                strength, dexterity, constitution, intelligence, wisdom, charisma,
                saving_throws, skills, damage_vulnerabilities, damage_resistances,
                damage_immunities, condition_immunities, senses, languages,
                challenge_rating, experience, proficiency_bonus, habitat, treasure,
                source, tags, legendary_intro, lair_text, spells, equipment, description,
                traits_json, actions_json, is_custom, created_at, updated_at)
            VALUES (
                @name, @name_en, @size, @creature_type, @subtype, @alignment, @photo_path,
                @armor_class, @armor_class_note, @hit_points, @hit_points_formula,
                @speed_walk, @speed_fly, @speed_swim, @speed_climb, @speed_burrow, @speed_note, @hover,
                @strength, @dexterity, @constitution, @intelligence, @wisdom, @charisma,
                @saving_throws, @skills, @damage_vulnerabilities, @damage_resistances,
                @damage_immunities, @condition_immunities, @senses, @languages,
                @challenge_rating, @experience, @proficiency_bonus, @habitat, @treasure,
                @source, @tags, @legendary_intro, @lair_text, @spells, @equipment, @description,
                @traits_json, @actions_json, @is_custom, @created_at, @updated_at)";

        private const string UpdateSql = @"
            UPDATE Bestiary SET
                name = @name, name_en = @name_en, size = @size, creature_type = @creature_type,
                subtype = @subtype, alignment = @alignment, photo_path = @photo_path,
                armor_class = @armor_class, armor_class_note = @armor_class_note,
                hit_points = @hit_points, hit_points_formula = @hit_points_formula,
                speed_walk = @speed_walk, speed_fly = @speed_fly, speed_swim = @speed_swim,
                speed_climb = @speed_climb, speed_burrow = @speed_burrow,
                speed_note = @speed_note, hover = @hover,
                strength = @strength, dexterity = @dexterity, constitution = @constitution,
                intelligence = @intelligence, wisdom = @wisdom, charisma = @charisma,
                saving_throws = @saving_throws, skills = @skills,
                damage_vulnerabilities = @damage_vulnerabilities,
                damage_resistances = @damage_resistances,
                damage_immunities = @damage_immunities,
                condition_immunities = @condition_immunities,
                senses = @senses, languages = @languages,
                challenge_rating = @challenge_rating, experience = @experience,
                proficiency_bonus = @proficiency_bonus, habitat = @habitat, treasure = @treasure,
                source = @source, tags = @tags, legendary_intro = @legendary_intro,
                lair_text = @lair_text, spells = @spells, equipment = @equipment,
                description = @description, traits_json = @traits_json, actions_json = @actions_json,
                is_custom = @is_custom, created_at = @created_at, updated_at = @updated_at
            WHERE id_monster = @id";

        private static void FillParameters(SqliteCommand cmd, MonsterData m)
        {
            void Add(string name, object value) => cmd.Parameters.AddWithValue(name, value ?? "");

            Add("@name", m.Name);
            Add("@name_en", m.NameEn);
            Add("@size", m.Size);
            Add("@creature_type", m.CreatureType);
            Add("@subtype", m.Subtype);
            Add("@alignment", m.Alignment);
            Add("@photo_path", m.PhotoPath);
            Add("@armor_class", m.ArmorClass);
            Add("@armor_class_note", m.ArmorClassNote);
            Add("@hit_points", m.HitPoints);
            Add("@hit_points_formula", m.HitPointsFormula);
            Add("@speed_walk", m.SpeedWalk);
            Add("@speed_fly", m.SpeedFly);
            Add("@speed_swim", m.SpeedSwim);
            Add("@speed_climb", m.SpeedClimb);
            Add("@speed_burrow", m.SpeedBurrow);
            Add("@speed_note", m.SpeedNote);
            Add("@hover", m.Hover ? 1 : 0);
            Add("@strength", m.Strength);
            Add("@dexterity", m.Dexterity);
            Add("@constitution", m.Constitution);
            Add("@intelligence", m.Intelligence);
            Add("@wisdom", m.Wisdom);
            Add("@charisma", m.Charisma);
            Add("@saving_throws", m.SavingThrows);
            Add("@skills", m.Skills);
            Add("@damage_vulnerabilities", m.DamageVulnerabilities);
            Add("@damage_resistances", m.DamageResistances);
            Add("@damage_immunities", m.DamageImmunities);
            Add("@condition_immunities", m.ConditionImmunities);
            Add("@senses", m.Senses);
            Add("@languages", m.Languages);
            Add("@challenge_rating", m.ChallengeRating);
            Add("@experience", m.Experience);
            Add("@proficiency_bonus", m.ProficiencyBonus);
            Add("@habitat", m.Habitat);
            Add("@treasure", m.Treasure);
            Add("@source", m.Source);
            Add("@tags", m.Tags);
            Add("@legendary_intro", m.LegendaryIntro);
            Add("@lair_text", m.LairText);
            Add("@spells", m.Spells);
            Add("@equipment", m.Equipment);
            Add("@description", m.Description);
            Add("@traits_json", SerializeTraits(m.Traits));
            Add("@actions_json", SerializeActions(m.Actions));
            Add("@is_custom", m.IsCustom ? 1 : 0);
            Add("@created_at", m.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            Add("@updated_at", m.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private static MonsterData ReadMonster(SqliteDataReader r)
        {
            var m = new MonsterData
            {
                Id = ReadInt(r, "id_monster"),
                Name = ReadText(r, "name"),
                NameEn = ReadText(r, "name_en"),
                Size = ReadText(r, "size"),
                CreatureType = ReadText(r, "creature_type"),
                Subtype = ReadText(r, "subtype"),
                Alignment = ReadText(r, "alignment"),
                PhotoPath = ReadText(r, "photo_path"),
                ArmorClass = ReadInt(r, "armor_class"),
                ArmorClassNote = ReadText(r, "armor_class_note"),
                HitPoints = ReadInt(r, "hit_points"),
                HitPointsFormula = ReadText(r, "hit_points_formula"),
                SpeedWalk = ReadInt(r, "speed_walk"),
                SpeedFly = ReadInt(r, "speed_fly"),
                SpeedSwim = ReadInt(r, "speed_swim"),
                SpeedClimb = ReadInt(r, "speed_climb"),
                SpeedBurrow = ReadInt(r, "speed_burrow"),
                SpeedNote = ReadText(r, "speed_note"),
                Hover = ReadInt(r, "hover") != 0,
                Strength = ReadInt(r, "strength"),
                Dexterity = ReadInt(r, "dexterity"),
                Constitution = ReadInt(r, "constitution"),
                Intelligence = ReadInt(r, "intelligence"),
                Wisdom = ReadInt(r, "wisdom"),
                Charisma = ReadInt(r, "charisma"),
                SavingThrows = ReadText(r, "saving_throws"),
                Skills = ReadText(r, "skills"),
                DamageVulnerabilities = ReadText(r, "damage_vulnerabilities"),
                DamageResistances = ReadText(r, "damage_resistances"),
                DamageImmunities = ReadText(r, "damage_immunities"),
                ConditionImmunities = ReadText(r, "condition_immunities"),
                Senses = ReadText(r, "senses"),
                Languages = ReadText(r, "languages"),
                ChallengeRating = ReadText(r, "challenge_rating"),
                Experience = ReadInt(r, "experience"),
                ProficiencyBonus = ReadInt(r, "proficiency_bonus"),
                Habitat = ReadText(r, "habitat"),
                Treasure = ReadText(r, "treasure"),
                Source = ReadText(r, "source"),
                Tags = ReadText(r, "tags"),
                LegendaryIntro = ReadText(r, "legendary_intro"),
                LairText = ReadText(r, "lair_text"),
                Spells = ReadText(r, "spells"),
                Equipment = ReadText(r, "equipment"),
                Description = ReadText(r, "description"),
                IsCustom = ReadInt(r, "is_custom") != 0
            };

            if (m.ArmorClass <= 0) m.ArmorClass = 10;
            if (m.HitPoints <= 0) m.HitPoints = 1;
            if (m.Strength == 0 && m.Dexterity == 0 && m.Constitution == 0)
            {
                m.Strength = m.Dexterity = m.Constitution = 10;
                m.Intelligence = m.Wisdom = m.Charisma = 10;
            }
            if (m.ProficiencyBonus <= 0) m.ProficiencyBonus = 2;

            m.Traits = DeserializeTraits(ReadText(r, "traits_json"));
            m.Actions = DeserializeActions(ReadText(r, "actions_json"));

            DateTime created = ParseDate(ReadText(r, "created_at"));
            DateTime updated = ParseDate(ReadText(r, "updated_at"));
            m.CreatedAt = created == default(DateTime) ? DateTime.Now : created;
            m.UpdatedAt = updated == default(DateTime) ? m.CreatedAt : updated;

            return m;
        }

        /// <summary>
        /// Разбор JSON-списков. Битые данные не должны ронять бестиарий:
        /// существо откроется без черт, но откроется.
        /// </summary>
        public static string SerializeTraits(List<MonsterTraitData> traits)
        {
            try
            {
                var clean = (traits ?? new List<MonsterTraitData>())
                    .Where(t => t != null && (!string.IsNullOrWhiteSpace(t.Name) || !string.IsNullOrWhiteSpace(t.Text)))
                    .ToList();
                return JsonSerializer.Serialize(clean, JsonOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] SerializeTraits: {ex.Message}");
                return "[]";
            }
        }

        public static string SerializeActions(List<MonsterActionData> actions)
        {
            try
            {
                var clean = (actions ?? new List<MonsterActionData>())
                    .Where(a => a != null && (!string.IsNullOrWhiteSpace(a.Name) || !string.IsNullOrWhiteSpace(a.Description)
                                             || !string.IsNullOrWhiteSpace(a.Damage)))
                    .ToList();
                foreach (var a in clean) a.Section = ActionSections.Normalize(a.Section);
                return JsonSerializer.Serialize(clean, JsonOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] SerializeActions: {ex.Message}");
                return "[]";
            }
        }

        public static List<MonsterTraitData> DeserializeTraits(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return new List<MonsterTraitData>();
                return JsonSerializer.Deserialize<List<MonsterTraitData>>(json, JsonOptions) ?? new List<MonsterTraitData>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] DeserializeTraits: {ex.Message}");
                return new List<MonsterTraitData>();
            }
        }

        public static List<MonsterActionData> DeserializeActions(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return new List<MonsterActionData>();
                var list = JsonSerializer.Deserialize<List<MonsterActionData>>(json, JsonOptions)
                           ?? new List<MonsterActionData>();
                foreach (var a in list) a.Section = ActionSections.Normalize(a.Section);
                return list;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BestiaryService] DeserializeActions: {ex.Message}");
                return new List<MonsterActionData>();
            }
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
        {
            if (DateTime.TryParse(text, out DateTime value)) return value;
            return default(DateTime);
        }

        #endregion
    }
}
