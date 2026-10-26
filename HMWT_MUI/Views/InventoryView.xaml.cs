using HMWT_MUI.Services;
using HMWT_MUI.Windows;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using HMWT_MUI.Helpers;

namespace HMWT_MUI.Views
{
    public partial class InventoryView : UserControl
    {
        #region === ПОЛЯ И КОНСТАНТЫ ===

        private readonly int _characterId;
        private readonly InventoryService _inventory;
        private readonly EquipmentService _equipment;

        private int? _selectedItemId;
        private int _editingItemId;

        /// <summary>
        /// Предмет, открытый в редакторе. Нужен для полей, которых в форме нет:
        /// заряды и настройка при сохранении возвращаются как были.
        /// </summary>
        private ItemData _editingItem;

        private string _currentItemPhotoPath;

        /// <summary>Отдельное фото для строки списка (может быть пустым).</summary>
        private string _currentItemListPhotoPath;
        private string _pendingPhotoSource;
        private int? _pendingCellId;
        private int? _pickedItemId;
        private int? _pendingSlotId;
        private int? _pendingGroupId;
        private int? _pendingDeleteId;
        /// <summary>Сколько всего предметов в стопке — верхняя граница выбора.</summary>
        private int _pendingDeleteMax = 1;
        private int? _pendingDeleteCellId;
        private int? _pendingDeleteGroupId;
        private bool _pendingDeleteIsGroup;
        private string _selectedTagName;
        private List<string> _editorTags = new List<string>();
        private bool _treasureOnly = true;
        private bool _viewGrid;

        /// <summary>Анимация GIF в инспекторе предмета. Живёт, пока предмет выбран.</summary>

        private static readonly string[] PlaceholderPalette =
            { "4F7BC0", "9A7BD6", "4FB6AC", "C08A3E", "C0568F", "7A3B3B", "8A9A3E", "E0AE4E" };

        #endregion

        #region === КОНСТРУКТОР ===

        public InventoryView(int characterId)
        {
            InitializeComponent();
            _characterId = characterId;
            _inventory = new InventoryService(characterId);
            _equipment = new EquipmentService(characterId);

            // Список восстановления зарядов собирается здесь, а не в разметке:
            // подписи живут рядом со значениями в ItemData и не разъезжаются с базой
            foreach (var (key, label) in ItemData.RechargeChoices)
                ed_cbRecharge.Items.Add(new ComboBoxItem { Content = label, Tag = key });

            // === === === === === === === === === === === === === === ===
            // Подписка на события элементов
            // === === === === === === === === === === === === === === ===
            btnAddItem.Click += (_, __) => OpenEditor(0);
            txtSearch.TextChanged += (_, __) => RefreshList();
            cbFilterTag.SelectionChanged += (_, __) => RefreshList();
            chkEquippedOnly.Checked += (_, __) => RefreshList();
            chkEquippedOnly.Unchecked += (_, __) => RefreshList();
            btnManageTags.Click += (_, __) => { RefreshTagManager(); overlayTagManager.Visibility = Visibility.Visible; };
            ed_btnSelectTags.Click += (_, __) => OpenTagsPicker();
            btnPickerAddTag.Click += BtnPickerAddTag_Click;
            btnOpenTagManager.Click += BtnOpenTagManager_Click;
            btnViewToggle.Click += BtnViewToggle_Click;
            btnChangeSilhouette.Click += BtnChangeSilhouette_Click;
            btnAddGroup.Click += BtnAddGroup_Click;
            btnToggleTreasureMode.Click += BtnToggleTreasureMode_Click;
            btnUseCharge.Click += BtnUseCharge_Click;
            btnEditItem.Click += (_, __) => { if (_selectedItemId.HasValue) OpenEditor(_selectedItemId.Value); };
            btnDeleteItem.Click += BtnDeleteItem_Click;
            btnItemSelectConfirm.Click += BtnItemSelectConfirm_Click;
            btnCreateCustomSlot.Click += BtnCreateCustomSlot_Click;
            btnSlotSelectConfirm.Click += BtnSlotSelectConfirm_Click;
            btnAddCellFromOverlay.Click += BtnAddCellFromOverlay_Click;

            // === === === === === === === === === === === === === === ===
            // Жизненный цикл View: инициализация + Esc-обработчик
            // === === === === === === === === === === === === === === ===
            Loaded += (_, __) =>
            {
                // Ошибка чтения базы не должна закрывать окно персонажа целиком:
                // раньше любое исключение при первичной загрузке (например,
                // отсутствующая колонка в схеме) убивало процесс. Теперь вкладка
                // остаётся открытой, а причина уходит в тост и в лог.
                try
                {
                    RefreshAll();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[InventoryView] первичная загрузка: {ex}");
                    Toast("Не удалось загрузить инвентарь: " + ex.Message, true);
                }
                // Автоматический импорт каталога убран по требованию пользователя:
                // при открытии вкладки из папки Data ничего не подгружается.
                // Каталог игрок наполняет сам — своими предметами или импортом
                // файла, который сделал другой игрок.
                if (Window.GetWindow(this) is Window w) w.PreviewKeyDown += OnEsc;
            };
            Unloaded += (_, __) =>
            {
                StopShopGif();
                if (Window.GetWindow(this) is Window w) w.PreviewKeyDown -= OnEsc;
            };
        }

        #endregion

        #region === КАТАЛОГ МАГАЗИНА ===

        /// <summary>
        /// Раньше здесь каталог магазина импортировался из папки Data/srd при
        /// первом открытии вкладки. По требованию пользователя автоматический импорт
        /// убран: в поставке данных справочника нет, игрок наполняет каталог сам
        /// (своими предметами или файлом от другого игрока).
        /// </summary>
        private void EnsureShopCatalogImported()
        {
            Debug.WriteLine("[InventoryView] автоматический импорт каталога отключён");
        }

        #endregion

        #region === ВКЛАДКИ: ИНВЕНТАРЬ / МАГАЗИН ===

        /// <summary>Показывает вкладку инвентаря, скрывая магазин.</summary>
        private void TabInventory_Checked(object sender, RoutedEventArgs e)
        {
            if (tabInventory == null || tabShop == null) return;
            tabInventory.Visibility = Visibility.Visible;
            tabShop.Visibility = Visibility.Collapsed;
        }

        /// <summary>Показывает магазин и при первом входе строит каталог.</summary>
        private void TabShop_Checked(object sender, RoutedEventArgs e)
        {
            if (tabInventory == null || tabShop == null) return;
            tabInventory.Visibility = Visibility.Collapsed;
            tabShop.Visibility = Visibility.Visible;

            EnsureShopInitialized();
        }

        #endregion

        #region === МАГАЗИН ===

        private ShopService _shop;
        private ShopFilter _shopFilter = new ShopFilter();
        private DispatcherTimer _shopSearchDebounce;
        private SrdItemRow _shopSelected;
        private GifAnimator _shopGifAnimator;
        private bool _shopInitialized;
        private bool _shopLoading;

        /// <summary>Ширина карточки при последней перестройке: чтобы не строить список заново на каждый пиксель.</summary>
        private double _shopLastCardWidth;

        /// <summary>Каталог из локальной БД читается синхронно: фильтр отдаёт до 400 записей.</summary>
        private const int ShopPageSize = 400;

        /// <summary>
        /// Первая инициализация магазина: сервис, выпадающие фильтры, обработчики
        /// и первая выборка каталога. Вызывается при первом открытии вкладки.
        /// </summary>
        private void EnsureShopInitialized()
        {
            if (_shopInitialized) return;
            _shopInitialized = true;

            try
            {
                _shop = new ShopService(_characterId);

                _shopSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
                _shopSearchDebounce.Tick += (_, __) =>
                {
                    _shopSearchDebounce.Stop();
                    _shopFilter.Search = shopSearch.Text ?? "";
                    RefreshShopList();
                };

                shopSearch.TextChanged += (_, __) =>
                {
                    _shopSearchDebounce.Stop();
                    _shopSearchDebounce.Start();
                };

                shopTypeFilter.SelectionChanged += (_, __) => { ApplyShopTypeFilter(); };
                shopRarityFilter.SelectionChanged += (_, __) => { ApplyShopRarityFilter(); };
                shopSort.SelectionChanged += (_, __) => { ApplyShopSort(); };
                shopOnlyAttunement.Checked += (_, __) => { _shopFilter.OnlyAttunement = true; RefreshShopList(); };
                shopOnlyAttunement.Unchecked += (_, __) => { _shopFilter.OnlyAttunement = false; RefreshShopList(); };
                shopOnlyStats.Checked += (_, __) => { _shopFilter.OnlyWithStats = true; RefreshShopList(); };
                shopOnlyStats.Unchecked += (_, __) => { _shopFilter.OnlyWithStats = false; RefreshShopList(); };

                // Только свои предметы: без этого фильтра созданный предмет
                // не видно — в каталоге десятки тысяч записей, а список
                // показывает первые 400 по имени
                shopOnlyCustom.Checked += (_, __) => { _shopFilter.OnlyCustom = true; RefreshShopList(); };
                shopOnlyCustom.Unchecked += (_, __) => { _shopFilter.OnlyCustom = false; RefreshShopList(); };

                btnShopClearFilters.Click += (_, __) => ResetShopFilters();
                btnShopBuy.Click += (_, __) => BuySelectedShopItem();
                btnShopChangePhoto.Click += (_, __) => ChangeShopItemPhoto(ShopPhotoKind.Photo);
                btnShopChangeListPhoto.Click += (_, __) => ChangeShopItemPhoto(ShopPhotoKind.ListPhoto);
                btnShopClearPhoto.Click += (_, __) => ClearSelectedShopItemPhoto(ShopPhotoKind.Photo);
                btnShopClearListPhoto.Click += (_, __) => ClearSelectedShopItemPhoto(ShopPhotoKind.ListPhoto);
                btnShopCleanup.Click += (_, __) => RunPhotoCleanup();

                FillShopFilters();
                RefreshShopList();

                // Две колонки пересчитываются при изменении ширины окна,
                // но не чаще, чем ширина реально изменилась заметно: иначе
                // раскладка вызывает перестройку списка по кругу.
                _shopLastCardWidth = ShopCardWidth;
                shopListScroll.SizeChanged += (_, __) =>
                {
                    if (_shopLoading) return;
                    double now = ShopCardWidth;
                    if (Math.Abs(now - _shopLastCardWidth) < 40) return;
                    _shopLastCardWidth = now;
                    RefreshShopList();
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InventoryView] магазин: {ex.Message}");
                Toast($"Магазин недоступен: {ex.Message}", true);
            }
        }

        /// <summary>Заполняет выпадающие списки типов, редкостей и сортировки.</summary>
        private void FillShopFilters()
        {
            var types = new List<string> { "Все типы" };
            types.AddRange(_shop.LoadItemTypes());
            shopTypeFilter.ItemsSource = types;
            shopTypeFilter.SelectedIndex = 0;

            var rarities = new List<string> { "Все редкости" };
            rarities.AddRange(_shop.LoadRarities());
            shopRarityFilter.ItemsSource = rarities;
            shopRarityFilter.SelectedIndex = 0;

            shopSort.SelectedIndex = 0;
            shopOnlyAttunement.IsChecked = false;
            shopOnlyStats.IsChecked = false;
        }

        private void ApplyShopTypeFilter()
        {
            if (!_shopInitialized) return;
            string value = shopTypeFilter.SelectedItem?.ToString() ?? "Все типы";
            _shopFilter.ItemType = value == "Все типы" ? "" : value;
            RefreshShopList();
        }

        private void ApplyShopRarityFilter()
        {
            if (!_shopInitialized) return;
            string value = shopRarityFilter.SelectedItem?.ToString() ?? "Все редкости";
            _shopFilter.Rarity = value == "Все редкости" ? "" : value;
            RefreshShopList();
        }

        private void ApplyShopSort()
        {
            if (!_shopInitialized) return;
            _shopFilter.SortBy = shopSort.SelectedIndex switch
            {
                1 => "weight",
                2 => "rarity",
                3 => "type",
                _ => "name"
            };
            RefreshShopList();
        }

        private void ResetShopFilters()
        {
            shopSearch.Text = "";
            if (shopTypeFilter.Items.Count > 0) shopTypeFilter.SelectedIndex = 0;
            if (shopRarityFilter.Items.Count > 0) shopRarityFilter.SelectedIndex = 0;
            if (shopSort.Items.Count > 0) shopSort.SelectedIndex = 0;
            shopOnlyAttunement.IsChecked = false;
            shopOnlyStats.IsChecked = false;
            shopOnlyCustom.IsChecked = false;
            _shopFilter = new ShopFilter();
            RefreshShopList();
        }

        /// <summary>
        /// Ширина карточки каталога.
        ///
        /// В списке две колонки, поэтому карточка занимает чуть меньше половины
        /// доступной ширины. Пересчитывается при каждой перестройке списка: при
        /// смене размера окна список строится заново.
        /// </summary>
        private double ShopCardWidth
        {
            get
            {
                double available = shopListPanel.ActualWidth;
                if (available <= 0) available = shopListScroll.ActualWidth - 6;
                if (available <= 0) available = 620;

                const double gap = 10;
                double half = (available - gap) / 2.0;
                return Math.Max(220, half - 4);
            }
        }

