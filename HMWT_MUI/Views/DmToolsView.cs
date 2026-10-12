using HMWT_MUI.Helpers;
using HMWT_MUI.Net;
using HMWT_MUI.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Инструменты ДМа: трекер инициативы и бестиарий в одной вкладке.
    ///
    /// Права проверяются здесь и в окне персонажа: пока игра идёт за одним
    /// компьютером, вкладка доступна всегда, а в отряде её видит только ведущий —
    /// бой должен вести один человек, иначе порядок хода разъедется.
    /// </summary>
    public sealed class DmToolsView : UserControl
    {
        private readonly int _characterId;

        private readonly ContentControl _area = new ContentControl();
        private readonly TextBlock _roleText = DmUiKit.Value("", 10.5, DmUiKit.B("Mute"));
        private readonly Border _lockPanel;

        private InitiativeView _initiative;
        private BestiaryView _bestiary;
        private string _current = "Initiative";
        private Button _initiativeTab;
        private Button _bestiaryTab;

        public DmToolsView(int characterId)
        {
            _characterId = characterId;
            _lockPanel = BuildLockPanel();
            BuildShell();
        }

        // ══════════════════════ ОБОЛОЧКА ══════════════════════

        private Border BuildLockPanel()
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(30) };
            panel.Children.Add(new IconView
            {
                Kind = "lock",
                IconSize = 34,
                HorizontalAlignment = HorizontalAlignment.Center,
                IconBrush = DmUiKit.B("Mute")
            });
            panel.Children.Add(new TextBlock
            {
                Text = "ИНСТРУМЕНТЫ ДМА ДОСТУПНЫ ТОЛЬКО ВЕДУЩЕМУ",
                FontFamily = DmUiKit.Display,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Gold"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 8)
            });
            panel.Children.Add(new TextBlock
            {
                Text = DmRights.DenyReason(),
                FontSize = 11.5,
                Foreground = DmUiKit.B("Mute"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 520
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Попросите ведущего бросить инициативу за ваш отряд — порядок хода придёт во вкладку «Отряд».",
                FontSize = 11,
                Foreground = DmUiKit.B("Mute"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 520,
                Margin = new Thickness(0, 10, 0, 0)
            });

            return new Border
            {
                Background = DmUiKit.Fade(DmUiKit.B("Panel"), 0xCC),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = panel,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
        }

        private void BuildShell()
        {
            var root = new Grid { Background = Brushes.Transparent };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ── шапка с подвкладками ──
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            _initiativeTab = TabButton("⚔ ИНИЦИАТИВА", "Initiative");
            _bestiaryTab = TabButton("🐉 БЕСТИАРИЙ", "Bestiary");
            tabs.Children.Add(_initiativeTab);
            tabs.Children.Add(_bestiaryTab);
            Grid.SetColumn(tabs, 0);
            header.Children.Add(tabs);

            _roleText.VerticalAlignment = VerticalAlignment.Center;
            _roleText.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(_roleText, 1);
            header.Children.Add(_roleText);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            Grid.SetRow(_area, 1);
            root.Children.Add(_area);

            Grid.SetRow(_lockPanel, 0);
            Grid.SetRowSpan(_lockPanel, 2);
            Panel.SetZIndex(_lockPanel, 50);
            root.Children.Add(_lockPanel);

            Content = root;
            Loaded += (s, e) => Refresh();
        }

        private Button TabButton(string text, string view)
        {
            var button = DmUiKit.Action(text, (s, e) => SwitchTo(view), DmUiKit.ButtonKind.Ghost, null, 180, 32);
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Tag = view;
            return button;
        }

        // ══════════════════════ ПЕРЕКЛЮЧЕНИЕ ══════════════════════

        /// <summary>Показывает нужную подвкладку. view: «Initiative» или «Bestiary».</summary>
        public void SwitchTo(string view)
        {
            if (string.IsNullOrWhiteSpace(view)) view = "Initiative";
            _current = view;

            if (view == "Bestiary")
            {
                if (_bestiary == null)
                {
                    _bestiary = new BestiaryView(_characterId);
                    _bestiary.AddToEncounterRequested += AddMonsterToEncounter;
                }
                _area.Content = _bestiary;
                _bestiary.Refresh();
            }
            else
            {
                if (_initiative == null)
                {
                    _initiative = new InitiativeView();
                    _initiative.OpenMonsterRequested += id =>
                    {
                        SwitchTo("Bestiary");
                        _bestiary?.SelectMonster(id);
                    };
                }
                _area.Content = _initiative;
                _initiative.Refresh();
            }

            UpdateTabStyles();
            UpdateRoleLine();
        }

        private void UpdateTabStyles()
        {
            // Активная подвкладка выделяется золотом, как в остальных вкладках окна
            ApplyTabStyle(_initiativeTab, _current == "Initiative");
            ApplyTabStyle(_bestiaryTab, _current == "Bestiary");
        }

        private void ApplyTabStyle(Button button, bool active)
        {
            if (button == null) return;
            button.Background = active ? DmUiKit.B("Gold") : DmUiKit.B("PanelHi");
            button.Foreground = active
                ? (Brush)new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x06))
                : DmUiKit.B("Ink");
        }

        private void UpdateRoleLine()
        {
            _roleText.Text = "роль: " + DmRights.RoleLine();
        }

        // ══════════════════════ ПРАВА И ОБНОВЛЕНИЕ ══════════════════════

        /// <summary>Перечитывает данные и пересчитывает права (роль могла смениться).</summary>
        public void Refresh()
        {
            bool allowed = DmRights.CanUseDmTools();
            _lockPanel.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
            _area.Visibility = allowed ? Visibility.Visible : Visibility.Hidden;
            UpdateRoleLine();

            if (!allowed) return;

            // Первое открытие вкладки: содержимое ещё не собрано. Без этого
            // вкладка показывала пустоту, пока не нажмёшь подвкладку руками.
            if (_area.Content == null)
            {
                SwitchTo(_current);
                return;
            }

            if (_current == "Bestiary") _bestiary?.Refresh();
            else _initiative?.Refresh();
        }

        /// <summary>Открывает бестиарий на конкретном существе (переход с жетона инициативы).</summary>
        public void OpenBestiary(int monsterId)
        {
            SwitchTo("Bestiary");
            _bestiary?.SelectMonster(monsterId);
        }

        /// <summary>Добавляет существо в текущую группу боя и показывает трекер.</summary>
        private void AddMonsterToEncounter(HMWT_MUI.Models.MonsterData monster)
        {
            if (monster == null) return;

            SwitchTo("Initiative");
            if (_initiative == null) return;
            _initiative.Refresh();

            // Состав меняет сама вкладка: она знает выбранную группу
            _initiative.AddMonsterToCurrentGroup(monster);
        }
    }
}
