using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Форма существа: всё, что есть в карточке бестиария.
    ///
    /// Форма собрана кодом: полей очень много, и разметка на каждое превратилась
    /// бы в тысячи строк. Зато здесь в одном месте видно, какие поля есть у
    /// существа, и добавление нового поля — это одна строка.
    ///
    /// Формула урона в записи атаки живая: «2к8+6 рубящим» подсвечено и по
    /// нажатию бросается, не выходя из редактирования.
    /// </summary>
    public sealed class MonsterEditor : UserControl
    {
        // ══════════════════════ СПРАВОЧНИКИ ══════════════════════

        private static readonly string[] Sizes =
        {
            "", "Крошечный", "Маленький", "Средний", "Большой", "Огромный", "Громадный"
        };

        private static readonly string[] CreatureTypes =
        {
            "", "Аберрация", "Зверь", "Небожитель", "Конструкт", "Дракон", "Элементаль",
            "Фея", "Исчадие", "Великан", "Гуманоид", "Монстр", "Слизь", "Растение", "Нежить"
        };

        private static readonly string[] Alignments =
        {
            "", "без мировоззрения", "любое мировоззрение", "любое злое мировоззрение",
            "законно-добрый", "нейтрально-добрый", "хаотично-добрый",
            "законно-нейтральный", "нейтральный", "хаотично-нейтральный",
            "законно-злой", "нейтрально-злой", "хаотично-злой"
        };

        private static readonly string[] ChallengeRatings =
        {
            "0", "1/8", "1/4", "1/2", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
            "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "23", "24",
            "25", "26", "27", "28", "29", "30"
        };

        private static readonly string[] Languages =
        {
            "Общий", "Дварфийский", "Эльфийский", "Гномий", "Полуросликов", "Орочий", "Гоблинский",
            "Драконий", "Инфернальный", "Небесный", "Бездны", "Великаний", "Сильван", "Подземный",
            "Акван", "Ауран", "Игнан", "Телепатия 60 фт."
        };

        private static readonly string[] Conditions =
        {
            "Бессознательный", "Испуганный", "Истощённый", "Невидимый", "Недееспособный", "Оглохший",
            "Окаменевший", "Опутанный", "Ослеплённый", "Отравленный", "Очарованный", "Ошеломлённый",
            "Парализованный", "Сбитый с ног", "Схваченный"
        };

        // ══════════════════════ СОСТОЯНИЕ ══════════════════════

        private MonsterData _monster = new MonsterData();
        private bool _isNew = true;
        private string _pendingPhotoPath;
        private bool _removePhoto;

        private readonly StackPanel _form = new StackPanel { Margin = new Thickness(16, 8, 8, 16) };
        private readonly StackPanel _preview = new StackPanel { Margin = new Thickness(8, 8, 16, 16) };
        private readonly StackPanel _traitsHost = new StackPanel();
        private readonly StackPanel _actionsHost = new StackPanel();
        private readonly TextBlock _headerText = new TextBlock();
        private readonly TextBlock _validationText = new TextBlock();

        /// <summary>
        /// Подпись выбранной картинки. Создаётся заново на каждую сборку формы:
        /// переиспользовать элемент нельзя — он остаётся логическим ребёнком
        /// прежней панели, и WPF роняет окно при повторном добавлении.
        /// </summary>
        private TextBlock _photoText;

        private TextBox _name, _nameEn, _subtype, _acNote, _hpFormula, _speedNote, _saves, _skills,
            _vulnerabilities, _resistances, _immunities, _conditionImmunities, _senses, _languages,
            _habitat, _treasure, _source, _tags, _legendaryIntro, _lairText, _spells, _equipment, _description;
        private TextBox _armorClass, _hitPoints, _speedWalk, _speedFly, _speedSwim, _speedClimb, _speedBurrow,
            _strength, _dexterity, _constitution, _intelligence, _wisdom, _charisma, _experience, _proficiency;
        private ComboBox _size, _creatureType, _alignment, _challenge;
        private CheckBox _hover;
        private WrapPanel _abilityMods;

        /// <summary>Существо сохранено — бестиарию нужно обновить список.</summary>
        public event Action<MonsterData> Saved;

        /// <summary>Правка отменена.</summary>
        public event Action Cancelled;

        public MonsterEditor()
        {
            BuildShell();
            LoadMonster(new MonsterData(), true);
        }

        public MonsterData Current => _monster;

        // ══════════════════════ СБОРКА ОБОЛОЧКИ ══════════════════════

        private void BuildShell()
        {
            var root = new Grid { Background = DmUiKit.B("Panel") };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── шапка ──
            var header = new Grid { Margin = new Thickness(16, 12, 16, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _headerText.FontFamily = DmUiKit.Display;
            _headerText.FontSize = 16;
            _headerText.FontWeight = FontWeights.Bold;
            _headerText.Foreground = DmUiKit.B("Gold");
            _headerText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_headerText, 0);
            header.Children.Add(_headerText);

            var headerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            headerButtons.Children.Add(DmUiKit.Action("💾 Сохранить", (s, e) => SaveCurrent(), DmUiKit.ButtonKind.Primary,
                "Сохранить существо в бестиарий (Ctrl+S)", 130, 32));
            headerButtons.Children.Add(DmUiKit.Action("Отмена", (s, e) => Cancel(), DmUiKit.ButtonKind.Ghost,
                "Закрыть без сохранения (Esc)", 90, 32));
            Grid.SetColumn(headerButtons, 1);
            header.Children.Add(headerButtons);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ── форма и предпросмотр ──
            // Привязку к строкам и столбцам ставим на саму прокрутку: если повесить
            // её на внутренний StackPanel, WPF положит обёртку в левый верхний угол,
            // и форма уедет под шапку
            var formScroll = DmUiKit.Scroll(_form);
            Grid.SetRow(formScroll, 1);
            Grid.SetColumn(formScroll, 0);
            root.Children.Add(formScroll);

            var previewHead = new TextBlock
            {
                Text = "ПРЕДПРОСМОТР КАРТОЧКИ",
                FontFamily = DmUiKit.Display,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Mute"),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var previewHost = new StackPanel();
            previewHost.Children.Add(previewHead);
            previewHost.Children.Add(_preview);

            var previewPanel = DmUiKit.Panel(previewHost, new Thickness(12), DmUiKit.B("Slate"), DmUiKit.B("Line"), 8,
                new Thickness(0, 8, 16, 16));

            var previewScroll = DmUiKit.Scroll(previewPanel);
            Grid.SetRow(previewScroll, 1);
            Grid.SetColumn(previewScroll, 1);
            root.Children.Add(previewScroll);

            // ── подвал: сообщение о проверке ──
            _validationText.FontSize = 11;
            _validationText.Foreground = DmUiKit.B("Danger");
            _validationText.Margin = new Thickness(16, 0, 16, 8);
            _validationText.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(_validationText, 2);
            root.Children.Add(_validationText);

            Content = root;

            // Обновление предпросмотра, когда пользователь уходит с поля
            _form.AddHandler(TextBox.LostFocusEvent, new RoutedEventHandler((s, e) => RefreshPreview()), true);
            _form.AddHandler(ComboBox.LostFocusEvent, new RoutedEventHandler((s, e) => RefreshPreview()), true);
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Cancel();
                else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) SaveCurrent();
            };
        }

        // ══════════════════════ ЗАГРУЗКА ══════════════════════

        /// <summary>Открывает существо для правки. isNew = true для нового.</summary>
        public void LoadMonster(MonsterData monster, bool isNew)
        {
            _monster = monster?.Clone() ?? new MonsterData();
            _isNew = isNew || _monster.Id <= 0;
            _pendingPhotoPath = null;
            _removePhoto = false;
            _validationText.Text = "";

            _headerText.Text = _isNew
                ? "НОВОЕ СУЩЕСТВО"
                : $"ПРАВКА: {(_monster.Name ?? "").ToUpperInvariant()}";

            BuildForm();
            RefreshPreview();
        }

        private void BuildForm()
        {
            _form.Children.Clear();

            // ── ОСНОВНОЕ ──
            _form.Children.Add(DmUiKit.SectionPanel("ОСНОВНОЕ"));

            _name = DmUiKit.Field(_monster.Name, 260, "Как существо называется в книге");
            _nameEn = DmUiKit.Field(_monster.NameEn, 220, "Оригинальное название (необязательно)");
            _size = DmUiKit.Combo(Sizes, _monster.Size, true, 150);
            _creatureType = DmUiKit.Combo(CreatureTypes, _monster.CreatureType, true, 160);
            _subtype = DmUiKit.Field(_monster.Subtype, 180, "Например: гоблиноид, друид, любая раса");
            _alignment = DmUiKit.Combo(Alignments, _monster.Alignment, true, 200);
            _source = DmUiKit.Field(_monster.Source, 120, "Откуда существо: MM14, доморощённое");
            _tags = DmUiKit.Field(_monster.Tags, 200, "Свои метки: «босс», «лес», «нежить»");

            _form.Children.Add(Wrap(
                DmUiKit.FieldBlock("НАЗВАНИЕ *", _name),
                DmUiKit.FieldBlock("НАЗВАНИЕ (ОРИГ.)", _nameEn),
                DmUiKit.FieldBlock("РАЗМЕР", _size),
                DmUiKit.FieldBlock("ТИП", _creatureType),
                DmUiKit.FieldBlock("ПОДТИП", _subtype),
                DmUiKit.FieldBlock("МИРОВОЗЗРЕНИЕ", _alignment),
                DmUiKit.FieldBlock("ИСТОЧНИК", _source),
                DmUiKit.FieldBlock("МЕТКИ", _tags)));

            var photoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            photoRow.Children.Add(DmUiKit.Action("🖼 Выбрать картинку", (s, e) => PickPhoto(), DmUiKit.ButtonKind.Ghost, null, 170, 28));
            photoRow.Children.Add(DmUiKit.Action("Убрать", (s, e) =>
            {
                _removePhoto = true;
                _pendingPhotoPath = null;
                UpdatePhotoText();
            }, DmUiKit.ButtonKind.Soft, "Убрать картинку у существа", 90, 28));
            _photoText = new TextBlock
            {
                FontSize = 10.5,
                Foreground = DmUiKit.B("Mute"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            photoRow.Children.Add(_photoText);
            _form.Children.Add(photoRow);
            UpdatePhotoText();

            // ── ЗАЩИТА ──
            _form.Children.Add(DmUiKit.SectionPanel("ЗАЩИТА"));

            _armorClass = DmUiKit.Field(_monster.ArmorClass.ToString(), 70);
            _acNote = DmUiKit.Field(_monster.ArmorClassNote, 240, "Например: кожаный доспех, щит");
            _hitPoints = DmUiKit.Field(_monster.HitPoints.ToString(), 80);
            _hpFormula = DmUiKit.Field(_monster.HitPointsFormula, 110, "Формула хитов: 2к6, 18к8+54");

            var hpRoll = DmUiKit.Action("🎲", (s, e) => RollHitPoints(), DmUiKit.ButtonKind.Ghost, "Бросить хиты по формуле", 34, 28);

            var hpBlock = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
            hpBlock.Children.Add(DmUiKit.Label("ХИТЫ"));
            var hpRow = new StackPanel { Orientation = Orientation.Horizontal };
            hpRow.Children.Add(_hitPoints);
            _hitPoints.Margin = new Thickness(0, 0, 6, 0);
            hpRow.Children.Add(_hpFormula);
            hpRow.Children.Add(hpRoll);
            hpBlock.Children.Add(hpRow);

            _form.Children.Add(Wrap(
                DmUiKit.FieldBlock("КЛАСС ДОСПЕХА", _armorClass),
                DmUiKit.FieldBlock("ПОЯСНЕНИЕ К КД", _acNote),
                hpBlock));

            _form.Children.Add(DmUiKit.SectionPanel("СКОРОСТЬ", "в футах; 0 — значит «нет такого способа движения»"));
            _speedWalk = DmUiKit.Field(_monster.SpeedWalk.ToString(), 70);
            _speedFly = DmUiKit.Field(_monster.SpeedFly.ToString(), 70);
            _speedSwim = DmUiKit.Field(_monster.SpeedSwim.ToString(), 70);
            _speedClimb = DmUiKit.Field(_monster.SpeedClimb.ToString(), 70);
            _speedBurrow = DmUiKit.Field(_monster.SpeedBurrow.ToString(), 70);
            _speedNote = DmUiKit.Field(_monster.SpeedNote, 200, "Например: только в воде");
            _hover = DmUiKit.Check("Парение (hover)", _monster.Hover);

            _form.Children.Add(Wrap(
                DmUiKit.FieldBlock("ХОДЬБА", _speedWalk),
                DmUiKit.FieldBlock("ПОЛЁТ", _speedFly),
                DmUiKit.FieldBlock("ПЛАВАНИЕ", _speedSwim),
                DmUiKit.FieldBlock("ЛАЗАНИЕ", _speedClimb),
                DmUiKit.FieldBlock("КОПАНИЕ", _speedBurrow),
                DmUiKit.FieldBlock("ПРИМЕЧАНИЕ", _speedNote)));

            _form.Children.Add(_hover);

            // ── ХАРАКТЕРИСТИКИ ──
            _form.Children.Add(DmUiKit.SectionPanel("ХАРАКТЕРИСТИКИ", "модификаторы считаются сами"));

            _strength = ScoreField(_monster.Strength);
            _dexterity = ScoreField(_monster.Dexterity);
            _constitution = ScoreField(_monster.Constitution);
            _intelligence = ScoreField(_monster.Intelligence);
            _wisdom = ScoreField(_monster.Wisdom);
            _charisma = ScoreField(_monster.Charisma);

            _form.Children.Add(Wrap(
                DmUiKit.FieldBlock("СИЛА", _strength),
                DmUiKit.FieldBlock("ЛОВКОСТЬ", _dexterity),
                DmUiKit.FieldBlock("ТЕЛОСЛОЖЕНИЕ", _constitution),
                DmUiKit.FieldBlock("ИНТЕЛЛЕКТ", _intelligence),
                DmUiKit.FieldBlock("МУДРОСТЬ", _wisdom),
                DmUiKit.FieldBlock("ХАРИЗМА", _charisma)));

            _abilityMods = new WrapPanel();
            _form.Children.Add(_abilityMods);

            _saves = DmUiKit.Field(_monster.SavingThrows, 300, "Например: Тел +5, Мдр +3");
            _skills = DmUiKit.Field(_monster.Skills, 300, "Например: Восприятие +5, Скрытность +4");
            _form.Children.Add(Wrap(
                DmUiKit.FieldBlock("СПАСБРОСКИ", _saves),
                DmUiKit.FieldBlock("НАВЫКИ", _skills)));

            // ── УЯЗВИМОСТИ И ИММУНИТЕТЫ ──
            string damageHint = "Типы урона: " + string.Join(", ", DiceFormulaParser.AllDamageWords());
            _form.Children.Add(DmUiKit.SectionPanel("УЯЗВИМОСТИ И ИММУНИТЕТЫ",
                "перечисляйте через запятую · " + damageHint + " · состояния: " + string.Join(", ", Conditions)));

            _vulnerabilities = DmUiKit.Field(_monster.DamageVulnerabilities, 320, damageHint);
            _resistances = DmUiKit.Field(_monster.DamageResistances, 320, damageHint);
            _immunities = DmUiKit.Field(_monster.DamageImmunities, 320, damageHint);
            _conditionImmunities = DmUiKit.Field(_monster.ConditionImmunities, 320,
                "Например: паралич, очарование, отравление");

            _form.Children.Add(Wrap(
                DmUiKit.FieldBlock("УЯЗВИМОСТЬ К УРОНУ", _vulnerabilities),
                DmUiKit.FieldBlock("СОПРОТИВЛЕНИЕ УРОНУ", _resistances)));
            _form.Children.Add(Wrap(
                DmUiKit.FieldBlock("ИММУНИТЕТ К УРОНУ", _immunities),
                DmUiKit.FieldBlock("ИММУНИТЕТ К СОСТОЯНИЯМ", _conditionImmunities)));

            // ── ЧУВСТВА, ЯЗЫКИ, ОПАСНОСТЬ ──
            _form.Children.Add(DmUiKit.SectionPanel("ЧУВСТВА, ЯЗЫКИ, ОПАСНОСТЬ"));

            _senses = DmUiKit.Field(_monster.Senses, 330,
                "Например: тёмное зрение 60 футов, пассивное Восприятие 9");
            _languages = DmUiKit.Field(_monster.Languages, 330,
                "Подсказка: " + string.Join(", ", Languages));

            _challenge = DmUiKit.Combo(ChallengeRatings, _monster.ChallengeRating, true, 110);
            _experience = DmUiKit.Field(_monster.Experience.ToString(), 110);
            _proficiency = DmUiKit.Field(_monster.ProficiencyBonus.ToString(), 90);

            var recalc = DmUiKit.Action("Пересчитать", (s, e) => RecalculateChallenge(), DmUiKit.ButtonKind.Ghost,
                "Взять опыт и бонус мастерства из опасности", 120, 28);

            _form.Children.Add(Wrap(
                DmUiKit.FieldBlock("ЧУВСТВА", _senses),
                DmUiKit.FieldBlock("ЯЗЫКИ", _languages)));

            var crBlock = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
            crBlock.Children.Add(DmUiKit.Label("ОПАСНОСТЬ (ПО)"));
            var crRow = new StackPanel { Orientation = Orientation.Horizontal };
            crRow.Children.Add(_challenge);
            crRow.Children.Add(recalc);
            crBlock.Children.Add(crRow);

            _form.Children.Add(Wrap(
                crBlock,
                DmUiKit.FieldBlock("ОПЫТ", _experience),
                DmUiKit.FieldBlock("БОНУС МАСТЕРСТВА", _proficiency),
                DmUiKit.FieldBlock("СРЕДА ОБИТАНИЯ", DmUiKit.Field(_monster.Habitat, 200, "лес, луг, подземье"),
                    null, 210),
                DmUiKit.FieldBlock("СОКРОВИЩА", DmUiKit.Field(_monster.Treasure, 160), null, 170)));

            // ── ЧЕРТЫ ──
            _form.Children.Add(DmUiKit.SectionPanel("ЧЕРТЫ И ОСОБЕННОСТИ",
                "текст тоже живой: «1к6 огнём» можно бросить нажатием"));
            _form.Children.Add(_traitsHost);
            _form.Children.Add(DmUiKit.Action("＋ Добавить черту", (s, e) =>
            {
                CollectFields();
                _monster.Traits.Add(new MonsterTraitData());
                RebuildTraits();
                RefreshPreview();
            }, DmUiKit.ButtonKind.Ghost, null, 180, 28));
            RebuildTraits();

            // ── ДЕЙСТВИЯ ──
            _form.Children.Add(DmUiKit.SectionPanel("ДЕЙСТВИЯ, РЕАКЦИИ, ЛЕГЕНДАРНОЕ",
                "попадание вида «+11» бросает d20+11, а урон — свои кубики"));
            _form.Children.Add(_actionsHost);

            var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var section in ActionSections.All)
            {
                string key = section.Key;
                actionButtons.Children.Add(DmUiKit.Action("＋ " + section.Title, (s, e) =>
                {
                    CollectFields();
                    _monster.Actions.Add(new MonsterActionData { Section = key });
                    RebuildActions();
                    RefreshPreview();
                }, DmUiKit.ButtonKind.Ghost, null, double.NaN, 28));
            }
            _form.Children.Add(actionButtons);
            RebuildActions();

            // ── ТЕКСТОВЫЕ БЛОКИ ──
            _form.Children.Add(DmUiKit.SectionPanel("ЛЕГЕНДАРНОЕ, ЛОГОВО, ЗАКЛИНАНИЯ"));
            _legendaryIntro = DmUiKit.Field(_monster.LegendaryIntro, double.NaN, null, true, 54);
            _lairText = DmUiKit.Field(_monster.LairText, double.NaN, null, true, 54);
            _spells = DmUiKit.Field(_monster.Spells, double.NaN, "Сл спасброска, врождённое колдовство, ячейки", true, 70);
            _equipment = DmUiKit.Field(_monster.Equipment, double.NaN, null, true, 54);
            _description = DmUiKit.Field(_monster.Description, double.NaN, null, true, 110);

            _form.Children.Add(DmUiKit.FieldBlock("ВВОДНЫЙ ТЕКСТ ЛЕГЕНДАРНЫХ ДЕЙСТВИЙ", _legendaryIntro));
            _form.Children.Add(DmUiKit.FieldBlock("ЛОГОВО", _lairText));
            _form.Children.Add(DmUiKit.FieldBlock("ЗАКЛИНАНИЯ И ПСИОНИКА", _spells));
            _form.Children.Add(DmUiKit.FieldBlock("СНАРЯЖЕНИЕ", _equipment));
            _form.Children.Add(DmUiKit.FieldBlock("ОПИСАНИЕ И ВНЕШНОСТЬ", _description));

            foreach (TextBox box in new[] { _spells, _description, _lairText, _legendaryIntro, _equipment })
            {
                box.Width = double.NaN;
                box.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            RefreshAbilityMods();
        }

        private TextBox ScoreField(int value)
        {
            var box = DmUiKit.Field(value.ToString(), 80);
            box.TextChanged += (s, e) => RefreshAbilityMods();
            return box;
        }

        private static WrapPanel Wrap(params UIElement[] children)
        {
            var panel = new WrapPanel();
            foreach (var child in children) if (child != null) panel.Children.Add(child);
            return panel;
        }

        // ══════════════════════ ЧЕРТЫ И ДЕЙСТВИЯ ══════════════════════

        private void RebuildTraits()
        {
            _traitsHost.Children.Clear();

            if (_monster.Traits.Count == 0)
            {
                _traitsHost.Children.Add(DmUiKit.Hint("Черт пока нет. Например: «Тактика стаи» или «Легендарное сопротивление (3/день)»."));
                return;
            }

            for (int i = 0; i < _monster.Traits.Count; i++)
            {
                var trait = _monster.Traits[i];
                var panel = new StackPanel();

                var head = new StackPanel { Orientation = Orientation.Horizontal };
                var name = DmUiKit.Field(trait.Name, 260, "Название черты");
                name.Margin = new Thickness(0, 0, 8, 0);
                name.TextChanged += (s, e) => { trait.Name = name.Text; };
                head.Children.Add(name);

                int index = i;
                head.Children.Add(DmUiKit.Icon("✕", (s, e) =>
                {
                    CollectFields();
                    if (index >= 0 && index < _monster.Traits.Count) _monster.Traits.RemoveAt(index);
                    RebuildTraits();
                    RefreshPreview();
                }, "Удалить черту", 26, DmUiKit.ButtonKind.Soft));

                panel.Children.Add(head);

                var text = DmUiKit.Field(trait.Text, double.NaN, null, true, 54);
                text.Margin = new Thickness(0, 6, 0, 0);
                text.TextChanged += (s, e) => { trait.Text = text.Text; };
                panel.Children.Add(text);

                _traitsHost.Children.Add(DmUiKit.Panel(panel, new Thickness(10, 8, 10, 8), DmUiKit.B("Slate"),
                    DmUiKit.B("Line"), 6, new Thickness(0, 0, 0, 8)));
            }
        }

        private void RebuildActions()
        {
            _actionsHost.Children.Clear();

            if (_monster.Actions.Count == 0)
            {
                _actionsHost.Children.Add(DmUiKit.Hint(
                    "Действий пока нет. Нажмите «ДЕЙСТВИЯ» и заполните атаку: попадание «+4», " +
                    "урон «1к6+2 рубящим» — обе формулы будут бросаться нажатием."));
                return;
            }

            for (int i = 0; i < _monster.Actions.Count; i++)
            {
                _actionsHost.Children.Add(BuildActionRow(_monster.Actions[i], i));
            }
        }

        private UIElement BuildActionRow(MonsterActionData action, int index)
        {
            var panel = new StackPanel();

            // ── строка 1: раздел, название, перезарядка, стоимость, удаление ──
            var head = new WrapPanel();

            var sectionCombo = DmUiKit.ComboPairs(
                ActionSections.All.Select(a => new KeyValuePair<string, string>(a.Key, a.Title)),
                action.Section, 180);
            sectionCombo.Margin = new Thickness(0, 0, 8, 6);
            sectionCombo.SelectionChanged += (s, e) => { action.Section = DmUiKit.ComboKey(sectionCombo); };
            head.Children.Add(sectionCombo);

            var name = DmUiKit.Field(action.Name, 240, "Название: «Скимитар», «Огненное дыхание»");
            name.Margin = new Thickness(0, 0, 8, 6);
            name.TextChanged += (s, e) => { action.Name = name.Text; };
            head.Children.Add(name);

            var recharge = DmUiKit.Field(action.Recharge, 130, "Перезарядка: 5–6, 3/день");
            recharge.Margin = new Thickness(0, 0, 8, 6);
            recharge.TextChanged += (s, e) => { action.Recharge = recharge.Text; };
            head.Children.Add(recharge);

            var cost = DmUiKit.Field(action.LegendaryCost.ToString(), 60, "Стоимость в легендарных действиях");
            cost.Margin = new Thickness(0, 0, 8, 6);
            cost.TextChanged += (s, e) => { action.LegendaryCost = ParseInt(cost.Text, 1, 0, 9); };
            head.Children.Add(cost);

            int captured = index;
            head.Children.Add(DmUiKit.Icon("✕", (s, e) =>
            {
                CollectFields();
                if (captured >= 0 && captured < _monster.Actions.Count) _monster.Actions.RemoveAt(captured);
                RebuildActions();
                RefreshPreview();
            }, "Удалить действие", 26, DmUiKit.ButtonKind.Soft));

            panel.Children.Add(head);

            // ── строка 2: попадание, досягаемость, дистанция ──
            var line2 = new WrapPanel();
            var hit = DmUiKit.Field(action.HitBonus, 110, "Попадание: +11 или «11 к попаданию»");
            hit.Margin = new Thickness(0, 0, 8, 6);
            hit.TextChanged += (s, e) => { action.HitBonus = hit.Text; };
            line2.Children.Add(hit);

            var kind = DmUiKit.Field(action.Kind, 220, "Рукопашная атака оружием / Дальнобойная атака / Заклинание");
            kind.Margin = new Thickness(0, 0, 8, 6);
            kind.TextChanged += (s, e) => { action.Kind = kind.Text; };
            line2.Children.Add(kind);

            var reach = DmUiKit.Field(action.Reach, 120, "Досягаемость: 5 футов");
            reach.Margin = new Thickness(0, 0, 8, 6);
            reach.TextChanged += (s, e) => { action.Reach = reach.Text; };
            line2.Children.Add(reach);

            var range = DmUiKit.Field(action.Range, 150, "Дистанция: 80/320 футов");
            range.Margin = new Thickness(0, 0, 8, 6);
            range.TextChanged += (s, e) => { action.Range = range.Text; };
            line2.Children.Add(range);

            panel.Children.Add(line2);

            // ── строка 3: урон и спасбросок ──
            var line3 = new WrapPanel();
            var damage = DmUiKit.Field(action.Damage, 340, "Урон: «2к8+6 рубящим + 3к10 психическим»");
            damage.Margin = new Thickness(0, 0, 8, 6);
            damage.TextChanged += (s, e) =>
            {
                action.Damage = damage.Text;
                RefreshActionPreview(action, panel);
            };
            line3.Children.Add(damage);

            var save = DmUiKit.Field(action.SaveDc, 260, "Спасбросок: Сл 17 Ловкости, половина при успехе");
            save.Margin = new Thickness(0, 0, 8, 6);
            save.TextChanged += (s, e) => { action.SaveDc = save.Text; };
            line3.Children.Add(save);
            panel.Children.Add(line3);

            // ── строка 4: описание ──
            var description = DmUiKit.Field(action.Description, double.NaN, "Если это не атака: текст действия", true, 48);
            description.Margin = new Thickness(0, 0, 0, 6);
            description.TextChanged += (s, e) => { action.Description = description.Text; };
            panel.Children.Add(description);

            // ── строка 5: живой предпросмотр с бросаемыми формулами ──
            var previewHost = new StackPanel();
            panel.Children.Add(previewHost);
            RefreshActionPreview(action, panel, previewHost);

            return DmUiKit.Panel(panel, new Thickness(10, 8, 10, 8), DmUiKit.B("Slate"), DmUiKit.B("Line"), 6,
                new Thickness(0, 0, 0, 8));
        }

        /// <summary>
        /// Живая строка: попадание и урон с подсвеченными формулами, которые
        /// бросаются прямо в форме.
        /// </summary>
        private void RefreshActionPreview(MonsterActionData action, Panel actionPanel, StackPanel explicitHost = null)
        {
            StackPanel host = explicitHost ?? actionPanel.Children.OfType<StackPanel>().LastOrDefault();
            if (host == null) return;

            host.Children.Clear();

            var style = new RollTextStyle
            {
                Text = DmUiKit.B("Ink"),
                Dice = DmUiKit.B("Gold"),
                Hint = DmUiKit.B("Mute"),
                Font = DmUiKit.Body,
                FontSize = 11.5
            };

            string who = _name != null ? _name.Text : _monster.Name;

            if (DiceFormulaParser.TryParseHitBonus(action.HitBonus, out int bonus))
            {
                var hitButton = DmUiKit.Action("🎲 Атака", (s, e) => DmRolls.RollAttack(this, bonus, who, action.Name),
                    DmUiKit.ButtonKind.Ghost, null, 90, 24);
                hitButton.Margin = new Thickness(0, 0, 8, 4);

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(hitButton);
                var hitLink = RollTextRenderer.BuildHitLine(action.HitBonus, action.Damage,
                    b => DmRolls.RollAttack(this, b, who, action.Name), style);
                if (hitLink != null)
                {
                    hitLink.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(hitLink);
                }
                host.Children.Add(row);
            }

            var damageParts = DiceFormulaParser.ParseDamages(action.Damage);
            if (damageParts.Count > 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var rollButton = DmUiKit.Action("🎲 Урон", (s, e) =>
                {
                    foreach (var part in damageParts)
                        DmRolls.RollDamage(this, part.Formula, part.Damage, who, action.Name);
                }, DmUiKit.ButtonKind.Ghost, null, 90, 24);
                rollButton.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(rollButton);

                row.Children.Add(RollTextRenderer.Build(action.Damage,
                    part => DmRolls.RollDamage(this, part.Formula, part.Damage, who, action.Name), style));
                host.Children.Add(row);

                int average = damageParts.Sum(p => p.AverageDamage);
                if (average > 0)
                    host.Children.Add(DmUiKit.Hint($"Средний урон: {average}"));
            }
        }

        // ══════════════════════ ЗНАЧЕНИЯ ══════════════════════

        private void RefreshAbilityMods()
        {
            if (_abilityMods == null) return;
            _abilityMods.Children.Clear();

            void Add(string label, TextBox box)
            {
                int score = ParseInt(box?.Text, 10, 0, 99);
                int mod = MonsterData.ModifierOf(score);
                _abilityMods.Children.Add(DmUiKit.Chip($"{label} {(mod >= 0 ? "+" : "")}{mod}", DmUiKit.B("Arcane")));
            }

            Add("СИЛ", _strength);
            Add("ЛОВ", _dexterity);
            Add("ТЕЛ", _constitution);
            Add("ИНТ", _intelligence);
            Add("МДР", _wisdom);
            Add("ХАР", _charisma);
        }

        private void RecalculateChallenge()
        {
            string cr = _challenge.Text ?? "";
            if (!DiceFormulaParser.TryParseChallenge(cr, out double rating, out int xp))
            {
                _validationText.Text = "Не понял опасность. Пишите как в книге: 1/4, 1/2, 5 или 10.";
                return;
            }

            _experience.Text = xp.ToString();
            _proficiency.Text = DiceFormulaParser.ProficiencyForRating(rating).ToString();
            _validationText.Text = "";
        }

        private void RollHitPoints()
        {
            if (!DiceFormulaParser.TryParseWholeFormula(_hpFormula.Text, out string normalized,
                    out int count, out int sides, out int modifier))
            {
                DmRolls.Toast(this, "Формула хитов непонятна. Пример: 2к6 или 18к8+54", true);
                return;
            }

            var result = DmRolls.Roll(this, normalized, "Хиты", _name?.Text ?? _monster.Name, "Бросок хитов", true, false);
            if (result == null) return;

            if (result.Total > 0)
            {
                _hitPoints.Text = result.Total.ToString();
                _monster.HitPoints = result.Total;
            }
        }

        private void PickPhoto()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Картинка существа",
                Filter = "Картинки|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Все файлы|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            _pendingPhotoPath = dialog.FileName;
            _removePhoto = false;
            UpdatePhotoText();
            RefreshPreview();
        }

        private void UpdatePhotoText()
        {
            if (_removePhoto)
            {
                _photoText.Text = "картинка будет убрана";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_pendingPhotoPath))
            {
                _photoText.Text = "выбрано: " + System.IO.Path.GetFileName(_pendingPhotoPath);
                return;
            }

            _photoText.Text = string.IsNullOrWhiteSpace(_monster.PhotoPath)
                ? "картинки нет"
                : "картинка есть";
        }

        /// <summary>Переносит значения полей в объект существа.</summary>
        private void CollectFields()
        {
            if (_name == null) return;

            _monster.Name = (_name.Text ?? "").Trim();
            _monster.NameEn = (_nameEn.Text ?? "").Trim();
            _monster.Size = (_size.Text ?? "").Trim();
            _monster.CreatureType = (_creatureType.Text ?? "").Trim();
            _monster.Subtype = (_subtype.Text ?? "").Trim();
            _monster.Alignment = (_alignment.Text ?? "").Trim();
            _monster.Source = (_source.Text ?? "").Trim();
            _monster.Tags = (_tags.Text ?? "").Trim();

            _monster.ArmorClass = ParseInt(_armorClass.Text, 10, 0, 99);
            _monster.ArmorClassNote = (_acNote.Text ?? "").Trim();
            _monster.HitPoints = ParseInt(_hitPoints.Text, 1, 0, 99999);
            _monster.HitPointsFormula = (_hpFormula.Text ?? "").Trim();

            _monster.SpeedWalk = ParseInt(_speedWalk.Text, 30, 0, 9999);
            _monster.SpeedFly = ParseInt(_speedFly.Text, 0, 0, 9999);
            _monster.SpeedSwim = ParseInt(_speedSwim.Text, 0, 0, 9999);
            _monster.SpeedClimb = ParseInt(_speedClimb.Text, 0, 0, 9999);
            _monster.SpeedBurrow = ParseInt(_speedBurrow.Text, 0, 0, 9999);
            _monster.SpeedNote = (_speedNote.Text ?? "").Trim();
            _monster.Hover = _hover.IsChecked == true;

            _monster.Strength = ParseInt(_strength.Text, 10, 0, 99);
            _monster.Dexterity = ParseInt(_dexterity.Text, 10, 0, 99);
            _monster.Constitution = ParseInt(_constitution.Text, 10, 0, 99);
            _monster.Intelligence = ParseInt(_intelligence.Text, 10, 0, 99);
            _monster.Wisdom = ParseInt(_wisdom.Text, 10, 0, 99);
            _monster.Charisma = ParseInt(_charisma.Text, 10, 0, 99);

            _monster.SavingThrows = (_saves.Text ?? "").Trim();
            _monster.Skills = (_skills.Text ?? "").Trim();

            _monster.DamageVulnerabilities = (_vulnerabilities.Text ?? "").Trim();
            _monster.DamageResistances = (_resistances.Text ?? "").Trim();
            _monster.DamageImmunities = (_immunities.Text ?? "").Trim();
            _monster.ConditionImmunities = (_conditionImmunities.Text ?? "").Trim();

            _monster.Senses = (_senses.Text ?? "").Trim();
            _monster.Languages = (_languages.Text ?? "").Trim();
            _monster.ChallengeRating = (_challenge.Text ?? "0").Trim();
            _monster.Experience = ParseInt(_experience.Text, 10, 0, 999999);
            _monster.ProficiencyBonus = ParseInt(_proficiency.Text, 2, 0, 12);

            _monster.LegendaryIntro = (_legendaryIntro.Text ?? "").Trim();
            _monster.LairText = (_lairText.Text ?? "").Trim();
            _monster.Spells = (_spells.Text ?? "").Trim();
            _monster.Equipment = (_equipment.Text ?? "").Trim();
            _monster.Description = (_description.Text ?? "").Trim();

            RefreshAbilityMods();
        }

        private static int ParseInt(string text, int fallback, int min, int max)
        {
            if (!int.TryParse((text ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
            {
                // Разрешаем запись вида «+2» и «30 футов»
                string digits = new string((text ?? "").Where(c => char.IsDigit(c) || c == '-').ToArray());
                if (!int.TryParse(digits, out value)) value = fallback;
            }

            if (value < min) value = min;
            if (value > max) value = max;
            return value;
        }

        // ══════════════════════ СОХРАНЕНИЕ ══════════════════════

        private void SaveCurrent()
        {
            CollectFields();

            if (!_monster.Validate(out string error))
            {
                _validationText.Text = error;
                DmRolls.Toast(this, error, true);
                return;
            }

            if (_monster.Experience <= 0)
            {
                DiceFormulaParser.TryParseChallenge(_monster.ChallengeRating, out _, out int xp);
                if (xp > 0) _monster.Experience = xp;
            }

            bool wasNew = _monster.Id <= 0 || _isNew;
            int id = BestiaryService.Save(_monster);
            if (id <= 0)
            {
                _validationText.Text = "Не удалось сохранить существо. Проверьте базу данных.";
                return;
            }

            if (_removePhoto && !string.IsNullOrWhiteSpace(_monster.PhotoPath))
                BestiaryService.RemovePhoto(_monster);
            else if (!string.IsNullOrWhiteSpace(_pendingPhotoPath))
                BestiaryService.AttachPhoto(_monster, _pendingPhotoPath);

            _validationText.Text = "";
            DmRolls.Toast(this, wasNew ? $"Существо «{_monster.Name}» добавлено" : $"Существо «{_monster.Name}» сохранено");
            Saved?.Invoke(_monster);
        }

        private void Cancel()
        {
            _validationText.Text = "";
            Cancelled?.Invoke();
        }

        // ══════════════════════ ПРЕДПРОСМОТР ══════════════════════

        /// <summary>Перерисовывает карточку по текущим значениям формы.</summary>
        public void RefreshPreview()
        {
            try
            {
                CollectFields();
                _preview.Children.Clear();
                _preview.Children.Add(MonsterCardRenderer.Build(_monster, this));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MonsterEditor] предпросмотр: {ex.Message}");
            }
        }
    }
}