        /// <summary>Перестраивает список каталога по текущему фильтру.</summary>
        private void RefreshShopList()
        {
            if (_shop == null || _shopLoading) return;
            _shopLoading = true;
            try
            {
                var items = _shop.LoadCatalog(_shopFilter, ShopPageSize);
                int total = _shop.CountCatalog(_shopFilter);

                shopListPanel.Children.Clear();
                double cardWidth = ShopCardWidth;
                foreach (var item in items)
                {
                    var row = BuildShopRow(item);
                    if (row is FrameworkElement element) element.Width = cardWidth;
                    shopListPanel.Children.Add(row);
                }

                shopCountText.Text = total > items.Count
                    ? $"Показано {items.Count} из {total}"
                    : $"Найдено: {total}";

                if (items.Count == 0)
                {
                    shopListPanel.Children.Add(new TextBlock
                    {
                        Text = "Ничего не найдено. Измените фильтры или поиск.",
                        Foreground = (Brush)FindResource("B.Mute"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4, 10, 4, 0)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InventoryView] RefreshShopList: {ex.Message}");
                Toast($"Не удалось загрузить каталог: {ex.Message}", true);
            }
            finally { _shopLoading = false; }
        }

        /// <summary>Строка каталога в списке магазина.</summary>
        /// <summary>
        /// Цвет метки по типу предмета.
        ///
        /// Оружие — красное, доспехи — серое, зелья — жёлтое, свитки — синие и так
        /// далее; близкие по смыслу типы отличаются оттенком. Нужно, чтобы в
        /// длинном списке тип читался с одного взгляда, а не по мелкой подписи.
        /// </summary>
        private static Color ShopTypeColor(string itemType)
        {
            string t = (itemType ?? "").ToLowerInvariant();

            if (t.Length == 0) return Color.FromRgb(0x6B, 0x67, 0x7A);

            // Оружие: холодное — красное, метательное/дальнее — оранжевое
            if (t.Contains("weapon") || t.Contains("оружие") || t.Contains("melee") ||
                t.Contains("martial") || t.Contains("sword") || t.Contains("axe"))
            {
                if (t.Contains("ranged") || t.Contains("ammunition") || t.Contains("bow"))
                    return Color.FromRgb(0xE0, 0x7A, 0x3C);
                return Color.FromRgb(0xD0, 0x4F, 0x45);
            }

            // Доспехи и щиты: тяжёлые темнее, лёгкие светлее
            if (t.Contains("armor") || t.Contains("armour") || t.Contains("доспех") || t.Contains("щит") || t.Contains("shield"))
            {
                if (t.Contains("heavy") || t.Contains("plate")) return Color.FromRgb(0x9A, 0xA3, 0xB0);
                if (t.Contains("light")) return Color.FromRgb(0xC3, 0xC9, 0xD4);
                return Color.FromRgb(0xA8, 0xB0, 0xBC);
            }

            // Зелья и яды: жёлтое и зеленоватое
            if (t.Contains("potion") || t.Contains("зелье")) return Color.FromRgb(0xE0, 0xC0, 0x4E);
            if (t.Contains("poison") || t.Contains("яд")) return Color.FromRgb(0x8F, 0xB8, 0x4A);

            // Свитки и книги
            if (t.Contains("scroll") || t.Contains("свиток")) return Color.FromRgb(0x5A, 0x8F, 0xD6);
            if (t.Contains("book") || t.Contains("tome") || t.Contains("книга")) return Color.FromRgb(0x4F, 0x76, 0xB8);

            // Кольца, амулеты, палочки, жезлы, посохи: магические предметы
            if (t.Contains("ring") || t.Contains("кольцо")) return Color.FromRgb(0xC0, 0x8A, 0x3E);
            if (t.Contains("rod") || t.Contains("жезл")) return Color.FromRgb(0xA9, 0x8B, 0xEC);
            if (t.Contains("wand") || t.Contains("staff") || t.Contains("палочка") || t.Contains("посох"))
                return Color.FromRgb(0xB0, 0x8A, 0xE0);
            if (t.Contains("wondrous") || t.Contains("чудесн")) return Color.FromRgb(0x7E, 0x6F, 0xC9);

            // Снаряжение и инструменты
            if (t.Contains("gear") || t.Contains("tool") || t.Contains("instrument") ||
                t.Contains("снаряж") || t.Contains("инструмент")) return Color.FromRgb(0x4F, 0xB6, 0xAC);

            // Всё остальное — нейтральный
            return Color.FromRgb(0x8B, 0x95, 0xB4);
        }

        /// <summary>Цвет для рамки и подписи типа предмета в списке.</summary>
        private static string ShopTypeLabel(string itemType)
        {
            if (string.IsNullOrWhiteSpace(itemType)) return "без типа";
            return itemType.Trim();
        }

        private UIElement BuildShopRow(SrdItemRow item)
        {
            var card = new Border { Style = (Style)FindResource("ShopCard") };
            card.MouseLeftButtonUp += (_, __) => SelectShopItem(item);

            Color typeColor = ShopTypeColor(item.ItemType);
            var typeBrush = new SolidColorBrush(typeColor);

            // Тонкая цветная полоса слева: тип виден с одного взгляда,
            // а текст остаётся на тёмном фоне и читается нормально
            var accent = new Border
            {
                Width = 4,
                CornerRadius = new CornerRadius(2),
                Background = typeBrush,
                Margin = new Thickness(0, 0, 10, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(accent, 0);
            grid.Children.Add(accent);

            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = item.Name,
                Foreground = (Brush)FindResource("B.Ink"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var typeLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            typeLine.Children.Add(new TextBlock
            {
                Text = ShopTypeLabel(item.ItemType),
                Foreground = typeBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // Редкость и книга — без типа: он уже показан цветной подписью слева,
            // иначе получалось «Wondrous item · Wondrous item · Артефакт»
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Rarity)) details.Add(item.Rarity.Trim());
            if (!string.IsNullOrWhiteSpace(item.Book)) details.Add(item.Book.Trim());

            if (details.Count > 0)
            {
                typeLine.Children.Add(new TextBlock
                {
                    Text = " · " + string.Join(" · ", details),
                    Foreground = (Brush)FindResource("B.Mute"),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
            left.Children.Add(typeLine);

            string stats = item.StatsLine;
            if (!string.IsNullOrWhiteSpace(stats))
            {
                left.Children.Add(new TextBlock
                {
                    Text = stats,
                    Foreground = (Brush)FindResource("B.Frost"),
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            Grid.SetColumn(left, 1);
            grid.Children.Add(left);

            // Миниатюра строки: берём ОТДЕЛЬНОЕ фото для списка, а если его нет —
            // крупное фото предмета, чтобы не было пустого места
            string photo = PhotoHelper.ResolvePhotoPath(item.ListPhotoPath)
                           ?? PhotoHelper.ResolvePhotoPath(item.PhotoPath);
            if (photo != null)
            {
                var thumb = new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(6),
                    ClipToBounds = true,
                    Background = (Brush)FindResource("B.Slate"),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = LoadItemImage(photo)
                };
                Grid.SetColumn(thumb, 2);
                grid.Children.Add(thumb);
            }

            card.Child = grid;
            card.ToolTip = string.IsNullOrWhiteSpace(item.ItemType)
                ? item.Name
                : item.ItemType + (string.IsNullOrWhiteSpace(item.Rarity) ? "" : " · " + item.Rarity);
            return card;
        }

        /// <summary>Показывает карточку выбранного предмета каталога.</summary>
        private void SelectShopItem(SrdItemRow item)
        {
            _shopSelected = item;
            StopShopGif();
            shopDetailPanel.Children.Clear();
            shopBuyPanel.Visibility = Visibility.Visible;

            string Photo() => PhotoHelper.ResolvePhotoPath(item.PhotoPath);

            // ── Фото ──
            string full = Photo();
            if (full != null)
            {
                // HoverGif показывает статичный кадр и анимирует только при наведении:
                // постоянная анимация в карточке держала в памяти весь набор кадров
                var img = new Controls.HoverGif
                {
                    Stretch = Stretch.Uniform,
                    MaxHeight = 200
                };
                img.SetImage(full);

                var photoBox = new Border
                {
                    Background = (Brush)FindResource("B.Slate"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = img
                };
                shopDetailPanel.Children.Add(photoBox);
            }

            // ── Название и подпись ──
            shopDetailPanel.Children.Add(new TextBlock
            {
                Text = item.Name,
                Foreground = (Brush)FindResource("B.Ink"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(item.Subtitle))
                shopDetailPanel.Children.Add(new TextBlock
                {
                    Text = item.Subtitle,
                    Foreground = (Brush)FindResource("B.Accent"),
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                });

            // ── Характеристики: прочерк, если данных в справочнике нет ──
            shopDetailPanel.Children.Add(BuildShopStatRow("Тип", Dash(item.ItemType)));
            shopDetailPanel.Children.Add(BuildShopStatRow("Подтип", Dash(item.Subtype)));
            shopDetailPanel.Children.Add(BuildShopStatRow("Редкость", Dash(item.Rarity)));
            shopDetailPanel.Children.Add(BuildShopStatRow("Урон", Dash(item.Damage)));
            shopDetailPanel.Children.Add(BuildShopStatRow("Тип урона", Dash(item.DamageType)));
            shopDetailPanel.Children.Add(BuildShopStatRow("КД", item.Ac > 0 ? item.Ac.ToString() : "—"));
            shopDetailPanel.Children.Add(BuildShopStatRow("Дистанция", Dash(item.Range)));
            shopDetailPanel.Children.Add(BuildShopStatRow("Вес", item.Weight > 0 ? $"{item.Weight:0.##} кг" : "—"));
            shopDetailPanel.Children.Add(BuildShopStatRow("Настройка", item.Attunement ? "требуется" : "не требуется"));
            shopDetailPanel.Children.Add(BuildShopStatRow("Источник", Dash(item.Book)));

            if (!string.IsNullOrWhiteSpace(item.PropertiesText))
                shopDetailPanel.Children.Add(BuildShopStatRow("Свойства", item.PropertiesText));

            // ── Описание: текст из справочника ──
            var descHeader = new TextBlock
            {
                Text = "ОПИСАНИЕ",
                Foreground = (Brush)FindResource("B.Mute"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 12, 0, 6)
            };
            shopDetailPanel.Children.Add(descHeader);

            string description = string.IsNullOrWhiteSpace(item.Description)
                ? "Описание в источнике отсутствует."
                : item.Description.Trim();

            shopDetailPanel.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = (Brush)FindResource("B.Ink"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
                Opacity = 0.92
            });

            btnShopClearPhoto.Visibility = full != null ? Visibility.Visible : Visibility.Collapsed;
            btnShopClearListPhoto.Visibility = PhotoHelper.ResolvePhotoPath(item.ListPhotoPath) != null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>Строка «подпись — значение» в карточке магазина.</summary>
        private UIElement BuildShopStatRow(string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = label,
                Foreground = (Brush)FindResource("B.Mute"),
                FontSize = 11
            };
            Grid.SetColumn(lbl, 0);

            var val = new TextBlock
            {
                Text = value,
                Foreground = (Brush)FindResource("B.Frost"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(val, 1);

            grid.Children.Add(lbl);
            grid.Children.Add(val);
            return grid;
        }

        /// <summary>Пустое значение показываем прочерком: в справочнике часть полей не заполнена.</summary>
        private static string Dash(string value)
            => string.IsNullOrWhiteSpace(value) ? "—" : value;

        private void StopShopGif()
        {
            if (_shopGifAnimator == null) return;
            try { _shopGifAnimator.Stop(); } catch { }
            _shopGifAnimator = null;
        }

        /// <summary>Покупка: добавляет предмет в инвентарь. Деньги списывает игрок сам.</summary>
        private void BuySelectedShopItem()
        {
            if (_shopSelected == null) { Toast("Сначала выберите предмет", true); return; }

            // Повторное нажатие (или «залипшая» кнопка) не должно добавить предмет дважды
            if (_buying) return;

            int quantity = ParseInt(shopQuantity.Text, 1);
            if (quantity < 1) quantity = 1;
            if (quantity > 99) quantity = 99;

            _buying = true;
            try
            {
                int newId = _shop.BuyItem(_shopSelected, quantity);
                if (newId <= 0) { Toast("Не удалось добавить предмет", true); return; }

                // Количество сбрасываем на единицу. Раньше 14 покупок
                // дали 42 предмета: поле «Количество» запоминало прежнее значение,
                // и каждый следующий клик добавлял по столько же.
                shopQuantity.Text = "1";

                RefreshAll();

                int inInventory = _inventory.GetItem(newId)?.Quantity ?? quantity;
                Toast($"«{_shopSelected.Name}»: добавлено {quantity}, в инвентаре {inInventory}");
            }
            finally
            {
                _buying = false;
            }
        }

        /// <summary>Признак того, что покупка уже выполняется.</summary>
        private bool _buying;

        /// <summary>
        /// Присваивает фото/гифку предмету каталога.
        ///
        /// photoKind различает два места: крупное фото карточки предмета и
        /// отдельное фото строки списка. Хранятся в разных файлах и колонках,
        /// поэтому смена одного не трогает другое.
        /// </summary>
        private void ChangeShopItemPhoto(string photoKind = ShopPhotoKind.Photo)
        {
            if (_shopSelected == null) { Toast("Сначала выберите предмет", true); return; }

            // Проводник мог открываться несколько раз подряд:
            // после закрытия диалога кнопка мыши ещё нажата, и нажатие приходит
            // повторно. Поэтому кроме флага проверяем и время.
            if (!CanPickPhoto()) return;

            bool forList = photoKind == ShopPhotoKind.ListPhoto;
            _pickingPhoto = true;
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Изображения (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp|Все файлы (*.*)|*.*",
                    Title = forList ? "Фото предмета для строки списка" : "Фото предмета для карточки"
                };
                if (dialog.ShowDialog() != true) return;

                string relative = _shop.SaveShopPhoto(_shopSelected.Id, dialog.FileName, photoKind);
                if (string.IsNullOrEmpty(relative)) { Toast("Фото не сохранено", true); return; }

                if (forList) _shopSelected.ListPhotoPath = relative;
                else _shopSelected.PhotoPath = relative;

                SelectShopItem(_shopSelected);
                RefreshShopList();
                Toast(forList ? "Фото для списка сохранено" : "Фото карточки сохранено");
            }
            finally
            {
                MarkPhotoPicked();
                _pickingPhoto = false;
            }
        }

        /// <summary>Признак того, что диалог выбора файла уже открыт.</summary>
        private bool _pickingPhoto;

        /// <summary>
        /// Когда последний раз закрывался диалог выбора файла. Нужно, чтобы
        /// нажатие, оставшееся от закрытия проводника, не открыло его заново.
        /// </summary>
        private DateTime _lastPhotoPickUtc = DateTime.MinValue;

        /// <summary>
        /// Проверяет, можно ли сейчас открывать диалог выбора файла.
        /// Используется всеми кнопками выбора картинок в окне инвентаря.
        /// </summary>
        private bool CanPickPhoto() =>
            !_pickingPhoto && (DateTime.UtcNow - _lastPhotoPickUtc).TotalMilliseconds >= 500;

        private void MarkPhotoPicked() => _lastPhotoPickUtc = DateTime.UtcNow;

        private void ClearSelectedShopItemPhoto(string photoKind = ShopPhotoKind.Photo)
        {
            if (_shopSelected == null) return;

            bool forList = photoKind == ShopPhotoKind.ListPhoto;
            _shop.ClearShopPhoto(_shopSelected.Id, photoKind);
            if (forList) _shopSelected.ListPhotoPath = "";
            else _shopSelected.PhotoPath = "";

            StopShopGif();
            SelectShopItem(_shopSelected);
            RefreshShopList();
            Toast(forList ? "Фото списка убрано" : "Фото карточки убрано");
        }

        /// <summary>
        /// Удаляет выбранный предмет из каталога магазина.
        ///
        /// Раньше предмет можно было создать, но не убрать: каталог только рос.
        /// Удаляем и свои предметы, и записи справочника.
        /// </summary>
        private void BtnShopDeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (_shopSelected == null) { Toast("Сначала выберите предмет", true); return; }

            string name = _shopSelected.Name ?? "";
            bool custom = string.Equals(_shopSelected.Book, SrdItemImporter.CustomBook, StringComparison.Ordinal);

            bool confirmed = MessageDialog.Confirm(Window.GetWindow(this),
                custom
                    ? $"Удалить свой предмет «{name}» из каталога?\n\nЕго фото тоже будут удалены. " +
                      "Предметы, уже лежащие в инвентаре персонажей, останутся."
                    : $"Удалить «{name}» из каталога магазина?\n\nЭто запись из загруженного справочника. " +
                      "Её можно вернуть повторным импортом файла справочника.",
                "Удаление предмета", "Удалить", "Отмена", danger: true);

            if (!confirmed) return;

            bool removed = ShareService.DeleteCatalogItem(_shopSelected.Id, custom);
            if (!removed) { Toast("Не удалось удалить предмет", true); return; }

            _shopSelected = null;
            _shopInitialized = false;
            _shopLoading = false;
            EnsureShopInitialized();
            RefreshShopList();
            RefreshAll();
            Toast($"«{name}» удалён из каталога");
        }

        /// <summary>Выгружает свои предметы и заклинания в файл обмена.</summary>
        private void BtnShopExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // На этой вкладке выгружаем только предметы: заклинания
                // выгружаются на вкладке заклинаний (просьба пользователя)
                var (items, spells) = ShareService.CountCustom();
                if (items == 0)
                {
                    Toast("Своих предметов пока нет — выгружать нечего", true);
                    return;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Файл обмена HMWT (*.hmwtpack)|*.hmwtpack",
                    FileName = "HMWT-свои-предметы-" + DateTime.Now.ToString("yyyy-MM-dd") + ShareService.FileExtension,
                    Title = "Куда сохранить свои предметы и заклинания"
                };
                if (dialog.ShowDialog() != true) return;

                var manifest = ShareService.Export(dialog.FileName, out string error,
                    includeItems: true, includeSpells: false);
                if (manifest == null)
                {
                    Toast("Не удалось выгрузить: " + error, true);
                    return;
                }

                Toast($"Выгружено: предметов {manifest.Items.Count}, заклинаний {manifest.Spells.Count}");
                MessageDialog.Info(Window.GetWindow(this),
                    $"Файл готов:\n{dialog.FileName}\n\n" +
                    $"Свои предметы: {manifest.Items.Count}\n" +
                    $"Свои заклинания: {manifest.Spells.Count}\n\n" +
                    "Передайте этот файл другому игроку — он загрузит его кнопкой «Загрузить базы друга».",
                    "Выгрузка завершена");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InventoryView] выгрузка баз: {ex.Message}");
                Toast("Ошибка выгрузки: " + ex.Message, true);
            }
        }

        /// <summary>Загружает файл обмена от другого игрока.</summary>
        private void BtnShopImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Файл обмена HMWT (*.hmwtpack)|*.hmwtpack|Все файлы (*.*)|*.*",
                    Title = "Файл с предметами и заклинаниями друга"
                };
                if (dialog.ShowDialog() != true) return;

                var result = ShareService.Import(dialog.FileName, _characterId);
                if (!string.IsNullOrEmpty(result.Error))
                {
                    Toast("Не удалось загрузить: " + result.Error, true);
                    return;
                }

                // Обновляем каталог: предметы уже в базе
                _shopInitialized = false;
                _shopLoading = false;
                EnsureShopInitialized();
                RefreshAll();

                var lines = new List<string>
                {
                    $"Свои предметы: добавлено {result.ItemsAdded}, обновлено {result.ItemsUpdated}",
                    $"Свои заклинания: добавлено {result.SpellsAdded}, обновлено {result.SpellsUpdated}"
                };
                if (result.ImagesRestored > 0) lines.Add($"Картинок восстановлено: {result.ImagesRestored}");
                if (result.SpellsForCharacter.Count > 0)
                {
                    lines.Add("");
                    lines.Add("Заклинания попали в ваш справочник. Откройте вкладку «Заклинания» " +
                              "и добавьте нужные себе кнопкой «+»:");
                    foreach (var name in result.SpellsForCharacter.Take(10)) lines.Add("• " + name);
                    if (result.SpellsForCharacter.Count > 10)
                        lines.Add($"…и ещё {result.SpellsForCharacter.Count - 10}");
                }

                Toast($"Загружено записей: {result.Total}");
                MessageDialog.Info(Window.GetWindow(this), string.Join("\n", lines), "Загрузка завершена");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InventoryView] загрузка баз: {ex.Message}");
                Toast("Ошибка загрузки: " + ex.Message, true);
            }
        }

        /// <summary>Убирает файлы фото, на которые не ссылается ни инвентарь, ни магазин.</summary>
        private void RunPhotoCleanup()
        {
            try
            {
                int removed = ShopService.CleanupUnusedItemPhotos();
                Toast(removed > 0
                    ? $"Удалено неиспользуемых файлов: {removed}"
                    : "Лишних файлов не найдено");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InventoryView] очистка фото: {ex.Message}");
                Toast($"Ошибка очистки: {ex.Message}", true);
            }
        }

        #endregion

        #region === ОБНОВЛЕНИЕ ДАННЫХ ===

        // === === === === === === === === === === === === === === ===
        // Полное обновление всех секций View
        // === === === === === === === === === === === === === === ===
        private void RefreshAll()
        {
            RefreshTagFilter();
            RefreshList();
            RefreshTreasure();
            RefreshWeight();
            RefreshGroups();
            var sil = _inventory.LoadSilhouettePath(_characterId);
            ApplySilhouette(File.Exists(sil) ? sil : null);
        }

        // === === === === === === === === === === === === === === ===
        // Закрытие верхнего оверлея по Esc (по приоритету)
        // === === === === === === === === === === === === === === ===
        private void OnEsc(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (overlayItemEditor.Visibility == Visibility.Visible) overlayItemEditor.Visibility = Visibility.Collapsed;
            else if (overlayDeleteConfirm.Visibility == Visibility.Visible) overlayDeleteConfirm.Visibility = Visibility.Collapsed;
            else if (overlayTagManager.Visibility == Visibility.Visible) overlayTagManager.Visibility = Visibility.Collapsed;
            else if (overlayGroupCells.Visibility == Visibility.Visible) overlayGroupCells.Visibility = Visibility.Collapsed;
            else if (overlaySlotSelect.Visibility == Visibility.Visible) overlaySlotSelect.Visibility = Visibility.Collapsed;
            else if (overlayItemSelect.Visibility == Visibility.Visible) overlayItemSelect.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region === ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ===

        // === === === === === === === === === === === === === === ===
        // Тост-уведомления (делегируем в родительское Window)
        // === === === === === === === === === === === === === === ===
        private void Toast(string msg, bool error = false)
            => (Window.GetWindow(this) as CharacterInfoWindow)?.ShowToast(msg, error);

        // === === === === === === === === === === === === === === ===
        // Конвертация костей: "к" ↔ "d" (БД хранит "d", UI показывает "к")
        // === === === === === === === === === === === === === === ===
        private static string ToCyr(string d) => string.IsNullOrWhiteSpace(d) ? "" : d.Replace("d", "к").Replace("D", "к");
        private static string ToD(string d) => string.IsNullOrWhiteSpace(d) ? "" : d.Replace("к", "d").Replace("К", "d");

        // === === === === === === === === === === === === === === ===
        // Безопасный парсинг чисел
        // === === === === === === === === === === === === === === ===
        private static int ParseInt(string s, int def = 0) => int.TryParse(s?.Trim(), out int v) ? v : def;
        private static decimal ParseDec(string s) => decimal.TryParse(s?.Trim(), out var v) ? v : 0m;

        // === === === === === === === === === === === === === === ===
        // Безопасное чтение колонки из DataRow
        // === === === === === === === === === === === === === === ===
        private object Cell(DataRow row, string col, object def = null)
            => row.Table.Columns.Contains(col) && !row.IsNull(col) ? row[col] : def;

        // === === === === === === === === === === === === === === ===
        // Приглушение цвета для плейсхолдер-бейджа
        // === === === === === === === === === === === === === === ===
        private static Color Dim(Color c) => Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));

        // === === === === === === === === === === === === === === ===
        // Выбор значения в ComboBox (по совпадению содержимого)
        // === === === === === === === === === === === === === === ===
        private static void SelectCombo(ComboBox cb, string value, string fallback)
        {
            string target = string.IsNullOrWhiteSpace(value) ? fallback : value;
            foreach (ComboBoxItem ci in cb.Items)
                ci.IsSelected = string.Equals(ci.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase);
            if (cb.SelectedIndex < 0 && cb.Items.Count > 0)
                ((ComboBoxItem)cb.Items[cb.Items.Count - 1]).IsSelected = true;
        }

        /// <summary>
        /// Выбирает восстановление зарядов. Здесь сравнение идёт по Tag, а не по
        /// подписи: подпись можно переписать, а значение в базе от неё не зависит.
        /// </summary>
        private static void SelectRecharge(ComboBox cb, string key)
        {
            string target = string.IsNullOrWhiteSpace(key) ? ItemData.RechargeLong : key;
            foreach (ComboBoxItem ci in cb.Items)
                ci.IsSelected = string.Equals(ci.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase);
            if (cb.SelectedIndex < 0 && cb.Items.Count > 0)
                ((ComboBoxItem)cb.Items[0]).IsSelected = true;
        }

        /// <summary>
        /// Приводит формулу восстановления к виду «1к3». Пусто — как было раньше
        /// (до максимума). Непонятную формулу не сохраняем и говорим об этом.
        /// </summary>
        private string NormalizeRechargeDice(string text)
        {
            string raw = (text ?? "").Trim();
            if (raw.Length == 0) return "";
            if (!Services.DiceFormulaParser.TryParseExpression(raw, out _, out string error))
            {
                Toast("Кость восстановления не понята: " + error, true);
                return "";
            }
            return Services.DiceFormulaParser.Normalize(raw);
        }
        private static string SelectedRecharge(ComboBox cb)
            => (cb.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ItemData.RechargeLong;

        #endregion

        #region === СПИСОК И ПЛИТКИ ПРЕДМЕТОВ ===

        // === === === === === === === === === === === === === === ===
        // Перерисовка списка/плиток с учётом фильтров
        // === === === === === === === === === === === === === === ===
        private void RefreshList()
        {
            spItemList.Children.Clear();
            wpItemGrid.Children.Clear();
            string tagFilter = cbFilterTag.SelectedIndex > 0
                ? (cbFilterTag.SelectedItem as ComboBoxItem)?.Content?.ToString() : null;

            var dt = _inventory.GetFilteredInventory(txtSearch.Text, "Все", chkEquippedOnly.IsChecked == true);
            foreach (DataRow row in dt.Rows)
            {
                int id = Convert.ToInt32(row["id_item"]);
                if (tagFilter != null && !_inventory.GetTagsForItem(id).Contains(tagFilter)) continue;
                spItemList.Children.Add(BuildItemCard(row));
                wpItemGrid.Children.Add(BuildItemTile(row));
            }
        }

        // === === === === === === === === === === === === === === ===
        // Карточка предмета в режиме списка (с тултипом)
        // === === === === === === === === === === === === === === ===
        private UIElement BuildItemCard(DataRow row)
        {
            int id = Convert.ToInt32(row["id_item"]);
            string name = row["name"]?.ToString() ?? "";
            string category = Cell(row, "category")?.ToString() ?? "";
            string desc = Cell(row, "description")?.ToString() ?? "";
            int qty = Convert.ToInt32(Cell(row, "quantity", 1));
            decimal weight = Convert.ToDecimal(Cell(row, "weight", 0m));
            bool equipped = _inventory.CheckIfItemEquipped(id).isEquipped;
            // В списке показываем отдельное фото, если оно выбрано: надо
            // разделить фото списка и фото инспектора
            string photo = FirstNotEmpty(
                Cell(row, "list_photo_path")?.ToString(),
                Cell(row, "PhotoPath")?.ToString());

            var card = new Border
            {
                Background = (Brush)FindResource("B.Slate"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                ToolTip = BuildTooltip(name, $"{category} · {qty} шт · {weight:F1} кг", desc)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            UIElement art = LoadItemArt(photo, name, 36);
            Grid.SetColumn(art, 0);
            grid.Children.Add(art);

            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock { Text = name, Foreground = (Brush)FindResource("B.Ink"), FontWeight = FontWeights.SemiBold, FontSize = 13 });
            if (qty > 1) nameRow.Children.Add(new TextBlock { Text = $" ×{qty}", Foreground = (Brush)FindResource("B.Accent"), FontSize = 12, FontWeight = FontWeights.Bold });
            info.Children.Add(nameRow);

            var subRow = new StackPanel { Orientation = Orientation.Horizontal };
            subRow.Children.Add(new TextBlock { Text = category, Foreground = (Brush)FindResource("B.Mute"), FontSize = 11 });
            if (equipped)
            {
                subRow.Children.Add(new TextBlock { Text = "  •", Foreground = (Brush)FindResource("B.Mute"), FontSize = 11 });
                subRow.Children.Add(new TextBlock { Text = " надето", Foreground = (Brush)FindResource("B.Frost"), FontSize = 11, FontWeight = FontWeights.SemiBold });
            }
            info.Children.Add(subRow);

            // Теги прямо в списке, чтобы кастомные теги были видно без открытия инспектора
            var tagNames = _inventory.GetTagsForItem(id);
            if (tagNames.Count > 0) info.Children.Add(BuildTagChips(tagNames));

            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            card.Child = grid;
            card.MouseLeftButtonUp += (_, __) => SelectItem(id);
            return card;
        }

        /// <summary>Плашки тегов предмета (видны прямо в списке, а не только в инспекторе).</summary>
        private UIElement BuildTagChips(System.Collections.Generic.IEnumerable<string> tags, double fontSize = 10)
        {
            var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var tag in tags)
            {
                panel.Children.Add(BuildTagChip(tag, fontSize));
            }
            return panel;
        }

        /// <summary>Одна плашка тега: «Сокровище» подсвечивается золотом, кастомные — арканом.</summary>
        private Border BuildTagChip(string tag, double fontSize = 10)
        {
            bool treasure = string.Equals(tag, "Сокровище", StringComparison.CurrentCultureIgnoreCase);
            var text = new TextBlock
            {
                Text = tag,
                FontSize = fontSize,
                Foreground = treasure ? (Brush)FindResource("B.Accent") : (Brush)FindResource("B.Arcane")
            };
            return new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 5, 4),
                Background = treasure ? (Brush)FindResource("B.Panel") : (Brush)FindResource("B.PanelHi"),
                BorderBrush = treasure ? (Brush)FindResource("B.Accent") : (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                Child = text
            };
        }

        /// <summary>Первое непустое значение: удобно для фото списка и карточки.</summary>
        private static string FirstNotEmpty(string first, string second)
            => !string.IsNullOrWhiteSpace(first) ? first : (second ?? "");

        // === === === === === === === === === === === === === === ===
        // Плитка предмета (96×118) с тултипом
        // === === === === === === === === === === === === === === ===
        private UIElement BuildItemTile(DataRow row)
        {
            int id = Convert.ToInt32(row["id_item"]);
            string name = row["name"]?.ToString() ?? "";
            string desc = Cell(row, "description")?.ToString() ?? "";
            int qty = Convert.ToInt32(Cell(row, "quantity", 1));
            decimal weight = Convert.ToDecimal(Cell(row, "weight", 0m));
            // В списке показываем отдельное фото, если оно выбрано: надо
            // разделить фото списка и фото инспектора
            string photo = FirstNotEmpty(
                Cell(row, "list_photo_path")?.ToString(),
                Cell(row, "PhotoPath")?.ToString());

            var tile = new Border
            {
                Width = 96,
                Height = 118,
                Background = (Brush)FindResource("B.Slate"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(4),
                Padding = new Thickness(6),
                Cursor = Cursors.Hand,
                ClipToBounds = true,
                ToolTip = BuildTooltip(name, $"{qty} шт · {weight:F1} кг", desc)
            };

            var sp = new StackPanel();
            sp.Children.Add(LoadItemArt(photo, name, 72));
            sp.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = (Brush)FindResource("B.Ink"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0)
            });

            var tileTags = _inventory.GetTagsForItem(id);
            if (tileTags.Count > 0)
            {
                var chips = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
                foreach (var tag in tileTags.Take(2))
                    chips.Children.Add(BuildTagChip(tag, 9));
                sp.Children.Add(chips);
            }

            tile.Child = sp;
            tile.MouseLeftButtonUp += (_, __) => SelectItem(id);
            return tile;
        }

        // === === === === === === === === === === === === === === ===
        // Единая загрузка арта предмета: фото или LetterBadge
        // animate: true только для инспектора — в списке и на силуэте
        // показываем первый кадр, иначе на каждую плитку заводится таймер
        // === === === === === === === === === === === === === === ===
        private UIElement LoadItemArt(string photo, string name, double size, bool animate = false)
        {
            if (!string.IsNullOrWhiteSpace(photo))
            {
                string full = PhotoHelper.ResolvePhotoPath(photo);
                if (full != null)
                {
                    try
                    {
                        var image = LoadItemImage(full);
                        if (image != null)
                        {
                            return new Border
                            {
                                Width = size,
                                Height = size,
                                CornerRadius = new CornerRadius(6),
                                ClipToBounds = true,
                                Child = image
                            };
                        }
                    }
                    catch { }
                }
            }
            return LetterBadge(name, size);
        }

        // === === === === === === === === === === === === === === ===
        // Загрузка картинки предмета.
        // GIF: кадры декодируются один раз и кэшируются (GifFrameCache);
        // анимация — только там, где она нужна (инспектор),
        // в списке и на силуэте — статичный первый кадр.
        // === === === === === === === === === === === === === === ===
        private static Controls.HoverGif LoadItemImage(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;

            // HoverGif показывает статичный первый кадр и анимирует только при
            // наведении. Постоянная анимация держала весь набор кадров в памяти:
            // у гифки 400×400 из 60 кадров это около 38 МБ на каждую плитку.
            var hover = new Controls.HoverGif { Stretch = Stretch.UniformToFill };
            hover.SetImage(fullPath);
            return hover;
        }

        /// <summary>Останавливает анимацию инспектора (при смене предмета и на выгрузке вкладки).</summary>

        // === === === === === === === === === === === === === === ===
        // Тултип предмета (имя, категория, краткое описание)
        // === === === === === === === === === === === === === === ===
        private ToolTip BuildTooltip(string name, string sub, string desc)
        {
            var tip = new StackPanel();
            tip.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.Bold, FontSize = 13 });
            tip.Children.Add(new TextBlock { Text = sub, Foreground = (Brush)FindResource("B.Mute"), FontSize = 11 });
            if (!string.IsNullOrWhiteSpace(desc))
                tip.Children.Add(new TextBlock
                {
                    Text = desc.Length > 140 ? desc.Substring(0, 140) + "…" : desc,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 260,
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            return new ToolTip { Content = tip };
        }

        // === === === === === === === === === === === === === === ===
        // Плейсхолдер-буква (цвет детерминирован по хешу имени)
        // === === === === === === === === === === === === === === ===
        private Border LetterBadge(string name, double size)
        {
            int h = Math.Abs(name?.GetHashCode() ?? 0);
            var c1 = Dim((Color)ColorConverter.ConvertFromString("#" + PlaceholderPalette[h % PlaceholderPalette.Length]));
            var c2 = Dim((Color)ColorConverter.ConvertFromString("#" + PlaceholderPalette[(h / 7) % PlaceholderPalette.Length]));
            var letter = (name?.FirstOrDefault(char.IsLetterOrDigit)).ToString()?.ToUpper();
            if (string.IsNullOrEmpty(letter)) letter = "✶";
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1)),
                Child = new TextBlock
                {
                    Text = letter,
                    FontFamily = (FontFamily)FindResource("F.Display"),
                    FontSize = size * 0.45,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        // === === === === === === === === === === === === === === ===
        // Переключение режимов Список/Плитки
        // === === === === === === === === === === === === === === ===
        private void BtnViewToggle_Click(object sender, RoutedEventArgs e)
        {
            _viewGrid = !_viewGrid;
            scrollList.Visibility = _viewGrid ? Visibility.Collapsed : Visibility.Visible;
            scrollGrid.Visibility = _viewGrid ? Visibility.Visible : Visibility.Collapsed;
            btnViewToggle.Content = _viewGrid ? DmUiKit.IconText("note", "Список", 15) : DmUiKit.IconText("grid", "Плитки", 15);
        }

        #endregion

        #region === ИНСПЕКТОР ПРЕДМЕТА ===

        // === === === === === === === === === === === === === === ===
        // Выбор предмета и показ деталей в инспекторе
        // === === === === === === === === === === === === === === ===
        private void SelectItem(int id)
        {
            _selectedItemId = id;
            ShowItemDetails(_inventory.GetItem(id));
        }

        // === === === === === === === === === === === === === === ===
        // Отображение полной информации о предмете
        // === === === === === === === === === === === === === === ===
        private void ShowItemDetails(ItemData it)
        {
            if (it == null) { ResetDetails(); return; }

            // --- Фото инспектора ---
            //
            // Это HoverGif: статичный первый кадр, анимация при наведении.
            // Раньше здесь стоял обычный Image, поэтому гифка в инспекторе
            // не крутилась вовсе.
            imgItemPhoto.ClearImage();
            if (!string.IsNullOrWhiteSpace(it.PhotoPath))
            {
                string full = PhotoHelper.ResolvePhotoPath(it.PhotoPath);
                if (!string.IsNullOrEmpty(full))
                {
                    try { imgItemPhoto.SetImage(full); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[InventoryView] фото инспектора: {ex.Message}"); }
                }
            }

            // --- Основные поля ---
            txtItemName.Text = it.Name;
            txtItemCategory.Text = string.IsNullOrWhiteSpace(it.Category) ? "Категория" : it.Category;
            txtItemRarity.Text = string.IsNullOrWhiteSpace(it.Rarity) ? "Редкость" : it.Rarity;
            txtItemDescription.Text = string.IsNullOrWhiteSpace(it.Description) ? "Описание предмета..." : it.Description;
            txtItemCost.Text = $"{it.CostGold} зм {it.CostSilver} см {it.CostCopper} мм";
            txtItemNotes.Text = string.IsNullOrWhiteSpace(it.Notes) ? "Нет заметок" : it.Notes;

            // --- Характеристики ---
            spItemStats.Children.Clear();
            spItemStats.Children.Add(new TextBlock
            {
                Text = "Характеристики:",
                Foreground = (Brush)FindResource("B.Mute"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            AddStat($"Количество: {it.Quantity}");
            AddStat($"Вес: {it.Weight:F1} кг");
            if (!string.IsNullOrWhiteSpace(it.DamageDice)) AddStat($"Урон: {ToCyr(it.DamageDice)} {it.DamageType}");
            if (it.ArmorClassBonus != 0) AddStat($"Бонус КД: +{it.ArmorClassBonus}");
            var loc = _equipment.GetItemEquipmentLocations(it.Id);
            if (loc.Count > 0) AddStat($"Надето: {string.Join(", ", loc)}");

            // --- Блок броска урона (виден только если есть кости) ---
            if (!string.IsNullOrWhiteSpace(it.DamageDice))
            {
                brDamageRoll.Visibility = Visibility.Visible;
                txtDamageFormula.Text = $"{ToCyr(it.DamageDice)} {it.DamageType}".Trim();
            }
            else brDamageRoll.Visibility = Visibility.Collapsed;

            // --- Теги ---
            wpItemTags.Children.Clear();
            foreach (var t in _inventory.GetTagsForItem(it.Id))
                wpItemTags.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Padding = new Thickness(9, 3, 9, 3),
                    Margin = new Thickness(0, 0, 6, 6),
                    Background = (Brush)FindResource("B.PanelHi"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = t, Foreground = (Brush)FindResource("B.Arcane"), FontSize = 11 }
                });

            // --- Заряды ---
            if (it.MaxCharges > 0)
            {
                spItemCharges.Visibility = Visibility.Visible;

                // Про длинный отдых не пишем: это обычное поведение. А вот
                // «на коротком» и «никогда» стоит показать — по ним видно,
                // какие предметы требуют отдельного внимания
                bool ordinary = string.IsNullOrWhiteSpace(it.Recharge)
                                || it.Recharge == ItemData.RechargeLong;
                txtCharges.Text = ordinary
                    ? $"{it.Charges} / {it.MaxCharges}"
                    : $"{it.Charges} / {it.MaxCharges} · {ItemData.RechargeLabel(it.Recharge).ToLowerInvariant()}";
            }
            else spItemCharges.Visibility = Visibility.Collapsed;

            void AddStat(string text) => spItemStats.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = (Brush)FindResource("B.Ink"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 3)
            });
        }

        // === === === === === === === === === === === === === === ===
        // Сброс инспектора в плейсхолдер
        // === === === === === === === === === === === === === === ===
        private void ResetDetails()
        {
            imgItemPhoto.ClearImage();
            txtItemName.Text = "Выберите предмет";
            txtItemCategory.Text = "Категория";
            txtItemRarity.Text = "Редкость";
            txtItemDescription.Text = "Описание предмета...";
            txtItemCost.Text = "0 зм";
            txtItemNotes.Text = "Нет заметок";
            brDamageRoll.Visibility = Visibility.Collapsed;
            spItemCharges.Visibility = Visibility.Collapsed;
            wpItemTags.Children.Clear();
            spItemStats.Children.Clear();
            spItemStats.Children.Add(new TextBlock
            {
                Text = "Характеристики:",
                Foreground = (Brush)FindResource("B.Mute"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            });
        }

        // === === === === === === === === === === === === === === ===
        // Урон оружия бросается на вкладке «Атаки»: там у атаки своё попадание,
        // несколько типов урона и общая история. Здесь только переносим атаку туда
        // === === === === === === === === === === === === === === ===
        private void BtnSendToAttacks_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItemId == null) return;

            var item = _inventory.GetItem(_selectedItemId.Value);
            if (item == null) return;

            if (string.IsNullOrWhiteSpace(item.DamageDice))
            {
                Toast("У предмета не указана кость урона — сначала впишите её в правке предмета", true);
                return;
            }

            int added = Services.AttackService.CollectOneFromInventory(_characterId, item.Id);
            if (added > 0) Toast($"«{item.Name}» добавлен в атаки");
            else Toast($"«{item.Name}» уже есть во вкладке «Атаки»");

            // Сразу показываем, где теперь живёт урон
            if (Window.GetWindow(this) is CharacterInfoWindow window) window.OpenAttacksTab();
        }

        // === === === === === === === === === === === === === === ===
        // Использование заряда предмета
        // === === === === === === === === === === === === === === ===
        private void BtnUseCharge_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItemId == null) return;
            if (_inventory.UseCharge(_selectedItemId.Value))
            {
                RefreshList();
                ShowItemDetails(_inventory.GetItem(_selectedItemId.Value));
                Toast("Заряд использован");
            }
            else Toast("Заряды закончились", true);
        }

