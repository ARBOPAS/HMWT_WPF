using HMWT_MUI.Controls;
using HMWT_MUI.Models;
using HMWT_MUI.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IODir = System.IO.Directory;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;
using HMWT_MUI.Windows;

using HMWT_MUI.Helpers;

namespace HMWT_MUI.Views
{
    public partial class SpellsView : UserControl
    {
        #region === ПОЛЯ И СОСТОЯНИЕ ===
        private readonly int _characterId;
        private int _currentLevel = -1;
        private string _searchText = "";
        private readonly DispatcherTimer _searchDebounce;
        private string _sourceFilter = "";
        private List<SpellData> _prepared = new List<SpellData>();
        private List<SpellData> _known = new List<SpellData>();
        private List<SpellData> _fullList = new List<SpellData>();
        private int _fullIndex = -1;
        private bool _creatingNew;
        private bool _prepareMode;
        private List<SpellRefRow> _allRefRows = new List<SpellRefRow>();
        private string _settingsContext = "sheet";
        private readonly HashSet<int> _activeTagFilters = new HashSet<int>();
        private Dictionary<int, HashSet<int>> _spellTags = new Dictionary<int, HashSet<int>>();
        private List<SpellTag> _allTags = new List<SpellTag>();
        private DateTime _lastCardClick = DateTime.MinValue;
        private SpellData _lastCardData;
        private Window _hostWindow;
        private DispatcherTimer _toastHideTimer;
        #endregion

        #region === ПАЛИТРЫ ШКОЛ ===
        private static readonly Dictionary<string, Color> SchoolColors =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            {"Abjuration", Color.FromRgb(0x4F,0x7B,0xC0)}, {"Защита", Color.FromRgb(0x4F,0x7B,0xC0)},
            {"Conjuration", Color.FromRgb(0xC0,0x8A,0x3E)}, {"Призыв", Color.FromRgb(0xC0,0x8A,0x3E)},
            {"Divination", Color.FromRgb(0x4F,0xB6,0xAC)}, {"Прорицание", Color.FromRgb(0x4F,0xB6,0xAC)},
            {"Enchantment", Color.FromRgb(0xC0,0x56,0x8F)}, {"Очарование", Color.FromRgb(0xC0,0x56,0x8F)},
            {"Evocation", Color.FromRgb(0xC0,0x56,0x4F)}, {"Эвокация", Color.FromRgb(0xC0,0x56,0x4F)},
            {"Illusion", Color.FromRgb(0x9A,0x7B,0xD6)}, {"Иллюзия", Color.FromRgb(0x9A,0x7B,0xD6)},
            {"Necromancy", Color.FromRgb(0x7A,0x3B,0x3B)}, {"Некромантия", Color.FromRgb(0x7A,0x3B,0x3B)},
            {"Transmutation", Color.FromRgb(0x8A,0x9A,0x3E)}, {"Трансмутация", Color.FromRgb(0x8A,0x9A,0x3E)},
        };
        private static readonly Color SchoolFallback = Color.FromRgb(0x55, 0x55, 0x66);
        private static readonly Color[] PlaceholderPalette = new[]
        {
            Color.FromRgb(0x4F,0x7B,0xC0), Color.FromRgb(0x9A,0x7B,0xD6), Color.FromRgb(0x4F,0xB6,0xAC),
            Color.FromRgb(0xC0,0x8A,0x3E), Color.FromRgb(0xC0,0x56,0x8F), Color.FromRgb(0x7A,0x3B,0x3B),
            Color.FromRgb(0x8A,0x9A,0x3E), Color.FromRgb(0x5F,0xD4,0xDD), Color.FromRgb(0x5B,0x7B,0xB0),
        };
        private static readonly string[] DefaultClasses = { "Варвар", "Бард", "Жрец", "Друид", "Воин", "Монах", "Паладин", "Следопыт", "Плут", "Чародей", "Колдун", "Волшебник", "Изобретатель" };
        #endregion

