using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Helpers;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Добыча: генератор по уровню и типу тайника плюс редактируемый
    /// справочник предметов. Справочник свой у каждого персонажа: ведущий
    /// сам решает, что может выпасть в его мире.
    /// </summary>
    public sealed class LootView : UserControl
    {
        private readonly int _characterId;

        private readonly ComboBox _cr = new ComboBox();
        private readonly ComboBox _container = new ComboBox();
        private readonly TextBlock _resultText = new TextBlock();

        private readonly ComboBox _categories = new ComboBox();
        private readonly TextBox _newCategory = new TextBox();
        private readonly ListBox _items = new ListBox();

        private readonly TextBox _itemName = new TextBox();
        private readonly ComboBox _itemRarity = new ComboBox();
        private readonly TextBox _itemQtyMin = new TextBox();
        private readonly TextBox _itemQtyMax = new TextBox();
        private readonly TextBox _itemNote = new TextBox();
        private readonly TextBlock _summary = new TextBlock();
        private LootItem _editing;

        private LootResult _last;

        public LootView(int characterId)
        {
            _characterId = characterId;
            Content = BuildShell();
            Loaded += (s, e) => ReloadCatalog();
        }

        // ══════════════════════ РАЗМЕТКА ══════════════════════

        private Grid BuildShell()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            root.Children.Add(BuildGenerator());
            var catalog = BuildCatalog();
            Grid.SetColumn(catalog, 1);
            root.Children.Add(catalog);

            return root;
        }

        private Border BuildGenerator()
        {
            var panel = new StackPanel();

            panel.Children.Add(Head("ГЕНЕРАТОР ДОБЫЧИ"));

            panel.Children.Add(Label("Уровень / CR"));
            _cr.Style = (Style)FindResource("DarkComboBox");
            _cr.Height = 32;
            for (int i = 1; i <= 20; i++) _cr.Items.Add(i);
            _cr.SelectedIndex = 4;
            _cr.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(_cr);

            panel.Children.Add(Label("Тип тайника"));
            _container.Style = (Style)FindResource("DarkComboBox");
            _container.Height = 32;
            foreach (string c in LootService.Containers) _container.Items.Add(c);
            _container.SelectedIndex = 1;
            _container.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(_container);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(DmUiKit.Action("Сгенерировать", (s, e) => Generate(),
                DmUiKit.ButtonKind.Primary, null, 150, 34));
            var toChat = DmUiKit.Action("В чат", (s, e) => SendToChat(),
                DmUiKit.ButtonKind.Ghost, "Отправить добычу в чат комнаты", 90, 34);
            toChat.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(toChat);
            panel.Children.Add(buttons);

            var resultBox = new Border
            {
                Margin = new Thickness(0, 14, 0, 0),
                Background = DmUiKit.B("Slate"),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                MinHeight = 220
            };

            _resultText.Text = "Нажмите «Сгенерировать» — золото и предметы появятся здесь.";
            _resultText.FontSize = 12.5;
            _resultText.Foreground = DmUiKit.B("Ink");
            _resultText.TextWrapping = TextWrapping.Wrap;
            _resultText.LineHeight = 19;

            resultBox.Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _resultText
            };

            panel.Children.Add(resultBox);

            return DmUiKit.Panel(panel, new Thickness(16));
        }

        private Border BuildCatalog()
        {
            var panel = new StackPanel();

            panel.Children.Add(Head("СПРАВОЧНИК ПРЕДМЕТОВ"));

            var categoryRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            categoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            categoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            categoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            categoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _categories.Style = (Style)FindResource("DarkComboBox");
            _categories.Height = 32;
            _categories.SelectionChanged += (s, e) => ReloadItems();
            categoryRow.Children.Add(_categories);

            _newCategory.Style = (Style)FindResource("TextField");
            _newCategory.Height = 32;
            _newCategory.Width = 150;
            _newCategory.Margin = new Thickness(8, 0, 0, 0);
            _newCategory.ToolTip = "Название новой категории";
            Grid.SetColumn(_newCategory, 1);
            categoryRow.Children.Add(_newCategory);

            var addCategory = DmUiKit.Action("+ Категория", (s, e) => AddCategory(),
                DmUiKit.ButtonKind.Soft, null, 122, 32);
            addCategory.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(addCategory, 2);
            categoryRow.Children.Add(addCategory);

            var deleteCategory = DmUiKit.Action("Удалить", (s, e) => DeleteCategory(),
                DmUiKit.ButtonKind.Ghost, "Удалить категорию вместе с предметами", 90, 32);
            deleteCategory.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(deleteCategory, 3);
            categoryRow.Children.Add(deleteCategory);

            panel.Children.Add(categoryRow);

            var itemsBox = new Border
            {
                Background = DmUiKit.B("Void"),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2),
                Height = 240
            };

            _items.Background = Brushes.Transparent;
            _items.BorderThickness = new Thickness(0);
            _items.Foreground = DmUiKit.B("Ink");
            _items.SelectionChanged += (s, e) => LoadItemIntoForm();
            itemsBox.Child = _items;
            panel.Children.Add(itemsBox);

            // ── форма предмета ──
            var form = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            _itemName.Style = (Style)FindResource("TextField");
            _itemName.Height = 32;
            _itemName.ToolTip = "Название предмета";
            form.Children.Add(_itemName);

            _itemRarity.Style = (Style)FindResource("DarkComboBox");
            _itemRarity.Height = 32;
            foreach (string r in LootService.Rarities) _itemRarity.Items.Add(r);
            _itemRarity.SelectedIndex = 0;
            _itemRarity.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(_itemRarity, 1);
            form.Children.Add(_itemRarity);

            _itemQtyMin.Style = (Style)FindResource("NumField");
            _itemQtyMin.Text = "1";
            _itemQtyMin.Margin = new Thickness(6, 0, 0, 0);
            _itemQtyMin.ToolTip = "Сколько минимум";
            Grid.SetColumn(_itemQtyMin, 2);
            form.Children.Add(_itemQtyMin);

            _itemQtyMax.Style = (Style)FindResource("NumField");
            _itemQtyMax.Text = "1";
            _itemQtyMax.Margin = new Thickness(6, 0, 0, 0);
            _itemQtyMax.ToolTip = "Сколько максимум";
            Grid.SetColumn(_itemQtyMax, 3);
            form.Children.Add(_itemQtyMax);

            panel.Children.Add(form);

            _itemNote.Style = (Style)FindResource("TextField");
            _itemNote.Height = 32;
            _itemNote.Margin = new Thickness(0, 8, 0, 0);
            _itemNote.ToolTip = "Заметка: свойства, вес, что угодно";
            panel.Children.Add(_itemNote);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(DmUiKit.Action("Добавить / сохранить", (s, e) => SaveItem(),
                DmUiKit.ButtonKind.Primary, null, 190, 34));

            var clear = DmUiKit.Action("Очистить", (s, e) => NewItem(),
                DmUiKit.ButtonKind.Soft, null, 100, 34);
            clear.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(clear);

            var delete = DmUiKit.Action("Удалить", (s, e) => DeleteItem(),
                DmUiKit.ButtonKind.Ghost, "Удалить выбранный предмет", 100, 34);
            delete.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(delete);

            var fromShop = DmUiKit.Action("Из магазина…", (s, e) => AddFromShop(),
                DmUiKit.ButtonKind.Soft, "Добавить предметы из каталога магазина (SRD)", 150, 34);
            fromShop.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(fromShop);

            panel.Children.Add(actions);

            // ── сводка и подсказка: заполняет пустое место под списком ──
            var summaryBox = new Border
            {
                Margin = new Thickness(0, 14, 0, 0),
                Background = DmUiKit.B("Slate"),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _summary.FontSize = 12;
            _summary.Foreground = DmUiKit.B("Ink");
            _summary.TextWrapping = TextWrapping.Wrap;
            _summary.LineHeight = 19;
            summaryBox.Child = _summary;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(panel);
            Grid.SetRow(summaryBox, 1);
            root.Children.Add(summaryBox);

            return DmUiKit.Panel(root, new Thickness(16));
        }

        // ══════════════════════ СПРАВОЧНИК ══════════════════════

        private void ReloadCatalog(int selectCategoryId = 0)
        {
            int keep = selectCategoryId > 0
                ? selectCategoryId
                : (_categories.SelectedItem as LootCategory)?.Id ?? 0;

            var categories = LootService.LoadCategories(_characterId);
            _categories.Items.Clear();
            foreach (var category in categories) _categories.Items.Add(category);

            if (categories.Count == 0)
            {
                ReloadItems();
                return;
            }

            var selected = categories.FirstOrDefault(c => c.Id == keep) ?? categories[0];
            _categories.SelectedItem = selected;
            RefreshSummary();
        }

        /// <summary>Сводка под редактором: сколько предметов в каждой категории.</summary>
        private void RefreshSummary()
        {
            if (_summary == null) return;

            var categories = LootService.LoadCategories(_characterId);
            var counts = LootService.CountsByCategory(_characterId);
            int total = counts.Values.Sum();

            var text = new System.Text.StringBuilder();
            text.AppendLine($"В справочнике предметов: {total}.");
            foreach (var category in categories)
            {
                int count = counts.TryGetValue(category.Id, out int value) ? value : 0;
                text.AppendLine($"• {category.Name} — {count}");
            }

            text.AppendLine();
            text.Append("Пусто в категории? Нажмите «Из магазина…» — там каталог SRD (2014 и 2024), который уже импортирован во вкладку «Магазин». ");
            text.Append("Генератор берёт предметы из справочника, а если он пуст — прямо из магазина по редкости уровня.");

            _summary.Text = text.ToString();
        }

        private void ReloadItems(int selectItemId = 0)
        {
            var category = _categories.SelectedItem as LootCategory;

            _items.Items.Clear();
            if (category == null) return;

            foreach (var item in LootService.LoadItems(category.Id))
            {
                _items.Items.Add(new ListBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(item.Caption)
                        ? item.Name
                        : item.Name + "  —  " + item.Caption,
                    Tag = item,
                    Padding = new Thickness(10, 6, 10, 6),
                    FontSize = 12.5,
                    Foreground = DmUiKit.B("Ink")
                });
            }

            if (selectItemId > 0)
            {
                var again = _items.Items.OfType<ListBoxItem>()
                    .FirstOrDefault(i => (i.Tag as LootItem)?.Id == selectItemId);
                if (again != null) _items.SelectedItem = again;
            }
        }

        private void AddCategory()
        {
            string name = (_newCategory.Text ?? "").Trim();
            if (name.Length == 0)
            {
                Toast("Впишите название категории");
                return;
            }

            int id = LootService.SaveCategory(_characterId, name);
            if (id <= 0)
            {
                Toast("Не удалось добавить категорию");
                return;
            }

            _newCategory.Text = "";
            ReloadCatalog(id);
        }

        private void DeleteCategory()
        {
            var category = _categories.SelectedItem as LootCategory;
            if (category == null)
            {
                Toast("Выберите категорию");
                return;
            }

            if (!Windows.MessageDialog.Confirm(this, "Удалить категорию «" + category.Name + "» вместе с предметами?",
                    "Справочник добычи", "Удалить", "Отмена", true))
                return;

            LootService.DeleteCategory(category.Id);
            ReloadCatalog();
        }

        private void LoadItemIntoForm()
        {
            if (_items.SelectedItem is ListBoxItem row && row.Tag is LootItem item)
            {
                _editing = item;
                _itemName.Text = item.Name;
                _itemRarity.SelectedItem = LootService.Rarities.Contains(item.Rarity) ? item.Rarity : "";
                _itemQtyMin.Text = item.QtyMin.ToString();
                _itemQtyMax.Text = item.QtyMax.ToString();
                _itemNote.Text = item.Note;
            }
        }

        private void NewItem()
        {
            _editing = null;
            _items.SelectedItem = null;
            _itemName.Text = "";
            _itemRarity.SelectedIndex = 0;
            _itemQtyMin.Text = "1";
            _itemQtyMax.Text = "1";
            _itemNote.Text = "";
            _itemName.Focus();
        }

        private void SaveItem()
        {
            var category = _categories.SelectedItem as LootCategory;
            if (category == null)
            {
                Toast("Сначала выберите или создайте категорию");
                return;
            }

            string name = (_itemName.Text ?? "").Trim();
            if (name.Length == 0)
            {
                Toast("Впишите название предмета");
                return;
            }

            var item = new LootItem
            {
                Id = _editing?.Id ?? 0,
                CategoryId = category.Id,
                Name = name,
                Rarity = (_itemRarity.SelectedItem as string ?? "").Trim(),
                QtyMin = ParseInt(_itemQtyMin.Text, 1),
                QtyMax = ParseInt(_itemQtyMax.Text, 1),
                Note = (_itemNote.Text ?? "").Trim()
            };

            int id = LootService.SaveItem(item);
            if (id <= 0)
            {
                Toast("Не удалось сохранить предмет");
                return;
            }

            ReloadItems(id);
            NewItem();
            RefreshSummary();
            Toast("Предмет сохранён");
        }

        private void DeleteItem()
        {
            var item = (_items.SelectedItem as ListBoxItem)?.Tag as LootItem;
            if (item == null)
            {
                Toast("Выберите предмет в списке");
                return;
            }

            LootService.DeleteItem(item.Id);
            ReloadItems();
            NewItem();
            RefreshSummary();
        }

        // ══════════════════════ ИЗ МАГАЗИНА ══════════════════════

        /// <summary>
        /// Добавляет предметы из каталога магазина в текущую категорию.
        /// Каталог SRD уже импортирован, поэтому справочник добычи не нужно
        /// заполнять вручную — берём готовое.
        /// </summary>
        private void AddFromShop()
        {
            var category = _categories.SelectedItem as LootCategory;
            if (category == null)
            {
                Toast("Сначала выберите или создайте категорию");
                return;
            }

            var window = new Windows.LootShopPickerWindow(_characterId) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() != true) return;

            int added = 0, skipped = 0;
            foreach (var row in window.SelectedItems)
            {
                int id = LootService.AddFromShop(category.Id, row);
                if (id > 0) added++;
                else skipped++;
            }

            ReloadItems();
            RefreshSummary();

            Toast(skipped > 0
                ? $"Добавлено: {added}, уже было: {skipped}"
                : $"Добавлено предметов: {added}");
        }

        // ══════════════════════ ГЕНЕРАЦИЯ ══════════════════════

        private void Generate()
        {
            int cr = _cr.SelectedItem is int value ? value : 5;
            string container = _container.SelectedItem as string ?? "Сундук";

            _last = LootService.Generate(_characterId, cr, container);
            _resultText.Text = _last.ToText();
        }

        private void SendToChat()
        {
            if (_last == null)
            {
                Generate();
                if (_last == null) return;
            }

            var room = RoomSession.Instance;
            if (room == null || !room.IsActive)
            {
                Toast("Чат доступен в комнате — подключитесь к отряду");
                return;
            }

            room.PublishChat(_last.ToText());
            Toast("Добыча отправлена в чат");
        }

        // ══════════════════════ МЕЛОЧИ ══════════════════════

        private static TextBlock Head(string text) => new TextBlock
        {
            Text = text,
            FontFamily = DmUiKit.Display,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = DmUiKit.B("Accent"),
            Margin = new Thickness(0, 0, 0, 12)
        };

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = DmUiKit.B("Mute"),
            Margin = new Thickness(0, 0, 0, 4)
        };

        private void Toast(string text)
        {
            var owner = Window.GetWindow(this);
            if (owner != null) DmRolls.Toast(owner, text);
        }

        private static int ParseInt(string raw, int fallback)
            => int.TryParse((raw ?? "").Trim(), out int value) ? Math.Max(1, Math.Min(99, value)) : fallback;
    }
}
