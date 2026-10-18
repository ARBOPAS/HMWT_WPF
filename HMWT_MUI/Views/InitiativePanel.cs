using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Трекер инициативы: порядок хода, броски, раунды.
    ///
    /// Один и тот же контрол показывает бой и во вкладке «Инструменты ДМа»,
    /// и в окошке из подвала окна персонажа: мастер не должен смотреть на бой
    /// в двух разных видах.
    ///
    /// Игрокам уходит только имя и результат броска — кости бросает ведущий.
    /// </summary>
    public sealed class InitiativePanel : UserControl
    {
        private readonly bool _compact;
        private readonly StackPanel _orderHost = new StackPanel();
        private readonly WrapPanel _headerChips = new WrapPanel();
        private readonly TextBlock _emptyHint = DmUiKit.Hint("");
        private readonly TextBlock _groupName = DmUiKit.Value("", 15, DmUiKit.B("Gold"), true);
        private readonly WrapPanel _actionsRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        private readonly WrapPanel _turnRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

        private EncounterData _encounter;

        /// <summary>Нажали на имя жетона, у которого есть существо в бестиарии.</summary>
        public event Action<int> OpenMonsterRequested;

        /// <summary>Показать весь трекер (кнопка в компактном виде).</summary>
        public event Action OpenTrackerRequested;

        /// <summary>Состояние боя изменилось — значок в подвале окна нужно обновить.</summary>
        public event Action Changed;

        public InitiativePanel(bool compact)
        {
            _compact = compact;
            Build();
            Loaded += (s, e) => Refresh();
        }

        public EncounterData Encounter => _encounter;

        // ══════════════════════ СБОРКА ══════════════════════

        private void Build()
        {
            var root = new StackPanel();

            // ── шапка: название группы и состояние ──
            // Подписи состояния идут отдельной строкой и переносятся: в окошке
            // из подвала (480 точек) четыре плашки в одну строку не влезают
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            _groupName.Margin = new Thickness(0, 0, 0, 4);
            head.Children.Add(_groupName);
            head.Children.Add(_headerChips);
            root.Children.Add(head);

            // ── кнопки ведения боя ──
            root.Children.Add(_turnRow);
            root.Children.Add(_actionsRow);

            root.Children.Add(_emptyHint);
            root.Children.Add(DmUiKit.Scroll(_orderHost));

            Content = root;
            BuildButtons();
        }

        private void BuildButtons()
        {
            _turnRow.Children.Clear();
            _actionsRow.Children.Clear();

            // В окошке из подвала места мало — подписи короче
            string rollAll = _compact ? "Всем" : "Бросить всем";
            string rollNew = _compact ? "Новым" : "Не бросившим";
            string clear = _compact ? "Сброс" : "Сбросить броски";
            string end = _compact ? "Конец боя" : "Закончить бой";
            string next = _compact ? "Дальше" : "Следующий ход";

            AddButton(_turnRow, "Назад", (s, e) => Step(-1), DmUiKit.ButtonKind.Ghost,
                "Вернуть ход предыдущему", _compact ? 74 : 90, "arrowLeft");
            AddButton(_turnRow, next, (s, e) => Step(1), DmUiKit.ButtonKind.Primary,
                "Передать ход следующему; после последнего начинается новый раунд", _compact ? 108 : 160, "arrowRight");
            AddButton(_turnRow, "＋ Раунд", (s, e) => AddRound(), DmUiKit.ButtonKind.Ghost,
                "Начать следующий раунд вручную", _compact ? 82 : 90);

            AddButton(_actionsRow, rollAll, (s, e) => RollForAll(false), DmUiKit.ButtonKind.Primary,
                "Бросить инициативу всем жетонам заново", _compact ? 86 : 150, "dice");
            AddButton(_actionsRow, rollNew, (s, e) => RollForAll(true), DmUiKit.ButtonKind.Ghost,
                "Бросить только тем, у кого инициативы ещё нет", _compact ? 92 : 150, "dice");
            AddButton(_actionsRow, clear, (s, e) => ClearRolls(), DmUiKit.ButtonKind.Ghost,
                "Убрать все результаты — перед новой стычкой", _compact ? 72 : 150);
            AddButton(_actionsRow, end, (s, e) => EndCombat(), DmUiKit.ButtonKind.Danger,
                "Бой завершён: порядок хода снимается", _compact ? 92 : 150);

            if (_compact)
            {
                AddButton(_actionsRow, "Открыть трекер", (s, e) => OpenTrackerRequested?.Invoke(),
                    DmUiKit.ButtonKind.Ghost, "Перейти во вкладку «Инструменты ДМа»", 130);
            }
        }

        private static void AddButton(Panel host, string text, RoutedEventHandler handler,
            DmUiKit.ButtonKind kind, string tooltip, double width, string icon = null)
        {
            var button = DmUiKit.Action(text, handler, kind, tooltip, width, 28, icon);
            button.Margin = new Thickness(0, 0, 6, 6);
            host.Children.Add(button);
        }

        // ══════════════════════ ДАННЫЕ ══════════════════════

        /// <summary>Показывает бой. Передаётся уже загруженная группа.</summary>
        public void Show(EncounterData encounter)
        {
            _encounter = encounter ?? InitiativeService.EnsureWorkingEncounter();
            Render();
        }

        /// <summary>Перечитывает активный бой из базы и перерисовывает.</summary>
        public void Refresh()
        {
            if (_encounter == null)
            {
                _encounter = InitiativeService.EnsureWorkingEncounter();
            }
            else
            {
                // Группу могли изменить в другом месте (например, во вкладке) —
                // берём свежую версию из базы
                var fresh = InitiativeService.LoadEncounter(_encounter.Id);
                if (fresh != null) _encounter = fresh;
            }

            Render();
        }

        private void Render()
        {
            if (_encounter == null)
            {
                _groupName.Text = "БОЙ НЕ СОЗДАН";
                _headerChips.Children.Clear();
                _orderHost.Children.Clear();
                _emptyHint.Text = "Создайте группу боя во вкладке «Инструменты ДМа».";
                return;
            }

            _groupName.Text = (_encounter.Name ?? "Бой").ToUpperInvariant();

            _headerChips.Children.Clear();
            _headerChips.Children.Add(DmUiKit.Chip($"РАУНД {Math.Max(1, _encounter.Round)}", DmUiKit.B("Arcane")));
            _headerChips.Children.Add(_encounter.IsActive
                ? DmUiKit.Chip("БОЙ ИДЁТ", DmUiKit.B("Ok"))
                : DmUiKit.Chip("БОЙ НЕ ИДЁТ", DmUiKit.B("Mute")));
            _headerChips.Children.Add(DmUiKit.Chip($"ЖЕТОНОВ {_encounter.Combatants.Count}", DmUiKit.B("Frost")));

            var current = _encounter.Current();
            if (_encounter.IsActive && current != null)
                _headerChips.Children.Add(DmUiKit.Chip($"ХОД: {current.Name}".ToUpperInvariant(), DmUiKit.B("Gold"), true));

            bool hasRolls = _encounter.HasAnyRoll;
            bool canManage = DmRights.CanUseDmTools();

            SetEnabled(_turnRow, canManage && _encounter.Combatants.Count > 0);
            SetEnabled(_actionsRow, canManage && _encounter.Combatants.Count > 0);

            _emptyHint.Text = !canManage
                ? DmRights.DenyReason()
                : _encounter.Combatants.Count == 0
                    ? "Жетонов пока нет. Добавьте существ из бестиария или впишите имя вручную."
                    : !hasRolls
                        ? "Инициатива не брошена. Нажмите «Бросить всем» — порядок выстроится сам."
                        : "";

            RenderOrder();
        }

        private static void SetEnabled(Panel panel, bool enabled)
        {
            foreach (var child in panel.Children.OfType<Button>()) child.IsEnabled = enabled;
        }

        private void RenderOrder()
        {
            _orderHost.Children.Clear();

            var ordered = _encounter.Ordered();
            var current = _encounter.Current();

            for (int i = 0; i < ordered.Count; i++)
            {
                var combatant = ordered[i];
                bool isCurrent = _encounter.IsActive && current != null && current.Id == combatant.Id;
                _orderHost.Children.Add(BuildCombatantCard(combatant, i + 1, isCurrent));
            }
        }

        private UIElement BuildCombatantCard(CombatantData combatant, int position, bool isCurrent)
        {
            bool canManage = DmRights.CanUseDmTools();

            var card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ── номер по порядку ──
            var number = new TextBlock
            {
                Text = position.ToString(),
                FontFamily = DmUiKit.Display,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = isCurrent ? DmUiKit.B("Gold") : DmUiKit.B("Mute"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(number, 0);
            card.Children.Add(number);

            // ── имя, модификатор, результат ──
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            var nameText = new TextBlock
            {
                Text = combatant.Name,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Ink"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            if (combatant.MonsterId > 0)
            {
                // Жетон из бестиария: имя ведёт на карточку существа.
                // Существ мог удалить другой заход — тогда переход просто ничего не сделает.
                var link = new Button
                {
                    Content = nameText,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Открыть существо в бестиарии"
                };
                link.Click += (s, e) => OpenMonsterRequested?.Invoke(combatant.MonsterId);
                nameRow.Children.Add(link);
            }
            else
            {
                nameRow.Children.Add(nameText);
            }
            // Плашки идут сразу за именем: у самой плашки отступ только справа,
            // поэтому слева его задаём руками — иначе «Гоблин» и «БЕСТИАРИЙ»
            // слипались в одно слово
            if (combatant.Kind == CombatantKind.Player)
            {
                var playerChip = DmUiKit.Chip("ИГРОК", DmUiKit.B("Frost"));
                playerChip.Margin = new Thickness(6, 0, 6, 0);
                nameRow.Children.Add(playerChip);
            }

            if (combatant.MonsterId > 0)
            {
                var sourceChip = DmUiKit.Chip("БЕСТИАРИЙ", DmUiKit.B("Arcane"));
                sourceChip.Margin = new Thickness(6, 0, 6, 0);
                nameRow.Children.Add(sourceChip);
            }
            info.Children.Add(nameRow);

            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            meta.Children.Add(new TextBlock
            {
                Text = $"мод. {combatant.ModifierLine()}",
                FontSize = 10.5,
                Foreground = DmUiKit.B("Mute"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            meta.Children.Add(new TextBlock
            {
                Text = combatant.HasRolled ? $"итог {combatant.InitiativeTotal}" : "инициатива не брошена",
                FontSize = 11.5,
                FontWeight = combatant.HasRolled ? FontWeights.Bold : FontWeights.Normal,
                Foreground = combatant.HasRolled ? DmUiKit.B("Gold") : DmUiKit.B("Mute"),
                VerticalAlignment = VerticalAlignment.Center
            });
            if (combatant.HasRolled)
            {
                meta.Children.Add(new TextBlock
                {
                    Text = $"  (кубик {combatant.InitiativeRoll})",
                    FontSize = 10,
                    Foreground = DmUiKit.B("Mute"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            info.Children.Add(meta);

            // ── хиты: бросок по формуле существа и правка в бою ──
            info.Children.Add(BuildHitPointRow(combatant, canManage));

            // ── состояния: плашки + добавление ──
            var conditions = BuildConditionRow(combatant, canManage);
            if (conditions != null) info.Children.Add(conditions);

            Grid.SetColumn(info, 1);
            card.Children.Add(info);

            // ── управление жетоном ──
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (canManage)
            {
                controls.Children.Add(DmUiKit.Icon("🎲", (s, e) => RollSingle(combatant), "Бросить инициативу этому жетону", 28));
                controls.Children.Add(DmUiKit.Icon("−", (s, e) => ShiftModifier(combatant, -1), "Уменьшить модификатор", 26, DmUiKit.ButtonKind.Soft));
                controls.Children.Add(new TextBlock
                {
                    Text = combatant.ModifierLine(),
                    FontSize = 11,
                    Width = 30,
                    TextAlignment = TextAlignment.Center,
                    Foreground = DmUiKit.B("Ink"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                controls.Children.Add(DmUiKit.Icon("+", (s, e) => ShiftModifier(combatant, 1), "Увеличить модификатор", 26, DmUiKit.ButtonKind.Soft));
                controls.Children.Add(DmUiKit.Icon("✕", (s, e) => RemoveCombatant(combatant), "Убрать жетон из боя", 26, DmUiKit.ButtonKind.Soft));
            }

            Grid.SetColumn(controls, 2);
            card.Children.Add(controls);

            return DmUiKit.Panel(card, new Thickness(12, 8, 10, 8),
                isCurrent ? DmUiKit.Fade(DmUiKit.B("Gold"), 0x22) : DmUiKit.B("Panel"),
                isCurrent ? DmUiKit.B("Gold") : DmUiKit.B("Line"), 8,
                new Thickness(0, 0, 0, 6));
        }

        /// <summary>
        /// Строка хитов: «ХИТЫ 13 / 13», кубик (бросает формулу существа),
        /// минус и плюс для правки в бою.
        /// </summary>
        private UIElement BuildHitPointRow(CombatantData combatant, bool canManage)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

            bool hasHp = combatant.MaxHitPoints > 0;
            string text;
            Brush color;

            if (!hasHp)
            {
                text = "хиты не заданы";
                color = DmUiKit.B("Mute");
            }
            else
            {
                text = $"ХИТЫ {combatant.HitPoints} / {combatant.MaxHitPoints}";
                color = combatant.HitPoints <= 0
                    ? DmUiKit.B("Danger")
                    : combatant.HitPoints * 3 <= combatant.MaxHitPoints
                        ? DmUiKit.B("Danger")
                        : combatant.HitPoints < combatant.MaxHitPoints
                            ? DmUiKit.B("Gold")
                            : DmUiKit.B("Ok");
            }

            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = color,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            if (canManage)
            {
                row.Children.Add(DmUiKit.Icon("🎲", (s, e) => RollHitPoints(combatant),
                    "Бросить хиты по формуле существа", 26));
                row.Children.Add(DmUiKit.Icon("−", (s, e) => ShiftHitPoints(combatant, -1),
                    "Убрать 1 хит", 24, DmUiKit.ButtonKind.Soft));
                row.Children.Add(DmUiKit.Icon("+", (s, e) => ShiftHitPoints(combatant, 1),
                    "Добавить 1 хит", 24, DmUiKit.ButtonKind.Soft));
            }

            return row;
        }

        /// <summary>Плашки состояний и кнопка «+» с меню выбора.</summary>
        private UIElement BuildConditionRow(CombatantData combatant, bool canManage)
        {
            var keys = ParseConditions(combatant.Conditions);
            if (keys.Count == 0 && !canManage) return null;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

            foreach (string key in keys)
            {
                var chip = DmUiKit.Chip(ConditionName(key), DmUiKit.B("Danger"));
                chip.Margin = new Thickness(0, 0, 5, 0);
                chip.ToolTip = "Состояние: " + ConditionName(key);
                if (canManage)
                {
                    chip.Cursor = Cursors.Hand;
                    string captured = key;
                    chip.MouseLeftButtonUp += (s, e) => ToggleCondition(combatant, captured);
                }
                row.Children.Add(chip);
            }

            if (canManage)
            {
                var add = DmUiKit.Icon("+", null, "Добавить состояние", 24, DmUiKit.ButtonKind.Soft);
                var menu = new ContextMenu();
                var all = CharacterStateService.LoadConditions();
                foreach (var option in all)
                {
                    string key = option.Key;
                    bool active = keys.Contains(key);
                    var item = new MenuItem
                    {
                        Header = (active ? "✓ " : "") + option.Name,
                        IsCheckable = false
                    };
                    item.Click += (s, e) => ToggleCondition(combatant, key);
                    menu.Items.Add(item);
                }
                add.Click += (s, e) =>
                {
                    menu.PlacementTarget = add;
                    menu.IsOpen = true;
                };
                row.Children.Add(add);
            }

            return row;
        }

        private static List<string> ParseConditions(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;

            foreach (string part in text.Split(','))
            {
                string key = (part ?? "").Trim();
                if (key.Length > 0 && !list.Contains(key)) list.Add(key);
            }
            return list;
        }

        private static string ConditionName(string key)
        {
            try
            {
                var match = CharacterStateService.LoadConditions()
                    .FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrWhiteSpace(match.Name)) return match.Name;
            }
            catch { }
            return key;
        }

        /// <summary>Включает или снимает состояние у участника боя.</summary>
        private void ToggleCondition(CombatantData combatant, string key)
        {
            if (combatant == null || _encounter == null || string.IsNullOrWhiteSpace(key)) return;

            var keys = ParseConditions(combatant.Conditions);
            if (keys.Remove(key)) { }
            else keys.Add(key);

            combatant.Conditions = string.Join(",", keys);
            InitiativeService.SaveCombatant(_encounter, combatant);
            Publish();
            Render();
            Changed?.Invoke();
        }

        /// <summary>Бросает хиты по формуле существа из бестиария.</summary>
        private void RollHitPoints(CombatantData combatant)
        {
            if (combatant == null || _encounter == null || !DmRights.CanUseDmTools()) return;

            var monster = combatant.MonsterId > 0 ? BestiaryService.Load(combatant.MonsterId) : null;
            string formula = monster?.HitPointsFormula;
            if (string.IsNullOrWhiteSpace(formula))
            {
                DmRolls.Toast(this, "У существа не задана формула хитов", true);
                return;
            }

            if (!DiceFormulaParser.TryParseWholeFormula(formula, out string normalized, out _, out _, out _))
            {
                DmRolls.Toast(this, "Формула хитов не разобралась: " + formula, true);
                return;
            }

            var result = DmRolls.Roll(this, normalized, "Хиты", combatant.Name,
                $"Хиты · {combatant.Name}", showPanel: true, publish: true);
            if (result == null) return;

            int hp = Math.Max(1, result.Total);
            combatant.MaxHitPoints = hp;
            combatant.HitPoints = hp;

            InitiativeService.SaveCombatant(_encounter, combatant);
            Publish();
            Render();
            Changed?.Invoke();
        }

        /// <summary>Правка хитов в бою: не ниже нуля и не выше максимума.</summary>
        private void ShiftHitPoints(CombatantData combatant, int delta)
        {
            if (combatant == null || _encounter == null || !DmRights.CanUseDmTools()) return;

            if (combatant.MaxHitPoints <= 0)
            {
                DmRolls.Toast(this, "Сначала бросьте хиты (🎲) или задайте максимум", true);
                return;
            }

            combatant.HitPoints = Math.Max(0, Math.Min(combatant.MaxHitPoints, combatant.HitPoints + delta));
            InitiativeService.SaveCombatant(_encounter, combatant);
            Publish();
            Render();
            Changed?.Invoke();
        }
        // ══════════════════════ ДЕЙСТВИЯ ══════════════════════

        private void RollSingle(CombatantData combatant)
        {
            if (_encounter == null || combatant == null || !DmRights.CanUseDmTools()) return;

            int diceRoll = RollD20(combatant.InitiativeModifier, combatant.Name,
                $"Инициатива · {combatant.Name}", showPanel: true, publish: true);
            if (diceRoll <= 0) return;

            InitiativeService.ApplyRoll(combatant, diceRoll);
            if (!_encounter.IsActive) InitiativeService.Begin(_encounter);
            else InitiativeService.SaveCombatant(_encounter, combatant);

            Publish();
            Render();
            Changed?.Invoke();
        }

        private void RollForAll(bool onlyMissing)
        {
            if (_encounter == null || !DmRights.CanUseDmTools()) return;

            var targets = _encounter.Combatants
                .Where(c => c != null && (!onlyMissing || !c.HasRolled))
                .ToList();

            if (targets.Count == 0)
            {
                DmRolls.Toast(this, onlyMissing
                    ? "Инициатива уже брошена всем жетонам"
                    : "В группе нет жетонов", true);
                return;
            }

            foreach (var combatant in targets)
            {
                // Панель результата показываем: мастеру важно видеть кубики.
                // В ленту отряда отдельные броски не шлём — вместо них уйдёт
                // готовый порядок хода, иначе лента превратится в кашу.
                int diceRoll = RollD20(combatant.InitiativeModifier, combatant.Name,
                    $"Инициатива · {combatant.Name}", showPanel: true, publish: false);
                if (diceRoll <= 0) continue;   // окно недоступно — молча пропускаем
                InitiativeService.ApplyRoll(combatant, diceRoll);
            }

            InitiativeService.Begin(_encounter);
            Publish();
            Render();
            Changed?.Invoke();
        }

        /// <summary>Бросает d20 и возвращает то, что выпало на кубике.</summary>
        private int RollD20(int modifier, string who, string what, bool showPanel, bool publish)
        {
            string formula = modifier == 0 ? "1d20" : $"1d20{(modifier > 0 ? "+" : "")}{modifier}";
            var result = DmRolls.Roll(this, formula, "Инициатива", who, what, showPanel, publish);
            if (result == null) return 0;

            if (result.IndividualRolls != null && result.IndividualRolls.Count > 0)
                return Math.Max(1, result.IndividualRolls[0]);

            return Math.Max(1, result.Total - modifier);
        }

        private void ShiftModifier(CombatantData combatant, int delta)
        {
            if (combatant == null) return;

            combatant.InitiativeModifier = Math.Max(-20, Math.Min(30, combatant.InitiativeModifier + delta));

            // Уже брошенный жетон пересчитываем: иначе итог разойдётся с модификатором
            if (combatant.HasRolled)
                combatant.InitiativeTotal = combatant.InitiativeRoll + combatant.InitiativeModifier;

            InitiativeService.SaveCombatant(_encounter, combatant);
            Publish();
            Render();
            Changed?.Invoke();
        }

        private void RemoveCombatant(CombatantData combatant)
        {
            if (combatant == null || _encounter == null) return;

            InitiativeService.DeleteCombatant(combatant);
            _encounter.Combatants.RemoveAll(c => c != null && c.Id == combatant.Id);

            // Убрали того, чей был ход — передаём ход дальше
            if (_encounter.Combatants.Count > 0 && _encounter.Current() == null)
                InitiativeService.Begin(_encounter);
            else if (_encounter.Combatants.Count == 0)
                InitiativeService.EndCombat(_encounter, false);

            Publish();
            Render();
            Changed?.Invoke();
        }

        private void Step(int direction)
        {
            if (_encounter == null) return;

            if (direction > 0) InitiativeService.NextTurn(_encounter);
            else InitiativeService.PreviousTurn(_encounter);

            Publish();
            Render();
            Changed?.Invoke();
        }

        private void AddRound()
        {
            if (_encounter == null) return;

            _encounter.Round = Math.Max(1, _encounter.Round) + 1;
            _encounter.IsActive = true;
            InitiativeService.SaveEncounter(_encounter);

            Publish();
            Render();
            Changed?.Invoke();
        }

        private void ClearRolls()
        {
            if (_encounter == null) return;

            bool confirmed = Windows.MessageDialog.Confirm(this,
                "Убрать все результаты инициативы? Порядок хода сбросится — это удобно перед новой стычкой.",
                "Сброс инициативы", "Сбросить");
            if (!confirmed) return;

            InitiativeService.ClearRolls(_encounter);
            Publish();
            Render();
            Changed?.Invoke();
        }

        private void EndCombat()
        {
            if (_encounter == null) return;

            bool confirmed = Windows.MessageDialog.Confirm(this,
                "Закончить бой? Порядок хода снимется, результаты бросков останутся — их можно посмотреть.",
                "Конец боя", "Закончить", "Отмена", false);
            if (!confirmed) return;

            InitiativeService.EndCombat(_encounter, false);
            Publish();
            Render();
            Changed?.Invoke();
        }

        // ══════════════════════ ОБМЕН С ОТРЯДОМ ══════════════════════

        /// <summary>
        /// Отправляет порядок хода игрокам. Уходит только имя и результат —
        /// бестиарий у каждого свой и по сети не передаётся.
        /// </summary>
        public void Publish(bool withNotice = false)
        {
            try
            {
                var session = RoomSession.Instance;
                if (session == null || !session.IsActive || !session.IsHost) return;
                if (_encounter == null) return;

                session.PublishInitiative(InitiativeService.BuildState(_encounter));

                if (withNotice && _encounter.HasAnyRoll)
                    session.PublishSystemNotice("Инициатива — " + InitiativeService.ToNotice(_encounter));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InitiativePanel] публикация: {ex.Message}");
            }
        }

        /// <summary>Объявление порядка хода в ленте отряда (по кнопке).</summary>
        public void PublishOrderToFeed()
        {
            Publish(withNotice: true);
            Changed?.Invoke();
        }

        /// <summary>Сбрасывает состояние: используется, когда бой закончился снаружи.</summary>
        public void ReloadFromDatabase()
        {
            Refresh();
            Changed?.Invoke();
        }
    }
}
