using HMWT_MUI.Net;
using HMWT_MUI.Helpers;
using HMWT_MUI.Services;
using HMWT_MUI.Views;
using HMWT_MUI.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace HMWT_MUI
{
    public partial class CharacterInfoWindow : Window, Helpers.IDmRollHost
    {
        private int _characterId;
        private Random _random = new Random();
        private int _tempHP = 0;
        private int _currentHp = 0; // Кэш текущего HP
        private int _maxHp = 0;     // Кэш максимального HP
        private CharacterInfoView _currentInfoView;

        // Инструменты ДМа: окошко инициативы из подвала. Создаётся при первом
        // открытии — иначе каждый заход в персонажа собирал бы трекер зря.
        private Views.InitiativePanel _initiativePanel;

        // HP и Стабилизация
        private int _stabSuccesses = 0;
        private int _stabFailures = 0;
        private string _hpInputValue = "0";

        // Калькулятор Золота
        private MoneyService _moneyService;
        private CharacterMoney _currentMoney;
        private string _selectedCurrency = "Gold";
        private string _calcDisplayValue = "0";
        private bool _isCalcOpen = false;
        private bool _goldExpanded = false;

        // Dice Roller
        private Services.DiceRoller _diceRoller;
        private Services.DiceType _selectedDiceType = Services.DiceType.D20;
        private int _diceCount = 1;
        private int _diceModifier = 0;
        private List<Services.DiceRollResult> _rollHistory = new List<Services.DiceRollResult>();
        private const int MAX_VISIBLE_RESULTS = 5;
        // Контекст ожидающего броска (что именно будем бросать)
        private string _pendingRollType = "Бросок";     // Проверка / Спасбросок / Навык / Атака / Атака заклинанием (в будущем)
        private string _pendingRollLabel = "";          // СИЛА / Атлетика / имя заклинания...

        /// <summary>Защита от рекурсии при синхронизации поля формулы с полями количества/модификатора.</summary>
        private bool _syncingDiceFormula;

        // Аватар
        private string _currentPhotoPath;
        private System.Windows.Threading.DispatcherTimer _avatarClickTimer;
        private int _avatarClickCount = 0;

        // Тосты
        private System.Windows.Threading.DispatcherTimer _toastTimer;

        // Ручное редактирование уровня
        private bool _isManualEditMode = false;
        private int _tempLevel = 1;
        private int _tempExp = 0;
        private int _tempProficiency = 2;
        private double _levelProgressPercent;

        // Состояние персонажа в шапке: состояния, вдохновение, концентрация
        private List<CharacterStateService.ConditionOption> _conditionOptions =
            new List<CharacterStateService.ConditionOption>();
        private bool _hasInspiration;
        private string _concentrationSpell = "";

        // Отдых: какой именно открыт, сколько костей хитов тратится и не подведён ли уже итог
        private RestService.RestKind _restKind = RestService.RestKind.Long;
        private int _restDiceToSpend;
        private bool _restApplied;

        public CharacterInfoWindow(int characterId)
        {            InitializeComponent();
            System.Windows.Media.TextOptions.SetTextFormattingMode(this,
                System.Windows.Media.TextFormattingMode.Display);
            System.Windows.Media.TextOptions.SetTextRenderingMode(this,
                System.Windows.Media.TextRenderingMode.ClearType);

            // Масштаб интерфейса: применяется ко всем окнам одинаково
            Services.UiScaleManager.Register(this);
            _characterId = characterId;
            PhotoHelper.MigrateLegacyPhotos();
            Loaded += (_, __) => ApplyAdaptiveLayout();
            SizeChanged += (_, __) => ApplyAdaptiveLayout();

            // Подписываем ТОЛЬКО то, чего нет в XAML
            btnCloseHPCalc.Click += (s, e) => HPCalcOverlay.Visibility = Visibility.Collapsed;
            btnCloseLevelManager.Click += (s, e) => LevelManagerOverlay.Visibility = Visibility.Collapsed;

            // Оверлеи этого окна (калькулятор ХП, менеджер уровней, портрет,
            // всплывающие подсказки) лежат в том же окне, что и карта, поэтому
            // WebView2 их перекрывает. Пока оверлей открыт — прячем карту.
            // оверлеи прячут карту через UpdateChatOcclusion (см. ниже)
            btnHPHeal.Click += BtnHPHeal_Click;
            btnHPDamage.Click += BtnHPDamage_Click;
            btnHPRoll.Click += BtnHPRoll_Click;

            // Веб-вью чата — отдельное HWND-окно и всегда рисуется поверх WPF.
            // Пока открыт любой оверлей, прячем его, иначе он торчит над затемнением.
            HookChatOcclusion();

            // Размещение вкладок выставляем ДО первого SwitchView: он красит
            // активную вкладку, и стиль должен быть уже выбран под нужный вариант
            ApplyTabPlacement();

            SwitchView("Info");

#if DEBUG
            // Отладка: HMWT_DEBUG_VIEW=Map открывает нужную вкладку сразу.
            // Нужно для проверок интерфейса без кликов; в релизе кода нет.
            try
            {
                string debugView = Environment.GetEnvironmentVariable("HMWT_DEBUG_VIEW");
                if (!string.IsNullOrWhiteSpace(debugView)) SwitchView(debugView.Trim());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CharacterInfoWindow] отладка вкладки: {ex.Message}"); }
#endif

            _diceRoller = new Services.DiceRoller();
            _diceRoller.OnDiceRolled += DiceRoller_OnDiceRolled;

            LoadCharacter(characterId);

            // ── Комната: показываем состояние и отдаём отряду данные персонажа ──
            RoomSession.Instance.StateChanged += RoomSession_StateChanged;

            // ── Аватар: фото могли сменить в другом окне или прислать по сети ──
            // Своё фото в этом окне обновляем сами: без подписки в листе персонажа
            // оставалась прежняя картинка, пока окно не закроют и не откроют заново
            PhotoHelper.CharacterPhotoChanged += CharacterInfoWindow_PhotoChanged;

            // Комната будет спрашивать данные именно этого персонажа
            RoomSession.MyCharacterProvider = ProvideMyRoomPlayer;

            UpdateRoomIndicator();
            Loaded += (_, __) =>
            {
                UpdateRoomIndicator();
                // Права на инструменты ДМа зависят от роли в отряде
                UpdateDmToolsVisibility();
                UpdateInitiativeIndicator();
                PublishCharacterToRoom();
            };
            Closed += (_, __) =>
            {
                // Деньги пишем и при закрытии окна: калькулятор мог остаться
                // открытым, а правки в нём — несохранёнными
                try { if (_currentMoney != null) _moneyService?.SaveMoney(_currentMoney); } catch { }

                // Тот же случай со списком состояний: он мог быть раскрыт
                SaveConditionsFromUi();

                RoomSession.Instance.StateChanged -= RoomSession_StateChanged;
                PhotoHelper.CharacterPhotoChanged -= CharacterInfoWindow_PhotoChanged;
                // Поставщик указывает на закрытое окно: снимаем, иначе комната
                // будет обращаться к уже не существующему персонажу
                if (ReferenceEquals(RoomSession.MyCharacterProvider, (Func<PlayerInfo>)ProvideMyRoomPlayer))
                    RoomSession.MyCharacterProvider = null;
            };
        }

        /// <summary>
        /// У персонажа сменился аватар. Перечитываем путь из базы и показываем
        /// картинку заново: имя файла у аватара не меняется, поэтому по одному
        /// лишь пути новую картинку не отличить от прежней.
        /// </summary>
        private void CharacterInfoWindow_PhotoChanged(int characterId)
        {
            if (characterId != _characterId) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => CharacterInfoWindow_PhotoChanged(characterId)));
                return;
            }

            try
            {
                string path = DatabaseHelper.GetCharacterPhoto(_characterId);
                _currentPhotoPath = path;

                if (string.IsNullOrWhiteSpace(path))
                {
                    imgCharacterPhoto.ClearImage();
                    txtAvatarPlaceholder.Visibility = Visibility.Visible;
                }
                else ShowAvatar(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterInfoWindow] обновление аватара: {ex.Message}");
            }
        }

        #region === 1. ЗАГРУЗКА ДАННЫХ ===
        public void LoadCharacter(int id)
        {
            _characterId = id;
            _moneyService = new MoneyService(_characterId);

            try
            {
                LoadCharacterHeader();
                LoadHeaderState();
                _currentMoney = _moneyService.LoadMoney();
                UpdateHeaderGoldDisplay();
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка загрузки: {ex.Message}", true);
            }
        }

        private void LoadCharacterHeader()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                // 1. Основная информация
                string queryInfo = "SELECT name, class, race, HP, Speed, ArmorClass, PhotoPath FROM CharacterInfo WHERE id_character = @id";
                using (var cmd = new SqliteCommand(queryInfo, conn))
                {
                    cmd.Parameters.AddWithValue("@id", _characterId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        txtName.Text = reader["name"]?.ToString() ?? "Безымянный";
                        txtClass.Text = reader["class"]?.ToString() ?? "Не указан";
                        txtRace.Text = reader["race"]?.ToString() ?? "Не указана";

                        _currentHp = Convert.ToInt32(reader["HP"]);
                        _maxHp = GetMaxHpFromSettings();
                        txtHP.Text = $"{_currentHp} / {_maxHp}";

                        txtSpeed.Text = reader["Speed"]?.ToString() ?? "30";
                        txtAC.Text = reader["ArmorClass"]?.ToString() ?? "10";

                        if (_currentHp <= 0) ShowStabilizationOverlay();

                        string photoPath = reader["PhotoPath"]?.ToString();
                        _currentPhotoPath = photoPath;
                        if (!string.IsNullOrEmpty(photoPath)) ShowAvatar(photoPath);
                        else txtAvatarPlaceholder.Visibility = Visibility.Visible;
                    }
                }

                // 2. Уровень и опыт
                string queryLevelData = "SELECT Level, Exp, ProficiencyBonus FROM LevelTable WHERE id_character = @id";
                using (var cmd = new SqliteCommand(queryLevelData, conn))
                {
                    cmd.Parameters.AddWithValue("@id", _characterId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        int level = Convert.ToInt32(reader["Level"]);
                        int exp = Convert.ToInt32(reader["Exp"]);
                        int proficiency = Convert.ToInt32(reader["ProficiencyBonus"]);

                        txtLevel.Text = level.ToString();
                        txtExp.Text = exp.ToString();
                        txtManagerProfBonus.Text = $"Бонус мастерства: +{proficiency}";
                        txtHeaderProfBonus.Text = $"+{proficiency}";

                        int expForNextLevel = GetExpForNextLevel(level);
                        int displayExp = Math.Min(exp, expForNextLevel);
                        txtExpCurrent.Text = $"Опыт: {displayExp} / {expForNextLevel}";

                        btnLevelUpAvailable.Visibility = (exp >= expForNextLevel && level < 20)
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadCharacterHeader] Ошибка: {ex.Message}");
            }
        }
        #endregion

        #region === 2. АВАТАР ===
        private void BtnChangeAvatar_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif|All files (*.*)|*.*",
                Title = "Выберите аватар персонажа",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // Сохраняем с исходным расширением, иначе gif/jpg превращались в битый «.png»
                    string extension = Path.GetExtension(dialog.FileName);
                    if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                    string relativePath = PhotoHelper.SaveCharacterAvatar(_characterId, dialog.FileName, extension);

                    // Пишем общим методом: он же сообщает остальным окнам, что фото
                    // сменилось — иначе карточка в главном окне остаётся со старым
                    DatabaseHelper.UpdateCharacterPhoto(_characterId, relativePath);

                    ShowAvatar(relativePath);
                    _currentPhotoPath = relativePath;
                    ShowToast("Аватар обновлён");
                    NotifyRoomProfileChanged();
                }
                catch (Exception ex) { ShowToast($"Ошибка: {ex.Message}", true); }
            }
        }

        /// <summary>
        /// Показывает аватар персонажа.
        ///
        /// Это HoverGif: он держит в памяти только первый кадр и никогда не
        /// разбирает всю гифку заранее. Анимация включается только когда курсор
        /// наведён, поэтому открытие персонажа больше не ждёт разбора файла.
        /// </summary>
        private void ShowAvatar(string path)
        {
            if (imgCharacterPhoto == null) return;

            string full = PhotoHelper.ResolvePhotoPath(path);
            if (string.IsNullOrEmpty(full))
            {
                imgCharacterPhoto.ClearImage();
                txtAvatarPlaceholder.Visibility = Visibility.Visible;
                return;
            }

            imgCharacterPhoto.SetImage(full);
            txtAvatarPlaceholder.Visibility = imgCharacterPhoto.HasImage
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void UpdateImage(Image imgControl, string path)
        {
            try
            {
                string full = PhotoHelper.ResolvePhotoPath(path);
                if (full == null)
                {
                    imgControl.Source = null;
                    return;
                }

                // Остальные картинки (например, фон): статичный первый кадр
                imgControl.Source = PhotoHelper.LoadUnlocked(full);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Avatar] не удалось показать фото: {ex.Message}");
                imgControl.Source = null;
            }
        }

        private void BtnAvatar_Click(object sender, MouseButtonEventArgs e)
        {
            _avatarClickCount++;
            if (_avatarClickCount == 1)
            {
                if (_avatarClickTimer == null)
                {
                    _avatarClickTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    _avatarClickTimer.Tick += (s, args) =>
                    {
                        _avatarClickTimer.Stop();
                        _avatarClickCount = 0;
                        OpenPortrait();
                    };
                }
                _avatarClickTimer.Start();
            }
            else
            {
                _avatarClickTimer?.Stop();
                _avatarClickCount = 0;
                PortraitOverlay.Visibility = Visibility.Collapsed;
                BtnChangeAvatar_Click(sender, e);
            }
        }

        private void OpenPortrait()
        {
            // Портрета нет — не открываем пустой чёрный прямоугольник, а предлагаем выбрать фото
            if (string.IsNullOrEmpty(_currentPhotoPath))
            {
                ShowToast("Портрета пока нет — выберите изображение");
                BtnChangeAvatar_Click(this, new RoutedEventArgs());
                return;
            }

            // Картинка могла не загрузиться (файл появился позже или был занят):
            // пробуем прочитать её ещё раз, чтобы не отправлять человека
            // выбирать фото заново, когда оно уже есть
            if (!imgCharacterPhoto.HasImage)
                ShowAvatar(_currentPhotoPath);

            if (!imgCharacterPhoto.HasImage)
            {
                ShowToast("Не удалось открыть аватар — выберите изображение заново", true);
                BtnChangeAvatar_Click(this, new RoutedEventArgs());
                return;
            }

            // В портрете показываем статичный кадр: это увеличенная картинка,
            // и анимация здесь только отвлекала бы
            imgPortrait.Source = PhotoHelper.LoadUnlocked(
                HMWT_MUI.PhotoHelper.ResolvePhotoPath(_currentPhotoPath));
            PortraitOverlay.Visibility = Visibility.Visible;
        }

        private void PortraitOverlay_Close(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender)
            {
                e.Handled = true;
                PortraitOverlay.Visibility = Visibility.Collapsed;
            }
        }
        #endregion

        #region === 3. ЗДОРОВЬЕ И СТАБИЛИЗАЦИЯ ===
        private void HeaderHP_Click(object sender, MouseButtonEventArgs e)
        {
            _hpInputValue = "0";
            txtHPInput.Text = _hpInputValue;
            txtHPCurrent.Text = $"{_currentHp} / {_maxHp}";
            txtHPMax.Text = $"Максимум: {_maxHp}";

            // Поля настроек заполняем при открытии: раньше в «Максимуме хитов»
            // оставалось прежнее значение, и проверить его было не с чем
            txtMaxHPInput.Text = _maxHp.ToString();
            UpdateHitDiceDisplay();

            HPCalcOverlay.Visibility = Visibility.Visible;
            var animation = new DoubleAnimation
            {
                From = 400,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            HPCalcOverlay.RenderTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        #region === Счётчик нажатий на плитке мастерства ===

        // Нажатие на плитку мастерства: плитка вздрагивает, в углу вспыхивает
        // счётчик, а вверх улетает «+1». На ProfClickLimit счётчик гаснет
        // и счёт начинается заново.

        /// <summary>Сколько нажатий терпит счётчик до обнуления.</summary>
        private const int ProfClickLimit = 9999;

        /// <summary>Вспышка в момент нажатия.</summary>
        private static readonly Color ProfFlashColor = Color.FromRgb(0xFF, 0xF4, 0xD2);

        /// <summary>Золото на «круглых» числах и на последнем нажатии.</summary>
        private static readonly Color ProfGoldColor = Color.FromRgb(0xFF, 0xBE, 0x5C);

        /// <summary>Обычный цвет счётчика — как у самого бонуса мастерства.</summary>
        private static readonly Color ProfBaseColor = Color.FromRgb(0xA9, 0x8B, 0xEC);

        private int _profClicks;
        private bool _profFinale;
        private readonly Random _profRng = new Random();
        private readonly List<TextBlock> _profPops = new List<TextBlock>();
        private int _profPopIndex;
        private DispatcherTimer _profResetTimer;
        private SolidColorBrush _profCounterBrush;
        private DropShadowEffect _profGlow;

        /// <summary>Круглое число: каждое сотое нажатие и последнее.</summary>
        private bool ProfRoundNumber => _profFinale || (_profClicks > 0 && _profClicks % 100 == 0);

        private void HeaderProfE_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            PrepareProfCounter();

            if (!_profFinale) _profClicks++;
            if (_profClicks >= ProfClickLimit) _profFinale = true;

            ShakeProfTile();
            PopProfCounter();
            ShowProfClickPop();

            if (_profFinale && _profResetTimer == null) StartProfFinale();
        }

        /// <summary>
        /// Готовит счётчик к работе: свою кисть и свечение.
        ///
        /// Кисти из словаря темы заморожены, а их цвет нужно плавно менять на
        /// каждом нажатии, поэтому здесь создаются обычные, «живые».
        /// </summary>
        private void PrepareProfCounter()
        {
            if (_profCounterBrush != null) return;

            _profCounterBrush = new SolidColorBrush(ProfBaseColor);
            txtProfClicks.Foreground = _profCounterBrush;

            _profGlow = new DropShadowEffect
            {
                Color = ProfBaseColor,
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.95
            };
            txtProfClicks.Effect = _profGlow;

            txtProfClicks.RenderTransformOrigin = new Point(0.5, 0.5);
            txtProfClicks.RenderTransform = new ScaleTransform(1, 1);
        }

        /// <summary>Короткое вздрагивание плитки: в сторону, обратно и на место.</summary>
        private void ShakeProfTile()
        {
            // На круглых числах тряхнём заметнее, чтобы нажатие чувствовалось
            double strength = 1.6 + _profRng.NextDouble() * 1.3;
            if (_profClicks > 0 && _profClicks % 100 == 0) strength *= 1.9;
            if (_profFinale) strength *= 2.2;

            int direction = _profRng.Next(2) == 0 ? 1 : -1;

            ProfTileShake.BeginAnimation(TranslateTransform.XProperty, Knock(strength * direction));
            ProfTileShake.BeginAnimation(TranslateTransform.YProperty, Knock(-strength * 0.5 * direction));
        }

        /// <summary>Дрожание по одной оси: туда, обратно, чуть-чуть и в ноль.</summary>
        private static DoubleAnimationUsingKeyFrames Knock(double offset)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(215)
            };

            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(offset, KeyTime.FromPercent(0.2),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(-offset * 0.6, KeyTime.FromPercent(0.5)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(offset * 0.28, KeyTime.FromPercent(0.74)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));

            return animation;
        }

        /// <summary>Счётчик вспыхивает: подскок размера, свечение и цвет.</summary>
        private void PopProfCounter()
        {
            txtProfClicks.Text = _profClicks.ToString();

            // Появился — значит, дальше горит, пока не обнулится
            ProfCounterBadge.BeginAnimation(UIElement.OpacityProperty, null);
            ProfCounterBadge.Opacity = 1;

            if (txtProfClicks.RenderTransform is ScaleTransform scale)
            {
                double peak = _profFinale ? 1.45 : 1.2;
                var pop = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(270)
                };
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.22)));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1),
                    new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }));

                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }

            if (_profGlow != null)
            {
                double bright = _profFinale ? 34 : 22;
                var blur = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(340)
                };
                blur.KeyFrames.Add(new EasingDoubleKeyFrame(8, KeyTime.FromPercent(0)));
                blur.KeyFrames.Add(new EasingDoubleKeyFrame(bright, KeyTime.FromPercent(0.2)));
                blur.KeyFrames.Add(new EasingDoubleKeyFrame(9, KeyTime.FromPercent(1)));
                _profGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blur);
                _profGlow.BeginAnimation(DropShadowEffect.ColorProperty, FlashTo(ProfFlashColor));
            }

            if (_profCounterBrush != null)
                _profCounterBrush.BeginAnimation(SolidColorBrush.ColorProperty, FlashTo(ProfFlashColor));
        }

        /// <summary>Цвет: вспышка в начале, золото на круглых, обычный в конце.</summary>
        private ColorAnimationUsingKeyFrames FlashTo(Color flash)
        {
            Color settle = ProfRoundNumber ? ProfGoldColor : ProfBaseColor;

            var animation = new ColorAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(430)
            };

            animation.KeyFrames.Add(new EasingColorKeyFrame(flash, KeyTime.FromPercent(0)));
            animation.KeyFrames.Add(new EasingColorKeyFrame(settle, KeyTime.FromPercent(0.55)));
            animation.KeyFrames.Add(new EasingColorKeyFrame(ProfBaseColor, KeyTime.FromPercent(1)));

            return animation;
        }

        /// <summary>Вверх от плитки улетает «+1» и растворяется.</summary>
        private void ShowProfClickPop()
        {
            if (ProfPopLayer == null) return;

            if (_profPops.Count == 0) CreateProfPops();

            var pop = _profPops[_profPopIndex];
            _profPopIndex = (_profPopIndex + 1) % _profPops.Count;

            pop.FontSize = 11 + _profRng.NextDouble() * 3;
            pop.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // «+1» появляется НАД плиткой и уходит выше: внутри плитки он
            // закрывал бы подпись «МАСТЕРСТВО», а так улетает в пустое место
            double width = ProfPopLayer.ActualWidth;
            if (width <= 0) width = pop.DesiredSize.Width * 4;

            Canvas.SetLeft(pop, Math.Max(0, (width - pop.DesiredSize.Width) / 2));
            Canvas.SetTop(pop, -pop.DesiredSize.Height - 2);

            pop.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(640))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });

            if (pop.RenderTransform is TranslateTransform move)
            {
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, -18, TimeSpan.FromMilliseconds(640))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    });
            }
        }

        /// <summary>Запас «+1»: пяти хватает даже на очень частое нажатие.</summary>
        private void CreateProfPops()
        {
            var font = TryFindResource("F.Display") as FontFamily ?? new FontFamily("Segoe UI");

            for (int i = 0; i < 5; i++)
            {
                var pop = new TextBlock
                {
                    Text = "+1",
                    FontFamily = font,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(ProfBaseColor),
                    IsHitTestVisible = false,
                    Opacity = 0,
                    RenderTransform = new TranslateTransform(),
                    Effect = new DropShadowEffect
                    {
                        Color = ProfBaseColor,
                        BlurRadius = 10,
                        ShadowDepth = 0,
                        Opacity = 0.9
                    }
                };

                ProfPopLayer.Children.Add(pop);
                _profPops.Add(pop);
            }
        }

        /// <summary>
        /// Последнее нажатие: счётчик показывает предел, вспыхивает и гаснет,
        /// а счёт начинается заново.
        /// </summary>
        private void StartProfFinale()
        {
            _profResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _profResetTimer.Tick += (s, e) =>
            {
                _profResetTimer.Stop();
                _profResetTimer = null;

                // Напоследок тряхнуть, потом погасить и обнулить
                ShakeProfTile();

                _profClicks = 0;
                _profFinale = false;

                ProfCounterBadge.BeginAnimation(UIElement.OpacityProperty, null);
                ProfCounterBadge.Opacity = 0;
                txtProfClicks.Text = "0";
            };
            _profResetTimer.Start();
        }

        #endregion

        private void BtnPotion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string diceFormula)
            {
                // Бросок по формуле "XdY+Z" через DiceRoller — тип броска = "Зелье"
                var potionResult = _diceRoller.RollFormula(diceFormula, "Зелье");
                potionResult.Description = $"Зелье лечения ({diceFormula})";

                ShowDiceResult(potionResult);
                SaveDiceRollToHistory(potionResult);
                ApplyHpChange(potionResult.Total, true);
                // Оверлей НЕ закрываем — юзер сам решит
            }
        }

        private int RollDiceFormula(string formula)
        {
            try
            {
                string[] parts = formula.Split('+');
                string dicePart = parts[0];
                int bonus = parts.Length > 1 ? int.Parse(parts[1]) : 0;

                string[] diceParts = dicePart.Split('d');
                int numberOfDice = int.Parse(diceParts[0]);
                int diceType = int.Parse(diceParts[1]);

                int total = bonus;
                for (int i = 0; i < numberOfDice; i++)
                    total += _random.Next(1, diceType + 1);

                return total;
            }
            catch { return 0; }
        }

        private void ApplyHpChange(int amount, bool isHeal)
        {
            if (amount == 0) return;

            int newHp = isHeal ? _currentHp + amount : _currentHp - amount;

            if (!isHeal && _tempHP > 0)
            {
                int damageAfterTemp = amount - _tempHP;
                if (damageAfterTemp > 0)
                {
                    newHp = _currentHp - damageAfterTemp;
                    _tempHP = 0;
                }
                else
                {
                    _tempHP -= amount;
                    newHp = _currentHp;
                }
            }

            newHp = Math.Max(0, Math.Min(newHp, _maxHp));
            _currentHp = newHp;

            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE CharacterInfo SET HP = @hp WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@hp", newHp);
            cmd.Parameters.AddWithValue("@id", _characterId);
            cmd.ExecuteNonQuery();

            UpdateHPDisplayWithTempHP();
            txtHP.Text = $"{newHp} / {_maxHp}";

            if (newHp == 0) ShowStabilizationOverlay();
            else ResetStabilization();

            NotifyRoomProfileChanged();
        }

        private void UpdateHPDisplayWithTempHP()
        {
            txtHPCurrent.Text = _tempHP > 0
                ? $"{_currentHp}/{_maxHp} (+{_tempHP} врем.)"
                : $"{_currentHp}/{_maxHp}";
        }

        private void BtnHPTemp_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtTempHP.Text, out int tempHPAmount) && tempHPAmount > 0)
            {
                _tempHP = Math.Max(_tempHP, tempHPAmount);
                ShowToast($"Временные хиты: {_tempHP}");
                UpdateHPDisplayWithTempHP();
            }
        }

        private void BtnSetMaxHP_Click(object sender, RoutedEventArgs e)
        {
            // Ограничиваем сверху: раньше можно было ввести любое число, и дальше
            // расчёты здоровья (сложение с уроном) переполнялись и роняли программу
            if (!int.TryParse(txtMaxHPInput.Text, out int maxHP) || maxHP <= 0)
            {
                ShowToast("Максимум здоровья — целое число больше нуля", true);
                return;
            }

            if (maxHP > MaxHpInput)
            {
                maxHP = MaxHpInput;
                txtMaxHPInput.Text = maxHP.ToString();
                ShowToast($"Максимум ограничен значением {MaxHpInput}", true);
            }

            {
                try
                {
                    using var conn = DatabaseHelper.GetConnection();
                    conn.Open();

                    using var cmd = new SqliteCommand("INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@key, @value)", conn);
                    cmd.Parameters.AddWithValue("@key", $"Character_{_characterId}_MaxHP");
                    cmd.Parameters.AddWithValue("@value", maxHP.ToString());
                    cmd.ExecuteNonQuery();

                    _maxHp = maxHP;

                    if (_currentHp > maxHP)
                    {
                        _currentHp = maxHP;
                        using var updateCmd = new SqliteCommand("UPDATE CharacterInfo SET HP = @hp WHERE id_character = @id", conn);
                        updateCmd.Parameters.AddWithValue("@hp", maxHP);
                        updateCmd.Parameters.AddWithValue("@id", _characterId);
                        updateCmd.ExecuteNonQuery();
                    }

                    txtHPMax.Text = $"Максимум: {maxHP}";
                    UpdateHPDisplayWithTempHP();
                    LoadCharacterHeader();
                    ShowToast($"Максимум HP: {maxHP}");
                    NotifyRoomProfileChanged();
                }
                catch (Exception ex)
                {
                    ShowToast($"Ошибка: {ex.Message}", true);
                }
            }
        }
        /// <summary>
        /// Предел вводимого числа здоровья.
        ///
        /// Раньше кнопки «Лечить» и «Урон» вызывали int.Parse без проверки, и большое
        /// число (или текст) роняло программу с исключением. Теперь ввод ограничен,
        /// а разбор везде идёт через безопасный метод.
        /// </summary>
        private const int MaxHpInput = 999999;

        /// <summary>Безопасно читает число из поля здоровья.</summary>
        private int ReadHpInput()
        {
            string raw = (txtHPInput.Text ?? "").Trim();
            if (raw.Length == 0) return 0;
            if (!int.TryParse(raw, out int value)) return 0;
            return Math.Max(0, Math.Min(value, MaxHpInput));
        }

        /// <summary>В поле здоровья пускаем только цифры.</summary>
        private void HpNumber_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        /// <summary>Пробел, Enter и вставку из буфера тоже обрабатываем без сюрпризов.</summary>
        private void HpNumber_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) { e.Handled = true; return; }

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl && e.Key == Key.V)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText().Trim();
                        int value;
                        if (int.TryParse(text, out value))
                        {
                            e.Handled = true;
                            SetHpInput(Math.Min(Math.Max(0, value), MaxHpInput));
                        }
                        else
                        {
                            e.Handled = true;   // в буфере не число — ничего не вставляем
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>Обрезает введённое руками число и синхронизирует накопитель значений.</summary>
        private void TxtHPInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            string raw = (txtHPInput.Text ?? "").Trim();

            // Ведущие нули убираем: иначе при наборе «12» получалось «120»,
            // потому что ноль по умолчанию оставался в начале строки
            string cleaned = raw.TrimStart('0');
            if (cleaned.Length == 0) cleaned = "0";
            if (cleaned.Length > 6) cleaned = cleaned.Substring(0, 6);

            if (cleaned != raw)
            {
                txtHPInput.Text = cleaned;
                txtHPInput.CaretIndex = cleaned.Length;
            }

            _hpInputValue = cleaned;
        }

        /// <summary>Ставит значение в поле здоровья (кнопки и вставка).</summary>
        private void SetHpInput(int value)
        {
            _hpInputValue = value.ToString();
            txtHPInput.Text = _hpInputValue;
        }

        private void BtnHPHeal_Click(object sender, RoutedEventArgs e) => ApplyHpChange(ReadHpInput(), true);
        private void BtnHPDamage_Click(object sender, RoutedEventArgs e) => ApplyHpChange(ReadHpInput(), false);
        private void BtnHPRoll_Click(object sender, RoutedEventArgs e)
        {
            int roll = _random.Next(1, 9);
            SetHpInput(roll);
        }

        private void HPNum_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                string digit = btn.Content?.ToString() ?? "";
                string current = _hpInputValue == "0" ? "" : _hpInputValue;
                string next = current + digit;
                if (next.Length > 6) next = next.Substring(0, 6);
                SetHpInput(int.TryParse(next, out int value) ? value : 0);
            }
        }

        private void HPClear_Click(object sender, RoutedEventArgs e) => SetHpInput(0);

        private void HPBackspace_Click(object sender, RoutedEventArgs e)
        {
            string current = _hpInputValue ?? "0";
            string next = current.Length > 1 ? current.Substring(0, current.Length - 1) : "0";
            SetHpInput(int.TryParse(next, out int value) ? value : 0);
        }

        private void ShowStabilizationOverlay()
        {
            _stabSuccesses = 0;
            _stabFailures = 0;
            StabilizationIndicator.Visibility = Visibility.Visible;
            btnStabilizationRollCompact.Visibility = Visibility.Visible;
            UpdateStabilizationUI();
        }

        private void BtnStabilizationRoll_Click(object sender, RoutedEventArgs e)
        {
            int roll = _random.Next(1, 21);
            bool isSuccess = roll >= 10;
            string description = $"d20 (DC 10)";

            if (roll == 20) { _stabSuccesses += 2; description += " [КРИТ! +2 успеха]"; }
            else if (roll == 1) { _stabFailures += 2; description += " [ПРОВАЛ! +2 провала]"; }
            else if (isSuccess) { _stabSuccesses++; description += " [Успех]"; }
            else { _stabFailures++; description += " [Провал]"; }

            var stabResult = new Services.DiceRollResult
            {
                Timestamp = DateTime.Now,
                Description = description,
                IndividualRolls = new List<int> { roll },
                Total = roll,
                RollType = "Спасбросок стабилизации"
            };

            ShowDiceResult(stabResult);
            SaveDiceRollToHistory(stabResult);
            UpdateStabilizationUI();

            if (_stabSuccesses >= 3)
            {
                var successResult = new Services.DiceRollResult
                {
                    Timestamp = DateTime.Now,
                    Description = "СТАБИЛИЗАЦИЯ УСПЕШНА! +1 HP",
                    IndividualRolls = new List<int>(),
                    Total = 1,
                    RollType = "Стабилизация"
                };
                ShowDiceResult(successResult);
                ResetStabilization();
                ApplyHpChange(1, true);
            }
            else if (_stabFailures >= 3)
            {
                var deathResult = new Services.DiceRollResult
                {
                    Timestamp = DateTime.Now,
                    Description = "СМЕРТЬ ПЕРСОНАЖА",
                    IndividualRolls = new List<int>(),
                    Total = 0,
                    RollType = "Смерть"
                };
                ShowDiceResult(deathResult);
                StabilizationIndicator.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateStabilizationUI()
        {
            UpdateCircles(StabSuccessesIndicator, _stabSuccesses, Color.FromRgb(46, 204, 113));
            UpdateCircles(StabFailuresIndicator, _stabFailures, Color.FromRgb(231, 76, 60));
        }

        private void UpdateCircles(StackPanel panel, int count, Color activeColor)
        {
            Color inactiveColor = Color.FromRgb(52, 73, 94);
            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is Border border)
                    border.Background = new SolidColorBrush(i < count ? activeColor : inactiveColor);
            }
        }

        private void ResetStabilization()
        {
            _stabSuccesses = 0;
            _stabFailures = 0;
            StabilizationIndicator.Visibility = Visibility.Collapsed;
            btnStabilizationRollCompact.Visibility = Visibility.Collapsed;
            UpdateStabilizationUI();
        }
        #endregion

        #region === 4. УРОВНИ И ОПЫТ ===
        private void AddExp(int amount)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                int level = 1, exp = 0;
                using (var cmd = new SqliteCommand("SELECT Level, Exp FROM LevelTable WHERE id_character = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", _characterId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        level = Convert.ToInt32(reader["Level"]);
                        exp = Convert.ToInt32(reader["Exp"]);
                    }
                }

                exp = Math.Max(0, exp + amount);
                int levelsGained = 0;
                while (level < 20 && exp >= GetExpForNextLevel(level))
                {
                    level++;
                    levelsGained++;
                }
                int prof = CalculateProficiencyBonus(level);

                using var updateCmd = new SqliteCommand("UPDATE LevelTable SET Level=@level, Exp=@exp, ProficiencyBonus=@prof WHERE id_character=@id", conn);
                updateCmd.Parameters.AddWithValue("@level", level);
                updateCmd.Parameters.AddWithValue("@exp", exp);
                updateCmd.Parameters.AddWithValue("@prof", prof);
                updateCmd.Parameters.AddWithValue("@id", _characterId);
                updateCmd.ExecuteNonQuery();

                LoadCharacterHeader();
                UpdateProgressBarFromDB();

                if (levelsGained > 0) ShowToast($"Новый уровень: {level}! Бонус мастерства +{prof}");
                else ShowToast($"Опыт: {exp}");
                NotifyRoomProfileChanged();
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка: {ex.Message}", true);
            }
        }

        private void BtnLevelUpAvailable_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                int currentLevel;
                int currentExp;

                // Читатель закрываем до UPDATE.
                //
                // Открытый SqliteDataReader держит разделяемую блокировку файла
                // базы, поэтому UPDATE по тому же соединению не проходит: SQLite
                // отвечает «database is locked», драйвер повторяет попытку до
                // CommandTimeout (30 с). Кнопка «Новый уровень» из-за этого
                // выглядела зависшей на полминуты. Заодно убрано чтение внутри
                // ветвлений: значения считываются один раз и сразу.
                using (var cmd = new SqliteCommand("SELECT Level, Exp FROM LevelTable WHERE id_character = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", _characterId);
                    using var reader = cmd.ExecuteReader();
                    if (!reader.Read()) return;

                    currentLevel = Convert.ToInt32(reader["Level"]);
                    currentExp = Convert.ToInt32(reader["Exp"]);
                }

                int expForNextLevel = GetExpForNextLevel(currentLevel);

                if (currentExp < expForNextLevel)
                {
                    ShowToast($"Недостаточно опыта! Нужно: {expForNextLevel}, у вас: {currentExp}", true);
                    return;
                }

                if (currentLevel >= 20)
                {
                    ShowToast("Максимальный уровень (20)!");
                    return;
                }

                int newLevel = currentLevel + 1;
                int newProficiency = CalculateProficiencyBonus(newLevel);

                using (var updateCmd = new SqliteCommand("UPDATE LevelTable SET Level = @level, ProficiencyBonus = @prof WHERE id_character = @id", conn))
                {
                    updateCmd.Parameters.AddWithValue("@level", newLevel);
                    updateCmd.Parameters.AddWithValue("@prof", newProficiency);
                    updateCmd.Parameters.AddWithValue("@id", _characterId);
                    updateCmd.ExecuteNonQuery();
                }

                ShowToast($"Новый уровень: {newLevel}! Бонус мастерства: +{newProficiency}");
                LoadCharacterHeader();
                UpdateProgressBarFromDB();
                NotifyRoomProfileChanged();
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка: {ex.Message}", true);
            }
        }

        private void HeaderLevel_Click(object sender, MouseButtonEventArgs e)
        {
            LevelManagerOverlay.Visibility = Visibility.Visible;
            UpdateProgressBarFromDB();
            var animation = new DoubleAnimation
            {
                From = 400,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            LevelManagerOverlay.RenderTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void UpdateProgressBar(int level, int currentExp)
        {
            txtLevelMin.Text = level.ToString();
            txtLevelMax.Text = (level + 1).ToString();

            int expForNextLevel = GetExpForNextLevel(level);

            // Опыт хранится накопительно, а в оверлее показывается как
            // «текущий / необходимый». Раньше из него вычитался порог прошлого
            // уровня, и при нестандартной паре уровень/опыт бар становился нулевым.
            double percentage = level >= 20 || expForNextLevel <= 0
                ? 100
                : (double)currentExp / expForNextLevel * 100;
            _levelProgressPercent = Math.Max(0, Math.Min(100, percentage));
            ApplyLevelProgressWidth();

            // Ширина дорожки появляется только после раскладки оверлея
            Dispatcher.BeginInvoke(new Action(ApplyLevelProgressWidth), DispatcherPriority.Loaded);

            txtExpCurrent.Text = level >= 20
                ? $"Опыт: {currentExp} / максимум"
                : $"Опыт: {currentExp} / {expForNextLevel}";
            btnLevelUpAvailable.Visibility = (currentExp >= expForNextLevel && level < 20)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void LevelProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
            => ApplyLevelProgressWidth();

        private void ApplyLevelProgressWidth()
        {
            if (LevelProgressTrack == null || ProgressBarFill == null) return;
            double width = LevelProgressTrack.ActualWidth;
            if (width <= 0) return;
            ProgressBarFill.Width = width * _levelProgressPercent / 100;
        }

        private void UpdateProgressBarFromDB()
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT Level, Exp FROM LevelTable WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@id", _characterId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                UpdateProgressBar(Convert.ToInt32(reader["Level"]), Convert.ToInt32(reader["Exp"]));
        }

        private int CalculateProficiencyBonus(int level) => level switch
        {
            <= 4 => 2,
            <= 8 => 3,
            <= 12 => 4,
            <= 16 => 5,
            _ => 6
        };

        private int GetExpForNextLevel(int currentLevel)
        {
            if (currentLevel < 0) return 0;
            int[] thresholds = { 0, 300, 900, 2700, 6500, 14000, 23000, 34000, 48000, 64000,
                                 85000, 100000, 120000, 140000, 165000, 195000, 225000, 265000,
                                 305000, 355000, int.MaxValue };
            return thresholds[Math.Min(currentLevel, thresholds.Length - 1)];
        }

        private void BtnDigit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string digit)
            {
                string current = txtLevelInput.Text ?? "";
                string next = (current == "0" || current.Length == 0) ? digit : current + digit;
                if (next.Length > 9) next = next.Substring(0, 9);
                txtLevelInput.Text = next;
            }
        }

        /// <summary>
        /// В поле ввода опыта — только цифры, и «0» по умолчанию не мешает вводу:
        /// раньше при наборе «12» получалось «120», потому что ноль не стирался.
        /// </summary>
        private void TxtLevelInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (txtLevelInput == null) return;

            string text = txtLevelInput.Text ?? "";
            if (text.Length > 9)
            {
                text = text.Substring(0, 9);
                txtLevelInput.Text = text;
                txtLevelInput.CaretIndex = text.Length;
            }
        }

        private void BtnClearInput_Click(object sender, RoutedEventArgs e) => txtLevelInput.Text = "0";

        private void BtnAddExpFromInput_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtLevelInput.Text, out int amount) && amount > 0)
            {
                AddExp(amount);
                txtLevelInput.Text = "0";
            }
        }

        private void BtnSubtractExpFromInput_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtLevelInput.Text, out int amount) && amount > 0)
            {
                AddExp(-amount);
                txtLevelInput.Text = "0";
            }
        }

        private void BtnLevelSettings_Click(object sender, RoutedEventArgs e)
        {
            _isManualEditMode = !_isManualEditMode;
            if (_isManualEditMode)
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT Level, Exp, ProficiencyBonus FROM LevelTable WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@id", _characterId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    _tempLevel = Convert.ToInt32(reader["Level"]);
                    _tempExp = Convert.ToInt32(reader["Exp"]);
                    _tempProficiency = Convert.ToInt32(reader["ProficiencyBonus"]);
                }
                txtManualLevel.Text = _tempLevel.ToString();
                txtManualExp.Text = _tempExp.ToString();
                txtManualProficiency.Text = $"+{_tempProficiency}";
                ManualEditPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ManualEditPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void SaveManualEditToDB()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("UPDATE LevelTable SET Level = @level, Exp = @exp, ProficiencyBonus = @prof WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@level", _tempLevel);
                cmd.Parameters.AddWithValue("@exp", _tempExp);
                cmd.Parameters.AddWithValue("@prof", _tempProficiency);
                cmd.Parameters.AddWithValue("@id", _characterId);
                cmd.ExecuteNonQuery();

                LoadCharacterHeader();
                UpdateProgressBarFromDB();
                ShowToast("Сохранено");
                NotifyRoomProfileChanged();
            }
            catch (Exception ex)
            {
                ShowToast($"Ошибка сохранения: {ex.Message}", true);
            }
        }

        private void TxtManualLevel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtManualLevel.Text, out int v))
            {
                _tempLevel = Math.Max(1, Math.Min(20, v));
                _tempProficiency = CalculateProficiencyBonus(_tempLevel);
                txtManualProficiency.Text = $"+{_tempProficiency}";
                SaveManualEditToDB();
            }
            else txtManualLevel.Text = _tempLevel.ToString();
        }

        private void TxtManualExp_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtManualExp.Text, out int v))
            {
                _tempExp = Math.Max(0, v);
                int levelsGained = 0;
                while (_tempLevel < 20 && _tempExp >= GetExpForNextLevel(_tempLevel))
                {
                    _tempLevel++;
                    levelsGained++;
                }
                _tempProficiency = CalculateProficiencyBonus(_tempLevel);
                txtManualLevel.Text = _tempLevel.ToString();
                txtManualProficiency.Text = $"+{_tempProficiency}";
                SaveManualEditToDB();
                if (levelsGained > 0) ShowToast($"Новый уровень: {_tempLevel}!");
            }
            else txtManualExp.Text = _tempExp.ToString();
        }

        private void TxtManualProf_LostFocus(object sender, RoutedEventArgs e)
        {
            string raw = txtManualProficiency.Text.Replace("+", "").Trim();
            if (int.TryParse(raw, out int v))
            {
                _tempProficiency = Math.Max(0, Math.Min(6, v));
                SaveManualEditToDB();
            }
            txtManualProficiency.Text = $"+{_tempProficiency}";
        }

        private void BtnDecreaseLevel_Click(object sender, RoutedEventArgs e)
        {
            if (_tempLevel > 1)
            {
                _tempLevel--;
                txtManualLevel.Text = _tempLevel.ToString();
                _tempProficiency = CalculateProficiencyBonus(_tempLevel);
                txtManualProficiency.Text = $"+{_tempProficiency}";
                SaveManualEditToDB();
            }
        }

        private void BtnIncreaseLevel_Click(object sender, RoutedEventArgs e)
        {
            if (_tempLevel < 20)
            {
                _tempLevel++;
                txtManualLevel.Text = _tempLevel.ToString();
                _tempProficiency = CalculateProficiencyBonus(_tempLevel);
                txtManualProficiency.Text = $"+{_tempProficiency}";
                SaveManualEditToDB();
            }
        }

        private void BtnDecreaseExpManual_Click(object sender, RoutedEventArgs e)
        {
            if (_tempExp >= 100)
            {
                _tempExp -= 100;
                txtManualExp.Text = _tempExp.ToString();
                SaveManualEditToDB();
            }
        }

        private void BtnIncreaseExpManual_Click(object sender, RoutedEventArgs e)
        {
            _tempExp += 100;
            txtManualExp.Text = _tempExp.ToString();
            SaveManualEditToDB();
        }

        private void BtnDecreaseProf_Click(object sender, RoutedEventArgs e)
        {
            if (_tempProficiency > 0)
            {
                _tempProficiency--;
                txtManualProficiency.Text = $"+{_tempProficiency}";
                SaveManualEditToDB();
            }
        }

        private void BtnIncreaseProf_Click(object sender, RoutedEventArgs e)
        {
            if (_tempProficiency < 6)
            {
                _tempProficiency++;
                txtManualProficiency.Text = $"+{_tempProficiency}";
                SaveManualEditToDB();
            }
        }

        private void BtnCloseManualEdit_Click(object sender, RoutedEventArgs e)
        {
            _isManualEditMode = false;
            ManualEditPanel.Visibility = Visibility.Collapsed;
        }
        #endregion

        #region === 5. КАЛЬКУЛЯТОР ЗОЛОТА ===
        private void HeaderGold_Click(object sender, MouseButtonEventArgs e)
        {
            if (IsInside(e.OriginalSource as DependencyObject, btnGoldDisplayToggle)) return;
            ToggleGoldCalculator();
        }

        private static bool IsInside(DependencyObject child, DependencyObject ancestor)
        {
            while (child != null)
            {
                if (child == ancestor) return true;
                child = VisualTreeHelper.GetParent(child) ?? (LogicalTreeHelper.GetParent(child) as DependencyObject);
            }
            return false;
        }

        private void ToggleGoldCalculator()
        {
            _isCalcOpen = !_isCalcOpen;
            if (_isCalcOpen)
            {
                GoldCalcOverlay.Visibility = Visibility.Visible;
                UpdateCalcUI();
                HighlightSelectedCurrency();
                var animation = new DoubleAnimation { From = 400, To = 0, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                GoldCalcOverlay.RenderTransform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
            else
            {
                var animation = new DoubleAnimation { From = 0, To = 400, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                animation.Completed += (s, e) => { GoldCalcOverlay.Visibility = Visibility.Collapsed; };
                GoldCalcOverlay.RenderTransform.BeginAnimation(TranslateTransform.XProperty, animation);
                _moneyService.SaveMoney(_currentMoney);
                UpdateHeaderGoldDisplay();
                ShowToast("Золото сохранено");
            }
        }


        // Мета-данные монет: ключ, аббревиатура, цвет металла
        private static readonly (string Key, string Abbr, string Hex)[] Coins =
        {
    ("Platinum", "ПМ", "#E8ECF0"),
    ("Electrum", "ЭМ", "#A9C4B5"),
    ("Gold",     "ЗМ", "#5FD4DD"),
    ("Silver",   "СМ", "#C3CBD6"),
    ("Copper",   "ММ", "#C47A50"),
};

        private int CoinCount(string key) => key switch
        {
            "Platinum" => _currentMoney.Platinum,
            "Electrum" => _currentMoney.Electrum,
            "Gold" => _currentMoney.Gold,
            "Silver" => _currentMoney.Silver,
            "Copper" => _currentMoney.Copper,
            _ => 0
        };

        // Чип монеты: [цветная точка] [кол-во цветом металла] [аббревиатура Mute]
        private Border MakeCoinChip(string key, bool compact)
        {
            var meta = Coins.First(c => c.Key == key);
            int count = CoinCount(key);
            var metal = new SolidColorBrush((Color)ColorConverter.ConvertFromString(meta.Hex));

            var chip = new Border
            {
                Background = (Brush)FindResource("B.PanelHi"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = count == 0 ? 0.45 : 1.0   // нулевые монеты не спорят с реальными
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            sp.Children.Add(new Ellipse
            {
                Width = compact ? 8 : 10,
                Height = compact ? 8 : 10,
                Fill = metal,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = count.ToString(),
                Foreground = metal,
                FontWeight = FontWeights.Bold,
                FontSize = compact ? 12 : 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = " " + meta.Abbr,
                Foreground = (Brush)FindResource("B.Mute"),
                FontSize = compact ? 10 : 11,
                VerticalAlignment = VerticalAlignment.Center
            });

            chip.Child = sp;
            return chip;
        }

        private void UpdateCalcUI()
        {
            txtCalcTotal.Text = $"Всего: {_currentMoney.TotalGoldValue:F2} зм";

            // Все 5 монет чипами (нулевые затемнены)
            panelCoins.Children.Clear();
            foreach (var coin in Coins)
                panelCoins.Children.Add(MakeCoinChip(coin.Key, compact: false));

            _calcDisplayValue = "0";
            txtCalcDisplay.Text = _calcDisplayValue;

            // Шапка обновляется живьём, пока калькулятор открыт
            UpdateHeaderGoldDisplay();
        }

        private void BtnGoldDisplayToggle_Click(object sender, RoutedEventArgs e)
        {
            _goldExpanded = !_goldExpanded;
            UpdateHeaderGoldDisplay();
            btnGoldDisplayToggle.Foreground = new SolidColorBrush((Color)FindResource(_goldExpanded ? "C.Accent" : "C.Mute"));
            btnGoldDisplayToggle.ToolTip = _goldExpanded
                ? "Режим: все монеты (клик — свернуть до золота)"
                : "Режим: только золото (клик — показать все монеты)";
        }

        private void UpdateHeaderGoldDisplay()
        {
            if (_currentMoney == null) return;

            panelGoldHeader.Children.Clear();

            if (!_goldExpanded)
            {
                // Обычный режим: только золото
                panelGoldHeader.Children.Add(MakeCoinChip("Gold", compact: true));
                return;
            }

            // Расширенный режим: все номиналы
            foreach (var coin in Coins)
                panelGoldHeader.Children.Add(MakeCoinChip(coin.Key, compact: true));
        }

        private void CurrencyBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string currency)
            {
                _selectedCurrency = currency;
                HighlightSelectedCurrency();
            }
        }

        private void HighlightSelectedCurrency()
        {
            var defaultBrush = new SolidColorBrush(Color.FromRgb(58, 58, 58));
            var accentBrush = new SolidColorBrush(Color.FromRgb(103, 58, 183));
            btnCurPlatinum.Background = defaultBrush;
            btnCurElectrum.Background = defaultBrush;
            btnCurGold.Background = defaultBrush;
            btnCurSilver.Background = defaultBrush;
            btnCurCopper.Background = defaultBrush;

            switch (_selectedCurrency)
            {
                case "Platinum": btnCurPlatinum.Background = accentBrush; break;
                case "Electrum": btnCurElectrum.Background = accentBrush; break;
                case "Gold": btnCurGold.Background = accentBrush; break;
                case "Silver": btnCurSilver.Background = accentBrush; break;
                case "Copper": btnCurCopper.Background = accentBrush; break;
            }
        }

        private void CalcNum_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                _calcDisplayValue = _calcDisplayValue == "0" ? btn.Content.ToString() : _calcDisplayValue + btn.Content.ToString();
                txtCalcDisplay.Text = _calcDisplayValue;
            }
        }

        private void CalcBackspace_Click(object sender, RoutedEventArgs e)
        {
            _calcDisplayValue = _calcDisplayValue.Length > 1 ? _calcDisplayValue.Substring(0, _calcDisplayValue.Length - 1) : "0";
            txtCalcDisplay.Text = _calcDisplayValue;
        }

        private void CalcQuickAdd_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag.ToString(), out int amount))
            {
                int current = int.Parse(_calcDisplayValue == "0" ? "0" : _calcDisplayValue);
                _calcDisplayValue = (current + amount).ToString();
                txtCalcDisplay.Text = _calcDisplayValue;
            }
        }

        private void CalcTake_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(_calcDisplayValue, out int amount) && amount > 0)
            {
                _currentMoney.Apply(_selectedCurrency, amount);
                UpdateCalcUI();
                SaveMoneyNow();
            }
        }

        private void CalcGive_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(_calcDisplayValue, out int amount) && amount > 0)
            {
                _currentMoney.Apply(_selectedCurrency, -amount);
                UpdateCalcUI();
                SaveMoneyNow();
            }
        }

        /// <summary>
        /// Сохраняет деньги сразу после каждой правки.
        ///
        /// Раньше запись шла только при закрытии калькулятора кнопкой. Если
        /// человек закрывал окошко кликом по затемнению или закрывал всё окно
        /// персонажа, правки не сохранялись — при следующем открытии монеты
        /// снова были нулями. Теперь терять нечего: пишем при каждом изменении.
        /// </summary>
        private void SaveMoneyNow()
        {
            try
            {
                _moneyService.SaveMoney(_currentMoney);
                UpdateHeaderGoldDisplay();

                // Сводка на листе персонажа показывает золото — обновляем и её
                _currentInfoView?.RefreshEquipmentSummary();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterInfoWindow] сохранение денег: {ex.Message}");
            }
        }

        private void btnCloseGoldCalc_Click(object sender, RoutedEventArgs e) => ToggleGoldCalculator();
        #endregion

        #region === 6. DICE ROLLER ===
        private void BtnDice_Click(object sender, RoutedEventArgs e)
        {
            _pendingRollType = "Бросок";
            _pendingRollLabel = "";
            txtDiceMenuTitle.Text = "ВЫБОР КУБИКОВ";

            _diceCount = 1;
            _diceModifier = 0;
            _syncingDiceFormula = true;
            txtDiceCount.Text = "1";
            txtDiceModifier.Text = "0";
            txtDiceFormula.Text = "1d20";
            _syncingDiceFormula = false;
            _selectedDiceType = Services.DiceType.D20;

            DiceMenuOverlay.Visibility = Visibility.Visible;
            HighlightSelectedDice(_selectedDiceType);
        }

        private void DiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string diceType)
            {
                _selectedDiceType = (Services.DiceType)Enum.Parse(typeof(Services.DiceType), diceType);
                HighlightSelectedDice(_selectedDiceType);
                SyncDiceFormulaFromFields();
            }
        }

        private void HighlightSelectedDice(Services.DiceType type)
        {
            var diceButtons = new[] { btnD4, btnD6, btnD8, btnD10, btnD12, btnD20, btnD100 };
            foreach (var btn in diceButtons)
            {
                if (btn != null)
                    btn.Background = new SolidColorBrush(Color.FromRgb(50, 50, 65));
            }

            var selectedBtn = type switch
            {
                Services.DiceType.D4 => btnD4,
                Services.DiceType.D6 => btnD6,
                Services.DiceType.D8 => btnD8,
                Services.DiceType.D10 => btnD10,
                Services.DiceType.D12 => btnD12,
                Services.DiceType.D20 => btnD20,
                Services.DiceType.D100 => btnD100,
                _ => btnD20
            };

            if (selectedBtn != null)
                selectedBtn.Background = new SolidColorBrush(Color.FromRgb(103, 58, 183));
        }

        /// <summary>Обновляет поле формулы по текущим выбору кубика, количеству и модификатору.</summary>
        private void SyncDiceFormulaFromFields()
        {
            if (_syncingDiceFormula) return;

            _syncingDiceFormula = true;
            try
            {
                int sides = (int)_selectedDiceType;
                string formula = $"{_diceCount}d{sides}";
                if (_diceModifier != 0)
                    formula += _diceModifier > 0 ? $"+{_diceModifier}" : _diceModifier.ToString();
                txtDiceFormula.Text = formula;
            }
            finally
            {
                _syncingDiceFormula = false;
            }
        }

        private void BtnDecreaseCount_Click(object sender, RoutedEventArgs e)
        {
            if (_diceCount > 1)
            {
                _diceCount--;
                txtDiceCount.Text = _diceCount.ToString();
                SyncDiceFormulaFromFields();
            }
        }

        private void BtnIncreaseCount_Click(object sender, RoutedEventArgs e)
        {
            _diceCount++;
            txtDiceCount.Text = _diceCount.ToString();
            SyncDiceFormulaFromFields();
        }

        private void BtnDecreaseMod_Click(object sender, RoutedEventArgs e)
        {
            _diceModifier--;
            txtDiceModifier.Text = _diceModifier.ToString();
            SyncDiceFormulaFromFields();
        }

        private void BtnIncreaseMod_Click(object sender, RoutedEventArgs e)
        {
            _diceModifier++;
            txtDiceModifier.Text = _diceModifier.ToString();
            SyncDiceFormulaFromFields();
        }

        /// <summary>
        /// Бросок введённой вручную формулы.
        ///
        /// Понимает несколько слагаемых и характеристики: «1к20+6+СИЛ», «2d6+3»,
        /// «1к10+1к6», «1d20-1». Раньше здесь был разбор только одной группы
        /// костей с одной добавкой, поэтому «1к20+6+СИЛ» превращалось в голый
        /// d20 — минимальный результат выходил 1 вместо 13.
        ///
        /// Возвращает null, если формулу разобрать не удалось.
        /// </summary>
        private Services.DiceRollResult RollTypedFormula(string formula, string rollType, string label)
        {
            if (string.IsNullOrWhiteSpace(formula)) return null;
            if (!Services.DiceFormulaParser.TryParseExpression(formula, out _, out _)) return null;

            var result = _diceRoller.RollExpression(formula, rollType, ResolveAbilityModifier);
            if (result == null) return null;

            string prefix = string.IsNullOrEmpty(label) ? "" : label + " · ";
            result.Description = prefix + result.Description;
            return result;
        }

        private void BtnRollDice_Click(object sender, RoutedEventArgs e)
        {
            // Если пользователь ввёл формулу — бросаем именно её (понимает 9d20, 2к6+3, 1d20+6+СИЛ)
            string typed = txtDiceFormula.Text?.Trim();
            Services.DiceRollResult result = RollTypedFormula(typed, _pendingRollType, _pendingRollLabel);

            if (result == null && !string.IsNullOrWhiteSpace(typed))
            {
                ShowToast($"Не понял формулу «{typed}» — бросаю выбранные кубики. " +
                          "Примеры: 1к20, 2к6+3, 1к20+6+СИЛ", true);
            }

            if (result == null)
            {
                result = string.IsNullOrEmpty(_pendingRollLabel)
                    ? _diceRoller.RollDice(_selectedDiceType, _diceCount, _diceModifier, _pendingRollType)
                    : _diceRoller.RollDice(_selectedDiceType, _diceCount, _diceModifier, _pendingRollType, _pendingRollLabel);

                // Человекочитаемое описание: ЧТО бросали и чем
                string countPart = _diceCount > 1 ? $"×{_diceCount}" : "";
                string modPart = _diceModifier == 0 ? "" : (_diceModifier > 0 ? $" +{_diceModifier}" : $" {_diceModifier}");
                string dicePart = $"{_selectedDiceType}{countPart}{modPart}";

                result.Description = string.IsNullOrEmpty(_pendingRollLabel)
                    ? dicePart
                    : $"{_pendingRollLabel} ({dicePart})";
            }

            _rollHistory.Add(result);
            ShowDiceResult(result);
            SaveDiceRollToHistory(result);

            DiceMenuOverlay.Visibility = Visibility.Collapsed;

            _diceCount = 1;
            _diceModifier = 0;
            _syncingDiceFormula = true;
            txtDiceCount.Text = "1";
            txtDiceModifier.Text = "0";
            txtDiceFormula.Text = "1d20";
            _syncingDiceFormula = false;
            _pendingRollType = "Бросок";
            _pendingRollLabel = "";
            txtDiceMenuTitle.Text = "ВЫБОР КУБИКОВ";
        }

        private void BtnCloseDiceMenu_Click(object sender, RoutedEventArgs e) => DiceMenuOverlay.Visibility = Visibility.Collapsed;

        /// <summary>
        /// Значение модификатора характеристики по её названию: принимает
        /// СИЛ/СИЛА/STR, ЛОВ/ЛОВКОСТЬ/DEX, ТЕЛ/ТЕЛОСЛОЖЕНИЕ/CON, ИНТ/ИНТЕЛЛЕКТ/INT,
        /// МДР/МУДРОСТЬ/WIS, ХАР/ХАРИЗМА/CHA.
        /// </summary>
        public int? ResolveAbilityModifier(string abilityName)
        {
            if (string.IsNullOrWhiteSpace(abilityName)) return null;

            var name = abilityName.Trim().ToLowerInvariant();
            string column;
            switch (name)
            {
                case "сил": case "сила": case "силы": case "str": case "strength":
                    column = "Strength"; break;
                case "лов": case "ловкость": case "ловкости": case "dex": case "dexterity":
                    column = "Dexterity"; break;
                case "тел": case "телосложение": case "con": case "constitution":
                    column = "Constitution"; break;
                case "инт": case "интеллект": case "int": case "intelligence":
                    column = "Intelligence"; break;
                case "мдр": case "мудрость": case "wis": case "wisdom":
                    column = "Wisdom"; break;
                case "хар": case "харизма": case "cha": case "charisma":
                    column = "Charisma"; break;
                default:
                    return null;
            }

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand($"SELECT {column} FROM CharacterInfo WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@id", _characterId);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value) return null;

                int score = Convert.ToInt32(value);
                return (int)Math.Floor((score - 10) / 2.0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiceFormula] не удалось взять характеристику {column}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Поле формулы — главное: из него обновляются количество, модификатор и тип кубика.
        /// </summary>
        private void TxtDiceFormula_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingDiceFormula) return;

            string text = txtDiceFormula.Text;

            // Полный разбор: «1к20+6+СИЛ», «2d6+3», «1к10+1к6». Берём первую
            // группу костей, остальное показываем как добавку — так поля под
            // формулой не расходятся с тем, что в ней написано
            if (Services.DiceFormulaParser.TryParseExpression(text, out var terms, out _))
            {
                Services.DiceFormulaParser.DiceTerm diceTerm = null;
                foreach (var term in terms)
                {
                    if (term.IsDice) { diceTerm = term; break; }
                }

                if (diceTerm != null)
                {
                    _syncingDiceFormula = true;
                    try
                    {
                        _diceCount = diceTerm.Count;
                        _selectedDiceType = Enum.IsDefined(typeof(Services.DiceType), diceTerm.Sides)
                            ? (Services.DiceType)diceTerm.Sides
                            : Services.DiceType.D20;

                        int flat = 0;
                        var abilities = new List<string>();
                        foreach (var term in terms)
                        {
                            if (term.IsDice) continue;
                            if (term.IsAbility) abilities.Add((term.Sign < 0 ? "−" : "+") + term.Ability.ToUpperInvariant());
                            else flat += term.Sign * term.Modifier;
                        }

                        txtDiceCount.Text = diceTerm.Count.ToString();

                        if (abilities.Count == 0)
                        {
                            _diceModifier = flat;
                            txtDiceModifier.Text = flat.ToString();
                        }
                        else
                        {
                            // Значение характеристики подставится при броске
                            string flatPart = flat == 0 ? "" : (flat > 0 ? "+" + flat : "−" + Math.Abs(flat));
                            txtDiceModifier.Text = (flatPart + string.Concat(abilities)).TrimStart('+');
                        }

                        HighlightSelectedDice(_selectedDiceType);
                    }
                    finally
                    {
                        _syncingDiceFormula = false;
                    }
                    return;
                }
            }

            if (Services.DiceRoller.TryParseFormula(text, out int count, out int sides, out int modifier,
                    out string ability, out _))
            {
                _syncingDiceFormula = true;
                try
                {
                    _diceCount = count;
                    _selectedDiceType = Enum.IsDefined(typeof(Services.DiceType), sides)
                        ? (Services.DiceType)sides
                        : Services.DiceType.D20;

                    if (string.IsNullOrEmpty(ability))
                    {
                        _diceModifier = modifier;
                        txtDiceModifier.Text = modifier.ToString();
                    }
                    else
                    {
                        // Характеристику показываем текстом, значение подставим при броске
                        txtDiceModifier.Text = (modifier < 0 ? "-" : "+") + ability.ToUpperInvariant();
                    }

                    txtDiceCount.Text = count.ToString();
                    HighlightSelectedDice(_selectedDiceType);
                }
                finally
                {
                    _syncingDiceFormula = false;
                }
            }
        }

        /// <summary>
        /// Кнопка «По формуле»: бросает ровно то, что написано в поле формулы,
        /// включая 9d20, 2к6+3, 1d20+СИЛ.
        /// </summary>
        private void BtnRollFormula_Click(object sender, RoutedEventArgs e)
        {
            string formula = txtDiceFormula.Text?.Trim();
            if (string.IsNullOrWhiteSpace(formula))
            {
                ShowToast("Введите формулу, например 1к20, 2к6+3 или 1к20+6+СИЛ", true);
                return;
            }

            var result = RollTypedFormula(formula, _pendingRollType, _pendingRollLabel);
            if (result == null)
            {
                ShowToast($"Не понял формулу «{formula}». Примеры: 1к20, 2к6+3, 1к20+6+СИЛ, 2d10", true);
                return;
            }

            _rollHistory.Add(result);
            ShowDiceResult(result);
            SaveDiceRollToHistory(result);

            DiceMenuOverlay.Visibility = Visibility.Collapsed;
            _diceCount = 1;
            _diceModifier = 0;
            _syncingDiceFormula = true;
            txtDiceCount.Text = "1";
            txtDiceModifier.Text = "0";
            txtDiceFormula.Text = "";
            _syncingDiceFormula = false;
            _pendingRollType = "Бросок";
            _pendingRollLabel = "";
            txtDiceMenuTitle.Text = "ВЫБОР КУБИКОВ";
        }

        private void DiceRoller_OnDiceRolled(object sender, Services.DiceRollResult result) { }

        /// <summary>
        /// Отправляет бросок в общий журнал отряда.
        ///
        /// Сюда попадает ВСЁ, что проходит через DiceRoller — включая урон предметов
        /// из инвентаря. Скрытых бросков у игрока нет: право на скрытый бросок даёт
        /// только роль ведущего, и проверка живёт в RoomSession.
        /// </summary>
        private void PublishRollToRoom(Services.DiceRollResult result)
        {
            try
            {
                var session = RoomSession.Instance;
                if (!session.IsActive || result == null) return;

                session.PublishRoll(
                    result.Description ?? "",
                    result.RollType ?? "",
                    result.Total,
                    result.IndividualRolls?.ToList() ?? new List<int>(),
                    hidden: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room] PublishRoll: {ex.Message}");
            }
        }

        /// <summary>
        /// Показывает готовый бросок (например, суммарный урон атаки) одной панелью.
        /// </summary>
        public void ShowCombinedRoll(Services.DiceRollResult result, bool publish = true)
            => ShowDiceResult(result, publish);

        private void ShowDiceResult(Services.DiceRollResult result, bool publish = true)
        {
            if (result == null) return;

            // Сначала в отряд: бросок должен уйти друзьям независимо от того, как отрисуется окно
            if (publish) PublishRollToRoom(result);

            // Крит и провал — только про d20: «20» на кости урона не включает крит
            bool isD20 = result.Dice != null && result.Dice.Count > 0 && result.Dice[0].Sides == 20;
            bool isCritSuccess = isD20 && result.IndividualRolls != null && result.IndividualRolls.Count > 0 && result.IndividualRolls[0] == 20;
            bool isCritFail = isD20 && result.IndividualRolls != null && result.IndividualRolls.Count > 0 && result.IndividualRolls[0] == 1;

            var resultPanel = BuildRollPanel(result, isCritSuccess, isCritFail);

            if (isCritSuccess || isCritFail) ApplyCritHighlight(resultPanel, isCritSuccess);

            AddResultPanel(resultPanel);
        }

        /// <summary>Добавляет готовую панель в стопку и вешает таймер закрытия.</summary>
        private void AddResultPanel(Border resultPanel)
        {
            if (resultPanel == null) return;

            spDiceResults.Children.Add(resultPanel);
            while (spDiceResults.Children.Count > MAX_VISIBLE_RESULTS)
                spDiceResults.Children.RemoveAt(0);

            DiceResultsPanel.Visibility = Visibility.Visible;

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (s, e) => { timer.Stop(); RemoveResultPanel(resultPanel); };
            timer.Start();
        }

        /// <summary>
        /// Урон по частям: одна панель, но внутри — отдельный блок на каждую
        /// часть (свои кубики, формула и итог). В ленту отряда уходит одна
        /// сводка, чтобы не засорять её пятью записями за один выстрел.
        /// </summary>
        public void ShowDamageParts(string what, IReadOnlyList<Services.DiceRollResult> parts, bool publish = true)
        {
            if (parts == null || parts.Count == 0) return;

            if (publish)
            {
                var summary = new Services.DiceRollResult
                {
                    Timestamp = DateTime.Now,
                    RollType = "Урон",
                    Description = what ?? "",
                    Dice = new List<Services.RolledDie>(),
                    IndividualRolls = new List<int>(),
                    Total = 0,
                    Modifier = 0
                };

                foreach (var part in parts)
                {
                    summary.Total += part.Total;
                    summary.Modifier += part.Modifier;
                    if (part.Dice != null) summary.Dice.AddRange(part.Dice);
                    if (part.IndividualRolls != null) summary.IndividualRolls.AddRange(part.IndividualRolls);
                    if ((summary.Formula ?? "").Length > 0) summary.Formula += " + ";
                    summary.Formula += part.Formula ?? "";
                }

                PublishRollToRoom(summary);
            }

            AddResultPanel(BuildDamagePartsPanel(what, parts));
        }

        /// <summary>
        /// Панель составного урона: каждая часть — своим блоком с кубиками,
        /// формулой и итогом. Раньше всё сливалось в одну сумму, и части без
        /// кубика («1», «ЛОВ») вообще не были видны.
        /// </summary>
        /// <summary>
        /// Панель составного урона: компактные строки, по одной на часть.
        /// Раньше каждая часть была отдельной высокой карточкой, и панель
        /// занимала пол-экрана; теперь блоки идут строками с общей шапкой.
        /// </summary>
        private Border BuildDamagePartsPanel(string what, IReadOnlyList<Services.DiceRollResult> parts)
        {
            var accent = (Brush)FindResource("B.Arcane");
            var gold = (Brush)FindResource("B.Accent");
            var ink = (Brush)FindResource("B.Ink");
            var mute = (Brush)FindResource("B.Mute");
            var frost = (Brush)FindResource("B.Frost");
            var slate = (Brush)FindResource("B.Slate");
            var line = (Brush)FindResource("B.Line");
            var display = (FontFamily)FindResource("F.Display");

            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x15, 0x1B, 0x2E)),
                CornerRadius = new CornerRadius(8),
                BorderBrush = accent,
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 8, 10, 8),
                MaxWidth = 430,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var root = new StackPanel();
            int sum = parts.Sum(p => p.Total);

            // ── шапка: что за урон, общий итог, закрыть ──
            var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock
            {
                Text = "УРОН",
                FontFamily = display,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = accent
            });
            if (!string.IsNullOrWhiteSpace(what))
                title.Children.Add(new TextBlock
                {
                    Text = what,
                    Foreground = ink,
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
            Grid.SetColumn(title, 0);
            head.Children.Add(title);

            var sumText = new TextBlock
            {
                Text = sum.ToString(),
                FontFamily = display,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = gold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0)
            };
            Grid.SetColumn(sumText, 1);
            head.Children.Add(sumText);

            var btnClose = new Button
            {
                Content = new Helpers.IconView
                {
                    Kind = "close", IconSize = 12, IconBrush = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 20,
                Height = 20,
                Background = Brushes.Transparent,
                Foreground = mute,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnClose.Click += (s, e) => RemoveResultPanel(panel);
            Grid.SetColumn(btnClose, 2);
            head.Children.Add(btnClose);
            root.Children.Add(head);

            // ── по строке на часть: подпись, кубики, итог ──
            int index = 0;
            foreach (var part in parts)
            {
                var rowBorder = new Border
                {
                    BorderBrush = line,
                    BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0),
                    Padding = new Thickness(0, 4, 0, 4)
                };
                index++;

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var label = new TextBlock
                {
                    Text = part.Description ?? "Урон",
                    Foreground = frost,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                System.Windows.Automation.AutomationProperties.SetName(label, "Урон: " + (part.Description ?? ""));
                left.Children.Add(label);
                left.Children.Add(new TextBlock
                {
                    Text = part.Formula ?? "",
                    Foreground = mute,
                    FontSize = 9,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(left, 0);
                row.Children.Add(left);

                var diceRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
                var dice = part.Dice ?? new List<Services.RolledDie>();
                foreach (var die in dice.Take(4))
                    diceRow.Children.Add(BuildDieChip(die, gold, mute, slate, line));
                if (dice.Count > 4)
                    diceRow.Children.Add(new TextBlock
                    {
                        Text = "+" + (dice.Count - 4),
                        Foreground = mute,
                        FontSize = 9,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                if (part.Modifier != 0 && dice.Count == 0)
                    diceRow.Children.Add(new TextBlock
                    {
                        Text = part.Modifier > 0 ? "+" + part.Modifier : part.Modifier.ToString(),
                        Foreground = frost,
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                Grid.SetColumn(diceRow, 1);
                row.Children.Add(diceRow);

                var total = new TextBlock
                {
                    Text = part.Total.ToString(),
                    FontFamily = display,
                    FontSize = 19,
                    FontWeight = FontWeights.Bold,
                    Foreground = gold,
                    TextAlignment = TextAlignment.Right,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(total, 2);
                row.Children.Add(total);

                rowBorder.Child = row;
                root.Children.Add(rowBorder);
            }

            panel.Child = root;
            return panel;
        }

        private Border BuildRollPanel(Services.DiceRollResult result, bool isCritSuccess, bool isCritFail)
        {
            var accent = (Brush)FindResource("B.Arcane");
            var gold = (Brush)FindResource("B.Accent");
            var ink = (Brush)FindResource("B.Ink");
            var mute = (Brush)FindResource("B.Mute");
            var frost = (Brush)FindResource("B.Frost");
            var slate = (Brush)FindResource("B.Slate");
            var line = (Brush)FindResource("B.Line");
            var display = (FontFamily)FindResource("F.Display");

            var panel = new Border
            {
                // Фон полупрозрачный: панель результатов висит поверх листа персонажа
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x15, 0x1B, 0x2E)),
                CornerRadius = new CornerRadius(10),
                BorderBrush = accent,
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(14, 12, 12, 12)
            };

            var root = new StackPanel();

            // ── шапка: что за бросок и крестик ──
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = (result.RollType ?? "Бросок").ToUpperInvariant(),
                FontFamily = display,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 0);
            head.Children.Add(title);

            var btnClose = new Button
            {
                Content = new Helpers.IconView { Kind = "close", IconSize = 13, IconBrush = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Width = 22,
                Height = 22,
                Background = Brushes.Transparent,
                Foreground = mute,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnClose.Click += (s, e) => RemoveResultPanel(panel);
            Grid.SetColumn(btnClose, 1);
            head.Children.Add(btnClose);
            root.Children.Add(head);

            // ── подпись: кто и что бросил ──
            if (!string.IsNullOrWhiteSpace(result.Description))
            {
                root.Children.Add(new TextBlock
                {
                    Text = result.Description,
                    Foreground = ink,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 8)
                });
            }

            // ── кости отдельными плашками плюс добавка ──
            var diceRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var dice = result.Dice ?? new List<Services.RolledDie>();

            // У старых записей (из базы и истории) гранности нет — показываем значения
            if (dice.Count == 0 && result.IndividualRolls != null)
            {
                foreach (int value in result.IndividualRolls)
                    dice.Add(new Services.RolledDie { Value = value, Sides = 0 });
            }

            int shown = 0;
            foreach (var die in dice)
            {
                if (shown >= 30)
                {
                    diceRow.Children.Add(new TextBlock
                    {
                        Text = $"… ещё {dice.Count - shown}",
                        Foreground = mute,
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 0, 0)
                    });
                    break;
                }

                diceRow.Children.Add(BuildDieChip(die, gold, mute, slate, line));
                shown++;
            }

            if (result.Modifier != 0)
            {
                diceRow.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4F, 0xB6, 0xAC)),
                    BorderBrush = frost,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 7, 9, 7),
                    Margin = new Thickness(0, 0, 5, 5),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Добавка к броску",
                    Child = new TextBlock
                    {
                        Text = result.Modifier > 0 ? "+" + result.Modifier : result.Modifier.ToString(),
                        Foreground = frost,
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }

            root.Children.Add(diceRow);

            // ── итог и из чего он сложился ──
            var totalRow = new Grid();
            totalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            totalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var breakdown = new TextBlock
            {
                Text = result.Breakdown(),
                Foreground = mute,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 10, 4)
            };
            Grid.SetColumn(breakdown, 0);
            totalRow.Children.Add(breakdown);

            var totalText = new TextBlock
            {
                Text = result.Total.ToString(),
                FontFamily = display,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            totalText.Foreground = isCritSuccess
                ? new SolidColorBrush(Color.FromRgb(0xF2, 0xC7, 0x66))
                : isCritFail
                    ? new SolidColorBrush(Color.FromRgb(0xB9, 0xAF, 0xA0))
                    : gold;

            int digits = result.Total.ToString().Length;
            totalText.FontSize = digits switch { 1 => 40, 2 => 36, 3 => 30, 4 => 24, _ => 20 };
            Grid.SetColumn(totalText, 1);
            totalRow.Children.Add(totalText);
            root.Children.Add(totalRow);

            // ── подписи: формула, среднее, крит ──
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.Formula)) notes.Add($"Формула: {result.Formula}");
            int average = Services.DiceFormulaParser.AverageExpression(result.Formula);
            if (average > 0) notes.Add($"в среднем {average}");
            if (isCritSuccess) notes.Add("КРИТ!");
            else if (isCritFail) notes.Add("ПРОВАЛ");

            if (notes.Count > 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", notes),
                    Foreground = isCritSuccess ? gold : mute,
                    FontSize = 10.5,
                    FontWeight = isCritSuccess ? FontWeights.Bold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            panel.Child = root;
            return panel;
        }

        /// <summary>
        /// Плашка одного кубика: выпавшее значение и гранность.
        /// Максимум на кости выделяется золотом, единица — приглушённо.
        /// </summary>
        private Border BuildDieChip(Services.RolledDie die, Brush gold, Brush mute, Brush slate, Brush line)
        {
            bool top = die.Sides > 0 && die.IsMax;
            bool bottom = die.Sides > 0 && die.IsMin;

            var border = new Border
            {
                Background = top
                    ? PaletteBrush.Get("B.AccentSoft", Color.FromArgb(0x33, 0x5F, 0xD4, 0xDD))
                    : bottom
                        ? new SolidColorBrush(Color.FromArgb(0x22, 0xB9, 0xAF, 0xA0))
                        : slate,
                BorderBrush = top ? gold : line,
                BorderThickness = new Thickness(top ? 1.5 : 1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 5, 9, 4),
                Margin = new Thickness(0, 0, 5, 5),
                MinWidth = 42,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = die.Value.ToString(),
                            FontFamily = (FontFamily)FindResource("F.Display"),
                            FontSize = 17,
                            FontWeight = FontWeights.Bold,
                            Foreground = top ? gold : (Brush)FindResource("B.Ink"),
                            HorizontalAlignment = HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = die.Sides > 0 ? "d" + die.Sides : "d?",
                            FontSize = 9,
                            Foreground = mute,
                            HorizontalAlignment = HorizontalAlignment.Center
                        }
                    }
                }
            };

            border.ToolTip = die.Sides > 0
                ? $"Выпало {die.Value} на d{die.Sides}" + (top ? " — максимум на кости" : bottom ? " — минимум" : "")
                : $"Выпало {die.Value}";
            return border;
        }
        /// <summary>
        /// Подсветка крита и провала: одна короткая вспышка вместо бесконечной
        /// красно-зелёной пульсации.
        ///
        /// Крит — золотой отблеск, пробегающий по панели, и лёгкий толчок размера.
        /// Провал — приглушённый песочный цвет и короткая дрожь.
        /// Через секунду панель совсем успокаивается, поэтому ряд бросков не мигает.
        /// </summary>
        private void ApplyCritHighlight(Border panel, bool success)
        {
            try
            {
                Color accent = success
                    ? PaletteBrush.GetColor("C.Accent", Color.FromRgb(0x5F, 0xD4, 0xDD))   // акцент темы
                    : Color.FromRgb(0x9A, 0x8F, 0x7A);  // песок

                panel.BorderBrush = new SolidColorBrush(accent);
                panel.BorderThickness = new Thickness(1.5);
                panel.Background = new SolidColorBrush(Blend(Color.FromArgb(0xD8, 25, 25, 35), accent, success ? 0.32 : 0.12));

                // Свечение: вспыхнуло и погасло — без повторов
                var glow = new DropShadowEffect { Color = accent, ShadowDepth = 0, BlurRadius = 0, Opacity = 0.85 };
                panel.Effect = glow;
                var glowFrames = new DoubleAnimationUsingKeyFrames();
                glowFrames.KeyFrames.Add(new LinearDoubleKeyFrame(success ? 30 : 16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
                glowFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(success ? 900 : 520))));
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, glowFrames);

                var transform = new TransformGroup();
                var scale = new ScaleTransform(1, 1);
                var shift = new TranslateTransform(0, 0);
                transform.Children.Add(scale);
                transform.Children.Add(shift);
                panel.RenderTransform = transform;
                panel.RenderTransformOrigin = new Point(0.5, 0.5);

                if (success)
                {
                    var pop = new DoubleAnimationUsingKeyFrames();
                    pop.KeyFrames.Add(new LinearDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
                    pop.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430))));
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                }
                else
                {
                    var shake = new DoubleAnimationUsingKeyFrames();
                    double[] offsets = { 0, -7, 6, -4, 2, 0 };
                    for (int i = 0; i < offsets.Length; i++)
                        shake.KeyFrames.Add(new LinearDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55 * (i + 1)))));
                    shift.BeginAnimation(TranslateTransform.XProperty, shake);
                }

                // Золотой блик по криту: полоса проходит слева направо один раз
                if (success && panel.Child is Grid inner && inner.RowDefinitions.Count > 0)
                {
                    var shine = new Border
                    {
                        Width = 120,
                        IsHitTestVisible = false,
                        Opacity = 0,
                        Background = new LinearGradientBrush(
                            Color.FromArgb(0x00, 0xFF, 0xE9, 0xB0),
                            Color.FromArgb(0x66, 0xFF, 0xE9, 0xB0),
                            new Point(0, 0), new Point(1, 0))
                    };
                    Grid.SetRowSpan(shine, inner.RowDefinitions.Count);
                    Grid.SetColumnSpan(shine, inner.ColumnDefinitions.Count);

                    var shineShift = new TranslateTransform(-160, 0);
                    shine.RenderTransform = shineShift;
                    inner.Children.Add(shine);

                    var move = new DoubleAnimation
                    {
                        From = -160,
                        To = 560,
                        Duration = TimeSpan.FromMilliseconds(700),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    var fade = new DoubleAnimationUsingKeyFrames();
                    fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
                    fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
                    shineShift.BeginAnimation(TranslateTransform.XProperty, move);
                    shine.BeginAnimation(OpacityProperty, fade);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Бросок] подсветка крита: {ex.Message}");
            }
        }

        /// <summary>Смешивает два цвета: нужно для мягкой подложки под крит.</summary>
        private static Color Blend(Color baseColor, Color tint, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                baseColor.A,
                (byte)(baseColor.R + (tint.R - baseColor.R) * amount),
                (byte)(baseColor.G + (tint.G - baseColor.G) * amount),
                (byte)(baseColor.B + (tint.B - baseColor.B) * amount));
        }

        private void RemoveResultPanel(Border panel)
        {
            if (spDiceResults.Children.Contains(panel))
            {
                spDiceResults.Children.Remove(panel);
                if (spDiceResults.Children.Count == 0)
                    DiceResultsPanel.Visibility = Visibility.Collapsed;
            }
        }

        public void ShowDiceMenu(string rollType, string skillName, int bonus)
        {
            _pendingRollType = rollType;
            _pendingRollLabel = skillName;

            _selectedDiceType = DiceType.D20;
            _diceCount = 1;
            _diceModifier = bonus;
            _syncingDiceFormula = true;
            txtDiceCount.Text = "1";
            txtDiceModifier.Text = bonus.ToString();
            txtDiceFormula.Text = bonus == 0 ? "1d20" : $"1d20{(bonus > 0 ? "+" : "")}{bonus}";
            _syncingDiceFormula = false;

            txtDiceMenuTitle.Text = string.IsNullOrEmpty(skillName)
                ? rollType.ToUpper()
                : $"{rollType.ToUpper()}: {skillName.ToUpper()}";

            DiceMenuOverlay.Visibility = Visibility.Visible;
            HighlightSelectedDice(DiceType.D20);
        }

        public void ShowDiceResult(string skillName, int bonus)
        {
            var result = _diceRoller.RollSkillCheck(skillName, bonus);
            result.RollType = "Навык";
            result.Description = $"{skillName} (мод. {(bonus >= 0 ? "+" + bonus : bonus.ToString())})";

            _rollHistory.Add(result);
            ShowDiceResult(result);
            SaveDiceRollToHistory(result);
        }

        /// <summary>
        /// Мгновенный бросок с готовым бонусом: считает и сразу показывает результат,
        /// не открывая окно выбора кубиков (для проверок, спасбросков и навыков).
        /// </summary>
        public void RollAndShow(string rollType, string label, int bonus, string formula)
        {
            try
            {
                var result = _diceRoller.RollExpression(formula, rollType, ResolveAbilityModifier);
                if (result == null) return;

                result.RollType = rollType;
                string mod = bonus >= 0 ? "+" + bonus : bonus.ToString();
                result.Description = string.IsNullOrWhiteSpace(label)
                    ? $"мод. {mod}"
                    : $"{label} (мод. {mod})";

                _rollHistory.Add(result);
                ShowDiceResult(result);
                SaveDiceRollToHistory(result);

                // Подсветка крита/провала в тосте, чтобы результат был заметен сразу
                bool crit = result.IndividualRolls != null && result.IndividualRolls.Count > 0 && result.IndividualRolls[0] == 20;
                bool fail = result.IndividualRolls != null && result.IndividualRolls.Count > 0 && result.IndividualRolls[0] == 1;
                string prefix = crit ? "Крит! " : fail ? "Провал! " : "";
                ShowToast($"{prefix}{rollType}: {label} → {result.Total}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RollAndShow] {ex.Message}");
                ShowToast($"Ошибка броска: {ex.Message}", true);
            }
        }

        private void SaveDiceRollToHistory(Services.DiceRollResult result)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("INSERT INTO DiceRollHistory (id_character, roll_type, description, result) VALUES (@charId, @type, @desc, @result)", conn);
                cmd.Parameters.AddWithValue("@charId", _characterId);
                cmd.Parameters.AddWithValue("@type", result.RollType);
                cmd.Parameters.AddWithValue("@desc", result.Description);
                cmd.Parameters.AddWithValue("@result", result.Total);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiceHistory] Error: {ex.Message}");
            }
        }
        /// <summary>Бросок формулы урона из InventoryView с показом результата и записью в историю.</summary>
        public void RollFormulaAndShow(string formula, string label)
        {
            // RollExpression, а не RollFormula: урон бывает из нескольких групп
            // («1к10+1к6» после подъёма уровня) — нужны все кости, а не первая
            var result = _diceRoller.RollExpression(formula, "Урон", ResolveAbilityModifier);
            string prefix = string.IsNullOrEmpty(label) ? "" : label + " · ";
            result.Description = $"{prefix}{result.Description}";
            _rollHistory.Add(result);
            ShowDiceResult(result);
            SaveDiceRollToHistory(result);
        }

        /// <summary>
        /// Бросок из инструментов ДМа: бестиарий и трекер инициативы.
        ///
        /// who и what попадают в подпись результата, поэтому в окне броска
        /// всегда видно, ЧТО брошено и ЗА КОГО: «Гоблин · Скимитар». Тот же текст
        /// уходит в отряд, и игроки видят бросок ведущего с автором.
        ///
        /// showPanel = false используется, когда панель только мешала бы
        /// (например, инициатива сразу для восьми жетонов), но в отряд бросок
        /// всё равно уходит, если publish = true.
        /// </summary>
        public Services.DiceRollResult RollExternal(string formula, string rollType, string who, string what,
            bool showPanel = true, bool publish = true)
        {
            try
            {
                // Выражение, а не одна группа: урон существа бывает составным
                var result = _diceRoller.RollExpression(formula, rollType, ResolveAbilityModifier);
                if (result == null) return null;

                result.RollType = string.IsNullOrWhiteSpace(rollType) ? "Бросок" : rollType.Trim();

                string owner = (who ?? "").Trim();
                string action = string.IsNullOrWhiteSpace(what) ? (formula ?? "") : what.Trim();
                result.Description = owner.Length > 0 ? $"{owner} · {action}" : action;

                _rollHistory.Add(result);

                if (showPanel)
                    ShowDiceResult(result, publish);
                else if (publish)
                    PublishRollToRoom(result);

                SaveDiceRollToHistory(result);
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RollExternal] {ex.Message}");
                ShowToast($"Ошибка броска: {ex.Message}", true);
                return null;
            }
        }

        /// <summary>Обновляет состояние трекера инициативы в подвале и у игроков.</summary>
        public void RefreshInitiativeBadge()
        {
            try
            {
                UpdateInitiativeIndicator();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Инициатива] обновление подвала: {ex.Message}");
            }
        }
        #endregion

        #region === ИСТОРИЯ БРОСКОВ (СЕССИЯ) ===
        private void BtnRollHistory_Click(object sender, RoutedEventArgs e)
        {
            PopulateRollHistory();
            RollHistoryOverlay.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation
            {
                From = -400,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            RollHistoryOverlay.RenderTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void BtnCloseRollHistory_Click(object sender, RoutedEventArgs e)
            => RollHistoryOverlay.Visibility = Visibility.Collapsed;

        private void RollHistoryOverlay_Close(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender)
            {
                e.Handled = true;
                RollHistoryOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void PopulateRollHistory()
        {
            spRollHistory.Children.Clear();

            if (_rollHistory.Count == 0)
            {
                spRollHistory.Children.Add(new TextBlock
                {
                    Text = "В этой сессии бросков ещё не было",
                    Foreground = (Brush)FindResource("B.Mute"),
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                });
                return;
            }

            for (int i = _rollHistory.Count - 1; i >= 0; i--)
            {
                var r = _rollHistory[i];

                var row = new Border
                {
                    Background = (Brush)FindResource("B.Slate"),
                    BorderBrush = (Brush)FindResource("B.Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

                var left = new StackPanel();
                left.Children.Add(new TextBlock
                {
                    Text = (r.RollType ?? "").ToUpper(),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("B.Arcane")
                });
                left.Children.Add(new TextBlock
                {
                    Text = r.Description ?? "",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("B.Ink"),
                    TextWrapping = TextWrapping.Wrap
                });
                left.Children.Add(new TextBlock
                {
                    Text = $"({string.Join(", ", r.IndividualRolls)})  ·  {r.Timestamp:HH:mm:ss}",
                    FontSize = 10,
                    Foreground = (Brush)FindResource("B.Mute")
                });

                var total = new TextBlock
                {
                    Text = r.Total.ToString(),
                    FontSize = 22,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("B.Accent"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };

                Grid.SetColumn(left, 0);
                Grid.SetColumn(total, 1);
                grid.Children.Add(left);
                grid.Children.Add(total);
                row.Child = grid;
                spRollHistory.Children.Add(row);
            }
        }
        #endregion


        #region === 7. НАВИГАЦИЯ И ОВЕРЛЕИ ===
        /// <summary>
        /// Открытые вкладки: имя → готовый вид.
        ///
        /// Раньше вкладка создавалась заново при КАЖДОМ переключении, и каждое
        /// создание заново читало базу и строило список. Из-за этого отклик
        /// доходил до секунды. Теперь вид создаётся один раз и переиспользуется.
        /// </summary>
        private readonly Dictionary<string, UserControl> _viewCache =
            new Dictionary<string, UserControl>(StringComparer.Ordinal);

        /// <summary>Что показываем сейчас: чтобы не пересобирать ту же вкладку.</summary>
        private string _currentViewName = "";

        /// <summary>
        /// Открыта ли сейчас именно вкладка «Отряд». Уведомления используют это,
        /// чтобы не всплывать, когда игрок и так смотрит на ленту.
        /// </summary>
        public bool IsRoomViewVisible => _currentViewName == "Room";

        /// <summary>
        /// Открыть вкладку «Отряд» из уведомления: развернуть окно, поднять его
        /// и переключить вкладку.
        /// </summary>
        public void ShowRoomTab()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            SwitchView("Room");
        }

        /// <summary>Переход к готовой карточке заклинания из бестиария.</summary>
        public void ShowSpellByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            SwitchView("Spells");
            if (_viewCache.TryGetValue("Spells", out var view) && view is Views.SpellsView spells)
                spells.OpenSpellByName(name);
        }

        /// <summary>
        /// Переключает вкладку и замеряет, сколько она открывается.
        ///
        /// Замер нужен потому, что на слабых машинах открытие вкладки иногда
        /// занимает секунды, а по внешнему виду непонятно, что именно тормозит:
        /// создание вида, подстановка в окно или пересчёт раскладки. Если
        /// открытие заняло больше четверти секунды, в
        /// %APPDATA%\HMWT\tab-trace.log появляется строка с разбивкой по этапам.
        /// </summary>
        /// <summary>Прячет WebView чата, пока открыт любой WPF-оверлей.</summary>
        private void HookChatOcclusion()
        {
            foreach (var overlay in new FrameworkElement[]
            {
                HPCalcOverlay, ACSpeedOverlay, GoldCalcOverlay, LevelManagerOverlay,
                DiceMenuOverlay, RestOverlay, RollHistoryOverlay, InitiativeOverlay, PortraitOverlay
            })
            {
                if (overlay == null) continue;
                overlay.IsVisibleChanged += (s, e) => UpdateChatOcclusion();
            }
        }

        private void UpdateChatOcclusion()
        {
            bool hidden = HPCalcOverlay.IsVisible || ACSpeedOverlay.IsVisible ||
                          GoldCalcOverlay.IsVisible || LevelManagerOverlay.IsVisible ||
                          DiceMenuOverlay.IsVisible || RestOverlay.IsVisible ||
                          RollHistoryOverlay.IsVisible || InitiativeOverlay.IsVisible ||
                          PortraitOverlay.IsVisible;

            // WebView2 рисуется поверх WPF, поэтому вместе с чатом прячем и карту
            if (hidden != _mapHiddenByOverlay)
            {
                _mapHiddenByOverlay = hidden;
                if (hidden) Controls.MapWebRenderer.DialogOpened();
                else Controls.MapWebRenderer.DialogClosed();
            }

            if (_viewCache != null && _viewCache.TryGetValue("Room", out var view) &&
                view is Views.RoomView room)
            {
                room.SetChatOccluded(hidden);
            }
        }

        private void SwitchView(string viewName)
        {
            var traceWatch = System.Diagnostics.Stopwatch.StartNew();
            long createdMs = -1, attachedMs = -1;

            // Повторный выбор той же вкладки ничего не пересобирает,
            // но данные могли измениться за время работы на другой вкладке
            if (viewName == _currentViewName && MainContentArea.Content != null)
            {
                UpdateTabButtonsStyle(viewName);
                NotifyViewShown(viewName, MainContentArea.Content as UserControl);
                UpdateChatOcclusion();
                return;
            }

            // Вид берём из кэша: повторное открытие вкладки больше не читает базу
            if (!_viewCache.TryGetValue(viewName, out var viewToShow) || viewToShow == null)
            {
                viewToShow = CreateView(viewName);
                if (viewToShow == null) return;
                _viewCache[viewName] = viewToShow;
            }
            createdMs = traceWatch.ElapsedMilliseconds;

            MainContentArea.Content = viewToShow;
            _currentViewName = viewName;

            // Открепляем от прежнего родителя: иначе WPF не отдаст элемент
            if (viewToShow.Parent is System.Windows.Controls.Panel oldPanel)
                oldPanel.Children.Remove(viewToShow);

            _currentInfoView = viewToShow as CharacterInfoView;
            UpdateTabButtonsStyle(viewName);

            // Данные могли устареть, пока вкладка была закрыта: сообщаем виду
            if (viewToShow is Views.RoomView room) room.Refresh();
            NotifyViewShown(viewName, viewToShow);
            UpdateChatOcclusion();
            attachedMs = traceWatch.ElapsedMilliseconds;

            // Раскладка идёт уже после обработчика. Этот вызов сработает, когда
            // WPF закончит пересчёт, — тогда и видно полную цену открытия вкладки.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                long totalMs = traceWatch.ElapsedMilliseconds;
                if (totalMs < 250) return;

                string line = $"{DateTime.Now:HH:mm:ss.fff} вкладка «{viewName}»: " +
                              $"создание вида {createdMs} мс, подстановка {attachedMs} мс, " +
                              $"раскладка и отрисовка {totalMs - attachedMs} мс, всего {totalMs} мс";
                System.Diagnostics.Debug.WriteLine("[ВКЛАДКА] " + line);
                TryWriteTabTrace(line);
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        /// <summary>Дописывает строку в журнал открытия вкладок (не мешает работе).</summary>
        private static void TryWriteTabTrace(string line)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "tab-trace.log");

                // Журнал не должен расти бесконечно: держим последние ~200 КБ
                var info = new System.IO.FileInfo(file);
                if (info.Exists && info.Length > 200 * 1024)
                    System.IO.File.WriteAllText(file, "", System.Text.Encoding.UTF8);

                System.IO.File.AppendAllText(file, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// Сообщает виду, что его показали: данные могли измениться, пока игрок
        /// работал на другой вкладке.
        ///
        /// Это важно для бонуса мастерства: он зависит от уровня, а уровень
        /// меняется в шапке окна. Раньше навыки вкладки «Характеристики»
        /// оставались с прежним бонусом до перезапуска программы.
        /// </summary>
        private void NotifyViewShown(string viewName, UserControl view)
        {
            try
            {
                if (view is CharacterInfoView info)
                {
                    info.RefreshProficiencyBonus();

                    // За время на других вкладках могли измениться инвентарь,
                    // экипировка и деньги — сводка внизу листа должна быть свежей
                    info.RefreshEquipmentSummary();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterInfoWindow] обновление вида «{viewName}»: {ex.Message}");
            }
        }

        /// <summary>Создаёт вид вкладки. Кэширования здесь нет — только создание.</summary>
        private UserControl CreateView(string viewName)
        {
            switch (viewName)
            {
                case "Info":
                    return new CharacterInfoView(_characterId);
                case "Inventory":
                    return new InventoryView(_characterId);
                case "Attacks":
                    return new Views.AttacksView(_characterId);
                case "Spells":
                    return new SpellsView(_characterId);
                case "Notes":
                    return new NotesView(_characterId);
                case "Room":
                    {
                        var room = new RoomView { CharacterId = _characterId };
                        return room;
                    }
                case "Map":
                    {
                        var map = new Views.MapView(_characterId);
                        map.OpenMonsterRequested += id =>
                        {
                            SwitchView("DmTools");
                            if (_viewCache.TryGetValue("DmTools", out var dmView) && dmView is Views.DmToolsView tools)
                                tools.OpenBestiary(id);
                        };
                        return map;
                    }
                case "DmTools":
                    return new Views.DmToolsView(_characterId);
                case "Settings":
                    return new Views.SettingsView();
                case "DatabaseEditor":
                    return new Views.DatabaseEditorView();                case "Reference":
                    return new Views.ReferenceView();
                default:
                    return null;
            }
        }

        /// <summary>
        /// Подсвечивает активную вкладку: золотая иконка с заливкой вместо
        /// серого контура. Заливка — только у «ключевых» вкладок (лист, атаки,
        /// заклинания, инструменты ДМа): так видно, где ты, и не пестрит.
        /// </summary>
        /// <summary>
        /// Где стоят вкладки: сверху (true) или слева рейкой (false).
        ///
        /// Значение берётся из настроек, поэтому вариант можно попробовать без
        /// пересборки: Ui_TopTabs = 1. По умолчанию вкладки слева — так окно
        /// выглядит привычно, а верхний вариант остаётся тестовым.
        /// </summary>
        public static bool TopTabs
        {
            get
            {
                try { return Services.UiPreferences.TopTabs; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Переносит панель вкладок в первую строку окна и переводит кнопки на
        /// горизонтальный стиль.
        ///
        /// Раскладка окна при этом не пересобирается: строка 0 в разметке уже
        /// есть, и пока вкладки стоят слева, она пустая и нулевой высоты.
        /// Поэтому переключение сводится к переносу одной панели.
        /// </summary>
        private void ApplyTabPlacement()
        {
            if (!TopTabs) return;

            // Рейка переезжает в первую строку и растягивается на всю ширину окна
            Grid.SetRow(LeftRail, 0);
            Grid.SetColumn(LeftRail, 0);
            Grid.SetColumnSpan(LeftRail, 2);
            Grid.SetRowSpan(LeftRail, 1);
            LeftRail.BorderThickness = new Thickness(0, 0, 0, 1);

            // Колонка рейки больше не нужна: её ширину отдаём содержимому.
            // Берём по индексу, а не по имени, чтобы не зависеть от генерации
            // поля для ColumnDefinition
            RootGrid.ColumnDefinitions[0].Width = new GridLength(0);

            TabStrip.Orientation = Orientation.Horizontal;
            TabStrip.HorizontalAlignment = HorizontalAlignment.Left;
            TabStrip.VerticalAlignment = VerticalAlignment.Center;
            TabStrip.Margin = new Thickness(12, 7, 12, 7);

            // Разделители рейки в горизонтальном ряду лишние
            foreach (var separator in TabStrip.Children.OfType<Separator>())
                separator.Visibility = Visibility.Collapsed;

            // Кнопки, которых нет в списке вкладок (назад, история бросков),
            // тоже переводим на верхний стиль — иначе останутся квадратами
            foreach (var button in TabStrip.Children.OfType<Button>())
                button.Style = (Style)FindResource("TopTabBtn");
        }

        private void UpdateTabButtonsStyle(string activeView)
        {
            // Пара стилей зависит от размещения вкладок: слева квадраты рейки,
            // сверху — «пилюли» со подписью
            bool top = TopTabs;
            var normal = (Style)FindResource(top ? "TopTabBtn" : "RailBtn");
            var active = (Style)FindResource(top ? "TopTabBtnActive" : "RailBtnActive");
            var accent = (Brush)FindResource("B.Accent");
            var mute = (Brush)FindResource("B.Mute");

            var tabs = new (Button Button, string View, IconView Icon, bool FillWhenActive)[]
            {
                (btnInfoTab, "Info", icoInfoTab, true),
                (btnInventoryTab, "Inventory", icoInventoryTab, false),
                (btnAttacksTab, "Attacks", icoAttacksTab, true),
                (btnNotesTab, "Notes", icoNotesTab, false),
                (btnSpellsTab, "Spells", icoSpellsTab, true),
                (btnRoomTab, "Room", icoRoomTab, false),
                (btnMapTab, "Map", icoMapTab, false),
                (btnDmToolsTab, "DmTools", icoDmToolsTab, true),
                (btnDbTab, "DatabaseEditor", icoDbTab, false),                (btnReferenceTab, "Reference", icoReferenceTab, false),
                (btnSettingsTab, "Settings", icoSettingsTab, false)
            };

            foreach (var tab in tabs)
            {
                bool isActive = string.Equals(tab.View, activeView, StringComparison.Ordinal);
                tab.Button.Style = isActive ? active : normal;

                if (tab.Icon == null) continue;
                tab.Icon.IconBrush = isActive ? accent : mute;
                tab.Icon.Filled = isActive && tab.FillWhenActive;
            }
        }

        /// <summary>Кнопка «К списку» возвращает в главное окно, а не закрывает программу.</summary>
        private bool _backToMain;

        private void BtnExportCharacter_Click(object sender, RoutedEventArgs e)
        {
            // Поля листа сохраняются по потере фокуса: снимаем фокус, чтобы
            // экспорт не поймал старые значения незакрытого текстового поля.
            Keyboard.ClearFocus();
            this.Focus();

            string name = (txtName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "Персонаж";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

            var dialog = new SaveFileDialog
            {
                Title = "Сохранить персонажа в файл",
                Filter = "Персонаж HMWT (*.hmwtpack)|*.hmwtpack|Все файлы (*.*)|*.*",
                FileName = name + CharacterPackService.FileExtension,
                DefaultExt = CharacterPackService.FileExtension
            };
            if (dialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                if (!CharacterPackService.Export(dialog.FileName, _characterId, out string error))
                {
                    MessageDialog.Error(this, "Не удалось экспортировать персонажа:\n\n" + error,
                        "Экспорт персонажа");
                    return;
                }

                MessageDialog.Info(this,
                    "Персонаж сохранён вместе с характеристиками, атаками, инвентарём, " +
                    "заклинаниями, заметками и картинками.\n\n" + dialog.FileName,
                    "Экспорт персонажа");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void BtnImportCharacter_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Импортировать персонажа",
                Filter = "Персонаж HMWT (*.hmwtpack)|*.hmwtpack|Все файлы (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                if (!CharacterPackService.Import(dialog.FileName, out int id, out string name, out string error))
                {
                    MessageDialog.Error(this, "Не удалось импортировать персонажа:\n\n" + error,
                        "Импорт персонажа");
                    return;
                }

                MessageDialog.Info(this,
                    $"Персонаж «{name}» добавлен в список.\n\n" +
                    "Текущий лист не изменялся. Нажми «К списку», чтобы увидеть нового персонажа.",
                    "Импорт персонажа");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            _backToMain = true;
            this.Close();
        }

        /// <summary>
        /// Крестик в заголовке закрывает программу целиком. Вернуться к списку
        /// персонажей можно кнопкой «К списку» на полосе вкладок.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (!_backToMain)
            {
                Application.Current?.Shutdown();
            }
        }

        private void OverlayBackground_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource != sender) return;

            // Закрываем на отпускании и гасим событие: иначе клик по тёмному
            // фону успевал «провалиться» в кнопку под ним и открыть второй оверлей
            e.Handled = true;

            // Клик по затемнению закрывает оверлей, но перед этим сохраняем
            // введённое: раньше при клике в тёмный фон правки КД и скорости
            // пропадали — игрок вводил число, щёлкал мимо поля, и ничего
            // не сохранялось. Теперь фон работает так же, как кнопка закрытия.
            CommitOpenOverlayEdits();

            GoldCalcOverlay.Visibility = Visibility.Collapsed;
            HPCalcOverlay.Visibility = Visibility.Collapsed;
            LevelManagerOverlay.Visibility = Visibility.Collapsed;
            ACSpeedOverlay.Visibility = Visibility.Collapsed;
            RollHistoryOverlay.Visibility = Visibility.Collapsed;
            RestOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Сохраняет правки из открытых оверлеев. Вызывается при любом закрытии:
        /// кнопкой, крестиком или кликом по затемнению.
        /// </summary>
        private void CommitOpenOverlayEdits()
        {
            try
            {
                if (ACSpeedOverlay.Visibility == Visibility.Visible)
                {
                    SaveAcSpeed("ArmorClass", txtACInput.Text, txtAC, "КД");
                    SaveAcSpeed("Speed", txtSpeedInput.Text, txtSpeed, "Скорость");
                }

                // Калькулятор денег закрывается и кликом по затемнению — правки
                // должны сохраниться, иначе игрок теряет введённую сумму
                if (GoldCalcOverlay.Visibility == Visibility.Visible)
                {
                    SaveMoneyNow();
                    _isCalcOpen = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterInfoWindow] сохранение боевых параметров: {ex.Message}");
            }
        }

        /// <summary>
        /// Не даёт клику по оверлею дойти до элементов под ним.
        ///
        /// Раньше при открытом калькуляторе здоровья клик по тёмному фону на месте
        /// плитки уровня открывал ещё и уровень: фон не перехватывал нажатие.
        /// </summary>
        private void Overlay_BlockBackgroundClicks(object sender, MouseButtonEventArgs e)
        {
            var overlay = sender as FrameworkElement;
            if (overlay == null || overlay.Visibility != Visibility.Visible) return;
            if (!ReferenceEquals(e.OriginalSource, overlay)) e.Handled = true;
        }

        private void btnInfoTab_Click(object sender, RoutedEventArgs e) => SwitchView("Info");
        private void btnInventoryTab_Click(object sender, RoutedEventArgs e) => SwitchView("Inventory");
        private void btnAttacksTab_Click(object sender, RoutedEventArgs e) => SwitchView("Attacks");
        private void btnNotesTab_Click(object sender, RoutedEventArgs e) => SwitchView("Notes");
        private void btnSpellsTab_Click(object sender, RoutedEventArgs e) => SwitchView("Spells");
        private void btnRoomTab_Click(object sender, RoutedEventArgs e) => SwitchView("Room");
        private void btnSettingsTab_Click(object sender, RoutedEventArgs e) => SwitchView("Settings");

        private void btnDbTab_Click(object sender, RoutedEventArgs e) => SwitchView("DatabaseEditor");
        private void btnReferenceTab_Click(object sender, RoutedEventArgs e) => SwitchView("Reference");

        private void btnMapTab_Click(object sender, RoutedEventArgs e) => SwitchView("Map");

        private void btnDmToolsTab_Click(object sender, RoutedEventArgs e)
        {
            // Права могли измениться, пока вкладка была закрыта: например,
            // игрок подключился к чужому отряду уже после открытия окна
            if (!DmRights.CanUseDmTools())
            {
                ShowToast(DmRights.DenyReason(), true);
                UpdateDmToolsVisibility();
                return;
            }

            SwitchView("DmTools");
        }

        /// <summary>
        /// Открывает вкладку инструментов ДМа извне (кнопка в главном окне).
        /// Права проверяются здесь же: у игрока вкладка закрыта.
        /// </summary>
        public void OpenDmToolsTab()
        {
            if (!DmRights.CanUseDmTools())
            {
                ShowToast(DmRights.DenyReason(), true);
                UpdateDmToolsVisibility();
                return;
            }

            SwitchView("DmTools");
        }

        /// <summary>
        /// Открывает вкладку «Атаки» извне: из инвентаря и заклинаний, когда атаку
        /// переносят туда. Так у урона одно место, а не три.
        /// </summary>
        public void OpenAttacksTab()
        {
            try
            {
                SwitchView("Attacks");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Атаки] открытие вкладки: {ex.Message}");
            }
        }

        #region === ИНСТРУМЕНТЫ ДМА: ПРАВА И КНОПКА В ПОДВАЛЕ ===

        /// <summary>
        /// Показывает или скрывает вход в инструменты ДМа.
        ///
        /// Пока игра идёт за одним компьютером, трекер и бестиарий доступны всегда.
        /// В отряде их видит только ведущий: бой должен вести один человек, иначе
        /// порядок хода разойдётся между машинами.
        /// </summary>
        private void UpdateDmToolsVisibility()
        {
            try
            {
                bool allowed = DmRights.CanUseDmTools();

                btnDmToolsTab.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
                btnInitiative.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;

                btnDmToolsTab.ToolTip = allowed
                    ? "Инструменты ДМа: трекер инициативы и бестиарий"
                    : DmRights.DenyReason();

                // Потеряли права, стоя на вкладке инструментов — уводим на лист персонажа
                if (!allowed && _currentViewName == "DmTools") SwitchView("Info");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ДМ] права: {ex.Message}");
            }
        }

        /// <summary>Подпись кнопки инициативы в подвале: раунд и чей ход.</summary>
        private void UpdateInitiativeIndicator()
        {
            try
            {
                if (btnInitiative == null) return;

                bool allowed = DmRights.CanUseDmTools();
                btnInitiative.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
                if (!allowed) return;

                var encounter = InitiativeService.ActiveEncounter();
                if (encounter == null)
                {
                    txtInitiativeStatus.Text = "Инициатива";
                    brInitiativeDot.Background = (Brush)FindResource("B.Mute");
                    btnInitiative.ToolTip = "Трекер инициативы: бой ещё не начат.\nНажмите, чтобы открыть окно инициативы";
                    return;
                }

                var current = encounter.Current();
                txtInitiativeStatus.Text = encounter.IsActive
                    ? $"Раунд {Math.Max(1, encounter.Round)} · {current?.Name ?? "—"}"
                    : "Инициатива";

                brInitiativeDot.Background = (Brush)FindResource(encounter.IsActive ? "B.Accent" : "B.Mute");
                btnInitiative.ToolTip = encounter.IsActive
                    ? $"Бой идёт: {encounter.StatusLine()}\nНажмите, чтобы открыть трекер"
                    : $"Группа «{encounter.Name}»: {encounter.StatusLine()}\nНажмите, чтобы открыть трекер";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Инициатива] подвал: {ex.Message}");
            }
        }

        /// <summary>Создаёт содержимое окошка инициативы при первом открытии.</summary>
        private void EnsureInitiativeOverlayBuilt()
        {
            if (_initiativePanel != null) return;

            _initiativePanel = new Views.InitiativePanel(true);
            _initiativePanel.Changed += UpdateInitiativeIndicator;
            _initiativePanel.OpenTrackerRequested += () =>
            {
                CloseInitiativeOverlay();
                SwitchView("DmTools");
            };
            _initiativePanel.OpenMonsterRequested += id =>
            {
                CloseInitiativeOverlay();
                SwitchView("DmTools");
                if (MainContentArea.Content is Views.DmToolsView tools) tools.OpenBestiary(id);
            };

            InitiativeOverlayHost.Content = _initiativePanel;
        }

        private void BtnInitiative_Click(object sender, RoutedEventArgs e)
        {
            if (!DmRights.CanUseDmTools())
            {
                ShowToast(DmRights.DenyReason(), true);
                return;
            }

            if (InitiativeOverlay.Visibility == Visibility.Visible)
            {
                CloseInitiativeOverlay();
                return;
            }

            EnsureInitiativeOverlayBuilt();
            _initiativePanel.Refresh();

            InitiativeOverlay.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation
            {
                From = 420,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            InitiativeOverlay.RenderTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void btnCloseInitiative_Click(object sender, RoutedEventArgs e) => CloseInitiativeOverlay();

        private void InitiativeOverlay_Close(object sender, MouseButtonEventArgs e)
        {
            // Клик по затемнению закрывает окошко; клики по содержимому не считаем
            if (!ReferenceEquals(e.OriginalSource, sender)) return;
            e.Handled = true;
            CloseInitiativeOverlay();
        }

        private void CloseInitiativeOverlay()
        {
            if (InitiativeOverlay == null) return;
            InitiativeOverlay.Visibility = Visibility.Collapsed;
            UpdateInitiativeIndicator();
        }

        #endregion

        #region === КОМНАТА: СОСТОЯНИЕ В ПОДВАЛЕ ===

        /// <summary>
        /// Сообщает отряду, что карточка сохранилась (имя, класс, здоровье,
        /// максимум, аватар).
        ///
        /// Раньше данные уходили только при входе в комнату, поэтому смену
        /// аватара или имени остальные видели лишь после перезапуска вкладки.
        /// Теперь любое сохранение карточки сразу уходит в комнату.
        /// </summary>
        private void NotifyRoomProfileChanged()
        {
            try
            {
                var session = RoomSession.Instance;
                if (!session.IsActive) return;
                PublishCharacterToRoom();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room] NotifyProfile: {ex.Message}");
                LogRoom("профиль: ошибка — " + ex.Message);
            }
        }

        /// <summary>Строка диагностики в room-log: по ней видно, что ушло в отряд.</summary>
        private static void LogRoom(string text)
        {
            try
            {
                string dir = PhotoHelper.GetWritableRoot();
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "room-log.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} {text}\r\n");
            }
            catch { }
        }

        /// <summary>
        /// Отправляет данные моего персонажа в комнату, если мы в ней состоим.
        /// Вызывается при открытии окна: чтобы отряд сразу видел актуальные ХП и класс.
        /// </summary>
        private void PublishCharacterToRoom()
        {
            try
            {
                var session = RoomSession.Instance;
                if (!session.IsActive) return;

                var me = BuildMyRoomPlayer();
                if (me != null) session.UpdateMyCharacter(me);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room] PublishCharacter: {ex.Message}");
            }
        }

        /// <summary>
        /// Отдаёт комнате мои актуальные данные по запросу.
        ///
        /// Нужен, когда хост просит профиль заново (вход, переподключение):
        /// имя, аватар и здоровье берутся из базы в момент запроса, поэтому
        /// ответ всегда свежий. Обычные сохранения отправляются сразу —
        /// см. <see cref="NotifyRoomProfileChanged"/>.
        /// </summary>
        private PlayerInfo ProvideMyRoomPlayer()
        {
            try
            {
                return BuildMyRoomPlayer();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room] ProvideMyRoomPlayer: {ex.Message}");
                return null;
            }
        }

        /// <summary>Собирает данные персонажа для комнаты из его листа.</summary>
        private PlayerInfo BuildMyRoomPlayer()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT name, class, race, HP, ArmorClass, PhotoPath,
                                           COALESCE((SELECT Level FROM LevelTable WHERE id_character = CharacterInfo.id_character), 1)
                                    FROM CharacterInfo WHERE id_character = @id";
                cmd.Parameters.AddWithValue("@id", _characterId);

                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;

                string name = r.IsDBNull(0) ? "" : r.GetString(0);
                int hp = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                string charClass = r.IsDBNull(1) ? "" : r.GetString(1);
                string race = r.IsDBNull(2) ? "" : r.GetString(2);
                int armor = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                string avatar = r.IsDBNull(5) ? "" : r.GetString(5);
                int level = r.IsDBNull(6) ? 1 : r.GetInt32(6);

                // В отряде у всех одно имя — имя персонажа. Личного профиля
                // для отряда нет: броски и трекер и так показывают персонажа.
                string display = string.IsNullOrWhiteSpace(name) ? "Игрок" : name.Trim();

                // Максимум HP в таблице CharacterInfo не хранится: он лежит в
                // настройках персонажа (там же, откуда его берёт шапка листа).
                // Без этого в отряд уходило «185/185» вместо «185/210».
                r.Close();
                int maxHp = GetMaxHpFromSettings();
                if (maxHp <= 0) maxHp = hp;

                return new PlayerInfo
                {
                    CharacterId = _characterId,
                    CharacterName = name,
                    CharacterClass = charClass,
                    CharacterRace = race,
                    Hp = hp,
                    MaxHp = maxHp,
                    ArmorClass = armor,
                    Level = level,
                    AvatarPath = avatar,
                    Name = display
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room] BuildMyRoomPlayer: {ex.Message}");
                LogRoom("профиль: не удалось собрать — " + ex.Message);
                return null;
            }
        }

        /// <summary>Обновляет индикатор комнаты в подвале: точка, текст, подсказка.</summary>
        private void UpdateRoomIndicator()
        {
            try
            {
                if (brRoomDot == null) return;

                var session = RoomSession.Instance;
                bool active = session.IsActive;

                brRoomDot.Background = (Brush)FindResource(
                    !active ? "B.Mute" : (session.IsHost ? "B.Ok" : "B.Frost"));

                txtRoomStatus.Text = active
                    ? $"Отряд · {session.GetPlayers().Count}/{NetLimits.MaxPlayers}"
                    : "Отряд";

                btnRoomStatus.ToolTip = active
                    ? session.StatusText + "\nНажмите, чтобы открыть вкладку отряда"
                    : "Создать отряд или подключиться к друзьям";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room] UpdateRoomIndicator: {ex.Message}");
            }
        }

        private void RoomSession_StateChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateRoomIndicator();
                UpdateDmToolsVisibility();
                UpdateInitiativeIndicator();
            }));
        }

        private void BtnRoomStatus_Click(object sender, RoutedEventArgs e)
        {
            if (!RoomSession.Instance.IsActive)
            {
                // Не в комнате — подсказываем, где её создать
                var main = Application.Current?.MainWindow;
                if (main is MainWindow mw) mw.OpenRoomPanel();
                else ShowToast("Откройте главное окно и нажмите «Отряд»");
                return;
            }

            SwitchView("Room");
        }

        #endregion
        #endregion

        #region === 8. AC И СКОРОСТЬ ===
        private void HeaderACSpeed_Click(object sender, MouseButtonEventArgs e)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT ArmorClass, Speed FROM CharacterInfo WHERE id_character=@id", conn);
            cmd.Parameters.AddWithValue("@id", _characterId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                txtACInput.Text = r["ArmorClass"]?.ToString() ?? "10";
                txtSpeedInput.Text = r["Speed"]?.ToString() ?? "30";
            }
            ACSpeedOverlay.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation { From = 400, To = 0, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ACSpeedOverlay.RenderTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void BtnCloseACSpeed_Click(object sender, RoutedEventArgs e)
        {
            // Крестик закрывает так же, как клик по фону: сначала сохраняем
            CommitOpenOverlayEdits();
            ACSpeedOverlay.Visibility = Visibility.Collapsed;
        }

        private void TxtACInput_LostFocus(object sender, RoutedEventArgs e) => SaveAcSpeed("ArmorClass", txtACInput.Text, txtAC, "КД");
        private void TxtSpeedInput_LostFocus(object sender, RoutedEventArgs e) => SaveAcSpeed("Speed", txtSpeedInput.Text, txtSpeed, "Скорость");

        private void SaveAcSpeed(string column, string value, TextBlock header, string label)
        {
            if (!int.TryParse(value, out int v) || v < 0)
            {
                ShowToast("Введите целое число ≥ 0", true);
                return;
            }
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand($"UPDATE CharacterInfo SET {column}=@v WHERE id_character=@id", conn);
            cmd.Parameters.AddWithValue("@v", v);
            cmd.Parameters.AddWithValue("@id", _characterId);
            cmd.ExecuteNonQuery();
            header.Text = v.ToString();
            ShowToast($"{label}: {v}");
            NotifyRoomProfileChanged();
        }
        #endregion

        #region === 9. ИМЯ/КЛАСС/РАСА ===
        private void Identity_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is string column && !string.IsNullOrWhiteSpace(column))
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand($"UPDATE CharacterInfo SET {column}=@v WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@v", tb.Text);
                cmd.Parameters.AddWithValue("@id", _characterId);
                cmd.ExecuteNonQuery();
                ShowToast("Сохранено");
                NotifyRoomProfileChanged();
            }
        }
        #endregion

        #region === 10. ТОСТЫ ===
        public void ShowToast(string message, bool isError = false)
        {
            txtToast.Text = message;
            txtToast.Foreground = new SolidColorBrush(isError ? (Color)FindResource("C.Danger") : (Color)FindResource("C.Ok"));
            ToastPanel.Visibility = Visibility.Visible;
            if (_toastTimer == null)
            {
                _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); ToastPanel.Visibility = Visibility.Collapsed; };
            }
            _toastTimer.Stop();
            _toastTimer.Start();

            try
            {
                var mapType = typeof(CharacterInfoWindow).Assembly.GetType("HMWT_MUI.Controls.MapWebRenderer");
                mapType?.GetMethod("ShowToast", System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Static)?.Invoke(null, new object[] { message, isError });
            }
            catch { }
        }
        #endregion

        #region === 11. HELPERS ===
        /// <summary>
        /// Максимум здоровья персонажа.
        ///
        /// Читает его RestService: то же число нужно отдыху, а держать две
        /// копии одного запроса к настройкам не за чем.
        /// </summary>
        private int GetMaxHpFromSettings() => RestService.LoadMaxHp(_characterId);

        public void UpdateHPDisplay(int newHP)
        {
            _currentHp = newHP;
            txtHP.Text = $"{newHP} / {_maxHp}";
            if (newHP <= 0) ShowStabilizationOverlay();
            else if (_stabSuccesses > 0 || _stabFailures > 0) ResetStabilization();

            // Здоровье изменилось (урон, лечение, отдых) — сразу сообщаем отряду
            NotifyRoomProfileChanged();
        }

        /// <summary>
        /// Лечит персонажа на указанное число хитов: бросок лечения заклинания
        /// прибавляется к здоровью, но выше максимума не поднимает.
        ///
        /// Нужно, чтобы игроку не приходилось вписывать лечение руками: броски
        /// лечения идут из вкладки заклинаний.
        /// </summary>
        public void HealCharacter(int amount)
        {
            if (amount <= 0) return;

            _maxHp = RestService.LoadMaxHp(_characterId);
            int newHp = Math.Min(_maxHp, _currentHp + amount);

            try { RestService.SaveCurrentHp(_characterId, newHp); }
            catch (Exception ex) { Debug.WriteLine($"[CharacterInfoWindow] лечение: {ex.Message}"); }

            UpdateHPDisplay(newHp);
        }

        public void UpdateLevelDisplay(int newLevel)
        {
            txtLevel.Text = newLevel.ToString();
            int proficiency = CalculateProficiencyBonus(newLevel);
            txtManagerProfBonus.Text = $"Бонус мастерства: +{proficiency}";
            UpdateProgressBarFromDB();
        }
        #endregion

        #region === 12. СОСТОЯНИЕ ПЕРСОНАЖА В ШАПКЕ ===

        /// <summary>
        /// Состояния, вдохновение и концентрация.
        ///
        /// Читается вместе с остальной шапкой, а не по событию Loaded: иначе
        /// первый кадр показывал бы «нет», и только потом настоящее значение.
        /// </summary>
        private void LoadHeaderState()
        {
            _conditionOptions = CharacterStateService.LoadConditions(_characterId);
            var chosen = new HashSet<string>(
                CharacterStateService.LoadConditionKeys(_characterId), StringComparer.OrdinalIgnoreCase);
            foreach (var option in _conditionOptions) option.IsSelected = chosen.Contains(option.Key);

            // Список пересобирается заново, поэтому источник назначаем в два шага:
            // ItemsControl иначе оставил бы в флажках прежние объекты
            listConditions.ItemsSource = null;
            listConditions.ItemsSource = _conditionOptions;
            UpdateConditionsDisplay();

            _hasInspiration = CharacterStateService.LoadInspiration(_characterId);
            UpdateInspirationDisplay();

            _concentrationSpell = CharacterStateService.LoadConcentration(_characterId);
            UpdateConcentrationDisplay();
        }

        /// <summary>Кисть темы по ключу: цвет плиток меняется вместе с отметками.</summary>
        private Brush ThemeBrush(string key) => (Brush)FindResource(key);

        /// <summary>Подпись плитки состояний: перечисление отметок или «нет».</summary>
        private void UpdateConditionsDisplay()
        {
            var selected = _conditionOptions.Where(o => o.IsSelected).ToList();
            txtConditions.Text = CharacterStateService.DescribeConditions(
                selected.Select(o => o.Key), _conditionOptions);

            // Если персонаж есть в трекере боёв — показываем то же, что на карте:
            // состояния живут у участника боя, лист лишь отражает их.
            var linked = Services.InitiativeService.FindByCharacterName(txtName.Text);
            if (linked != null)
            {
                string fromCombatant = linked.Value.Combatant.Conditions ?? "";
                if (!string.IsNullOrWhiteSpace(fromCombatant))
                {
                    var names = CharacterStateService.DescribeConditions(
                        fromCombatant.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Trim()),
                        CharacterStateService.LoadConditions(0));
                    txtConditions.Text = names;
                }
            }

            // Пусто — серым, отмечено — золотым: состояние видно с одного взгляда
            txtConditions.Foreground = ThemeBrush(selected.Count == 0 ? "B.Mute" : "B.Accent");

            btnConditions.ToolTip = selected.Count == 0
                ? "Отметьте состояния, которые сейчас действуют на персонажа"
                : "Сейчас действует:\n" + string.Join("\n", selected.Select(o => "• " + o.Name));
        }

        private void BtnConditions_Click(object sender, RoutedEventArgs e)
        {
            popConditions.IsOpen = !popConditions.IsOpen;
        }

        /// <summary>
        /// Отметки уходят в базу при закрытии списка: щёлкать по флажкам можно
        /// сколько угодно, а запись будет одна.
        /// </summary>
        private void PopConditions_Closed(object sender, EventArgs e) => SaveConditionsFromUi();

        private void SaveConditionsFromUi()
        {
            try
            {
                var keys = _conditionOptions.Where(o => o.IsSelected).Select(o => o.Key).ToList();
                CharacterStateService.SaveConditionKeys(_characterId, keys);

                // Та же функция в трёх местах: карта, трекер и лист персонажа.
                // Пишем и участнику боя, иначе состояния на карте не увидят.
                var linked = Services.InitiativeService.FindByCharacterName(txtName.Text);
                if (linked != null)
                {
                    linked.Value.Combatant.Conditions = string.Join(", ", keys);
                    Services.InitiativeService.SaveCombatant(linked.Value.Encounter, linked.Value.Combatant);
                }

                UpdateConditionsDisplay();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterInfoWindow] состояния: {ex.Message}");
            }
        }

        private void BtnInspiration_Click(object sender, RoutedEventArgs e)
        {
            _hasInspiration = !_hasInspiration;
            CharacterStateService.SaveInspiration(_characterId, _hasInspiration);
            UpdateInspirationDisplay();
            ShowToast(_hasInspiration ? "Вдохновение: есть" : "Вдохновение: нет");
        }

        private void UpdateInspirationDisplay()
        {
            string color = _hasInspiration ? "B.Accent" : "B.Mute";
            txtInspiration.Text = _hasInspiration ? "есть" : "нет";
            txtInspiration.Foreground = ThemeBrush(color);
            icoInspiration.IconBrush = ThemeBrush(color);
        }

        /// <summary>
        /// Концентрация: список заклинаний открывается меню от самой плитки.
        /// Предлагаются только заклинания с пометкой «концентрация» — держать
        /// концентрацию на «Огненном шаре» всё равно нельзя.
        /// </summary>
        private void BtnConcentration_Click(object sender, RoutedEventArgs e)
        {
            // Меню принадлежит кнопке: правый клик открывает тот же список.
            // Собирается он каждый раз заново — он зависит от того, что сейчас
            // записано у персонажа
            var menu = BuildConcentrationMenu();
            btnConcentration.ContextMenu = menu;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Собирает меню выбора заклинания для концентрации.
        ///
        /// Отдельным методом, а не внутри обработчика: стенд собирает то же меню
        /// и снимает его отдельно — ContextMenu в невидимом окне не раскрывается.
        /// </summary>
        public ContextMenu BuildConcentrationMenu()
        {
            var menu = new ContextMenu
            {
                Style = (Style)FindResource("DarkMenu"),
                PlacementTarget = btnConcentration,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };

            var spells = CharacterStateService.LoadConcentrationSpells(_characterId);
            if (spells.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "нет заклинаний с концентрацией",
                    Style = (Style)FindResource("DarkMenuItem"),
                    IsEnabled = false
                });
            }
            else
            {
                foreach (string spell in spells)
                {
                    var item = new MenuItem
                    {
                        Header = spell,
                        Style = (Style)FindResource("DarkMenuItem"),
                        IsCheckable = true,
                        IsChecked = string.Equals(spell, _concentrationSpell, StringComparison.CurrentCultureIgnoreCase)
                    };
                    string chosen = spell;
                    item.Click += (s, args) => SetConcentration(chosen);
                    menu.Items.Add(item);
                }
            }

            if (!string.IsNullOrWhiteSpace(_concentrationSpell))
            {
                menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparator") });

                var clear = new MenuItem
                {
                    Header = "Снять концентрацию",
                    Style = (Style)FindResource("DarkMenuItem")
                };
                clear.Click += (s, args) => SetConcentration(null);
                menu.Items.Add(clear);
            }

            return menu;
        }

        private void SetConcentration(string spell)
        {
            _concentrationSpell = spell ?? "";
            CharacterStateService.SaveConcentration(_characterId, _concentrationSpell);
            UpdateConcentrationDisplay();

            ShowToast(string.IsNullOrWhiteSpace(_concentrationSpell)
                ? "Концентрация снята"
                : $"Концентрация: {_concentrationSpell}");
        }

        private void UpdateConcentrationDisplay()
        {
            bool active = !string.IsNullOrWhiteSpace(_concentrationSpell);
            txtConcentration.Text = active ? _concentrationSpell : "нет";
            txtConcentration.Foreground = ThemeBrush(active ? "B.Frost" : "B.Mute");

            btnConcentration.ToolTip = active
                ? $"Концентрация: {_concentrationSpell}\nНажмите, чтобы сменить заклинание или снять её"
                : "Заклинание, на котором персонаж сосредоточен";
        }

        #endregion

        #region === 13. ОТДЫХ ===

        private void BtnShortRest_Click(object sender, RoutedEventArgs e) => OpenRestOverlay(RestService.RestKind.Short);
        private void BtnLongRest_Click(object sender, RoutedEventArgs e) => OpenRestOverlay(RestService.RestKind.Long);

        /// <summary>
        /// Открывает окно отдыха. Содержимое зависит от вида: короткий спрашивает,
        /// сколько костей хитов потратить, длинный просто перечисляет, что вернёт.
        /// </summary>
        private void OpenRestOverlay(RestService.RestKind kind)
        {
            _restKind = kind;
            _restApplied = false;
            _restDiceToSpend = 0;

            bool isShort = kind == RestService.RestKind.Short;
            txtRestTitle.Text = isShort ? "Короткий отдых" : "Длинный отдых";
            panelRestHitDice.Visibility = isShort ? Visibility.Visible : Visibility.Collapsed;
            brRestSummary.Visibility = Visibility.Visible;
            panelRestResult.Visibility = Visibility.Collapsed;
            btnRestCancel.Visibility = Visibility.Visible;
            btnRestGo.Content = isShort ? "ПОТРАТИТЬ И ОТДОХНУТЬ" : "ОТДОХНУТЬ";

            if (isShort) UpdateRestHitDiceUi();
            FillRestSummary(kind);

            RestOverlay.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation
            {
                From = 400,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            RestOverlay.RenderTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void BtnCloseRest_Click(object sender, RoutedEventArgs e) => RestOverlay.Visibility = Visibility.Collapsed;

        private void BtnRestDiceMinus_Click(object sender, RoutedEventArgs e)
        {
            _restDiceToSpend--;
            UpdateRestHitDiceUi();
        }

        private void BtnRestDicePlus_Click(object sender, RoutedEventArgs e)
        {
            _restDiceToSpend++;
            UpdateRestHitDiceUi();
        }

        /// <summary>
        /// Счётчик костей и подпись под ним.
        ///
        /// Потратить больше, чем осталось, нельзя: кости конечны, и отдых не
        /// должен превращаться в бесконечное лечение. Отдых при этом остаётся
        /// доступным и с нулём костей — заряды предметов он возвращает всё равно.
        /// </summary>
        private void UpdateRestHitDiceUi()
        {
            var (die, total, used) = RestService.LoadHitDice(_characterId);
            int available = Math.Max(0, total - used);

            if (total <= 0)
            {
                _restDiceToSpend = 0;
                txtRestDiceCount.Text = "—";
                txtRestDiceHint.Text = "Кости хитов не заданы, отдых пройдёт без лечения. " +
                                       "Кость и их количество указываются в калькуляторе здоровья (плитка «ХИТЫ»).";
                return;
            }

            _restDiceToSpend = Math.Max(0, Math.Min(_restDiceToSpend, available));
            txtRestDiceCount.Text = _restDiceToSpend.ToString();

            if (available == 0)
            {
                txtRestDiceHint.Text = $"Все кости потрачены: осталось 0 из {total}. Отдых пройдёт без лечения, " +
                                       "а длинный отдых вернёт часть запаса.";
                return;
            }

            int constitution = RestService.ConstitutionModifier(_characterId);
            string sign = constitution >= 0 ? "+" + constitution : constitution.ToString();
            txtRestDiceHint.Text = $"Доступно {available} из {total}. " +
                                   $"Каждая кость лечит 1d{die} {sign} (ТЕЛОСЛОЖЕНИЕ), но не меньше одного хита. " +
                                   "Ячейки заклинаний короткий отдых не возвращает.";
        }

        private void BtnRestGo_Click(object sender, RoutedEventArgs e)
        {
            // Кнопка после отдыха превращается в «Закрыть»: второй раз отдыхать
            // тем же нажатием не нужно
            if (_restApplied)
            {
                RestOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            RestService.RestOutcome outcome;
            try
            {
                outcome = _restKind == RestService.RestKind.Short
                    ? RestService.ShortRest(_characterId, _restDiceToSpend)
                    : RestService.LongRest(_characterId);
            }
            catch (Exception ex)
            {
                ShowToast($"Отдых не получился: {ex.Message}", true);
                return;
            }

            _restApplied = true;
            ApplyRestToWindow(outcome);

            // Список «что вернётся» на отдыхе уже неактуален, а итог ниже
            // подробнее — держать оба незачем
            panelRestHitDice.Visibility = Visibility.Collapsed;
            brRestSummary.Visibility = Visibility.Collapsed;
            panelRestResult.Visibility = Visibility.Visible;
            txtRestResult.Text = DescribeRestOutcome(outcome);

            btnRestCancel.Visibility = Visibility.Collapsed;
            btnRestGo.Content = "ЗАКРЫТЬ";
        }

        /// <summary>Подтягивает окно к тому, что стало после отдыха.</summary>
        private void ApplyRestToWindow(RestService.RestOutcome outcome)
        {
            _maxHp = RestService.LoadMaxHp(_characterId);
            UpdateHPDisplay(RestService.LoadCurrentHp(_characterId));

            // Ячейки и заряды показывают вкладки заклинаний и инвентаря. У них
            // нет общего «перечитать»: проще собрать их заново, чем дёргать
            // каждую — заодно подтянется всё остальное, что могло измениться
            _viewCache.Remove("Spells");
            _viewCache.Remove("Inventory");
            if (_currentViewName == "Spells" || _currentViewName == "Inventory")
            {
                string again = _currentViewName;
                _currentViewName = "";
                SwitchView(again);
            }
        }

        /// <summary>Строка итога после отдыха.</summary>
        private static string DescribeRestOutcome(RestService.RestOutcome outcome)
        {
            var lines = new List<string>();

            if (outcome.Healed > 0) lines.Add($"Восстановлено хитов: {outcome.Healed} (стало {outcome.HpAfter} из {outcome.HpMax}).");
            else lines.Add($"Здоровье: {outcome.HpAfter} из {outcome.HpMax}.");

            if (outcome.HitDiceSpent > 0)
            {
                string rolls = string.Join(" + ", outcome.HitDiceRolls);
                lines.Add($"Потрачено костей хитов: {outcome.HitDiceSpent} ({rolls}).");
            }

            if (outcome.HitDiceRecovered > 0) lines.Add($"Вернулось костей хитов: {outcome.HitDiceRecovered}.");
            if (outcome.SlotsRestored > 0) lines.Add($"Восстановлено ячеек заклинаний: {outcome.SlotsRestored}.");
            if (outcome.ItemsRecharged > 0)
                lines.Add("Заряжены предметы: " + string.Join(", ", outcome.RechargedItems) + ".");

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Что отдых вернёт — список показывается до нажатия, чтобы решение было
        /// осознанным: длинный отдых меняет сразу всё.
        /// </summary>
        private void FillRestSummary(RestService.RestKind kind)
        {
            panelRestSummary.Children.Clear();

            int hp = RestService.LoadCurrentHp(_characterId);
            int max = RestService.LoadMaxHp(_characterId);
            AddRestLine("Здоровье", hp >= max ? $"{hp} из {max} — уже полное" : $"{hp} → {max}");

            if (kind == RestService.RestKind.Long)
            {
                int slots = RestService.CountSpentSlots(_characterId);
                AddRestLine("Ячейки заклинаний", slots == 0 ? "все на месте" : $"вернётся {slots}");

                int items = RestService.CountItemsToRecharge(_characterId, "long");
                AddRestLine("Заряды предметов", items == 0 ? "всё заряжено" : $"вернётся {items}");

                var (_, total, used) = RestService.LoadHitDice(_characterId);
                if (total == 0) AddRestLine("Кости хитов", "не заданы");
                else
                {
                    int back = Math.Min(used, Math.Max(1, total / 2));
                    AddRestLine("Кости хитов", back == 0 ? "запас полный" : $"вернётся {back} из {total}");
                }
            }
            else
            {
                int items = RestService.CountItemsToRecharge(_characterId, "short");
                AddRestLine("Заряды предметов", items == 0 ? "всё заряжено" : $"вернётся {items}");
                AddRestLine("Ячейки заклинаний", "короткий отдых их не возвращает");
            }
        }

        /// <summary>Строка «подпись — значение» в блоках отдыха.</summary>
        private void AddRestLine(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var caption = new TextBlock
            {
                Text = label,
                Foreground = ThemeBrush("B.Mute"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            var text = new TextBlock
            {
                Text = value,
                Foreground = ThemeBrush("B.Ink"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);

            row.Children.Add(caption);
            row.Children.Add(text);
            panelRestSummary.Children.Add(row);
        }

        /// <summary>Сохраняет кость хитов и их количество из калькулятора здоровья.</summary>
        private void BtnSetHitDice_Click(object sender, RoutedEventArgs e)
        {
            int die = 8;
            if (cbHitDie.SelectedItem is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), out int parsedDie)) die = parsedDie;

            if (!int.TryParse(txtHitDiceTotal.Text, out int total) || total < 0)
            {
                ShowToast("Костей хитов — целое число не меньше нуля", true);
                return;
            }

            try
            {
                RestService.SaveHitDiceSettings(_characterId, die, total);
                UpdateHitDiceDisplay();
                ShowToast(total == 0 ? "Кости хитов не заданы" : $"Кость хитов d{die}, всего {total}");
            }
            catch (Exception ex)
            {
                ShowToast($"Не сохранилось: {ex.Message}", true);
            }
        }

        /// <summary>Показывает настройку костей в калькуляторе здоровья.</summary>
        private void UpdateHitDiceDisplay()
        {
            var (die, total, used) = RestService.LoadHitDice(_characterId);

            foreach (ComboBoxItem item in cbHitDie.Items)
                item.IsSelected = item.Tag?.ToString() == die.ToString();
            if (cbHitDie.SelectedIndex < 0 && cbHitDie.Items.Count > 0)
                ((ComboBoxItem)cbHitDie.Items[1]).IsSelected = true;

            txtHitDiceTotal.Text = total.ToString();
            txtHitDiceStatus.Text = total == 0
                ? "Кости не заданы: короткий отдых лечить не будет."
                : $"Потрачено {used} из {total}. Каждая кость лечит 1d{die} + модификатор ТЕЛОСЛОЖЕНИЯ.";
        }

        #endregion

        #region === 14. АДАПТИВНАЯ ВЕРСТКА ===
        /// <summary>
        /// Прячет WebGL-карту, пока поверх окна открыт оверлей. WebView2 —
        /// отдельное окно и всегда выше WPF, поэтому оверлеи «уходили под карту».
        /// </summary>
        private void SyncMapOverlays()
        {
            bool open =
                HPCalcOverlay.Visibility == Visibility.Visible ||
                LevelManagerOverlay.Visibility == Visibility.Visible ||
                PortraitOverlay.Visibility == Visibility.Visible ||
                ProfPopLayer.Children.Count > 0;

            if (open == _mapOverlayOpen) return;
            _mapOverlayOpen = open;

            if (open) Controls.MapWebRenderer.DialogOpened();
            else Controls.MapWebRenderer.DialogClosed();
        }

        private bool _mapOverlayOpen;
        private bool _mapHiddenByOverlay;

        private void ApplyAdaptiveLayout()
        {
            double w = ActualWidth;
            if (w <= 0) return;

            double avatar, fName, fSub, fStatLabel, fStatValue;
            double fTitle, fScore, fMod, fRowText, fRowValue, fSaveText, fBtnText, fTabText;
            double roundBtn, diceBtn, actionH;
            Thickness statPad, headerPad, cardPad, rowPad;

            if (w < 1450)
            {
                avatar = 84; fName = 16; fSub = 12; fStatLabel = 8; fStatValue = 14;
                fTitle = 12; fScore = 30; fMod = 14; fRowText = 11; fRowValue = 12; fSaveText = 12; fBtnText = 11; fTabText = 12;
                roundBtn = 26; diceBtn = 24; actionH = 28;
                statPad = new Thickness(8, 5, 8, 5); headerPad = new Thickness(14, 10, 14, 10);
                cardPad = new Thickness(8); rowPad = new Thickness(6, 4, 6, 4);
            }
            else if (w < 1900)
            {
                avatar = 96; fName = 18; fSub = 13; fStatLabel = 9; fStatValue = 16;
                fTitle = 13; fScore = 36; fMod = 16; fRowText = 12; fRowValue = 13; fSaveText = 13; fBtnText = 12; fTabText = 13;
                roundBtn = 28; diceBtn = 28; actionH = 32;
                statPad = new Thickness(10, 6, 10, 6); headerPad = new Thickness(18, 12, 18, 12);
                cardPad = new Thickness(10); rowPad = new Thickness(8, 6, 8, 6);
            }
            else if (w < 2500)
            {
                avatar = 112; fName = 20; fSub = 14; fStatLabel = 10; fStatValue = 18;
                fTitle = 15; fScore = 44; fMod = 18; fRowText = 14; fRowValue = 15; fSaveText = 14; fBtnText = 13; fTabText = 14;
                roundBtn = 32; diceBtn = 32; actionH = 34;
                statPad = new Thickness(12, 8, 12, 8); headerPad = new Thickness(22, 14, 22, 14);
                cardPad = new Thickness(12); rowPad = new Thickness(10, 7, 10, 7);
            }
            else
            {
                avatar = 128; fName = 22; fSub = 15; fStatLabel = 11; fStatValue = 20;
                fTitle = 16; fScore = 50; fMod = 20; fRowText = 15; fRowValue = 16; fSaveText = 15; fBtnText = 14; fTabText = 15;
                roundBtn = 34; diceBtn = 34; actionH = 36;
                statPad = new Thickness(14, 9, 14, 9); headerPad = new Thickness(26, 16, 26, 16);
                cardPad = new Thickness(14); rowPad = new Thickness(11, 8, 11, 8);
            }

            Resources["SZ.Avatar"] = avatar;
            Resources["FS.Name"] = fName;
            Resources["FS.Sub"] = fSub;
            Resources["FS.StatLabel"] = fStatLabel;
            Resources["FS.StatValue"] = fStatValue;
            Resources["FS.AbilityTitle"] = fTitle;
            Resources["FS.AbilityScore"] = fScore;
            Resources["FS.AbilityMod"] = fMod;
            Resources["FS.RowText"] = fRowText;
            Resources["FS.RowValue"] = fRowValue;
            Resources["FS.SaveText"] = fSaveText;
            Resources["FS.ButtonText"] = fBtnText;
            Resources["FS.TabText"] = fTabText;
            Resources["SZ.RoundBtn"] = roundBtn;
            Resources["SZ.DiceBtn"] = diceBtn;
            Resources["SZ.ActionBtnHeight"] = actionH;
            Resources["TH.StatPadding"] = statPad;
            Resources["TH.HeaderPadding"] = headerPad;
            Resources["TH.AbilityCardPadding"] = cardPad;
            Resources["TH.SkillRowPadding"] = rowPad;
        }
        #endregion
    }
}