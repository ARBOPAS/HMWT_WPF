using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Transactions;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using static System.Net.Mime.MediaTypeNames;

namespace HMWT_MUI
{
    /// <summary>
    /// Централизованный помощник для работы с базой данных SQLite.
    /// Все операции с БД должны проходить через этот класс.
    /// </summary>
    public static class DatabaseHelper
    {
        private static string _connectionString;
        private static string _databasePath;

        /// <summary>Какой файл базы уже подготовлен в этом запуске.</summary>
        private static string _initializedFile;

        #region === ИНИЦИАЛИЗАЦИЯ И ПОДКЛЮЧЕНИЕ ===

        /// <summary>
        /// Инициализирует базу данных: создаёт папку, создаёт таблицы и применяет миграции.
        ///
        /// Работает один раз на файл базы за запуск: метод зовут и при старте
        /// приложения, и при создании главного окна, и из GetConnection — а под
        /// ним создание трёх десятков таблиц, проверка колонок и засев справочника.
        /// </summary>
        public static void Initialize(string dbFileName = "HMWT_app.db")
        {
            if (_initializedFile == dbFileName && !string.IsNullOrEmpty(_connectionString))
                return;

            Debug.WriteLine($"\n[DB] >>> ИНИЦИАЛИЗАЦИЯ БД <<<");

            // 1. Создаём папку в AppData
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appDir = Path.Combine(appDataPath, "HMWT");
            if (!Directory.Exists(appDir))
            {
                Directory.CreateDirectory(appDir);
            }

            _databasePath = Path.Combine(appDir, dbFileName);
            Debug.WriteLine($"[DB] Путь к БД: {_databasePath}");

            // 2. Копируем БД из ресурсов если её нет в AppData


            // Default Timeout ограничивает ожидание блокировки. По умолчанию
            // Microsoft.Data.Sqlite повторяет попытку 30 секунд, поэтому любая
            // коллизия блокировок выглядела как зависание на полминуты.
            // 5 секунд согласованы с busy_timeout, который ставится при открытии.
            _connectionString = $"Data Source={_databasePath};Default Timeout=5";
            Debug.WriteLine($"[DB] Строка подключения: {_connectionString}");

            // Журнал WAL: читатель и писатель не блокируют друг друга.
            // Режим хранится в самом файле базы, поэтому повторный вызов безвреден.
            EnableWalJournal();

            // 3. Создаём все таблицы и применяем миграции
            var ddlWatch = Stopwatch.StartNew();
            CreateAllTablesIfNotExists();
            ReportIfSlow("старт: создание таблиц и миграции", ddlWatch.ElapsedMilliseconds);

            if (File.Exists(_databasePath))
            {
                var fileInfo = new FileInfo(_databasePath);
                Debug.WriteLine($"[DB] Размер БД: {fileInfo.Length / 1024.0:F2} KB");
            }

            Debug.WriteLine($"[DB] <<< ИНИЦИАЛИЗАЦИЯ ЗАВЕРШЕНА >>>\n");
            _initializedFile = dbFileName;
        }