        #endregion

        #region === СОКРОВИЩА И ВЕС ===

        // === === === === === === === === === === === === === === ===
        // Блок сокровищ (сумма + список)
        // === === === === === === === === === === === === === === ===
        private void RefreshTreasure()
        {
            var (total, items) = _inventory.GetTreasureSummary(_treasureOnly);
            txtTreasureTotal.Text = $"Общая стоимость: {total:F2} зм";
            spTreasureList.Children.Clear();
            foreach (var s in items)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock
                {
                    Text = $"{s.Name} ×{s.Quantity}",
                    Foreground = (Brush)FindResource("B.Ink"),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                var val = new TextBlock
                {
                    Text = $"{s.TotalValue:F1}",
                    Foreground = (Brush)FindResource("B.Accent"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                };
                Grid.SetColumn(val, 1);
                g.Children.Add(val);
                spTreasureList.Children.Add(g);
            }
            if (items.Count == 0)
                spTreasureList.Children.Add(new TextBlock
                {
                    Text = _treasureOnly ? "Сокровищ нет" : "Предметов нет",
                    Foreground = (Brush)FindResource("B.Mute"),
                    FontSize = 11
                });
        }

        // === === === === === === === === === === === === === === ===
        // Тумблер «Сокровища / Все предметы»
        // === === === === === === === === === === === === === === ===
        private void BtnToggleTreasureMode_Click(object sender, RoutedEventArgs e)
        {
            _treasureOnly = !_treasureOnly;
            btnToggleTreasureMode.Content = _treasureOnly ? DmUiKit.IconText("backpack", "Все предметы", 15) : DmUiKit.IconText("coin", "Только сокровища", 15);
            RefreshTreasure();
        }

        // === === === === === === === === === === === === === === ===
        // Общий вес инвентаря и прогресс-бар
        // === === === === === === === === === === === === === === ===
        private void RefreshWeight()
        {
            var (w, cnt) = _inventory.CalculateTotalWeight();
            decimal max = _inventory.GetMaxCarryingCapacity();
            txtCurrentWeight.Text = $"{w:F1} кг";

            // Поле не трогаем, пока игрок в нём печатает: иначе ввод затирался бы
            if (!txtMaxWeightEdit.IsKeyboardFocusWithin)
                txtMaxWeightEdit.Text = max.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

            // Пока предел не задан, полосы загрузки нет: иначе она показывала бы
            // ложную заполненность
            double pw = ((Border)brWeightProgress.Parent).ActualWidth;
            if (max > 0 && txtWeightHint != null) txtWeightHint.Visibility = Visibility.Collapsed;
            if (max <= 0 && txtWeightHint != null) txtWeightHint.Visibility = Visibility.Visible;

            if (pw > 0 && max <= 0)
            {
                brWeightProgress.Width = 0;
                return;
            }

            if (pw > 0 && max > 0)
            {
                double ratio = Math.Min(1, (double)(w / max));
                brWeightProgress.Width = pw * ratio;
                brWeightProgress.Background = (Brush)FindResource(ratio > 0.85 ? "B.Danger" : "B.Frost");
            }
        }

        /// <summary>Игрок задал максимальный вес: сохраняем и обновляем полосу.</summary>
        private void TxtMaxWeightEdit_LostFocus(object sender, RoutedEventArgs e)
            => ApplyMaxWeightFromField();

        private void TxtMaxWeightEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ApplyMaxWeightFromField();
            Keyboard.ClearFocus();
        }

