using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Настройка скрипта тёмной зоны: при входе жетона игрока тот бросает
    /// спасбросок или проверку от своего имени.
    ///
    /// Зона — инструмент мастера, поэтому окно доступно только ему.
    /// </summary>
    public sealed class ZoneScriptWindow : Window
    {
        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF5));
        private static readonly Brush Mute = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xB4));
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x1F));
        private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x18, 0x1F, 0x33));
        private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x68));
        private static readonly Brush ButtonBg = new SolidColorBrush(Color.FromRgb(0x22, 0x2B, 0x45));

        private ComboBox _kind, _ability, _skill;
        private TextBox _dc, _name;

        private ZoneScriptWindow()
        {
            Title = "Скрипт тёмной зоны";
            Width = 430;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Bg;
            Foreground = Ink;
            ShowInTaskbar = false;
        }

        /// <summary>Открывает окно и при сохранении записывает поля в фигуру.</summary>
        public static bool Edit(Window owner, MapShapeData shape)
        {
            if (shape == null) return false;

            var window = new ZoneScriptWindow();
            if (owner != null && owner.IsVisible) window.Owner = owner;

            var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };

            root.Children.Add(new TextBlock
            {
                Text = "Что происходит при входе жетона в зону",
                FontSize = 12, Foreground = Mute, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });

            // ── тип броска ──
            // Комбобоксы — из DmUiKit: у них тёмный шаблон и тёмный список,
            // иначе системный ComboBox рисовал белым по белому.
            window._kind = DmUiKit.ComboPairs(new[]
            {
                Pair("", "Ничего не делать"),
                Pair("save", "Спасбросок"),
                Pair("check", "Проверка")
            }, shape.ScriptKind ?? "");
            root.Children.Add(Label("Бросок"));
            root.Children.Add(window._kind);

            // ── характеристика ──
            window._ability = DmUiKit.ComboPairs(new[]
            {
                Pair("STR", "СИЛА"), Pair("DEX", "ЛОВКОСТЬ"), Pair("CON", "ТЕЛОСЛОЖЕНИЕ"),
                Pair("INT", "ИНТЕЛЛЕКТ"), Pair("WIS", "МУДРОСТЬ"), Pair("CHA", "ХАРИЗМА")
            }, string.IsNullOrWhiteSpace(shape.ScriptAbility) ? "DEX" : shape.ScriptAbility);
            root.Children.Add(Label("Характеристика (для спасброска и проверки)"));
            root.Children.Add(window._ability);

            // ── навык (только для проверки) ──
            var skillPairs = new List<KeyValuePair<string, string>>
            {
                Pair("", "— проверка характеристики —")
            };
            skillPairs.AddRange(SkillCatalog.All.Select(s => Pair(s.Key, s.Russian)));
            window._skill = DmUiKit.ComboPairs(skillPairs, shape.ScriptSkill ?? "");
            root.Children.Add(Label("Навык (если проверка на навык)"));
            root.Children.Add(window._skill);

            // ── DC и название ──
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dcPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            dcPanel.Children.Add(Label("Сложность (DC)"));
            window._dc = new TextBox
            {
                Text = shape.ScriptDC > 0 ? shape.ScriptDC.ToString() : "15",
                Padding = new Thickness(8, 5, 8, 5),
                Background = Field,
                Foreground = Ink,
                BorderBrush = Line,
                FontSize = 13
            };
            dcPanel.Children.Add(window._dc);
            Grid.SetColumn(dcPanel, 0);
            grid.Children.Add(dcPanel);

            var namePanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            namePanel.Children.Add(Label("Название эффекта"));
            window._name = new TextBox
            {
                Text = shape.ScriptName ?? "",
                Padding = new Thickness(8, 5, 8, 5),
                Background = Field,
                Foreground = Ink,
                BorderBrush = Line,
                FontSize = 13,
                ToolTip = "Например: Огненный шар, Ядовитый газ"
            };
            namePanel.Children.Add(window._name);
            Grid.SetColumn(namePanel, 1);
            grid.Children.Add(namePanel);

            root.Children.Add(grid);

            root.Children.Add(new TextBlock
            {
                Text = "Бросок делает сам игрок, чей жетон вошёл в зону, и он попадает " +
                       "в общую ленту. Срабатывает только на входе, а не постоянно.",
                FontSize = 11, Foreground = Mute, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var ok = MakeButton("Сохранить", 110);
            var cancel = MakeButton("Отмена", 90);

            ok.Click += (s, e) =>
            {
                shape.ScriptKind = DmUiKit.ComboKey(window._kind);
                shape.ScriptAbility = DmUiKit.ComboKey(window._ability);
                shape.ScriptSkill = DmUiKit.ComboKey(window._skill);
                shape.ScriptName = (window._name.Text ?? "").Trim();
                shape.ScriptDC = int.TryParse((window._dc.Text ?? "").Trim(), out int dc)
                    ? Math.Max(0, Math.Min(50, dc)) : 0;
                window.DialogResult = true;
            };
            cancel.Click += (s, e) => window.DialogResult = false;

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            window.Content = root;

            return window.ShowDialog() == true;
        }

        private static KeyValuePair<string, string> Pair(string key, string text)
            => new KeyValuePair<string, string>(key, text);

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Mute,
            Margin = new Thickness(0, 2, 0, 4)
        };

        private static Button MakeButton(string text, double width) => new Button
        {
            Content = text,
            MinWidth = width,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Background = ButtonBg,
            Foreground = Ink,
            BorderBrush = Line,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }
}
