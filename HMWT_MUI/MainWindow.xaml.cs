using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HMWT_MUI.Services;
using HMWT_MUI.Windows;
using HMWT_MUI.Net;
using HMWT_MUI.Helpers;

namespace HMWT_MUI
{
    /// <summary>
    /// Логика взаимодействия для MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _isCharacterFormOpen = false;

        /// <summary>
        /// Окно уже закрыто. После закрытия WPF запрещает Show и Hide, а во время
        /// выхода или перезапуска приложение закрывает окна до того, как вернётся
        /// управление из открытого листа персонажа.
        /// </summary>
        private bool _windowClosed = false;
        private ContextMenu _settingsContextMenu;
        private readonly Dictionary<Border, string> _cardSearchKeys = new Dictionary<Border, string>();

        public MainWindow()
        {
            InitializeComponent();
            DatabaseHelper.Initialize();
            PhotoHelper.Initialize();
            SetupSettingsMenu();

            // Поиск не был подключен в XAML — вешаем здесь
            SearchBox.TextChanged += SearchBox_TextChanged;

            // Масштаб интерфейса: на ноутбуке всё крупно, на большом мониторе мелко
            InitializeUiScale();

            var loadWatch = Stopwatch.StartNew();
            LoadCharacters();
            DatabaseHelper.ReportIfSlow("старт: построение карточек персонажей", loadWatch.ElapsedMilliseconds);

            // ── Аватар могли сменить в другом окне (в том числе в комнате) ──
            // Подписка на событие, а не проверка по таймеру: карточка обновляется
            // ровно тогда, когда фото действительно поменяли
            PhotoHelper.CharacterPhotoChanged += MainWindow_PhotoChanged;

            // ── Отряд: состояние в шапке и обновление панели ──
            RoomSession.Instance.StateChanged += Room_StateChanged;
            UpdateRoomIndicator();
            Closed += (_, __) =>
            {
                _windowClosed = true;
                RoomSession.Instance.StateChanged -= Room_StateChanged;
                PhotoHelper.CharacterPhotoChanged -= MainWindow_PhotoChanged;
            };

            ShowVersionFooter();

            // Запуск закончен: окно собрано, курсор «занято» из App.OnStartup
            // больше не нужен. Здесь же в журнал попадает полное время запуска —
            // но только если оно подозрительно большое.
            Mouse.OverrideCursor = null;
            DatabaseHelper.ReportIfSlow("старт: до конца сборки главного окна", App.StartupWatch.ElapsedMilliseconds);
        }