        /// <summary>
        /// В поля с дробным числом пускаем только цифры и один разделитель.
        /// Так «2....5» или «2,,,5» физически невозможно напечатать, и значение
        /// не сбрасывается в ноль.
        /// </summary>
        private void DecimalField_PreviewTextInput(object sender, TextCompositionEventArgs e)
            => Helpers.NumberInput.PreviewDigitsAndSeparator(sender, e);

        /// <summary>В целочисленные поля пускаем только цифры.</summary>
        private void IntField_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c)) { e.Handled = true; return; }
            }
        }

        private void ApplyMaxWeightFromField()
        {
            // Принимаем и точку, и запятую: «2.5» раньше считалось мусором и
            // сбрасывало значение в ноль
            if (!Helpers.NumberInput.TryParse(txtMaxWeightEdit.Text, out decimal value) || value < 0)
            {
                // Мусор в поле — возвращаем сохранённое значение и объясняем формат
                txtMaxWeightEdit.Text = _inventory.GetMaxCarryingCapacity()
                    .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                Toast("Предел веса — число, например 150 или 150.5", true);
                return;
            }

            _inventory.SetMaxCarryingCapacity(value);
            RefreshWeight();
            Toast($"Максимальный вес: {value:0.##} кг");
        }

        #endregion

        #region === РЕДАКТОР ПРЕДМЕТА ===

        // === === === === === === === === === === === === === === ===
        // СВОЙ ПРЕДМЕТ В МАГАЗИНЕ
        //
        // Без файла справочника магазин пуст, и с ним нельзя взаимодействовать.
        // Созданный здесь предмет попадает в тот же каталог, что импортированные,
        // поэтому ведёт себя как обычный: его видно в списке и можно купить.
        //
        // Показывается ОВЕРЛЕЕМ, а не отдельным окном: так текст чёткий и
        // используются те же тёмные стили полей, что и в редакторе инвентаря.
        // === === === === === === === === === === === === === === ===

        /// <summary>Что делаем при сохранении: 0 — создаём, иначе изменяем.</summary>
        private int _shopItemEditingId;

        /// <summary>Данные, с которыми открыт оверлей: нужны для проверки на похожий.</summary>
        private Services.SrdItemRow _shopItemInitial;

        /// <summary>Человек подтвердил, что хочет сохранить похожий предмет.</summary>
        private bool _shopItemForceSave;

        private void BtnShopCreateItem_Click(object sender, RoutedEventArgs e)
        {
            // Логируем каждый шаг: если что-то падает, это видно в журнале
            Debug.WriteLine("[Магазин] нажата кнопка «Создать предмет»");

            try
            {
                OpenShopItemOverlay(0, null);
                Debug.WriteLine($"[Магазин] оверлей создания: видимость = {overlayShopItem.Visibility}");
            }
            catch (Exception ex)
            {
                // Раньше исключение здесь пропадало молча: обработчик не был
                // обёрнут, и создание предмета просто «ничего не делало»
                Debug.WriteLine($"[Магазин] ОШИБКА открытия оверлея: {ex}");
                Toast("Не удалось открыть окно создания: " + ex.Message, true);
            }
        }

        private void BtnShopEditItem_Click(object sender, RoutedEventArgs e)
        {
            // Изменять можно только свои предметы: справочник перезаписывается
            // при обновлении файла, и правки в нём всё равно потерялись бы
            var selected = _shopSelected;
            if (selected == null)
            {
                Toast("Сначала выберите предмет в списке", true);
                return;
            }

            if (!Services.SrdItemImporter.IsCustom(selected))
            {
                Toast("Это предмет из справочника — его изменить нельзя. Создайте свой.", true);
                return;
            }

            OpenShopItemOverlay(selected.Id, selected);
        }

        /// <summary>Открывает оверлей своего предмета и заполняет поля.</summary>
        private void OpenShopItemOverlay(int itemId, Services.SrdItemRow existing)
        {
            _shopItemEditingId = itemId;
            _shopItemInitial = existing;
            _shopItemForceSave = false;

            txtShopItemTitle.Text = itemId == 0 ? "СВОЙ ПРЕДМЕТ" : "ИЗМЕНИТЬ ПРЕДМЕТ";
            txtShopItemError.Visibility = Visibility.Collapsed;
            pnlShopItemHint.Visibility = Visibility.Collapsed;

            if (si_cmbType.Items.Count == 0)
            {
                foreach (var t in new[] { "Оружие", "Доспех", "Щит", "Зелье", "Свиток", "Кольцо",
                                          "Амулет", "Палочка", "Жезл", "Посох", "Инструмент", "Набор", "Прочее" })
                    si_cmbType.Items.Add(t);

                foreach (var r in new[] { "Обычный", "Необычный", "Редкий", "Очень редкий",
                                          "Легендарный", "Артефакт" })
                    si_cmbRarity.Items.Add(r);
            }

            if (si_cmbDamageType.Items.Count == 0)
            {
                foreach (var d in DamageTypes)
                    si_cmbDamageType.Items.Add(d);
            }

            FillShopItemFields(existing);

            overlayShopItem.Visibility = Visibility.Visible;
            si_txtName.Focus();
        }

        /// <summary>
        /// Заполняет поля оверлея.
        ///
        /// Отдельным методом, потому что заполнение может упасть (например,
        /// если вид ещё не построен) — тогда видно, на каком именно шаге.
        /// </summary>
        private void FillShopItemFields(Services.SrdItemRow existing)
        {
            if (existing != null)
            {
                si_txtName.Text = existing.Name ?? "";
                SetComboTextSafe(si_cmbType, existing.ItemType);
                SetComboTextSafe(si_cmbRarity, existing.Rarity);
                si_txtWeight.Text = existing.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
                si_txtAc.Text = existing.Ac.ToString();
                si_txtDamage.Text = existing.Damage ?? "";
                SetDamageTypeSafe(existing.DamageType);
                si_txtDescription.Text = existing.Description ?? "";
                si_chkAttunement.IsChecked = existing.Attunement;
                si_chkMagic.IsChecked = existing.IsMagic;
            }
            else
            {
                si_txtName.Text = "";
                if (si_cmbType.Items.Count > 0) si_cmbType.SelectedIndex = 0;
                if (si_cmbRarity.Items.Count > 0) si_cmbRarity.SelectedIndex = 0;
                si_txtWeight.Text = "0";
                si_txtAc.Text = "0";
                si_txtDamage.Text = "";
                SetDamageTypeSafe("");
                si_txtDescription.Text = "";
                si_chkAttunement.IsChecked = false;
                si_chkMagic.IsChecked = false;
            }
        }

        /// <summary>
        /// Тип урона для сохранения: из пункта списка берём английскую часть
        /// («Fire (огонь)» → «Fire»), как хранит справочник. Если игрок вписал
        /// своё название, сохраняем его целиком.
        /// </summary>
        private string ReadDamageType()
        {
            string raw = (si_cmbDamageType.Text ?? "").Trim();
            if (raw.Length == 0) return "";

            int bracket = raw.IndexOf('(');
            if (bracket > 0) return raw.Substring(0, bracket).Trim();
            return raw;
        }

        /// <summary>
        /// Ставит тип урона: сначала ищем английское название в списке (так хранит
        /// справочник), иначе показываем как есть — своим предметом игрок может
        /// назвать тип по-своему.
        /// </summary>
        private void SetDamageTypeSafe(string damageType)
        {
            string wanted = (damageType ?? "").Trim();
            si_cmbDamageType.Text = wanted;

            if (wanted.Length == 0) return;
            foreach (var item in si_cmbDamageType.Items)
            {
                if (string.Equals(item?.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    si_cmbDamageType.SelectedItem = item;
                    return;
                }
            }
        }

        /// <summary>
        /// Типы урона по рулбуку: в списке по-русски, в базу уходит английское
        /// название — так же, как в справочнике, иначе фильтры не совпадут.
        /// </summary>
        private static readonly string[] DamageTypes =
        {
            "Bludgeoning (дробящий)", "Piercing (колющий)", "Slashing (рубящий)",
            "Acid (кислота)", "Cold (холод)", "Fire (огонь)", "Force (силовой)",
            "Lightning (электричество)", "Necrotic (некротический)", "Poison (яд)",
            "Psychic (психический)", "Radiant (излучение)", "Thunder (звук)"
        };

        /// <summary>
        /// Ставит текстом выбранный пункт списка.
        ///
        /// У нередактируемого списка свойства Text нет — присваивание бросило бы
        /// исключение. Поэтому ищем подходящий пункт и выбираем его.
        /// </summary>
        private static void SetComboTextSafe(ComboBox combo, string value)
        {
            if (combo == null) return;

            string wanted = (value ?? "").Trim();
            if (wanted.Length == 0)
            {
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
                return;
            }

            foreach (var item in combo.Items)
            {
                if (string.Equals(item?.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            // Такого пункта нет: добавляем, чтобы значение не потерялось
            combo.Items.Add(wanted);
            combo.SelectedItem = wanted;
        }

        private void OverlayShopItem_Cancel(object sender, RoutedEventArgs e)
            => overlayShopItem.Visibility = Visibility.Collapsed;

        private void OverlayShopItem_Background_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) overlayShopItem.Visibility = Visibility.Collapsed;
        }

        /// <summary>Выбрать уже существующий похожий предмет вместо создания нового.</summary>
        private void BtnShopItemUseExisting_Click(object sender, RoutedEventArgs e)
        {
            overlayShopItem.Visibility = Visibility.Collapsed;
            _shopFilter.Search = (si_txtName.Text ?? "").Trim();
            shopSearch.Text = _shopFilter.Search;
            RefreshShopList();
            Toast("Похожие предметы показаны в списке");
        }

        /// <summary>Сохранить, несмотря на предупреждение о похожем предмете.</summary>
        private void BtnShopItemSaveAnyway_Click(object sender, RoutedEventArgs e)
        {
            _shopItemForceSave = true;
            BtnShopItemSave_Click(sender, e);
        }

        private void BtnShopItemSave_Click(object sender, RoutedEventArgs e)
        {
            string name = (si_txtName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                ShowShopItemError("Укажите название предмета — без него он не найдётся в магазине.");
                return;
            }

            var row = new Services.SrdItemRow
            {
                Name = name,
                ItemType = (si_cmbType.Text ?? "").Trim(),
                Rarity = (si_cmbRarity.Text ?? "").Trim(),
                Weight = Helpers.NumberInput.ToDouble(si_txtWeight.Text),
                Ac = ToInt(si_txtAc.Text),
                Damage = (si_txtDamage.Text ?? "").Trim(),
                DamageType = ReadDamageType(),
                Description = (si_txtDescription.Text ?? "").Trim(),
                Attunement = si_chkAttunement.IsChecked == true,
                IsMagic = si_chkMagic.IsChecked == true,
                Book = Services.SrdItemImporter.CustomBook,
                Source = Services.SrdItemImporter.CustomBook
            };

            // ── Проверка на похожий предмет ──
            //
            // Защита от неумелого пользователя: нельзя завести второй предмет,
            // который ничем не отличается от уже имеющегося. Сравниваем то, что
            // описывает предмет: название, описание, кость и тип урона, тип,
            // редкость и бонус КД.
            if (!_shopItemForceSave)
            {
                var twin = Services.SrdItemImporter.FindSimilar(row, _shopItemEditingId);
                if (twin != null)
                {
                    txtShopItemHint.Text =
                        $"В каталоге уже есть «{twin.Name}» с такими же характеристиками " +
                        $"({twin.ItemType}, {twin.Rarity}, урон «{(string.IsNullOrWhiteSpace(twin.Damage) ? "—" : twin.Damage)}»). " +
                        "Создавать второй такой же не нужно — выберите существующий или измените описание.";
                    pnlShopItemHint.Visibility = Visibility.Visible;
                    txtShopItemError.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            int id = Services.SrdItemImporter.SaveCustomItem(row);
            if (id <= 0)
            {
                ShowShopItemError("Не удалось сохранить предмет. Попробуйте другое название.");
                return;
            }

            overlayShopItem.Visibility = Visibility.Collapsed;

            // Свой предмет мог попасть за предел выдачи (в каталоге десятки тысяч
            // записей, а список показывает первые 400). Включаем фильтр «только
            // свои», чтобы созданное сразу было видно — иначе кажется, что
            // создание не сработало.
            shopOnlyCustom.IsChecked = true;
            _shopFilter.OnlyCustom = true;
            _shopFilter.Search = "";

            _shopInitialized = false;
            _shopLoading = false;
            EnsureShopInitialized();
            Toast(_shopItemEditingId == 0
                ? "Свой предмет добавлен. Показаны только свои предметы."
                : "Предмет изменён. Показаны только свои предметы.");
        }

        private void ShowShopItemError(string text)
        {
            txtShopItemError.Text = text;
            txtShopItemError.Visibility = Visibility.Visible;
            pnlShopItemHint.Visibility = Visibility.Collapsed;
        }

        private static double ToDouble(string raw)
        {
            string text = (raw ?? "").Trim().Replace(',', '.');
            return double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0;
        }

        private static int ToInt(string raw)
        {
            string text = (raw ?? "").Trim();
            return int.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;
        }

        // === === === === === === === === === === === === === === ===
        // Открытие редактора (0 = новый предмет)
        // === === === === === === === === === === === === === === ===
        private void OpenEditor(int itemId)
        {
            _editingItemId = itemId;
            _currentItemPhotoPath = null;
            _currentItemListPhotoPath = "";
            _pendingPhotoSource = null;
            ed_imgPreview.Source = null;
            ed_photoPlaceholder.Visibility = Visibility.Visible;

            ItemData it = itemId == 0 ? null : _inventory.GetItem(itemId);

            // Запоминаем исходный предмет: заряды и настройка в форме не
            // показываются, но при сохранении их надо вернуть как было
            _editingItem = it;

            txtEditorTitle.Text = it == null ? "НОВЫЙ ПРЕДМЕТ" : "РЕДАКТОР ПРЕДМЕТА";

            // --- Заполнение полей ---
            ed_txtName.Text = it?.Name ?? "";
            SelectCombo(ed_cbCategory, it?.Category, "Прочее");
            SelectCombo(ed_cbRarity, it?.Rarity, "Обычный");
            ed_txtDescription.Text = it?.Description ?? "";
            ed_txtQuantity.Text = (it?.Quantity ?? 1).ToString();
            ed_txtWeight.Text = (it?.Weight ?? 0).ToString();
            ed_txtCostGold.Text = (it?.CostGold ?? 0).ToString();
            ed_txtCostSilver.Text = (it?.CostSilver ?? 0).ToString();
            ed_txtCostCopper.Text = (it?.CostCopper ?? 0).ToString();
            ed_cbDamageDice.Text = ToCyr(it?.DamageDice);
            SelectCombo(ed_cbDamageType, it?.DamageType, "");
            ed_txtArmorBonus.Text = (it?.ArmorClassBonus ?? 0).ToString();
            ed_txtRechargeDice.Text = it?.RechargeDice ?? "";            ed_txtMaxCharges.Text = (it?.MaxCharges ?? 0).ToString();
            SelectRecharge(ed_cbRecharge, it?.Recharge);
            ed_txtNotes.Text = it?.Notes ?? "";

            // --- Фото ---
            _currentItemPhotoPath = it?.PhotoPath;

            // Отдельное фото для строки списка (в магазине такая возможность уже есть)
            _currentItemListPhotoPath = it?.ListPhotoPath;
            UpdateListPhotoControls();

            if (!string.IsNullOrWhiteSpace(_currentItemPhotoPath))
            {
                string full = PhotoHelper.ResolvePhotoPath(_currentItemPhotoPath);
                if (full != null) SetPreview(full);
            }

            // --- Теги предмета: выбор через отдельное окно ---
            _editorTags = itemId == 0 ? new List<string>() : _inventory.GetTagsForItem(itemId);
            UpdateEditorTagsSummary();

            overlayItemEditor.Visibility = Visibility.Visible;
        }

        // === === === === === === === === === === === === === === ===
        // Установка превью фото в редакторе
        // === === === === === === === === === === === === === === ===

        /// <summary>
        /// Выбирает отдельное фото для строки списка.
        ///
        /// Требуется: иногда первый кадр гифки некрасивый, и в
        /// списке хочется показать другую картинку, а в инспекторе — основную.
        /// </summary>
        private void Ed_BtnListPhoto_Click(object sender, RoutedEventArgs e)
        {
            if (!CanPickPhoto()) return;

            _pickingPhoto = true;
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Изображения (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp|Все файлы (*.*)|*.*",
                    Title = "Фото предмета для строки списка",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                };
                if (dialog.ShowDialog() != true) return;

                string extension = Path.GetExtension(dialog.FileName);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Items");
                Directory.CreateDirectory(dir);

                // Имя своё, чтобы файл слона не путался с основным фото предмета
                string rel = $"Items/itemlist_{DateTime.Now.Ticks}{extension.ToLowerInvariant()}";
                File.Copy(dialog.FileName, Path.Combine(PhotoHelper.GetWritableRoot(), rel), true);

                // Прежний файл списка убираем, иначе они копятся
                RemoveOldListPhotoFile();

                _currentItemListPhotoPath = rel;
                UpdateListPhotoControls();
                Toast("Фото для списка выбрано");
            }
            catch (Exception ex)
            {
                Toast($"Фото не сохранено: {ex.Message}", true);
            }
            finally
            {
                MarkPhotoPicked();
                _pickingPhoto = false;
            }
        }

        /// <summary>Убирает отдельное фото списка: в списке снова будет основное фото.</summary>
        private void Ed_BtnClearListPhoto_Click(object sender, RoutedEventArgs e)
        {
            RemoveOldListPhotoFile();
            _currentItemListPhotoPath = "";
            UpdateListPhotoControls();
            Toast("Фото списка убрано");
        }

        /// <summary>Приводит кнопки и подпись про фото списка в соответствие с состоянием.</summary>
        private void UpdateListPhotoControls()
        {
            bool hasList = !string.IsNullOrWhiteSpace(_currentItemListPhotoPath);
            if (ed_btnClearListPhoto != null)
                ed_btnClearListPhoto.Visibility = hasList ? Visibility.Visible : Visibility.Collapsed;

            if (ed_btnListPhoto != null)
                ed_btnListPhoto.Content = DmUiKit.IconText("image", hasList ? "Заменить фото списка" : "Фото для списка", 15, (Brush)FindResource("B.Ink"));

            if (ed_listPhotoHint != null)
                ed_listPhotoHint.Text = hasList
                    ? "В списке показывается выбранное фото списка, в инспекторе — основное."
                    : "Если отдельного фото списка нет, в списке показывается основное фото.";
        }

        /// <summary>Удаляет файл прежнего фото списка, если он был.</summary>
        private void RemoveOldListPhotoFile()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentItemListPhotoPath)) return;

                string full = Path.Combine(PhotoHelper.GetWritableRoot(),
                    _currentItemListPhotoPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InventoryView] удаление фото списка: {ex.Message}");
            }
        }
        private void SetPreview(string fullPath)
        {
            try
            {
                // В редакторе показываем статичный кадр: здесь фото выбирают,
                // а не рассматривают, поэтому анимация только мешала бы
                ed_imgPreview.Source = Services.GifFrameCache.GetFirstFrame(fullPath);
                ed_photoPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        // === === === === === === === === === === === === === === ===
        // Выбор фото через OpenFileDialog
        // === === === === === === === === === === === === === === ===
        private void Ed_PhotoBorder_Click(object sender, MouseButtonEventArgs e)
        {
            // Диалог выбора файла не должен открываться второй раз от того же
            // действия: после закрытия проводника кнопка мыши ещё может быть
            // нажата, и нажатие приходит повторно. Флаг от этого не спасает
            // (он уже снят), поэтому дополнительно смотрим на время.
            if (_pickingPhoto || (DateTime.UtcNow - _lastPhotoPickUtc).TotalMilliseconds < 500) return;

            _pickingPhoto = true;
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif|All files (*.*)|*.*",
                    Title = "Выберите фото предмета",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                };
                if (dialog.ShowDialog() != true) return;
                _pendingPhotoSource = dialog.FileName;
                SetPreview(dialog.FileName);
            }
            finally
            {
                _lastPhotoPickUtc = DateTime.UtcNow;
                _pickingPhoto = false;
            }
        }

        // === === === === === === === === === === === === === === ===
        // Сохранение предмета (создание/обновление + копирование фото)
        // === === === === === === === === === === === === === === ===
        private void Ed_BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string name = ed_txtName.Text.Trim();
            if (string.IsNullOrEmpty(name)) { Toast("Введите название предмета", true); return; }
            string category = (ed_cbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Прочее";

            // --- Копирование фото в AppData (с исходным расширением) ---
            if (_pendingPhotoSource != null)
            {
                try
                {
                    string extension = Path.GetExtension(_pendingPhotoSource);
                    if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                    string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Items");
                    Directory.CreateDirectory(dir);
                    string rel = $"Items/item_{DateTime.Now.Ticks}{extension.ToLowerInvariant()}";
                    File.Copy(_pendingPhotoSource, Path.Combine(PhotoHelper.GetWritableRoot(), rel), true);
                    _currentItemPhotoPath = rel;
                }
                catch (Exception ex) { Toast($"Фото не сохранено: {ex.Message}", true); }
            }

            // --- Формирование данных ---
            int maxCharges = ParseInt(ed_txtMaxCharges.Text);

            // Заряды и настройка в форме не правятся: их ведёт игра. Раньше
            // сохранение молча обнуляло заряды и снимало настройку, и предмет
            // после любой правки названия выглядел неиспользованным
            int charges = _editingItem?.Charges ?? 0;
            charges = maxCharges > 0 ? Math.Min(charges, maxCharges) : 0;

            var data = new ItemData
            {
                Name = name,
                Category = category,
                Rarity = (ed_cbRarity.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Обычный",
                Description = ed_txtDescription.Text,
                Quantity = Math.Max(1, ParseInt(ed_txtQuantity.Text, 1)),
                Weight = Helpers.NumberInput.ToDecimal(ed_txtWeight.Text),
                CostGold = ParseInt(ed_txtCostGold.Text),
                CostSilver = ParseInt(ed_txtCostSilver.Text),
                CostCopper = ParseInt(ed_txtCostCopper.Text),
                DamageDice = ToD(ed_cbDamageDice.Text),
                DamageType = (ed_cbDamageType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "",
                ArmorClassBonus = ParseInt(ed_txtArmorBonus.Text),
                MaxCharges = maxCharges,
                Charges = charges,
                IsAttuned = _editingItem?.IsAttuned ?? false,
                RechargeDice = NormalizeRechargeDice(ed_txtRechargeDice.Text),                Recharge = SelectedRecharge(ed_cbRecharge),
                Notes = ed_txtNotes.Text,
                PhotoPath = _currentItemPhotoPath ?? "",
                ListPhotoPath = _currentItemListPhotoPath ?? ""
            };

            // --- Сохранение с обработкой дубликатов ---
            int savedId;
            if (_editingItemId == 0)
            {
                var (exists, existingId, _) = _inventory.CheckDuplicateItem(name, category);
                if (exists)
                {
                    _inventory.IncreaseItemQuantity(existingId, data.Quantity);
                    savedId = existingId;
                    Toast($"Добавлено к существующему «{name}»");
                }
                else
                {
                    savedId = _inventory.SaveItem(0, data);
                    Toast("Предмет создан");
                }
            }
            else
            {
                savedId = _inventory.SaveItem(_editingItemId, data);
                Toast("Сохранено");
            }

            // --- Теги предмета ---
            _inventory.SetItemTags(savedId, _editorTags ?? new List<string>());

            overlayItemEditor.Visibility = Visibility.Collapsed;
            RefreshAll();
            SelectItem(savedId);
        }

        // === === === === === === === === === === === === === === ===
        // Закрытие редактора (X и клик по фону)
        // === === === === === === === === === === === === === === ===
        private void OverlayEditor_Close(object sender, RoutedEventArgs e)
            => overlayItemEditor.Visibility = Visibility.Collapsed;

        private void OverlayEditor_Background_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) overlayItemEditor.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region === УДАЛЕНИЕ ПРЕДМЕТА ===

        // === === === === === === === === === === === === === === ===
        // Открытие диалога подтверждения удаления
        // === === === === === === === === === === === === === === ===
        private void BtnDeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItemId == null) return;
            var it = _inventory.GetItem(_selectedItemId.Value);
            if (it == null) return;
            _pendingDeleteId = _selectedItemId;
            _pendingDeleteCellId = null;
            _pendingDeleteGroupId = null;
            _pendingDeleteIsGroup = false;
            txtDeleteConfirmTitle.Text = "Выбросить предмет";
            btnDeleteConfirm.Content = "Выбросить";
            txtDeleteConfirmText.Text = $"«{it.Name}»: в инвентаре {it.Quantity} шт." +
                                        (_equipment.IsItemEquipped(it.Id) ? "\nПредмет будет снят с экипировки." : "");

            // Количество начинаем с единицы: выбрасывать всю стопку по умолчанию опасно
            _pendingDeleteMax = Math.Max(1, it.Quantity);
            _pendingDeleteIsGroup = false;
            txtDeleteQuantity.Text = "1";
            txtDeleteQtyMax.Text = $"из {_pendingDeleteMax}";
            // Если предмет не стопка, спрашивать нечего — прячем выбор количества
            panelDeleteQuantity.Visibility = _pendingDeleteMax > 1 ? Visibility.Visible : Visibility.Collapsed;

            overlayDeleteConfirm.Visibility = Visibility.Visible;
        }

        // === === === === === === === === === === === === === === ===
        // Подтверждение удаления (с снятием с экипировки)
        // === === === === === === === === === === === === === === ===
        private void BtnDeleteConfirm_Click(object sender, RoutedEventArgs e)
        {
            overlayDeleteConfirm.Visibility = Visibility.Collapsed;

            try
            {
                // Удаление группы экипировки
                if (_pendingDeleteIsGroup && _pendingDeleteGroupId.HasValue)
                {
                    var g = _equipment.GetCharacterGroups().FirstOrDefault(x => x.Id == _pendingDeleteGroupId.Value);
                    bool removed = _equipment.DeleteGroup(_pendingDeleteGroupId.Value);
                    if (removed)
                    {
                        RefreshGroups();
                        RefreshList();
                        RefreshWeight();
                        RefreshTreasure();
                        if (overlayGroupCells.Visibility == Visibility.Visible) overlayGroupCells.Visibility = Visibility.Collapsed;
                        Toast(g != null && !string.IsNullOrWhiteSpace(g.GroupName)
                            ? $"Группа «{g.GroupName}» удалена, предметы сняты"
                            : "Группа удалена, предметы сняты");
                    }
                    else Toast("Не удалось удалить группу", true);
                    return;
                }

                // Удаление ячейки внутри группы
                if (_pendingDeleteCellId.HasValue)
                {
                    _equipment.DeleteCell(_pendingDeleteCellId.Value);
                    RefreshGroups();
                    RefreshList();
                    RefreshWeight();
                    PopulateGroupCells();
                    Toast("Ячейка удалена, предмет снят");
                    return;
                }

                // Удаление предмета из инвентаря
                if (_pendingDeleteId.HasValue)
                {
                    var it = _inventory.GetItem(_pendingDeleteId.Value);
                    int total = it?.Quantity ?? 1;

                    // Берём введённое количество и приводим к разумным границам:
                    // раньше здесь всегда стояло «всё», из-за чего стопка исчезала целиком
                    int amount = ClampDeleteQuantity(total);

                    bool ok = _inventory.DeleteItemWithUnequip(_pendingDeleteId.Value, amount, out string err);
                    if (ok)
                    {
                        if (amount >= total) _selectedItemId = null;
                        ResetDetails();
                        RefreshAll();
                        Toast(amount >= total
                            ? "Предмет выброшен"
                            : $"Выброшено {amount} шт., осталось {total - amount}");
                    }
                    else Toast(err, true);
                }
            }
            finally
            {
                _pendingDeleteId = null;
                _pendingDeleteCellId = null;
                _pendingDeleteGroupId = null;
                _pendingDeleteIsGroup = false;
            }
        }

        // === === === === === === === === === === === === === === ===
        // Выбор количества для выброса
        // === === === === === === === === === === === === === === ===

        private void BtnDeleteQtyMinus_Click(object sender, RoutedEventArgs e) => StepDeleteQuantity(-1);
        private void BtnDeleteQtyPlus_Click(object sender, RoutedEventArgs e) => StepDeleteQuantity(+1);

        private void StepDeleteQuantity(int delta)
        {
            int current = ClampDeleteQuantity(_pendingDeleteMax);
            int next = Math.Max(1, Math.Min(_pendingDeleteMax, current + delta));
            txtDeleteQuantity.Text = next.ToString();
        }

        /// <summary>
        /// Приводит введённое количество к допустимому: от 1 до размера стопки.
        /// Пустое или нечисловое значение считается единицей — так безопаснее.
        /// </summary>
        private int ClampDeleteQuantity(int max)
        {
            int maxValue = Math.Max(1, max);
            if (!int.TryParse((txtDeleteQuantity?.Text ?? "").Trim(), out int value) || value < 1)
                return 1;

            return Math.Min(value, maxValue);
        }

        // === === === === === === === === === === === === === === ===
        // Отмена удаления
        // === === === === === === === === === === === === === === ===
        private void OverlayDeleteConfirm_Cancel(object sender, RoutedEventArgs e)
        {
            overlayDeleteConfirm.Visibility = Visibility.Collapsed;
            _pendingDeleteId = null;
            _pendingDeleteCellId = null;
            _pendingDeleteGroupId = null;
            _pendingDeleteIsGroup = false;
        }

        private void OverlayDeleteConfirm_Background_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) overlayDeleteConfirm.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region === ВЫБОР ПРЕДМЕТА ДЛЯ ЯЧЕЙКИ ===

        // === === === === === === === === === === === === === === ===
        // Заполнение списка предметов для экипировки
        // === === === === === === === === === === === === === === ===
        private void PopulateItemSelect(string search)
        {
            spItemSelectList.Children.Clear();
            var dt = _inventory.GetFilteredInventory(search, "Все", false);
            foreach (DataRow row in dt.Rows)
            {
                int id = Convert.ToInt32(row["id_item"]);
                string name = row["name"]?.ToString() ?? "";
                int qty = Convert.ToInt32(Cell(row, "quantity", 1));
                var b = new Border
                {
                    Background = (Brush)FindResource("B.Slate"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand,
                    Tag = id
                };
                b.Child = new TextBlock { Text = $"{name} ×{qty}", Foreground = (Brush)FindResource("B.Ink"), FontSize = 12 };
                b.MouseLeftButtonUp += (_, __) =>
                {
                    _pickedItemId = id;
                    foreach (var child in spItemSelectList.Children.OfType<Border>())
                        child.Background = (Brush)FindResource("B.Slate");
                    b.Background = (Brush)FindResource("B.PanelHi");
                };
                spItemSelectList.Children.Add(b);
            }
        }

        // === === === === === === === === === === === === === === ===
        // Фильтрация списка в оверлее выбора предмета
        // === === === === === === === === === === === === === === ===
        private void TxtItemSelectSearch_TextChanged(object sender, TextChangedEventArgs e)
            => PopulateItemSelect(txtItemSelectSearch.Text);

        // === === === === === === === === === === === === === === ===
        // Подтверждение выбора предмета для ячейки
        // === === === === === === === === === === === === === === ===
        private void BtnItemSelectConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingCellId == null || _pickedItemId == null)
            {
                overlayItemSelect.Visibility = Visibility.Collapsed;
                return;
            }
            var (ok, err, _) = _inventory.CheckEquipAvailability(_pickedItemId.Value);
            if (!ok) { Toast(err, true); return; }
            _equipment.AssignItemToCell(_pendingCellId.Value, _pickedItemId);
            overlayItemSelect.Visibility = Visibility.Collapsed;
            _pendingCellId = null; _pickedItemId = null;
            RefreshGroups(); RefreshList(); RefreshWeight();
            Toast("Экипировано");
        }

        // === === === === === === === === === === === === === === ===
        // Закрытие оверлея выбора предмета
        // === === === === === === === === === === === === === === ===
        private void OverlayItemSelect_Close(object sender, RoutedEventArgs e)
        {
            overlayItemSelect.Visibility = Visibility.Collapsed;
            _pickedItemId = null;
        }

        #endregion

        #region === ГРУППЫ ЭКИПИРОВКИ ===

        // === === === === === === === === === === === === === === ===
        // Перерисовка всех групп на Canvas силуэта
        // === === === === === === === === === === === === === === ===
        private void RefreshGroups()
        {
            var hint = canvasSilhouette.Children.OfType<TextBlock>().FirstOrDefault();
            foreach (var child in canvasSilhouette.Children.OfType<StackPanel>().ToList())
                canvasSilhouette.Children.Remove(child);
            var groups = _equipment.GetCharacterGroups();
            foreach (var g in groups) canvasSilhouette.Children.Add(BuildGroupVisual(g));

            if (hint != null)
            {
                hint.Opacity = groups.Count == 0 ? 0.5 : 0;
                hint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // Кнопка создания группы показывается при наведении на панель экипировки
            btnAddGroup.Opacity = groups.Count == 0 ? 1 : 0.75;
        }

        // === === === === === === === === === === === === === === ===
        // Визуализация группы (заголовок + ячейки + drag)
        // === === === === === === === === === === === === === === ===
        private const double CellSize = 44;      // диаметр ячейки
        private const double RingRadius = 48;    // радиус кольца (центры ячеек)
        private const int MAX_CIRCLES = 6;       // максимум кругов на кольце (включая +N)

        private UIElement BuildGroupVisual(EquipmentGroup g)
        {
            var root = new StackPanel { Tag = g.Id, Background = Brushes.Transparent };
            Canvas.SetLeft(root, g.PositionX);
            Canvas.SetTop(root, g.PositionY);

            int total = g.Cells.Count;
            bool overflow = total > MAX_CIRCLES;
            int visible = overflow ? MAX_CIRCLES - 1 : total;
            int ringCount = visible + (overflow ? 1 : 0);
            double ringSize = RingRadius * 2 + CellSize;
            double cx = ringSize / 2, cy = ringSize / 2;

            // ── кольцо: ячейки по кругу, имя группы в центре ──
            var ring = new Grid
            {
                Width = ringSize,
                Height = ringSize,
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(g.GroupName) ? g.SlotName : g.GroupName,
                Foreground = (Brush)FindResource("B.Accent"),
                FontFamily = (FontFamily)FindResource("F.Display"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Background = Brushes.Transparent,   // ← иначе клик только по глифам букв
                Cursor = Cursors.SizeAll,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Перетащить — переместить · Двойной клик — все ячейки"
            };
            ring.Children.Add(title);

            for (int i = 0; i < visible; i++)
            {
                double angle = -Math.PI / 2 + i * 2 * Math.PI / Math.Max(1, ringCount);
                var cellUi = (FrameworkElement)BuildCellVisual(g.Cells[i]);
                cellUi.HorizontalAlignment = HorizontalAlignment.Left;
                cellUi.VerticalAlignment = VerticalAlignment.Top;
                cellUi.Margin = new Thickness(
                    cx + RingRadius * Math.Cos(angle) - CellSize / 2,
                    cy + RingRadius * Math.Sin(angle) - CellSize / 2, 0, 0);
                ring.Children.Add(cellUi);
            }

            if (overflow)
            {
                int hidden = total - visible;
                double angle = -Math.PI / 2 + visible * 2 * Math.PI / Math.Max(1, ringCount);
                var more = new Border
                {
                    Width = CellSize,
                    Height = CellSize,
                    CornerRadius = new CornerRadius(CellSize / 2),
                    Background = (Brush)FindResource("B.PanelHi"),
                    BorderBrush = (Brush)FindResource("B.Accent"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Ещё {hidden} — открыть список",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(
                        cx + RingRadius * Math.Cos(angle) - CellSize / 2,
                        cy + RingRadius * Math.Sin(angle) - CellSize / 2, 0, 0),
                    Child = new TextBlock
                    {
                        Text = $"+{hidden}",
                        Foreground = (Brush)FindResource("B.Accent"),
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                int groupId = g.Id;
                more.MouseLeftButtonUp += (_, __) => OpenGroupCells(groupId);
                ring.Children.Add(more);
            }
            root.Children.Add(ring);

            // ── сервисные кнопки под кольцом (видны по hover) ──
            var svc = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var btnAdd = new Button { Content = "+", Width = 30, Height = 30, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("B.Frost"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Добавить ячейку" };
            var btnRename = new Button { Width = 30, Height = 30, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Переименовать группу" };
                DmUiKit.ApplyIcon(btnRename, "edit");
            var btnDel = new Button { Width = 30, Height = 30, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Удалить группу (с подтверждением)" };
                DmUiKit.ApplyIcon(btnDel, "close");
            svc.Children.Add(btnAdd);
            svc.Children.Add(btnRename);
            svc.Children.Add(btnDel);
            root.Children.Add(svc);

            root.MouseEnter += (_, __) => svc.Visibility = Visibility.Visible;
            root.MouseLeave += (_, __) => svc.Visibility = Visibility.Collapsed;
            int gid = g.Id;
            btnAdd.Click += (_, __) => { _equipment.CreateCell(gid, g.Cells.Count); RefreshGroups(); };
            btnRename.Click += (_, __) => RenameGroupSafe(gid);
            btnDel.Click += (_, __) => DeleteGroupSafe(gid);

            // ── drag + двойной клик по центральному имени ──
            bool moved = false;
            title.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount >= 2) { OpenGroupCells(gid); return; }
                moved = false;
                title.CaptureMouse();
                var start = e.GetPosition(canvasSilhouette);
                double ox = Canvas.GetLeft(root), oy = Canvas.GetTop(root);

                MouseEventHandler move = null;
                MouseButtonEventHandler up = null;
                move = (ss, ee) =>
                {
                    var p = ee.GetPosition(canvasSilhouette);
                    double dx = p.X - start.X, dy = p.Y - start.Y;
                    if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) moved = true;
                    Canvas.SetLeft(root, Math.Max(0, ox + dx));
                    Canvas.SetTop(root, Math.Max(0, oy + dy));
                };
                up = (ss, ee) =>
                {
                    title.ReleaseMouseCapture();
                    title.MouseMove -= move;
                    title.MouseUp -= up;
                    if (moved) _equipment.UpdateGroupPosition(gid, Canvas.GetLeft(root), Canvas.GetTop(root));
                };
                title.MouseMove += move;
                title.MouseUp += up;
            };

            return root;
        }

        private UIElement BuildCellVisual(EquipmentCell c)
        {
            double inner = CellSize - 4;
            var b = new Border
            {
                Width = CellSize,
                Height = CellSize,
                CornerRadius = new CornerRadius(CellSize / 2),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                BorderBrush = (Brush)FindResource(c.ItemId.HasValue ? "B.Frost" : "B.Line"),
                Background = (Brush)FindResource(c.ItemId.HasValue ? "B.Slate" : "B.Panel"),
                ToolTip = (c.ItemName ?? "Пустая ячейка — клик, чтобы выбрать предмет") + "\nПКМ — удалить ячейку"
            };

            if (c.ItemId.HasValue)
            {
                string full = PhotoHelper.ResolvePhotoPath(c.ItemPhotoPath ?? "");
                if (full != null)
                {
                    try
                    {
                        var img = new BitmapImage();
                        img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad;
                        img.UriSource = new Uri(full, UriKind.Absolute); img.EndInit(); img.Freeze();
                        b.Child = new Image
                        {
                            Source = img,
                            Stretch = Stretch.UniformToFill,
                            Margin = new Thickness(2),
                            Clip = new EllipseGeometry(new Rect(0, 0, inner, inner))
                        };
                    }
                    catch { b.Child = LetterInCell(c.ItemName); }
                }
                else b.Child = LetterInCell(c.ItemName);
            }
            else
            {
                b.Child = new TextBlock
                {
                    Text = "+",
                    Foreground = (Brush)FindResource("B.Mute"),
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            b.MouseLeftButtonUp += (_, __) =>
            {
                if (c.ItemId.HasValue) SelectItem(c.ItemId.Value);
                else
                {
                    _pendingCellId = c.Id; _pickedItemId = null;
                    PopulateItemSelect("");
                    overlayItemSelect.Visibility = Visibility.Visible;
                }
            };

            // ПКМ по ячейке = удалить ячейку
            int cellId = c.Id, groupId = c.GroupId;
            b.MouseRightButtonUp += (_, __) => DeleteCellSafe(cellId, groupId);

            return b;
        }

        /// Удаление одной ячейки: спрашиваем, предупреждаем о снятии экипировки,
        /// последнюю ячейку в группе не отдаём.
        private void DeleteCellSafe(int cellId, int groupId)
        {
            var g = _equipment.GetCharacterGroups().FirstOrDefault(x => x.Id == groupId);
            if (g == null) return;
            if (g.Cells.Count <= 1)
            {
                Toast("В группе должна остаться хотя бы одна ячейка", true);
                return;
            }

            var cell = g.Cells.FirstOrDefault(c => c.Id == cellId);
            string itemPart = cell?.ItemId.HasValue == true
                ? $"\nПредмет «{cell.ItemName}» будет снят с экипировки."
                : "";

            _pendingDeleteCellId = cellId;
            _pendingDeleteGroupId = groupId;
            _pendingDeleteIsGroup = false;
            txtDeleteConfirmTitle.Text = "Удалить ячейку";
            txtDeleteConfirmText.Text = $"Удалить ячейку группы «{(string.IsNullOrWhiteSpace(g.GroupName) ? g.SlotName : g.GroupName)}»?{itemPart}";
            btnDeleteConfirm.Content = "Удалить";
            overlayDeleteConfirm.Visibility = Visibility.Visible;
        }

        /// <summary>Удаление группы: подтверждение + предупреждение о снятии экипировки.</summary>
        private void DeleteGroupSafe(int groupId)
        {
            var g = _equipment.GetCharacterGroups().FirstOrDefault(x => x.Id == groupId);
            if (g == null) return;

            int equipped = _equipment.CountEquippedInGroup(groupId);
            string name = string.IsNullOrWhiteSpace(g.GroupName) ? g.SlotName : g.GroupName;

            _pendingDeleteCellId = null;
            _pendingDeleteGroupId = groupId;
            _pendingDeleteIsGroup = true;
            txtDeleteConfirmTitle.Text = "Удалить группу";
            txtDeleteConfirmText.Text = equipped > 0
                ? $"Удалить группу «{name}» вместе с {g.Cells.Count} ячейками?\n{equipped} предмет(а) будут сняты с экипировки."
                : $"Удалить группу «{name}» вместе с {g.Cells.Count} ячейками?";
            btnDeleteConfirm.Content = "Удалить группу";
            overlayDeleteConfirm.Visibility = Visibility.Visible;
        }

        /// <summary>Переименование группы (своё название вместо имени слота).</summary>
        private void RenameGroupSafe(int groupId)
        {
            var g = _equipment.GetCharacterGroups().FirstOrDefault(x => x.Id == groupId);
            if (g == null) return;

            string name = NotePromptWindow.AskText(Window.GetWindow(this), "Название группы",
                "Как назвать группу экипировки?", string.IsNullOrWhiteSpace(g.GroupName) ? g.SlotName : g.GroupName, "Сохранить");
            if (name == null) return;

            _equipment.RenameGroup(groupId, name.Trim());
            RefreshGroups();
            PopulateGroupCells();
            Toast("Группа переименована");
        }

        #endregion

        #region === ТЕГИ В РЕДАКТОРЕ ПРЕДМЕТА ===

        /// <summary>Открывает окно выбора тегов для редактируемого предмета.</summary>
        private void OpenTagsPicker()
        {
            RefreshTagsPicker();
            overlayTagsPicker.Visibility = Visibility.Visible;
        }

        /// <summary>Список всех тегов с галочками; здесь же создаются свои теги.</summary>
        private void RefreshTagsPicker()
        {
            if (_editorTags == null) _editorTags = new List<string>();
            if (spTagsPickerList == null) return;

            spTagsPickerList.Children.Clear();
            foreach (var tag in _inventory.GetAllTagNames())
            {
                var check = new CheckBox
                {
                    Content = tag,
                    Tag = tag,
                    IsChecked = _editorTags.Contains(tag),
                    Foreground = (Brush)FindResource("B.Ink"),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                if (string.Equals(tag, "Сокровище", StringComparison.CurrentCultureIgnoreCase))
                    check.Foreground = (Brush)FindResource("B.Accent");

                check.Checked += (s, e) =>
                {
                    var name = ((CheckBox)s).Tag?.ToString();
                    if (!string.IsNullOrEmpty(name) && !_editorTags.Contains(name)) _editorTags.Add(name);
                    UpdateEditorTagsSummary();
                };
                check.Unchecked += (s, e) =>
                {
                    var name = ((CheckBox)s).Tag?.ToString();
                    if (!string.IsNullOrEmpty(name)) _editorTags.Remove(name);
                    UpdateEditorTagsSummary();
                };
                spTagsPickerList.Children.Add(check);
            }
        }

        /// <summary>Сводка выбранных тегов рядом с кнопкой в редакторе.</summary>
        private void UpdateEditorTagsSummary()
        {
            if (ed_txtTagsSummary == null) return;

            int count = _editorTags?.Count ?? 0;
            ed_txtTagsSummary.Text = count == 0
                ? "Теги не выбраны"
                : string.Join(", ", _editorTags);
            ed_txtTagsSummary.Foreground = (Brush)FindResource(count == 0 ? "B.Mute" : "B.Arcane");
        }

        /// <summary>Создание своего тега прямо из редактора предмета.</summary>
        private void BtnPickerAddTag_Click(object sender, RoutedEventArgs e)
        {
            string name = txtPickerNewTag?.Text?.Trim();
            if (string.IsNullOrEmpty(name)) { Toast("Введите название тега", true); return; }

            _inventory.CreateTag(name);
            if (_editorTags == null) _editorTags = new List<string>();
            if (!_editorTags.Contains(name)) _editorTags.Add(name);

            txtPickerNewTag.Text = "";
            RefreshTagsPicker();
            RefreshTagFilter();
            UpdateEditorTagsSummary();
            Toast($"Тег «{name}» создан");
        }

        private void BtnOpenTagManager_Click(object sender, RoutedEventArgs e)
        {
            RefreshTagManager();
            overlayTagManager.Visibility = Visibility.Visible;
        }

        private void OverlayTagsPicker_Close(object sender, RoutedEventArgs e)
            => overlayTagsPicker.Visibility = Visibility.Collapsed;

        private void OverlayTagsPicker_Background_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) overlayTagsPicker.Visibility = Visibility.Collapsed;
        }

        #endregion

        /// Первая буква предмета (фолбэк, когда нет фото)
        private static TextBlock LetterInCell(string name)
        {
            string s = (name ?? "").Trim();
            string letter = s.Length > 0 ? char.ToUpperInvariant(s[0]).ToString() : "✶";
            return new TextBlock
            {
                Text = letter,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF5)),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        private void OpenGroupCells(int groupId)
        {
            _pendingGroupId = groupId;
            PopulateGroupCells();
            overlayGroupCells.Visibility = Visibility.Visible;
        }


        // === === === === === === === === === === === === === === ===
        // Добавление новой группы экипировки (оверлей выбора слота)
        // === === === === === === === === === === === === === === ===
        private void BtnAddGroup_Click(object sender, RoutedEventArgs e)
        {
            _pendingSlotId = null;
            PopulateSlotSelect();
            overlaySlotSelect.Visibility = Visibility.Visible;
        }

        // === === === === === === === === === === === === === === ===
        // Заполнение списка доступных слотов
        // === === === === === === === === === === === === === === ===
        private void PopulateSlotSelect()
        {
            spSlotSelectList.Children.Clear();
            foreach (var s in _equipment.GetAllSlots())
            {
                var b = new Border
                {
                    Background = (Brush)FindResource("B.Slate"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand,
                    Tag = s.Id
                };
                b.Child = new TextBlock { Text = s.SlotName, Foreground = (Brush)FindResource("B.Ink"), FontSize = 12 };
                b.MouseLeftButtonUp += (_, __) =>
                {
                    _pendingSlotId = s.Id;
                    foreach (var child in spSlotSelectList.Children.OfType<Border>())
                        child.Background = (Brush)FindResource("B.Slate");
                    b.Background = (Brush)FindResource("B.PanelHi");
                };
                spSlotSelectList.Children.Add(b);
            }
        }

        // === === === === === === === === === === === === === === ===
        // Создание нового кастомного слота
        // === === === === === === === === === === === === === === ===
        private void BtnCreateCustomSlot_Click(object sender, RoutedEventArgs e)
        {
            string name = txtNewSlotName.Text.Trim();
            if (string.IsNullOrEmpty(name)) { Toast("Введите название слота", true); return; }
            _pendingSlotId = _equipment.CreateCustomSlot(name);
            txtNewSlotName.Text = "";
            PopulateSlotSelect();
            foreach (var child in spSlotSelectList.Children.OfType<Border>())
                if (Equals(child.Tag, _pendingSlotId)) child.Background = (Brush)FindResource("B.PanelHi");
        }

        // === === === === === === === === === === === === === === ===
        // Подтверждение создания группы на силуэте
        // === === === === === === === === === === === === === === ===
        private void BtnSlotSelectConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingSlotId == null) { Toast("Выберите тип слота", true); return; }
            double x = Math.Max(0, canvasSilhouette.ActualWidth / 2 - 70);
            double y = Math.Max(0, canvasSilhouette.ActualHeight / 2 - 40);
            _equipment.CreateGroup(_pendingSlotId.Value, x, y);
            overlaySlotSelect.Visibility = Visibility.Collapsed;
            RefreshGroups();
            Toast("Группа добавлена");
        }

        // === === === === === === === === === === === === === === ===
        // Закрытие оверлея выбора слота
        // === === === === === === === === === === === === === === ===
        private void OverlaySlotSelect_Close(object sender, RoutedEventArgs e)
            => overlaySlotSelect.Visibility = Visibility.Collapsed;

        private void OverlaySlotSelect_Background_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) overlaySlotSelect.Visibility = Visibility.Collapsed;
        }

        // === === === === === === === === === === === === === === ===
        // Обзор ячеек группы (двойной клик по заголовку)
        // === === === === === === === === === === === === === === ===
        private void PopulateGroupCells()
        {
            var g = _equipment.GetCharacterGroups().FirstOrDefault(x => x.Id == _pendingGroupId);
            if (g == null) return;
            txtGroupCellsTitle.Text = string.IsNullOrWhiteSpace(g.GroupName) ? g.SlotName : g.GroupName;
            spGroupCellsList.Children.Clear();
            int i = 0;
            foreach (var c in g.Cells)
            {
                i++;
                int idx = i;
                var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var b = new Border
                {
                    Background = (Brush)FindResource("B.Slate"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Cursor = Cursors.Hand
                };
                b.Child = new TextBlock
                {
                    Text = $"Ячейка {idx}: {(c.ItemName ?? "пусто")}",
                    Foreground = (Brush)FindResource(c.ItemId.HasValue ? "B.Ink" : "B.Mute"),
                    FontSize = 12
                };
                Grid.SetColumn(b, 0);
                rowGrid.Children.Add(b);

                int cellId = c.Id, groupId = g.Id;
                var btnDelCell = new Button { Width = 26, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Удалить ячейку", VerticalAlignment = VerticalAlignment.Center };
                DmUiKit.ApplyIcon(btnDelCell, "close");
                btnDelCell.Click += (_, __) =>
                {
                    DeleteCellSafe(cellId, groupId);
                    PopulateGroupCells();   // обновить список
                };
                Grid.SetColumn(btnDelCell, 1);
                rowGrid.Children.Add(btnDelCell);

                b.MouseLeftButtonUp += (_, __) =>
                {
                    overlayGroupCells.Visibility = Visibility.Collapsed;
                    _pendingCellId = cellId; _pickedItemId = null;
                    PopulateItemSelect("");
                    overlayItemSelect.Visibility = Visibility.Visible;
                };
                spGroupCellsList.Children.Add(rowGrid);
            }
        }

        // === === === === === === === === === === === === === === ===
        // Добавление ячейки в группу из оверлея
        // === === === === === === === === === === === === === === ===
        private void BtnAddCellFromOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingGroupId == null) return;
            var g = _equipment.GetCharacterGroups().FirstOrDefault(x => x.Id == _pendingGroupId);
            _equipment.CreateCell(_pendingGroupId.Value, g?.Cells.Count ?? 0);
            PopulateGroupCells();
            RefreshGroups();
        }

        // === === === === === === === === === === === === === === ===
        // Закрытие оверлея ячеек группы
        // === === === === === === === === === === === === === === ===
        private void OverlayGroupCells_Close(object sender, RoutedEventArgs e)
            => overlayGroupCells.Visibility = Visibility.Collapsed;

        private void OverlayGroupCells_Background_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) overlayGroupCells.Visibility = Visibility.Collapsed;
        }

        #region === СИЛУЭТ ===

        // === === === === === === === === === === === === === === ===
        // Применение фонового изображения силуэта
        // === === === === === === === === === === === === === === ===
        private void ApplySilhouette(string fullPath)
        {
            // Если свой фон не выбран или файл пропал — показываем встроенный силуэт CursedBro
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                try
                {
                    canvasSilhouette.Background = new ImageBrush(
                        new BitmapImage(new Uri("pack://application:,,,/Resources/CursedBro.png", UriKind.Absolute)))
                    {
                        Stretch = Stretch.Uniform,
                        Opacity = 0.22
                    };
                }
                catch { }

                btnChangeSilhouette.ToolTip = "Сейчас показан встроенный силуэт. Нажмите, чтобы выбрать свой фон";
                return;
            }

            try
            {
                // Читаем файл в память (PhotoHelper.LoadUnlocked), а не через UriSource.
                //
                // Из-за UriSource здесь было два дефекта сразу. Во-первых, WPF кэширует
                // картинки ПО АДРЕСУ, а имя файла фона не меняется (Silhouettes\char_<id>.<ext>):
                // при второй смене возвращался прежний кадр, и казалось, что фон «не меняется».
                // Во-вторых, картинка держала файл занятым, поэтому следующая замена
                // упиралась в «файл используется другим процессом».
                // У чтения из потока адреса нет — значит, нет ни кэша, ни блокировки.
                var img = PhotoHelper.LoadUnlocked(fullPath);
                if (img == null)
                {
                    ApplySilhouette(null);
                    return;
                }

                canvasSilhouette.Background = new ImageBrush(img) { Stretch = Stretch.Uniform, Opacity = 0.22 };
                btnChangeSilhouette.ToolTip = "Сменить фон панели экипировки";
            }
            catch
            {
                // Битый файл — не оставляем пустоту, показываем встроенный силуэт
                ApplySilhouette(null);
            }
        }

        // === === === === === === === === === === === === === === ===
        // Смена фона силуэта (с сохранением в AppData)
        // === === === === === === === === === === === === === === ===
        private void BtnChangeSilhouette_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Изображения (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp|Все файлы (*.*)|*.*",
                Title = "Выберите фон панели экипировки"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Silhouettes");
                Directory.CreateDirectory(dir);

                string extension = Path.GetExtension(dialog.FileName);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
                extension = extension.ToLowerInvariant();

                string dest = Path.Combine(dir, $"char_{_characterId}{extension}");

                // Сначала копируем в стороне и только потом трогаем рабочий файл.
                // Так смена фона не зависит от того, свободен ли прежний файл:
                // раньше он удалялся до копирования, и если удаление/перезапись
                // не проходили, фон молча оставался прежним.
                string temp = dest + ".new";
                File.Copy(dialog.FileName, temp, true);

                // Убираем прежние фоны персонажа, чтобы файлы не копились
                foreach (var old in Directory.GetFiles(dir, $"char_{_characterId}.*"))
                {
                    if (string.Equals(old, temp, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(old); } catch { }
                }

                File.Copy(temp, dest, true);
                try { File.Delete(temp); } catch { }

                _inventory.SaveSilhouettePath(_characterId, dest);
                ApplySilhouette(dest);
                Toast("Фон обновлён");
            }
            catch (Exception ex) { Toast($"Ошибка фона: {ex.Message}", true); }
        }

        #endregion

        #region === ТЕГИ ===

        // === === === === === === === === === === === === === === ===
        // Заполнение фильтра тегов (ComboBox)
        // === === === === === === === === === === === === === === ===
        private void RefreshTagFilter()
        {
            string current = cbFilterTag.SelectedIndex > 0
                ? (cbFilterTag.SelectedItem as ComboBoxItem)?.Content?.ToString() : null;
            cbFilterTag.Items.Clear();
            cbFilterTag.Items.Add(new ComboBoxItem { Content = "Все теги", IsSelected = true });
            foreach (var t in _inventory.GetAllTagNames())
                cbFilterTag.Items.Add(new ComboBoxItem { Content = t });
            if (current != null)
                foreach (ComboBoxItem ci in cbFilterTag.Items)
                    if (ci.Content?.ToString() == current) { ci.IsSelected = true; break; }
        }

        // === === === === === === === === === === === === === === ===
        // Заполнение списка тегов в менеджере
        // === === === === === === === === === === === === === === ===
        private void RefreshTagManager()
        {
            _selectedTagName = null;
            spTagList.Children.Clear();
            foreach (var t in _inventory.GetAllTagNames())
            {
                var b = new Border
                {
                    Background = (Brush)FindResource("B.Slate"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand
                };
                b.Child = new TextBlock { Text = t, Foreground = (Brush)FindResource("B.Ink"), FontSize = 12 };
                b.MouseLeftButtonUp += (_, __) =>
                {
                    _selectedTagName = t;
                    foreach (var child in spTagList.Children.OfType<Border>())
                        child.Background = (Brush)FindResource("B.Slate");
                    b.Background = (Brush)FindResource("B.PanelHi");
                };
                spTagList.Children.Add(b);
            }
        }

        // === === === === === === === === === === === === === === ===
        // Добавление нового тега через менеджер
        // === === === === === === === === === === === === === === ===
        private void BtnAddOverlayTag_Click(object sender, RoutedEventArgs e)
        {
            string name = txtNewOverlayTag.Text.Trim();
            if (string.IsNullOrEmpty(name)) { Toast("Введите название тега", true); return; }
            _inventory.CreateTag(name);
            txtNewOverlayTag.Text = "";
            RefreshTagManager(); RefreshTagFilter();
            Toast("Тег добавлен");
        }

        // === === === === === === === === === === === === === === ===
        // Удаление тега через менеджер
        // === === === === === === === === === === === === === === ===
        private void BtnDeleteOverlayTag_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTagName == null) { Toast("Выберите тег из списка", true); return; }
            _inventory.DeleteTag(_selectedTagName);
            RefreshTagManager(); RefreshTagFilter(); RefreshList();
            Toast("Тег удалён");
        }

        // === === === === === === === === === === === === === === ===
        // Закрытие менеджера тегов
        // === === === === === === === === === === === === === === ===
        private void OverlayTagManager_Close(object sender, RoutedEventArgs e)
            => overlayTagManager.Visibility = Visibility.Collapsed;

        private void OverlayTagManager_Background_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) overlayTagManager.Visibility = Visibility.Collapsed;
        }

        #endregion
    }
}