        /// <summary>
        /// Переводит базу в журнал WAL.
        ///
        /// Зачем: в обычном журнале (delete) активный читатель держит разделяемую
        /// блокировку файла, и запись с другого соединения не проходит. Драйвер
        /// повторяет попытку до CommandTimeout, после чего падает с «database is
        /// locked» — со стороны это выглядит как зависание на десятки секунд.
        /// В WAL читатели не мешают писателю.
        ///
        /// Режим журнала хранится в самом файле базы, так что выставляется один
        /// раз и переживает перезапуск; повторный вызов просто подтверждает режим.
        /// </summary>
        private static void EnableWalJournal()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                var mode = cmd.ExecuteScalar()?.ToString();
                Debug.WriteLine($"[DB] journal_mode = {mode}");
            }
            catch (Exception ex)
            {
                // Не критично: без WAL остаётся лишь риск блокировок,
                // ради которого читатели теперь закрываются перед записью
                Debug.WriteLine($"[DB] не удалось включить WAL: {ex.Message}");
            }
        }

        /// <summary>
        /// Создаёт новое подключение к БД.
        /// Включает foreign keys и busy_timeout для предотвращения блокировок.
        /// </summary>
        public static SqliteConnection GetConnection()
        {
            if (string.IsNullOrEmpty(_connectionString))
            {
                Initialize();
            }

            var conn = new SqliteConnection(_connectionString);

            // Включаем FK и busy_timeout при каждом открытии соединения
            conn.StateChange += (s, e) =>
            {
                if (e.CurrentState == ConnectionState.Open)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
                    cmd.ExecuteNonQuery();
                }
            };

            return conn;
        }

        public static string GetDatabasePath() => _databasePath;

        public static string GetPhotoBasePath()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appDataPath, "HMWT", "CharacterPhoto");
        }

        #endregion

        #region === СОЗДАНИЕ ТАБЛИЦ И МИГРАЦИИ ===

        private static void CreateAllTablesIfNotExists()
        {
            using var conn = GetConnection();
            conn.Open();
            using var transaction = conn.BeginTransaction();

            try
            {
                var tableQueries = new[]
                {
            @"CREATE TABLE IF NOT EXISTS CharacterInfo (
                id_character INTEGER PRIMARY KEY AUTOINCREMENT, 
                name TEXT, class TEXT, race TEXT, 
                HP INTEGER, Speed INTEGER, MasterLVL INTEGER, Exp INTEGER, Ideology INTEGER, PhotoPath TEXT,
                Strength INTEGER DEFAULT 10, Dexterity INTEGER DEFAULT 10, Constitution INTEGER DEFAULT 10,
                Intelligence INTEGER DEFAULT 10, Wisdom INTEGER DEFAULT 10, Charisma INTEGER DEFAULT 10, 
                ArmorClass INTEGER,
                PassivePerception INTEGER DEFAULT 10, PassiveInsight INTEGER DEFAULT 10, 
                PassiveInvestigation INTEGER DEFAULT 10, Initiative INTEGER DEFAULT 0);",

            @"CREATE TABLE IF NOT EXISTS ListOfCharacters (
                id_character INTEGER PRIMARY KEY, NumberOfCharacter INTEGER,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT);",

            @"CREATE TABLE IF NOT EXISTS CharacterStrenghtSkills (
                id_character INTEGER PRIMARY KEY, Athletics INTEGER DEFAULT 0, SaveThrow INTEGER DEFAULT 0,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS CharacterDexteritySkills (
                id_character INTEGER PRIMARY KEY, Acrobatics INTEGER DEFAULT 0, Stealth INTEGER DEFAULT 0, 
                SleightOfHand INTEGER DEFAULT 0, SaveThrow INTEGER DEFAULT 0,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS CharacterConstSkills (
                id_character INTEGER PRIMARY KEY, SaveThrow INTEGER DEFAULT 0,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS CharacterIntSkills (
                id_character INTEGER PRIMARY KEY, Analysis INTEGER DEFAULT 0, History INTEGER DEFAULT 0, 
                Magic INTEGER DEFAULT 0, Nature INTEGER DEFAULT 0, Religion INTEGER DEFAULT 0, SaveThrow INTEGER DEFAULT 0,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS CharacterWisdomSkills (
                id_character INTEGER PRIMARY KEY, Perception INTEGER DEFAULT 0, Survival INTEGER DEFAULT 0, 
                Medicine INTEGER DEFAULT 0, Insight INTEGER DEFAULT 0, AnimalCare INTEGER DEFAULT 0, SaveThrow INTEGER DEFAULT 0,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS CharacterCharismaSkills (
                id_character INTEGER PRIMARY KEY, Performance INTEGER DEFAULT 0, Deception INTEGER DEFAULT 0, 
                Persuasion INTEGER DEFAULT 0, Intimidation INTEGER DEFAULT 0, SaveThrow INTEGER DEFAULT 0,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS CharacterIdAddInfo (
                id_character INTEGER PRIMARY KEY, Languages TEXT, Traits TEXT, Attachments TEXT, Weakness TEXT,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS LevelTable (
                id_character INTEGER PRIMARY KEY, Level INTEGER DEFAULT 1, Exp INTEGER DEFAULT 0, ProficiencyBonus INTEGER DEFAULT 2,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS GoldTable (
                id_character INTEGER PRIMARY KEY, CurrentMoney TEXT, Gold INTEGER DEFAULT 0, Silver INTEGER DEFAULT 0, 
                Copper INTEGER DEFAULT 0, Platinum INTEGER DEFAULT 0, Electrum INTEGER DEFAULT 0,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS Inventory (
                id_item INTEGER PRIMARY KEY AUTOINCREMENT, id_character INTEGER NOT NULL, name TEXT NOT NULL, description TEXT, 
                category TEXT, quantity INTEGER DEFAULT 1, weight REAL DEFAULT 0, cost_gold INTEGER DEFAULT 0, 
                cost_silver INTEGER DEFAULT 0, cost_copper INTEGER DEFAULT 0, damage_dice TEXT, damage_type TEXT, 
                armor_class_bonus INTEGER DEFAULT 0, is_equipped INTEGER DEFAULT 0, is_attuned INTEGER DEFAULT 0, 
                charges INTEGER DEFAULT 0, rarity TEXT, notes TEXT, PhotoPath TEXT DEFAULT '',
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS SpellReference (
                id_spell INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, level INTEGER NOT NULL, school TEXT NOT NULL, 
                casting_time TEXT NOT NULL, range TEXT NOT NULL, components TEXT NOT NULL, duration TEXT NOT NULL, 
                description TEXT NOT NULL, at_higher_levels TEXT, classes TEXT NOT NULL, ritual INTEGER DEFAULT 0, 
                concentration INTEGER DEFAULT 0, source TEXT DEFAULT 'PHB', material TEXT, page TEXT, archetype TEXT, 
                domains TEXT, source_json TEXT, language TEXT DEFAULT 'en');",

            @"CREATE TABLE IF NOT EXISTS CharacterSpells (
                id_char_spell INTEGER PRIMARY KEY AUTOINCREMENT, id_character INTEGER NOT NULL, id_spell INTEGER NOT NULL, 
                is_prepared INTEGER DEFAULT 0, spell_slot_level INTEGER, uses_left INTEGER DEFAULT 0, notes TEXT,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE,
                FOREIGN KEY (id_spell) REFERENCES SpellReference(id_spell) ON DELETE CASCADE,
                UNIQUE(id_character, id_spell));",

            @"CREATE TABLE IF NOT EXISTS CharacterSpellSlots (
                id_slot INTEGER PRIMARY KEY AUTOINCREMENT, id_character INTEGER NOT NULL, slot_level INTEGER NOT NULL, 
                total_slots INTEGER NOT NULL DEFAULT 0, used_slots INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE,
                UNIQUE(id_character, slot_level));",

            // Состояния персонажа: отметок бывает несколько, а справочник можно
            // переимпортировать или не импортировать вовсе — поэтому хранится не
            // название, а ключ записи SRD (srd_index). Подпись берётся из
            // справочника, а если его нет — из встроенного списка.
            @"CREATE TABLE IF NOT EXISTS CharacterConditions (
                id_character INTEGER NOT NULL,
                condition_key TEXT NOT NULL,
                PRIMARY KEY (id_character, condition_key),
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            // Экспертиза навыка: владение хранится галочкой в таблице характеристик,
            // а экспертиза — вторым бонусом мастерства. Отдельной таблицей, а не
            // второй колонкой рядом с каждой из восемнадцати: колонок стало бы
            // вдвое больше, и половина всегда пустовала бы.
            // Бонусы к характеристикам, которые игрок заводит сам: у одного
            // навыка и проверки их бывает несколько, поэтому отдельными строками.
            // Таблица раньше создавалась прямо в листе персонажа — схема жила
            // в окне, и её не было видно ни в одном другом месте программы.
            @"CREATE TABLE IF NOT EXISTS CharacterAbilityBonuses (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                id_character INTEGER NOT NULL,
                ability TEXT NOT NULL,
                scope TEXT NOT NULL,
                name TEXT,
                bonus INTEGER NOT NULL DEFAULT 0,
                is_active INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS CharacterSkillExpertise (
                id_character INTEGER NOT NULL,
                skill_key TEXT NOT NULL,
                PRIMARY KEY (id_character, skill_key),
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS DiceRollHistory (
                id_roll INTEGER PRIMARY KEY AUTOINCREMENT, id_character INTEGER, timestamp DATETIME DEFAULT CURRENT_TIMESTAMP, 
                roll_type TEXT, description TEXT, result INTEGER,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS SrdReference (
                id_srd INTEGER PRIMARY KEY AUTOINCREMENT,
                category TEXT NOT NULL,   
                name TEXT NOT NULL,
                description TEXT,
                properties TEXT,            
                publisher TEXT,
                book TEXT,
                UNIQUE(category, name, book));",

            // Каталог предметов для вкладки «Магазин» (импорт Data\srd\items.json).
            // Отдельная таблица, а не SrdReference: фильтры идут по конкретным полям
            // (тип, редкость, урон, доспех), искать их внутри JSON было бы медленно.
            @"CREATE TABLE IF NOT EXISTS SrdItem (
                id_srd_item INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                description TEXT,
                item_type TEXT,
                subtype TEXT,
                rarity TEXT,
                weight REAL DEFAULT 0,
                damage TEXT,
                damage_type TEXT,
                ac INTEGER DEFAULT 0,
                range TEXT,
                properties_text TEXT,
                attunement INTEGER DEFAULT 0,
                is_magic INTEGER DEFAULT 0,
                source TEXT,
                book TEXT,
                raw_properties TEXT,
                photo_path TEXT,
                list_photo_path TEXT,
                UNIQUE(name, book));",

            @"CREATE TABLE IF NOT EXISTS CharacterNoteGroups (
                id_group INTEGER PRIMARY KEY AUTOINCREMENT,
                id_character INTEGER NOT NULL,
                name TEXT NOT NULL,
                id_parent INTEGER,
                sort_order INTEGER DEFAULT 0,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE,
                FOREIGN KEY (id_parent) REFERENCES CharacterNoteGroups(id_group) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS CharacterNotes (
                id_note INTEGER PRIMARY KEY AUTOINCREMENT,
                id_character INTEGER NOT NULL,
                id_group INTEGER,
                title TEXT NOT NULL,
                content_xaml TEXT,
                sort_order INTEGER DEFAULT 0,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE,
                FOREIGN KEY (id_group) REFERENCES CharacterNoteGroups(id_group) ON DELETE CASCADE);",

            @"CREATE TABLE IF NOT EXISTS NoteAttachments (
                id_attachment INTEGER PRIMARY KEY AUTOINCREMENT,
                id_note INTEGER NOT NULL,
                stored_name TEXT NOT NULL,
                original_name TEXT NOT NULL,
                FOREIGN KEY (id_note) REFERENCES CharacterNotes(id_note) ON DELETE CASCADE);",

            @"CREATE INDEX IF NOT EXISTS idx_notes_char ON CharacterNotes(id_character);",
            @"CREATE INDEX IF NOT EXISTS idx_notes_group ON CharacterNotes(id_group);"
        };

                // ВЫПОЛНЯЕМ ВСЕ ЗАПРОСЫ СОЗДАНИЯ ТАБЛИЦ
                foreach (var query in tableQueries)
                {
                    using var cmd = new SqliteCommand(query, conn, transaction);
                    cmd.ExecuteNonQuery();
                }

                // Миграции
                EnsureColumn(conn, transaction, "CharacterNotes", "content_html TEXT DEFAULT ''");
                EnsureColumn(conn, transaction, "CharacterNotes", "content_md TEXT DEFAULT ''");
                EnsureColumn(conn, transaction, "CharacterNotes", "sort_order INTEGER DEFAULT 0");
                EnsureColumn(conn, transaction, "CharacterNoteGroups", "id_parent INTEGER");
                EnsureColumn(conn, transaction, "CharacterNoteGroups", "sort_order INTEGER DEFAULT 0");

                // Вдохновение — признак «есть или нет», поэтому 0/1 в самой
                // таблице персонажа: заводить под него отдельную таблицу не за чем
                EnsureColumn(conn, transaction, "CharacterInfo", "Inspiration INTEGER DEFAULT 0");

                // Концентрация хранится названием, а не ссылкой на заклинание:
                // запись из списка персонажа могут удалить, а подпись в шапке
                // должна остаться осмысленной
                EnsureColumn(conn, transaction, "CharacterInfo", "ConcentrationSpell TEXT DEFAULT ''");

                // Кости хитов: без них короткому отдыху нечего тратить. Размер
                // кости один — программа ведёт персонажа одной строкой класса
                EnsureColumns(conn, transaction, "CharacterInfo",
                    "HitDie INTEGER DEFAULT 8",
                    "HitDiceTotal INTEGER DEFAULT 0",
                    "HitDiceUsed INTEGER DEFAULT 0");

                EnsureColumns(conn, transaction, "CharacterSpells", "IndividualImagePath TEXT");
                EnsureColumns(conn, transaction, "CharacterIdAddInfo",
                    "Weapons TEXT", "Ideals TEXT", "Bonds TEXT", "Background TEXT",
                    "Height TEXT", "Weight TEXT", "Eyes TEXT", "Hair TEXT", "Backstory TEXT",
                    // Ссылка на предысторию из справочника: сам текст остаётся в Background,
                    // чтобы ничего не потерялось, если запись удалят
                    "id_background INTEGER");

                // Колонки, которые раньше догонял лист персонажа своим MigrateDatabase.
                // У баз, созданных до их появления, их нет, а CREATE TABLE IF NOT EXISTS
                // к существующей таблице колонок не добавляет
                EnsureColumns(conn, transaction, "CharacterInfo",
                    "Ideology INTEGER",
                    "PassivePerception INTEGER DEFAULT 10",
                    "PassiveInsight INTEGER DEFAULT 10",
                    "PassiveInvestigation INTEGER DEFAULT 10",
                    "Initiative INTEGER DEFAULT 0");

                CreateBackgroundTables(conn, transaction);

                CreateInventorySystemTables(conn, transaction);
                MigrateLegacyItemTags(conn, transaction);

                transaction.Commit();
                Debug.WriteLine("[DB] Все таблицы успешно созданы/проверены");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Debug.WriteLine($"[DB] Ошибка создания таблиц: {ex.Message}");
                throw;
            }

            // Засев предысторий — ПОСЛЕ фиксации транзакции.
            //
            // Раньше этот вызов стоял внутри транзакции, а он открывает своё
            // соединение и пишет в таблицу: первая же вставка упиралась в чужую
            // блокировку и ждала освобождения по 5 секунд, пока не сдавалась.
            // Итог — запуск программы занимал 34 секунды на каждой инициализации
            // (а она шла и при старте, и при создании главного окна).
            try { Services.BackgroundService.EnsureSeeded(); }
            catch (Exception ex) { Debug.WriteLine($"[DB] предыстории: {ex.Message}"); }
        }

        /// <summary>
        /// Справочник предысторий: помимо встроенного списка игрок может
        /// создавать свои. Хранятся поля по книге правил — владения навыками,
        /// инструментами и снаряжение, плюс заметка.
        /// </summary>
        private static void CreateBackgroundTables(SqliteConnection conn, SqliteTransaction tx)
        {
            using var cmd = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS BackgroundReference (
                    id_background INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    skill_proficiencies TEXT DEFAULT '',
                    tool_proficiencies TEXT DEFAULT '',
                    languages TEXT DEFAULT '',
                    equipment TEXT DEFAULT '',
                    feature TEXT DEFAULT '',
                    description TEXT DEFAULT '',
                    is_custom INTEGER DEFAULT 0,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT DEFAULT CURRENT_TIMESTAMP
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_background_name ON BackgroundReference(name);", conn, tx);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Добавляет колонку в таблицу, если её нет.
        /// </summary>
        public static void EnsureColumn(SqliteConnection conn, SqliteTransaction tx, string tableName, string columnDefinition)        {
            var parts = columnDefinition.Split(' ');
            var columnName = parts[0];

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA table_info({tableName})";

            bool columnExists = false;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    columnExists = true;
                    break;
                }
            }

            if (!columnExists)
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.Transaction = tx;
                alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnDefinition}";
                alterCmd.ExecuteNonQuery();
                System.Diagnostics.Debug.WriteLine($"[DB] Добавлена колонка {columnName} в {tableName}");
            }
        }




        /// <summary>
        /// Добавляет несколько колонок в таблицу.
        /// </summary>
        /// <summary>Ключ записи в исходной базе 5e SRD — поле index.</summary>
        private const string SrdIndexColumn = "srd_index TEXT DEFAULT ''";

        private static void EnsureColumns(SqliteConnection conn, SqliteTransaction transaction, string tableName, params string[] columnDefinitions)
        {
            foreach (var colDef in columnDefinitions)
            {
                EnsureColumn(conn, transaction, tableName, colDef);  // ИСПРАВЛЕНО: используем tableName и colDef
            }
        }

        private static void CreateInventorySystemTables(SqliteConnection conn, SqliteTransaction transaction)
        {
            var queries = new[]
            {
                @"CREATE TABLE IF NOT EXISTS EquipmentSlots (
                    id_slot INTEGER PRIMARY KEY AUTOINCREMENT,
                    slot_name TEXT NOT NULL,
                    slot_code TEXT NOT NULL UNIQUE,
                    icon_path TEXT,
                    sort_order INTEGER DEFAULT 0);",

                @"CREATE TABLE IF NOT EXISTS EquipmentGroups (
                    id_group INTEGER PRIMARY KEY AUTOINCREMENT,
                    id_character INTEGER NOT NULL,
                    id_slot INTEGER NOT NULL,
                    group_name TEXT,
                    position_x REAL DEFAULT 0,
                    position_y REAL DEFAULT 0,
                    rotation_angle REAL DEFAULT 0,
                    cell_count INTEGER DEFAULT 1,
                    is_expanded INTEGER DEFAULT 0,
                    group_size REAL NOT NULL DEFAULT 100,
                    FOREIGN KEY (id_character) REFERENCES CharacterInfo(id_character) ON DELETE CASCADE,
                    FOREIGN KEY (id_slot) REFERENCES EquipmentSlots(id_slot));",

                @"CREATE TABLE IF NOT EXISTS EquipmentCells (
                    id_cell INTEGER PRIMARY KEY AUTOINCREMENT,
                    id_group INTEGER NOT NULL,
                    id_item INTEGER,
                    cell_index INTEGER DEFAULT 0,
                    is_locked INTEGER DEFAULT 0,
                    FOREIGN KEY (id_group) REFERENCES EquipmentGroups(id_group) ON DELETE CASCADE,
                    FOREIGN KEY (id_item) REFERENCES Inventory(id_item) ON DELETE SET NULL);",

                @"CREATE TABLE IF NOT EXISTS Tags (
                    id_tag INTEGER PRIMARY KEY AUTOINCREMENT,
                    tag_name TEXT NOT NULL UNIQUE,
                    tag_color TEXT DEFAULT '#888888',
                    is_system INTEGER DEFAULT 0);",

                @"CREATE TABLE IF NOT EXISTS ItemTagMapping (
                    id_item INTEGER NOT NULL,
                    id_tag INTEGER NOT NULL,
                    PRIMARY KEY (id_item, id_tag),
                    FOREIGN KEY (id_item) REFERENCES Inventory(id_item) ON DELETE CASCADE,
                    FOREIGN KEY (id_tag) REFERENCES Tags(id_tag) ON DELETE CASCADE);"
            };

            foreach (var query in queries)
            {
                using var cmd = new SqliteCommand(query, conn, transaction);
                cmd.ExecuteNonQuery();
            }

            EnsureColumns(conn, transaction, "Inventory",
                "item_tag TEXT",
                "max_charges INTEGER DEFAULT 0",
                "is_stackable INTEGER DEFAULT 0",
                // Когда предмету возвращаются заряды: long — на длинном отдыхе
                // (так заряжается почти всё), short — на коротком, none — никогда.
                // Отдых смотрит именно сюда, а не на «есть заряды или нет»
                "recharge TEXT DEFAULT 'long'",
                // Отдельное фото для строки списка (раньше колонку досоздавал
                // конструктор InventoryService на каждое создание сервиса)
                "list_photo_path TEXT DEFAULT ''");

            // Каталог магазина: колонки добавляются и к уже существующей таблице
            EnsureColumns(conn, transaction, "SrdItem",
                "photo_path TEXT",
                // Отдельное фото именно для списка: в длинном списке нужна мелкая
                // картинка, а в карточке предмета — крупная, и это разные файлы
                "list_photo_path TEXT",
                "raw_properties TEXT",
                "subtype TEXT",
                SrdIndexColumn);

            // Ключ записи в исходной базе. Сам текст переведён не весь: index
            // одинаков в английских и русских файлах, поэтому по нему видно,
            // что запись уже есть, и вторая база только дополняет первую
            EnsureColumns(conn, transaction, "SpellReference", SrdIndexColumn);
            EnsureColumns(conn, transaction, "SrdReference", SrdIndexColumn);

            SeedDefaultEquipmentSlots(conn, transaction);
            SeedDefaultTags(conn, transaction);

            Debug.WriteLine("[DB] Таблицы инвентаря и тегов созданы");
        }

        private static void SeedDefaultEquipmentSlots(SqliteConnection conn, SqliteTransaction transaction)
        {
            using var cmdCount = new SqliteCommand("SELECT COUNT(*) FROM EquipmentSlots", conn, transaction);
            if (Convert.ToInt32(cmdCount.ExecuteScalar()) > 0) return;

            var defaultSlots = new[]
            {
                ("Голова", "head", 1), ("Туловище", "torso", 2), ("Руки", "hands", 3),
                ("Ноги", "legs", 4), ("Ступни", "feet", 5), ("Кольцо 1", "ring1", 6),
                ("Кольцо 2", "ring2", 7), ("Амулет", "amulet", 8), ("Рука (оружие)", "main_hand", 9),
                ("Рука (щит)", "off_hand", 10), ("Пояс", "belt", 11), ("Плащ", "cloak", 12)
            };

            foreach (var (name, code, order) in defaultSlots)
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO EquipmentSlots (slot_name, slot_code, sort_order) VALUES (@name, @code, @order)",
                    conn, transaction);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@code", code);
                cmd.Parameters.AddWithValue("@order", order);
                cmd.ExecuteNonQuery();
            }
        }

        private static void SeedDefaultTags(SqliteConnection conn, SqliteTransaction transaction)
        {
            using var cmdCount = new SqliteCommand("SELECT COUNT(*) FROM Tags", conn, transaction);
            if (Convert.ToInt32(cmdCount.ExecuteScalar()) > 0) return;

            var defaultTags = new[]
            {
                ("treasure", "#E0AE4E"), ("weapon", "#C0564F"), ("armor", "#4F7BC0"),
                ("consumable", "#5BB974"), ("misc", "#8B859C"), ("quest", "#9A7BD6")
            };

            foreach (var (name, color) in defaultTags)
            {
                using var cmd = new SqliteCommand(
                    "INSERT OR IGNORE INTO Tags (tag_name, tag_color) VALUES (@name, @color)",
                    conn, transaction);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@color", color);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Одноразовая миграция: легаси-таблица ItemTags → каноническая Tags.
        /// Копирует имена, переназначает ItemTagMapping по имени, чистит сирот, дропает ItemTags.
        /// </summary>
        private static void MigrateLegacyItemTags(SqliteConnection conn, SqliteTransaction tx)
        {
            using var check = new SqliteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ItemTags'", conn, tx);
            if (Convert.ToInt32(check.ExecuteScalar()) == 0) return;   // легаси нет — выходим

            // 1. Копируем имена тегов в Tags (UNIQUE не даст дублей)
            using (var copy = new SqliteCommand(
                "INSERT OR IGNORE INTO Tags (tag_name) SELECT tag_name FROM ItemTags", conn, tx))
                copy.ExecuteNonQuery();

            // 2. Переназначаем связи ItemTagMapping на id из Tags (по имени тега)
            using (var remap = new SqliteCommand(@"
        UPDATE ItemTagMapping
        SET id_tag = (SELECT t.id_tag FROM Tags t
                      INNER JOIN ItemTags it ON it.tag_name = t.tag_name
                      WHERE it.id_tag = ItemTagMapping.id_tag)
        WHERE EXISTS (SELECT 1 FROM ItemTags it WHERE it.id_tag = ItemTagMapping.id_tag)", conn, tx))
                remap.ExecuteNonQuery();

            // 3. Убираем дубли (id_item, id_tag) после переназначения
            using (var dedupe = new SqliteCommand(@"
        DELETE FROM ItemTagMapping
        WHERE rowid NOT IN (SELECT MIN(rowid) FROM ItemTagMapping GROUP BY id_item, id_tag)", conn, tx))
                dedupe.ExecuteNonQuery();

            // 4. Чистим висячие ссылки и дропаем легаси
            using (var orphans = new SqliteCommand(
                "DELETE FROM ItemTagMapping WHERE id_tag NOT IN (SELECT id_tag FROM Tags)", conn, tx))
                orphans.ExecuteNonQuery();
            using (var drop = new SqliteCommand("DROP TABLE ItemTags", conn, tx))
                drop.ExecuteNonQuery();

            Debug.WriteLine("[DB] Миграция ItemTags → Tags выполнена");
        }

        #endregion

        #region === УНИВЕРСАЛЬНЫЕ ТРАНЗАКЦИИ ===

        /// <summary>
        /// Выполняет действие в транзакции. Автоматически коммитит или откатывает.
        /// </summary>
        public static void RunInTransaction(Action<SqliteConnection, SqliteTransaction> action)
        {
            using var conn = GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                action(conn, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Выполняет функцию в транзакции и возвращает результат.
        /// </summary>
        public static T RunInTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> func)
        {
            using var conn = GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var result = func(conn, tx);
                tx.Commit();
                return result;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        #endregion

        #region === CRUD ОПЕРАЦИИ С ПЕРСОНАЖАМИ ===

        /// <summary>
        /// Создаёт нового персонажа со всеми дочерними записями.
        /// </summary>
        public static int CreateCharacter(string name, string charClass, string race,
            int hp, int speed, string photoPath = "",
            int strength = 10, int dexterity = 10, int constitution = 10,
            int intelligence = 10, int wisdom = 10, int charisma = 10,
            int armorClass = 10, int masterLVL = 1, int exp = 0)
        {
            return RunInTransaction((conn, tx) =>
            {
                string query = @"
                    INSERT INTO CharacterInfo 
                    (name, class, race, HP, Speed, PhotoPath, 
                     Strength, Dexterity, Constitution, 
                     Intelligence, Wisdom, Charisma, 
                     ArmorClass, MasterLVL, Exp) 
                    VALUES 
                    (@name, @class, @race, @hp, @speed, @photo,
                     @str, @dex, @con, @int, @wis, @cha,
                     @ac, @lvl, @exp);
                    SELECT last_insert_rowid();";

                using var cmd = new SqliteCommand(query, conn, tx);
                cmd.Parameters.AddWithValue("@name", name ?? "");
                cmd.Parameters.AddWithValue("@class", charClass ?? "");
                cmd.Parameters.AddWithValue("@race", race ?? "");
                cmd.Parameters.AddWithValue("@hp", hp);
                cmd.Parameters.AddWithValue("@speed", speed);
                cmd.Parameters.AddWithValue("@photo", photoPath ?? "");
                cmd.Parameters.AddWithValue("@str", strength);
                cmd.Parameters.AddWithValue("@dex", dexterity);
                cmd.Parameters.AddWithValue("@con", constitution);
                cmd.Parameters.AddWithValue("@int", intelligence);
                cmd.Parameters.AddWithValue("@wis", wisdom);
                cmd.Parameters.AddWithValue("@cha", charisma);
                cmd.Parameters.AddWithValue("@ac", armorClass);
                cmd.Parameters.AddWithValue("@lvl", masterLVL);
                cmd.Parameters.AddWithValue("@exp", exp);

                int characterId = Convert.ToInt32(cmd.ExecuteScalar());

                // Создаём записи во всех дочерних таблицах
                string[] skillsTables = {
                    "CharacterStrenghtSkills", "CharacterDexteritySkills", "CharacterConstSkills",
                    "CharacterIntSkills", "CharacterWisdomSkills", "CharacterCharismaSkills"
                };

                foreach (string table in skillsTables)
                {
                    using var cmdChild = new SqliteCommand(
                        $"INSERT INTO {table} (id_character) VALUES (@id)", conn, tx);
                    cmdChild.Parameters.AddWithValue("@id", characterId);
                    cmdChild.ExecuteNonQuery();
                }

                using var cmdLevel = new SqliteCommand(
                    "INSERT INTO LevelTable (id_character, Level, Exp, ProficiencyBonus) VALUES (@id, 1, 0, 2)",
                    conn, tx);
                cmdLevel.Parameters.AddWithValue("@id", characterId);
                cmdLevel.ExecuteNonQuery();

                using var cmdGold = new SqliteCommand(
                    "INSERT INTO GoldTable (id_character, Gold, Silver, Copper) VALUES (@id, 0, 0, 0)",
                    conn, tx);
                cmdGold.Parameters.AddWithValue("@id", characterId);
                cmdGold.ExecuteNonQuery();

                using var cmdAddInfo = new SqliteCommand(
                    "INSERT INTO CharacterIdAddInfo (id_character) VALUES (@id)",
                    conn, tx);
                cmdAddInfo.Parameters.AddWithValue("@id", characterId);
                cmdAddInfo.ExecuteNonQuery();

                Debug.WriteLine($"[DB] Создан персонаж ID={characterId}: {name}");
                return characterId;
            });
        }

        /// <summary>
        /// Удаляет персонажа и все связанные данные (каскадное удаление через FK).
        /// </summary>
        public static void DeleteCharacter(int id)
        {
            RunInTransaction((conn, tx) =>
            {
                using var cmdList = new SqliteCommand(
                    "DELETE FROM ListOfCharacters WHERE id_character = @id", conn, tx);
                cmdList.Parameters.AddWithValue("@id", id);
                cmdList.ExecuteNonQuery();

                using var cmdChar = new SqliteCommand(
                    "DELETE FROM CharacterInfo WHERE id_character = @id", conn, tx);
                cmdChar.Parameters.AddWithValue("@id", id);
                cmdChar.ExecuteNonQuery();

                Debug.WriteLine($"[DB] Персонаж ID={id} удален");
            });
        }

        #endregion

        #region === ЗАПРОСЫ ===

        /// <summary>
        /// Возвращает всех персонажей с JOIN на LevelTable для актуального уровня.
        /// </summary>
        public static DataTable GetAllCharacters()
        {
            using var conn = GetConnection();
            conn.Open();

            string query = @"
                SELECT 
                    ci.id_character, 
                    ci.name, 
                    ci.class, 
                    ci.race,
                    COALESCE(lt.Level, ci.MasterLVL, 1) AS Level,
                    ci.HP, 
                    ci.PhotoPath
                FROM CharacterInfo ci
                LEFT JOIN LevelTable lt ON ci.id_character = lt.id_character
                ORDER BY ci.name";

            using var cmd = new SqliteCommand(query, conn);
            DataTable dt = new DataTable();
            using var reader = cmd.ExecuteReader();
            dt.Load(reader);
            return dt;
        }

        /// <summary>
        /// Возвращает полную информацию о персонаже по ID.
        /// </summary>
        public static DataRow GetCharacterById(int id)
        {
            using var conn = GetConnection();
            conn.Open();

            string query = "SELECT * FROM CharacterInfo WHERE id_character = @id";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", id);

            DataTable dt = new DataTable();
            using var reader = cmd.ExecuteReader();
            dt.Load(reader);

            return dt.Rows.Count > 0 ? dt.Rows[0] : null;
        }

        #endregion

        #region === ФОТО ===

        public static string GetCharacterPhoto(int characterId)
        {
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(
                "SELECT PhotoPath FROM CharacterInfo WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@id", characterId);

            return cmd.ExecuteScalar()?.ToString() ?? "";
        }

        public static void UpdateCharacterPhoto(int characterId, string photoPath)
        {
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(
                "UPDATE CharacterInfo SET PhotoPath = @photo WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@id", characterId);
            cmd.Parameters.AddWithValue("@photo", photoPath);
            cmd.ExecuteNonQuery();
            conn.Close();

            // Картинка могла лечь поверх прежнего файла с тем же именем — в кэше
            // кадров остался старый кадр. Забываем его до того, как окна начнут
            // перерисовывать аватар
            PhotoHelper.ForgetPhotoCache(photoPath);

            // Фото сменилось — об этом должны узнать открытые окна: иначе карточка
            // в главном окне остаётся со старым аватаром до перезапуска
            PhotoHelper.RaiseCharacterPhotoChanged(characterId);
        }

        #endregion

        #region === НАСТРОЙКИ ===

        public static string GetSetting(string key, string defaultValue = "")
        {
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(
                "SELECT Value FROM Settings WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);

            return cmd.ExecuteScalar()?.ToString() ?? defaultValue;
        }

        public static void SaveSetting(string key, string value)
        {
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@key, @value)", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }

        #endregion

        #region === ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ===

        /// <summary>
        /// Порог, после которого операция считается медленной и попадает в журнал.
        /// </summary>
        public const long SlowOperationMs = 150;

        /// <summary>
        /// Отмечает в журнале операцию, которая длилась подозрительно долго.
        ///
        /// Зачем: жалобы «программа думает» бесполезно разбирать догадками.
        /// Здесь замер превращается в конкретную строку с названием операции и
        /// числом, поэтому видно, что именно стопорится — запрос к базе,
        /// ожидание блокировки или построение интерфейса.
        ///
        /// Журнал: %APPDATA%\HMWT\db-trace.log (последние ~200 КБ).
        /// </summary>
        public static void ReportIfSlow(string what, long elapsedMs)
        {
            if (elapsedMs < SlowOperationMs) return;

            string line = $"{DateTime.Now:HH:mm:ss.fff} {elapsedMs,6} мс  {what}";
            Debug.WriteLine("[DB][медленно] " + line);
            TryWriteTrace(line);
        }

        /// <summary>Дописывает строку в журнал медленных операций (сбой журнала не мешает работе).</summary>
        private static void TryWriteTrace(string line)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "db-trace.log");

                // Журнал не должен расти бесконечно: держим последние ~200 КБ
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 200 * 1024)
                    File.WriteAllText(file, "");

                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch { }
        }

        #endregion

        #region === УТИЛИТЫ ЧТЕНИЯ ===

        /// <summary>
        /// Обёртка для SELECT скаляра с значением по умолчанию.
        /// </summary>
        public static T GetScalarOrDefault<T>(string sql, T defaultValue, params (string name, object value)[] parameters)
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            foreach (var p in parameters)
                cmd.Parameters.AddWithValue(p.name, p.value ?? DBNull.Value);
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return defaultValue;
            try
            {
                var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                return (T)Convert.ChangeType(result, targetType);
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Обёртка для SELECT COUNT с защитой от null.
        /// </summary>
        public static int GetCount(string sql, params (string name, object value)[] parameters)
            => GetScalarOrDefault(sql, 0, parameters);

        #endregion

        /// <summary>
        /// Проверяет существование колонки в таблице.
        /// </summary>
        public static bool HasColumn(SqliteConnection conn, string table, string column)
        {
            try
            {
                using var cmd = new SqliteCommand($"PRAGMA table_info({table})", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.IsDBNull(1) ? "" : reader.GetValue(1)?.ToString(), column, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

    }
}
