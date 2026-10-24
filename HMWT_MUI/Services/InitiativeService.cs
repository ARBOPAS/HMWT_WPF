using HMWT_MUI.Models;
using HMWT_MUI.Net;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Трекер инициативы.
    ///
    /// Мастер создаёт группу (стычку), накидывает в неё жетоны — существ из
    /// бестиария или просто имена, — бросает инициативу и ведёт бой: следующий
    /// ход, счёт раундов, конец боя. Всё лежит в базе, поэтому случайно
    /// закрытое окно бой не теряет.
    ///
    /// Игрокам уходит только имя и результат (см. <see cref="BuildState"/>):
    /// бестиарий у каждого свой и по сети не передаётся.
    /// </summary>
    public static class InitiativeService
    {
        private static bool _tablesChecked;

        #region === СХЕМА ===

        public static void EnsureTables()
        {
            if (_tablesChecked) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                using var cmd = new SqliteCommand(@"
                    CREATE TABLE IF NOT EXISTS Encounter (
                        id_encounter INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL DEFAULT 'Бой',
                        round INTEGER DEFAULT 1,
                        is_active INTEGER DEFAULT 0,
                        created_at TEXT DEFAULT '',
                        updated_at TEXT DEFAULT ''
                    );
                    CREATE TABLE IF NOT EXISTS EncounterCombatant (
                        id_combatant INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_encounter INTEGER NOT NULL,
                        name TEXT NOT NULL DEFAULT '',
                        initiative_mod INTEGER DEFAULT 0,
                        initiative_roll INTEGER DEFAULT 0,
                        initiative_total INTEGER DEFAULT 0,
                        has_rolled INTEGER DEFAULT 0,
                        id_monster INTEGER DEFAULT 0,
                        kind INTEGER DEFAULT 0,
                        hp INTEGER DEFAULT 0,
                        max_hp INTEGER DEFAULT 0,
                        note TEXT DEFAULT '',
                        is_current INTEGER DEFAULT 0,
                        conditions TEXT DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_combatant_encounter ON EncounterCombatant(id_encounter);", conn, tx);
                cmd.ExecuteNonQuery();

                // Базы, созданные до 1.1.1.0, колонку не получат: CREATE TABLE
                // IF NOT EXISTS к готовой таблице колонки не добавляет.
                DatabaseHelper.EnsureColumn(conn, tx, "EncounterCombatant", "conditions TEXT DEFAULT ''");

                tx.Commit();
                _tablesChecked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] EnsureTables error: {ex.Message}");
            }
        }

        public static void ResetSchemaCache() => _tablesChecked = false;

        #endregion

        #region === ЧТЕНИЕ ===

        /// <summary>Все группы боя, свежие сверху.</summary>
        public static List<EncounterData> LoadEncounters()
        {
            var result = new List<EncounterData>();
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT * FROM Encounter ORDER BY is_active DESC, id_encounter DESC", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(ReadEncounter(reader));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] LoadEncounters error: {ex.Message}");
            }

            foreach (var encounter in result) encounter.Combatants = LoadCombatants(encounter.Id);
            return result;
        }

        public static EncounterData LoadEncounter(int id)
        {
            if (id <= 0) return null;
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT * FROM Encounter WHERE id_encounter = @id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;
                var encounter = ReadEncounter(reader);
                encounter.Combatants = LoadCombatants(encounter.Id);
                return encounter;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] LoadEncounter error: {ex.Message}");
                return null;
            }
        }

        /// <summary>Активная группа боя или null, если бой не идёт.</summary>
        public static EncounterData ActiveEncounter()
        {
            var all = LoadEncounters();
            return all.FirstOrDefault(e => e.IsActive);
        }

        /// <summary>
        /// Группа для работы: активная, иначе последняя, иначе создаётся новая.
        /// Нужна, чтобы вкладка всегда открывалась в рабочем состоянии.
        /// </summary>
        public static EncounterData EnsureWorkingEncounter()
        {
            var all = LoadEncounters();
            var active = all.FirstOrDefault(e => e.IsActive);
            if (active != null) return active;
            if (all.Count > 0) return all[0];

            var created = new EncounterData { Name = "Бой 1" };
            created.Id = SaveEncounter(created);
            return created.Id > 0 ? created : null;
        }

        private static List<CombatantData> LoadCombatants(int encounterId)
        {
            var list = new List<CombatantData>();
            if (encounterId <= 0) return list;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT * FROM EncounterCombatant WHERE id_encounter = @id ORDER BY id_combatant", conn);
                cmd.Parameters.AddWithValue("@id", encounterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new CombatantData
                    {
                        Id = ReadInt(reader, "id_combatant"),
                        EncounterId = ReadInt(reader, "id_encounter"),
                        Name = ReadText(reader, "name"),
                        InitiativeModifier = ReadInt(reader, "initiative_mod"),
                        InitiativeRoll = ReadInt(reader, "initiative_roll"),
                        InitiativeTotal = ReadInt(reader, "initiative_total"),
                        HasRolled = ReadInt(reader, "has_rolled") != 0,
                        MonsterId = ReadInt(reader, "id_monster"),
                        Kind = ReadInt(reader, "kind") == 1 ? CombatantKind.Player : CombatantKind.Creature,
                        HitPoints = ReadInt(reader, "hp"),
                        MaxHitPoints = ReadInt(reader, "max_hp"),
                        Note = ReadText(reader, "note"),
                        Conditions = ReadConditionText(reader),
                        IsCurrent = ReadInt(reader, "is_current") != 0
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] LoadCombatants error: {ex.Message}");
            }

            return list;
        }

        /// <summary>Читает состояния участника; в старой базе колонки может не быть.</summary>
        private static string ReadConditionText(SqliteDataReader reader)
        {
            try
            {
                int index = reader.GetOrdinal("conditions");
                return reader.IsDBNull(index) ? "" : (reader.GetString(index) ?? "");
            }
            catch { return ""; }
        }
        private static EncounterData ReadEncounter(SqliteDataReader reader)
        {
            return new EncounterData
            {
                Id = ReadInt(reader, "id_encounter"),
                Name = ReadText(reader, "name"),
                Round = Math.Max(1, ReadInt(reader, "round")),
                IsActive = ReadInt(reader, "is_active") != 0,
                CreatedAt = ParseDate(ReadText(reader, "created_at")),
                UpdatedAt = ParseDate(ReadText(reader, "updated_at"))
            };
        }

        #endregion

        #region === ГРУППЫ И ЖЕТОНЫ ===

        /// <summary>Создаёт группу боя. Возвращает её же с присвоенным идентификатором.</summary>
        public static EncounterData CreateEncounter(string name)
        {
            var encounter = new EncounterData
            {
                Name = string.IsNullOrWhiteSpace(name) ? NextEncounterName() : name.Trim(),
                Round = 1,
                IsActive = false
            };
            encounter.Id = SaveEncounter(encounter);
            return encounter;
        }

        private static string NextEncounterName()
        {
            int count = 0;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT COUNT(*) FROM Encounter", conn);
                count = Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] NextEncounterName: {ex.Message}");
            }
            return $"Бой {count + 1}";
        }

        public static int SaveEncounter(EncounterData encounter)
        {
            if (encounter == null) return 0;
            EnsureTables();
            encounter.UpdatedAt = DateTime.Now;
            if (encounter.CreatedAt == default(DateTime)) encounter.CreatedAt = DateTime.Now;
            if (string.IsNullOrWhiteSpace(encounter.Name)) encounter.Name = "Бой";
            if (encounter.Round < 1) encounter.Round = 1;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                string sql = encounter.Id > 0
                    ? @"UPDATE Encounter SET name = @name, round = @round, is_active = @active,
                              updated_at = @updated WHERE id_encounter = @id"
                    : @"INSERT INTO Encounter (name, round, is_active, created_at, updated_at)
                              VALUES (@name, @round, @active, @created, @updated)";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@name", encounter.Name);
                cmd.Parameters.AddWithValue("@round", encounter.Round);
                cmd.Parameters.AddWithValue("@active", encounter.IsActive ? 1 : 0);
                cmd.Parameters.AddWithValue("@created", encounter.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@updated", encounter.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                if (encounter.Id > 0) cmd.Parameters.AddWithValue("@id", encounter.Id);
                cmd.ExecuteNonQuery();

                if (encounter.Id <= 0)
                {
                    using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn);
                    encounter.Id = Convert.ToInt32(idCmd.ExecuteScalar());
                }

                return encounter.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] SaveEncounter error: {ex.Message}");
                return 0;
            }
        }

        public static bool DeleteEncounter(int id)
        {
            if (id <= 0) return false;
            EnsureTables();

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                using (var cmd = new SqliteCommand("DELETE FROM EncounterCombatant WHERE id_encounter = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
                bool removed;
                using (var cmd = new SqliteCommand("DELETE FROM Encounter WHERE id_encounter = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    removed = cmd.ExecuteNonQuery() > 0;
                }

                tx.Commit();
                return removed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] DeleteEncounter error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Сохраняет жетон (новый или изменённый).</summary>
        /// <summary>
        /// Боец изменили (например, состояния на карте) — окно трекера
        /// должно перерисоваться, не дожидаясь переоткрытия.
        /// </summary>
        public static event Action Changed;

        public static int SaveCombatant(EncounterData encounter, CombatantData combatant)
        {
            if (encounter == null || combatant == null) return 0;
            EnsureTables();
            if (encounter.Id <= 0) SaveEncounter(encounter);
            if (combatant.EncounterId <= 0) combatant.EncounterId = encounter.Id;
            if (string.IsNullOrWhiteSpace(combatant.Name)) return 0;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                string sql = combatant.Id > 0
                    ? @"UPDATE EncounterCombatant SET name = @name, initiative_mod = @mod,
                              initiative_roll = @roll, initiative_total = @total, has_rolled = @rolled,
                              id_monster = @monster, kind = @kind, hp = @hp, max_hp = @maxhp,
                              note = @note, is_current = @current, conditions = @conditions
                        WHERE id_combatant = @id"
                    : @"INSERT INTO EncounterCombatant (id_encounter, name, initiative_mod, initiative_roll,
                              initiative_total, has_rolled, id_monster, kind, hp, max_hp, note, is_current, conditions)
                        VALUES (@enc, @name, @mod, @roll, @total, @rolled, @monster, @kind, @hp, @maxhp, @note, @current, @conditions)";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@enc", encounter.Id);
                cmd.Parameters.AddWithValue("@name", combatant.Name.Trim());
                cmd.Parameters.AddWithValue("@mod", combatant.InitiativeModifier);
                cmd.Parameters.AddWithValue("@roll", combatant.InitiativeRoll);
                cmd.Parameters.AddWithValue("@total", combatant.InitiativeTotal);
                cmd.Parameters.AddWithValue("@rolled", combatant.HasRolled ? 1 : 0);
                cmd.Parameters.AddWithValue("@monster", combatant.MonsterId);
                cmd.Parameters.AddWithValue("@kind", combatant.Kind == CombatantKind.Player ? 1 : 0);
                cmd.Parameters.AddWithValue("@hp", combatant.HitPoints);
                cmd.Parameters.AddWithValue("@maxhp", combatant.MaxHitPoints);
                cmd.Parameters.AddWithValue("@note", combatant.Note ?? "");
                cmd.Parameters.AddWithValue("@current", combatant.IsCurrent ? 1 : 0);
                cmd.Parameters.AddWithValue("@conditions", combatant.Conditions ?? "");
                if (combatant.Id > 0) cmd.Parameters.AddWithValue("@id", combatant.Id);
                cmd.ExecuteNonQuery();

                if (combatant.Id <= 0)
                {
                    using var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn);
                    combatant.Id = Convert.ToInt32(idCmd.ExecuteScalar());
                    if (!encounter.Combatants.Contains(combatant)) encounter.Combatants.Add(combatant);
                }

                try { Changed?.Invoke(); } catch { }
            return combatant.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] SaveCombatant error: {ex.Message}");
                return 0;
            }
        }

        public static bool DeleteCombatant(CombatantData combatant)
        {
            if (combatant == null || combatant.Id <= 0) return false;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("DELETE FROM EncounterCombatant WHERE id_combatant = @id", conn);
                cmd.Parameters.AddWithValue("@id", combatant.Id);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitiativeService] DeleteCombatant error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Добавляет жетон по имени. Возвращает созданный жетон или null.</summary>
        public static CombatantData AddByName(EncounterData encounter, string name, int modifier,
            CombatantKind kind = CombatantKind.Creature, int monsterId = 0)
        {
            if (encounter == null || string.IsNullOrWhiteSpace(name)) return null;

            var combatant = new CombatantData
            {
                EncounterId = encounter.Id,
                Name = name.Trim(),
                InitiativeModifier = Math.Max(-20, Math.Min(30, modifier)),
                Kind = kind,
                MonsterId = monsterId
            };

            if (SaveCombatant(encounter, combatant) <= 0) return null;
            return combatant;
        }

        /// <summary>
        /// Добавляет существ из бестиария. Несколько одинаковых получают номера:
        /// «Гоблин», «Гоблин 2», «Гоблин 3».
        /// </summary>
        public static int AddFromMonster(EncounterData encounter, MonsterData monster, int count = 1)
        {
            if (encounter == null || monster == null) return 0;
            if (count < 1) count = 1;
            if (count > 20) count = 20;

            int added = 0;
            for (int i = 0; i < count; i++)
            {
                string name = UniqueName(encounter, monster.Name);
                var combatant = AddByName(encounter, name, monster.InitiativeModifier,
                    CombatantKind.Creature, monster.Id);
                if (combatant != null) added++;
            }
            return added;
        }

        /// <summary>Подбирает имя, которого ещё нет в группе.</summary>
        private static string UniqueName(EncounterData encounter, string baseName)
        {
            string name = string.IsNullOrWhiteSpace(baseName) ? "Существо" : baseName.Trim();
            if (!encounter.Combatants.Any(c => c != null && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                return name;

            for (int i = 2; i < 100; i++)
            {
                string candidate = $"{name} {i}";
                if (!encounter.Combatants.Any(c => c != null && string.Equals(c.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
                    return candidate;
            }
            return name + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        #endregion

        #region === ХОД БОЯ ===

        /// <summary>
        /// Записывает результат броска инициативы жетону.
        /// Бросок делает вызывающий (у него уже есть DiceRoller), сюда приходит итог.
        /// </summary>
        public static void ApplyRoll(CombatantData combatant, int diceRoll)
        {
            if (combatant == null) return;
            combatant.InitiativeRoll = diceRoll;
            combatant.HasRolled = true;
            combatant.InitiativeTotal = diceRoll + combatant.InitiativeModifier;
        }

        /// <summary>Начинает бой: сортирует порядок, ставит ход первому, раунд 1.</summary>
        public static void Begin(EncounterData encounter)
        {
            if (encounter == null) return;

            var ordered = encounter.Ordered();
            foreach (var combatant in encounter.Combatants) combatant.IsCurrent = false;
            if (ordered.Count > 0) ordered[0].IsCurrent = true;

            encounter.Round = 1;
            encounter.IsActive = ordered.Count > 0;

            SaveEncounter(encounter);
            SaveAll(encounter);
        }

        /// <summary>
        /// Передаёт ход следующему. Когда очередь доходит до конца списка,
        /// начинается новый раунд.
        /// </summary>
        public static bool NextTurn(EncounterData encounter)
        {
            if (encounter == null) return false;

            var ordered = encounter.Ordered();
            if (ordered.Count == 0) return false;

            int index = encounter.CurrentIndex();
            int next = index + 1;
            if (next >= ordered.Count)
            {
                next = 0;
                encounter.Round = Math.Max(1, encounter.Round) + 1;
            }
            else if (index < 0)
            {
                next = 0;
            }

            foreach (var combatant in encounter.Combatants) combatant.IsCurrent = false;
            ordered[next].IsCurrent = true;
            encounter.IsActive = true;

            SaveEncounter(encounter);
            SaveAll(encounter);
            return true;
        }

        /// <summary>Отдаёт ход предыдущему (мастер ошибся кнопкой).</summary>
        public static bool PreviousTurn(EncounterData encounter)
        {
            if (encounter == null) return false;

            var ordered = encounter.Ordered();
            if (ordered.Count == 0) return false;

            int index = encounter.CurrentIndex();
            int previous = index - 1;
            if (previous < 0)
            {
                previous = ordered.Count - 1;
                encounter.Round = Math.Max(1, encounter.Round - 1);
            }

            foreach (var combatant in encounter.Combatants) combatant.IsCurrent = false;
            ordered[previous].IsCurrent = true;
            encounter.IsActive = true;

            SaveEncounter(encounter);
            SaveAll(encounter);
            return true;
        }

        /// <summary>Заканчивает бой. Сбрасывать броски не обязательно: мастера это бесит.</summary>
        public static void EndCombat(EncounterData encounter, bool clearRolls)
        {
            if (encounter == null) return;

            encounter.IsActive = false;
            foreach (var combatant in encounter.Combatants)
            {
                combatant.IsCurrent = false;
                if (clearRolls)
                {
                    combatant.HasRolled = false;
                    combatant.InitiativeRoll = 0;
                    combatant.InitiativeTotal = 0;
                }
            }
            encounter.Round = 1;

            SaveEncounter(encounter);
            SaveAll(encounter);
        }

        /// <summary>Сбрасывает броски у всей группы — перед новой стычкой.</summary>
        public static void ClearRolls(EncounterData encounter)
        {
            if (encounter == null) return;
            foreach (var combatant in encounter.Combatants)
            {
                combatant.HasRolled = false;
                combatant.InitiativeRoll = 0;
                combatant.InitiativeTotal = 0;
                combatant.IsCurrent = false;
            }
            encounter.IsActive = false;
            encounter.Round = 1;

            SaveEncounter(encounter);
            SaveAll(encounter);
        }

        private static void SaveAll(EncounterData encounter)
        {
            foreach (var combatant in encounter.Combatants.ToList())
                SaveCombatant(encounter, combatant);
        }

        #endregion

        #region === ОБМЕН С КОМНАТОЙ ===

        /// <summary>
        /// Состояние для игроков: только имена, результаты и чей ход.
        /// Ни бестиарий, ни заметки мастера по сети не уходят.
        /// </summary>
        public static InitiativeState BuildState(EncounterData encounter)
        {
            var state = new InitiativeState();

            if (encounter == null)
            {
                state.IsActive = false;
                return state;
            }

            state.IsActive = encounter.IsActive;
            state.Name = encounter.Name ?? "";
            state.Round = Math.Max(1, encounter.Round);

            foreach (var combatant in encounter.Ordered())
            {
                if (!combatant.HasRolled) continue;   // не брошено — игрокам показывать нечего
                state.Combatants.Add(new InitiativeCombatantDto
                {
                    Name = combatant.Name,
                    Roll = combatant.InitiativeRoll,
                    Modifier = combatant.InitiativeModifier,
                    Total = combatant.InitiativeTotal,
                    IsPlayer = combatant.Kind == CombatantKind.Player,
                    IsCurrent = combatant.IsCurrent
                });
            }

            return state;
        }

        /// <summary>Текст для ленты комнаты: «Инициатива: Гоблин 17, Арагорн 12».</summary>
        public static string ToNotice(EncounterData encounter)
        {
            if (encounter == null) return "";
            var rolled = encounter.Ordered().Where(c => c.HasRolled).ToList();
            if (rolled.Count == 0) return $"{encounter.Name}: инициатива не брошена";

            string list = string.Join(", ", rolled.Select(c => $"{c.Name} — {c.InitiativeTotal}"));
            return $"{encounter.Name}: {list}";
        }

        #endregion

        #region === МЕЛОЧИ ===

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
            => DateTime.TryParse(text, out DateTime value) ? value : DateTime.Now;

        /// <summary>Пустая группа боя нужна вкладке, когда мастер всё удалил.</summary>
        public static EncounterData CreateDefaultIfEmpty()
        {
            var all = LoadEncounters();
            if (all.Count > 0) return all[0];
            return CreateEncounter("Бой 1");
        }

        #endregion
    }
}
