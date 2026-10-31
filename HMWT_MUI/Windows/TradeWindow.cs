using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Передача предметов и золота другому игроку.
    ///
    /// Инициатор выбирает предметы в своём инвентаре, получатель видит
    /// предложение и подтверждает. Перемещение в базе делает каждая сторона
    /// у себя: у игроков нет чужой базы, а по сети идут только снимки предметов.
    /// </summary>
    public sealed class TradeWindow : Window
    {
        private readonly int _characterId;
        private readonly PlayerInfo _other;
        private readonly TradeMessage _incoming;
        private readonly RoomSession _room = RoomSession.Instance;
        private readonly InventoryService _inventory;
        private readonly MoneyService _money;

        private readonly List<ItemData> _items = new List<ItemData>();
        private readonly List<ItemData> _selected = new List<ItemData>();
        private readonly StackPanel _leftList = new StackPanel();
        private readonly StackPanel _rightList = new StackPanel();
        private readonly TextBox _search = new TextBox();
        private readonly ComboBox _category = new ComboBox();
        private readonly ComboBox _sort = new ComboBox();
        private readonly TextBox _gold = new TextBox();
        private readonly TextBox _electrum = new TextBox();
        private readonly TextBox _silver = new TextBox();
        private readonly TextBox _copper = new TextBox();
        private readonly TextBlock _balance = new TextBlock();
        private readonly Dictionary<int, int> _quantities = new Dictionary<int, int>();
        private bool _accepted;
        private bool _completed;
        private bool _closeSent;
        private System.Windows.Threading.DispatcherTimer _waitTimer;
        private readonly TextBlock _status = new TextBlock();
        private readonly Button _primary;
        private readonly Button _cancel;
        private TradeMessage _pending;
        private bool _busy;

        public TradeWindow(Window owner, int characterId, PlayerInfo other, TradeMessage incoming = null)
        {
            _characterId = characterId;
            _other = other;
            _incoming = incoming;
            _inventory = new InventoryService(characterId);
            _money = new MoneyService(characterId);

            Title = incoming == null ? "Передать предметы" : "Входящий обмен";
            Width = 1040;
            Height = 680;
            MinWidth = 820;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            Owner = owner;
            Background = Brushes.Transparent;
            Foreground = (Brush)FindResource("B.Ink");

            _primary = new Button
            {
                Content = incoming == null ? "Предложить" : "Принять",
                Style = (Style)FindResource("MagicBtn"),
                Padding = new Thickness(22, 8, 22, 8),
                Margin = new Thickness(10, 0, 0, 0)
            };
            _primary.Click += OnPrimary;
            _cancel = new Button
            {
                Content = incoming == null ? "Отмена" : "Отклонить",
                Style = (Style)FindResource("GhostBtn"),
                Padding = new Thickness(18, 8, 18, 8)
            };
            _cancel.Click += OnCancel;

            Build();
            LoadInventory();
            LoadMoney();
            _room.TradeReceived += OnTrade;
            Closed += OnClosed;
            if (_incoming != null) ShowIncomingOffer();
        }

        private void Build()
        {
            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = _incoming == null ? "ПЕРЕДАТЬ ПРЕДМЕТЫ" : "ВХОДЯЩИЙ ОБМЕН",
                FontFamily = (FontFamily)FindResource("F.Display"),
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("B.Accent")
            };
            head.Children.Add(title);
            head.Cursor = Cursors.SizeAll;
            head.MouseLeftButtonDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed) DragMove();
            };
            var close = new Button
            {
                Content = new Helpers.IconView { Kind = "close", IconSize = 14, IconBrush = (Brush)FindResource("B.Mute") },
                Style = (Style)FindResource("GhostCloseBtn"),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            close.Click += (s, e) => Close();
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.Children.Add(BuildLeftPanel());
            var spacer = new Border();
            Grid.SetColumn(spacer, 1);
            columns.Children.Add(spacer);
            var right = BuildRightPanel();
            Grid.SetColumn(right, 2);
            columns.Children.Add(right);
            Grid.SetRow(columns, 1);
            root.Children.Add(columns);

            var bottom = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _status.Foreground = (Brush)FindResource("B.Mute");
            _status.FontSize = 12;
            _status.TextWrapping = TextWrapping.Wrap;
            bottom.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_cancel);
            buttons.Children.Add(_primary);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            Content = new Border
            {
                Background = (Brush)FindResource("B.Panel"),
                BorderBrush = (Brush)FindResource("B.AccentEdge"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = root,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.7
                }
            };
        }

        private UIElement BuildLeftPanel()
        {
            var panel = new Border
            {
                Background = (Brush)FindResource("B.Panel"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = _incoming == null ? "ВАШ ИНВЕНТАРЬ" : "ПРЕДЛОЖЕНИЕ ОТ " +
                    (_other?.CharacterName ?? _other?.Name ?? "игрока").ToUpperInvariant(),
                FontFamily = (FontFamily)FindResource("F.Display"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("B.Accent"),
                Margin = new Thickness(0, 0, 0, 10)
            });

            if (_incoming == null)
            {
                var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                if (TryFindResource("SearchBox") is Style searchStyle)
                {
                    _search.Style = searchStyle;
                    _search.Tag = "Поиск по инвентарю";
                }
                _search.Width = 210;
                _search.Height = 30;
                _search.Padding = new Thickness(7, 4, 7, 4);
                _search.Background = (Brush)FindResource("B.Slate");
                _search.Foreground = (Brush)FindResource("B.Ink");
                _search.BorderBrush = (Brush)FindResource("B.Line");
                _search.BorderThickness = new Thickness(1);
                _search.TextChanged += (s, e) => RefreshLeft();
                filters.Children.Add(_search);

                _category.Width = 170;
                _category.Height = 30;
                _category.Margin = new Thickness(8, 0, 0, 0);
                _category.Background = (Brush)FindResource("B.Slate");
                _category.Foreground = (Brush)FindResource("B.Ink");
                _category.BorderBrush = (Brush)FindResource("B.Line");
                if (TryFindResource("EditorComboBox") is Style comboStyle) _category.Style = comboStyle;
                _category.SelectionChanged += (s, e) => RefreshLeft();
                filters.Children.Add(_category);

                _sort.Width = 160;
                _sort.Height = 30;
                _sort.Margin = new Thickness(8, 0, 0, 0);
                _sort.Background = (Brush)FindResource("B.Slate");
                _sort.Foreground = (Brush)FindResource("B.Ink");
                _sort.BorderBrush = (Brush)FindResource("B.Line");
                _sort.Items.Add("По названию");
                _sort.Items.Add("По категории");
                _sort.Items.Add("По цене");
                _sort.SelectedIndex = 0;
                if (TryFindResource("EditorComboBox") is Style sortStyle) _sort.Style = sortStyle;
                _sort.SelectionChanged += (s, e) => RefreshLeft();
                filters.Children.Add(_sort);
                stack.Children.Add(filters);
            }

            stack.Children.Add(new ScrollViewer
            {
                MaxHeight = 430,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _leftList
            });

            if (_incoming == null)
            {
                var goldRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                goldRow.Children.Add(Label("Золото"));
                goldRow.Children.Add(MoneyField(_gold));
                goldRow.Children.Add(Label("Электрум"));
                goldRow.Children.Add(MoneyField(_electrum));
                goldRow.Children.Add(Label("Серебро"));
                goldRow.Children.Add(MoneyField(_silver));
                goldRow.Children.Add(Label("Медь"));
                goldRow.Children.Add(MoneyField(_copper));
                foreach (var box in new[] { _gold, _electrum, _silver, _copper })
                {
                    box.Background = (Brush)FindResource("B.Slate");
                    box.Foreground = (Brush)FindResource("B.Ink");
                    box.BorderBrush = (Brush)FindResource("B.Line");
                    box.BorderThickness = new Thickness(1);
                }
                stack.Children.Add(goldRow);
                _balance.Foreground = (Brush)FindResource("B.Mute");
                _balance.FontSize = 11;
                _balance.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(_balance);
            }

            panel.Child = stack;
            return panel;
        }

        private UIElement BuildRightPanel()
        {
            var panel = new Border
            {
                Background = (Brush)FindResource("B.Panel"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14)
            };
            var stack = new StackPanel();
            string who = _other?.CharacterName ?? _other?.Name ?? "получатель";
            stack.Children.Add(new TextBlock
            {
                Text = _incoming == null ? "ПОЛУЧАТЕЛЬ: " + who.ToUpperInvariant() : "ВАШ ИНВЕНТАРЬ",
                FontFamily = (FontFamily)FindResource("F.Display"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("B.Frost"),
                Margin = new Thickness(0, 0, 0, 10)
            });
            stack.Children.Add(new ScrollViewer
            {
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _rightList
            });
            panel.Child = stack;
            return panel;
        }

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xB4)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        private static TextBox MoneyField(TextBox box)
        {
            box.Width = 64;
            box.Height = 28;
            box.Text = "0";
            box.Margin = new Thickness(0, 0, 12, 0);
            box.TextAlignment = TextAlignment.Center;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            return box;
        }

        private void LoadInventory()
        {
            _items.Clear();
            try
            {
                var table = _inventory.GetFilteredInventory();
                foreach (DataRow row in table.Rows)
                {
                    int id = Convert.ToInt32(row["id_item"]);
                    var item = _inventory.GetItem(id);
                    if (item != null) _items.Add(item);
                }
            }
            catch (Exception ex)
            {
                _status.Text = "Не удалось прочитать инвентарь: " + ex.Message;
            }

            _category.Items.Clear();
            _category.Items.Add("Все категории");
            foreach (string c in _items.Select(i => i.Category ?? "").Where(c => c.Length > 0)
                         .Distinct().OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase))
                _category.Items.Add(c);
            _category.SelectedIndex = 0;
            RefreshLeft();
        }

        private void LoadMoney()
        {
            try
            {
                var money = _money.LoadMoney();
                _gold.Text = money.Gold.ToString();
                _electrum.Text = money.Electrum.ToString();
                _silver.Text = money.Silver.ToString();
                _copper.Text = money.Copper.ToString();
                _balance.Text = $"У вас: {money.Gold} з. · {money.Electrum} э. · {money.Silver} с. · {money.Copper} м.";
            }
            catch { }
        }

        private void RefreshLeft()
        {
            _leftList.Children.Clear();
            if (_incoming != null)
            {
                foreach (var item in _incoming.Items ?? new List<TradeItem>())
                    _leftList.Children.Add(new TextBlock
                    {
                        Text = "• " + item.Name + (item.Quantity > 1 ? " ×" + item.Quantity : ""),
                        FontSize = 13,
                        Foreground = (Brush)FindResource("B.Ink"),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                if ((_incoming.Gold + _incoming.Electrum + _incoming.Silver + _incoming.Copper) > 0)
                    _leftList.Children.Add(new TextBlock
                    {
                        Text = $"Монеты: {_incoming.Gold} з. · {_incoming.Electrum} э. · {_incoming.Silver} с. · {_incoming.Copper} м.",
                        FontSize = 12,
                        Foreground = (Brush)FindResource("B.Accent"),
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                return;
            }

            string query = (_search.Text ?? "").Trim();
            string category = _category.SelectedItem as string ?? "Все категории";
            var filtered = _items.Where(i =>
                (query.Length == 0 || (i.Name ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                 (i.Category ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0) &&
                (category == "Все категории" || string.Equals(i.Category, category, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();

            if (_sort.SelectedIndex == 1) filtered = filtered.OrderBy(i => i.Category).ThenBy(i => i.Name).ToList();
            else if (_sort.SelectedIndex == 2) filtered = filtered.OrderByDescending(i => i.CostGold).ThenBy(i => i.Name).ToList();
            else filtered = filtered.OrderBy(i => i.Name).ToList();

            foreach (var item in filtered)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

                var qty = new TextBox
                {
                    Text = (_quantities.TryGetValue(item.Id, out int kept) ? kept : item.Quantity).ToString(),
                    Width = 40,
                    Height = 24,
                    TextAlignment = TextAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = (Brush)FindResource("B.Slate"),
                    Foreground = (Brush)FindResource("B.Ink"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    IsEnabled = false,
                    Tag = item
                };

                var check = new CheckBox
                {
                    Content = BuildItemText(item),
                    Tag = item,
                    IsChecked = _selected.Any(x => x.Id == item.Id),
                    Cursor = Cursors.Hand,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                qty.IsEnabled = check.IsChecked == true;
                qty.TextChanged += (s, e) =>
                {
                    if (!(qty.IsEnabled)) return;
                    if (int.TryParse(qty.Text, out int n))
                    {
                        n = Math.Max(1, Math.Min(item.Quantity, n));
                        _quantities[item.Id] = n;
                        RefreshRight();
                    }
                };
                check.Checked += (s, e) =>
                {
                    if (!_selected.Any(x => x.Id == item.Id)) _selected.Add(item);
                    qty.IsEnabled = true;
                    if (!int.TryParse(qty.Text, out int n) || n < 1 || n > item.Quantity)
                        qty.Text = item.Quantity.ToString();
                    _quantities[item.Id] = Math.Max(1, Math.Min(item.Quantity,
                        int.TryParse(qty.Text, out int q) ? q : item.Quantity));
                    RefreshRight();
                };
                check.Unchecked += (s, e) =>
                {
                    _selected.RemoveAll(x => x.Id == item.Id);
                    _quantities.Remove(item.Id);
                    qty.IsEnabled = false;
                    RefreshRight();
                };

                Grid.SetColumn(check, 0);
                row.Children.Add(check);
                Grid.SetColumn(qty, 1);
                row.Children.Add(qty);
                _leftList.Children.Add(row);
            }

            if (filtered.Count == 0)
                _leftList.Children.Add(new TextBlock { Text = "Ничего не найдено", Foreground = (Brush)FindResource("B.Mute") });
            RefreshRight();
        }

        private static StackPanel BuildItemText(ItemData item)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = item.Name ?? "(без названия)", FontSize = 13 });
            string sub = (item.Category ?? "") + (item.Quantity > 1 ? " · " + item.Quantity + " шт." : "");
            if (sub.Trim().Length > 0)
                stack.Children.Add(new TextBlock { Text = sub.Trim(), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xB4)) });
            return stack;
        }

        private void RefreshRight()
        {
            _rightList.Children.Clear();
            if (_incoming == null)
            {
                _rightList.Children.Add(new TextBlock
                {
                    Text = _selected.Count == 0 ? "Отметьте предметы слева" : "Будет передано:",
                    Foreground = (Brush)FindResource("B.Mute"),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                foreach (var item in _selected)
                    _rightList.Children.Add(new TextBlock
                    {
                        Text = "• " + item.Name + " ×" + SelectedQuantity(item),
                        FontSize = 13,
                        Foreground = (Brush)FindResource("B.Ink"),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                int g = ParseMoney(_gold), e = ParseMoney(_electrum), s = ParseMoney(_silver), c = ParseMoney(_copper);
                if (g + e + s + c > 0)
                    _rightList.Children.Add(new TextBlock
                    {
                        Text = $"Монеты: {g} з. · {e} э. · {s} с. · {c} м.",
                        FontSize = 12,
                        Foreground = (Brush)FindResource("B.Accent"),
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                return;
            }

            foreach (var item in _items.OrderBy(i => i.Name))
                _rightList.Children.Add(new TextBlock
                {
                    Text = "• " + item.Name + (item.Quantity > 1 ? " ×" + item.Quantity : ""),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("B.Mute"),
                    Margin = new Thickness(0, 1, 0, 0)
                });
        }

        private void ShowIncomingOffer()
        {
            _search.IsEnabled = false;
            _category.IsEnabled = false;
            _sort.IsEnabled = false;
            _gold.IsEnabled = false;
            _electrum.IsEnabled = false;
            _silver.IsEnabled = false;
            _copper.IsEnabled = false;
            _status.Text = "Проверьте предметы и нажмите «Принять». Предметы перейдут вам только после подтверждения.";
            RefreshLeft();
            RefreshRight();
        }

        private void OnPrimary(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            _primary.IsEnabled = false;
            if (_incoming == null) SendOffer();
            else AcceptOffer();
        }

        private void SendOffer()
        {
            if (_other == null || string.IsNullOrWhiteSpace(_other.Id))
            {
                Fail("Получатель не выбран.");
                return;
            }
            if (_selected.Count == 0 && ParseMoney(_gold) + ParseMoney(_electrum) + ParseMoney(_silver) + ParseMoney(_copper) == 0)
            {
                Fail("Выберите предметы или укажите монеты.");
                return;
            }

            var offer = new TradeMessage
            {
                TradeId = Guid.NewGuid().ToString("N"),
                Kind = "offer",
                FromPlayerId = _room.MyPlayerId,
                FromName = _room.GetPlayers().FirstOrDefault(p => p.Id == _room.MyPlayerId)?.CharacterName
                           ?? _room.GetPlayers().FirstOrDefault(p => p.Id == _room.MyPlayerId)?.Name ?? "Игрок",
                ToPlayerId = _other.Id,
                ToName = _other.CharacterName ?? _other.Name,
                Items = _selected.Select(i => ToTradeItem(i, SelectedQuantity(i))).ToList(),
                Gold = ParseMoney(_gold),
                Electrum = ParseMoney(_electrum),
                Silver = ParseMoney(_silver),
                Copper = ParseMoney(_copper)
            };
            _pending = offer;
            _room.SendTrade(offer);
            _status.Text = "Предложение отправлено. Ждём подтверждения…";
            StartTimeout(60, "Получатель не ответил — предложение отменено.");
        }

        private void AcceptOffer()
        {
            var accept = new TradeMessage
            {
                TradeId = _incoming.TradeId,
                Kind = "accept",
                Accepted = true,
                FromPlayerId = _room.MyPlayerId,
                FromName = _room.GetPlayers().FirstOrDefault(p => p.Id == _room.MyPlayerId)?.CharacterName
                           ?? _room.GetPlayers().FirstOrDefault(p => p.Id == _room.MyPlayerId)?.Name ?? "Игрок",
                ToPlayerId = _incoming.FromPlayerId,
                ToName = _incoming.FromName
            };
            _room.ClearPendingTrade(_incoming.TradeId);
            _accepted = true;
            _room.SendTrade(accept);
            _status.Text = "Подтверждено. Ждём, пока отправитель передаст предметы…";
            StartTimeout(20, "Отправитель не передал предметы — возможно, он закрыл окно.");
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (_incoming != null)
            {
                _room.SendTrade(new TradeMessage
                {
                    TradeId = _incoming.TradeId,
                    Kind = "decline",
                    FromPlayerId = _room.MyPlayerId,
                    ToPlayerId = _incoming.FromPlayerId,
                    ToName = _incoming.FromName
                });
            }
            else if (_pending != null)
            {
                _room.SendTrade(new TradeMessage
                {
                    TradeId = _pending.TradeId,
                    Kind = "cancel",
                    FromPlayerId = _room.MyPlayerId,
                    ToPlayerId = _pending.ToPlayerId,
                    ToName = _pending.ToName
                });
            }
            _closeSent = true;
            Close();
        }

        private void OnTrade(TradeMessage msg)
        {
            if (msg == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!string.Equals(msg.TradeId, _incoming?.TradeId ?? _pending?.TradeId,
                        StringComparison.Ordinal)) return;

                StopTimeout();
                if (_incoming == null && string.Equals(msg.Kind, "accept", StringComparison.Ordinal))
                {
                    CommitTransfer();
                }
                else if (_incoming == null && (string.Equals(msg.Kind, "decline", StringComparison.Ordinal) ||
                                                string.Equals(msg.Kind, "cancel", StringComparison.Ordinal)))
                {
                    Fail("Получатель отклонил передачу.");
                }
                else if (_incoming != null && string.Equals(msg.Kind, "commit", StringComparison.Ordinal))
                {
                    ReceiveTransfer(msg);
                }
                else if (_incoming != null && string.Equals(msg.Kind, "cancel", StringComparison.Ordinal))
                {
                    Fail("Отправитель отменил передачу.");
                }
            }));
        }

        private void CommitTransfer()
        {
            if (_pending == null) return;
            string error = null;
            foreach (var item in _pending.Items ?? new List<TradeItem>())
            {
                var source = _inventory.GetItem(item.SourceItemId);
                if (source == null || source.Quantity < Math.Max(1, item.Quantity))
                {
                    error = "Предмет «" + item.Name + "» уже изменился — передача отменена.";
                    break;
                }
            }

            var money = _money.LoadMoney();
            if (money.Gold < _pending.Gold || money.Electrum < _pending.Electrum ||
                money.Silver < _pending.Silver || money.Copper < _pending.Copper)
                error = "Недостаточно монет — передача отменена.";

            if (error != null)
            {
                _room.SendTrade(new TradeMessage
                {
                    TradeId = _pending.TradeId,
                    Kind = "cancel",
                    FromPlayerId = _room.MyPlayerId,
                    ToPlayerId = _pending.ToPlayerId,
                    ToName = _pending.ToName,
                    Error = error
                });
                Fail(error);
                return;
            }

            foreach (var item in _pending.Items ?? new List<TradeItem>())
            {
                if (!_inventory.DeleteItemWithUnequip(item.SourceItemId, Math.Max(1, item.Quantity), out string itemError))
                {
                    Fail(itemError ?? "Не удалось убрать предмет из инвентаря.");
                    return;
                }
            }

            money.Gold -= _pending.Gold;
            money.Electrum -= _pending.Electrum;
            money.Silver -= _pending.Silver;
            money.Copper -= _pending.Copper;
            _money.SaveMoney(money);

            _completed = true;
            StopTimeout();
            _pending.Kind = "commit";
            _room.SendTrade(_pending);
            _status.Text = "Передано. Получатель получит предметы.";
            _primary.IsEnabled = false;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (s, e) => { timer.Stop(); Close(); };
            timer.Start();
        }

        private void ReceiveTransfer(TradeMessage msg)
        {
            try
            {
                foreach (var item in msg.Items ?? new List<TradeItem>())
                {
                    var data = ToItemData(item);
                    _inventory.AddOrStack(data);
                }

                var money = _money.LoadMoney();
                money.Gold += msg.Gold;
                money.Electrum += msg.Electrum;
                money.Silver += msg.Silver;
                money.Copper += msg.Copper;
                _money.SaveMoney(money);

                _completed = true;
                StopTimeout();
                _status.Text = "Предметы и монеты получены.";
                _primary.IsEnabled = false;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                timer.Tick += (s, e) => { timer.Stop(); Close(); };
                timer.Start();
            }
            catch (Exception ex)
            {
                Fail("Не удалось принять предметы: " + ex.Message);
            }
        }

        private void Fail(string text)
        {
            _busy = false;
            _primary.IsEnabled = true;
            _status.Text = text;
            _status.Foreground = (Brush)FindResource("B.Danger");
        }

        private void StartTimeout(int seconds, string message)
        {
            StopTimeout();
            _waitTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };
            _waitTimer.Tick += (s, e) =>
            {
                StopTimeout();
                if (_completed) return;
                Fail(message);
                Close();
            };
            _waitTimer.Start();
        }

        private void StopTimeout()
        {
            if (_waitTimer == null) return;
            _waitTimer.Stop();
            _waitTimer = null;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _room.TradeReceived -= OnTrade;
            StopTimeout();
            if (_completed || _closeSent) return;
            _closeSent = true;

            if (_incoming != null)
            {
                _room.SendTrade(new TradeMessage
                {
                    TradeId = _incoming.TradeId,
                    Kind = _accepted ? "cancel" : "decline",
                    FromPlayerId = _room.MyPlayerId,
                    ToPlayerId = _incoming.FromPlayerId,
                    ToName = _incoming.FromName
                });
            }
            else if (_incoming == null && _pending != null)
            {
                _room.SendTrade(new TradeMessage
                {
                    TradeId = _pending.TradeId,
                    Kind = "cancel",
                    FromPlayerId = _room.MyPlayerId,
                    ToPlayerId = _pending.ToPlayerId,
                    ToName = _pending.ToName
                });
            }
        }

        private static int ParseMoney(TextBox box)
        {
            return int.TryParse((box.Text ?? "").Trim(), out int value) ? Math.Max(0, Math.Min(999999, value)) : 0;
        }

        private int SelectedQuantity(ItemData item)
            => _quantities.TryGetValue(item.Id, out int q)
                ? Math.Max(1, Math.Min(item.Quantity, q))
                : Math.Max(1, item.Quantity);

        private static TradeItem ToTradeItem(ItemData item, int quantity) => new TradeItem
        {
            SourceItemId = item.Id,
            Name = item.Name ?? "",
            Category = item.Category ?? "",
            Description = item.Description ?? "",
            Quantity = Math.Max(1, quantity),
            Weight = item.Weight,
            CostGold = item.CostGold,
            CostSilver = item.CostSilver,
            CostCopper = item.CostCopper,
            DamageDice = item.DamageDice ?? "",
            DamageType = item.DamageType ?? "",
            ArmorClassBonus = item.ArmorClassBonus,
            IsAttuned = item.IsAttuned,
            Charges = item.Charges,
            MaxCharges = item.MaxCharges,
            Recharge = item.Recharge ?? "",
            RechargeDice = item.RechargeDice ?? "",
            Rarity = item.Rarity ?? "",
            Notes = item.Notes ?? "",
            PhotoPath = item.PhotoPath ?? "",
            ListPhotoPath = item.ListPhotoPath ?? ""
        };

        private static ItemData ToItemData(TradeItem item) => new ItemData
        {
            Name = item.Name ?? "",
            Category = item.Category ?? "",
            Description = item.Description ?? "",
            Quantity = Math.Max(1, item.Quantity),
            Weight = item.Weight,
            CostGold = item.CostGold,
            CostSilver = item.CostSilver,
            CostCopper = item.CostCopper,
            DamageDice = item.DamageDice ?? "",
            DamageType = item.DamageType ?? "",
            ArmorClassBonus = item.ArmorClassBonus,
            IsAttuned = item.IsAttuned,
            Charges = item.Charges,
            MaxCharges = item.MaxCharges,
            Recharge = item.Recharge ?? ItemData.RechargeLong,
            RechargeDice = item.RechargeDice ?? "",
            Rarity = item.Rarity ?? "",
            Notes = item.Notes ?? "",
            PhotoPath = item.PhotoPath ?? "",
            ListPhotoPath = item.ListPhotoPath ?? ""
        };
    }
}
