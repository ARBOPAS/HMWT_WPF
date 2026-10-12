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
    /// Вкладка трекера инициативы: группы боя, набор жетонов и ведение боя.
    ///
    /// Мастер создаёт группу под стычку, кидает в неё существ из бестиария или
    /// просто имена с модификатором, бросает инициативу и щёлкает «следующий ход».
    /// Игрокам уходит только имя и результат — бой ведёт один человек.
    /// </summary>
    public sealed class InitiativeView : UserControl
    {
        private readonly InitiativePanel _panel = new InitiativePanel(false);

        private readonly ComboBox _groupCombo = DmUiKit.Combo(Enumerable.Empty<string>(), null, false, 260);
        private readonly TextBox _nameBox = DmUiKit.Field("", 220, "Имя жетона: «Гоблин», «Вождь орков»");
        private readonly TextBox _modBox = DmUiKit.Field("0", 70, "Модификатор инициативы, например +2 или -1");
        private readonly TextBlock _statusText = DmUiKit.Value("", 11, DmUiKit.B("Mute"));

        private readonly StackPanel _pickerHost = new StackPanel();
        private readonly TextBox _pickerSearch = DmUiKit.Field("", 300, "Поиск по бестиарию");
        private readonly TextBox _pickerCount = DmUiKit.Field("1", 60, "Сколько таких добавить");
        private readonly StackPanel _pickerList = new StackPanel();
        private readonly TextBlock _pickerStatus = DmUiKit.Value("", 11, DmUiKit.B("Mute"));

        private Border _pickerLayer;
        private List<EncounterData> _encounters = new List<EncounterData>();
        private MonsterData _pickerSelected;

        public InitiativeView()
        {
            BuildShell();

            _panel.OpenMonsterRequested += id => OpenMonsterRequested?.Invoke(id);
            _panel.Changed += () => UpdateStatus();

            Loaded += (s, e) => Refresh();
        }

        /// <summary>Просят открыть существо в бестиарии (переключает вкладку внутри инструментов).</summary>
        public event Action<int> OpenMonsterRequested;

        // ══════════════════════ ОБОЛОЧКА ══════════════════════

        private void BuildShell()
        {
            var root = new Grid { Background = Brushes.Transparent };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(DmUiKit.Title("ТРЕКЕР ИНИЦИАТИВЫ"));
            _statusText.VerticalAlignment = VerticalAlignment.Bottom;
            _statusText.Margin = new Thickness(12, 0, 0, 3);
            title.Children.Add(_statusText);
            Grid.SetColumn(title, 0);
            titleRow.Children.Add(title);

            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };
            rightButtons.Children.Add(DmUiKit.Action("Объявить порядок", (s, e) => AnnounceOrder(),
                DmUiKit.ButtonKind.Ghost, "Отправить порядок хода в ленту отряда", 180, 30));
            Grid.SetColumn(rightButtons, 1);
            titleRow.Children.Add(rightButtons);
            toolbar.Children.Add(titleRow);

            // ── выбор группы боя ──
            var groupRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            groupRow.Children.Add(new TextBlock
            {
                Text = "Группа:",
                FontSize = 11,
                Foreground = DmUiKit.B("Mute"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            _groupCombo.Width = 260;
            _groupCombo.SelectionChanged += (s, e) => OnGroupChanged();
            groupRow.Children.Add(_groupCombo);
            groupRow.Children.Add(DmUiKit.Action("＋ Новая группа", (s, e) => CreateGroup(), DmUiKit.ButtonKind.Primary,
                "Создать новую стычку", 150, 30));
            groupRow.Children.Add(DmUiKit.Action("Переименовать", (s, e) => RenameGroup(), DmUiKit.ButtonKind.Ghost, null, 140, 30));
            groupRow.Children.Add(DmUiKit.Action("Удалить группу", (s, e) => DeleteGroup(), DmUiKit.ButtonKind.Danger,
                "Удалить группу вместе со всеми жетонами", 150, 30));
            toolbar.Children.Add(groupRow);

            // ── добавление жетонов ──
            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            addRow.Children.Add(DmUiKit.Action("＋ Существо из бестиария", (s, e) => OpenPicker(),
                DmUiKit.ButtonKind.Primary, "Добавить существ из своего бестиария", 210, 30));

            addRow.Children.Add(DmUiKit.Separator());

            addRow.Children.Add(_nameBox);
            _nameBox.Margin = new Thickness(0, 0, 6, 0);
            addRow.Children.Add(_modBox);
            _modBox.Margin = new Thickness(0, 0, 6, 0);
            addRow.Children.Add(DmUiKit.Action("＋ По имени", (s, e) => AddByName(), DmUiKit.ButtonKind.Ghost,
                "Добавить жетон с введённым именем и модификатором", 130, 30));

            addRow.Children.Add(DmUiKit.Separator());
            addRow.Children.Add(DmUiKit.Action("＋ Отряд игроков", (s, e) => AddParty(),
                DmUiKit.ButtonKind.Ghost, "Добавить персонажей из отряда с их инициативой", 170, 30));
            toolbar.Children.Add(addRow);

            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            Grid.SetRow(_panel, 1);
            root.Children.Add(_panel);

            // ── слой выбора существа из бестиария ──
            _pickerSearch.TextChanged += (s, e) => FillPickerList();
            _pickerSearch.Margin = new Thickness(0, 0, 8, 0);

            var pickerHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            pickerHead.Children.Add(_pickerSearch);
            pickerHead.Children.Add(new TextBlock
            {
                Text = "Количество:",
                FontSize = 11,
                Foreground = DmUiKit.B("Mute"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            pickerHead.Children.Add(_pickerCount);
            pickerHead.Children.Add(DmUiKit.Action("＋ Добавить в бой", (s, e) => ConfirmPicker(),
                DmUiKit.ButtonKind.Primary, null, 170, 30));
            pickerHead.Children.Add(DmUiKit.Action("Закрыть", (s, e) => ClosePicker(), DmUiKit.ButtonKind.Ghost, null, 110, 30));

            _pickerHost.Children.Add(pickerHead);
            _pickerHost.Children.Add(_pickerStatus);
            _pickerHost.Children.Add(DmUiKit.Scroll(_pickerList));

            var pickerCard = DmUiKit.Panel(_pickerHost, new Thickness(16), DmUiKit.B("Panel"), DmUiKit.B("GoldEdge"), 10);
            pickerCard.Width = 720;
            pickerCard.MaxHeight = 560;
            pickerCard.HorizontalAlignment = HorizontalAlignment.Center;
            pickerCard.VerticalAlignment = VerticalAlignment.Center;

            _pickerLayer = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x06, 0x06, 0x0A)),
                Visibility = Visibility.Collapsed,
                Child = pickerCard
            };
            Panel.SetZIndex(_pickerLayer, 200);
            Grid.SetRow(_pickerLayer, 0);
            Grid.SetRowSpan(_pickerLayer, 2);
            root.Children.Add(_pickerLayer);

            Content = root;
        }

        // ══════════════════════ ДАННЫЕ ══════════════════════

        /// <summary>Перечитывает группы из базы и показывает выбранную.</summary>
        public void Refresh()
        {
            if (!DmRights.CanUseDmTools())
            {
                _statusText.Text = DmRights.DenyReason();
                return;
            }

            int previousId = _panel.Encounter?.Id ?? 0;
            _encounters = InitiativeService.LoadEncounters();

            if (_encounters.Count == 0)
            {
                var created = InitiativeService.CreateEncounter("Бой 1");
                if (created != null) _encounters.Add(created);
            }

            FillGroupCombo(previousId);
            UpdateStatus();
        }

        private void FillGroupCombo(int preferId)
        {
            _groupCombo.SelectionChanged -= OnGroupChangedSilent;
            _groupCombo.Items.Clear();

            foreach (var encounter in _encounters)
            {
                string mark = encounter.IsActive ? "• " : "";
                _groupCombo.Items.Add($"{mark}{encounter.Name}  ({encounter.Combatants.Count})");
            }

            int index = _encounters.FindIndex(e => e.Id == preferId);
            if (index < 0) index = 0;
            _groupCombo.SelectedIndex = index;
            _groupCombo.SelectionChanged += OnGroupChangedSilent;

            ShowCurrentGroup();
        }

        private void OnGroupChangedSilent(object sender, SelectionChangedEventArgs e) => ShowCurrentGroup();

        private void OnGroupChanged()
        {
            // Пользователь выбрал группу руками: перечитываем её из базы
            ShowCurrentGroup();
        }

        private void ShowCurrentGroup()
        {
            int index = _groupCombo.SelectedIndex;
            if (index < 0 || index >= _encounters.Count)
            {
                _panel.Show(null);
                return;
            }

            var fresh = InitiativeService.LoadEncounter(_encounters[index].Id) ?? _encounters[index];
            _encounters[index] = fresh;
            _panel.Show(fresh);
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var encounter = _panel.Encounter;
            string role = DmRights.RoleLine();

            if (encounter == null)
            {
                _statusText.Text = "";
                return;
            }

            string room = RoomSession.Instance != null && RoomSession.Instance.IsActive
                ? "порядок уходит игрокам"
                : "порядок только у вас";

            _statusText.Text = $"{role} · {room} · {encounter.StatusLine()}";
        }

        // ══════════════════════ ГРУППЫ ══════════════════════

        private void CreateGroup()
        {
            var created = InitiativeService.CreateEncounter("");
            if (created == null)
            {
                DmRolls.Toast(this, "Не удалось создать группу", true);
                return;
            }

            _encounters.Insert(0, created);
            FillGroupCombo(created.Id);
            DmRolls.Toast(this, $"Создана группа «{created.Name}»");
        }

        private void RenameGroup()
        {
            var encounter = _panel.Encounter;
            if (encounter == null) return;

            string name = Windows.NotePromptWindow.AskText(Window.GetWindow(this), "Название группы",
                "Как назвать эту стычку?", encounter.Name ?? "", "Сохранить");
            if (string.IsNullOrWhiteSpace(name)) return;

            encounter.Name = name.Trim();
            InitiativeService.SaveEncounter(encounter);
            _panel.Refresh();
            ReloadGroups(encounter.Id);
            DmRolls.Toast(this, $"Группа переименована: {encounter.Name}");
        }

        private void DeleteGroup()
        {
            var encounter = _panel.Encounter;
            if (encounter == null) return;

            bool confirmed = Windows.MessageDialog.Confirm(this,
                $"Удалить группу «{encounter.Name}» вместе со всеми жетонами ({encounter.Combatants.Count})?",
                "Удаление группы", "Удалить");
            if (!confirmed) return;

            InitiativeService.DeleteEncounter(encounter.Id);
            _encounters.RemoveAll(e => e.Id == encounter.Id);
            _panel.Show(null);
            Refresh();
            DmRolls.Toast(this, "Группа удалена");
        }

        // ══════════════════════ ДОБАВЛЕНИЕ ЖЕТОНОВ ══════════════════════

        private void AddByName()
        {
            var encounter = _panel.Encounter;
            if (encounter == null) return;

            string name = (_nameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                DmRolls.Toast(this, "Впишите имя жетона", true);
                return;
            }

            int modifier = ParseModifier(_modBox.Text);
            var combatant = InitiativeService.AddByName(encounter, name, modifier);
            if (combatant == null)
            {
                DmRolls.Toast(this, "Не удалось добавить жетон", true);
                return;
            }

            _nameBox.Text = "";
            _panel.Refresh();
            ReloadGroups(encounter.Id);
            DmRolls.Toast(this, $"В бой добавлен «{combatant.Name}» (мод. {combatant.ModifierLine()})");
        }

        /// <summary>
        /// Добавляет существо в текущую группу боя. Вызывается кнопкой «В бой»
        /// из бестиария: мастер видит существо и сразу кидает его в бой.
        /// </summary>
        public void AddMonsterToCurrentGroup(MonsterData monster)
        {
            if (monster == null) return;

            var encounter = _panel.Encounter;
            if (encounter == null)
            {
                DmRolls.Toast(this, "Сначала создайте группу боя", true);
                return;
            }

            if (InitiativeService.AddFromMonster(encounter, monster, 1) <= 0)
            {
                DmRolls.Toast(this, "Не удалось добавить существо в бой", true);
                return;
            }

            _panel.Refresh();
            ReloadGroups(encounter.Id);
            DmRolls.Toast(this, $"В бой добавлен «{monster.Name}». Не забудьте бросить инициативу.");
        }

        private void AddParty()
        {
            var encounter = _panel.Encounter;
            if (encounter == null) return;

            var session = RoomSession.Instance;
            if (session == null || !session.IsActive)
            {
                DmRolls.Toast(this, "Отряд не создан: игроков пока нет", true);
                return;
            }

            var players = session.GetPlayers()
                .Where(p => p != null && p.Role == RoomRole.Player && !string.IsNullOrWhiteSpace(p.CharacterName))
                .ToList();

            if (players.Count == 0)
            {
                DmRolls.Toast(this, "В отряде нет игроков с персонажем", true);
                return;
            }

            int added = 0;
            foreach (var player in players)
            {
                string name = player.CharacterName.Trim();
                bool exists = encounter.Combatants.Any(c => c != null &&
                    string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase));
                if (exists) continue;

                if (InitiativeService.AddByName(encounter, name, player.Initiative, CombatantKind.Player) != null)
                    added++;
            }

            if (added == 0)
            {
                DmRolls.Toast(this, "Все персонажи отряда уже в этой группе");
                return;
            }

            _panel.Refresh();
            ReloadGroups(encounter.Id);
            DmRolls.Toast(this, $"Добавлено игроков: {added}");
        }

        /// <summary>
        /// Перечитывает группы из базы и оставляет выбранной указанную.
        /// Нужно после каждого изменения состава: счётчик жетонов в списке групп
        /// иначе показывал бы старое число.
        /// </summary>
        private void ReloadGroups(int selectedId)
        {
            _encounters = InitiativeService.LoadEncounters();
            if (_encounters.Count == 0)
            {
                var created = InitiativeService.CreateEncounter("Бой 1");
                if (created != null) _encounters.Add(created);
            }
            FillGroupCombo(selectedId);
        }

        // ══════════════════════ ВЫБОР СУЩЕСТВА ══════════════════════

        private void OpenPicker()
        {
            _pickerSelected = null;
            _pickerSearch.Text = "";
            FillPickerList();
            _pickerLayer.Visibility = Visibility.Visible;
        }

        private void ClosePicker() => _pickerLayer.Visibility = Visibility.Collapsed;

        private void FillPickerList()
        {
            _pickerList.Children.Clear();
            var all = BestiaryService.LoadAll();
            var shown = BestiaryService.Filter(all, _pickerSearch.Text);

            _pickerStatus.Text = all.Count == 0
                ? "Бестиарий пуст. Откройте вкладку «Бестиарий» и создайте существ (или добавьте примеры)."
                : $"Найдено: {shown.Count} из {all.Count}. Выбрано: {_pickerSelected?.Name ?? "—"}";

            if (shown.Count == 0) return;

            foreach (var monster in shown.Take(200))
            {
                bool selected = _pickerSelected != null && _pickerSelected.Id == monster.Id;

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = monster.Name,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Width = 190,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = selected ? DmUiKit.B("Gold") : DmUiKit.B("Ink"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = monster.TypeLine(),
                    FontSize = 10.5,
                    Width = 240,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = DmUiKit.B("Mute"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(DmUiKit.Chip($"ПО {monster.ChallengeRating}", DmUiKit.B("Arcane")));
                row.Children.Add(DmUiKit.Chip(
                    $"МОД. {(monster.InitiativeModifier >= 0 ? "+" : "")}{monster.InitiativeModifier}",
                    DmUiKit.B("Ok")));

                var border = DmUiKit.Panel(row, new Thickness(10, 6, 10, 6),
                    selected ? DmUiKit.Fade(DmUiKit.B("Gold"), 0x22) : DmUiKit.B("Slate"),
                    selected ? DmUiKit.B("Gold") : DmUiKit.B("Line"), 6,
                    new Thickness(0, 0, 0, 4));
                border.Cursor = Cursors.Hand;
                border.MouseLeftButtonUp += (s, e) =>
                {
                    _pickerSelected = monster;
                    FillPickerList();
                };

                _pickerList.Children.Add(border);
            }
        }

        private void ConfirmPicker()
        {
            var encounter = _panel.Encounter;
            if (encounter == null) return;

            if (_pickerSelected == null)
            {
                DmRolls.Toast(this, "Выберите существо в списке", true);
                return;
            }

            int count = 1;
            int.TryParse((_pickerCount.Text ?? "").Trim(), out count);
            if (count < 1) count = 1;
            if (count > 20) count = 20;

            int added = InitiativeService.AddFromMonster(encounter, _pickerSelected, count);
            if (added <= 0)
            {
                DmRolls.Toast(this, "Не удалось добавить существо в бой", true);
                return;
            }

            ClosePicker();
            _panel.Refresh();
            ReloadGroups(encounter.Id);
            DmRolls.Toast(this, $"В бой добавлено: {_pickerSelected.Name} × {added}");
        }

        private void AnnounceOrder()
        {
            var session = RoomSession.Instance;
            if (session == null || !session.IsActive || !session.IsHost)
            {
                DmRolls.Toast(this, "Отряд не создан: объявлять некому", true);
                return;
            }

            var encounter = _panel.Encounter;
            if (encounter == null || !encounter.HasAnyRoll)
            {
                DmRolls.Toast(this, "Сначала бросьте инициативу", true);
                return;
            }

            _panel.PublishOrderToFeed();
            DmRolls.Toast(this, "Порядок хода отправлен игрокам");
        }

        private static int ParseModifier(string text)
        {
            string cleaned = (text ?? "").Trim().Replace("+", "");
            if (int.TryParse(cleaned, out int value)) return Math.Max(-20, Math.Min(30, value));
            return 0;
        }
    }
}
