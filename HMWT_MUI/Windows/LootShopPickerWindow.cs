using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMWT_MUI.Helpers;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Выбор предметов из каталога магазина для справочника добычи.
    /// Каталог уже заполнен импортом SRD — не нужно вбивать предметы руками.
    /// </summary>
    public sealed class LootShopPickerWindow : Window
    {
        private readonly ShopService _shop;
        private readonly ComboBox _type = new ComboBox();
        private readonly ComboBox _rarity = new ComboBox();
        private readonly TextBox _search = new TextBox();
        private readonly ListBox _list = new ListBox();
        private readonly TextBlock _status = new TextBlock();

        public List<SrdItemRow> SelectedItems { get; } = new List<SrdItemRow>();

        public LootShopPickerWindow(int characterId)
        {
            _shop = new ShopService(characterId);

            Title = "Предметы из магазина";
            Width = 780;
            Height = 560;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Content = BuildShell();

            Loaded += (s, e) => Reload();
        }

        private Border BuildShell()
        {
            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid { Margin = new Thickness(0, 0, 0, 12), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titles = new StackPanel();
            titles.Children.Add(new TextBlock
            {
                Text = "ИЗ МАГАЗИНА",
                FontFamily = DmUiKit.Display,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Accent")
            });
            titles.Children.Add(new TextBlock
            {
                Text = "Выберите предметы — они попадут в справочник добычи. Ctrl+клик — несколько.",
                FontSize = 11,
                Foreground = DmUiKit.B("Mute"),
                Margin = new Thickness(0, 3, 12, 0),
                TextWrapping = TextWrapping.Wrap
            });
            header.Children.Add(titles);

            var close = DmUiKit.Action("✕", (s, e) => DialogResult = false, DmUiKit.ButtonKind.Ghost, "Закрыть", 34, 30);
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var filters = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

            _search.Style = (Style)FindResource("SearchBox");
            _search.Height = 32;
            _search.Tag = "Поиск по названию";
            _search.TextChanged += (s, e) => Reload();
            filters.Children.Add(_search);

            _type.Style = (Style)FindResource("DarkComboBox");
            _type.Height = 32;
            _type.Margin = new Thickness(8, 0, 0, 0);
            _type.Items.Add("");
            foreach (string t in _shop.LoadItemTypes()) _type.Items.Add(t);
            _type.SelectedIndex = 0;
            _type.SelectionChanged += (s, e) => Reload();
            Grid.SetColumn(_type, 1);
            filters.Children.Add(_type);

            _rarity.Style = (Style)FindResource("DarkComboBox");
            _rarity.Height = 32;
            _rarity.Margin = new Thickness(8, 0, 0, 0);
            foreach (string r in LootService.Rarities) _rarity.Items.Add(r);
            _rarity.SelectedIndex = 0;
            _rarity.SelectionChanged += (s, e) => Reload();
            Grid.SetColumn(_rarity, 2);
            filters.Children.Add(_rarity);

            Grid.SetRow(filters, 1);
            root.Children.Add(filters);

            var listBox = new Border
            {
                Background = DmUiKit.B("Void"),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2)
            };

            _list.Background = Brushes.Transparent;
            _list.BorderThickness = new Thickness(0);
            _list.Foreground = DmUiKit.B("Ink");
            _list.SelectionMode = SelectionMode.Extended;
            _list.SelectionChanged += (s, e) => UpdateStatus();
            listBox.Child = _list;

            Grid.SetRow(listBox, 2);
            root.Children.Add(listBox);

            var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _status.FontSize = 11;
            _status.Foreground = DmUiKit.B("Mute");
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Text = "Загрузка…";
            bottom.Children.Add(_status);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var add = DmUiKit.Action("Добавить в справочник", (s, e) => Confirm(), DmUiKit.ButtonKind.Primary, null, 200, 36);
            actions.Children.Add(add);
            var cancel = DmUiKit.Action("Отмена", (s, e) => DialogResult = false, DmUiKit.ButtonKind.Soft, null, 100, 36);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(cancel);
            Grid.SetColumn(actions, 1);
            bottom.Children.Add(actions);

            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            return new Border
            {
                Background = DmUiKit.B("Panel"),
                BorderBrush = DmUiKit.B("AccentEdge"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = root
            };
        }

        private void Reload()
        {
            var filter = new ShopFilter
            {
                Search = (_search.Text ?? "").Trim(),
                ItemType = _type.SelectedItem as string ?? "",
                Rarity = _rarity.SelectedItem as string ?? ""
            };

            var items = _shop.LoadCatalog(filter, 300) ?? new List<SrdItemRow>();

            _list.Items.Clear();
            foreach (var item in items)
            {
                var text = string.IsNullOrWhiteSpace(item.Subtitle)
                    ? item.Name
                    : item.Name + "  —  " + item.Subtitle;

                _list.Items.Add(new ListBoxItem
                {
                    Content = text,
                    Tag = item,
                    Padding = new Thickness(10, 5, 10, 5),
                    FontSize = 12.5,
                    Foreground = DmUiKit.B("Ink")
                });
            }

            _status.Text = items.Count == 0
                ? "Ничего не найдено. Импортируйте SRD во вкладке «Магазин»."
                : "Найдено: " + items.Count;
        }

        private void UpdateStatus()
        {
            int count = _list.SelectedItems.Count;
            if (count > 0) _status.Text = "Выбрано: " + count;
        }

        private void Confirm()
        {
            SelectedItems.Clear();
            foreach (var item in _list.SelectedItems.OfType<ListBoxItem>())
                if (item.Tag is SrdItemRow row) SelectedItems.Add(row);

            if (SelectedItems.Count == 0)
            {
                _status.Text = "Выберите хотя бы один предмет";
                return;
            }

            DialogResult = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            try { DragMove(); } catch { }
        }
    }
}
