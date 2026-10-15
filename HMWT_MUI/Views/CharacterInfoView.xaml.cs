using HMWT_MUI.Services;
using HMWT_MUI.Windows;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using HMWT_MUI.Helpers;

namespace HMWT_MUI.Views
{
    public partial class CharacterInfoView : UserControl
    {
        private int _characterId;
        private DiceRoller _diceRoller;

        /// <summary>
        /// Навыки с экспертизой. Читается один раз при загрузке листа: бонус
        /// навыка считается для всех восемнадцати сразу, и спрашивать про каждый
        /// по отдельности было бы восемнадцать запросов.
        /// </summary>
        private HashSet<string> _expertSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CharacterInfoView(int characterId)
        {
            InitializeComponent();
            _characterId = characterId;
            _diceRoller = new DiceRoller();

            // Схему здесь больше не трогаем: и колонки, и таблицу бонусов создаёт
            // DatabaseHelper при запуске. Раньше вкладка догоняла их сама — по
            // четырнадцать ALTER на каждое открытие листа персонажа
            BuildProficiencyToggles();

            // Флажки навыков в форме предыстории — тоже один раз, они статичны
            BuildBackgroundSkillPicker();

            LoadCharacterData();

            Loaded += (_, __) => ApplyAdaptiveLayout();
            SizeChanged += (_, __) => ApplyAdaptiveLayout();
        }

        /// <summary>
        /// Метка состояния вместо символов ● / ○: включено — залитый кружок,
        /// выключено — контур. Иконки темы, а не символы шрифта.
        /// </summary>
        private static Helpers.IconView Marker(bool on)
        {
            return new Helpers.IconView
            {
                Kind = on ? "dot" : "circle",
                IconSize = 14,
                IconBrush = on ? (Brush)Application.Current.FindResource("B.Ok") : (Brush)Application.Current.FindResource("B.Mute"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>
        /// Метка ступени владения навыком: пустое кольцо, залитый кружок или
        /// звезда. Экспертиза отмечена звездой: она не «ещё одно владение»,
        /// а другой уровень — и на глаз должна отличаться сразу.
        /// </summary>
        private static Helpers.IconView Marker(SkillProficiencyLevel level)
        {
            switch (level)
            {
                case SkillProficiencyLevel.Expert:
                    return new Helpers.IconView
                    {
                        Kind = "star",
                        IconSize = 15,
                        IconBrush = (Brush)Application.Current.FindResource("B.Gold"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                case SkillProficiencyLevel.Proficient:
                    return Marker(true);
                default:
                    return Marker(false);
            }
        }
        #region === АДАПТИВНАЯ ВЕРСТКА ===
        private void ApplyAdaptiveLayout()
        {
            double w = ActualWidth;
            if (w <= 0) return;

            double title, score, mod, rowText, rowValue, saveText, btnText, tabText, roundBtn, diceBtn, actionH;
            Thickness cardPad, rowPad;

            // Сколько колонок характеристик реально влезает: в каждой карточке длинные
            // названия навыков, при 6 колонках на узком экране они обрезались («Атлети…»).
            int columns =
                w >= 1340 ? 6 :     // Full HD и шире
                w >= 1100 ? 4 :
                w >= 820 ? 3 :
                w >= 560 ? 2 : 1;

            if (w < 1450)        // Compact
            {
                title = 12; score = 30; mod = 14; rowText = 11; rowValue = 12; saveText = 12;
                btnText = 11; tabText = 12; roundBtn = 26; diceBtn = 24; actionH = 28;
                cardPad = new Thickness(8); rowPad = new Thickness(6, 4, 6, 4);
            }
            else if (w < 1900)   // Normal (Full HD)
            {
                title = 13; score = 36; mod = 16; rowText = 12; rowValue = 13; saveText = 13;
                btnText = 12; tabText = 13; roundBtn = 28; diceBtn = 28; actionH = 32;
                cardPad = new Thickness(10); rowPad = new Thickness(8, 6, 8, 6);
            }
            else if (w < 2500)   // Wide (2K)
            {
                title = 15; score = 44; mod = 18; rowText = 14; rowValue = 15; saveText = 14;
                btnText = 13; tabText = 14; roundBtn = 32; diceBtn = 32; actionH = 34;
                cardPad = new Thickness(12); rowPad = new Thickness(10, 7, 10, 7);
            }
            else                 // Ultra (4K+)
            {
                title = 16; score = 50; mod = 20; rowText = 15; rowValue = 16; saveText = 15;
                btnText = 14; tabText = 15; roundBtn = 34; diceBtn = 34; actionH = 36;
                cardPad = new Thickness(14); rowPad = new Thickness(11, 8, 11, 8);
            }

            if (AbilitiesGrid != null && AbilitiesGrid.Columns != columns)
                AbilitiesGrid.Columns = columns;

            Resources["FS.AbilityTitle"] = title;
            Resources["FS.AbilityScore"] = score;
            Resources["FS.AbilityMod"] = mod;
            Resources["FS.RowText"] = rowText;
            Resources["FS.RowValue"] = rowValue;
            Resources["FS.SaveText"] = saveText;
            Resources["FS.ButtonText"] = btnText;
            Resources["FS.TabText"] = tabText;
            Resources["SZ.RoundBtn"] = roundBtn;
            Resources["SZ.DiceBtn"] = diceBtn;
            Resources["SZ.ActionBtnHeight"] = actionH;
            Resources["TH.AbilityCardPadding"] = cardPad;
            Resources["TH.SkillRowPadding"] = rowPad;
        }
        #endregion

        #region === ЗАГРУЗКА ДАННЫХ ===

        private void LoadCharacterData()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                // Загружаем основные характеристики
                string query = @"
                    SELECT Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma,
                           PassivePerception, PassiveInsight, PassiveInvestigation, Initiative
                    FROM CharacterInfo WHERE id_character = @id";

                using var cmd = new SqliteCommand(query, conn);
                cmd.Parameters.AddWithValue("@id", _characterId);
                using var reader = cmd.ExecuteReader();

                if (reader.Read())
                {
                    int str = Convert.ToInt32(reader["Strength"]);
                    int dex = Convert.ToInt32(reader["Dexterity"]);
                    int con = Convert.ToInt32(reader["Constitution"]);
                    int intel = Convert.ToInt32(reader["Intelligence"]);
                    int wis = Convert.ToInt32(reader["Wisdom"]);
                    int cha = Convert.ToInt32(reader["Charisma"]);

                    txtStrScore.Text = str.ToString();
                    txtStrMod.Text = FormatModifier(CalculateModifier(str));

                    txtDexScore.Text = dex.ToString();
                    txtDexMod.Text = FormatModifier(CalculateModifier(dex));

                    txtConScore.Text = con.ToString();
                    txtConMod.Text = FormatModifier(CalculateModifier(con));

                    txtIntScore.Text = intel.ToString();
                    txtIntMod.Text = FormatModifier(CalculateModifier(intel));

                    txtWisScore.Text = wis.ToString();
                    txtWisMod.Text = FormatModifier(CalculateModifier(wis));

                    txtChaScore.Text = cha.ToString();
                    txtChaMod.Text = FormatModifier(CalculateModifier(cha));

                    // Пассивные навыки
                    txtPassivePerception.Text = reader["PassivePerception"]?.ToString() ?? "10";
                    txtPassiveInsight.Text = reader["PassiveInsight"]?.ToString() ?? "10";
                    txtPassiveInvestigation.Text = reader["PassiveInvestigation"]?.ToString() ?? "10";
                    txtInitiative.Text = FormatModifier(Convert.ToInt32(reader["Initiative"] ?? 0));
                }

                // ВАЖНО: закрываем читатель ДО всего, что пишет в базу.
                //
                // Открытый SqliteDataReader держит разделяемую блокировку файла
                // базы. Запись идёт ниже, из LoadSkills → RefreshPassiveValues:
                // там UPDATE CharacterInfo на другом соединении. Раньше читатель
                // жил до конца try-блока, поэтому писатель не мог получить
                // блокировку, драйвер повторял попытку до CommandTimeout (30 с) и
                // падал с «database is locked», а исключение глушилось catch'ем.
                //
                // Из-за этого открытие листа персонажа и смена характеристики
                // «висели» около 30 секунд, а пассивные значения (внимательность,
                // проницательность, расследование) молча не сохранялись.
                // В режиме WAL читатель писателя не блокирует, но закрывать
                // читателя перед записью всё равно обязательно.
                reader.Close();

                LoadProficiencyBonus(conn);

                // Загружаем навыки
                LoadSkills();

                // Загружаем доп.информацию
                LoadAdditionalInfo(conn);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadData] {ex.Message}");
            }

            // Сводка по снаряжению читается своим соединением — только после
            // того, как основное закрылось
            RefreshEquipmentSummary();

            // Если лист открывался долго — в журнале появится строка с точным
            // числом: раньше на этом пути база стояла в блокировке 30 секунд,
            // а по внешнему виду нельзя было понять, что именно тормозит
            DatabaseHelper.ReportIfSlow($"лист персонажа id={_characterId}: полная перезагрузка данных", watch.ElapsedMilliseconds);
        }

        /// <summary>
        /// Читает бонус мастерства из базы.
        ///
        /// Вынесено отдельно, потому что бонус зависит от уровня: раньше он
        /// читался только при открытии вкладки, и после повышения уровня навыки
        /// продолжали показывать старое значение (+2 вместо +6). Теперь вкладка
        /// пересчитывает его по событию смены уровня.
        /// </summary>
        private void LoadProficiencyBonus(SqliteConnection conn)
        {
            try
            {
                using var profCmd = new SqliteCommand(
                    "SELECT ProficiencyBonus FROM LevelTable WHERE id_character = @id", conn);
                profCmd.Parameters.AddWithValue("@id", _characterId);
                int profBonus = Convert.ToInt32(profCmd.ExecuteScalar() ?? 2);
                txtProfBonus.Text = $"+{profBonus}";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadProficiencyBonus] {ex.Message}");
            }
        }

        /// <summary>
        /// Заполняет сводку по снаряжению: что надето, вес инвентаря и золото.
        ///
        /// Нужна, чтобы освободившееся место внизу вкладки не пустовало и игрок
        /// видел главное, не переходя на вкладку инвентаря.
        /// </summary>
        public void RefreshEquipmentSummary()
        {
            try
            {
                var inventory = new Services.InventoryService(_characterId);

                // Что надето: берём из ячеек экипировки
                var equipped = ReadEquippedNames();
                if (txtEquippedSummary != null)
                    txtEquippedSummary.Text = equipped.Count == 0
                        ? "ничего не надето"
                        : string.Join(", ", equipped.Take(4)) + (equipped.Count > 4 ? $" и ещё {equipped.Count - 4}" : "");

                // Вес инвентаря
                var (weight, count) = inventory.CalculateTotalWeight();
                if (txtWeightSummary != null)
                    txtWeightSummary.Text = $"{weight:0.#} кг · предметов {count}";

                // Золото
                if (txtGoldSummary != null)
                    txtGoldSummary.Text = ReadGoldSummary();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CharacterInfoView] сводка снаряжения: {ex.Message}");
            }
        }