        #region === КОНСТРУКТОР ===
        public SpellsView(int characterId)
        {
            InitializeComponent();
            _characterId = characterId;
            this.Unloaded += (s, e) => { StopAllGifs(); UnwireWindowKey(); };
            this.Loaded += (s, e) => WireWindowKey();
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); ApplyView(); };

            if (KnownSearchBox != null)
                KnownSearchBox.TextChanged += (s, e) => ApplyView();
            if (KnownSortCombo != null)
                KnownSortCombo.SelectionChanged += (s, e) => ApplyView();

            WireEvents();

            try
            {
                SpellService.EnsureTables();

                LoadHealSelfSetting();

                // Панель классов нужно собрать: раньше BuildClassesPanel не вызывался,
                // и раздел «Классы» в редакторе был пустым — «текбокс не кликабелен»
                // означало «в нём нечего нажимать».
                BuildClassesPanel();

                // Автоматический импорт справочника убран по требованию пользователя:
                // при открытии вкладки ничего не подгружается. Данные игрок
                // добавляет сам — своими заклинаниями или импортом своего файла.

                LoadSpellSlotsForLevel(_currentLevel);
                LoadSpellsFromDb();
                _spellTags = SpellService.LoadSpellTagMap();
                _allTags = SpellService.LoadAllTags();
                ApplyView();
                LoadSettingsIntoPanel();
                PrewarmArtCache();
            }
            catch (Exception ex)
            {
                Log("init", ex);
                ShowFatalEmptyState(ex.Message);
            }
            Debug.WriteLine($"[SpellsView] открыта для персонажа {_characterId}");
        }
        #endregion

        #region === ESC = ЗАКРЫТЬ ОВЕРЛЕЙ ===
        private void WireWindowKey()
        {
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null) _hostWindow.PreviewKeyDown += OnHostKeyDown;
        }
        private void UnwireWindowKey()
        {
            if (_hostWindow != null) _hostWindow.PreviewKeyDown -= OnHostKeyDown;
        }
        private void OnHostKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (SettingsOverlay != null && ToggleSettings != null && ToggleSettings.IsChecked == true) { ToggleSettings.IsChecked = false; e.Handled = true; return; }
            if (TagFilterOverlay != null && TagFilterOverlay.Visibility == Visibility.Visible) { TagFilterOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (SpellbookOverlay != null && SpellbookOverlay.Visibility == Visibility.Visible) { SpellbookOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (TagsOverlay != null && TagsOverlay.Visibility == Visibility.Visible) { TagsOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (FullEditorOverlay != null && FullEditorOverlay.Visibility == Visibility.Visible) { CloseFullEditor(); e.Handled = true; return; }
            if (PrepareOverlay != null && PrepareOverlay.Visibility == Visibility.Visible) { ClosePrepareOverlay(); e.Handled = true; return; }
            if (SlotsOverviewOverlay != null && SlotsOverviewOverlay.Visibility == Visibility.Visible) { SlotsOverviewOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        }
        #endregion

        #region === ПОДПИСКА НА СОБЫТИЯ ===
        private void WireEvents()
        {
            if (SearchBox != null) SearchBox.TextChanged += SearchBox_TextChanged;
            if (BtnExpandFull != null) BtnExpandFull.Click += (s, e) => OpenFullEditor();
            if (BtnCollapseFull != null) BtnCollapseFull.Click += (s, e) => CloseFullEditor();
            if (BtnSlotMinus != null) BtnSlotMinus.Click += (s, e) => ChangeUsed(-1);
            if (BtnSlotPlus != null) BtnSlotPlus.Click += (s, e) => ChangeUsed(+1);
            if (BtnRestSlots != null) BtnRestSlots.Click += BtnRestSlots_Click;
            if (BtnFullPrev != null) BtnFullPrev.Click += (s, e) => StepFullEditor(-1);
            if (BtnFullNext != null) BtnFullNext.Click += (s, e) => StepFullEditor(+1);
            if (BtnFullSave != null) BtnFullSave.Click += BtnFullSave_Click;
            if (BtnCancelCreate != null) BtnCancelCreate.Click += (s, e) => CloseFullEditor();
            if (BtnSlotsOverview != null) BtnSlotsOverview.Click += (s, e) => OpenSlotsOverview();
            if (SlotsOverviewClose != null) SlotsOverviewClose.Click += (s, e) => { if (SlotsOverviewOverlay != null) SlotsOverviewOverlay.Visibility = Visibility.Collapsed; };
            if (BtnTagFilter != null) BtnTagFilter.Click += (s, e) => OpenTagFilter();
            if (TagFilterClose != null) TagFilterClose.Click += (s, e) => { if (TagFilterOverlay != null) TagFilterOverlay.Visibility = Visibility.Collapsed; };
            if (TagFilterClear != null) TagFilterClear.Click += (s, e) => { _activeTagFilters.Clear(); RenderTagFilterList(); RenderActiveFilterChips(); ApplyView(); };
            if (BtnAddFromRef != null) BtnAddFromRef.Click += (s, e) => OpenSpellbook();
            if (SbClose != null) SbClose.Click += (s, e) => { if (SpellbookOverlay != null) SpellbookOverlay.Visibility = Visibility.Collapsed; };
            if (SbSearch != null) SbSearch.TextChanged += SbSearch_TextChanged;
            if (SbAddSelected != null) SbAddSelected.Click += SbAddSelected_Click;
            if (SbList != null) SbList.MouseDoubleClick += (s, e) => SbAddSelected_Click(s, e);
            if (BtnCreateNew != null) BtnCreateNew.Click += (s, e) => OpenCreateNew();
            if (BtnOpenTagsRail != null) BtnOpenTagsRail.Click += (s, e) => OpenTags();
            if (BtnOpenTagEditor != null) BtnOpenTagEditor.Click += (s, e) => OpenTags();
            if (TagsClose != null) TagsClose.Click += (s, e) => { if (TagsOverlay != null) TagsOverlay.Visibility = Visibility.Collapsed; };
            if (BtnTagAdd != null) BtnTagAdd.Click += BtnTagAdd_Click;
            if (BtnTagDelete != null) BtnTagDelete.Click += BtnTagDelete_Click;
            if (BtnSaveSettings != null) BtnSaveSettings.Click += BtnSaveSettings_Click;
            if (BtnCancelSettings != null) BtnCancelSettings.Click += (s, e) => { if (ToggleSettings != null) ToggleSettings.IsChecked = false; };
            if (ToggleSettings != null) ToggleSettings.Checked += (s, e) => LoadSettingsIntoPanel();
            if (CmbSpellStat != null) CmbSpellStat.SelectionChanged += (s, e) => RefreshSpellFormulas();
            if (CmbMaxLevel != null) CmbMaxLevel.SelectionChanged += (s, e) => ApplyMaxLevelTabs();
            if (SetCtxSheet != null) SetCtxSheet.Checked += (s, e) => { _settingsContext = "sheet"; };
            if (SetCtxGlobal != null) SetCtxGlobal.Checked += (s, e) => { _settingsContext = "global"; };
        }
        #endregion

        #region === СЛОТЫ ===
        private void LoadSpellSlotsForLevel(int level)
        {
            _currentLevel = level;
            if (level < 0) { LoadAllSlotsSummary(); return; }
            var (total, used) = SpellService.LoadSlots(_characterId, level);
            SetSlotsHeader(total - used, total, level == 0 ? "заговоры · ячейки не требуются" : $"ячейки {ToRoman(level)} уровня");
        }
        private void LoadAllSlotsSummary()
        {
            var (total, used) = SpellService.LoadSlotsSummary(_characterId);
            SetSlotsHeader(total - used, total, "все ячейки (суммарно)");
        }
        private void SetSlotsHeader(int available, int total, string label)
        {
            if (TxtSlotsUsed != null) TxtSlotsUsed.Text = Math.Max(0, available).ToString();
            if (TxtSlotsTotal != null) TxtSlotsTotal.Text = total.ToString();
            if (TxtSlotsLabel != null) TxtSlotsLabel.Text = label;
        }
        private void ChangeUsed(int deltaAvailable)
        {
            // В режиме «ВСЕ» уровень не выбран. Раньше здесь выскакивало окно с
            // подсказкой, а в нём — пустая красная кнопка (передавался пустой
            // текст). Теперь переходим на первый уровень, у которого есть ячейки,
            // и ОБЯЗАТЕЛЬНО переключаем на него фильтр: без этого в списке
            // оставались заклинания всех уровней, а счётчик показывал чужой
            // уровень — выглядело как ошибка.
            if (_currentLevel < 0)
            {
                int level = FirstLevelWithSlots(deltaAvailable > 0);
                if (level <= 0)
                {
                    ShowToast("Ячейки не настроены — задайте их в настройках заклинаний", "!", B("B.Danger"));
                    return;
                }

                if (!SelectLevelTab(level))
                {
                    // Вкладку не нашли (уровень скрыт фильтром) — работаем напрямую
                    LoadSpellSlotsForLevel(level);
                    ApplyView();
                }
            }

            if (_currentLevel < 0) return;   // ячейки заговоров не тратятся
            SpellService.ChangeUsedSlots(_characterId, _currentLevel, deltaAvailable);
            LoadSpellSlotsForLevel(_currentLevel);
        }

        /// <summary>
        /// Делает активной вкладку уровня. Нужна, чтобы переход из режима «ВСЕ»
        /// менял и список заклинаний, и счётчик ячеек согласованно.
        /// </summary>
        private bool SelectLevelTab(int level)
        {
            try
            {
                if (TabAll?.Parent is not Panel tabs) return false;

                foreach (var child in tabs.Children)
                {
                    if (child is not RadioButton rb) continue;
                    if (rb.Tag == null || !int.TryParse(rb.Tag.ToString(), out int tag)) continue;
                    if (tag != level || rb.Visibility != Visibility.Visible) continue;

                    rb.IsChecked = true;   // сам вызовет LevelTab_Checked
                    return true;
                }
            }
            catch (Exception ex) { Log("SelectLevelTab", ex); }
            return false;
        }

        /// <summary>
        /// Первый уровень, у которого есть ячейки: для «+» — где есть свободные,
        /// для «−» — где есть занятые.
        /// </summary>
        private int FirstLevelWithSlots(bool needFree)
        {
            try
            {
                foreach (var row in SpellService.LoadSlotsDetails(_characterId))
                {
                    if (row.Level <= 0) continue;
                    int free = row.Total - row.Used;
                    if (needFree ? free > 0 : row.Used > 0) return row.Level;
                    if (row.Total > 0) return row.Level;
                }
            }
            catch (Exception ex) { Log("FirstLevelWithSlots", ex); }
            return 0;
        }
        private void BtnRestSlots_Click(object sender, RoutedEventArgs e)
        {
            // На режиме «ВСЕ» восполняем всё, на конкретном уровне — только его
            // ячейки: некоторые предметы и умения восстанавливают не все сразу
            if (_currentLevel < 0)
            {
                SpellService.RestSlots(_characterId);
                ShowToast("Все ячейки восполнены", "⟲", B("B.Ok"));
            }
            else
            {
                SpellService.RestSlotsForLevel(_characterId, _currentLevel);
                ShowToast(_currentLevel == 0
                    ? "Заговоры не тратят ячейки"
                    : $"Ячейки уровня {_currentLevel} восполнены", "⟲", B("B.Ok"));
            }

            LoadSpellSlotsForLevel(_currentLevel);
        }
        #endregion

        #region === ОБЗОР ЯЧЕЕК ===
        private void OpenSlotsOverview()
        {
            try
            {
                if (SlotsOverviewList == null) return;
                SlotsOverviewList.Children.Clear();
                var rows = SpellService.LoadSlotsDetails(_characterId);
                if (rows.Count == 0)
                {
                    SlotsOverviewList.Children.Add(new TextBlock { Text = "ячейки не настроены", Foreground = B("B.Mute"), FontSize = 11, FontStyle = FontStyles.Italic });
                }
                else
                {
                    foreach (var row in rows)
                    {
                        var line = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var lbl = new TextBlock
                        {
                            Text = row.Level == 0 ? "Заговоры" : $"{ToRoman(row.Level)} уровень",
                            FontFamily = (FontFamily)FindResource("F.Display"),
                            FontSize = 13,
                            Foreground = B("B.Ink"),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(lbl, 0); line.Children.Add(lbl);
                        TextBlock val;
                        if (row.Level == 0)
                        {
                            val = new TextBlock { Text = "∞", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = B("B.Arcane"), VerticalAlignment = VerticalAlignment.Center };
                        }
                        else
                        {
                            int a = Math.Max(0, row.Total - row.Used);
                            Brush col = row.Total <= 0 ? B("B.Mute") : (a == 0 ? B("B.Danger") : (a >= row.Total ? B("B.Frost") : B("B.Accent")));
                            val = new TextBlock { Text = $"{a} / {row.Total}", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = col, VerticalAlignment = VerticalAlignment.Center };
                        }
                        Grid.SetColumn(val, 1); line.Children.Add(val);
                        SlotsOverviewList.Children.Add(line);
                    }
                }
                if (SlotsOverviewOverlay != null) SlotsOverviewOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Log("OpenSlotsOverview", ex); }
        }
        private void SlotsOverviewBg_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender && SlotsOverviewOverlay != null)
                SlotsOverviewOverlay.Visibility = Visibility.Collapsed;
        }
        #endregion

        #region === ЗАГРУЗКА ЗАКЛИНАНИЙ ===
        private void LoadSpellsFromDb()
        {
            var (prep, known) = SpellService.LoadSpells(_characterId);
            foreach (var d in prep) FinalizeCardVisuals(d);
            foreach (var d in known) FinalizeCardVisuals(d);
            _prepared = prep;
            _known = known;
        }
        private void FinalizeCardVisuals(SpellData d)
        {
            d.SchoolBrush = new SolidColorBrush(SchoolColors.TryGetValue(d.School ?? "", out var c) ? c : SchoolFallback);
            d.SchoolInitial = string.IsNullOrWhiteSpace(d.School) ? "?" : d.School.Trim().Substring(0, 1).ToUpperInvariant();
            d.CastingShort = ShortCasting(d.CastingTime);
            d.ConcentrationVisibility = d.Concentration ? Visibility.Visible : Visibility.Collapsed;
            d.PreparedVisibility = d.IsPrepared ? Visibility.Visible : Visibility.Collapsed;
            d.ModifiedVisibility = d.IsModified ? Visibility.Visible : Visibility.Collapsed;
            var (c1, c2) = PlaceholderColors(d.Name);
            d.PlaceholderBrush = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
            d.PlaceholderLetter = FirstLatinLetter(d.Name);
            d.PlaceholderRune = SchoolRune(d.School);
        }
        private static string ShortCasting(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.ToLowerInvariant();
            if (s.Contains("bonus")) return "бонусное действие";
            if (s.Contains("reaction")) return "реакция";
            if (s.Contains("action")) return "1 действие";
            return raw.ToLowerInvariant();
        }
        #endregion

        #region === ИЗОБРАЖЕНИЯ И GIF ===

        /// <summary>
        /// Показывает арт заклинания.
        ///
        /// Для гифок используется HoverGif: он держит в памяти только первый кадр,
        /// а анимирует лишь при наведении — иначе полный набор кадров (десятки
        /// мегабайт на файл) оставался бы в памяти для каждой плитки.
        /// </summary>
        private void ApplyImage(Image img, Controls.HoverGif hover, SpellData d,
            Border placeholder, TextBlock letter, TextBlock rune)
        {
            if (img == null && hover == null) return;

            if (hover != null) hover.ClearImage();

            string path = d?.ImagePath ?? "";
            // Арт ищем там, где приложение реально хранит картинки (AppData), иначе — рядом с exe
            string full = ResolveExistingPath(path);
            bool hasArt = full != null;

            if (!hasArt)
            {
                if (placeholder != null)
                {
                    ApplyPlaceholder(placeholder, letter, rune, d);
                    placeholder.Visibility = Visibility.Visible;
                }
                return;
            }

            if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;

            // Гифка: HoverGif сам покажет первый кадр и включит анимацию при наведении
            if (hover != null)
            {
                hover.SetImage(full);
                return;
            }

            if (path.ToLowerInvariant().EndsWith(".gif"))
            {
                var first = GifFrameCache.GetFirstFrame(full);
                if (first != null)
                {
                    img.Source = first;
                    return;
                }
            }

            if (d != null && d.CachedStatic != null)
            {
                img.Source = d.CachedStatic;
            }
            else
            {
                var bmp = LoadImageSafe(path);
                if (d != null) d.CachedStatic = bmp;
                img.Source = bmp;
            }
        }
        private void ApplyPlaceholder(Border placeholder, TextBlock letter, TextBlock rune, SpellData d)
        {
            if (placeholder == null) return;
            if (d != null && d.PlaceholderBrush != null)
            {
                placeholder.Background = d.PlaceholderBrush;
                if (letter != null) letter.Text = d.PlaceholderLetter ?? "✶";
                if (rune != null) rune.Text = d.PlaceholderRune ?? "✶";
            }
            else
            {
                StylePlaceholder(placeholder, letter, rune, d?.Name ?? "", d?.School ?? "");
            }
        }
        private void PrewarmArtCache()
        {
            var all = new List<SpellData>(_prepared);
            all.AddRange(_known);
            Task.Run(() =>
            {
                foreach (var d in all)
                {
                    try
                    {
                        string full = ResolvePath(d.ImagePath);
                        if (string.IsNullOrWhiteSpace(d.ImagePath) || full == null || !IOFile.Exists(full)) continue;
                        if (d.ImagePath.ToLowerInvariant().EndsWith(".gif"))
                        {
                            // Готовим заранее только первый кадр. Полный набор кадров
                            // разбирать заранее нельзя: это и было причиной гигабайтов памяти.
                            GifFrameCache.GetFirstFrame(full);
                        }
                        else
                        {
                            if (d.CachedStatic == null) d.CachedStatic = LoadImageSafe(d.ImagePath);
                        }
                    }
                    catch (Exception ex) { Log("prewarm", ex); }
                }
                Debug.WriteLine($"[SpellsView] кеш арта прогрет: {all.Count} карточек");
            });
        }
        private void StopAllGifs()
        {
        }
        #endregion

        #region === ПЛЕЙСХОЛДЕРЫ ===
        private void StylePlaceholder(Border placeholder, TextBlock letter, TextBlock rune, string name, string school)
        {
            if (placeholder == null) return;
            var (c1, c2) = PlaceholderColors(name);
            placeholder.Background = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
            if (letter != null) letter.Text = FirstLatinLetter(name);
            if (rune != null) rune.Text = SchoolRune(school);
        }
        private static (Color, Color) PlaceholderColors(string name)
        {
            int h = (name ?? "").ToLowerInvariant().GetHashCode();
            int i = Math.Abs(h) % PlaceholderPalette.Length;
            int j = (i + 1 + (Math.Abs(h >> 3) % (PlaceholderPalette.Length - 1))) % PlaceholderPalette.Length;
            return (Dim(PlaceholderPalette[i]), Dim(PlaceholderPalette[j]));
        }
        private static Color Dim(Color c) => Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));
        private static string FirstLatinLetter(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "✶";
            char ch = name.TrimStart()[0];
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) return char.ToUpperInvariant(ch).ToString();
            if (TranslitFirst.TryGetValue(ch, out string lat)) return lat;
            return "✶";
        }
        private static readonly Dictionary<char, string> TranslitFirst = new Dictionary<char, string>
        {
            {'А',"A"},{'Б',"B"},{'В',"V"},{'Г',"G"},{'Д',"D"},{'Е',"E"},{'Ё',"E"},{'Ж',"Z"},{'З',"Z"},
            {'И',"I"},{'Й',"I"},{'К',"K"},{'Л',"L"},{'М',"M"},{'Н',"N"},{'О',"O"},{'П',"P"},{'Р',"R"},
            {'С',"S"},{'Т',"T"},{'У',"U"},{'Ф',"F"},{'Х',"H"},{'Ц',"C"},{'Ч',"C"},{'Ш',"S"},{'Щ',"S"},
            {'Ъ',""},{'Ы',"Y"},{'Ь',""},{'Э',"E"},{'Ю',"U"},{'Я',"Y"}
        };
        private static string SchoolRune(string school)
        {
            if (string.IsNullOrWhiteSpace(school)) return "✶";
            string s = school.Trim().ToLowerInvariant();
            if (s.Contains("evoc") || s.Contains("эвок")) return "bolt";
            if (s.Contains("necr") || s.Contains("некр")) return "warning";
            if (s.Contains("illus") || s.Contains("иллю")) return "eye";
            if (s.Contains("abjur") || s.Contains("защит")) return "shield";
            if (s.Contains("conj") || s.Contains("приз")) return "wand";
            if (s.Contains("divin") || s.Contains("прориц")) return "star";
            if (s.Contains("enchant") || s.Contains("очар")) return "sparkle";
            if (s.Contains("trans") || s.Contains("транс")) return "";
            return "✶";
        }
        #endregion

        #region === РЕНДЕР СПИСКОВ ===
        private void ComputeVisible(out List<SpellData> prep, out List<SpellData> known)
        {
            bool searching = !string.IsNullOrWhiteSpace(_searchText);
            string q = searching ? _searchText.Trim() : null;
            var prepSrc = _currentLevel < 0 ? _prepared : FilterByLevel(_prepared, _currentLevel);
            var knownSrc = _currentLevel < 0 ? _known : FilterByLevel(_known, _currentLevel);
            prep = q == null ? prepSrc : Match(prepSrc, q);
            known = q == null ? knownSrc : Match(knownSrc, q);
            if (_activeTagFilters.Count > 0)
            {
                prep = FilterByTags(prep);
                known = FilterByTags(known);
            }
            known = FilterKnown(known);
        }

        /// <summary>Поиск и сортировка только по блоку известных заклинаний.</summary>
        private List<SpellData> FilterKnown(List<SpellData> src)
        {
            var list = new List<SpellData>(src);
            string q = (KnownSearchBox?.Text ?? "").Trim();
            if (q.Length > 0)
                list = list.Where(d => (d.Name ?? "").IndexOf(q,
                    StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();

            bool descending = KnownSortCombo?.SelectedIndex == 1;
            list.Sort((a, b) => descending
                ? string.Compare(b.Name, a.Name, StringComparison.CurrentCultureIgnoreCase)
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }
        private List<SpellData> FilterByTags(List<SpellData> src)
        {
            var res = new List<SpellData>();
            foreach (var d in src)
            {
                if (!_spellTags.TryGetValue(d.IdSpell, out var set)) continue;
                bool ok = true;
                foreach (var t in _activeTagFilters) if (!set.Contains(t)) { ok = false; break; }
                if (ok) res.Add(d);
            }
            return res;
        }
        private void ApplyView()
        {
            try
            {
                ComputeVisible(out var prep, out var known);
                RenderSection(PreparedCardsStack, prep, true,
                    !string.IsNullOrWhiteSpace(_searchText) || _activeTagFilters.Count > 0 ? "Ничего не найдено" : "Нет подготовленных заклинаний");
                RenderSection(KnownCardsStack, known, false,
                    !string.IsNullOrWhiteSpace(_searchText) || _activeTagFilters.Count > 0 ? "" : "Нет известных заклинаний");
                if (TxtPreparedCount != null)
                {
                    int limit = GetIntSetting(SKey("PreparedLimit"), 0);
                    TxtPreparedCount.Text = limit > 0 ? $"подготовлено {_prepared.Count} / {limit}" : $"подготовлено {_prepared.Count}";
                }
                RenderActiveFilterChips();
            }
            catch (Exception ex) { Log("ApplyView", ex); }
        }
        private static List<SpellData> FilterByLevel(List<SpellData> src, int lvl)
        {
            var res = new List<SpellData>();
            foreach (var d in src) if (d.Level == lvl) res.Add(d);
            return res;
        }
        private static List<SpellData> Match(List<SpellData> src, string q)
        {
            string n = q.ToLowerInvariant();
            var res = new List<SpellData>();
            foreach (var d in src)
                if ((d.Name ?? "").ToLowerInvariant().Contains(n) ||
                    (d.School ?? "").ToLowerInvariant().Contains(n) ||
                    (d.Description ?? "").ToLowerInvariant().Contains(n))
                    res.Add(d);
            return res;
        }
        private void RenderSection(WrapPanel panel, List<SpellData> items, bool prepared, string emptyText)
        {
            if (panel == null) return;
            panel.Children.Clear();
            if (items == null || items.Count == 0)
            {
                if (!string.IsNullOrEmpty(emptyText)) panel.Children.Add(BuildEmptyState(emptyText));
                panel.Children.Add(BuildPlusTile(prepared));
                return;
            }
            foreach (var d in items) panel.Children.Add(BuildSpellCard(d));
            panel.Children.Add(BuildPlusTile(prepared));
        }
        private Border BuildSpellCard(SpellData d)
        {
            var card = new Border();
            card.SetResourceReference(StyleProperty, "FilledCardHover");
            card.DataContext = d;
            card.MouseLeftButtonUp += OnCardClick;
            if (d.IsPrepared) card.BorderBrush = B("B.Accent");
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var imgBox = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x38)) };
            var imgGrid = new Grid();
            // HoverGif: статичный первый кадр, анимация только при наведении.
              var img = new Controls.HoverGif { Stretch = Stretch.Uniform };
            var ph = new Border { Visibility = Visibility.Collapsed };
            var phGrid = new Grid();
            var rune = new TextBlock { Text = "✶", FontFamily = (FontFamily)FindResource("F.Display"), FontSize = 44, Foreground = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var letter = new TextBlock { Text = "?", FontFamily = (FontFamily)FindResource("F.Display"), FontSize = 52, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            phGrid.Children.Add(rune); phGrid.Children.Add(letter);
            ph.Child = phGrid;
            imgGrid.Children.Add(img); imgGrid.Children.Add(ph);
            imgBox.Child = imgGrid;
            ApplyImage(null, img, d, ph, letter, rune);
            Grid.SetRow(imgBox, 0); grid.Children.Add(imgBox);
            var topRow = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5, 5, 5, 0) };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var leftBadges = new StackPanel { Orientation = Orientation.Horizontal };
            leftBadges.Children.Add(MakeBadge(d.SchoolInitial, d.SchoolBrush, string.IsNullOrWhiteSpace(d.School) ? "Школа не указана" : d.School));
            if (d.ConcentrationVisibility == Visibility.Visible)
                leftBadges.Children.Add(MakeBadge("wand", B("B.Frost"), "Концентрация", darkGlyph: true));
            if (d.PreparedVisibility == Visibility.Visible)
                leftBadges.Children.Add(MakeBadge("check", B("B.Accent"), "Подготовлено", darkGlyph: true));
            if (d.ModifiedVisibility == Visibility.Visible)
                leftBadges.Children.Add(MakeBadge("edit", B("B.Arcane"), "Изменено относительно справочника"));
            Grid.SetColumn(leftBadges, 0); topRow.Children.Add(leftBadges);
            var del = new Button
            {
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Убрать заклинание",
                DataContext = d
            };

            // Свой шаблон кнопки.
            //
            // Раньше у кнопки было только Background=Transparent и BorderThickness=0,
            // но СИСТЕМНЫЙ шаблон всё равно рисовал подложку при наведении —
            // за крестиком проступал квадрат. Свой шаблон убирает это: виден
            // только круг, который чуть светлеет при наведении.
            var delTemplate = new ControlTemplate(typeof(Button));
            var delFactory = new FrameworkElementFactory(typeof(Border), "delCircle");
            delFactory.SetValue(Border.WidthProperty, 18.0);
            delFactory.SetValue(Border.HeightProperty, 18.0);
            delFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            delFactory.SetValue(Border.BackgroundProperty, B("B.Danger"));

            var delGlyph = new FrameworkElementFactory(typeof(Helpers.IconView));
            delGlyph.SetValue(Helpers.IconView.KindProperty, "close");
            delGlyph.SetValue(Helpers.IconView.IconSizeProperty, 11.0);
            delGlyph.SetValue(Helpers.IconView.IconBrushProperty, Brushes.White);
            delGlyph.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            delGlyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            delFactory.AppendChild(delGlyph);

            delTemplate.VisualTree = delFactory;
            delTemplate.Triggers.Add(new Trigger
            {
                Property = IsMouseOverProperty,
                Value = true,
                Setters = { new Setter(Border.OpacityProperty, 0.75, "delCircle") }
            });
            del.Template = delTemplate;

            del.Click += OnCardDeleteClick;
            Grid.SetColumn(del, 1); topRow.Children.Add(del);
            Grid.SetRow(topRow, 0); grid.Children.Add(topRow);
            var nameBox = new Border { Background = B("B.PanelHi"), Padding = new Thickness(7, 5, 7, 5) };
            Grid.SetRow(nameBox, 1);
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(d.Name) ? "(без названия)" : d.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = B("B.Ink"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(d.CastingShort))
                nameStack.Children.Add(new TextBlock { Text = d.CastingShort, FontSize = 10, Foreground = B("B.Mute"), Margin = new Thickness(0, 1, 0, 0) });
            nameBox.Child = nameStack;
            grid.Children.Add(nameBox);

            // Содержимое обрезаем по скруглённой рамке карточки.
            //
            // Без этого картинка и подписи заходили на углы и вылезали за
            // золотую рамку: Border со скруглёнными углами содержимое не обрезает.
            card.ClipToBounds = true;
            card.Child = grid;
            card.SizeChanged += (s, e) => ApplyRoundedClip(card, grid);
            ApplyRoundedClip(card, grid);

            return card;
        }

        /// <summary>
        /// Обрезает содержимое карточки по её скруглённым углам.
        ///
        /// У Border со скруглением углов содержимое НЕ обрезается — картинка
        /// и подписи заходили на углы и вылезали за рамку. Скругление берём
        /// из самой карточки, чтобы не подбирать число вручную.
        /// </summary>
        private static void ApplyRoundedClip(Border card, Grid content)
        {
            if (card == null || content == null) return;

            double radius = card.CornerRadius.TopLeft;
            double w = card.ActualWidth;
            double h = card.ActualHeight;

            // Пока размеры не посчитаны, обрезать нечего: придёт SizeChanged
            if (w <= 0 || h <= 0) return;

            content.Clip = new RectangleGeometry(
                new Rect(0, 0, w, h), radius, radius);
        }
        private static Border MakeBadge(string glyph, Brush bg, string tip, bool darkGlyph = false)
        {
            var b = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = bg,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = tip
            };
            // Значок рисуем иконкой набора: «wand», «check», «edit»
            b.Child = new Helpers.IconView
            {
                Kind = glyph,
                IconSize = 12,
                IconBrush = darkGlyph ? new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x18)) : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return b;
        }
        private Border BuildPlusTile(bool prepared)
        {
            var plus = new Border
            {
                DataContext = prepared,
                ToolTip = prepared ? "Добавить из известных" : "Добавить из справочника",
                Cursor = Cursors.Hand
            };
            plus.SetResourceReference(StyleProperty, "PlusTile");
            plus.MouseLeftButtonUp += (s, e) =>
            {
                if (prepared == true)
                    OpenPrepareFromKnown();
                else
                    OpenSpellbook();
            };
            plus.Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M9,0 V18 M0,9 H18"),
                Width = 18,
                Height = 18,
                Stretch = Stretch.None,
                Stroke = B("B.Mute"),
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return plus;
        }
        private Border BuildEmptyState(string text)
        {
            var b = new Border { Width = 316, Height = 120, Margin = new Thickness(8), CornerRadius = new CornerRadius(8), BorderBrush = B("B.Line"), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = "✶", FontSize = 22, Foreground = B("B.Arcane"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
            sp.Children.Add(new TextBlock { Text = text, Foreground = B("B.Mute"), FontSize = 12, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = "нажмите + чтобы добавить из справочника", Foreground = B("B.Mute"), FontSize = 11, FontStyle = FontStyles.Italic, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            b.Child = sp;
            return b;
        }
        private void ShowFatalEmptyState(string msg)
        {
            if (PreparedCardsStack != null) { PreparedCardsStack.Children.Clear(); PreparedCardsStack.Children.Add(BuildEmptyState("Не удалось загрузить заклинания")); }
            if (KnownCardsStack != null) { KnownCardsStack.Children.Clear(); KnownCardsStack.Children.Add(BuildEmptyState("")); }
            Debug.WriteLine($"[SpellsView] fatal empty: {msg}");
        }
        #endregion

        #region === ПОДГОТОВКА ИЗ ИЗВЕСТНЫХ ===
        private List<SpellData> _prepareKnown = new List<SpellData>();
        private bool _prepareDragging;
        private Point _prepareDragStart;
        private double _prepareStartX, _prepareStartY;

        private void OpenPrepareFromKnown()
        {
            _prepareKnown = _known
                .Where(s => !s.IsPrepared)
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (_prepareKnown.Count == 0)
            {
                MessageDialog.Info(this,
                    "У персонажа пока нет известных заклинаний.\nСначала добавь их из справочника (кнопка «+» в разделе «Известные»).",
                    "Нет доступных заклинаний");
                return;
            }

            PrepareSearchBox.Text = "";
            PrepareSortCombo.SelectedIndex = 0;
            PrepareDrag.X = 0;
            PrepareDrag.Y = 0;
            FillPrepareList();
            PrepareOverlay.Visibility = Visibility.Visible;
            PrepareSearchBox.Focus();
        }

        private void FillPrepareList()
        {
            if (PrepareList == null) return;
            PrepareList.Items.Clear();

            string query = (PrepareSearchBox?.Text ?? "").Trim();
            bool descending = PrepareSortCombo?.SelectedIndex == 1;

            var items = _prepareKnown
                .Where(s => query.Length == 0 ||
                            (s.Name ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (descending) items.Reverse();

            foreach (var spell in items)
            {
                PrepareList.Items.Add(new ListBoxItem
                {
                    Content = spell.Name ?? "(без названия)",
                    Tag = spell,
                    Padding = new Thickness(12, 10, 12, 10),
                    Cursor = Cursors.Hand,
                    FontSize = 13
                });
            }
        }

        private void PrepareSearch_TextChanged(object sender, TextChangedEventArgs e) => FillPrepareList();
        private void PrepareSort_SelectionChanged(object sender, SelectionChangedEventArgs e) => FillPrepareList();

        private void ClosePrepareOverlay()
        {
            _prepareDragging = false;
            PrepareOverlay.Visibility = Visibility.Collapsed;
            PrepareDrag.X = 0;
            PrepareDrag.Y = 0;
        }

        private void PrepareCancel_Click(object sender, RoutedEventArgs e) => ClosePrepareOverlay();

        private void PrepareOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == PrepareOverlay) ClosePrepareOverlay();
        }

        private void PrepareConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (PrepareList.SelectedItem is ListBoxItem item && item.Tag is SpellData spell)
            {
                SpellService.TogglePrepared(_characterId, spell.IdCharSpell, true);
                LoadSpellsFromDb();
                ApplyView();
                ClosePrepareOverlay();
            }
            else
            {
                MessageDialog.Info(this, "Выберите заклинание в списке.", "Подготовка");
            }
        }

        private void PrepareHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            _prepareDragging = true;
            _prepareDragStart = e.GetPosition(PrepareOverlay);
            _prepareStartX = PrepareDrag.X;
            _prepareStartY = PrepareDrag.Y;
            PrepareDragArea.CaptureMouse();
            e.Handled = true;
        }

        private void PrepareHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_prepareDragging || e.LeftButton != MouseButtonState.Pressed) return;
            Point p = e.GetPosition(PrepareOverlay);
            PrepareDrag.X = _prepareStartX + (p.X - _prepareDragStart.X);
            PrepareDrag.Y = _prepareStartY + (p.Y - _prepareDragStart.Y);
        }

        private void PrepareHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _prepareDragging = false;
            PrepareDragArea.ReleaseMouseCapture();
        }
        #endregion

        #region === ЧИПЫ ТЕГОВ ===
        private void RenderActiveFilterChips()
        {
            if (ActiveFiltersPanel == null || ActiveFiltersBar == null) return;
            ActiveFiltersPanel.Children.Clear();
            if (_activeTagFilters.Count == 0) { ActiveFiltersBar.Visibility = Visibility.Collapsed; return; }
            ActiveFiltersBar.Visibility = Visibility.Visible;
            foreach (var id in _activeTagFilters)
            {
                var tag = _allTags.Find(t => t.Id == id);
                string name = tag?.Name ?? "?";
                var chip = new Border();
                chip.SetResourceReference(StyleProperty, "Chip");
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = " " + name, Foreground = B("B.Arcane"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var x = new Helpers.IconView { Kind = "close", IconSize = 11, IconBrush = B("B.Mute"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Tag = id };
                x.MouseLeftButtonUp += (s, e) =>
                {
                    if (s is TextBlock tb && tb.Tag is int tid)
                    {
                        _activeTagFilters.Remove(tid);
                        RenderTagFilterList();
                        RenderActiveFilterChips();
                        ApplyView();
                    }
                };
                sp.Children.Add(x);
                chip.Child = sp;
                ActiveFiltersPanel.Children.Add(chip);
            }
            var addChip = new Border { Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderBrush = B("B.Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6) };
            addChip.Child = DmUiKit.IconText("plus", "тег", 12, B("B.Mute"), 5);
            addChip.MouseLeftButtonUp += (s, e) => OpenTagFilter();
            ActiveFiltersPanel.Children.Add(addChip);
        }
        #endregion

        #region === ТАБЫ И ПОИСК ===
        private void LevelTab_Checked(object sender, RoutedEventArgs e)
        {
            if (PreparedCardsStack == null) return;
            if (sender is RadioButton rb && rb.Tag != null && int.TryParse(rb.Tag.ToString(), out int lvl))
            {
                LoadSpellSlotsForLevel(lvl);
                ApplyView();
            }
        }
        private void ApplyMaxLevelTabs()
        {
            try
            {
                // 0-9 = показывать все уровни; иначе — конкретный уровень.
                int max = 0;
                if (CmbMaxLevel?.SelectedItem is ComboBoxItem item)
                    int.TryParse((item.Content ?? "").ToString(), out max);
                if (max <= 0) max = 9;
                if (TabAll?.Parent is Panel tabs)
                {
                    foreach (var child in tabs.Children)
                    {
                        if (child is RadioButton rb && rb != TabAll && rb.Tag != null && int.TryParse(rb.Tag.ToString(), out int t))
                            rb.Visibility = t <= max ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                if (_currentLevel > max && TabAll != null) TabAll.IsChecked = true;
            }
            catch (Exception ex) { Log("ApplyMaxLevelTabs", ex); }
        }
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = SearchBox?.Text ?? "";
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
        #endregion

        #region === ФИЛЬТР ПО ТЕГАМ ===
        private void OpenTagFilter()
        {
            try
            {
                _allTags = SpellService.LoadAllTags();
                RenderTagFilterList();
                if (TagFilterOverlay != null) TagFilterOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Log("OpenTagFilter", ex); }
        }
        private void RenderTagFilterList()
        {
            if (TagFilterList == null) return;
            TagFilterList.Children.Clear();
            if (_allTags.Count == 0)
            {
                TagFilterList.Children.Add(new TextBlock { Text = "тегов пока нет — создай в «Редакторе тегов»", Foreground = B("B.Mute"), FontSize = 11, FontStyle = FontStyles.Italic });
            }
            else
            {
                foreach (var t in _allTags)
                {
                    bool on = _activeTagFilters.Contains(t.Id);
                    var chip = new Border
                    {
                        Cursor = Cursors.Hand,
                        CornerRadius = new CornerRadius(13),
                        Padding = new Thickness(11, 5, 11, 5),
                        Margin = new Thickness(0, 0, 7, 7),
                        BorderThickness = new Thickness(1),
                        Background = on ? new SolidColorBrush(Color.FromArgb(0x33, 0xA9, 0x8B, 0xEC)) : B("B.PanelHi"),
                        BorderBrush = on ? B("B.Arcane") : B("B.Line"),
                        Tag = t.Id
                    };
                    chip.Child = new TextBlock { Text = t.Name, Foreground = on ? B("B.Ink") : B("B.Mute"), FontSize = 12 };
                    chip.MouseLeftButtonUp += (s, e) =>
                    {
                        if (s is Border b && b.Tag is int id)
                        {
                            if (!_activeTagFilters.Remove(id)) _activeTagFilters.Add(id);
                            RenderTagFilterList();
                            RenderActiveFilterChips();
                            ApplyView();
                        }
                    };
                    TagFilterList.Children.Add(chip);
                }
            }
            if (TagFilterStatus != null)
                TagFilterStatus.Text = _activeTagFilters.Count == 0 ? "фильтр не активен" : $"активно тегов: {_activeTagFilters.Count}";
        }
        #endregion

        #region === КЛИК ПО КАРТОЧКЕ ===
        private void OnCardClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if ((sender as FrameworkElement)?.DataContext is not SpellData d) return;
                var now = DateTime.Now;
                bool doubleClick = _lastCardData == d && (now - _lastCardClick).TotalMilliseconds < 320;
                _lastCardClick = now;
                _lastCardData = d;
                FillDescription(d);
                BuildFullList(d);
                if (doubleClick) OpenFullEditor();
            }
            catch (Exception ex) { Log("OnCardClick", ex); }
        }
        private void TogglePrepared(SpellData d)
        {
            try
            {
                bool makePrepared = !d.IsPrepared;
                if (makePrepared)
                {
                    int limit = GetIntSetting(SKey("PreparedLimit"), 0);
                    if (limit > 0 && _prepared.Count >= limit)
                    {
                        MessageDialog.Info(this, $"Достигнут лимит подготовки ({limit}). Сними подготовку с другого заклинания или увеличь лимит в настройках.",
                            "Лимит подготовки");
                        return;
                    }
                }
                SpellService.TogglePrepared(_characterId, d.IdCharSpell, makePrepared);
                LoadSpellsFromDb();
                ApplyView();
            }
            catch (Exception ex) { Log("TogglePrepared", ex); }
        }
        private void FillDescription(SpellData d)
        {
            _selectedSpell = d;
            _castLevel = Math.Max(0, d?.Level ?? 0);
            RefreshCastIsland();

            ApplyImage(DescImage, null, d, DescArtPlaceholder, DescArtLetter, DescArtRune);
            if (DescName != null) DescName.Text = string.IsNullOrWhiteSpace(d.Name) ? "(без названия)" : d.Name;
            if (DescLevelSchool != null)
            {
                string lvlTxt = d.Level == 0 ? "заговор" : $"{d.Level} уровень";
                DescLevelSchool.Text = string.IsNullOrWhiteSpace(d.School) ? lvlTxt : $"{lvlTxt}, {d.School.ToLowerInvariant()}";
            }
            if (DescCasting != null) DescCasting.Text = Blank(d.CastingTime);
            if (DescRange != null) DescRange.Text = Blank(d.Range);
            if (DescComponents != null) DescComponents.Text = Blank(d.Components);
            if (DescDuration != null) DescDuration.Text = Blank(d.Duration);
            if (DescClasses != null) DescClasses.Text = Blank(d.Classes);

            // Описание рисуется разбором формул: «2к4» в тексте становится
            // ссылкой, по нажатию бросаются кубики. Так «Исцеляющее слово»
            // лечит, а не «просто сотворяется», и это работает у любого
            // заклинания из справочника — настраивать ничего не нужно.
            FillRollText(DescText, string.IsNullOrWhiteSpace(d.Description) ? "Описание отсутствует." : d.Description,
                "Описание", d);
            FillRollText(DescHigher, d.AtHigherLevels, "На больших уровнях", d);
            if (DescHigher != null)
                DescHigher.Visibility = string.IsNullOrWhiteSpace(d.AtHigherLevels) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Рисует текст с подсветкой формул броска. Обычный текст остаётся
        /// текстом, каждая формула становится кликабельной.
        /// </summary>
        private void FillRollText(TextBlock target, string text, string scope, SpellData spell)
        {
            if (target == null) return;

            string spellName = spell?.Name ?? "";
            Helpers.RollTextRenderer.Render(target, text, part =>
            {
                var window = Window.GetWindow(this) as CharacterInfoWindow;
                window?.RollExternal(part.Normalized, scope + ": " + spellName, "", part.Describe());
            }, new Helpers.RollTextStyle
            {
                Text = (Brush)FindResource("B.Ink"),
                Dice = (Brush)FindResource("B.Accent"),
                DiceBack = (Brush)FindResource("B.PanelHi"),
                Hint = (Brush)FindResource("B.Mute"),
                Font = target.FontFamily,
                FontSize = target.FontSize,
                Wrap = true
            });
        }

        #region === ОСТРОВОК КАСТОВАНИЯ ===

        /// <summary>Заклинание, показанное в карточке.</summary>
        private SpellData _selectedSpell;

        /// <summary>Уровень ячейки, которым собираемся сотворить.</summary>
        private int _castLevel;

        /// <summary>
        /// Обновляет островок кастования: видимость, уровень, остаток ячеек,
        /// формулу урона и доступность кнопки.
        /// </summary>
        private void RefreshCastIsland()
        {
            if (CastIsland == null) return;

            var spell = _selectedSpell;
            if (spell == null)
            {
                CastIsland.Visibility = Visibility.Collapsed;
                return;
            }

            CastIsland.Visibility = Visibility.Visible;

            int baseLevel = Math.Max(0, spell.Level);
            if (_castLevel < baseLevel) _castLevel = baseLevel;

            if (CastLevelText != null) CastLevelText.Text = _castLevel == 0 ? "—" : _castLevel.ToString();

            // «−» только когда уровень поднят: ниже базового уйти нельзя
            if (BtnCastLevelMinus != null)
                // Кнопка «меньше» видна всегда: раньше она пряталась на базовом
                // уровне, и было непонятно, что уровень можно понизить
                BtnCastLevelMinus.Visibility = Visibility.Visible;
                BtnCastLevelMinus.IsEnabled = _castLevel > baseLevel;
                BtnCastLevelPlus.IsEnabled = _castLevel < 9;

            string formula = SpellService.BuildCastFormula(spell, _castLevel);
            string healFormula = SpellService.BuildCastHealFormula(spell, _castLevel);
            int steps = Math.Max(0, _castLevel - Math.Max(0, spell.Level));

            if (CastFormulaText != null)
            {
                var blocks = new List<string>();

                if (!string.IsNullOrWhiteSpace(formula))
                {
                    // Показываем каждую часть урона со своим типом: у заклинания
                    // их бывает несколько, и все они бросаются за одно сотворение
                    var castParts = SpellService.BuildCastParts(spell, _castLevel);

                    string text;
                    if (castParts.Count > 1)
                    {
                        var lines = new List<string>();
                        foreach (var part in castParts)
                        {
                            string type = part.TypeLabel;
                            lines.Add(string.IsNullOrWhiteSpace(type)
                                ? part.Formula
                                : $"{part.Formula} · {type}");
                        }
                        text = "Урон за каст: " + string.Join("  +  ", lines);
                    }
                    else
                    {
                        string type = castParts.Count == 1 ? castParts[0].TypeLabel : "";
                        text = (string.IsNullOrWhiteSpace(type)
                            ? $"Урон: {formula}"
                            : $"Урон: {formula} · {type}");
                    }

                    // Из чего сложилась формула: в панели было «1d4+1d6+1d6»
                    // и не понимал, что это базовый урон плюс добавка за уровень
                    if (steps > 0 && spell.CanUpcast)
                        text += $" (добавка за {steps} ур. выше)";

                    blocks.Add(text);
                }

                if (!string.IsNullOrWhiteSpace(healFormula))
                {
                    string text = $"Лечение: {healFormula}";
                    if (steps > 0 && spell.CanUpcastHealing)
                        text += $" (добавка за {steps} ур. выше)";
                    blocks.Add(text);
                }

                if (!string.IsNullOrWhiteSpace(spell.SaveNote))
                    blocks.Add($"Спасбросок: {spell.SaveNote}");

                if (blocks.Count == 0)
                {
                    CastFormulaText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CastFormulaText.Text = string.Join("\n", blocks);
                    CastFormulaText.Visibility = Visibility.Visible;
                }
            }

            // Галочка нужна только тем заклинаниям, что лечат
            if (ChkHealSelf != null)
            {
                ChkHealSelf.Visibility = string.IsNullOrWhiteSpace(healFormula)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            bool cantrip = baseLevel == 0;
            int available = 0;
            if (!cantrip)
            {
                var (total, used) = SpellService.LoadSlots(_characterId, _castLevel);
                available = Math.Max(0, total - used);
                if (CastSlotText != null)
                    CastSlotText.Text = total <= 0
                        ? $"ячеек {_castLevel}-го уровня нет"
                        : $"ячейка {_castLevel}-го уровня · осталось {available} из {total}";
            }
            else if (CastSlotText != null)
            {
                CastSlotText.Text = "заговор · ячейка не тратится";
            }

            if (BtnCastSpell != null) BtnCastSpell.IsEnabled = cantrip || available > 0;

            if (CastWarnText != null)
            {
                bool noSlots = !cantrip && available <= 0;
                CastWarnText.Text = noSlots
                    ? "Ячеек этого уровня нет — сотворить нельзя."
                    : "";
                CastWarnText.Visibility = noSlots ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void BtnCastLevelPlus_Click(object sender, RoutedEventArgs e)
        {
            var spell = _selectedSpell;
            if (spell == null) return;
            if (_castLevel >= 9) return;
            _castLevel++;
            RefreshCastIsland();
        }

        private void BtnCastLevelMinus_Click(object sender, RoutedEventArgs e)
        {
            var spell = _selectedSpell;
            if (spell == null) return;
            int baseLevel = Math.Max(0, spell.Level);
            if (_castLevel <= baseLevel) return;
            _castLevel--;
            RefreshCastIsland();
        }

        /// <summary>
        /// Сотворение: тратит ячейку и отправляет бросок урона в общее окно
        /// результатов (то же, куда попадают броски кубиков и урона оружием).
        /// </summary>
        /// <summary>
        /// Отправляет заклинание на вкладку «Атаки».
        ///
        /// Сотворение с ячейкой и спасброском остаётся здесь, а бросок попадания
        /// и урона переезжает в одно место — чтобы не было трёх разных кнопок урона.
        /// </summary>
        private void BtnSpellToAttacks_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var spell = _selectedSpell;
                if (spell == null)
                {
                    ShowToast("Сначала выберите заклинание", "!", B("B.Danger"));
                    return;
                }

                if (!spell.HasDamage)
                {
                    ShowToast("У заклинания не указан урон", "!", B("B.Danger"));
                    return;
                }

                int added = Services.AttackService.CollectFromSpells(_characterId);
                int removed = Services.AttackService.LastRemovedSpells;
                string removedPart = removed > 0 ? $" Убрано неподготовленных: {removed}." : "";

                // Сюда попадают только подготовленные заклинания (и заговоры):
                // известные, но не подготовленные, в атаки не идут
                ShowToast(added > 0
                    ? $"Атаки по подготовленным заклинаниям добавлены: {added}.{removedPart}"
                    : $"Атаки по подготовленным заклинаниям уже собраны.{removedPart}",
                    added > 0 ? "⚔" : "·", B(added > 0 ? "B.Ok" : "B.Mute"));

                if (Window.GetWindow(this) is CharacterInfoWindow window) window.OpenAttacksTab();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Заклинания] в атаки: {ex.Message}");
                ShowToast("Не удалось перенести заклинание в атаки", "!", B("B.Danger"));
            }
        }

        private void BtnCastSpell_Click(object sender, RoutedEventArgs e)
        {
            var spell = _selectedSpell;
            if (spell == null) return;

            var cast = SpellService.TryCast(_characterId, spell, _castLevel);
            if (!cast.Success)
            {
                // Никаких предложений «сотворить выше»: нет ячейки — каст запрещён
                ShowToast(cast.Message, "!", B("B.Danger"));
                RefreshCastIsland();
                LoadSpellSlotsForLevel(_currentLevel);
                return;
            }

            ShowToast(cast.Message, "check", B("B.Ok"));
            RefreshCastIsland();
            LoadSpellSlotsForLevel(_currentLevel);

            // Лечение бросается и у заклинания без урона: «Исцеляющее слово»
            // ничего не наносит, и раньше каст на этом и заканчивался — игроку
            // оставалось только самому вписывать себе хиты
            RollCastHealing(cast);

            if (cast.Parts == null || cast.Parts.Count == 0)
            {
                // Заклинание без урона: ячейку потратили, бросать урон нечего
                if (!string.IsNullOrWhiteSpace(cast.SaveNote))
                    ShowToast(cast.SaveNote, "⚖", B("B.Frost"));
                return;
            }

            var window = Window.GetWindow(this) as CharacterInfoWindow;
            if (window == null) return;

            // Каждая часть урона бросается отдельно: у неё свой тип, и складывать
            // разные типы в одну цифру нельзя. Итог показываем отдельной подписью.
            string rollType = cast.SlotLevel > 0
                ? $"Урон: {spell.Name} ({cast.SlotLevel} ур.)"
                : $"Урон: {spell.Name}";

            int total = 0;
            int rolled = 0;

            foreach (var part in cast.Parts)
            {
                string typeLabel = part.TypeLabel;
                string what = string.IsNullOrWhiteSpace(typeLabel)
                    ? part.Formula
                    : $"{part.Formula} {typeLabel}";

                var roll = window.RollExternal(part.Formula, rollType, "", what);
                if (roll == null) continue;

                total += roll.Total;
                rolled++;
            }

            if (rolled > 1)
                ShowToast($"Всего урона за каст: {total}", "plus", B("B.Ok"));

            RollCastHealing(cast);

            if (!string.IsNullOrWhiteSpace(cast.SaveNote))
                ShowToast(cast.SaveNote, "⚖", B("B.Frost"));
        }

        /// <summary>
        /// Бросает лечение заклинания и, если игрок не снял галочку, прибавляет
        /// его персонажу.
        ///
        /// Отдельным броском от урона: у заклинания бывает и то, и другое, и
        /// складывать лечение с уроном в одно число нельзя.
        /// </summary>
        private void RollCastHealing(SpellService.CastResult cast)
        {
            if (cast == null || string.IsNullOrWhiteSpace(cast.HealFormula)) return;

            var window = Window.GetWindow(this) as CharacterInfoWindow;
            if (window == null) return;

            string rollType = cast.SlotLevel > 0
                ? $"Лечение: {cast.SpellName} ({cast.SlotLevel} ур.)"
                : $"Лечение: {cast.SpellName}";

            var roll = window.RollExternal(cast.HealFormula, rollType, "", cast.HealFormula);
            if (roll == null) return;

            if (ChkHealSelf?.IsChecked != true) return;

            window.HealCharacter(roll.Total);
            ShowToast($"Лечение: +{roll.Total} хитов", "plus", B("B.Ok"));
        }

        /// <summary>
        /// Галочка «прибавлять лечение персонажу» запоминается между заходами:
        /// целитель либо лечит себя, либо нет — это не разовое решение.
        /// </summary>
        private void ChkHealSelf_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingHealSelf) return;

            bool value = ChkHealSelf?.IsChecked == true;
            try { DatabaseHelper.SaveSetting(HealSelfKey, value ? "1" : "0"); }
            catch (Exception ex) { Log("HealSelf", ex); }
        }

        /// <summary>Ключ настройки «прибавлять лечение персонажу».</summary>
        private string HealSelfKey => $"Character_{_characterId}_HealSelf";

        /// <summary>
        /// Восстанавливает галочку. По умолчанию она стоит: чаще лечат себя,
        /// а для лечения союзника её снимают.
        /// </summary>
        private void LoadHealSelfSetting()
        {
            if (ChkHealSelf == null) return;

            _loadingHealSelf = true;
            try
            {
                ChkHealSelf.IsChecked = GetStringSetting(HealSelfKey, "1") != "0";
            }
            finally
            {
                _loadingHealSelf = false;
            }
        }

        /// <summary>Галочка сейчас грузится — в этот момент её не сохраняем.</summary>
        private bool _loadingHealSelf;

        #endregion

        #endregion

        #region === ПОЛНЫЙ РЕДАКТОР ===
        /// <summary>Открывает готовую карточку заклинания по имени.
        /// Вызывается из бестиария, когда мастер щёлкает название заклинания.</summary>
        public void OpenSpellByName(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                if (_prepared.Count == 0 && _known.Count == 0)
                    LoadSpellsFromDb();

                var spell = _prepared.Concat(_known)
                    .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

                if (spell == null)
                {
                    spell = SpellService.LoadSpellByName(name);
                    if (spell == null)
                    {
                        ShowToast("Заклинание не найдено в справочнике", "?", B("B.Mute"));
                        return;
                    }

                    _fullList = new List<SpellData>(_prepared);
                    _fullList.AddRange(_known);
                    if (!_fullList.Any(s => s.IdSpell == spell.IdSpell))
                        _fullList.Insert(0, spell);
                    _fullIndex = _fullList.FindIndex(s => s.IdSpell == spell.IdSpell);
                    _creatingNew = false;
                }
                else
                {
                    BuildFullList(spell);
                    _creatingNew = false;
                }

                _selectedSpell = spell;
                FillFullEditor(spell);
                if (FullEditorOverlay != null) FullEditorOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Log("OpenSpellByName", ex); }
        }

        private void BuildFullList(SpellData current)
        {
            ComputeVisible(out var prep, out var known);
            _fullList = new List<SpellData>(prep);
            _fullList.AddRange(known);
            _fullIndex = _fullList.IndexOf(current);
            if (_fullIndex < 0 && _fullList.Count > 0) _fullIndex = 0;
        }
        private void OpenFullEditor()
        {
            try
            {
                if (_fullList.Count == 0 || _fullIndex < 0)
                {
                    ComputeVisible(out var p, out var k);
                    _fullList = new List<SpellData>(p); _fullList.AddRange(k);
                    _fullIndex = 0;
                }
                if (_fullList.Count == 0 && !_creatingNew) { MessageDialog.Info(this, "Нет заклинаний для просмотра.", "Пусто"); return; }
                if (!_creatingNew && _fullIndex >= 0) FillFullEditor(_fullList[_fullIndex]);
                if (FullEditorOverlay != null) FullEditorOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Log("OpenFullEditor", ex); }
        }
        private void CloseFullEditor()
        {
            _creatingNew = false;
            if (FullEditorOverlay != null) FullEditorOverlay.Visibility = Visibility.Collapsed;
        }
        private void StepFullEditor(int dir)
        {
            try
            {
                if (_creatingNew || _fullList.Count == 0) return;
                _fullIndex = (_fullIndex + dir + _fullList.Count) % _fullList.Count;
                FillFullEditor(_fullList[_fullIndex]);
            }
            catch (Exception ex) { Log("StepFullEditor", ex); }
        }
        #region === ЭФФЕКТ ЗАКЛИНАНИЯ: ПОЛЯ РЕДАКТОРА ===

        /// <summary>Заполняет список типов урона (один раз на вид).</summary>
        /// <summary>
        /// Показывает, какая формула получится при касте на разных уровнях.
        ///
        /// В панели было «1d4+1d6+1d6» без пояснений: это базовый урон плюс
        /// добавка за поднятый уровень. Здесь это видно сразу, ещё при заполнении.
        /// </summary>
        private void Ed_DamageField_TextChanged(object sender, TextChangedEventArgs e)
            => UpdateDamagePreview();

        private void UpdateDamagePreview()
        {
            if (EdDamagePreview == null) return;

            string baseFormula = (EdDamageFormula?.Text ?? "").Trim();
            string perLevel = (EdPerLevelDamage?.Text ?? "").Trim();

            // Составной урон: показываем все части так, как они уйдут в бросок
            var lines = new List<string>();
            if (baseFormula.Length > 0)
                lines.Add(DescribePart(baseFormula, perLevel, ReadEditorDamageType()));

            foreach (var part in _extraParts)
            {
                if (part == null || string.IsNullOrWhiteSpace(part.Formula)) continue;
                lines.Add(DescribePart(part.Formula.Trim(), (part.PerLevelDamage ?? "").Trim(), part.DamageType));
            }

            var preview = new List<string>();

            if (lines.Count == 0)
                preview.Add("Урон на базовом уровне: — (заклинание не наносит урон)");
            else if (lines.Count == 1)
                preview.Add("Урон: " + lines[0]);
            else
                preview.Add($"Урон из {lines.Count} частей — за одно сотворение: " + string.Join("  ·  ", lines));

            // Лечение показываем рядом с уроном: у заклинания бывает и то, и другое
            string heal = (EdHealFormula?.Text ?? "").Trim();
            string healPerLevel = (EdHealPerLevel?.Text ?? "").Trim();
            if (heal.Length > 0)
                preview.Add("Лечение: " + DescribePart(
                    heal, healPerLevel, "").Replace(" · выше: ", " · выше лечит: "));

            string save = (EdSaveNote?.Text ?? "").Trim();
            if (save.Length > 0) preview.Add(save);

            EdDamagePreview.Text = string.Join("\n", preview);
        }

        /// <summary>Одна часть урона словами: «1к10 (психический) · выше: 1к10+1к6».</summary>
        private static string DescribePart(string formula, string perLevel, string damageType)
        {
            string label = formula;
            string type = SpellService.DamageTypeLabel(damageType);
            if (!string.IsNullOrWhiteSpace(type)) label += $" ({type})";

            if (!string.IsNullOrWhiteSpace(perLevel))
            {
                string add = perLevel.StartsWith("+") ? perLevel : "+" + perLevel;
                label += $" · выше: {formula}{add}";
            }
            return label;
        }

        private void EnsureDamageTypeItems()
        {
            if (EdDamageType == null || EdDamageType.Items.Count > 0) return;
            foreach (var (en, ru) in SpellService.DamageTypes)
                EdDamageType.Items.Add($"{en} ({ru})");
        }

        #region === СОСТАВНОЙ УРОН: ДОБАВЛЕНИЕ ТИПОВ ===

        /// <summary>
        /// Строки дополнительного урона. Каждая пишет прямо в свой объект,
        /// поэтому сбор значения при сохранении — просто список этих объектов.
        /// </summary>
        private readonly List<DamagePart> _extraParts = new List<DamagePart>();

        private void BtnAddDamagePart_Click(object sender, RoutedEventArgs e)
        {
            _extraParts.Add(new DamagePart());
            RebuildExtraParts();
            UpdateDamagePreview();
        }

        /// <summary>Показывает в редакторе список дополнительных частей урона.</summary>
        private void SetExtraParts(List<DamagePart> parts)
        {
            _extraParts.Clear();
            foreach (var part in parts ?? new List<DamagePart>())
                if (part != null && !part.IsEmpty) _extraParts.Add(part.Clone());

            RebuildExtraParts();
        }

        /// <summary>Собирает части урона для сохранения (пустые строки выбрасываются).</summary>
        private List<DamagePart> ReadExtraParts()
        {
            var result = new List<DamagePart>();
            foreach (var part in _extraParts)
            {
                if (part == null || part.IsEmpty) continue;
                result.Add(part.Clone());
            }
            return result;
        }

        private void RebuildExtraParts()
        {
            if (spExtraDamage == null) return;
            spExtraDamage.Children.Clear();

            foreach (var part in _extraParts)
                spExtraDamage.Children.Add(BuildExtraPartRow(part));
        }

        /// <summary>Одна строка дополнительного урона: формула, тип, добавка и удаление.</summary>
        private UIElement BuildExtraPartRow(DamagePart part)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var formula = new TextBox
            {
                Style = (Style)FindResource("TextField"),
                Text = part.Formula ?? "",
                ToolTip = "Формула этой части урона: например 3d10"
            };
            formula.TextChanged += (s, e) => { part.Formula = formula.Text; UpdateDamagePreview(); };
            Grid.SetColumn(formula, 0);
            grid.Children.Add(formula);

            var typePanel = new StackPanel();
            var typeCombo = new ComboBox
            {
                Style = (Style)FindResource("EditorComboBox"),
                IsEditable = true,
                Text = part.DamageType ?? "",
                ToolTip = "Тип урона этой части: список подсказывает, но можно вписать своё"
            };
            foreach (var (en, ru) in SpellService.DamageTypes)
                typeCombo.Items.Add($"{en} ({ru})");
            typeCombo.Text = DamageTypeToEditorText(part.DamageType);
            typeCombo.LostFocus += (s, e) =>
            {
                part.DamageType = ReadDamageTypeFromCombo(typeCombo);
                UpdateDamagePreview();
            };
            typeCombo.SelectionChanged += (s, e) =>
            {
                part.DamageType = ReadDamageTypeFromCombo(typeCombo);
                UpdateDamagePreview();
            };
            typePanel.Children.Add(typeCombo);
            Grid.SetColumn(typePanel, 2);
            grid.Children.Add(typePanel);

            var perLevel = new TextBox
            {
                Style = (Style)FindResource("TextField"),
                Text = part.PerLevelDamage ?? "",
                ToolTip = "Добавка за каждый уровень выше базового: например 1d6"
            };
            perLevel.TextChanged += (s, e) => { part.PerLevelDamage = perLevel.Text; UpdateDamagePreview(); };
            Grid.SetColumn(perLevel, 4);
            grid.Children.Add(perLevel);

            var remove = new Button
            {
                Content = new Helpers.IconView { Kind = "close", IconSize = 14, IconBrush = B("B.Mute") },
                Width = 28,
                Height = 28,
                Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)FindResource("GhostBtn"),
                ToolTip = "Убрать этот тип урона"
            };
            remove.Click += (s, e) =>
            {
                _extraParts.Remove(part);
                RebuildExtraParts();
                UpdateDamagePreview();
            };
            Grid.SetColumn(remove, 5);
            grid.Children.Add(remove);

            return new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Формула · тип урона · добавка за уровень",
                        Foreground = B("B.Mute"),
                        FontSize = 10,
                        Margin = new Thickness(0, 0, 0, 2)
                    },
                    grid
                }
            };
        }

        /// <summary>«Psychic» → «Psychic (психический)» для поля со списком.</summary>
        private static string DamageTypeToEditorText(string damageType)
        {
            string raw = (damageType ?? "").Trim();
            if (raw.Length == 0) return "";
            if (raw.Contains("(")) return raw;

            foreach (var (en, ru) in SpellService.DamageTypes)
                if (string.Equals(en, raw, StringComparison.OrdinalIgnoreCase)) return $"{en} ({ru})";

            return raw;
        }

        /// <summary>В базу уходит английское название без скобок.</summary>
        private static string ReadDamageTypeFromCombo(ComboBox combo)
        {
            string raw = (combo?.Text ?? "").Trim();
            if (raw.Length == 0) return "";
            int bracket = raw.IndexOf('(');
            return bracket > 0 ? raw.Substring(0, bracket).Trim() : raw;
        }

        #endregion

        /// <summary>Ставит тип урона в поле редактора.</summary>
        private void SetEditorDamageType(string damageType)
        {
            EnsureDamageTypeItems();
            if (EdDamageType == null) return;

            string raw = (damageType ?? "").Trim();
            EdDamageType.Text = raw;
            if (raw.Length == 0) return;

            foreach (var item in EdDamageType.Items)
            {
                string text = item?.ToString() ?? "";
                if (text.StartsWith(raw, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, raw, StringComparison.OrdinalIgnoreCase))
                {
                    EdDamageType.SelectedItem = item;
                    return;
                }
            }
        }

        /// <summary>Читает тип урона из поля: в базу уходит английское название.</summary>
        private string ReadEditorDamageType()
        {
            string raw = (EdDamageType?.Text ?? "").Trim();
            if (raw.Length == 0) return "";
            int bracket = raw.IndexOf('(');
            return bracket > 0 ? raw.Substring(0, bracket).Trim() : raw;
        }

        #endregion

        private void FillFullEditor(SpellData d)
        {
            _creatingNew = false;
            FillDescription(d);
            ApplyImage(null, FullImage, d, FullArtPlaceholder, FullArtLetter, FullArtRune);
            if (FullName != null) FullName.Text = string.IsNullOrWhiteSpace(d.Name) ? "(без названия)" : d.Name;
            if (FullLevelSchool != null)
            {
                string lvlTxt = d.Level == 0 ? "заговор" : $"{d.Level} уровень";
                FullLevelSchool.Text = string.IsNullOrWhiteSpace(d.School) ? lvlTxt : $"{lvlTxt}, {d.School.ToLowerInvariant()}";
            }
            if (FullCasting != null) FullCasting.Text = Blank(d.CastingTime);
            if (FullRange != null) FullRange.Text = Blank(d.Range);
            if (FullComponents != null) FullComponents.Text = Blank(d.Components);
            if (FullDuration != null) FullDuration.Text = Blank(d.Duration);
            if (FullSchool != null) FullSchool.Text = Blank(d.School);
            if (FullSource != null) FullSource.Text = Blank(d.Source);
            if (FullClasses != null) FullClasses.Text = Blank(d.Classes);
            if (FullText != null)
                FillRollText(FullText,
                    string.IsNullOrWhiteSpace(d.Description) ? "Описание отсутствует." : d.Description,
                    "Описание", d);
            if (FullHigher != null)
            {
                FillRollText(FullHigher, d.AtHigherLevels, "На больших уровнях", d);
                FullHigher.Visibility = string.IsNullOrWhiteSpace(d.AtHigherLevels) ? Visibility.Collapsed : Visibility.Visible;
            }
            if (FullModifiedBadge != null) FullModifiedBadge.Visibility = d.IsModified ? Visibility.Visible : Visibility.Collapsed;

            if (EdName != null) EdName.Text = d.Name ?? "";
            if (EdLevel != null) EdLevel.SelectedIndex = d.Level >= 0 && d.Level <= 9 ? d.Level : 0;
            if (EdSchool != null) EdSchool.Text = d.School ?? "";
            if (EdCasting != null) EdCasting.Text = d.CastingTime ?? "";
            if (EdRange != null) EdRange.Text = d.Range ?? "";
            SetComponents(d.Components);
            if (EdDuration != null) EdDuration.Text = d.Duration ?? "";
            SetClasses(d.Classes);
            if (ChkConcentration != null) ChkConcentration.IsChecked = d.Concentration;
            if (ChkRitual != null) ChkRitual.IsChecked = d.Ritual;
            if (EdDescription != null) EdDescription.Text = d.Description ?? "";
            if (EdHigher != null) EdHigher.Text = d.AtHigherLevels ?? "";
            if (EdDamageFormula != null) EdDamageFormula.Text = d.DamageFormula ?? "";
            SetEditorDamageType(d.DamageType);
            if (EdPerLevelDamage != null) EdPerLevelDamage.Text = d.PerLevelDamage ?? "";
            if (EdHealFormula != null) EdHealFormula.Text = d.HealFormula ?? "";
            if (EdHealPerLevel != null) EdHealPerLevel.Text = d.HealPerLevel ?? "";
            if (EdSaveNote != null) EdSaveNote.Text = d.SaveNote ?? "";
            SetExtraParts(d.ExtraDamageParts);
            if (EdSource != null) EdSource.Text = d.Source ?? "";
            if (EdPage != null) EdPage.Text = d.Page ?? "";
            if (EdMaterial != null) EdMaterial.Text = d.Material ?? "";

            bool rulebook = !string.IsNullOrWhiteSpace(d.Source) && !string.Equals(d.Source, "custom", StringComparison.OrdinalIgnoreCase);
            if (TxtRulebookWarning != null)
            {
                TxtRulebookWarning.Visibility = rulebook ? Visibility.Visible : Visibility.Collapsed;
                if (TxtRulebookWarningText != null && rulebook)
                    TxtRulebookWarningText.Text = $"Внимание: это заклинание из официального справочника ({d.Source}). Правки затронут всех персонажей и будут помечены как «изменено».";
            }
            BuildTagsAssignPanel(d.IdSpell);
            if (BtnCancelCreate != null) BtnCancelCreate.Visibility = Visibility.Collapsed;
        }
        private void OpenCreateNew()
        {
            _creatingNew = true;
            ApplyImage(null, FullImage, null, FullArtPlaceholder, FullArtLetter, FullArtRune);
            if (FullName != null) FullName.Text = "(новое заклинание)";
            if (FullLevelSchool != null) FullLevelSchool.Text = "—";
            if (FullCasting != null) FullCasting.Text = "—";
            if (FullRange != null) FullRange.Text = "—";
            if (FullComponents != null) FullComponents.Text = "—";
            if (FullDuration != null) FullDuration.Text = "—";
            if (FullSchool != null) FullSchool.Text = "—";
            if (FullSource != null) FullSource.Text = "custom";
            if (FullClasses != null) FullClasses.Text = "—";
            if (FullText != null) FullText.Text = "Заполни поля справа и нажми «Сохранить карточку».";
            if (FullHigher != null) { FullHigher.Text = ""; FullHigher.Visibility = Visibility.Collapsed; }
            if (FullModifiedBadge != null) FullModifiedBadge.Visibility = Visibility.Collapsed;

            if (EdName != null) EdName.Text = "";
            if (EdLevel != null) EdLevel.SelectedIndex = 0;
            if (EdSchool != null) EdSchool.Text = "";
            if (EdCasting != null) EdCasting.Text = "";
            if (EdRange != null) EdRange.Text = "";
            SetComponents("");
            if (EdDuration != null) EdDuration.Text = "";
            SetClasses("");
            if (ChkConcentration != null) ChkConcentration.IsChecked = false;
            if (ChkRitual != null) ChkRitual.IsChecked = false;
            if (EdDescription != null) EdDescription.Text = "";
            if (EdHigher != null) EdHigher.Text = "";
            if (EdDamageFormula != null) EdDamageFormula.Text = "";
            SetEditorDamageType("");
            if (EdPerLevelDamage != null) EdPerLevelDamage.Text = "";
            if (EdHealFormula != null) EdHealFormula.Text = "";
            if (EdHealPerLevel != null) EdHealPerLevel.Text = "";
            if (EdSaveNote != null) EdSaveNote.Text = "";
            SetExtraParts(null);
            if (EdSource != null) EdSource.Text = "custom";
            if (EdPage != null) EdPage.Text = "";
            if (EdMaterial != null) EdMaterial.Text = "";
            if (TxtRulebookWarning != null) TxtRulebookWarning.Visibility = Visibility.Collapsed;

            BuildTagsAssignPanel(0);
            if (FullEditorOverlay != null) FullEditorOverlay.Visibility = Visibility.Visible;
            if (BtnCancelCreate != null) BtnCancelCreate.Visibility = Visibility.Visible;
            if (EdName != null) EdName.Focus();
        }
        private void BtnFullSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string name = (EdName?.Text ?? "").Trim();
                int lvl = EdLevel?.SelectedIndex ?? 0;
                string school = EdSchool?.Text ?? "";
                string casting = EdCasting?.Text ?? "";
                string range = EdRange?.Text ?? "";
                string comp = GetComponents();
                string dur = EdDuration?.Text ?? "";
                string classes = GetClasses();
                bool conc = ChkConcentration?.IsChecked == true;
                bool ritual = ChkRitual?.IsChecked == true;
                string desc = EdDescription?.Text ?? "";
                string higher = EdHigher?.Text ?? "";
                string source = (EdSource?.Text ?? "").Trim();
                string page = EdPage?.Text ?? "";
                string material = EdMaterial?.Text ?? "";
                string dmgFormula = (EdDamageFormula?.Text ?? "").Trim();
                string dmgType = ReadEditorDamageType();
                string perLevel = (EdPerLevelDamage?.Text ?? "").Trim();
                string healFormula = (EdHealFormula?.Text ?? "").Trim();
                string healPerLevel = (EdHealPerLevel?.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(source)) source = "custom";

                if (_creatingNew)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        ShowConfirmDialog("Название", "Введи название нового заклинания.", "Ок", "", _ => { EdName?.Focus(); });
                        EdName?.Focus();
                        return;
                    }
                    var newSpell = new SpellData
                    {
                        Name = name,
                        Level = lvl,
                        School = school,
                        CastingTime = casting,
                        Range = range,
                        Components = comp,
                        Duration = dur,
                        Classes = classes,
                        Concentration = conc,
                        Ritual = ritual,
                        Description = desc,
                        AtHigherLevels = higher,
                        Source = source,
                        Page = page,
                        Material = material,
                        DamageFormula = dmgFormula,
                        DamageType = dmgType,
                        PerLevelDamage = perLevel,
                        HealFormula = healFormula,
                        HealPerLevel = healPerLevel,
                        ExtraDamageParts = ReadExtraParts(),
                        SaveNote = (EdSaveNote?.Text ?? "").Trim()
                    };
                    int newSpellId = SpellService.CreateNewSpell(newSpell, _characterId);
                    Helpers.SpellTextLinker.Invalidate();
                    SpellService.SaveSpellTags(newSpellId, ReadChosenTags());
                    _creatingNew = false;
                    LoadSpellsFromDb();
                    _spellTags = SpellService.LoadSpellTagMap();
                    ApplyView();
                    var created = _prepared.Find(x => x.IdSpell == newSpellId) ?? _known.Find(x => x.IdSpell == newSpellId);
                    if (created != null) { BuildFullList(created); FillFullEditor(created); }
                    ShowToast("Заклинание создано", "＋", B("B.Accent"));
                    return;
                }
                if (_fullIndex < 0 || _fullIndex >= _fullList.Count) return;
                var d = _fullList[_fullIndex];
                if (d.IdSpell <= 0) return;
                bool rulebook = !string.IsNullOrWhiteSpace(d.Source) && !string.Equals(d.Source, "custom", StringComparison.OrdinalIgnoreCase);
                d.Name = string.IsNullOrWhiteSpace(name) ? d.Name : name;
                d.Level = lvl; d.School = school; d.CastingTime = casting; d.Range = range;
                d.Components = comp; d.Duration = dur; d.Classes = classes; d.Concentration = conc;
                d.Ritual = ritual; d.Description = desc; d.AtHigherLevels = higher; d.Source = source;
                d.Page = page; d.Material = material;
                d.DamageFormula = dmgFormula; d.DamageType = dmgType; d.PerLevelDamage = perLevel;
                d.HealFormula = healFormula; d.HealPerLevel = healPerLevel;
                d.ExtraDamageParts = ReadExtraParts();
                d.SaveNote = (EdSaveNote?.Text ?? "").Trim();
                d.IsModified = rulebook || d.IsModified;
                SpellService.SaveSpell(d);
                Helpers.SpellTextLinker.Invalidate();
                SpellService.SaveSpellTags(d.IdSpell, ReadChosenTags());
                _spellTags = SpellService.LoadSpellTagMap();
                FinalizeCardVisuals(d);
                ApplyView();
                FillFullEditor(d);
                ShowToast("Карточка сохранена", "check", B("B.Ok"));
            }
            catch (Exception ex)
            {
                Log("BtnFullSave_Click", ex);
                MessageDialog.Error(this, $"Не удалось сохранить: {ex.Message}");
            }
        }
        private List<int> ReadChosenTags()
        {
            var chosen = new List<int>();
            if (TagsAssignPanel != null)
                foreach (var ch in TagsAssignPanel.Children)
                    if (ch is CheckBox cb && cb.IsChecked == true && cb.Tag is int id) chosen.Add(id);
            return chosen;
        }
        private void FullImage_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_creatingNew) { MessageDialog.Info(this, "Сначала сохрани карточку, затем загружай арт.", "Новое заклинание"); return; }
                if (_fullIndex < 0 || _fullIndex >= _fullList.Count) return;
                var d = _fullList[_fullIndex];
                var dlg = new OpenFileDialog
                {
                    Filter = "Изображения (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif|Все файлы (*.*)|*.*",
                    Title = "Выберите арт заклинания"
                };
                if (dlg.ShowDialog() != true) return;

                // Сохраняем в AppData (рядом с exe писать нельзя после установки).
                // PhotoHelper возвращает относительный путь вида Spells/...
                string rel = PhotoHelper.SaveIndividualSpellPhoto(d.IdCharSpell, dlg.FileName);
                if (string.IsNullOrWhiteSpace(rel))
                {
                    ShowToast("Не удалось сохранить арт", "!", B("B.Danger"));
                    return;
                }

                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "UPDATE CharacterSpells SET IndividualImagePath=@p WHERE id_char_spell=@cs", conn);
                cmd.Parameters.AddWithValue("@p", rel);
                cmd.Parameters.AddWithValue("@cs", d.IdCharSpell);
                cmd.ExecuteNonQuery();
                d.ImagePath = rel;
                d.CachedStatic = null;
                ApplyView();
                FillFullEditor(d);
                ShowToast("Арт загружен", "image", B("B.Arcane"));
            }
            catch (Exception ex) { Log("FullImage_Click", ex); }
        }
        #endregion

        #region === ТЕГИ: ПРИСВОЕНИЕ ===
        private void BuildTagsAssignPanel(int idSpell)
        {
            if (TagsAssignPanel == null) return;
            TagsAssignPanel.Children.Clear();
            var all = _allTags.Count > 0 ? _allTags : SpellService.LoadAllTags();
            var assigned = SpellService.LoadAssignedTagIds(idSpell);
            if (all.Count == 0)
            {
                TagsAssignPanel.Children.Add(new TextBlock { Text = "нет тегов — создай в «Редакторе тегов»", Foreground = B("B.Mute"), FontSize = 11, FontStyle = FontStyles.Italic });
                return;
            }
            foreach (var t in all)
            {
                TagsAssignPanel.Children.Add(new CheckBox
                {
                    Content = t.Name,
                    Tag = t.Id,
                    IsChecked = assigned.Contains(t.Id),
                    Foreground = B("B.Ink"),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 12, 6)
                });
            }
        }
        #endregion

        #region === СПРАВОЧНИК ===
        private async void OpenSpellbook()
        {
            try
            {
                if (SbStatus != null) SbStatus.Text = "Загрузка справочника…";
                if (SpellbookOverlay != null) SpellbookOverlay.Visibility = Visibility.Visible;
                // Автоматический импорт убран: справочник показываем только из того,
                // что уже есть в базе. Данные игрок добавляет сам, вручную.
                _allRefRows = SpellService.LoadSpellReference();
                if (SbSourceFilter != null)
                {
                    string keep = _sourceFilter;
                    SbSourceFilter.SelectionChanged -= SbSourceFilter_SelectionChanged;
                    SbSourceFilter.Items.Clear();
                    SbSourceFilter.Items.Add("Все источники");
                    foreach (var s in SpellService.LoadDistinctSources()) SbSourceFilter.Items.Add(s);
                    SbSourceFilter.SelectedIndex = string.IsNullOrEmpty(keep) ? 0 : Math.Max(0, SbSourceFilter.Items.IndexOf(keep));
                    SbSourceFilter.SelectionChanged += SbSourceFilter_SelectionChanged;
                    _sourceFilter = SbSourceFilter.SelectedIndex <= 0 ? "" : (SbSourceFilter.SelectedItem as string ?? "");
                }
                ApplySbFilter();
                SbList.ItemsSource = _allRefRows;
                if (SbStatus != null)
                    SbStatus.Text = _allRefRows.Count == 0
                        ? "Справочник пуст — импортируйте файл заклинаний (кнопка «Импорт»)"
                        : $"Найдено: {_allRefRows.Count}";
                SbSearch?.Focus();
            }
            catch (Exception ex) { Log("OpenSpellbook", ex); }
        }
        /// <summary>
        /// Импорт справочника заклинаний из файла (JSON или .dtn).
        /// Раньше для этого нужно было класть spells.json в папку Data/srd.
        /// </summary>
        /// <summary>
        /// Выгружает весь справочник заклинаний в .hmwtpack: и свои строки,
        /// и строки из книг. Спрашиваем название и источник — по ним видно,
        /// откуда данные.
        /// </summary>
        private void SbExportPack_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (int total, int custom) = SpellPackService.CountSpells();
                if (total == 0)
                {
                    MessageDialog.Info(this, "Справочник пуст — выгружать нечего.", "Экспорт пака");
                    return;
                }

                string name = NotePromptWindow.AskText(Window.GetWindow(this), "Пак заклинаний",
                    "Название пака (это имя файла, кириллицей лучше не надо)", "Spells", "Далее");
                if (string.IsNullOrWhiteSpace(name)) return;

                string automatic = string.Join(", ", SpellService.LoadDistinctSources()
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                string sourceNote = NotePromptWindow.AskText(Window.GetWindow(this), "Пак заклинаний",
                    "Откуда данные (книги, перевод, ссылка)", automatic, "Сохранить");
                if (sourceNote == null) return;

                var dialog = new SaveFileDialog
                {
                    Title = "Сохранить пак заклинаний",
                    Filter = "Пак заклинаний (*.hmwtpack)|*.hmwtpack|Все файлы (*.*)|*.*",
                    FileName = name.Trim() + SpellPackService.FileExtension,
                    DefaultExt = SpellPackService.FileExtension
                };
                if (dialog.ShowDialog() != true) return;

                if (SpellPackService.Export(dialog.FileName, false, name, sourceNote,
                        out int count, out int customCount, out string error))
                {
                    MessageDialog.Info(this,
                        $"Пак сохранён.\n\nФайл: {IOPath.GetFileName(dialog.FileName)}\n" +
                        $"Заклинаний: {count} (своих: {customCount})\n\n" +
                        "Внутри manifest.json — название, список источников и место под лицензию. " +
                        "Перед передачей другим проверьте право распространять текст: содержимое SRD " +
                        "идёт под CC-BY-4.0, строки из книг — нет.",
                        "Экспорт пака");
                }
                else
                {
                    MessageDialog.Error(this, error, "Не удалось сохранить пак");
                }
            }
            catch (Exception ex)
            {
                Log("SbExportPack", ex);
                MessageDialog.Error(this, ex.Message, "Не удалось сохранить пак");
            }
        }

        /// <summary>Добавляет заклинания из пака; совпадающие по названию и источнику пропускает.</summary>
        private void SbImportPack_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Открыть пак заклинаний",
                    Filter = "Пак заклинаний (*.hmwtpack)|*.hmwtpack|Все файлы (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;

                if (!SpellPackService.Import(dialog.FileName, out int added, out int skipped, out string error))
                {
                    MessageDialog.Error(this, error, "Не удалось прочитать пак");
                    return;
                }

                OpenSpellbook();
                if (SbStatus != null)
                {
                    SbStatus.Text = added == 0
                        ? $"Новых заклинаний нет — все {skipped} уже в справочнике"
                        : $"Из пака добавлено: {added}, пропущено: {skipped}";
                }
            }
            catch (Exception ex)
            {
                Log("SbImportPack", ex);
                MessageDialog.Error(this, ex.Message, "Не удалось прочитать пак");
            }
        }
        private void SbImportFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Файл заклинаний",
                    Filter = "Файл заклинаний (*.dtn;*.json)|*.dtn;*.json|Все файлы (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;

                int added = SpellFileImporter.Import(dialog.FileName, out int skipped, out string error);

                if (added == 0 && !string.IsNullOrWhiteSpace(error))
                {
                    MessageDialog.Error(this, error, "Не удалось прочитать файл");
                    return;
                }

                OpenSpellbook();
                if (SbStatus != null)
                {
                    SbStatus.Text = added == 0
                        ? $"Новых заклинаний нет — все {skipped} уже в справочнике"
                        : $"Импортировано: {added}, уже было: {skipped}";
                }
            }
            catch (Exception ex)
            {
                Log("SbImportFile", ex);
                MessageDialog.Error(this, ex.Message, "Не удалось прочитать файл");
            }
        }
        private void SbAddSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SbList?.SelectedItem is not SpellRefRow row)
                {
                    ShowConfirmDialog("Ничего не выбрано", "Выбери заклинание в списке.", "Ок", "", _ => { });
                    return;
                }
                bool added = SpellService.AddSpellToCharacter(_characterId, row.Id);
                if (!added)
                {
                    MessageDialog.Info(this, $"«{row.Name}» уже есть у персонажа.", "Дубликат");
                    return;
                }
                LoadSpellsFromDb();
                ApplyView();
                if (SbStatus != null) SbStatus.Text = $"Добавлено: {row.Name}";
                ShowToast($"Добавлено: {row.Name}", "＋", B("B.Accent"));
            }
            catch (Exception ex) { Log("SbAddSelected_Click", ex); }
        }
        private void SbSourceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SbSourceFilter == null) return;
            _sourceFilter = SbSourceFilter.SelectedIndex <= 0 ? "" : (SbSourceFilter.SelectedItem as string ?? "");
            ApplySbFilter();
        }
        private void SbSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplySbFilter();
        private void ApplySbFilter()
        {
            try
            {
                string q = (SbSearch?.Text ?? "").Trim().ToLowerInvariant();
                IEnumerable<SpellRefRow> f = _allRefRows;
                if (!string.IsNullOrEmpty(_sourceFilter))
                    f = f.Where(r => string.Equals(r.Source, _sourceFilter, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(q))
                    f = f.Where(r => (r.Name ?? "").ToLowerInvariant().Contains(q) || (r.School ?? "").ToLowerInvariant().Contains(q));
                var list = f.ToList();
                SbList.ItemsSource = list;
                if (SbStatus != null) SbStatus.Text = $"Показано: {list.Count} из {_allRefRows.Count}";
            }
            catch (Exception ex) { Log("ApplySbFilter", ex); }
        }
        #endregion

        #region === РЕДАКТОР ТЕГОВ ===
        private void OpenTags()
        {
            try
            {
                LoadTagsList();
                if (TagNewName != null) TagNewName.Text = "";
                if (TagNewCategory != null) TagNewCategory.Text = "";
                if (TagsOverlay != null) TagsOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Log("OpenTags", ex); }
        }
        private void LoadTagsList()
        {
            _allTags = SpellService.LoadAllTags();
            if (TagsList != null) TagsList.ItemsSource = _allTags;
        }
        private void BtnTagAdd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string name = (TagNewName?.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) { ShowConfirmDialog("Пусто", "Введи название тега.", "Ок", "", _ => { }); return; }
                string cat = (TagNewCategory?.Text ?? "").Trim();
                bool created = SpellService.CreateTag(name, cat);
                if (!created) { MessageDialog.Info(this, "Такой тег уже существует.", "Дубликат"); return; }
                if (TagNewName != null) TagNewName.Text = "";
                LoadTagsList();
                ShowToast($"Тег «{name}» создан", "✦", B("B.Arcane"));
            }
            catch (Exception ex) { Log("BtnTagAdd_Click", ex); }
        }
        private void BtnTagDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TagsList?.SelectedItem is not SpellTag row) { MessageDialog.Info(this, "Выбери тег для удаления.", "Ничего не выбрано"); return; }
                var ans = MessageDialog.Confirm(this, $"Удалить тег «{row.Name}»?", "Подтверждение", "Удалить");
                if (!ans) return;
                SpellService.DeleteTag(row.Id);
                LoadTagsList();
                _activeTagFilters.Remove(row.Id);
                RenderActiveFilterChips();
                ApplyView();
            }
            catch (Exception ex) { Log("BtnTagDelete_Click", ex); }
        }
        #endregion

        #region === УДАЛЕНИЕ КАРТОЧКИ ===
        private void OnCardDeleteClick(object sender, RoutedEventArgs e)
        {
            try
            {
                e.Handled = true;
                if ((sender as FrameworkElement)?.DataContext is not SpellData d) return;

                string spellName = string.IsNullOrWhiteSpace(d.Name) ? "заклинание" : d.Name;
                string message = d.IsPrepared
                    ? $"Убрать «{spellName}» из подготовленных?\n\nОно вернётся в известные заклинания."
                    : $"Удалить «{spellName}» из известных заклинаний?\n\nЕсли это не пользовательское заклинание, оно останется в справочнике.";

                ShowConfirmDialog(
                    title: "Подтверждение удаления",
                    message: message,
                    yesText: d.IsPrepared ? "Убрать" : "Удалить",
                    noText: "Отмена",
                    callback: confirmed =>
                    {
                        if (!confirmed) return;
                        SpellService.RemoveSpellSmart(_characterId, d.IdCharSpell);
                        LoadSpellsFromDb();
                        ApplyView();
                        ShowToast(
                            d.IsPrepared ? "Заклинание возвращено в известные" : "Заклинание удалено",
                            "✓", B("B.Ok"));
                    }
                );
            }
            catch (Exception ex) { Log("OnCardDeleteClick", ex); }
        }
        #endregion

        #region === НАСТРОЙКИ ===
        private void LoadSettingsIntoPanel()
        {
            try
            {
                var slots = SpellService.LoadAllSlots(_characterId);
                SetSlotField(Slot1, slots, 1); SetSlotField(Slot2, slots, 2); SetSlotField(Slot3, slots, 3);
                SetSlotField(Slot4, slots, 4); SetSlotField(Slot5, slots, 5); SetSlotField(Slot6, slots, 6);
                SetSlotField(Slot7, slots, 7); SetSlotField(Slot8, slots, 8); SetSlotField(Slot9, slots, 9);
                if (TxtWarlockSlots != null) TxtWarlockSlots.Text = GetIntSetting(SKey("WarlockSlots"), 0).ToString();
                if (TxtPreparedLimit != null) TxtPreparedLimit.Text = GetIntSetting(SKey("PreparedLimit"), 0).ToString();
                string stat = GetStringSetting(SKey("SpellStat"), "Charisma");
                if (CmbSpellStat != null) CmbSpellStat.SelectedIndex = stat == "Intelligence" ? 0 : stat == "Wisdom" ? 1 : 2;
                if (CmbMaxLevel != null)
                {
                    // 0 = «0-9» (все уровни), дальше уровень-ограничитель.
                    // В старом списке был лишний пункт «1», поэтому индекс
                    // съезжал: выбранный 4-й уровень показывал V. MaxLevelV2
                    // хранит уже правильный уровень; старый MaxLevel (индекс)
                    // переводим один раз.
                    int ml;
                    string v2 = GetStringSetting(SKey("MaxLevelV2"), null);
                    if (!string.IsNullOrEmpty(v2) && int.TryParse(v2, out int saved))
                    {
                        ml = saved;
                    }
                    else
                    {
                        int legacy = GetIntSetting(SKey("MaxLevel"), 0);
                        ml = legacy >= 2 ? legacy - 1 : legacy;
                        SetSetting(SKey("MaxLevelV2"), Clamp(ml, 0, 9).ToString());
                    }
                    CmbMaxLevel.SelectedIndex = Clamp(ml, 0, 9);
                }
                ApplyMaxLevelTabs();
                RefreshSpellFormulas();
            }
            catch (Exception ex) { Log("LoadSettingsIntoPanel", ex); }
        }
        private static void SetSlotField(TextBox tb, Dictionary<int, int> slots, int lvl)
        {
            if (tb != null) tb.Text = (slots.TryGetValue(lvl, out int v) ? v : 0).ToString();
        }
        private void BtnCleanupCache_Click(object sender, RoutedEventArgs e)
        {
            var report = Services.CacheCleanupService.Scan();
            if (report.Count == 0)
            {
                MessageDialog.Info(this, "Нечего чистить: кэш карт и фотографий комнат уже в порядке.",
                    "Очистка кэша");
                return;
            }

            string text =
                "Будут удалены только временные данные:\n" +
                $"• карты: дубликаты, эскизы и скачанные копии — {report.MapFiles} файлов;\n" +
                $"• старые фото комнат без ссылок — {report.PhotoFiles} файлов;\n" +
                $"• всего: {report.Count} файлов, примерно {BytesText(report.Bytes)}.\n\n" +
                "Карты, используемые в сценах, текущие аватары, Monsters, Audio и база не затрагиваются.";
            if (!MessageDialog.Confirm(this, text, "Очистка кэша", "Очистить", "Отмена", false))
                return;

            var result = Services.CacheCleanupService.Clean();
            string done = result.Errors.Count == 0
                ? $"Удалено файлов: {result.Deleted}. Освобождено: {BytesText(result.Freed)}."
                : $"Удалено файлов: {result.Deleted}. Освобождено: {BytesText(result.Freed)}.\n" +
                  $"Не удалось удалить: {result.Errors.Count}. Например: {result.Errors[0]}";
            MessageDialog.Info(this, done, "Очистка кэша");
        }

        private static string BytesText(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.0") + " ГБ";
            if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " МБ";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " КБ";
            return bytes + " Б";
        }
        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                for (int lvl = 1; lvl <= 9; lvl++)
                {
                    TextBox tb = lvl switch { 1 => Slot1, 2 => Slot2, 3 => Slot3, 4 => Slot4, 5 => Slot5, 6 => Slot6, 7 => Slot7, 8 => Slot8, _ => Slot9 };
                    int total = ParseClamped(tb?.Text, 0, 99, 0);
                    SpellService.SaveSlotCount(_characterId, lvl, total);
                }
                SetSetting(SKey("WarlockSlots"), ParseClamped(TxtWarlockSlots?.Text, 0, 99, 0).ToString());
                SetSetting(SKey("PreparedLimit"), ParseClamped(TxtPreparedLimit?.Text, 0, 999, 0).ToString());
                string statKey = CmbSpellStat?.SelectedIndex == 0 ? "Intelligence" : CmbSpellStat?.SelectedIndex == 1 ? "Wisdom" : "Charisma";
                SetSetting(SKey("SpellStat"), statKey);
                SetSetting(SKey("MaxLevelV2"), (CmbMaxLevel?.SelectedIndex ?? 9).ToString());
                LoadSpellSlotsForLevel(_currentLevel);
                ApplyView();
                ApplyMaxLevelTabs();
                RefreshSpellFormulas();
                if (ToggleSettings != null) ToggleSettings.IsChecked = false;
                ShowToast("Настройки сохранены", "✓", B("B.Ok"));
            }
            catch (Exception ex)
            {
                Log("BtnSaveSettings_Click", ex);
                MessageDialog.Error(this, $"Не удалось сохранить настройки: {ex.Message}");
            }
        }
        private void RefreshSpellFormulas()
        {
            try
            {
                string stat = CmbSpellStat?.SelectedIndex == 0 ? "Intelligence" : CmbSpellStat?.SelectedIndex == 1 ? "Wisdom" : "Charisma";
                var (dc, attack) = SpellService.GetSpellFormulas(_characterId, stat);
                if (TxtSpellSaveDC != null) TxtSpellSaveDC.Text = $" = {dc}";
                if (TxtSpellAttack != null) TxtSpellAttack.Text = $" = +{attack}";
            }
            catch (Exception ex) { Log("RefreshSpellFormulas", ex); }
        }
        #endregion

        #region === ТОСТ ===
        private void ShowToast(string text, string icon, Brush iconColor)
        {
            if (Toast == null) return;
            if (ToastText != null) ToastText.Text = text;
            if (ToastIcon != null) DmUiKit.ApplyIcon(ToastIcon, icon, iconColor ?? B("B.Ok"));
            Toast.Visibility = Visibility.Visible;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            if (_toastHideTimer == null)
            {
                _toastHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
                _toastHideTimer.Tick += (s, e) =>
                {
                    _toastHideTimer.Stop();
                    var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(320));
                    fade.Completed += (ss, ee) => { Toast.Visibility = Visibility.Collapsed; };
                    Toast.BeginAnimation(OpacityProperty, fade);
                };
            }
            _toastHideTimer.Stop();
            _toastHideTimer.Start();
        }
        #endregion

        #region === НАСТРОЙКИ: ХЕЛПЕРЫ ===
        private string SKey(string name) => _settingsContext == "global" ? $"Global_{name}" : $"Character_{_characterId}_{name}";
        private string GetStringSetting(string key, string fallback)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection(); conn.Open();
                using var c = new SqliteCommand("SELECT Value FROM Settings WHERE Key=@k", conn);
                c.Parameters.AddWithValue("@k", key);
                var o = c.ExecuteScalar();
                return (o == null || o == DBNull.Value) ? fallback : (o.ToString() ?? fallback);
            }
            catch (Exception ex) { Log("GetStringSetting", ex); return fallback; }
        }
        private int GetIntSetting(string key, int fallback) => int.TryParse(GetStringSetting(key, null), out int v) ? v : fallback;
        private void SetSetting(string key, string value)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection(); conn.Open();
                using var c = new SqliteCommand("INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@k, @v)", conn);
                c.Parameters.AddWithValue("@k", key); c.Parameters.AddWithValue("@v", value ?? "");
                c.ExecuteNonQuery();
            }
            catch (Exception ex) { Log("SetSetting", ex); }
        }
        #endregion

        #region === УТИЛИТЫ ===
        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        private static int ParseClamped(string text, int min, int max, int fallback)
        {
            if (!int.TryParse((text ?? "").Trim(), out int v)) return fallback;
            return Clamp(v, min, max);
        }
        private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
        private Brush B(string key)
        {
            try { return (Brush)FindResource(key); }
            catch { return Brushes.Gray; }
        }
        /// <summary>
        /// Ищет файл арта: сначала в AppData (куда пишет приложение), затем рядом
        /// с приложением (старые установки). Раньше искалось только рядом с exe,
        /// поэтому загруженный арт «не находился».
        /// </summary>
        private static string ResolveExistingPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;

            try
            {
                if (IOPath.IsPathRooted(relativePath))
                    return IOFile.Exists(relativePath) ? relativePath : null;

                var normalized = relativePath.Replace('/', IOPath.DirectorySeparatorChar);

                // 1. AppData\HMWT\... — основное место хранения
                var appData = IOPath.Combine(PhotoHelper.GetWritableRoot(), normalized);
                if (IOFile.Exists(appData)) return appData;

                // 2. AppData\HMWT\CharacterPhoto\... — на случай путей с этим префиксом
                var photos = IOPath.Combine(DatabaseHelper.GetPhotoBasePath(), normalized);
                if (IOFile.Exists(photos)) return photos;

                // 3. Рядом с приложением — легаси
                var legacy = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, normalized);
                if (IOFile.Exists(legacy)) return legacy;

                return null;
            }
            catch { return null; }
        }

        /// <summary>Совместимость: путь, который может не существовать (для отладки).</summary>
        private static string ResolvePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            try
            {
                return IOPath.IsPathRooted(relativePath)
                    ? relativePath
                    : IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            }
            catch { return null; }
        }

        private BitmapImage LoadImageSafe(string relativePath)
        {
            string full = ResolveExistingPath(relativePath);
            if (full == null) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(full, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex) { Log("LoadImageSafe", ex); return null; }
        }
        private static string ToRoman(int n) => n switch
        {
            0 => "★",
            1 => "I",
            2 => "II",
            3 => "III",
            4 => "IV",
            5 => "V",
            6 => "VI",
            7 => "VII",
            8 => "VIII",
            9 => "IX",
            _ => n.ToString()
        };
        private static void Log(string where, Exception ex) =>
            Debug.WriteLine($"[SpellsView][{where}] {ex.Message}");
        #endregion

        #region === ЧЕКБОКСЫ: КОМПОНЕНТЫ И КЛАССЫ ===
        private void BuildClassesPanel()
        {
            if (ClassesPanel == null) return;
            ClassesPanel.Children.Clear();
            foreach (var cls in DefaultClasses)
            {
                var cb = new CheckBox
                {
                    Content = cls,
                    Tag = cls,
                    Foreground = (Brush)FindResource("B.Ink"),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 12, 6)
                };
                ClassesPanel.Children.Add(cb);
            }
        }
        private void SetComponents(string comp)
        {
            if (ChkCompV != null) ChkCompV.IsChecked = !string.IsNullOrEmpty(comp) && (comp.Contains("В") || comp.Contains("V"));
            if (ChkCompS != null) ChkCompS.IsChecked = !string.IsNullOrEmpty(comp) && (comp.Contains("С") || comp.Contains("S"));
            if (ChkCompM != null) ChkCompM.IsChecked = !string.IsNullOrEmpty(comp) && (comp.Contains("М") || comp.Contains("M"));
        }
        private string GetComponents()
        {
            var parts = new List<string>();
            if (ChkCompV?.IsChecked == true) parts.Add("В");
            if (ChkCompS?.IsChecked == true) parts.Add("С");
            if (ChkCompM?.IsChecked == true) parts.Add("М");
            return string.Join(", ", parts);
        }
        private void SetClasses(string classes)
        {
            if (ClassesPanel == null) return;
            foreach (var child in ClassesPanel.Children)
            {
                if (child is CheckBox cb && cb.Tag is string cls)
                {
                    cb.IsChecked = !string.IsNullOrEmpty(classes) && classes.Contains(cls);
                }
            }
        }
        private string GetClasses()
        {
            var parts = new List<string>();
            if (ClassesPanel != null)
            {
                foreach (var child in ClassesPanel.Children)
                {
                    if (child is CheckBox cb && cb.IsChecked == true && cb.Tag is string cls)
                    {
                        parts.Add(cls);
                    }
                }
            }
            return string.Join(", ", parts);
        }
        #endregion

        #region === ОБМЕН С ДРУГИМИ ИГРОКАМИ ===

        /// <summary>Выгружает свои заклинания и предметы в файл обмена.</summary>
        private void BtnShareExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // На этой вкладке выгружаем только заклинания: предметы
                // выгружаются на вкладке инвентаря (просьба пользователя)
                var counts = ShareService.CountCustom();
                if (counts.Spells == 0)
                {
                    ShowToast("Своих заклинаний нет. Весь справочник выгружается в «Справочник» → «Экспорт пака»", "!", B("B.Danger"));
                    return;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Файл обмена HMWT (*.hmwtpack)|*.hmwtpack",
                    FileName = "HMWT-свои-заклинания-" + DateTime.Now.ToString("yyyy-MM-dd") + ShareService.FileExtension,
                    Title = "Куда сохранить свои заклинания и предметы"
                };
                if (dialog.ShowDialog() != true) return;

                var manifest = ShareService.Export(dialog.FileName, out string error,
                    includeItems: false, includeSpells: true);
                if (manifest == null)
                {
                    ShowToast("Не удалось выгрузить: " + error, "!", B("B.Danger"));
                    return;
                }

                ShowToast($"Выгружено: заклинаний {manifest.Spells.Count}, предметов {manifest.Items.Count}", "✓", B("B.Ok"));
                if (Window.GetWindow(this) is Window host)
                {
                    MessageDialog.Info(host,
                        $"Файл готов:\n{dialog.FileName}\n\n" +
                        $"Свои заклинания: {manifest.Spells.Count}\n" +
                        $"Свои предметы: {manifest.Items.Count}\n\n" +
                        "Передайте файл другому игроку — он загрузит его кнопкой загрузки на этой же вкладке.",
                        "Выгрузка завершена");
                }
            }
            catch (Exception ex)
            {
                Log("ShareExport", ex);
                ShowToast("Ошибка выгрузки: " + ex.Message, "!", B("B.Danger"));
            }
        }

        /// <summary>Загружает файл обмена от другого игрока.</summary>
        private void BtnShareImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Файл обмена HMWT (*.hmwtpack)|*.hmwtpack|Все файлы (*.*)|*.*",
                    Title = "Файл с заклинаниями и предметами друга"
                };
                if (dialog.ShowDialog() != true) return;

                var result = ShareService.Import(dialog.FileName, _characterId);
                if (!string.IsNullOrEmpty(result.Error))
                {
                    ShowToast("Не удалось загрузить: " + result.Error, "!", B("B.Danger"));
                    return;
                }

                // Кнопка «обмен своими» часто получает файл пака справочника.
                // Если в файле не оказалось ни предметов, ни своих заклинаний —
                // пробуем прочитать его как пак справочника, чтобы не гонять
                // человека по разным кнопкам.
                if (result.Total == 0 && SpellPackService.Import(dialog.FileName, out int packAdded, out int packSkipped, out string packError))
                {
                    OpenSpellbook();
                    if (SbStatus != null)
                    {
                        SbStatus.Text = packAdded == 0
                            ? $"Новых заклинаний нет — все {packSkipped} уже в справочнике"
                            : $"Из пака добавлено: {packAdded}, пропущено: {packSkipped}";
                    }
                    ShowToast($"Справочник обновлён: добавлено {packAdded}", "✓", B("B.Ok"));
                    return;
                }

                LoadSpellsFromDb();
                ApplyView();

                var lines = new List<string>
                {
                    $"Свои заклинания: добавлено {result.SpellsAdded}, обновлено {result.SpellsUpdated}",
                    $"Свои предметы: добавлено {result.ItemsAdded}, обновлено {result.ItemsUpdated}"
                };
                if (result.ImagesRestored > 0) lines.Add($"Картинок восстановлено: {result.ImagesRestored}");
                if (result.SpellsForCharacter.Count > 0)
                {
                    lines.Add("");
                    lines.Add("Новые заклинания уже в справочнике — добавляйте себе кнопкой «+»:");
                    foreach (var name in result.SpellsForCharacter.Take(10)) lines.Add("• " + name);
                    if (result.SpellsForCharacter.Count > 10)
                        lines.Add($"…и ещё {result.SpellsForCharacter.Count - 10}");
                }

                ShowToast($"Загружено записей: {result.Total}", "✓", B("B.Ok"));
                if (Window.GetWindow(this) is Window host)
                    MessageDialog.Info(host, string.Join("\n", lines), "Загрузка завершена");
            }
            catch (Exception ex)
            {
                Log("ShareImport", ex);
                ShowToast("Ошибка загрузки: " + ex.Message, "!", B("B.Danger"));
            }
        }

        #endregion

        #region === ДИАЛОГ ПОДТВЕРЖДЕНИЯ ===
        private Grid _confirmOverlay;
        private Action<bool>? _confirmCallback;

        /// <summary>
        /// Показывает кастомный диалог подтверждения поверх текущего View.
        /// Возвращает результат через callback (true = подтверждено).
        /// </summary>
        private void ShowConfirmDialog(string title, string message, string yesText, string noText, Action<bool> callback)
        {
            _confirmCallback = callback;

            // Фон-затемнение
            var bg = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x05, 0x07, 0x0E)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            bg.MouseLeftButtonDown += (s, e) => CloseConfirmDialog(false);

            // Панель диалога
            var panel = new Border
            {
                Width = 420,
                Background = (Brush)FindResource("B.Panel"),
                BorderBrush = (Brush)FindResource("B.AccentEdge"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(24, 24, 24, 24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 30,
                    ShadowDepth = 0,
                    Opacity = 0.7
                }
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Заголовок + крестик
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = title.ToUpper(),
                FontFamily = (FontFamily)FindResource("F.Display"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("B.Accent"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);
            headerGrid.Children.Add(titleText);

            var closeBtn = new Button
            {
                Content = new Helpers.IconView { Kind = "close", IconSize = 14, IconBrush = B("B.Mute") },
                Width = 28,
                Height = 28,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource("B.Mute"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.Click += (s, e) => CloseConfirmDialog(false);
            closeBtn.MouseEnter += (s, e) => closeBtn.Foreground = (Brush)FindResource("B.Accent");
            closeBtn.MouseLeave += (s, e) => closeBtn.Foreground = (Brush)FindResource("B.Mute");
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);

            Grid.SetRow(headerGrid, 0);
            Grid.SetColumnSpan(headerGrid, 2);
            grid.Children.Add(headerGrid);

            // Разделитель
            var divider = new Border
            {
                Height = 1,
                Background = (Brush)FindResource("B.Line"),
                Margin = new Thickness(0, 12, 0, 12)
            };
            Grid.SetRow(divider, 1);
            grid.Children.Add(divider);

            // Иконка + сообщение
            var msgPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 16) };
            var iconText = new TextBlock
            {
                Text = "✦",
                FontSize = 28,
                Foreground = (Brush)FindResource("B.Arcane"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            msgPanel.Children.Add(iconText);

            var msgText = new TextBlock
            {
                Text = message,
                FontFamily = (FontFamily)FindResource("F.Body"),
                FontSize = 13,
                Foreground = (Brush)FindResource("B.Ink"),
                TextWrapping = TextWrapping.Wrap,  // ← ДОБАВЛЕНО: перенос текста
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 320  // ← ДОБАВЛЕНО: ограничение ширины для переноса
            };
            msgPanel.Children.Add(msgText);


            Grid.SetRow(msgPanel, 2);
            grid.Children.Add(msgPanel);

            // Кнопки
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var btnNo = new Button
            {
                Content = noText,
                Padding = new Thickness(16, 8, 16, 8),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                Foreground = (Brush)FindResource("B.Mute"),
                FontFamily = (FontFamily)FindResource("F.Body"),
                FontSize = 12
            };
            btnNo.MouseEnter += (s, e) => { btnNo.Background = (Brush)FindResource("B.PanelHi"); btnNo.Foreground = (Brush)FindResource("B.Accent"); };
            btnNo.MouseLeave += (s, e) => { btnNo.Background = Brushes.Transparent; btnNo.Foreground = (Brush)FindResource("B.Mute"); };
            btnNo.Click += (s, e) => CloseConfirmDialog(false);

            var btnYes = new Button
            {
                Content = yesText,
                Padding = new Thickness(20, 8, 20, 8),
                Cursor = Cursors.Hand,
                Background = (Brush)FindResource("B.Danger"),
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                FontFamily = (FontFamily)FindResource("F.Body"),
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 0)
            };
            btnYes.MouseEnter += (s, e) => btnYes.Opacity = 0.85;
            btnYes.MouseLeave += (s, e) => btnYes.Opacity = 1.0;
            btnYes.Click += (s, e) => CloseConfirmDialog(true);

            // Пустую кнопку не добавляем: раньше при вызове без текста «нет»
            // в диалоге оставался пустой красный квадрат.
            if (!string.IsNullOrWhiteSpace(noText)) btnPanel.Children.Add(btnNo);
            btnPanel.Children.Add(btnYes);

            // Если второй кнопки нет, единственная не должна отстоять от края
            if (string.IsNullOrWhiteSpace(noText)) btnYes.Margin = new Thickness(0);

            Grid.SetRow(btnPanel, 3);
            grid.Children.Add(btnPanel);

            panel.Child = grid;

            // Оверлей
            _confirmOverlay = new Grid
            {
                Visibility = Visibility.Visible,
                Opacity = 0
            };
            Panel.SetZIndex(_confirmOverlay, 250);
            _confirmOverlay.Children.Add(bg);
            _confirmOverlay.Children.Add(panel);

            // Добавляем в корень UserControl
            if (this.Content is Grid rootGrid)
            {
                rootGrid.Children.Add(_confirmOverlay);
            }

            // Анимация появления
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(180));
            _confirmOverlay.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void CloseConfirmDialog(bool result)
        {
            if (_confirmOverlay == null) return;

            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s, e) =>
            {
                if (_confirmOverlay != null && _confirmOverlay.Parent is Grid parent)
                {
                    parent.Children.Remove(_confirmOverlay);
                }
                _confirmOverlay = null;
                _confirmCallback?.Invoke(result);
                _confirmCallback = null;
            };
            _confirmOverlay.BeginAnimation(OpacityProperty, fadeOut);
        }
        #endregion

    }
}
