using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Передача предметов и монет другому игроку.
    ///
    /// Инициатор выбирает предметы в своём инвентаре, получатель видит
    /// предложение и подтверждает. Перемещение в базе делает каждая сторона
    /// у себя: у игроков нет чужой базы, а по сети идут только снимки предметов.
    ///
    /// Окно собрано кодом, а не разметкой, но целиком на токенах темы: цвета
    /// берутся из B.*, шрифты из F.*, флажки — общий CustomCheckBox, списки —
    /// EditorComboBox. Своего titlebar нет: шапка нарисована внутри, иначе
    /// светлая полоса Windows выбивалась бы из тёмного окна.
    ///
    /// Раскладка повторяет смысл сделки:
    ///   отправитель — слева свой инвентарь, справа корзина передачи;
    ///   получатель  — слева предложение, справа свой инвентарь.
    /// </summary>
    public sealed class TradeWindow : Window
    {
        private const double DesignWidth = 1140;
        private const double DesignHeight = 730;

        private readonly int _characterId;
        private readonly PlayerInfo _other;
        private readonly TradeMessage _incoming;
        private readonly RoomSession _room = RoomSession.Instance;
        private readonly InventoryService _inventory;
        private readonly MoneyService _money;
        private readonly bool _isSender;

        private readonly List<ItemData> _items = new List<ItemData>();
        private readonly List<ItemData> _selected = new List<ItemData>();
        private readonly Dictionary<int, int> _quantities = new Dictionary<int, int>();

        private readonly StackPanel _leftList = new StackPanel();
        private readonly StackPanel _rightList = new StackPanel();

        private readonly TextBox _search = new TextBox();
        private readonly ComboBox _category = new ComboBox();
        private readonly ComboBox _sort = new ComboBox();

        /// <summary>Поля монет: платина, золото, электрум, серебро, медь.</summary>
        private readonly TextBox[] _coinBoxes = new TextBox[5];
        private readonly Border[] _coinCells = new Border[5];
        private readonly int[] _coinOwned = new int[5];
        private readonly string[] _coinShort = { "п.", "з.", "э.", "с.", "м." };

        private CharacterMoney _moneyOwned = new CharacterMoney();
        private TextBlock _balance;
        private TextBlock _balanceLeft;
        private TextBlock _leftHint;
        private TextBlock _rightHint;
        private TextBlock _basketNote;
        private TextBlock _status;
        private Border _leftHost;
        private bool _coinsSilent;

        private readonly Button _primary;
        private readonly Button _cancel;

        private TradeMessage _pending;
        private bool _accepted;
        private bool _completed;
        private bool _closeSent;
        private bool _commitSent;
        private bool _busy;
        private bool _locked;
        private DispatcherTimer _waitTimer;

        /// <summary>
        /// Уже полученные передачи. Сделка может прийти дважды (повтор кадра,
        /// переподключение), а предмет — один: второй раз тот же номер не применяем.
        /// </summary>
        private static readonly HashSet<string> AppliedCommits =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly object AppliedLock = new object();

        public TradeWindow(Window owner, int characterId, PlayerInfo other, TradeMessage incoming = null)
        {
            _characterId = characterId;
            _other = other;
            _incoming = incoming;
            _isSender = incoming == null;
            _inventory = new InventoryService(characterId);
            _money = new MoneyService(characterId);

            Title = _isSender ? "Передать предметы" : "Входящий обмен";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            Owner = owner;
            FontFamily = Ff("F.Body");
            Foreground = Br("B.Ink");
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            _primary = new Button
            {
                Content = PrimaryContent(),
                Style = St("MagicBtn"),
                MinWidth = 134,
                Height = 36,
                Padding = new Thickness(18, 0, 18, 0),
                Margin = new Thickness(10, 0, 0, 0),
                IsDefault = true,
                Cursor = Cursors.Hand
            };
            _primary.Click += OnPrimary;

            _cancel = new Button
            {
                Content = _isSender ? "Отмена" : "Отклонить",
                Style = St("GhostBtn"),
                MinWidth = 108,
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                Cursor = Cursors.Hand
            };
            _cancel.Click += OnCancel;

            Build();
            LoadInventory();
            LoadMoney();

            _room.TradeReceived += OnTrade;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;

            if (_isSender)
            {
                SetStatus("Отметьте предметы слева, при желании укажите монеты и нажмите «Предложить».", Br("B.Mute"));
                UpdateBasket();
            }
            else
            {
                SetStatus("Проверьте предложение. Предметы перейдут вам только после подтверждения.", Br("B.Mute"));
                RefreshLeft();
                UpdateOwnList();
            }
            UpdatePrimaryState();
        }

        #region === РЕСУРСЫ ТЕМЫ ===

        private Brush Br(string key) => TryFindResource(key) as Brush ?? Brushes.Transparent;

        private FontFamily Ff(string key) => TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

        private Style St(string key) => TryFindResource(key) as Style;

        private static string Plural(int count, string one, string few, string many)
        {
            int mod100 = count % 100;
            int mod10 = count % 10;
            if (mod100 >= 11 && mod100 <= 14) return count + " " + many;
            if (mod10 == 1) return count + " " + one;
            if (mod10 >= 2 && mod10 <= 4) return count + " " + few;
            return count + " " + many;
        }

        private static string CoinsText(int[] values)
            => $"{values[0]} п. · {values[1]} з. · {values[2]} э. · {values[3]} с. · {values[4]} м.";

        #endregion

        #region === СБОРКА ОКНА ===

        private void Build()
        {
            // Масштаб интерфейса общий для приложения. SizeToContent считает
            // размер уже вместе с LayoutTransform, поэтому окно не «висит»
            // в пустом поле при масштабе 70 %.
            double scale = Math.Max(0.7, UiScaleManager.CurrentPercent / 100.0);
            double width = Math.Min(DesignWidth, (SystemParameters.WorkArea.Width - 60) / scale);
            double height = Math.Min(DesignHeight, (SystemParameters.WorkArea.Height - 90) / scale);
            width = Math.Max(880, width);
            height = Math.Max(580, height);

            var shell = new Grid { Width = width, Height = height };
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            shell.Children.Add(BuildHeader());

            var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = _isSender ? (UIElement)BuildInventoryPanel(interactive: true) : BuildOfferPanel();
            body.Children.Add(left);

            var right = _isSender ? (UIElement)BuildBasketPanel() : BuildInventoryPanel(interactive: false);
            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            Grid.SetRow(body, 1);
            shell.Children.Add(body);

            var footer = BuildFooter();
            Grid.SetRow(footer, 2);
            shell.Children.Add(footer);

            Content = new Border
            {
                Background = Br("B.Panel"),
                BorderBrush = Br("B.AccentEdge"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                LayoutTransform = new ScaleTransform(scale, scale),
                Margin = new Thickness(12),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 32,
                    ShadowDepth = 0,
                    Opacity = 0.65
                },
                Child = new Border
                {
                    Padding = new Thickness(20, 16, 20, 16),
                    Child = shell
                }
            };
        }

        private UIElement BuildHeader()
        {
            var head = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(12),
                Background = Br("B.AccentFaint"),
                BorderBrush = Br("B.AccentSoft"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new IconView
                {
                    Kind = "swap",
                    IconSize = 20,
                    IconBrush = Br("B.Accent"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            head.Children.Add(badge);

            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock
            {
                Text = _isSender ? "ПЕРЕДАТЬ ПРЕДМЕТЫ" : "ВХОДЯЩИЙ ОБМЕН",
                FontFamily = Ff("F.Display"),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = Br("B.Accent")
            });
            titles.Children.Add(new TextBlock
            {
                Text = BuildSubtitle(),
                FontSize = 11.5,
                Foreground = Br("B.Mute"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);

            var close = new Button
            {
                Content = new IconView { Kind = "close", IconSize = 13, IconBrush = Br("B.Mute") },
                Style = St("GhostCloseBtn"),
                Width = 32,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Закрыть"
            };
            close.Click += (s, e) => OnCancel(null, null);
            Grid.SetColumn(close, 2);
            head.Children.Add(close);

            head.MouseLeftButtonDown += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                try { DragMove(); } catch { }
            };

            return head;
        }

        private string BuildSubtitle()
        {
            string who = DisplayName(_other);
            var parts = new List<string>();
            if (_other != null && !string.IsNullOrWhiteSpace(_other.CharacterClass))
                parts.Add(_other.CharacterClass.Trim());
            if (_other != null && _other.Level > 0)
                parts.Add(_other.Level + " ур.");
            string detail = string.Join(" · ", parts);
            string prefix = _isSender ? "Кому" : "От";
            return detail.Length > 0 ? $"{prefix}: {who} · {detail}" : $"{prefix}: {who}";
        }

        private static string DisplayName(PlayerInfo p)
        {
            string character = (p?.CharacterName ?? "").Trim();
            if (character.Length > 0) return character;
            string name = (p?.Name ?? "").Trim();
            return name.Length > 0 ? name : "игрок";
        }

        private StackPanel PrimaryContent()
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new IconView
            {
                Kind = _isSender ? "swap" : "check",
                IconSize = 14,
                IconBrush = Br("B.OnAccent"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = _isSender ? "Предложить" : "Принять",
                FontFamily = Ff("F.Display"),
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = Br("B.OnAccent"),
                VerticalAlignment = VerticalAlignment.Center
            });
            return stack;
        }

        private Border PanelFrame()
        {
            return new Border
            {
                Background = Br("B.Slate"),
                BorderBrush = Br("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12)
            };
        }

        private UIElement PanelHead(string title, out TextBlock hint, string hintText)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = Ff("F.Display"),
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = Br("B.Accent"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            hint = new TextBlock
            {
                Text = hintText ?? "",
                FontSize = 10.5,
                Foreground = Br("B.Mute"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 1, 0, 0)
            };
            Grid.SetColumn(hint, 1);
            grid.Children.Add(hint);
            return grid;
        }

        private UIElement BuildInventoryPanel(bool interactive)
        {
            var frame = PanelFrame();
            var grid = new Grid();

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (interactive) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            if (interactive) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int row = 0;
            TextBlock hint;
            var head = PanelHead("ВАШ ИНВЕНТАРЬ", out hint, "");
            Grid.SetRow(head, row++);
            grid.Children.Add(head);

            if (interactive)
            {
                _leftHint = hint;
                var filters = BuildFilters();
                Grid.SetRow(filters, row++);
                grid.Children.Add(filters);
            }
            else
            {
                _rightHint = hint;
            }

            var scroll = new ScrollViewer
            {
                Content = interactive ? (UIElement)_leftList : _rightList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(scroll, row++);
            grid.Children.Add(scroll);

            if (interactive)
            {
                var money = BuildMoneySection();
                Grid.SetRow(money, row++);
                grid.Children.Add(money);
            }
            else
            {
                _balance = new TextBlock
                {
                    Text = "",
                    FontSize = 11,
                    Foreground = Br("B.Ink"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                Grid.SetRow(_balance, row++);
                grid.Children.Add(_balance);
            }

            if (interactive)
            {
                _leftHost = frame;
            }

            frame.Child = grid;
            return frame;
        }

        private UIElement BuildFilters()
        {
            var filters = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchStyle = St("SearchBox");
            if (searchStyle != null) _search.Style = searchStyle;
            _search.Tag = "Найти предмет";
            _search.Height = 34;
            _search.MinWidth = 110;
            _search.FontSize = 12.5;
            _search.VerticalContentAlignment = VerticalAlignment.Center;
            _search.TextChanged += (s, e) => RefreshLeft();
            filters.Children.Add(_search);

            PrepareCombo(_category, 146, 8);
            _category.SelectionChanged += (s, e) => RefreshLeft();
            Grid.SetColumn(_category, 1);
            filters.Children.Add(_category);

            PrepareCombo(_sort, 126, 6);
            _sort.Items.Add("По названию");
            _sort.Items.Add("По категории");
            _sort.Items.Add("По цене");
            _sort.SelectedIndex = 0;
            _sort.SelectionChanged += (s, e) => RefreshLeft();
            Grid.SetColumn(_sort, 2);
            filters.Children.Add(_sort);

            return filters;
        }

        private void PrepareCombo(ComboBox combo, double width, double leftMargin)
        {
            var style = St("EditorComboBox");
            if (style != null) combo.Style = style;
            combo.Width = width;
            combo.Height = 34;
            combo.MinHeight = 0;
            combo.FontSize = 12;
            combo.Margin = new Thickness(leftMargin, 0, 0, 0);
            combo.VerticalAlignment = VerticalAlignment.Center;
        }

        private UIElement BuildMoneySection()
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

            var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Text = "МОНЕТЫ",
                FontFamily = Ff("F.Display"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = Br("B.Accent")
            });
            var hint = new TextBlock
            {
                Text = "сколько отдаёте",
                FontSize = 10,
                Foreground = Br("B.Mute"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hint, 1);
            head.Children.Add(hint);
            wrap.Children.Add(head);

            var grid = new Grid();
            for (int i = 0; i < 5; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labels = new[] { "ПЛАТИНА", "ЗОЛОТО", "ЭЛЕКТРУМ", "СЕРЕБРО", "МЕДЬ" };
            for (int i = 0; i < 5; i++)
            {
                var box = new TextBox
                {
                    Text = "0",
                    TextAlignment = TextAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = Br("B.Ink"),
                    CaretBrush = Br("B.Accent"),
                    FontSize = 13,
                    Padding = new Thickness(0),
                    Height = 26,
                    MinWidth = 0,
                    Width = double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var numStyle = St("NumField");
                if (numStyle != null) box.Style = numStyle;
                box.Text = "0";
                box.TextChanged += (s, e) => OnCoinChanged();

                var cell = new Border
                {
                    Background = Br("B.Panel"),
                    BorderBrush = Br("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(3, 4, 3, 2),
                    Margin = new Thickness(i == 0 ? 0 : 3, 0, i == 4 ? 0 : 3, 0)
                };
                var cellStack = new StackPanel();
                cellStack.Children.Add(new TextBlock
                {
                    Text = labels[i],
                    FontSize = 8.5,
                    Foreground = Br("B.Mute"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 0, 2)
                });
                cellStack.Children.Add(box);
                cell.Child = cellStack;

                _coinBoxes[i] = box;
                _coinCells[i] = cell;
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            wrap.Children.Add(grid);

            _balance = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = Br("B.Ink"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            _balanceLeft = new TextBlock
            {
                Text = "",
                FontSize = 10.5,
                Foreground = Br("B.Mute"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            wrap.Children.Add(_balance);
            wrap.Children.Add(_balanceLeft);
            return wrap;
        }

        private UIElement BuildBasketPanel()
        {
            var frame = PanelFrame();
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock hint;
            var head = PanelHead("КОРЗИНА ПЕРЕДАЧИ", out hint, "");
            _rightHint = hint;
            grid.Children.Add(head);

            var scroll = new ScrollViewer
            {
                Content = _rightList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            _basketNote = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = Br("B.Mute"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(_basketNote, 2);
            grid.Children.Add(_basketNote);

            frame.Child = grid;
            return frame;
        }

        private UIElement BuildOfferPanel()
        {
            var frame = PanelFrame();
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string who = DisplayName(_other).ToUpperInvariant();
            TextBlock hint;
            // Без предлога: «предложение от Гром» режет слух, а склонять чужие
            // имена по падежам — заведомо промахнуться.
            grid.Children.Add(PanelHead("ПРЕДЛОЖЕНИЕ · " + who, out hint, ""));
            _leftHint = hint;

            var scroll = new ScrollViewer
            {
                Content = _leftList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var note = new TextBlock
            {
                Text = "Списание и зачисление идут только после подтверждения. Закрытие окна отменяет обмен.",
                FontSize = 10.5,
                Foreground = Br("B.Mute"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Opacity = 0.85
            };
            Grid.SetRow(note, 2);
            grid.Children.Add(note);

            frame.Child = grid;
            return frame;
        }

        private UIElement BuildFooter()
        {
            var wrap = new StackPanel();
            wrap.Children.Add(new Border
            {
                Height = 1,
                Background = Br("B.Line"),
                Margin = new Thickness(0, 0, 0, 12),
                Opacity = 0.8
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _status = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                Foreground = Br("B.Mute"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            grid.Children.Add(_status);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            buttons.Children.Add(_cancel);
            buttons.Children.Add(_primary);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            wrap.Children.Add(grid);
            return wrap;
        }

        #endregion

        #region === ДАННЫЕ ===

        private void LoadInventory()
        {
            _items.Clear();
            try
            {
                var table = _inventory.GetFilteredInventory();
                foreach (System.Data.DataRow row in table.Rows)
                {
                    int id = Convert.ToInt32(row["id_item"]);
                    var item = _inventory.GetItem(id);
                    if (item != null) _items.Add(item);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось прочитать инвентарь: " + ex.Message, Br("B.Danger"));
            }

            if (_isSender)
            {
                _category.Items.Clear();
                _category.Items.Add("Все категории");
                foreach (string c in _items.Select(i => i.Category ?? "").Where(c => c.Length > 0)
                             .Distinct().OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase))
                    _category.Items.Add(c);
                _category.SelectedIndex = 0;
                RefreshLeft();
            }
        }

        private void LoadMoney()
        {
            try
            {
                _moneyOwned = _money.LoadMoney() ?? new CharacterMoney();
            }
            catch (Exception ex)
            {
                _moneyOwned = new CharacterMoney();
                Debug.WriteLine("[Trade] монеты: " + ex.Message);
            }

            _coinOwned[0] = Math.Max(0, _moneyOwned.Platinum);
            _coinOwned[1] = Math.Max(0, _moneyOwned.Gold);
            _coinOwned[2] = Math.Max(0, _moneyOwned.Electrum);
            _coinOwned[3] = Math.Max(0, _moneyOwned.Silver);
            _coinOwned[4] = Math.Max(0, _moneyOwned.Copper);

            if (_balance != null) _balance.Text = "У вас: " + CoinsText(_coinOwned);
            if (_balanceLeft != null) _balanceLeft.Text = "Останется: " + CoinsText(_coinOwned);
            UpdateCoinsVisual();
        }

        private int[] MoneyValues()
        {
            var values = new int[5];
            for (int i = 0; i < 5; i++)
            {
                var box = _coinBoxes[i];
                values[i] = box != null && int.TryParse((box.Text ?? "").Trim(), out int n)
                    ? Math.Max(0, Math.Min(999999, n))
                    : 0;
            }
            return values;
        }

        private int SelectedQuantity(ItemData item)
        {
            int max = Math.Max(1, item.Quantity);
            return _quantities.TryGetValue(item.Id, out int q) ? Math.Max(1, Math.Min(max, q)) : max;
        }

        #endregion

        #region === СПИСКИ ===

        private void RefreshLeft()
        {
            _leftList.Children.Clear();

            if (!_isSender)
            {
                BuildOfferCards();
                return;
            }

            string query = (_search.Text ?? "").Trim();
            string category = _category.SelectedItem as string ?? "Все категории";

            var filtered = _items.Where(i =>
                    (query.Length == 0 ||
                     (i.Name ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                     (i.Category ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0) &&
                    (category == "Все категории" ||
                     string.Equals(i.Category, category, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();

            if (_sort.SelectedIndex == 1) filtered = filtered.OrderBy(i => i.Category).ThenBy(i => i.Name).ToList();
            else if (_sort.SelectedIndex == 2) filtered = filtered.OrderByDescending(i => i.CostGold).ThenBy(i => i.Name).ToList();
            else filtered = filtered.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            foreach (var item in filtered)
                _leftList.Children.Add(BuildInventoryRow(item));

            if (filtered.Count == 0)
            {
                _leftList.Children.Add(EmptyState("Ничего не найдено", "Измените запрос или категорию."));
            }

            if (_leftHint != null)
            {
                _leftHint.Text = filtered.Count == _items.Count
                    ? Plural(_items.Count, "предмет", "предмета", "предметов")
                    : $"{filtered.Count} из {_items.Count}";
            }

            UpdateBasket();
        }

        private void BuildOfferCards()
        {
            var items = _incoming?.Items ?? new List<TradeItem>();

            foreach (var trade in items)
            {
                var card = new Border
                {
                    Background = Br("B.Panel"),
                    BorderBrush = Br("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(9, 7, 8, 7),
                    Margin = new Thickness(0, 0, 0, 5)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(trade.Name) ? "(без названия)" : trade.Name,
                    FontSize = 12.5,
                    Foreground = Br("B.Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                };
                name.ToolTip = name.Text;
                text.Children.Add(name);

                string sub = TradeCaption(trade);
                if (sub.Length > 0)
                {
                    text.Children.Add(new TextBlock
                    {
                        Text = sub,
                        FontSize = 10,
                        Foreground = Br("B.Mute"),
                        Margin = new Thickness(0, 2, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                }
                grid.Children.Add(text);

                var badge = new Border
                {
                    Background = Br("B.AccentFaint"),
                    BorderBrush = Br("B.AccentSoft"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "×" + Math.Max(1, trade.Quantity),
                        FontSize = 11.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = Br("B.Accent")
                    }
                };
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);

                card.Child = grid;
                _leftList.Children.Add(card);
            }

            int p = Math.Max(0, _incoming?.Platinum ?? 0);
            int g = Math.Max(0, _incoming?.Gold ?? 0);
            int e = Math.Max(0, _incoming?.Electrum ?? 0);
            int s = Math.Max(0, _incoming?.Silver ?? 0);
            int c = Math.Max(0, _incoming?.Copper ?? 0);

            if (p + g + e + s + c > 0)
            {
                var moneyCard = new Border
                {
                    Background = Br("B.Panel"),
                    BorderBrush = Br("B.AccentDim"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(9, 8, 9, 8),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new IconView
                {
                    Kind = "coin",
                    IconSize = 15,
                    IconBrush = Br("B.Accent"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                row.Children.Add(new TextBlock
                {
                    Text = "Монеты: " + CoinsText(new[] { p, g, e, s, c }),
                    FontSize = 11.5,
                    Foreground = Br("B.Ink"),
                    TextWrapping = TextWrapping.Wrap
                });
                moneyCard.Child = row;
                _leftList.Children.Add(moneyCard);
            }

            if (items.Count == 0 && p + g + e + s + c == 0)
                _leftList.Children.Add(EmptyState("Пустое предложение", "Отправитель ничего не выбрал."));

            if (_leftHint != null)
            {
                int total = items.Count;
                _leftHint.Text = p + g + e + s + c > 0
                    ? Plural(total, "предмет", "предмета", "предметов") + " и монеты"
                    : Plural(total, "предмет", "предмета", "предметов");
            }
        }

        private UIElement BuildInventoryRow(ItemData item)
        {
            bool picked = _selected.Any(x => x.Id == item.Id);
            int max = Math.Max(1, item.Quantity);

            var row = new Border
            {
                Background = picked ? Br("B.PanelHi") : Br("B.Panel"),
                BorderBrush = picked ? Br("B.AccentDim") : Br("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 6, 6, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.Name) ? "(без названия)" : item.Name,
                FontSize = 12.5,
                Foreground = Br("B.Ink"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            name.ToolTip = name.Text;
            text.Children.Add(name);

            string sub = ItemCaption(item);
            if (sub.Length > 0)
            {
                text.Children.Add(new TextBlock
                {
                    Text = sub,
                    FontSize = 10,
                    Foreground = Br("B.Mute"),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var check = new CheckBox
            {
                Content = text,
                IsChecked = picked,
                Style = St("CustomCheckBox"),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Focusable = false
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            var qty = new TextBox
            {
                Text = (picked ? SelectedQuantity(item) : max).ToString(),
                Width = 34,
                Height = 22,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Br("B.Ink"),
                CaretBrush = Br("B.Accent"),
                FontSize = 12,
                Padding = new Thickness(0),
                IsReadOnly = !picked
            };
            var numStyle = St("NumField");
            if (numStyle != null) qty.Style = numStyle;
            qty.BorderThickness = new Thickness(0);
            qty.Background = Brushes.Transparent;
            qty.Width = 34;
            qty.Height = 22;
            qty.Padding = new Thickness(0);

            var stepperHost = new Border
            {
                Width = 96,
                Height = 28,
                CornerRadius = new CornerRadius(9),
                Background = Br("B.Slate"),
                BorderBrush = Br("B.Line"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = picked ? 1 : 0.45
            };

            var stepper = new Grid();
            stepper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            stepper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stepper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            void Store(int value)
            {
                int clamped = Math.Max(1, Math.Min(max, value));
                if (qty.Text != clamped.ToString())
                {
                    qty.Text = clamped.ToString();
                    qty.CaretIndex = qty.Text.Length;
                }
                _quantities[item.Id] = clamped;
                UpdateBasket();
            }

            var minus = StepButton("minus", () =>
            {
                if (check.IsChecked != true) return;
                int current = int.TryParse(qty.Text, out int parsed) ? parsed : max;
                Store(current - 1);
            });
            var plus = StepButton("plus", () =>
            {
                if (check.IsChecked != true) return;
                int current = int.TryParse(qty.Text, out int parsed) ? parsed : 1;
                Store(current + 1);
            });

            stepper.Children.Add(minus);
            Grid.SetColumn(qty, 1);
            stepper.Children.Add(qty);
            Grid.SetColumn(plus, 2);
            stepper.Children.Add(plus);
            stepperHost.Child = stepper;

            Grid.SetColumn(stepperHost, 1);
            grid.Children.Add(stepperHost);

            qty.TextChanged += (s, e) =>
            {
                if (check.IsChecked != true) return;
                if (!int.TryParse(qty.Text, out int n)) return;

                if (n > max)
                {
                    // Выше, чем лежит в инвентаре, поднять нельзя — режем сразу,
                    // а не «при отправке»: иначе в корзине видно то, чего нет.
                    qty.Text = max.ToString();
                    qty.CaretIndex = qty.Text.Length;
                    return;
                }
                if (n < 1)
                {
                    qty.Text = "1";
                    qty.CaretIndex = 1;
                    return;
                }
                _quantities[item.Id] = n;
                UpdateBasket();
            };

            qty.LostFocus += (s, e) =>
            {
                if (check.IsChecked != true) return;
                int n = int.TryParse(qty.Text, out int parsed) ? parsed : max;
                n = Math.Max(1, Math.Min(max, n));
                qty.Text = n.ToString();
                _quantities[item.Id] = n;
                UpdateBasket();
            };

            check.Checked += (s, e) =>
            {
                if (!_selected.Any(x => x.Id == item.Id)) _selected.Add(item);
                if (!_quantities.TryGetValue(item.Id, out int kept) || kept < 1 || kept > max)
                    _quantities[item.Id] = max;
                qty.IsReadOnly = false;
                qty.Text = _quantities[item.Id].ToString();
                row.Background = Br("B.PanelHi");
                row.BorderBrush = Br("B.AccentDim");
                stepperHost.Opacity = 1;
                UpdateBasket();
            };

            check.Unchecked += (s, e) =>
            {
                _selected.RemoveAll(x => x.Id == item.Id);
                _quantities.Remove(item.Id);
                qty.IsReadOnly = true;
                qty.Text = max.ToString();
                row.Background = Br("B.Panel");
                row.BorderBrush = Br("B.Line");
                stepperHost.Opacity = 0.45;
                UpdateBasket();
            };

            row.Child = grid;
            return row;
        }

        private Button StepButton(string icon, Action onClick)
        {
            var button = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2, 0, 2, 0),
                Style = St("MiniRound"),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                // 13, а не 10: на десяти точках плюс превращался в точку —
                // «+» и «−» должны читаться одинаково.
                Content = new IconView { Kind = icon, IconSize = 13, Thickness = 2.2, IconBrush = Br("B.Accent") },
                ToolTip = icon == "plus" ? "Больше" : "Меньше"
            };
            button.Click += (s, e) => onClick();
            return button;
        }

        private void UpdateBasket()
        {
            if (!_isSender) return;

            _rightList.Children.Clear();
            var ordered = _selected.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            foreach (var item in ordered)
            {
                int id = item.Id;
                var row = new Border
                {
                    Background = Br("B.Panel"),
                    BorderBrush = Br("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(9, 6, 6, 6),
                    Margin = new Thickness(0, 0, 0, 5)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Name) ? "(без названия)" : item.Name,
                    FontSize = 12,
                    Foreground = Br("B.Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                };
                name.ToolTip = name.Text;
                text.Children.Add(name);
                grid.Children.Add(text);

                var badge = new Border
                {
                    Background = Br("B.AccentFaint"),
                    BorderBrush = Br("B.AccentSoft"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(6, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "×" + SelectedQuantity(item),
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        Foreground = Br("B.Accent")
                    }
                };
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);

                var remove = new Button
                {
                    Width = 26,
                    Height = 26,
                    Style = St("GhostCloseBtn"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new IconView { Kind = "trash", IconSize = 12, IconBrush = Br("B.Mute") },
                    ToolTip = "Убрать из корзины"
                };
                remove.Click += (s, e) =>
                {
                    _selected.RemoveAll(x => x.Id == id);
                    _quantities.Remove(id);
                    RefreshLeft();
                };
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);

                row.Child = grid;
                _rightList.Children.Add(row);
            }

            var values = MoneyValues();
            int coinSum = values.Sum();
            if (coinSum > 0)
            {
                var moneyCard = new Border
                {
                    Background = Br("B.Panel"),
                    BorderBrush = Br("B.AccentDim"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(9, 8, 9, 8),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new IconView
                {
                    Kind = "coin",
                    IconSize = 15,
                    IconBrush = Br("B.Accent"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                row.Children.Add(new TextBlock
                {
                    Text = "Монеты: " + CoinsText(values),
                    FontSize = 11.5,
                    Foreground = Br("B.Ink"),
                    TextWrapping = TextWrapping.Wrap
                });
                moneyCard.Child = row;
                _rightList.Children.Add(moneyCard);
            }

            if (ordered.Count == 0 && coinSum == 0)
            {
                _rightList.Children.Add(EmptyState("Корзина пуста",
                    "Отметьте предметы в своём инвентаре слева — они появятся здесь."));
            }

            if (_rightHint != null)
                _rightHint.Text = ordered.Count > 0 ? Plural(ordered.Count, "предмет", "предмета", "предметов") : "";

            if (_basketNote != null)
            {
                if (ordered.Count == 0 && coinSum == 0)
                    _basketNote.Text = "";
                else if (ordered.Count == 0)
                    _basketNote.Text = "К передаче: монеты";
                else
                    _basketNote.Text = "К передаче: " + Plural(ordered.Count, "предмет", "предмета", "предметов")
                                       + (coinSum > 0 ? " и монеты" : "");
            }

            UpdatePrimaryState();
        }

        private void UpdateOwnList()
        {
            if (_isSender) return;

            _rightList.Children.Clear();
            foreach (var item in _items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new Border
                {
                    Background = Br("B.Panel"),
                    BorderBrush = Br("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var text = new StackPanel();
                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Name) ? "(без названия)" : item.Name,
                    FontSize = 11.5,
                    Foreground = Br("B.Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                name.ToolTip = name.Text;
                text.Children.Add(name);

                string sub = ItemCaption(item);
                if (sub.Length > 0)
                {
                    text.Children.Add(new TextBlock
                    {
                        Text = sub,
                        FontSize = 9.5,
                        Foreground = Br("B.Mute"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                }
                row.Child = text;
                _rightList.Children.Add(row);
            }

            if (_items.Count == 0)
                _rightList.Children.Add(EmptyState("Инвентарь пуст", "Пока сюда нечего положить."));

            if (_rightHint != null)
                _rightHint.Text = Plural(_items.Count, "предмет", "предмета", "предметов");
        }

        private UIElement EmptyState(string title, string hint)
        {
            var stack = new StackPanel { Margin = new Thickness(6, 20, 6, 6) };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                Foreground = Br("B.Mute"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(hint))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = hint,
                    FontSize = 10.5,
                    Foreground = Br("B.Mute"),
                    Opacity = 0.75,
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            return stack;
        }

        private static string ItemCaption(ItemData item)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Category)) parts.Add(item.Category.Trim());
            if (item.Quantity > 1) parts.Add(item.Quantity + " шт.");
            if (item.IsEquipped) parts.Add("надето");
            if (item.IsAttuned) parts.Add("настройка");
            if (item.CostGold > 0) parts.Add(item.CostGold + " зм");
            else if (item.CostSilver > 0) parts.Add(item.CostSilver + " см");
            else if (item.CostCopper > 0) parts.Add(item.CostCopper + " мм");
            return string.Join(" · ", parts);
        }

        private static string TradeCaption(TradeItem item)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Category)) parts.Add(item.Category.Trim());
            if (!string.IsNullOrWhiteSpace(item.Rarity)) parts.Add(item.Rarity.Trim());
            if (item.ArmorClassBonus > 0) parts.Add("+" + item.ArmorClassBonus + " КД");
            if (!string.IsNullOrWhiteSpace(item.DamageDice)) parts.Add(item.DamageDice.Trim());
            if (item.Charges > 0) parts.Add("заряды " + item.Charges);
            return string.Join(" · ", parts);
        }

        #endregion

        #region === МОНЕТЫ ===

        private void OnCoinChanged()
        {
            if (_coinsSilent) return;

            _coinsSilent = true;
            try
            {
                for (int i = 0; i < _coinBoxes.Length; i++)
                {
                    var box = _coinBoxes[i];
                    if (box == null) continue;
                    string raw = box.Text ?? "";
                    string digits = new string(raw.Where(char.IsDigit).ToArray());
                    if (digits.Length > 6) digits = digits.Substring(0, 6);
                    if (digits != raw)
                    {
                        box.Text = digits;
                        box.CaretIndex = digits.Length;
                    }
                }
                UpdateCoinsVisual();

                // Корзина обязана показать монеты сразу: раньше при вводе
                // обновлялись только подписи, а в правой панели монет не
                // появлялось — и казалось, что передать их нельзя.
                UpdateBasket();
            }
            finally
            {
                _coinsSilent = false;
            }
        }

        private void UpdateCoinsVisual()
        {
            var values = MoneyValues();
            bool over = false;

            for (int i = 0; i < 5; i++)
            {
                bool tooMuch = values[i] > _coinOwned[i];
                if (tooMuch) over = true;

                if (_coinCells[i] != null)
                    _coinCells[i].BorderBrush = tooMuch ? Br("B.Danger") : Br("B.Line");
                if (_coinBoxes[i] != null)
                    _coinBoxes[i].Foreground = tooMuch ? Br("B.Danger") : Br("B.Ink");
            }

            if (_balance != null) _balance.Text = "У вас: " + CoinsText(_coinOwned);

            if (_balanceLeft != null)
            {
                var left = new int[5];
                for (int i = 0; i < 5; i++) left[i] = Math.Max(0, _coinOwned[i] - values[i]);
                _balanceLeft.Text = "Останется: " + CoinsText(left);
                _balanceLeft.Foreground = over ? Br("B.Danger") : Br("B.Mute");
            }

            UpdatePrimaryState();
        }

        #endregion

        #region === ОТПРАВКА ===

        private void OnPrimary(object sender, RoutedEventArgs e)
        {
            if (_busy || _primary == null || !_primary.IsEnabled) return;

            _busy = true;
            _primary.IsEnabled = false;

            if (_isSender) SendOffer();
            else AcceptOffer();
        }

        private string MyName()
        {
            try
            {
                var me = _room.GetPlayers()?.FirstOrDefault(p => p.Id == _room.MyPlayerId);
                string character = (me?.CharacterName ?? "").Trim();
                if (character.Length > 0) return character;
                string name = (me?.Name ?? "").Trim();
                if (name.Length > 0) return name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Trade] своё имя: " + ex.Message);
            }
            return "Игрок";
        }

        private string ValidateOffer()
        {
            var want = MoneyValues();
            for (int i = 0; i < 5; i++)
            {
                if (want[i] > _coinOwned[i])
                {
                    // Клампим до того, что реально лежит в кошельке, и говорим об этом
                    if (_coinBoxes[i] != null && _coinOwned[i] >= 0)
                    {
                        _coinBoxes[i].Text = _coinOwned[i].ToString();
                        _coinBoxes[i].CaretIndex = _coinBoxes[i].Text.Length;
                    }
                    return $"У вас только {_coinOwned[i]} {_coinShort[i]} — сумму уменьшили.";
                }
            }

            if (_selected.Count == 0 && want.Sum() == 0)
                return "Выберите предметы или укажите монеты.";

            foreach (var item in _selected)
            {
                int qty = SelectedQuantity(item);
                ItemData fresh = null;
                try { fresh = _inventory.GetItem(item.Id); } catch { }
                if (fresh == null)
                    return $"«{item.Name}» больше нет в инвентаре — обновите список.";
                if (qty > fresh.Quantity)
                {
                    _quantities[item.Id] = fresh.Quantity;
                    RefreshLeft();
                    return $"«{fresh.Name}»: у вас только {fresh.Quantity} — количество уменьшили.";
                }
            }

            if (want.Sum() > 0)
            {
                CharacterMoney money = null;
                try { money = _money.LoadMoney(); } catch { }
                if (money == null) return "Не удалось прочитать кошелёк — передача отменена.";
                if (money.Platinum < want[0] || money.Gold < want[1] ||
                    money.Electrum < want[2] || money.Silver < want[3] || money.Copper < want[4])
                    return "Монет не хватает — сумму уменьшили. Обновите окно.";
            }

            return null;
        }

        private void SendOffer()
        {
            if (_other == null || string.IsNullOrWhiteSpace(_other.Id))
            {
                Fail("Получатель не выбран.");
                return;
            }
            if (!_room.IsActive)
            {
                Fail("Нет связи с отрядом — предложение не отправить.");
                return;
            }

            string error = ValidateOffer();
            if (error != null)
            {
                Fail(error);
                return;
            }

            var values = MoneyValues();
            var offer = new TradeMessage
            {
                TradeId = Guid.NewGuid().ToString("N"),
                Kind = "offer",
                FromPlayerId = _room.MyPlayerId,
                FromName = MyName(),
                ToPlayerId = _other.Id,
                ToName = DisplayName(_other),
                Items = _selected.Select(i => ToTradeItem(i, SelectedQuantity(i))).ToList(),
                Platinum = values[0],
                Gold = values[1],
                Electrum = values[2],
                Silver = values[3],
                Copper = values[4]
            };

            _pending = offer;
            _locked = true;

            // После отправки корзина зафиксирована: иначе у отправителя и
            // получателя оказались бы разные списки одной сделки.
            if (_leftHost != null) _leftHost.IsEnabled = false;

            _room.SendTrade(offer);

            SetStatus("Предложение отправлено. Ждём, пока получатель подтвердит.", Br("B.Mute"));
            StartTimeout(60, "Получатель не ответил — предложение отменено.");
        }

        private void AcceptOffer()
        {
            if (_incoming == null)
            {
                Fail("Предложение не найдено.");
                return;
            }
            if (!_room.IsActive)
            {
                Fail("Нет связи с отрядом.");
                return;
            }

            var accept = new TradeMessage
            {
                TradeId = _incoming.TradeId,
                Kind = "accept",
                Accepted = true,
                FromPlayerId = _room.MyPlayerId,
                FromName = MyName(),
                ToPlayerId = _incoming.FromPlayerId,
                ToName = _incoming.FromName
            };

            _room.ClearPendingTrade(_incoming.TradeId);
            _accepted = true;
            _room.SendTrade(accept);

            SetStatus("Подтверждено. Ждём, пока отправитель передаст предметы…", Br("B.Mute"));
            StartTimeout(20, "Отправитель не передал предметы — возможно, он закрыл окно.");
            UpdatePrimaryState();
        }

        private void CommitTransfer()
        {
            if (_pending == null || _commitSent || _completed)
                return;

            _commitSent = true;

            try
            {
                CommitTransferCore();
            }
            catch (Exception ex)
            {
                // Списание — единственное место, где окно трогает базу. Падать
                // в обработчике сети нельзя: упадёт всё приложение.
                Debug.WriteLine("[Trade] списание: " + ex);
                Fail("Не удалось передать предметы: " + ex.Message);
            }
        }

        private void CommitTransferCore()
        {
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
            if (money.Platinum < _pending.Platinum || money.Gold < _pending.Gold ||
                money.Electrum < _pending.Electrum || money.Silver < _pending.Silver ||
                money.Copper < _pending.Copper)
                error = "Недостаточно монет — передача отменена.";

            if (error != null)
            {
                SendKind("cancel", _pending, error);
                _pending = null;
                _closeSent = true;
                StopTimeout();
                SetStatus(error, Br("B.Danger"));
                CloseSoon(1400);
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

            money.Platinum -= _pending.Platinum;
            money.Gold -= _pending.Gold;
            money.Electrum -= _pending.Electrum;
            money.Silver -= _pending.Silver;
            money.Copper -= _pending.Copper;
            _money.SaveMoney(money);

            _completed = true;
            StopTimeout();

            _pending.Kind = "commit";
            _room.SendTrade(_pending);

            SetStatus("Передано. Получатель получает предметы.", Br("B.Ok"));
            CloseSoon(700);
        }

        private void ReceiveTransfer(TradeMessage msg)
        {
            if (_completed || msg == null) return;

            string id = msg.TradeId ?? "";

            // Повторное сообщение с тем же номером не применяем второй раз:
            // предметы уже зачислены, и «ещё раз» — это удвоение инвентаря.
            if (!MarkCommitApplied(id))
            {
                SetStatus("Эта передача уже получена — повтор пропущен.", Br("B.Mute"));
                return;
            }

            try
            {
                foreach (var item in msg.Items ?? new List<TradeItem>())
                    _inventory.AddOrStack(ToItemData(item));

                var money = _money.LoadMoney();
                money.Platinum += Math.Max(0, msg.Platinum);
                money.Gold += Math.Max(0, msg.Gold);
                money.Electrum += Math.Max(0, msg.Electrum);
                money.Silver += Math.Max(0, msg.Silver);
                money.Copper += Math.Max(0, msg.Copper);
                _money.SaveMoney(money);

                _completed = true;
                StopTimeout();
                SetStatus("Предметы и монеты получены.", Br("B.Ok"));
                CloseSoon(900);
            }
            catch (Exception ex)
            {
                // Не смогли применить — снимаем отметку, чтобы повтор кадра
                // мог донести передачу, а не потерялся навсегда.
                UnmarkCommitApplied(id);
                Fail("Не удалось принять предметы: " + ex.Message);
            }
        }

        private void SendKind(string kind, TradeMessage basis, string error)
        {
            if (basis == null) return;
            try
            {
                _room.SendTrade(new TradeMessage
                {
                    TradeId = basis.TradeId,
                    Kind = kind,
                    FromPlayerId = _room.MyPlayerId,
                    FromName = MyName(),
                    ToPlayerId = _isSender ? basis.ToPlayerId : basis.FromPlayerId,
                    ToName = _isSender ? basis.ToName : basis.FromName,
                    Error = error ?? ""
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Trade] {kind}: {ex.Message}");
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (_completed)
            {
                Close();
                return;
            }

            if (_isSender)
            {
                if (_pending != null) SendKind("cancel", _pending, "");
            }
            else if (_incoming != null)
            {
                SendKind(_accepted ? "cancel" : "decline", _incoming, "");
            }

            _closeSent = true;
            Close();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            OnCancel(null, null);
        }

        private void OnTrade(TradeMessage msg)
        {
            if (msg == null) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Matches(msg)) return;
                if (_completed) return;

                if (_isSender)
                {
                    if (string.Equals(msg.Kind, "accept", StringComparison.Ordinal))
                    {
                        StopTimeout();
                        CommitTransfer();
                    }
                    else if (string.Equals(msg.Kind, "decline", StringComparison.Ordinal) ||
                             string.Equals(msg.Kind, "cancel", StringComparison.Ordinal))
                    {
                        StopTimeout();
                        string text = string.IsNullOrWhiteSpace(msg.Error)
                            ? "Получатель отклонил передачу."
                            : msg.Error;
                        _pending = null;
                        _closeSent = true;
                        Fail(text);
                        CloseSoon(1400);
                    }
                    return;
                }

                if (string.Equals(msg.Kind, "commit", StringComparison.Ordinal))
                {
                    if (!_accepted)
                    {
                        // Устаревшая передача: мы это предложение не подтверждали
                        SetStatus("Предложение устарело — предметы не приняты.", Br("B.Danger"));
                        return;
                    }
                    StopTimeout();
                    ReceiveTransfer(msg);
                }
                else if (string.Equals(msg.Kind, "cancel", StringComparison.Ordinal) ||
                         string.Equals(msg.Kind, "decline", StringComparison.Ordinal))
                {
                    StopTimeout();
                    string text = string.IsNullOrWhiteSpace(msg.Error)
                        ? "Отправитель отменил передачу."
                        : msg.Error;
                    _closeSent = true;
                    Fail(text);
                    CloseSoon(1400);
                }
            }));
        }

        private bool Matches(TradeMessage msg)
        {
            string id = _isSender ? _pending?.TradeId : _incoming?.TradeId;
            return !string.IsNullOrEmpty(id) && string.Equals(msg.TradeId, id, StringComparison.Ordinal);
        }

        private void Fail(string text)
        {
            _busy = false;
            SetStatus(text, Br("B.Danger"));
            UpdatePrimaryState();
        }

        private void SetStatus(string text, Brush brush)
        {
            if (_status == null) return;
            _status.Text = text ?? "";
            _status.Foreground = brush ?? Br("B.Mute");
        }

        private void UpdatePrimaryState()
        {
            if (_primary == null) return;

            bool can;
            if (_isSender)
            {
                var values = MoneyValues();
                bool moneyOk = true;
                for (int i = 0; i < 5; i++)
                    if (values[i] > _coinOwned[i]) moneyOk = false;

                can = !_busy && !_locked && moneyOk && (_selected.Count > 0 || values.Sum() > 0);
            }
            else
            {
                can = !_busy && !_accepted && !_completed;
            }

            _primary.IsEnabled = can;
        }

        #endregion

        #region === ТАЙМАУТЫ И ЗАКРЫТИЕ ===

        private void StartTimeout(int seconds, string message)
        {
            StopTimeout();
            _waitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _waitTimer.Tick += (s, e) =>
            {
                StopTimeout();
                if (_completed) return;

                // Таймаут обязан закрыть и вторую сторону: иначе у неё навсегда
                // остаётся «Ждём подтверждения» от партнёра, которого уже нет.
                if (_isSender && _pending != null)
                {
                    SendKind("cancel", _pending, message);
                    _pending = null;
                }
                else if (!_isSender && _incoming != null)
                {
                    SendKind(_accepted ? "cancel" : "decline", _incoming, message);
                }

                _closeSent = true;
                SetStatus(message, Br("B.Danger"));
                CloseSoon(1400);
            };
            _waitTimer.Start();
        }

        private void StopTimeout()
        {
            if (_waitTimer == null) return;
            _waitTimer.Stop();
            _waitTimer = null;
        }

        private void CloseSoon(int milliseconds)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(200, milliseconds)) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                Close();
            };
            timer.Start();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _room.TradeReceived -= OnTrade;
            StopTimeout();

            if (_completed || _closeSent) return;
            _closeSent = true;

            if (_isSender)
            {
                if (_pending != null) SendKind("cancel", _pending, "");
            }
            else if (_incoming != null)
            {
                SendKind(_accepted ? "cancel" : "decline", _incoming, "");
            }
        }

        #endregion

        #region === ПРЕОБРАЗОВАНИЕ ===

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
            Recharge = string.IsNullOrWhiteSpace(item.Recharge) ? ItemData.RechargeLong : item.Recharge,
            RechargeDice = item.RechargeDice ?? "",
            Rarity = item.Rarity ?? "",
            Notes = item.Notes ?? "",
            PhotoPath = item.PhotoPath ?? "",
            ListPhotoPath = item.ListPhotoPath ?? "",
            IsStackable = item.Quantity > 1
        };

        #endregion

        #region === ДЛЯ ПРОВЕРОК ===

        // Небольшая открытая часть для тестов: окно собирается кодом, и без этого
        // проверка «количество клампится», «баланс виден», «платина на месте»
        // цеплялась бы за имена внутренних элементов, которые легко переименовать.
        internal string BalanceText => _balance?.Text ?? "";
        internal string RemainingText => _balanceLeft?.Text ?? "";
        internal string StatusText => _status?.Text ?? "";
        internal string BasketNote => _basketNote?.Text ?? "";
        internal bool PrimaryEnabled => _primary?.IsEnabled == true;
        internal bool IsSenderWindow => _isSender;
        internal StackPanel LeftList => _leftList;
        internal StackPanel RightList => _rightList;
        internal IReadOnlyList<ItemData> SelectedItems => _selected;
        internal int QuantityOf(int itemId) => _quantities.TryGetValue(itemId, out int q) ? q : 0;

        /// <summary>
        /// Отмечает номер сделки как применённый. false — уже был, второй раз
        /// предметы не начисляем.
        /// </summary>
        internal static bool MarkCommitApplied(string tradeId)
        {
            if (string.IsNullOrWhiteSpace(tradeId)) return false;
            lock (AppliedLock)
            {
                if (AppliedCommits.Contains(tradeId)) return false;
                if (AppliedCommits.Count > 300) AppliedCommits.Clear();
                AppliedCommits.Add(tradeId);
                return true;
            }
        }

        internal static void UnmarkCommitApplied(string tradeId)
        {
            if (string.IsNullOrWhiteSpace(tradeId)) return;
            lock (AppliedLock) AppliedCommits.Remove(tradeId);
        }

        #endregion
    }
}