        /// <summary>
        /// Названия предметов, лежащих в ячейках экипировки.
        ///
        /// Отдельный запрос вместо метода сервиса: сводке нужны только имена,
        /// и незачем поднимать ради этого весь сервис экипировки.
        /// </summary>
        private List<string> ReadEquippedNames()
        {
            var names = new List<string>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT DISTINCT i.name
                    FROM EquipmentCells c
                    INNER JOIN Inventory i ON i.id_item = c.id_item
                    WHERE i.id_character = @id
                    ORDER BY i.name", conn);
                cmd.Parameters.AddWithValue("@id", _characterId);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string name = r["name"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CharacterInfoView] экипировка: {ex.Message}");
            }
            return names;
        }

        /// <summary>Читает золото персонажа для сводки.</summary>
        private string ReadGoldSummary()
        {
            try
            {
                // Деньги лежат в GoldTable (там же, где их правит калькулятор
                // в шапке). Раньше сводка читала колонки CharacterInfo, которых
                // в базе нет: запрос падал, и в строке всегда стоял прочерк.
                var money = new Services.MoneyService(_characterId).LoadMoney();
                if (money == null) return "—";

                return $"{money.Gold} зм · {money.Silver} см · {money.Copper} мм";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CharacterInfoView] золото: {ex.Message}");
                return "—";
            }
        }

        /// <summary>
        /// Перечитывает бонус мастерства и пересчитывает все навыки, проверки и
        /// спасброски. Вызывается, когда уровень персонажа изменился.
        /// </summary>
        public void RefreshProficiencyBonus()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                LoadProficiencyBonus(conn);
                LoadSkills();

                System.Diagnostics.Debug.WriteLine("[CharacterInfoView] бонус мастерства пересчитан");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RefreshProficiencyBonus] {ex.Message}");
            }
        }
        /// <summary>
        /// Пересчитывает значения навыков и спасбросков на листе.
        ///
        /// Чтение отметок — в AbilityService: раньше здесь стояли шесть запросов
        /// к шести таблицам, то есть SQL жил прямо в разметке окна.
        /// </summary>
        private void LoadSkills()
        {
            int profBonus = ProfBonus();

            // Экспертиза участвует в бонусе каждого навыка, поэтому читается заранее
            _expertSkills = AbilityService.LoadExpertSkills(_characterId);

            var flags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var saves = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in AbilityService.LoadAbilitySkills(_characterId))
            {
                saves[group.Ability] = group.SaveProficient;
                foreach (var (key, flag) in group.Skills) flags[key] = flag;
            }

            int Flag(string key) => flags.TryGetValue(key, out int value) ? value : 0;
            bool Save(string ability) => saves.TryGetValue(ability, out bool value) && value;

            int strMod = CalculateModifier(int.Parse(txtStrScore.Text));
            int dexMod = CalculateModifier(int.Parse(txtDexScore.Text));
            int conMod = CalculateModifier(int.Parse(txtConScore.Text));
            int intMod = CalculateModifier(int.Parse(txtIntScore.Text));
            int wisMod = CalculateModifier(int.Parse(txtWisScore.Text));
            int chaMod = CalculateModifier(int.Parse(txtChaScore.Text));

            // Бонус навыка — модификатор характеристики плюс ступень владения
            // и плюс бонусы, которые игрок завёл сам в редакторе характеристик
            string SkillValue(int mod, string ability, string key)
                => FormatModifier(mod + SkillBonus(key, Flag(key)) + GetBonusSum(ability, "Skill"));
            string SaveValue(int mod, string ability)
                => FormatModifier(mod + (Save(ability) ? profBonus : 0) + GetBonusSum(ability, "Save"));

            // Сила
            txtAthleticsVal.Text = SkillValue(strMod, "СИЛА", "Athletics");
            txtStrSaveVal.Text = SaveValue(strMod, "СИЛА");
            UpdateSkillToggleVisual(btnProfAthletics, SkillLevel("Athletics", Flag("Athletics")));
            UpdateToggleVisual(btnSaveProfStr, Save("СИЛА"));

            // Ловкость
            txtAcrobaticsVal.Text = SkillValue(dexMod, "ЛОВКОСТЬ", "Acrobatics");
            txtStealthVal.Text = SkillValue(dexMod, "ЛОВКОСТЬ", "Stealth");
            txtSleightOfHandVal.Text = SkillValue(dexMod, "ЛОВКОСТЬ", "SleightOfHand");
            txtDexSaveVal.Text = SaveValue(dexMod, "ЛОВКОСТЬ");
            UpdateSkillToggleVisual(btnProfAcrobatics, SkillLevel("Acrobatics", Flag("Acrobatics")));
            UpdateSkillToggleVisual(btnProfStealth, SkillLevel("Stealth", Flag("Stealth")));
            UpdateSkillToggleVisual(btnProfSleightOfHand, SkillLevel("SleightOfHand", Flag("SleightOfHand")));
            UpdateToggleVisual(btnSaveProfDex, Save("ЛОВКОСТЬ"));

            // Телосложение: навыков нет, только спасбросок
            txtConSaveVal.Text = SaveValue(conMod, "ТЕЛОСЛОЖЕНИЕ");
            UpdateToggleVisual(btnSaveProfCon, Save("ТЕЛОСЛОЖЕНИЕ"));

            // Интеллект
            txtAnalysisVal.Text = SkillValue(intMod, "ИНТЕЛЛЕКТ", "Analysis");
            txtHistoryVal.Text = SkillValue(intMod, "ИНТЕЛЛЕКТ", "History");
            txtMagicVal.Text = SkillValue(intMod, "ИНТЕЛЛЕКТ", "Magic");
            txtNatureVal.Text = SkillValue(intMod, "ИНТЕЛЛЕКТ", "Nature");
            txtReligionVal.Text = SkillValue(intMod, "ИНТЕЛЛЕКТ", "Religion");
            txtIntSaveVal.Text = SaveValue(intMod, "ИНТЕЛЛЕКТ");
            UpdateSkillToggleVisual(btnProfAnalysis, SkillLevel("Analysis", Flag("Analysis")));
            UpdateSkillToggleVisual(btnProfHistory, SkillLevel("History", Flag("History")));
            UpdateSkillToggleVisual(btnProfMagic, SkillLevel("Magic", Flag("Magic")));
            UpdateSkillToggleVisual(btnProfNature, SkillLevel("Nature", Flag("Nature")));
            UpdateSkillToggleVisual(btnProfReligion, SkillLevel("Religion", Flag("Religion")));
            UpdateToggleVisual(btnSaveProfInt, Save("ИНТЕЛЛЕКТ"));

            // Мудрость
            txtPerceptionVal.Text = SkillValue(wisMod, "МУДРОСТЬ", "Perception");
            txtSurvivalVal.Text = SkillValue(wisMod, "МУДРОСТЬ", "Survival");
            txtMedicineVal.Text = SkillValue(wisMod, "МУДРОСТЬ", "Medicine");
            txtInsightVal.Text = SkillValue(wisMod, "МУДРОСТЬ", "Insight");
            txtAnimalCareVal.Text = SkillValue(wisMod, "МУДРОСТЬ", "AnimalCare");
            txtWisSaveVal.Text = SaveValue(wisMod, "МУДРОСТЬ");
            UpdateSkillToggleVisual(btnProfPerception, SkillLevel("Perception", Flag("Perception")));
            UpdateSkillToggleVisual(btnProfSurvival, SkillLevel("Survival", Flag("Survival")));
            UpdateSkillToggleVisual(btnProfMedicine, SkillLevel("Medicine", Flag("Medicine")));
            UpdateSkillToggleVisual(btnProfInsight, SkillLevel("Insight", Flag("Insight")));
            UpdateSkillToggleVisual(btnProfAnimalCare, SkillLevel("AnimalCare", Flag("AnimalCare")));
            UpdateToggleVisual(btnSaveProfWis, Save("МУДРОСТЬ"));

            // Харизма
            txtPerformanceVal.Text = SkillValue(chaMod, "ХАРИЗМА", "Performance");
            txtDeceptionVal.Text = SkillValue(chaMod, "ХАРИЗМА", "Deception");
            txtPersuasionVal.Text = SkillValue(chaMod, "ХАРИЗМА", "Persuasion");
            txtIntimidationVal.Text = SkillValue(chaMod, "ХАРИЗМА", "Intimidation");
            txtChaSaveVal.Text = SaveValue(chaMod, "ХАРИЗМА");
            UpdateSkillToggleVisual(btnProfPerformance, SkillLevel("Performance", Flag("Performance")));
            UpdateSkillToggleVisual(btnProfDeception, SkillLevel("Deception", Flag("Deception")));
            UpdateSkillToggleVisual(btnProfPersuasion, SkillLevel("Persuasion", Flag("Persuasion")));
            UpdateSkillToggleVisual(btnProfIntimidation, SkillLevel("Intimidation", Flag("Intimidation")));
            UpdateToggleVisual(btnSaveProfCha, Save("ХАРИЗМА"));

            // Пассивные значения считаем ПОСЛЕ навыков: они из них и берутся.
            // Раньше эти поля только читались из базы и всегда показывали 10 —
            // их никто не пересчитывал, и блок был бесполезным.
            RefreshPassiveValues();
        }

        /// <summary>
        /// Считает пассивные значения и инициативу и записывает их в базу.
        ///
        /// Пассивный навык = 10 + бонус навыка (Внимательность, Проницательность,
        /// Анализ), инициатива = модификатор Ловкости. Значения лежат в
        /// CharacterInfo, чтобы их видели другие экраны (карточка в отряде).
        /// </summary>
        public void RefreshPassiveValues()
        {
            try
            {
                int passivePerception = 10 + ParseModifierText(txtPerceptionVal?.Text);
                int passiveInsight = 10 + ParseModifierText(txtInsightVal?.Text);
                int passiveInvestigation = 10 + ParseModifierText(txtAnalysisVal?.Text);
                int initiative = ParseModifierText(txtDexMod?.Text);

                if (txtPassivePerception != null) txtPassivePerception.Text = passivePerception.ToString();
                if (txtPassiveInsight != null) txtPassiveInsight.Text = passiveInsight.ToString();
                if (txtPassiveInvestigation != null) txtPassiveInvestigation.Text = passiveInvestigation.ToString();
                if (txtInitiative != null) txtInitiative.Text = FormatModifier(initiative);

                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    UPDATE CharacterInfo
                    SET PassivePerception = @pp, PassiveInsight = @pi,
                        PassiveInvestigation = @pv, Initiative = @ini
                    WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@pp", passivePerception);
                cmd.Parameters.AddWithValue("@pi", passiveInsight);
                cmd.Parameters.AddWithValue("@pv", passiveInvestigation);
                cmd.Parameters.AddWithValue("@ini", initiative);
                cmd.Parameters.AddWithValue("@id", _characterId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CharacterInfoView] пассивные значения: {ex.Message}");
            }
        }

        private void LoadAdditionalInfo(SqliteConnection conn)
        {
            using var cmd = new SqliteCommand("SELECT * FROM CharacterIdAddInfo WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@id", _characterId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                LoadProficiencyToggles(r["Attachments"]?.ToString() ?? "", r["Weapons"]?.ToString() ?? "");
                txtLanguages.Text = r["Languages"]?.ToString() ?? "";
                txtPersTraits.Text = r["Traits"]?.ToString() ?? "";
                txtIdeals.Text = r["Ideals"]?.ToString() ?? "";
                txtBonds.Text = r["Bonds"]?.ToString() ?? "";
                txtFlaws.Text = r["Weakness"]?.ToString() ?? "";

                // Внешность и предыстория: сохранялись, но при загрузке не читались,
                // поэтому поля всегда выглядели пустыми
                SetIfColumn(r, "Height", txtHeight);
                SetIfColumn(r, "Weight", txtWeight);
                SetIfColumn(r, "Eyes", txtEyes);
                SetIfColumn(r, "Hair", txtHair);
                SetIfColumn(r, "Backstory", txtBackstory);
                SetIfColumn(r, "Background", txtBackground);
            }
        }

        /// <summary>Присваивает текст из колонки, если такая колонка есть в выборке.</summary>
        private static void SetIfColumn(System.Data.Common.DbDataReader r, string column, System.Windows.Controls.TextBox box)
        {
            if (box == null) return;
            for (int i = 0; i < r.FieldCount; i++)
            {
                if (!string.Equals(r.GetName(i), column, StringComparison.OrdinalIgnoreCase)) continue;
                box.Text = r.IsDBNull(i) ? "" : r.GetValue(i)?.ToString() ?? "";
                return;
            }
        }
        #endregion

        #region === РЕДАКТОР ХАРАКТЕРИСТИК И БОНУСОВ ===
        private string _editorAbility;

        private static readonly Dictionary<string, string> AbilitySaveKey = new Dictionary<string, string>
        {
            ["СИЛА"] = "Str",
            ["ЛОВКОСТЬ"] = "Dex",
            ["ТЕЛОСЛОЖЕНИЕ"] = "Con",
            ["ИНТЕЛЛЕКТ"] = "Int",
            ["МУДРОСТЬ"] = "Wis",
            ["ХАРИЗМА"] = "Cha"
        };

        /// Ссылки на элементы карточки (для пересчёта после правок)
        private (TextBlock score, TextBlock mod, TextBlock saveVal, Button saveProfBtn) AbilityRefs(string ability)
        {
            switch (ability)
            {
                case "СИЛА": return (txtStrScore, txtStrMod, txtStrSaveVal, btnSaveProfStr);
                case "ЛОВКОСТЬ": return (txtDexScore, txtDexMod, txtDexSaveVal, btnSaveProfDex);
                case "ТЕЛОСЛОЖЕНИЕ": return (txtConScore, txtConMod, txtConSaveVal, btnSaveProfCon);
                case "ИНТЕЛЛЕКТ": return (txtIntScore, txtIntMod, txtIntSaveVal, btnSaveProfInt);
                case "МУДРОСТЬ": return (txtWisScore, txtWisMod, txtWisSaveVal, btnSaveProfWis);
                default: return (txtChaScore, txtChaMod, txtChaSaveVal, btnSaveProfCha);
            }
        }

        private void OpenAbilityEditor(string ability)
        {
            _editorAbility = ability;
            var refs = AbilityRefs(ability);
            txtAbilityEditorTitle.Text = ability;
            txtAbilityBaseValue.Text = refs.score.Text;
            RefreshEditorHeader();
            RefreshSaveProfOverlayButton();
            RebuildBonusPanels();
            abilityBonusOverlay.Visibility = Visibility.Visible;
        }

        private void AbilityBonusOverlay_Close(object sender, MouseButtonEventArgs e)
        { if (e.OriginalSource == sender) abilityBonusOverlay.Visibility = Visibility.Collapsed; }

        private void AbilityBonusOverlay_Close_Click(object sender, RoutedEventArgs e)
            => abilityBonusOverlay.Visibility = Visibility.Collapsed;

        private void RefreshEditorHeader()
        {
            var refs = AbilityRefs(_editorAbility);
            txtAbilityEditorScore.Text = refs.score.Text;
            txtAbilityEditorSave.Text = refs.saveVal.Text;
        }

        private void RefreshSaveProfOverlayButton()
        {
            bool prof = AbilityService.ReadSaveProficiency(_characterId, AbilitySaveKey[_editorAbility]);

            // Только круглый значок, без слов «есть» и «нет».
            //
            // Слова занимали место и читались как текст, хотя состояние видно
            // по цвету и заполнению: закрашенный золотой кружок — владение есть,
            // пустое кольцо — владения нет.
            btnSaveProfOverlay.Content = Marker(prof);
            btnSaveProfOverlay.FontSize = 18;
            btnSaveProfOverlay.Foreground = new SolidColorBrush((Color)FindResource(prof ? "C.Gold" : "C.Mute"));
            btnSaveProfOverlay.ToolTip = prof
                ? "Владение есть: бонус мастерства учитывается. Нажмите, чтобы снять."
                : "Владения нет: бонус мастерства не учитывается. Нажмите, чтобы добавить.";
        }

        // ========================= БАЗОВОЕ ЗНАЧЕНИЕ =========================

        private void TxtAbilityBaseValue_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtAbilityBaseValue.Text, out int v) || v < 1 || v > 30)
            {
                txtAbilityBaseValue.Text = AbilityRefs(_editorAbility).score.Text;
                return;
            }

            AbilityService.SetBaseScore(_characterId, _editorAbility, v);
            LoadCharacterData();          // пересчитает карточку и все навыки
            RefreshEditorHeader();
            ShowToastSafe($"Значение: {v}");
        }

        // ========================= CRUD БОНУСОВ (через сервис) =========================

        public int GetBonusSum(string ability, string scope)
            => AbilityService.GetBonusSum(_characterId, ability, scope);

        private void RebuildBonusPanels()
        {
            BuildRows(panelCheckBonuses, "Check");
            BuildRows(panelSaveBonuses, "Save");
        }

        private void BuildRows(StackPanel panel, string scope)
        {
            panel.Children.Clear();
            // SQL больше не здесь — сервис отдаёт готовые DTO
            var rows = AbilityService.GetBonuses(_characterId, _editorAbility, scope);
            foreach (var row in rows)
                panel.Children.Add(MakeBonusRow(row));
        }

        /// Сборка UI-строки бонуса (это презентация, остаётся во View)
        private UIElement MakeBonusRow(AbilityBonus bonus)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            // Поле значения
            var tbValue = new TextBox { Text = bonus.Value.ToString(), TextAlignment = TextAlignment.Center };
            tbValue.SetResourceReference(StyleProperty, "TextField");
            int capturedValue = bonus.Value;
            tbValue.LostFocus += (s, e) =>
            {
                if (int.TryParse(tbValue.Text, out int v)) UpdateBonus(bonus.Id, null, v, null);
                else tbValue.Text = capturedValue.ToString();
            };
            Grid.SetColumn(tbValue, 0);

            // Поле имени
            var tbName = new TextBox { Text = bonus.Name };
            tbName.SetResourceReference(StyleProperty, "TextField");
            tbName.Margin = new Thickness(6, 0, 6, 0);
            string capturedName = bonus.Name;
            tbName.LostFocus += (s, e) => UpdateBonus(bonus.Id, tbName.Text, null, null);
            Grid.SetColumn(tbName, 1);

            // Кнопка "глаз" (активен/выключен)
            var btnEye = new Button { Content = Marker(bonus.Active), ToolTip = bonus.Active ? "Бонус учтён" : "Бонус выключен" };
            btnEye.SetResourceReference(StyleProperty, "RoundBtn");
            btnEye.Foreground = new SolidColorBrush((Color)FindResource(bonus.Active ? "C.Gold" : "C.Mute"));
            bool capturedActive = bonus.Active;
            btnEye.Click += (s, e) => UpdateBonus(bonus.Id, null, null, !capturedActive);
            Grid.SetColumn(btnEye, 2);

            // Кнопка удаления
            var btnDel = new Button { ToolTip = "Удалить бонус" }; btnDel.Content = new Helpers.IconView { Kind = "close", IconSize = 13, IconBrush = (Brush)FindResource("B.Mute") };
            btnDel.SetResourceReference(StyleProperty, "RoundBtn");
            btnDel.Foreground = new SolidColorBrush((Color)FindResource("C.Danger"));
            btnDel.Click += (s, e) => DeleteBonus(bonus.Id);
            Grid.SetColumn(btnDel, 3);

            grid.Children.Add(tbValue); grid.Children.Add(tbName); grid.Children.Add(btnEye); grid.Children.Add(btnDel);
            if (!bonus.Active) grid.Opacity = 0.5;
            return grid;
        }

        private void UpdateBonus(int id, string name, int? value, bool? active)
        {
            AbilityService.UpdateBonus(id, name, value, active);
            AfterBonusChange();
        }

        private void DeleteBonus(int id)
        {
            AbilityService.DeleteBonus(id);
            AfterBonusChange();
        }

        private void BtnAddCheckBonus_Click(object sender, RoutedEventArgs e) => AddBonus("Check");
        private void BtnAddSaveBonus_Click(object sender, RoutedEventArgs e) => AddBonus("Save");

        private void AddBonus(string scope)
        {
            AbilityService.AddBonus(_characterId, _editorAbility, scope);
            AfterBonusChange();
        }

        private void AfterBonusChange()
        {
            RebuildBonusPanels();
            RecalcAbilityCard(_editorAbility);
            RefreshEditorHeader();
        }

        // ========================= ТОГЛ ВЛАДЕНИЯ СПАСБРОСКОМ ИЗ ОВЕРЛЕЯ =========================

        private void BtnSaveProfOverlay_Click(object sender, RoutedEventArgs e)
        {
            string saveKey = AbilitySaveKey[_editorAbility];
            AbilityService.ToggleSaveThrowProficiency(_characterId, saveKey);
            RefreshSaveProfOverlayButton();
            RecalcAbilityCard(_editorAbility);
            RefreshEditorHeader();
            UpdateToggleVisual(AbilityRefs(_editorAbility).saveProfBtn,
                               AbilityService.ReadSaveProficiency(_characterId, saveKey));
        }

        // ========================= ПЕРЕСЧЁТ КАРТОЧКИ С УЧЁТОМ БОНУСОВ =========================

        private void RecalcAbilityCard(string ability)
        {
            var refs = AbilityRefs(ability);
            int mod = CalculateModifier(int.Parse(refs.score.Text));
            int profBonus = int.Parse(txtProfBonus.Text.Replace("+", ""));
            bool hasProf = AbilityService.ReadSaveProficiency(_characterId, AbilitySaveKey[ability]);
            int saveVal = mod + (hasProf ? profBonus : 0) + GetBonusSum(ability, "Save");
            refs.saveVal.Text = FormatModifier(saveVal);
        }

        private void ShowToastSafe(string message)
        {
            if (Window.GetWindow(this) is CharacterInfoWindow w) w.ShowToast(message);
        }
        #endregion

        #region === ПЕРЕКЛЮЧЕНИЕ ТАБОВ ===
        private void TabMain_Checked(object sender, RoutedEventArgs e)
        {
            if (tabMain != null && tabPersonality != null)
            {
                tabMain.Visibility = Visibility.Visible;
                tabPersonality.Visibility = Visibility.Collapsed;
            }
        }

        private void TabPersonality_Checked(object sender, RoutedEventArgs e)
        {
            if (tabMain != null && tabPersonality != null)
            {
                tabMain.Visibility = Visibility.Collapsed;
                tabPersonality.Visibility = Visibility.Visible;
            }
        }
        #endregion

        #region === ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ===
        /// <summary>
        /// Модификатор характеристики: половина разницы с 10, округление вниз.
        ///
        /// Именно вниз: при значении 9 модификатор −1, а целочисленное деление
        /// в C# отбрасывает дробь к нулю и давало 0. Из-за этого все нечётные
        /// значения ниже 10 были завышены на единицу — вместе с навыками,
        /// спасбросками и пассивными значениями.
        /// </summary>
        private int CalculateModifier(int score) => (int)Math.Floor((score - 10) / 2.0);

        private string FormatModifier(int mod) => mod >= 0 ? $"+{mod}" : mod.ToString();

        private void UpdateToggleVisual(Button btn, bool isActive)
        {
            if (btn == null) return;

            // Только круглый значок, без слов «есть» и «нет»: состояние видно по
            // цвету и заполнению — закрашенный золотой кружок или пустое кольцо.
            // Слова дублировали то же самое и занимали место.
            btn.Content = Marker(isActive);
            btn.FontSize = 18;
            btn.ToolTip = isActive
                ? "Владение есть: бонус мастерства учитывается\nНажмите, чтобы снять"
                : "Владения нет: бонус мастерства не учитывается\nНажмите, чтобы добавить";
            btn.Foreground = isActive
                ? new SolidColorBrush((Color)FindResource("C.Gold"))
                : new SolidColorBrush((Color)FindResource("C.Mute"));
        }

        /// <summary>
        /// Кнопка навыка: три ступени — нет, владение, экспертиза. Владение
        /// даёт один бонус мастерства, экспертиза второй, поэтому и метка
        /// у них разная.
        /// </summary>
        private void UpdateSkillToggleVisual(Button btn, SkillProficiencyLevel level)
        {
            if (btn == null) return;

            btn.Content = Marker(level);
            btn.FontSize = 18;
            btn.BorderBrush = level == SkillProficiencyLevel.Expert
                ? (Brush)FindResource("B.Gold")
                : (Brush)FindResource("B.Line");
            btn.Foreground = level == SkillProficiencyLevel.None
                ? new SolidColorBrush((Color)FindResource("C.Mute"))
                : new SolidColorBrush((Color)FindResource("C.Gold"));

            switch (level)
            {
                case SkillProficiencyLevel.Expert:
                    btn.ToolTip = "Экспертиза: бонус мастерства удваивается\nНажмите, чтобы снять";
                    break;
                case SkillProficiencyLevel.Proficient:
                    btn.ToolTip = "Владение: бонус мастерства учитывается\nНажмите, чтобы поставить экспертизу";
                    break;
                default:
                    btn.ToolTip = "Владения нет: бонус мастерства не учитывается\nНажмите, чтобы добавить";
                    break;
            }
        }

        /// <summary>Ступень владения навыком по прочитанному флагу из базы.</summary>
        private SkillProficiencyLevel SkillLevel(string skillKey, int flag)
        {
            if (flag <= 0) return SkillProficiencyLevel.None;
            return _expertSkills.Contains(skillKey) ? SkillProficiencyLevel.Expert : SkillProficiencyLevel.Proficient;
        }

        /// <summary>
        /// Бонус владения навыком: владение даёт бонус мастерства один раз,
        /// экспертиза — второй. По правилам это и есть удвоенный бонус.
        /// </summary>
        private int SkillBonus(string skillKey, int flag)
        {
            if (flag <= 0) return 0;

            int bonus = ProfBonus();
            if (_expertSkills.Contains(skillKey)) bonus += ProfBonus();
            return bonus;
        }
        #endregion

        #region === ОБРАБОТЧИКИ СОБЫТИЙ ===
        private void AbilityScore_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string ability)
                OpenAbilityEditor(ability);   // то же окно, что открывал карандаш
        }

        private void ToggleProficiency_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                string skillName = btn.Name.Replace("btnProf", "");

                // Круг ступеней: нет → владение → экспертиза → нет
                AbilityService.ToggleSkillProficiency(_characterId, skillName);
                LoadCharacterData();
            }
        }

        private void ToggleSaveProf_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                string ability = btn.Name.Replace("btnSaveProf", "");
                ToggleSaveThrowProficiency(ability);
                LoadCharacterData();
            }
        }

        private void ToggleSaveThrowProficiency(string ability)
    => AbilityService.ToggleSaveThrowProficiency(_characterId, ability);

        private void AddInfo_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is string field)
            {
                SaveAdditionalInfo(field, tb.Text);
            }
        }

        #region === ВЛАДЕНИЯ: ДОСПЕХИ И ОРУЖИЕ ===

        /// <summary>
        /// Категории владения доспехами по книге правил.
        ///
        /// Свободное текстовое поле заменено кнопками: игрок просто отмечает
        /// категории, и они сохраняются в тех же колонках базы, что и раньше.
        /// </summary>
        private static readonly string[] ArmorProficiencies =
        {
            "Лёгкие доспехи", "Средние доспехи", "Тяжёлые доспехи", "Щиты"
        };

        /// <summary>Владение оружием: категории и распространённые виды.</summary>
        private static readonly string[] WeaponProficiencies =
        {
            "Простое оружие", "Воинское оружие",
            "Кинжалы", "Дротики", "Пращи", "Дубинки", "Булавы", "Топоры", "Копья",
            "Мечи", "Луки", "Арбалеты", "Алебарды", "Молоты", "Рапиры", "Посохи"
        };

        /// <summary>Отмеченные владения. Заполняется при загрузке и при кликах.</summary>
        private readonly HashSet<string> _armorProf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _weaponProf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Строит кнопки владений. Вызывается один раз при загрузке вида.
        /// </summary>
        private void BuildProficiencyToggles()
        {
            FillProficiencyPanel(wpArmorProf, ArmorProficiencies, _armorProf);
            FillProficiencyPanel(wpWeaponProf, WeaponProficiencies, _weaponProf);
        }

        private void FillProficiencyPanel(Panel panel, string[] kinds, HashSet<string> selected)
        {
            if (panel == null) return;
            panel.Children.Clear();

            foreach (var kind in kinds)
            {
                var button = new Button
                {
                    Content = kind,
                    Tag = kind,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12,
                    Cursor = Cursors.Hand,
                    ToolTip = "Клик — отметить владение, повторный клик — снять"
                };

                button.Click += (s, e) =>
                {
                    if (selected.Contains(kind)) selected.Remove(kind);
                    else selected.Add(kind);

                    UpdateProficiencyButtonVisual(button, selected.Contains(kind));
                    SaveProficiencies();
                };

                UpdateProficiencyButtonVisual(button, selected.Contains(kind));
                panel.Children.Add(button);
            }
        }

        /// <summary>Подсвечивает кнопку, если владение отмечено.</summary>
        private void UpdateProficiencyButtonVisual(Button button, bool active)
        {
            try
            {
                button.Background = active ? (Brush)FindResource("B.Gold") : (Brush)FindResource("B.Slate");
                button.Foreground = active ? new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x06)) : (Brush)FindResource("B.Ink");
                button.BorderBrush = active ? (Brush)FindResource("B.Gold") : (Brush)FindResource("B.Line");
                button.BorderThickness = new Thickness(1);
                button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CharacterInfoView] вид кнопки владения: {ex.Message}");
            }
        }

        /// <summary>
        /// Разбирает сохранённую строку владений и отмечает кнопки.
        ///
        /// Старые записи — свободный текст, поэтому всё, что не совпало с известными
        /// категориями, уходит в поле «Другое»: ничего не теряется.
        /// </summary>
        private void LoadProficiencyToggles(string armorValue, string weaponsValue)
        {
            _armorProf.Clear();
            _weaponProf.Clear();

            var others = new List<string>();

            // Новый формат — только отмеченные категории через «;»
            string armorRest = SplitKnown(armorValue, ArmorProficiencies, _armorProf);
            string weaponRest = SplitKnown(weaponsValue, WeaponProficiencies, _weaponProf);

            if (!string.IsNullOrWhiteSpace(armorRest)) others.Add(armorRest);
            if (!string.IsNullOrWhiteSpace(weaponRest)) others.Add(weaponRest);

            if (txtArmorProfCustom != null) txtArmorProfCustom.Text = string.Join(", ", others);

            BuildProficiencyToggles();
        }

        /// <summary>
        /// Раскладывает строку на известные категории и остаток.
        /// Разделители — запятая и точка с запятой.
        /// </summary>
        private static string SplitKnown(string value, string[] known, HashSet<string> selected)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            var rest = new List<string>();
            foreach (var part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();

                string match = known.FirstOrDefault(k =>
                    string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase));

                if (match != null) selected.Add(match);
                else rest.Add(trimmed);
            }

            return string.Join(", ", rest);
        }

        /// <summary>
        /// Сохраняет отмеченные владения в те же колонки базы, что и раньше:
        /// доспехи — в Attachments, оружие — в Weapons. Категории разделяем
        /// точкой с запятой, чтобы разбор не путался с запятыми в свободном тексте.
        /// </summary>
        private void SaveProficiencies()
        {
            try
            {
                SaveAdditionalInfo("Attachments", string.Join("; ", _armorProf));
                SaveAdditionalInfo("Weapons", string.Join("; ", _weaponProf));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CharacterInfoView] сохранение владений: {ex.Message}");
            }
        }

        #endregion

        private void SaveAdditionalInfo(string field, string value)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                string checkQuery = "SELECT COUNT(*) FROM CharacterIdAddInfo WHERE id_character = @id";
                using var checkCmd = new SqliteCommand(checkQuery, conn);
                checkCmd.Parameters.AddWithValue("@id", _characterId);
                int count = Convert.ToInt32(checkCmd.ExecuteScalar());

                if (count == 0)
                {
                    string insertQuery = "INSERT INTO CharacterIdAddInfo (id_character) VALUES (@id)";
                    using var insertCmd = new SqliteCommand(insertQuery, conn);
                    insertCmd.Parameters.AddWithValue("@id", _characterId);
                    insertCmd.ExecuteNonQuery();
                }

                string updateQuery = $"UPDATE CharacterIdAddInfo SET {field} = @val WHERE id_character = @id";
                using var updateCmd = new SqliteCommand(updateQuery, conn);
                updateCmd.Parameters.AddWithValue("@val", value);
                updateCmd.Parameters.AddWithValue("@id", _characterId);
                updateCmd.ExecuteNonQuery();
                (Window.GetWindow(this) as CharacterInfoWindow)?.ShowToast("Сохранено");

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SaveInfo] {ex.Message}");
            }
        }

        private void Alignment_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag && int.TryParse(tag, out int ideology))
            {
                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();
                    string query = "UPDATE CharacterInfo SET Ideology = @val WHERE id_character = @id";
                    using var cmd = new SqliteCommand(query, conn);
                    cmd.Parameters.AddWithValue("@val", ideology);
                    cmd.Parameters.AddWithValue("@id", _characterId);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Alignment] {ex.Message}");
                }
            }
        }

        private void BtnSearchBackground_Click(object sender, RoutedEventArgs e)
        {
            backgroundPickerOverlay.Visibility = Visibility.Visible;
            RefreshBackgroundList();   // список из базы, а не заглушка
        }

        private void BackgroundPicker_Close(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender)
                backgroundPickerOverlay.Visibility = Visibility.Collapsed;
        }

        private void BackgroundSearch_TextChanged(object sender, TextChangedEventArgs e)
            => RefreshBackgroundList();

        /// <summary>
        /// Показывает список предысторий: встроенные из книги правил и свои.
        ///
        /// Раньше это была заглушка из восьми английских названий, и создать свою
        /// предысторию было нельзя. Теперь список берётся из базы, у каждой
        /// предыстории видны владения навыками, а рядом есть кнопка создания.
        /// </summary>
        private void RefreshBackgroundList()
        {
            if (backgroundListPanel == null) return;

            string query = (txtBackgroundSearch?.Text ?? "").Trim();
            var all = Services.BackgroundService.LoadAll();

            backgroundListPanel.Children.Clear();

            var filtered = string.IsNullOrEmpty(query)
                ? all
                : all.Where(b => (b.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                                 || (b.SkillProficiencies ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                     .ToList();

            if (filtered.Count == 0)
            {
                backgroundListPanel.Children.Add(new TextBlock
                {
                    Text = all.Count == 0
                        ? "Список предысторий пуст. Создайте свою кнопкой «Своя предыстория»."
                        : "Ничего не найдено. Можно создать свою предысторию.",
                    Foreground = (Brush)FindResource("B.Mute"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                });
                return;
            }

            foreach (var bg in filtered)
                backgroundListPanel.Children.Add(BuildBackgroundRow(bg));
        }

        /// <summary>Строка списка предысторий: название, владения и кнопки.</summary>
        private Border BuildBackgroundRow(Services.BackgroundData bg)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = bg.Name,
                Foreground = (Brush)FindResource("B.Ink"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(bg.SkillProficiencies)) details.Add("навыки: " + bg.SkillProficiencies);
            if (!string.IsNullOrWhiteSpace(bg.ToolProficiencies) && bg.ToolProficiencies != "—")
                details.Add("инструменты: " + bg.ToolProficiencies);
            if (!string.IsNullOrWhiteSpace(bg.Languages)) details.Add("языки: " + bg.Languages);

            if (details.Count > 0)
            {
                text.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", details),
                    Foreground = (Brush)FindResource("B.Mute"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            if (!string.IsNullOrWhiteSpace(bg.Feature))
            {
                text.Children.Add(new TextBlock
                {
                    Text = "Умение: " + bg.Feature,
                    Foreground = (Brush)FindResource("B.Frost"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

            var pick = new Button
            {
                Content = "Выбрать",
                Style = (Style)FindResource("MagicBtn"),
                MinWidth = 96,

                // Отступ задаём на месте: у стиля он рассчитан на крупные кнопки
                // (16,8), а здесь кнопка невысокая — с ним текст обрезался
                Padding = new Thickness(14, 6, 14, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            pick.Click += (s, a) => ChooseBackground(bg);
            buttons.Children.Add(pick);

            if (bg.IsCustom || true)   // править можно и встроенную: это игра игрока
            {
                var edit = new Button
                {
                    Content = new Helpers.IconView { Kind = "edit", IconSize = 15, IconBrush = (Brush)FindResource("B.Mute") },
                    Style = (Style)FindResource("GhostBtn"),
                    Width = 32,
                    Height = 28,
                    // У GhostBtn отступ 12,6 — в квадрате 32×28 он обрезал значок
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = "Изменить"
                };
                edit.Click += (s, a) => OpenBackgroundEditor(bg);
                buttons.Children.Add(edit);

                var del = new Button
                {
                    Content = new Helpers.IconView { Kind = "trash", IconSize = 14, IconBrush = (Brush)FindResource("B.Danger"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    Style = (Style)FindResource("GhostBtn"),
                    Width = 32,
                    Height = 28,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = "Удалить предысторию"
                };
                del.Click += (s, a) => DeleteBackground(bg);
                buttons.Children.Add(del);
            }

            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            return new Border
            {
                Background = (Brush)FindResource("B.Slate"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid
            };
        }

        /// <summary>
        /// Присваивает предысторию персонажу.
        ///
        /// Спрашиваем про навыки: у предыстории они свои, и игрок должен решить,
        /// отмечать их автоматически или он сделает это сам.
        /// </summary>
        private void ChooseBackground(Services.BackgroundData bg)
        {
            var skills = Services.BackgroundService.SplitSkills(bg.SkillProficiencies);
            bool applySkills = false;

            if (skills.Count > 0)
            {
                applySkills = MessageDialog.Confirm(Window.GetWindow(this),
                    $"Предыстория «{bg.Name}» даёт владение навыками: {string.Join(", ", skills)}.\n\n" +
                    "Отметить их у персонажа сейчас?",
                    "Предыстория выбрана", "Отметить", "Не надо");
            }

            Services.BackgroundService.ApplyToCharacter(_characterId, bg, applySkills);

            if (txtBackground != null) txtBackground.Text = bg.Name;
            if (pnlBackgroundEditor != null) pnlBackgroundEditor.Visibility = Visibility.Collapsed;
            if (backgroundPickerOverlay != null) backgroundPickerOverlay.Visibility = Visibility.Collapsed;

            if (applySkills)
            {
                // Навыки могли измениться — перечитываем значения на вкладке
                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();
                    LoadSkills();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CharacterInfoView] навыки после предыстории: {ex.Message}");
                }
            }
        }

        private void BtnBgCreate_Click(object sender, RoutedEventArgs e) => OpenBackgroundEditor(null);

        /// <summary>
        /// Сохраняет название предыстории, введённое руками, и подтягивает связь
        /// со справочником, если предыстория с таким названием есть.
        ///
        /// Нужно, чтобы правки в справочнике потом находили персонажа: без ссылки
        /// карточка жила бы отдельно от предыстории.
        /// </summary>
        private void BackgroundText_LostFocus(object sender, RoutedEventArgs e)
        {
            string name = (txtBackground?.Text ?? "").Trim();

            SaveAdditionalInfo("Background", name);
            if (name.Length == 0) return;

            try
            {
                var found = Services.BackgroundService.Find(name);
                if (found != null) Services.BackgroundService.SaveCharacterLink(_characterId, found);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CharacterInfoView] связь предыстории: {ex.Message}");
            }
        }

        /// <summary>
        /// Флажки навыков в форме предыстории.
        ///
        /// Строятся из общего списка навыков: игрок выбирает из восемнадцати
        /// названий, и строка владений всегда совпадает с тем, что понимает
        /// таблица характеристик. Пока навыки писали руками, любая опечатка
        /// молча ломала отметку — предыстория «давала» навык, которого не было.
        /// </summary>
        private readonly Dictionary<string, CheckBox> _bgSkillBoxes =
            new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Владения из старой записи, которые не удалось опознать.</summary>
        private List<string> _bgUnknownSkills = new List<string>();

        private void BuildBackgroundSkillPicker()
        {
            if (bg_pnlSkills == null) return;

            bg_pnlSkills.Children.Clear();
            _bgSkillBoxes.Clear();

            foreach (string ability in SkillCatalog.AbilityOrder)
            {
                var skills = SkillCatalog.ForAbility(ability);
                if (skills.Count == 0) continue;

                bg_pnlSkills.Children.Add(new TextBlock
                {
                    Text = ability,
                    Foreground = (Brush)FindResource("B.Mute"),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, bg_pnlSkills.Children.Count == 0 ? 0 : 8, 0, 4)
                });

                var row = new WrapPanel();
                foreach (var skill in skills)
                {
                    var box = new CheckBox
                    {
                        Content = skill.Russian,
                        Style = (Style)FindResource("CustomCheckBox"),
                        Margin = new Thickness(0, 0, 16, 4),
                        Tag = skill.Key
                    };
                    _bgSkillBoxes[skill.Key] = box;
                    row.Children.Add(box);
                }
                bg_pnlSkills.Children.Add(row);
            }
        }

        /// <summary>Отмечает в форме навыки предыстории.</summary>
        private void LoadBackgroundSkills(string skills)
        {
            var keys = SkillCatalog.KeysFrom(skills);

            _bgUnknownSkills = Services.BackgroundService.SplitSkills(skills)
                .Where(name => SkillCatalog.KeyOf(name) == null)
                .ToList();

            foreach (var pair in _bgSkillBoxes)
                pair.Value.IsChecked = keys.Contains(pair.Key);

            if (bg_txtSkillsHint != null)
            {
                bg_txtSkillsHint.Text = _bgUnknownSkills.Count == 0
                    ? ""
                    : "Не опознаны и останутся как есть: " + string.Join(", ", _bgUnknownSkills);
                bg_txtSkillsHint.Visibility = _bgUnknownSkills.Count == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        /// <summary>Собирает владения навыками из отмеченных флажков.</summary>
        private string CollectBackgroundSkills()
        {
            var keys = _bgSkillBoxes
                .Where(pair => pair.Value.IsChecked == true)
                .Select(pair => pair.Key)
                .ToList();

            var parts = new List<string>();
            string known = SkillCatalog.JoinRussian(keys);
            if (known.Length > 0) parts.Add(known);

            // Непонятые названия из старой записи не выбрасываем: игрок мог
            // написать что-то своё, и терять это при сохранении нельзя
            parts.AddRange(_bgUnknownSkills);

            return string.Join(", ", parts);
        }

        /// <summary>Открывает форму предыстории: пустую или с данными для правки.</summary>
        private void OpenBackgroundEditor(Services.BackgroundData bg)
        {
            if (pnlBackgroundEditor == null) return;

            _editingBackground = bg;

            txtBackgroundEditorTitle.Text = bg == null ? "НОВАЯ ПРЕДЫСТОРИЯ" : "ИЗМЕНЕНИЕ ПРЕДЫСТОРИИ";
            bg_txtName.Text = bg?.Name ?? "";
            LoadBackgroundSkills(bg?.SkillProficiencies);
            bg_txtTools.Text = bg?.ToolProficiencies ?? "";
            bg_txtLanguages.Text = bg?.Languages ?? "";
            bg_txtFeature.Text = bg?.Feature ?? "";
            bg_txtEquipment.Text = bg?.Equipment ?? "";
            bg_txtDescription.Text = bg?.Description ?? "";

            pnlBackgroundEditor.Visibility = Visibility.Visible;
            bg_txtName.Focus();
        }

        private void Bg_BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (pnlBackgroundEditor != null) pnlBackgroundEditor.Visibility = Visibility.Collapsed;
            _editingBackground = null;
        }

        private void Bg_BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string name = (bg_txtName?.Text ?? "").Trim();
            if (name.Length == 0)
            {
                MessageDialog.Error(Window.GetWindow(this), "Укажите название предыстории.", "Не хватает названия");
                return;
            }

            var data = new Services.BackgroundData
            {
                Name = name,
                SkillProficiencies = CollectBackgroundSkills(),
                ToolProficiencies = (bg_txtTools?.Text ?? "").Trim(),
                Languages = (bg_txtLanguages?.Text ?? "").Trim(),
                Feature = (bg_txtFeature?.Text ?? "").Trim(),
                Equipment = (bg_txtEquipment?.Text ?? "").Trim(),
                Description = (bg_txtDescription?.Text ?? "").Trim()
            };

            // Если правим встроенную, метку «своя» не ставим
            bool markCustom = _editingBackground == null || _editingBackground.IsCustom;
            int id = Services.BackgroundService.Save(data, markCustom);

            if (id <= 0)
            {
                MessageDialog.Error(Window.GetWindow(this), "Не удалось сохранить предысторию.", "Ошибка");
                return;
            }

            _editingBackground = null;
            pnlBackgroundEditor.Visibility = Visibility.Collapsed;
            RefreshBackgroundList();
        }

        /// <summary>Удаляет предысторию из справочника.</summary>
        private void DeleteBackground(Services.BackgroundData bg)
        {
            if (bg == null) return;

            bool confirmed = MessageDialog.Confirm(Window.GetWindow(this),
                $"Удалить предысторию «{bg.Name}» из списка?\n\n" +
                "У персонажей, которым она выбрана, название останется в карточке.",
                "Удаление предыстории", "Удалить", "Отмена", danger: true);

            if (!confirmed) return;

            if (Services.BackgroundService.Delete(bg.Id))
            {
                RefreshBackgroundList();
            }
            else
            {
                MessageDialog.Error(Window.GetWindow(this), "Не удалось удалить предысторию.", "Ошибка");
            }
        }

        /// <summary>Предыстория, которую сейчас правят (null — создаётся новая).</summary>
        private Services.BackgroundData _editingBackground;

        // Броски навыков (через Window API)
        private void BtnStrCheck_Click(object sender, RoutedEventArgs e) => RollAbilityCheck("СИЛА", int.Parse(txtStrScore.Text));
        private void BtnDexCheck_Click(object sender, RoutedEventArgs e) => RollAbilityCheck("ЛОВКОСТЬ", int.Parse(txtDexScore.Text));
        private void BtnConCheck_Click(object sender, RoutedEventArgs e) => RollAbilityCheck("ТЕЛОСЛОЖЕНИЕ", int.Parse(txtConScore.Text));
        private void BtnIntCheck_Click(object sender, RoutedEventArgs e) => RollAbilityCheck("ИНТЕЛЛЕКТ", int.Parse(txtIntScore.Text));
        private void BtnWisCheck_Click(object sender, RoutedEventArgs e) => RollAbilityCheck("МУДРОСТЬ", int.Parse(txtWisScore.Text));
        private void BtnChaCheck_Click(object sender, RoutedEventArgs e) => RollAbilityCheck("ХАРИЗМА", int.Parse(txtChaScore.Text));

        private void BtnStrSave_Click(object sender, RoutedEventArgs e) => RollSaveThrow("СИЛА", int.Parse(txtStrScore.Text));
        private void BtnDexSave_Click(object sender, RoutedEventArgs e) => RollSaveThrow("ЛОВКОСТЬ", int.Parse(txtDexScore.Text));
        private void BtnConSave_Click(object sender, RoutedEventArgs e) => RollSaveThrow("ТЕЛОСЛОЖЕНИЕ", int.Parse(txtConScore.Text));
        private void BtnIntSave_Click(object sender, RoutedEventArgs e) => RollSaveThrow("ИНТЕЛЛЕКТ", int.Parse(txtIntScore.Text));
        private void BtnWisSave_Click(object sender, RoutedEventArgs e) => RollSaveThrow("МУДРОСТЬ", int.Parse(txtWisScore.Text));
        private void BtnChaSave_Click(object sender, RoutedEventArgs e) => RollSaveThrow("ХАРИЗМА", int.Parse(txtChaScore.Text));

        /// <summary>
        /// Проверка характеристики. По правилам бонус уже известен (модификатор + бонусы),
        /// поэтому бросаем сразу и показываем результат — без окна выбора кубиков.
        /// </summary>
        private void RollAbilityCheck(string ability, int score)
        {
            int bonus = CalculateModifier(score) + GetBonusSum(ability, "Check");
            RollNow("Проверка", ability, bonus, $"1d20{(bonus >= 0 ? "+" : "")}{bonus}");
        }

        /// <summary>
        /// Спасбросок. Бонус берём прямо с карточки: там уже модификатор + владение +
        /// бонусы спасброска, поэтому отдельная настройка броска не нужна.
        /// </summary>
        private void RollSaveThrow(string ability, int score)
        {
            int bonus = CalculateModifier(score)
                        + (AbilityService.ReadSaveProficiency(_characterId, AbilitySaveKey[ability]) ? ProfBonus() : 0)
                        + GetBonusSum(ability, "Save");
            RollNow("Спасбросок", ability, bonus, $"1d20{(bonus >= 0 ? "+" : "")}{bonus}");
        }

        /// <summary>Бонус мастерства из шапки карточки.</summary>
        private int ProfBonus()
            => int.TryParse((txtProfBonus.Text ?? "").Replace("+", "").Trim(), out int p) ? p : 0;

        /// <summary>Немедленный бросок d20 с готовым бонусом и показом результата.</summary>
        private void RollNow(string rollType, string label, int bonus, string formula)
        {
            if (!(Window.GetWindow(this) is CharacterInfoWindow ciw))
            {
                ShowToastSafe("Не удалось выполнить бросок: окно персонажа не найдено");
                return;
            }

            ciw.RollAndShow(rollType, label, bonus, formula);
        }

        // Броски навыков
        private void BtnAthleticsRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Атлетика", txtAthleticsVal.Text);
        private void BtnAcrobaticsRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Акробатика", txtAcrobaticsVal.Text);
        private void BtnStealthRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Скрытность", txtStealthVal.Text);
        private void BtnSleightOfHandRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Ловкость рук", txtSleightOfHandVal.Text);
        private void BtnAnalysisRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Анализ", txtAnalysisVal.Text);
        private void BtnHistoryRoll_Click(object sender, RoutedEventArgs e) => RollSkill("История", txtHistoryVal.Text);
        private void BtnMagicRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Магия", txtMagicVal.Text);
        private void BtnNatureRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Природа", txtNatureVal.Text);
        private void BtnReligionRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Религия", txtReligionVal.Text);
        private void BtnPerceptionRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Внимательность", txtPerceptionVal.Text);
        private void BtnSurvivalRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Выживание", txtSurvivalVal.Text);
        private void BtnMedicineRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Медицина", txtMedicineVal.Text);
        private void BtnInsightRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Проницательность", txtInsightVal.Text);
        private void BtnAnimalCareRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Уход за животными", txtAnimalCareVal.Text);
        private void BtnPerformanceRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Выступление", txtPerformanceVal.Text);
        private void BtnDeceptionRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Обман", txtDeceptionVal.Text);
        private void BtnPersuasionRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Убеждение", txtPersuasionVal.Text);
        private void BtnIntimidationRoll_Click(object sender, RoutedEventArgs e) => RollSkill("Запугивание", txtIntimidationVal.Text);

        /// <summary>Навык: бонус уже посчитан и виден на карточке — бросаем сразу.</summary>
        private void RollSkill(string skillName, string bonusText)
        {
            int bonus = ParseModifierText(bonusText);
            RollNow("Навык", skillName, bonus, $"1d20{(bonus >= 0 ? "+" : "")}{bonus}");
        }

        /// <summary>Разбор текста вида «+3», «-1», «0».</summary>
        private static int ParseModifierText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return int.TryParse(text.Replace("+", "").Trim(), out int v) ? v : 0;
        }
        #endregion
    }
}