        /// <summary>
        /// Сменился аватар персонажа: перестраиваем карточки, сохраняя поиск.
        ///
        /// Событие может прийти из другого потока (сеть), поэтому переключаемся
        /// в поток интерфейса.
        /// </summary>
        private void MainWindow_PhotoChanged(int characterId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => MainWindow_PhotoChanged(characterId)));
                return;
            }

            string query = SearchBox?.Text ?? "";
            LoadCharacters();
            if (!string.IsNullOrEmpty(query)) SearchBox_TextChanged(SearchBox, null);
        }

        /// <summary>
        /// Версия сборки в подвале главного окна.
        ///
        /// При проверке сборок важно сразу видеть, что у всех одинаковая версия,
        /// и что запущен именно свежий файл, а не вчерашний: поэтому рядом с
        /// версией из кода показываем версию самого файла и дату сборки.
        /// </summary>
        private void ShowVersionFooter()
        {
            try
            {
                string version = Net.NetVersions.AppVersion;

                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string fileVersion = assembly.GetName().Version?.ToString() ?? "?";
                DateTime built = System.IO.File.GetLastWriteTime(assembly.Location);

                if (txtAppVersion != null) txtAppVersion.Text = "версия " + version;

                if (txtAppVersionHint != null)
                {
                    txtAppVersionHint.Text = string.Equals(fileVersion, version, StringComparison.Ordinal)
                        ? "файл совпадает"
                        : $"в файле {fileVersion} — сборка устарела!";
                    txtAppVersionHint.Foreground = string.Equals(fileVersion, version, StringComparison.Ordinal)
                        ? (Brush)FindResource("B.Mute")
                        : (Brush)FindResource("B.Danger");
                }

                if (txtBuildStamp != null)
                    txtBuildStamp.Text = "сборка от " + built.ToString("dd.MM.yyyy HH:mm") +
                                         " · протокол комнаты " + Net.NetVersions.ProtocolVersion;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] версия в подвале: {ex.Message}");
            }
        }

        #region === ЗАГРУЗКА И ПОИСК ПЕРСОНАЖЕЙ ===

        private void LoadCharacters()
        {
            CharacterPanel.Children.Clear();
            _cardSearchKeys.Clear();
            try
            {
                DataTable dt = DatabaseHelper.GetAllCharacters();
                foreach (DataRow row in dt.Rows)
                {
                    CreateCharacterCard(
                        GetInt(row, "id_character", 0),
                        GetStr(row, "name", "Без имени"),
                        GetStr(row, "class", "Воин"),
                        GetStr(row, "race", ""),
                        GetInt(row, "Level", 1),
                        GetInt(row, "HP", 0),
                        GetStr(row, "PhotoPath", ""));
                }
            }
            catch (Exception ex)
            {
                MessageDialog.Error(this, $"Ошибка загрузки: {ex.Message}");
            }
            UpdateCharCountAndEmpty();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
            int visible = 0;
            foreach (var child in CharacterPanel.Children)
                if (child is Border b)
                {
                    bool match = string.IsNullOrEmpty(q) || (_cardSearchKeys.TryGetValue(b, out string key) && key.Contains(q));
                    b.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (match) visible++;
                }
            UpdateCharCountAndEmpty(visible);
        }

        private void UpdateCharCountAndEmpty(int? visibleOverride = null)
        {
            int total = 0, visible = 0;
            foreach (var child in CharacterPanel.Children)
                if (child is Border b) { total++; if (b.Visibility != Visibility.Collapsed) visible++; }
            if (TxtCharCount != null) TxtCharCount.Text = total == 0 ? "" : $"персонажей: {total}";
            if (EmptyState != null)
            {
                int shown = visibleOverride ?? visible;
                bool noMatch = total > 0 && shown == 0;
                EmptyState.Visibility = (total == 0 || noMatch) ? Visibility.Visible : Visibility.Collapsed;
                if (EmptyStateText != null) EmptyStateText.Text = noMatch ? "Никого не найден" : "Пока нет персонажей";
            }
        }

        #endregion

        #region === СОЗДАНИЕ / УДАЛЕНИЕ / ОТКРЫТИЕ ЛИСТА ===

        private void AddCharacterBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DatabaseHelper.CreateCharacter(
                    name: $"Персонаж {DateTime.Now:HHmmss}",
                    charClass: "Воин",
                    race: "Человек",
                    hp: 10,
                    speed: 30,
                    photoPath: "");
                LoadCharacters();
            }
            catch (Exception ex)
            {
                MessageDialog.Error(this, $"Ошибка создания: {ex.Message}");
            }
        }

        private void DeleteCharacter(int id, string name, Border card)
        {
            if (!MessageDialog.Confirm(this, $"Удалить персонажа \"{name}\"? Действие необратимо.",
                "Подтверждение", "Удалить")) return;
            try
            {
                // Удаляем фото из AppData (если есть)
                DataRow row = DatabaseHelper.GetCharacterById(id);
                if (row != null)
                {
                    string photoPath = row["PhotoPath"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(photoPath))
                    {
                        string fullPath = PhotoHelper.ResolvePhotoPath(photoPath);
                        if (fullPath != null && File.Exists(fullPath)) File.Delete(fullPath);
                    }
                }
                DatabaseHelper.DeleteCharacter(id);
                CharacterPanel.Children.Remove(card);
                LoadCharacters();
            }
            catch (Exception ex)
            {
                MessageDialog.Error(this, $"Ошибка удаления: {ex.Message}");
            }
        }

        /// <summary>
        /// Открывает лист персонажа. MainWindow прячется на время листа
        /// и возвращается + обновляется после закрытия (правило дизайн-доки).
        /// </summary>
        private void OpenCharacterForm(int id)
        {
            if (_isCharacterFormOpen) return;
            _isCharacterFormOpen = true;
            try
            {
                // Окно уже закрыто (выход или перезапуск приложения): собирать
                // лист персонажа некуда, показывать его тоже
                if (_windowClosed) return;
                // Окно листа собирается целиком в конструкторе, и это самая
                // долгая часть открытия. Пока оно собирается, главное окно
                // остаётся на экране и показывает курсор «занято»: иначе
                // пользователь видел пустой рабочий стол и решал, что
                // программа зависла.
                Mouse.OverrideCursor = Cursors.Wait;
                var buildWatch = Stopwatch.StartNew();
                CharacterInfoWindow charWindow;
                try
                {
                    charWindow = new CharacterInfoWindow(id);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }
                DatabaseHelper.ReportIfSlow($"открытие листа персонажа id={id}: сборка окна", buildWatch.ElapsedMilliseconds);

                // LoadCharacter здесь больше не зовём: конструктор окна уже
                // загрузил данные, а повторный вызов — это второй полный
                // проход по базе на каждое открытие листа
                if (!_windowClosed)
                {
                    this.Hide();
                    charWindow.ShowDialog();
                }
            }
            finally
            {
                // Пока лист персонажа был открыт, приложение могли закрыть или
                // перезапустить: тогда это окно уже закрыто и Show() бросает
                // исключение. Обновлять список в этом случае тоже незачем.
                if (!_windowClosed)
                {
                    this.Show();
                    this.Activate();
                    LoadCharacters();   // аватар/уровень/HP могли измениться
                }

                _isCharacterFormOpen = false;
            }
        }

        #endregion

        #region === КАРТОЧКА ПЕРСОНАЖА ===

        private void CreateCharacterCard(int id, string name, string charClass, string race, int level, int hp, string photoPath)
        {
            var card = new Border { Tag = id, Cursor = Cursors.Hand };
            ApplyCharacterCardVisuals(card, name, charClass, race, level, hp, photoPath);
            card.Width = 251;
            card.Height = 313;
            card.Clip = new RectangleGeometry(new Rect(0, 0, 251, 313), 12, 12);
            // круглый крестик удаления (правый низ фото, по наведению)
            var delBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 8, 8),   // было (0,0,8,70) — из-за этого уезжал в центр
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Удалить персонажа",
                Visibility = Visibility.Collapsed
            };
            var delBox = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = (Brush)FindResource("B.Danger") };
            delBox.Child = new Helpers.IconView { Kind = "close", IconSize = 12, IconBrush = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            delBtn.Content = delBox;
            delBtn.Click += (s, e) => { e.Handled = true; DeleteCharacter(id, name, card); };
            delBtn.MouseLeftButtonUp += (s, e) => e.Handled = true;
            if (card.Child is Grid g)
            {
                Grid.SetRow(delBtn, 0);   // явно в ряд фото
                g.Children.Add(delBtn);
            }
            card.MouseEnter += (s, e) => delBtn.Visibility = Visibility.Visible;
            card.MouseLeave += (s, e) => delBtn.Visibility = Visibility.Collapsed;
            card.MouseLeftButtonUp += (s, e) => OpenCharacterForm(id);

            _cardSearchKeys[card] = ((name ?? "") + " " + (charClass ?? "") + " " + (race ?? "")).ToLowerInvariant();
            CharacterPanel.Children.Add(card);
        }

        private void ApplyCharacterCardVisuals(Border card, string name, string cls, string race, int level, int hp, string photoPath)
        {
            card.SetResourceReference(StyleProperty, "CharacterCardStyle");
            Color accent = ClassAccentColor(cls);

            var grid = new Grid { ClipToBounds = true };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ── фото-зона ──
            var photoBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x38)),
                ClipToBounds = true
            };
            var photoGrid = new Grid { ClipToBounds = true };
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var bmp = LoadCharImage(photoPath);
            img.Source = bmp;
            var placeholder = new Border { Visibility = bmp == null ? Visibility.Visible : Visibility.Collapsed };
            var phGrid = new Grid();
            var (c1, c2) = CharPlaceholderColors(name);
            placeholder.Background = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
            phGrid.Children.Add(new TextBlock { Text = "✶", FontFamily = (FontFamily)FindResource("F.Display"), FontSize = 70, Foreground = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            phGrid.Children.Add(new TextBlock { Text = CharFirstLatin(name), FontFamily = (FontFamily)FindResource("F.Display"), FontSize = 84, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            placeholder.Child = phGrid;
            photoGrid.Children.Add(img);
            photoGrid.Children.Add(placeholder);
            photoBox.Child = photoGrid;
            Grid.SetRow(photoBox, 0);
            grid.Children.Add(photoBox);

            // ── бейдж HP (слева сверху) ──
            var hpBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x09, 0x0C, 0x16)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                ToolTip = $"Хиты: {hp}"
            };
            var hpStack = new StackPanel { Orientation = Orientation.Horizontal };
            hpStack.Children.Add(new Helpers.IconView { Kind = "heart", IconSize = 13, IconBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            hpStack.Children.Add(new TextBlock { Text = hp.ToString(), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("B.Ink"), VerticalAlignment = VerticalAlignment.Center });
            hpBadge.Child = hpStack;
            grid.Children.Add(hpBadge);

            // ── бейдж уровня (справа сверху) ──
            var lvlBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Background = (Brush)FindResource("B.Accent"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                ToolTip = $"Уровень {level}"
            };
            lvlBadge.Child = new TextBlock { Text = $"Ур. {level}", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = PaletteBrush.Get("B.OnAccent", Color.FromRgb(0x04, 0x22, 0x2A)) };
            grid.Children.Add(lvlBadge);

            // ── подпись: имя + класс•раса ──
            var info = new Border { Background = (Brush)FindResource("B.PanelHi"), Padding = new Thickness(12, 10, 12, 10) };
            Grid.SetRow(info, 1);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(name) ? "Безымянный" : name,
                FontFamily = (FontFamily)FindResource("F.Display"),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("B.Ink"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            stack.Children.Add(nameText);
            var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            metaRow.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = string.IsNullOrWhiteSpace(cls) ? "" : cls });
            string meta = string.IsNullOrWhiteSpace(cls) ? "—" : cls;
            if (!string.IsNullOrWhiteSpace(race)) meta += $" • {race}";
            var metaText = new TextBlock
            {
                Text = meta,
                FontFamily = (FontFamily)FindResource("F.Body"),
                FontSize = 14,
                Foreground = (Brush)FindResource("B.Mute"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            metaRow.Children.Add(metaText);
            stack.Children.Add(metaRow);
            info.Child = stack;
            grid.Children.Add(info);
            card.Child = grid;
        }

        #endregion

        #region === ФОТО (только AppData, без legacy из bin) ===

        private BitmapImage LoadCharImage(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            try
            {
                // Единая точка разрешения путей: AppData/HMWT (+ абсолютные пути)
                string full = PhotoHelper.ResolvePhotoPath(relativePath);
                if (full == null || !File.Exists(full)) return null;

                // Файл читаем в память и разбираем сразу в размере плитки.
                //
                // Раньше к адресу дописывалась метка времени, чтобы обойти кэш
                // WPF: «file:///...jpg?t=...». Такой адрес WPF не открывает —
                // «Illegal characters in path», и карточка показывала не фото,
                // а букву-заглушку. Чтение через поток решает обе задачи: файл
                // не занят (его можно заменить) и картинка всегда свежая, потому
                // что берётся из файла в момент показа.
                return PhotoHelper.LoadUnlocked(full, 400);
            }
            catch { return null; }
        }

        #endregion

        #region === ХЕЛПЕРЫ ВИДА КАРТОЧКИ ===

        private static Color ClassAccentColor(string cls)
        {
            if (string.IsNullOrWhiteSpace(cls)) return Color.FromRgb(0xA9, 0x8B, 0xEC);
            string s = cls.Trim().ToLowerInvariant();
            var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                {"воин",Color.FromRgb(0x8A,0x93,0xA6)}, {"fighter",Color.FromRgb(0x8A,0x93,0xA6)},
                {"паладин",Color.FromRgb(0x5F,0xD4,0xDD)}, {"paladin",Color.FromRgb(0x5F,0xD4,0xDD)},
                {"варвар",Color.FromRgb(0xC0,0x56,0x4F)}, {"barbarian",Color.FromRgb(0xC0,0x56,0x4F)},
                {"бард",Color.FromRgb(0xC0,0x56,0x8F)}, {"bard",Color.FromRgb(0xC0,0x56,0x8F)},
                {"жрец",Color.FromRgb(0xD8,0xD2,0xC0)}, {"cleric",Color.FromRgb(0xD8,0xD2,0xC0)},
                {"друид",Color.FromRgb(0x5B,0xB9,0x74)}, {"druid",Color.FromRgb(0x5B,0xB9,0x74)},
                {"монах",Color.FromRgb(0xB0,0x9A,0x6A)}, {"monk",Color.FromRgb(0xB0,0x9A,0x6A)},
                {"плут",Color.FromRgb(0x9A,0x7B,0xD6)}, {"rogue",Color.FromRgb(0x9A,0x7B,0xD6)},
                {"рейнджер",Color.FromRgb(0x4F,0xB6,0xAC)}, {"ranger",Color.FromRgb(0x4F,0xB6,0xAC)},
                {"чародей",Color.FromRgb(0xC0,0x8A,0x3E)}, {"sorcerer",Color.FromRgb(0xC0,0x8A,0x3E)},
                {"колдун",Color.FromRgb(0x7A,0x3B,0x3B)}, {"warlock",Color.FromRgb(0x7A,0x3B,0x3B)},
                {"волшебник",Color.FromRgb(0x4F,0x7B,0xC0)}, {"wizard",Color.FromRgb(0x4F,0x7B,0xC0)},
            };
            foreach (var kv in map) if (s.Contains(kv.Key)) return kv.Value;
            return Color.FromRgb(0xA9, 0x8B, 0xEC);
        }

        private static string GetStr(DataRow r, string col, string fb)
        {
            try { return r.Table.Columns.Contains(col) && !r.IsNull(col) ? (r[col]?.ToString() ?? fb) : fb; }
            catch { return fb; }
        }

        private static int GetInt(DataRow r, string col, int fb)
        {
            try { return r.Table.Columns.Contains(col) && !r.IsNull(col) ? Convert.ToInt32(r[col]) : fb; }
            catch { return fb; }
        }

        private static readonly Color[] CharPalette = new[]
        {
            Color.FromRgb(0x4F,0x7B,0xC0), Color.FromRgb(0x9A,0x7B,0xD6), Color.FromRgb(0x4F,0xB6,0xAC),
            Color.FromRgb(0xC0,0x8A,0x3E), Color.FromRgb(0xC0,0x56,0x8F), Color.FromRgb(0x7A,0x3B,0x3B),
            Color.FromRgb(0x8A,0x9A,0x3E), Color.FromRgb(0x5F,0xD4,0xDD),
        };

        private static (Color, Color) CharPlaceholderColors(string name)
        {
            int h = (name ?? "").ToLowerInvariant().GetHashCode();
            int i = Math.Abs(h) % CharPalette.Length;
            int j = (i + 1 + (Math.Abs(h >> 3) % (CharPalette.Length - 1))) % CharPalette.Length;
            return (Dim(CharPalette[i]), Dim(CharPalette[j]));
        }

        private static Color Dim(Color c) => Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));

        private static string CharFirstLatin(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "✶";
            char ch = name.TrimStart()[0];
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) return char.ToUpperInvariant(ch).ToString();
            var tr = new Dictionary<char, string>
            {
                {'А',"A"},{'Б',"B"},{'В',"V"},{'Г',"G"},{'Д',"D"},{'Е',"E"},{'Ё',"E"},{'Ж',"Z"},{'З',"Z"},
                {'И',"I"},{'Й',"I"},{'К',"K"},{'Л',"L"},{'М',"M"},{'Н',"N"},{'О',"O"},{'П',"P"},{'Р',"R"},
                {'С',"S"},{'Т',"T"},{'У',"U"},{'Ф',"F"},{'Х',"H"},{'Ц',"C"},{'Ч',"C"},{'Ш',"S"},{'Щ',"S"},
                {'Ъ',""},{'Ы',"Y"},{'Ь',""},{'Э',"E"},{'Ю',"U"},{'Я',"Y"}
            };
            return tr.TryGetValue(ch, out string lat) && lat.Length > 0 ? lat : "✶";
        }

        #endregion

        #region === НАСТРОЙКИ / ЗАГЛУШКИ ===

        private void SetupSettingsMenu()
        {
            // Настройки живут во вкладке окна персонажа: отдельное окно не могло
            // использовать стили главного и падало при разборе разметки.
            // Здесь остаётся только справка о программе.
            _settingsContextMenu = new ContextMenu();

            var aboutItem = new MenuItem { Header = "О программе" };
            aboutItem.Click += (s, e) => MessageDialog.Info(this,
                "Редактор персонажа и инструменты ведущего для настольных ролевых игр.\n\n" +
                "Имена, тексты и описания из книг принадлежат их издателям. " +
                "Свой справочник можно загрузить отдельным файлом.\n\n" +
                "Настройки масштаба и анимации — во вкладке настроек внутри персонажа.",
                "HMWT Гримуар");
            _settingsContextMenu.Items.Add(aboutItem);
        }

        
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _settingsContextMenu.IsOpen = true;
        }

        private void DmToolsButton_Click(object sender, RoutedEventArgs e)
        {
            // Инструменты ДМа живут в окне персонажа: трекер инициативы и
            // бестиарий привязаны к тому, кто их открыл. Если персонаж уже
            // открыт — просто переходим на нужную вкладку.
            var character = Application.Current?.Windows
                .OfType<CharacterInfoWindow>()
                .FirstOrDefault();

            if (character != null && DmRights.CanUseDmTools())
            {
                if (character.WindowState == WindowState.Minimized)
                    character.WindowState = WindowState.Normal;

                character.Activate();
                character.OpenDmToolsTab();
                return;
            }

            MessageDialog.Info(this,
                character == null
                    ? "Инструменты ДМа открываются из окна персонажа: значок инструментов ДМа в левой рейке " +
                      "или кнопка «Инициатива» в подвале.\n\nУже работает:\n" +
                      "• трекер инициативы (группы боя, жетоны, ходы, раунды)\n" +
                      "• бестиарий: свои существа со всеми полями карточки и живыми формулами атак\n\n" +
                      "Планируется:\n" +
                      "• экран ДМа (заметки и сюжет вне персонажей)\n" +
                      "• журнал бросков всех игроков\n" +
                      "• генераторы: имён, лута, встреч, погоды\n" +
                      "• быстрые таблицы (состояния, DC, сложность)"
                    : DmRights.DenyReason(),
                character == null ? "Инструменты ДМа" : "Инструменты ДМа — только у ведущего");
        }

        #endregion
    }
}