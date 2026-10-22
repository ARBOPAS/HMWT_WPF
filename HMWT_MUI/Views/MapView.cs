using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;
using HMWT_MUI.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Вкладка «Карта»: сцены с картинкой, сеткой и жетонами.
    ///
    /// Роли: мастер грузит карту, создаёт сцены, двигает любые жетоны и видит
    /// скрытые. Игрок видит карту, двигает только своего персонажа, скрытых
    /// жетонов не видит вовсе.
    ///
    /// Жетон связан с участником боя, поэтому хиты и состояния у него те же,
    /// что в трекере инициативы — это не копия.
    /// </summary>
    public sealed class MapView : UserControl
    {
        private readonly int _characterId;

        /// <summary>
        /// Роль вкладки спрашиваем у сессии каждый раз: комнату можно создать
        /// или покинуть уже после открытия вкладки, и запомненное однажды
        /// состояние оставляло её «локальной» навсегда.
        /// </summary>
        private bool _inRoom => RoomSession.Instance.IsActive;
        private bool _remote => _inRoom && !RoomSession.Instance.IsHost;
        private bool _isDm => DmRights.CanUseDmTools() && (!_inRoom || RoomSession.Instance.AmIGameMaster);

        /// <summary>Как игрок называется в отряде. Пересчитывается после входа в комнату.</summary>
        private string _selfNameCache;
        private string SelfName => _selfNameCache ??= ResolveSelfName();


        private readonly Border _viewport = new Border();
        private readonly Canvas _world = new Canvas();
        private readonly MapWebRenderer _webMap = new MapWebRenderer();
        private System.Windows.Threading.DispatcherTimer _webSyncTimer;
        private readonly Dictionary<string, string> _webDataUrls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int _webSeenVersion = -1;
        private bool _webMapReady;
        private string _webImageHash = "";
        private readonly Dictionary<int, string> _webTokenPhotoKeys = new Dictionary<int, string>();
        private readonly Image _mapImage = new Image();

        /// <summary>Какой файл карты уже раскодирован: не перечитываем огромный PNG на каждый ход.</summary>
        private string _loadedImagePath;
        private readonly Canvas _gridLayer = new Canvas();
        private readonly Canvas _tokenLayer = new Canvas();

        // содержимое карты: поворот вида применяется к нему, файл не трогаем
        private readonly Canvas _content = new Canvas();
        // туман — фигура, а не прямоугольник: из неё вырезаются обзоры жетонов
        private readonly System.Windows.Shapes.Path _fog = new System.Windows.Shapes.Path();
        private readonly System.Windows.Shapes.Path _fogDark = new System.Windows.Shapes.Path();
        private readonly List<string> _seenPoints = new List<string>();

        /// <summary>
        /// Быстрая проверка «такой сектор уже запоминали» и жёсткий предел
        /// разведанного: без него список рос вместе с каждым шагом
        /// перетаскивания и туман пересобирался по тысячам полигонов.
        /// </summary>
        private readonly HashSet<string> _seenSet = new HashSet<string>(StringComparer.Ordinal);
        private const int MaxSeenSectors = 2000;

        /// <summary>Кэш геометрии разведанного: 240 полигонов не пересобираются на каждый кадр.</summary>
        private readonly Dictionary<string, Geometry> _seenGeometries =
            new Dictionary<string, Geometry>(StringComparer.Ordinal);
        private Matrix _seenGeometryMatrix;

        // 129 отдельных полигонов в CombinedGeometry стоят сотни миллисекунд.
        // Один раз объединяем их в общую фигуру и пересобираем только при
        // добавлении нового сектора.
        private Geometry _seenUnion;
        private int _seenVersion;
        private int _seenUnionVersion = -1;
        private Matrix _seenUnionMatrix;

        /// <summary>Для какой сцены разведанное уже прочитано из базы.</summary>
        private int _seenLoadedSceneId = -1;
        private readonly CheckBox _fogCheck = DmUiKit.Check("Туман", false);

        /// <summary>
        /// Галочку «Туман» ставят и вручную, и при открытии сцены. Флаг отличает
        /// одно от другого: при открытии нельзя заново начинать разведку.
        /// </summary>
        private bool _suppressFogEvents;

        /// <summary>Выбор жетона сам двигает ползунок «Обзор» — сохранять это не нужно.</summary>
        private bool _suppressRevealEvents;

        private Button _shapeButton;

        private readonly Canvas _drawLayer = new Canvas();
        private readonly Canvas _fovLayer = new Canvas();
        private readonly Canvas _trailLayer = new Canvas();
        private readonly Canvas _shapeLayer = new Canvas();
        private readonly Canvas _wallLayer = new Canvas();
        private readonly CheckBox _wallCheck = DmUiKit.Check("Стены", false);
        private Point? _wallStart;
        private System.Windows.Shapes.Line _wallPreview;
        private readonly CheckBox _drawCheck = DmUiKit.Check("Кисть", false);
        private readonly CheckBox _eraserCheck = DmUiKit.Check("Ластик", false);
        private readonly TextBox _brushField = DmUiKit.Field("16", 50, "Толщина");
        private Border _brushSwatch;
        private string _brushColor = "#FFE06A63";
        private List<Point> _strokePoints;
        private System.Windows.Shapes.Polyline _strokePreview;
        private MapShapeData _pendingShape;
        private MapShapeData _dragShape;
        private Point _shapeOffset;
        private int _selectedTokenId;
        private readonly Stack<Action> _undoStack = new Stack<Action>();

        /// <summary>
        /// Раз в две секунды доспрашиваем то, что ещё не пришло: сцену, картинку
        /// карты и портреты жетонов. Без этого «фото через раз» лечилось только
        /// повторным открытием вкладки, а карта могла не появиться вовсе —
        /// запрос уходил один раз, и если ответ терялся, ждать было нечего.
        /// </summary>
        private readonly System.Windows.Threading.DispatcherTimer _retryTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        /// <summary>Были ли мы в комнате на прошлом событии: вход/выход требуют перечитывания.</summary>
        private bool _lastRoomActive;
        private bool _roomSubscribed;

        // Не просим одну и ту же картинку каждые две секунды: повторный запрос
        // запускает вторую передачу файла и забивает канал, из-за чего жетоны
        // начинают ездить с задержкой.
        private readonly Dictionary<string, DateTime> _mapAssetAsked =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private const int MapAssetRetrySeconds = 15;

        // Перетаскивание приходит сотнями MouseMove в секунду. Пересобирать
        // туман и следы на каждый пиксель нельзя: очередь событий растёт,
        // и карта постепенно превращается в 1 FPS.
        private DateTime _lastDragFov = DateTime.MinValue;
        private DateTime _lastTrailRender = DateTime.MinValue;

        // Чужие live-ходы приходят с сетевым джиттером. Копим последний кадр
        // по жетону и применяем его с небольшой задержкой, как в VTT: плавнее
        // и без очереди из сотен промежуточных позиций.
        private sealed class PendingLiveMove
        {
            public double X;
            public double Y;
            public double Facing;
            public DateTime ReceivedAt;
        }

        private readonly Dictionary<int, PendingLiveMove> _pendingLiveMoves =
            new Dictionary<int, PendingLiveMove>();
        private readonly System.Windows.Threading.DispatcherTimer _liveMoveTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        private const int LiveMoveDelayMs = 250;

        // Сглаживание самого жетона: модель уже в новой точке, а картинка
        // доезжает за 160 мс. Иначе при пинге 140 мс жетон прыгает рывками.
        private sealed class VisualMove
        {
            public double FromLeft, FromTop, ToLeft, ToTop;
            public DateTime StartedAt;
        }

        private readonly Dictionary<int, VisualMove> _visualMoves =
            new Dictionary<int, VisualMove>();
        private readonly System.Windows.Threading.DispatcherTimer _visualTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        private const int VisualMoveMs = 240;

        /// <summary>
        /// След жетона: по каким точкам он ехал. Общий для всех, кто видит карту,
        /// и не сохраняется — через несколько секунд после остановки тает.
        /// </summary>
        private readonly Dictionary<int, List<Point>> _trails = new Dictionary<int, List<Point>>();
        private readonly Dictionary<int, DateTime> _trailTouched = new Dictionary<int, DateTime>();
        private readonly System.Windows.Threading.DispatcherTimer _trailTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        /// <summary>Когда последний раз отправляли промежуточное движение при перетаскивании.</summary>
        private DateTime _lastLiveMoveSent = DateTime.MinValue;

        /// <summary>Когда последний раз запоминали обзор по пути (не чаще 3 раз в секунду).</summary>
        private DateTime _lastSeenMemory = DateTime.MinValue;

        /// <summary>Когда последний раз пересчитывали туман по чужому live-ходу.</summary>
        private DateTime _lastLiveFog = DateTime.MinValue;
        private readonly TextBlock _status = new TextBlock();
        private readonly ComboBox _sceneCombo = DmUiKit.Combo(new[] { "—" }, "—", false, 170);
        private readonly TextBox _cellField = DmUiKit.Field("70", 58, "Клетка, px");
        private readonly Slider _cellSlider = new Slider { Width = 110, Minimum = 20, Maximum = 200, TickFrequency = 5, IsSnapToTickEnabled = true };
        private readonly Slider _gridThicknessSlider = new Slider { Width = 76, Minimum = 0.5, Maximum = 4, TickFrequency = 0.5, IsSnapToTickEnabled = true, Value = 1 };
        private readonly Slider _revealSlider = new Slider { Width = 100, Minimum = 1, Maximum = 20, TickFrequency = 1, IsSnapToTickEnabled = true, Value = 6 };
        private readonly TextBlock _revealLabel = new TextBlock { FontSize = 11 };
        private readonly TextBox _shapeField = DmUiKit.Field("4", 52, "Клетки");
        private readonly CheckBox _measureCheck = DmUiKit.Check("Линейка", false);
        private readonly CheckBox _pingCheck = DmUiKit.Check("Пинг", false);
        private readonly Canvas _measureLayer = new Canvas { IsHitTestVisible = false };
        private readonly Canvas _pingLayer = new Canvas { IsHitTestVisible = false };
        private System.Windows.Shapes.Line _measureLine;
        private Border _measureLabel;
        private Point? _measureStart;
        private readonly CheckBox _gridCheck = DmUiKit.Check("Сетка", true);
        private readonly CheckBox _snapCheck = DmUiKit.Check("Привязка", true);
        private readonly CheckBox _showPlayersCheck = DmUiKit.Check("Токены отряда", false);

        private readonly ScaleTransform _zoom = new ScaleTransform(1, 1);
        private readonly TranslateTransform _shift = new TranslateTransform(0, 0);

        private List<MapSceneData> _scenes = new List<MapSceneData>();
        private MapSceneData _scene;
        private Dictionary<int, (EncounterData Encounter, CombatantData Combatant)> _combatants =
            new Dictionary<int, (EncounterData, CombatantData)>();

        private bool _userZoomed;
        private double _scale = 1.0;
        private double _tx, _ty;

        private bool _panning;
        private Point _panStart;
        private double _panTx, _panTy;

        private MapTokenData _dragToken;
        private Point _dragOffset;
        private Border _dragHost;

        public MapView(int characterId)
        {
            _characterId = characterId;

            Focusable = true;
            FocusVisualStyle = null;
            KeyDown += MapView_KeyDown;
            _retryTimer.Tick += OnRetryTick;
            _liveMoveTimer.Tick += ApplyPendingLiveMoves;
            _visualTimer.Tick += ApplyVisualMoves;
            _trailTimer.Tick += (s, e) => RemoveOldTrails();

            BuildShell();

            _webMap.Ready += () =>
            {
                _webMapReady = true;
                _webMap.Visibility = Visibility.Visible;
                _world.Visibility = Visibility.Collapsed;
                _webSyncTimer?.Start();

                // Страница WebView могла перезагрузиться (или впервые открыться),
                // а кэш «что уже отправлено» остался от прошлой. Без сброса карта
                // и портреты не приезжали: C# считал, что они уже в WebView, и
                // картинка оставалась чёрной, а туман рисовался поверх пустоты.
                ResetWebCache();
                PushWebMap();

                try
                {
                    Point origin = _webMap.PointToScreen(new Point(0, 0));
                    WebLog($"web origin {origin.X:0},{origin.Y:0} " +
                           $"size {_webMap.ActualWidth:0}x{_webMap.ActualHeight:0}");
                }
                catch { }

                // Окно могли развернуть уже после старта WebView: для проверок
                // мыши важно знать актуальные экранные координаты полотна.
                _webMap.SizeChanged -= OnWebMapSizeChanged;
                _webMap.SizeChanged += OnWebMapSizeChanged;
            };
            _webMap.Failed += () =>
            {
                _webMapReady = false;
                _webMap.Visibility = Visibility.Collapsed;
                _world.Visibility = Visibility.Visible;
            };
            _webMap.TokenMovedWithTrail += OnWebTokenMoved;
            _webMap.TokenMovedLive += OnWebTokenMovedLive;
            _webMap.ShapePlaced += OnWebShapePlaced;
            _webMap.ShapeMoved += OnWebShapeMoved;
            _webMap.StrokeAdded += OnWebStroke;
            _webMap.WallAdded += OnWebWall;
            _webMap.WallAction += OnWebWallAction;
            _webMap.TokenMenuAction += OnWebTokenMenuAction;
            _webMap.TokenSelected += OnWebTokenSelected;
            _webMap.TrailCleared += OnWebTrailCleared;
            _webMap.ShapeAction += OnWebShapeAction;
            _webMap.ImageNeeded += OnWebImageNeeded;
            _webMap.Pinged += OnWebPinged;

            _webSyncTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _webSyncTimer.Tick += (s, e) => PushWebMap();

            Loaded += (s, e) =>
            {
                SubscribeRoom();
                Refresh();
                _lastRoomActive = _inRoom;
                _retryTimer.Start();
                _webSyncTimer.Start();
            };
            Unloaded += (s, e) =>
            {
                UnsubscribeRoom();
                _retryTimer.Stop();
                _liveMoveTimer.Stop();
                _pendingLiveMoves.Clear();
                _visualTimer.Stop();
                _visualMoves.Clear();
                _trailTimer.Stop();
                _webSyncTimer?.Stop();
                _trails.Clear();
                _trailTouched.Clear();
                _trailLayer.Children.Clear();
            };
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) return;
                // Вкладку могли не выгрузить, а просто скрыть; Loaded в таком
                // случае не срабатывает, и карта оставалась с прошлым кадром.
                SubscribeRoom();
                Refresh();
                _retryTimer.Start();
                if (_webMapReady) _webSyncTimer?.Start();
            };
        }

        private void SubscribeRoom()
        {
            if (_roomSubscribed) return;
            var room = RoomSession.Instance;
            _roomSubscribed = true;
            room.MapChanged += OnMapChanged;
            room.MapImageReady += OnMapImageReady;
            room.MapSeenReset += OnMapSeenReset;
            room.MapSeenAdded += OnMapSeenAdded;
            room.MapSeenSynced += OnMapSeenSynced;
            room.Pinged += OnMapPing;
            room.TokenMovedLive += OnTokenMovedLive;
            room.MapTrailReceived += OnMapTrailReceived;
            room.StateChanged += OnRoomStateChanged;
        }

        private void UnsubscribeRoom()
        {
            if (!_roomSubscribed) return;
            var room = RoomSession.Instance;
            _roomSubscribed = false;
            room.MapChanged -= OnMapChanged;
            room.MapImageReady -= OnMapImageReady;
            room.MapSeenReset -= OnMapSeenReset;
            room.MapSeenAdded -= OnMapSeenAdded;
            room.MapSeenSynced -= OnMapSeenSynced;
            room.Pinged -= OnMapPing;
            room.TokenMovedLive -= OnTokenMovedLive;
            room.MapTrailReceived -= OnMapTrailReceived;
            room.StateChanged -= OnRoomStateChanged;
        }

        /// <summary>Мастер просит открыть существо в бестиарии (карточка жетона).</summary>
        /// <summary>
        /// Как игрок называется в отряде: сначала смотрим себя в комнате,
        /// потом имя первого персонажа. Имя учётной записи Windows не используем.
        /// </summary>
        private string ResolveSelfName()
        {
            try
            {
                var session = RoomSession.Instance;
                if (session != null && session.IsActive)
                {
                    foreach (var player in session.GetPlayers())
                    {
                        if (player == null || player.Id != session.MyPlayerId) continue;
                        if (!string.IsNullOrWhiteSpace(player.CharacterName)) return player.CharacterName.Trim();
                        if (!string.IsNullOrWhiteSpace(player.Name)) return player.Name.Trim();
                    }

                    // В комнате имя берём ТОЛЬКО из отряда. Раньше здесь был
                    // запасной вариант «первый персонаж из своей базы», и клиент
                    // мог считать своим жетон с тем же именем, хотя ведущий
                    // видел его участником без персонажа — ход молча отклонялся.
                    return "";
                }

                using var conn = DatabaseHelper.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM CharacterInfo ORDER BY id_character LIMIT 1";
                var name = cmd.ExecuteScalar();
                if (name != null && name != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(name)))
                    return Convert.ToString(name).Trim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] своё имя: {ex.Message}");
            }

            return "Игрок";
        }

        public event Action<int> OpenMonsterRequested;

        // ══════════════════════ ОБОЛОЧКА ══════════════════════

        private void BuildShell()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

            bar.Children.Add(DmUiKit.Title("КАРТА"));

            var open = DmUiKit.Action("Открыть карту…", (s, e) => PickImage(), DmUiKit.ButtonKind.Primary,
                "Загрузить PNG или JPEG в текущую сцену", 150, 30);
            open.Margin = new Thickness(14, 0, 8, 0);
            open.IsEnabled = _isDm;
            bar.Children.Add(open);

            _sceneCombo.Height = 30;
            _sceneCombo.Margin = new Thickness(0, 0, 8, 0);
            _sceneCombo.SelectionChanged += (s, e) =>
            {
                if (_sceneCombo.SelectedItem is MapSceneData scene && (_scene == null || scene.Id != _scene.Id))
                    OpenScene(scene);
            };
            bar.Children.Add(_sceneCombo);

            var newScene = DmUiKit.Icon("+", (s, e) => NewScene(), "Новая сцена", 30);
            newScene.IsEnabled = _isDm;
            newScene.Margin = new Thickness(0, 0, 6, 0);
            bar.Children.Add(newScene);

            var delScene = DmUiKit.Icon("✕", (s, e) => DeleteScene(), "Удалить текущую сцену", 30, DmUiKit.ButtonKind.Soft);
            delScene.IsEnabled = _isDm;
            delScene.Margin = new Thickness(0, 0, 10, 0);
            bar.Children.Add(delScene);

            // ── сетка: включение, размер клетки, цвет ──
            _gridCheck.Margin = new Thickness(0, 0, 8, 0);
            _gridCheck.IsEnabled = _isDm;
            _gridCheck.Checked += (s, e) => { if (_scene != null) { _scene.GridOn = true; MapService.SaveScene(_scene); PublishMap(); BuildGrid(); } };
            _gridCheck.Unchecked += (s, e) => { if (_scene != null) { _scene.GridOn = false; MapService.SaveScene(_scene); PublishMap(); BuildGrid(); } };
            bar.Children.Add(_gridCheck);

            _snapCheck.Margin = new Thickness(0, 0, 8, 0);
            _snapCheck.IsEnabled = _isDm;
            _snapCheck.ToolTip = "Привязывать жетоны к клеткам. Сетку можно оставить видимой и двигаться свободно.";
            _snapCheck.Checked += (s, e) =>
            {
                if (_scene == null || !_isDm) return;
                _scene.SnapToGrid = true;
                MapService.SaveScene(_scene);
                PublishMap();
            };
            _snapCheck.Unchecked += (s, e) =>
            {
                if (_scene == null || !_isDm) return;
                _scene.SnapToGrid = false;
                MapService.SaveScene(_scene);
                PublishMap();
            };
            bar.Children.Add(_snapCheck);

            _showPlayersCheck.Margin = new Thickness(0, 0, 8, 0);
            _showPlayersCheck.ToolTip = "Показывать жетоны игроков даже вне вашего обзора";
            _showPlayersCheck.Checked += (s, e) => RefreshTokenVisibility();
            _showPlayersCheck.Unchecked += (s, e) => RefreshTokenVisibility();
            bar.Children.Add(_showPlayersCheck);

            _cellSlider.Margin = new Thickness(0, 0, 6, 0);
            _cellSlider.VerticalAlignment = VerticalAlignment.Center;
            _cellSlider.ToolTip = "Размер клетки: тяните, чтобы подогнать под сетку карты";
            _cellSlider.IsEnabled = _isDm;
            _cellSlider.ValueChanged += (s, e) =>
            {
                if (_scene == null || !_isDm) return;
                if (Math.Abs(_scene.GridPx - _cellSlider.Value) < 0.5) return;

                _scene.GridPx = _cellSlider.Value;
                MapService.SaveScene(_scene);
                PublishMap();
                _cellField.Text = ((int)Math.Round(_cellSlider.Value)).ToString();
                BuildGrid();
                RebuildTokens();
                BuildFogMask();
            };
            bar.Children.Add(_cellSlider);

            // Толщина линий сетки: на светлых картах тонкая линия почти не видна.
            _gridThicknessSlider.Margin = new Thickness(0, 0, 8, 0);
            _gridThicknessSlider.VerticalAlignment = VerticalAlignment.Center;
            _gridThicknessSlider.ToolTip = "Толщина линий сетки";
            _gridThicknessSlider.IsEnabled = _isDm;
            _gridThicknessSlider.ValueChanged += (s, e) =>
            {
                if (_scene == null || !_isDm) return;
                double value = Math.Max(0.5, Math.Min(4, _gridThicknessSlider.Value));
                if (Math.Abs(_scene.GridThickness - value) < 0.01) return;

                _scene.GridThickness = value;
                MapService.SaveScene(_scene);
                PublishMap();
                BuildGrid();
            };
            bar.Children.Add(_gridThicknessSlider);

            _cellField.Width = 58;
            _cellField.Margin = new Thickness(0, 0, 4, 0);
            _cellField.IsEnabled = _isDm;
            _cellField.ToolTip = "Размер клетки в точках картинки: подгоните под готовую сетку карты";
            _cellField.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ApplyGridSize(); e.Handled = true; } };
            _cellField.LostFocus += (s, e) => ApplyGridSize();
            bar.Children.Add(_cellField);

            var gridColor = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Цвет сетки",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                Background = DmUiKit.B("Ink")
            };
            gridColor.Content = swatch;
            gridColor.Template = FlatButtonTemplate();
            gridColor.IsEnabled = _isDm;
            gridColor.Click += (s, e) => OpenGridColorMenu(gridColor, swatch);
            bar.Children.Add(gridColor);

            bar.Children.Add(DmUiKit.Action("Вписать", (s, e) => FitToWindow(), DmUiKit.ButtonKind.Ghost,
                "Показать карту целиком", 90, 30));
            bar.Children.Add(DmUiKit.Icon("−", (s, e) => ZoomAtCenter(1 / 1.2), "Отдалить", 30, DmUiKit.ButtonKind.Soft));
            bar.Children.Add(DmUiKit.Icon("+", (s, e) => ZoomAtCenter(1.2), "Приблизить", 30, DmUiKit.ButtonKind.Soft));

            // ── ориентация вида: файл не трогаем, поворачиваем показ ──
            var rotLeft = DmUiKit.Icon("⟲", (s, e) => RotateView(-90), "Повернуть вид влево", 30, DmUiKit.ButtonKind.Soft);
            rotLeft.Margin = new Thickness(10, 0, 4, 0);
            rotLeft.IsEnabled = _isDm;
            bar.Children.Add(rotLeft);

            var rotRight = DmUiKit.Icon("⟳", (s, e) => RotateView(90), "Повернуть вид вправо", 30, DmUiKit.ButtonKind.Soft);
            rotRight.Margin = new Thickness(0, 0, 4, 0);
            rotRight.IsEnabled = _isDm;
            bar.Children.Add(rotRight);

            var flipH = DmUiKit.Icon("⇋", (s, e) => FlipView(true), "Отразить по горизонтали", 30, DmUiKit.ButtonKind.Soft);
            flipH.Margin = new Thickness(0, 0, 4, 0);
            flipH.IsEnabled = _isDm;
            bar.Children.Add(flipH);

            var flipV = DmUiKit.Icon("⇅", (s, e) => FlipView(false), "Отразить по вертикали", 30, DmUiKit.ButtonKind.Soft);
            flipV.Margin = new Thickness(0, 0, 10, 0);
            flipV.IsEnabled = _isDm;
            bar.Children.Add(flipV);

            _fogCheck.Margin = new Thickness(0, 0, 10, 0);
            _fogCheck.IsEnabled = _isDm;
            _fogCheck.Checked += (s, e) => { if (!_suppressFogEvents) SetFog(true); };
            _fogCheck.Unchecked += (s, e) => { if (!_suppressFogEvents) SetFog(false); };
            bar.Children.Add(_fogCheck);

            // ── фигуры областей ──
            _shapeButton = DmUiKit.Action("Фигура…", (s, e) => OpenShapeMenu(_shapeButton), DmUiKit.ButtonKind.Ghost,
                "Круг, квадрат, конус или линия: выберите тип, затем щёлкните по карте", 96, 30);
            _shapeButton.Margin = new Thickness(0, 0, 10, 0);
            _shapeButton.IsEnabled = _isDm || _remote;
            bar.Children.Add(_shapeButton);

            _shapeField.Margin = new Thickness(0, 0, 10, 0);
            _shapeField.VerticalAlignment = VerticalAlignment.Center;
            _shapeField.ToolTip = "Размер новой фигуры в клетках (число)";
            _shapeField.IsEnabled = _isDm || _remote;
            bar.Children.Add(_shapeField);

            Button trailColor = null;
            trailColor = DmUiKit.Action("Цвет следа", (s, e) => OpenTrailColorMenu(trailColor),
                DmUiKit.ButtonKind.Ghost, "Цвет линии, остающейся за жетоном", 110, 30);
            trailColor.Margin = new Thickness(0, 0, 10, 0);
            trailColor.IsEnabled = _isDm;
            bar.Children.Add(trailColor);

            // ── линейка: протяните мышью и увидите клетки/футы ──
            _measureCheck.Margin = new Thickness(0, 0, 10, 0);
            _measureCheck.ToolTip = "Протяните мышью по карте — покажет расстояние в клетках и футах";
            _measureCheck.Checked += (s, e) =>
            {
                _drawCheck.IsChecked = false;
                _wallCheck.IsChecked = false;
                _pingCheck.IsChecked = false;
                _pendingShape = null;
                ClearMeasure();
                _viewport.Cursor = Cursors.Cross;
            };
            _measureCheck.Unchecked += (s, e) =>
            {
                ClearMeasure();
                _viewport.Cursor = Cursors.Arrow;
            };
            bar.Children.Add(_measureCheck);

            // ── пинг: точка, которую видят все в комнате ──
            _pingCheck.Margin = new Thickness(0, 0, 10, 0);
            _pingCheck.ToolTip = "Щёлкните по карте — все увидят пульс в этом месте";
            _pingCheck.Checked += (s, e) =>
            {
                _drawCheck.IsChecked = false;
                _wallCheck.IsChecked = false;
                _measureCheck.IsChecked = false;
                _pendingShape = null;
                _viewport.Cursor = Cursors.Cross;
            };
            _pingCheck.Unchecked += (s, e) => _viewport.Cursor = Cursors.Arrow;
            bar.Children.Add(_pingCheck);

            // ── стены: ломают обзор, за ними туман не рассеивается ──
            // ── дальность обзора выделенного жетона ──
            _revealLabel.Text = "Обзор";
            _revealLabel.Foreground = DmUiKit.B("Mute");
            _revealLabel.VerticalAlignment = VerticalAlignment.Center;
            _revealLabel.Margin = new Thickness(0, 0, 4, 0);
            bar.Children.Add(_revealLabel);

            _revealSlider.Margin = new Thickness(0, 0, 10, 0);
            _revealSlider.VerticalAlignment = VerticalAlignment.Center;
            _revealSlider.ToolTip = "Дальность обзора выделенного жетона, клетки";
            _revealSlider.IsEnabled = _isDm || _remote;
            _revealSlider.ValueChanged += (s, e) =>
            {
                if (_suppressRevealEvents) return;
                if (_scene == null) return;
                var selected = _scene.Tokens.FirstOrDefault(x => x.Id == _selectedTokenId);
                if (selected == null) return;
                if (_remote && !CanMoveToken(selected)) return;
                if (Math.Abs(selected.RevealCells - _revealSlider.Value) < 0.5) return;

                selected.RevealCells = _revealSlider.Value;

                if (_isDm)
                {
                    MapService.SaveToken(selected);
                    PublishMap();
                }
                else
                {
                    // Игрок меняет обзор своего жетона: хост применяет и рассылает
                    RoomSession.Instance.SendMapMove(selected.Id, selected.X, selected.Y,
                        selected.Facing, selected.ViewAngle, (int)selected.SizeCells,
                        selected.RevealCells);
                }

                RenderFov();
                RememberCurrentVision();
                BuildFogMask();
                PushWebMap();
            };
            bar.Children.Add(_revealSlider);

            _wallCheck.Margin = new Thickness(0, 0, 10, 0);
            _wallCheck.IsEnabled = _isDm;
            _wallCheck.ToolTip = "Тяните мышью по карте, чтобы нарисовать стену. ПКМ по стене — убрать.";
            _wallCheck.Checked += (s, e) => { _wallStart = null; _viewport.Cursor = Cursors.Cross; };
            _wallCheck.Unchecked += (s, e) => { _wallStart = null; _viewport.Cursor = Cursors.Arrow; };
            bar.Children.Add(_wallCheck);

            // ── кисть ──
            _drawCheck.Margin = new Thickness(0, 0, 6, 0);
            _drawCheck.IsEnabled = _isDm || _remote;
            _drawCheck.Checked += (s, e) => { _strokePoints = null; _viewport.Cursor = Cursors.Pen; };
            _drawCheck.Unchecked += (s, e) => { _strokePoints = null; _eraserCheck.IsChecked = false; _viewport.Cursor = Cursors.Arrow; };
            bar.Children.Add(_drawCheck);

            _eraserCheck.Margin = new Thickness(0, 0, 6, 0);
            _eraserCheck.IsEnabled = _isDm || _remote;
            _eraserCheck.Checked += (s, e) => { if (_drawCheck.IsChecked != true) _drawCheck.IsChecked = true; };
            bar.Children.Add(_eraserCheck);

            var brushColor = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Цвет кисти",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsEnabled = _isDm || _remote,
                Template = FlatButtonTemplate()
            };
            _brushSwatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x63))
            };
            brushColor.Content = _brushSwatch;
            brushColor.Click += (s, e) => OpenBrushColorMenu(brushColor);
            System.Windows.Automation.AutomationProperties.SetAutomationId(brushColor, "mapBrushColor");
            System.Windows.Automation.AutomationProperties.SetName(brushColor, "Цвет кисти");
            bar.Children.Add(brushColor);

            _brushField.Width = 50;
            _brushField.Margin = new Thickness(0, 0, 6, 0);
            _brushField.IsEnabled = _isDm || _remote;
            _brushField.ToolTip = "Толщина кисти в точках картинки";
            bar.Children.Add(_brushField);

            var clearDraw = DmUiKit.Action("Очистить", (s, e) => ClearStrokes(), DmUiKit.ButtonKind.Ghost,
                "Убрать все рисунки с этой сцены", 96, 30);
            clearDraw.Margin = new Thickness(0, 0, 10, 0);
            clearDraw.IsEnabled = _isDm;
            bar.Children.Add(clearDraw);

            var tokens = DmUiKit.Action("Токены из боя", (s, e) => SyncTokens(), DmUiKit.ButtonKind.Ghost,
                "Поставить жетоны всех участников текущего боя", 150, 30);
            tokens.Margin = new Thickness(10, 0, 0, 0);
            tokens.IsEnabled = _isDm;
            bar.Children.Add(tokens);

            _status.FontSize = 11;
            _status.Foreground = DmUiKit.B("Mute");
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(12, 0, 0, 0);
            bar.Children.Add(_status);

            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            // ── полотно ──
            _viewport.Background = DmUiKit.B("Void");
            _viewport.ClipToBounds = true;
            System.Windows.Automation.AutomationProperties.SetAutomationId(_viewport, "mapViewport");
            System.Windows.Automation.AutomationProperties.SetName(_viewport, "Поле карты");

            var group = new TransformGroup();
            group.Children.Add(_zoom);
            group.Children.Add(_shift);
            _world.RenderTransform = group;
            _world.RenderTransformOrigin = new Point(0, 0);
            _world.HorizontalAlignment = HorizontalAlignment.Left;
            _world.VerticalAlignment = VerticalAlignment.Top;

            _mapImage.Stretch = Stretch.Fill;
            _mapImage.IsHitTestVisible = false;

            _content.HorizontalAlignment = HorizontalAlignment.Left;
            _content.VerticalAlignment = VerticalAlignment.Top;
            _content.RenderTransformOrigin = new Point(0, 0);
            _content.Children.Add(_mapImage);
            _content.Children.Add(_gridLayer);
            _content.Children.Add(_wallLayer);
            _content.Children.Add(_drawLayer);
            _content.Children.Add(_fovLayer);
            _content.Children.Add(_trailLayer);
            // Фигуры под жетонами: жетон игрока всегда можно схватить.
            _content.Children.Add(_shapeLayer);
            _content.Children.Add(_tokenLayer);
            _world.Children.Add(_content);

            _fog.IsHitTestVisible = false;
            // Туман — сплошная заливка без анимации: кэшируем её в битмап,
            // иначе WPF каждый кадр перерисовывал сотни полигонов-исключений.
            _fog.CacheMode = new BitmapCache();
            _fogDark.CacheMode = new BitmapCache();
            _fog.Visibility = Visibility.Collapsed;
            _world.Children.Add(_fog);
            _world.Children.Add(_fogDark);

            // Линейка — отдельный слой поверх карты и тумана, в координатах
            // окна: так подпись не масштабируется вместе с картой и всегда видна.
            var viewportHost = new Grid();
            viewportHost.Children.Add(_world);
            viewportHost.Children.Add(_measureLayer);
            viewportHost.Children.Add(_pingLayer);
            viewportHost.Children.Add(_webMap);
            _viewport.Child = viewportHost;
            _viewport.SizeChanged += (s, e) => { if (!_userZoomed) FitToWindow(); };
            _viewport.MouseWheel += Viewport_MouseWheel;
            _viewport.MouseLeftButtonDown += (s, e) => Focus();
            _viewport.MouseLeftButtonDown += Viewport_MouseDown;
            _viewport.MouseMove += Viewport_MouseMove;
            _viewport.MouseLeftButtonUp += Viewport_MouseUp;
            _viewport.MouseLeave += (s, e) => { _panning = false; _viewport.Cursor = Cursors.Arrow; };

            var card = DmUiKit.Panel(_viewport, new Thickness(0), DmUiKit.B("Panel"), DmUiKit.B("Line"), 8);
            Grid.SetRow(card, 1);
            root.Children.Add(card);

            Content = root;
        }

        private static ControlTemplate FlatButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "content");
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            template.VisualTree = border;
            return template;
        }

        // ══════════════════════ ДАННЫЕ ══════════════════════

        private void PushWebMap()
        {
            if (!_webMapReady || _scene == null) return;

            try
            {
                string imagePath = string.IsNullOrWhiteSpace(_scene.ImageHash)
                    ? null
                    : (_remote ? RemoteImagePath(_scene.ImageHash) : MapService.ResolveImagePath(_scene));
                string imageHash = _scene.ImageHash ?? "";
                bool sendImage = !string.Equals(imageHash, _webImageHash, StringComparison.OrdinalIgnoreCase);
                string image = sendImage ? WebAssetUrl(imagePath) : null;
                bool includeSeen = _webSeenVersion != _seenVersion;

                var tokens = new List<object>();
                foreach (var t in _scene.Tokens)
                {
                    string photoPath = ResolveTokenPhoto(t);
                    string photoKey = photoPath ?? "";
                    bool sendPhoto = !_webTokenPhotoKeys.TryGetValue(t.Id, out var oldPhoto) ||
                                     !string.Equals(oldPhoto, photoKey, StringComparison.OrdinalIgnoreCase);
                    if (sendPhoto) _webTokenPhotoKeys[t.Id] = photoKey;

                    tokens.Add(new
                    {
                        id = t.Id,
                        label = t.Label ?? "",
                        x = t.X,
                        y = t.Y,
                        sizeCells = t.SizeCells,
                        color = t.Color ?? "",
                        isPlayer = t.IsPlayer,
                        hidden = t.Hidden,
                        selected = t.Id == _selectedTokenId,
                        canMove = CanMoveToken(t),
                        frameColor = t.FrameColor ?? "",
                        // null — аватар не менялся; "" — аватара нет.
                        photo = sendPhoto ? WebAssetUrl(photoPath) : null
                    });
                }

                var payload = new
                {
                    type = "scene",
                    sceneId = _scene.Id,
                    width = _scene.WidthPx > 0 ? _scene.WidthPx : 1,
                    height = _scene.HeightPx > 0 ? _scene.HeightPx : 1,
                    imageHash,
                    image,
                    isDm = _isDm,
                    rotation = _scene.Rotation,
                    flipH = _scene.FlipH,
                    flipV = _scene.FlipV,
                    grid = new
                    {
                        on = _scene.GridOn,
                        px = _scene.GridPx,
                        offsetX = _scene.GridOffsetX,
                        offsetY = _scene.GridOffsetY,
                        thickness = _scene.GridThickness,
                        color = _scene.GridColor,
                        snap = _scene.SnapToGrid
                    },
                    fog = new
                    {
                        on = WebFogEnabled(),
                        seen = includeSeen ? _seenPoints.ToList() : null
                    },
                    trailColor = string.IsNullOrWhiteSpace(_scene.TrailColor)
                        ? "#FF9B8BFF" : _scene.TrailColor,
                    fovTokens = WebFovTokens(),
                    tool = new
                    {
                        shape = _pendingShape == null ? null : _pendingShape.Kind,
                        shapeRadius = _pendingShape == null ? 0 : _pendingShape.RadiusCells,
                        ping = _pingCheck.IsChecked == true,
                        measure = _measureCheck.IsChecked == true,
                        brush = _drawCheck.IsChecked == true,
                        eraser = _eraserCheck.IsChecked == true,
                        wall = _wallCheck.IsChecked == true,
                        brushColor = _brushColor,
                        brushThickness = BrushThickness()
                    },
                    strokes = _scene.Strokes.Select(s => new
                    {
                        id = s.Id,
                        color = s.Color,
                        thickness = s.Thickness,
                        points = s.Points
                    }).ToList(),
                    trails = _trails.Select(pair => new
                    {
                        id = pair.Key,
                        points = pair.Value.Select(p => new { x = p.X, y = p.Y }).ToList()
                    }).ToList(),
                    tokens,
                    shapes = _scene.Shapes.Select(s => new
                    {
                        id = s.Id,
                        kind = s.Kind,
                        x = s.X,
                        y = s.Y,
                        radius = s.RadiusCells,
                        angle = s.Angle,
                        color = s.Color,
                        label = s.Label ?? ""
                    }).ToList(),
                    walls = _scene.Walls.Select(w => new
                    {
                        id = w.Id,
                        x1 = w.X1,
                        y1 = w.Y1,
                        x2 = w.X2,
                        y2 = w.Y2,
                        kind = w.Kind,
                        isOpen = w.IsOpen
                    }).ToList()
                };

                string json = System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });

                _webMap.SendState(json);
                if (includeSeen) _webSeenVersion = _seenVersion;
                if (sendImage) _webImageHash = imageHash;
            }
            catch (Exception ex)
            {
                WebLog("state error: " + ex);
                Debug.WriteLine("[MapWeb] state: " + ex.Message);
            }
        }

        private bool WebFogEnabled()
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("HMWT_DEBUG_NOFOG") == "1") return false;
#endif
            return _scene != null && _scene.FogOn;
        }

        private static void WebLog(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "hmwt-map-web.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
            }
            catch { }
        }

        private static readonly System.Text.Json.JsonSerializerOptions WebJsonOptions =
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        /// <summary>Секущие области обзора — их WebView и вырезает из тумана.</summary>
        private List<object> WebFovTokens()
        {
            var list = new List<object>();
            if (_scene?.Tokens == null) return list;

            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);

            foreach (var t in _scene.Tokens)
            {
                if (t == null || t.Hidden || t.RevealCells <= 0) continue;
                list.Add(new
                {
                    id = t.Id,
                    x = t.X,
                    y = t.Y,
                    facing = t.Facing,
                    viewAngle = t.ViewAngle,
                    revealCells = t.RevealCells,
                    isPlayer = t.IsPlayer,
                    // Развеивает ли этот жетон туман именно для смотрящего:
                    // у игрока — отряд или только свой, у мастера — отряд.
                    reveals = RevealsFog(t, combined),
                    // «Свой» жетон: у мастера своих нет (он видит всё, но
                    // приглушает скрытое), у игрока — только его персонаж.
                    mine = !_isDm && CanMoveToken(t)
                });
            }

            return list;
        }

        /// <summary>
        /// Лёгкое обновление тумана: только «разведанное» и текущий обзор.
        /// Полную сцену на каждом шаге перетаскивания слать нельзя — там следы,
        /// жетоны и портреты, а нужно лишь новое вырезанное пятно.
        /// </summary>
        private void PushWebFog()
        {
            if (!_webMapReady || _scene == null) return;

            try
            {
                var fov = WebFovTokens();
                var payload = new
                {
                    type = "fog",
                    sceneId = _scene.Id,
                    isDm = _isDm,
                    grid = new { px = _scene.GridPx },
                    fog = new { on = WebFogEnabled(), seen = _seenPoints.ToList() },
                    fovTokens = fov
                };

                _webMap.SendState(System.Text.Json.JsonSerializer.Serialize(payload, WebJsonOptions));
                _webSeenVersion = _seenVersion;
                WebLog($"fog push seen={_seenPoints.Count} fov={fov.Count}");
            }
            catch (Exception ex)
            {
                WebLog("fog state error: " + ex.Message);
            }
        }

        /// <summary>После перезагрузки WebView всё, что «уже отправлено», нужно слать заново.</summary>
        private void ResetWebCache()
        {
            _webImageHash = "";
            _webTokenPhotoKeys.Clear();
            _webDataUrls.Clear();
            _webSeenVersion = -1;
        }

        /// <summary>Полотно WebView переехало или изменило размер — пишем его экранные координаты.</summary>
        private void OnWebMapSizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                Point origin = _webMap.PointToScreen(new Point(0, 0));
                WebLog($"web origin {origin.X:0},{origin.Y:0} " +
                       $"size {_webMap.ActualWidth:0}x{_webMap.ActualHeight:0}");
            }
            catch { }
        }

        private void OnWebImageNeeded()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnWebImageNeeded));
                return;
            }

            ResetWebCache();
            PushWebMap();
        }

        private void OnWebTokenMovedLive(int tokenId, double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokenMovedLive(tokenId, x, y)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null || !CanMoveToken(token)) return;

            if (_scene.SnapToGrid)
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                x = Math.Round((x - cell / 2) / cell) * cell + cell / 2;
                y = Math.Round((y - cell / 2) / cell) * cell + cell / 2;
            }

            token.X = Math.Max(0, Math.Min(_scene.WidthPx, x));
            token.Y = Math.Max(0, Math.Min(_scene.HeightPx, y));

            AppendTrail(token, new Point(token.X, token.Y));

            if ((DateTime.UtcNow - _lastLiveMoveSent).TotalMilliseconds >= 200)
            {
                _lastLiveMoveSent = DateTime.UtcNow;
                if (_remote)
                    RoomSession.Instance.SendMapMoveLive(token.Id, token.X, token.Y, token.Facing);
                else if (_inRoom)
                    RoomSession.Instance.PublishLiveTokenMove(token.Id, token.X, token.Y, token.Facing);
            }

            // Разведка «на ходу»: новый сектор появляется сразу, а не после
            // отпускания кнопки. В базу и сеть пишем не чаще 3 раз в секунду.
            if ((DateTime.UtcNow - _lastLiveFog).TotalMilliseconds >= 320)
            {
                _lastLiveFog = DateTime.UtcNow;
                if (WebFogEnabled())
                {
                    RememberCurrentVision();
                    PushWebFog();
                }
            }

            if (!_webMapReady) { BuildFogMask(); RenderFov(); }
        }

        private void OnWebShapeMoved(int shapeId, double x, double y, bool commit)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebShapeMoved(shapeId, x, y, commit)));
                return;
            }

            if (_scene == null || (!_isDm && !_remote)) return;
            var shape = _scene.Shapes.FirstOrDefault(s => s.Id == shapeId);
            if (shape == null) return;

            if (_scene.SnapToGrid)
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                x = Math.Round((x - cell / 2) / cell) * cell + cell / 2;
                y = Math.Round((y - cell / 2) / cell) * cell + cell / 2;
            }

            shape.X = x;
            shape.Y = y;
            RenderShapes();
            if (!commit) return;

            CommitShape(shape, "update");
            PushWebMap();
        }

        private void OnWebStroke(List<MapTrailPoint> points, string color, double thickness, bool eraser)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebStroke(points, color, thickness, eraser)));
                return;
            }

            if (_scene == null || points == null || points.Count == 0) return;

            var path = points.Where(p => p != null).Select(p => new Point(p.X, p.Y)).ToList();
            if (path.Count == 0) return;

            if (eraser)
            {
                // Ластик прощает промах: радиус вдвое больше кисти, иначе на
                // карте 5000 px линию толщиной 16 почти невозможно задеть.
                EraseAt(path, Math.Max(12, thickness * 2));
                PushWebMap();
                return;
            }

            var stroke = new MapStrokeData
            {
                SceneId = _scene.Id,
                Color = string.IsNullOrWhiteSpace(color) ? _brushColor : color.Trim(),
                Thickness = Math.Max(1, Math.Min(80, thickness <= 0 ? BrushThickness() : thickness)),
                Points = PointsToString(path)
            };

            if (_remote)
            {
                // У игрока нет базы ведущего: рисуем у себя и просим сохранить.
                stroke.Id = -Math.Abs(Guid.NewGuid().GetHashCode());
                _scene.Strokes.Add(stroke);
                RoomSession.Instance.SendMapStroke(stroke);
            }
            else if (MapService.SaveStroke(stroke) > 0)
            {
                _scene.Strokes.Add(stroke);
                PublishMap();
                var saved = stroke;
                PushUndo(() =>
                {
                    MapService.DeleteStroke(saved.Id);
                    _scene?.Strokes.RemoveAll(s => s.Id == saved.Id);
                    PublishMap();
                    PushWebMap();
                });
            }

            RenderStrokes();
            PushWebMap();
        }

        private void OnWebWall(double x1, double y1, double x2, double y2, string kind)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebWall(x1, y1, x2, y2, kind)));
                return;
            }

            if (_scene == null || !_isDm) return;

            Point start = SnapWallPoint(new Point(x1, y1));
            Point end = SnapWallPoint(new Point(x2, y2));

            // Стена за пределами картинки бесполезна: обзор и туман считаются
            // в её координатах, поэтому прижимаем концы к полю карты.
            double maxX = Math.Max(1, _scene.WidthPx);
            double maxY = Math.Max(1, _scene.HeightPx);
            start = new Point(Math.Max(0, Math.Min(maxX, start.X)),
                              Math.Max(0, Math.Min(maxY, start.Y)));
            end = new Point(Math.Max(0, Math.Min(maxX, end.X)),
                            Math.Max(0, Math.Min(maxY, end.Y)));

            if (Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) < 8) return;

            var wall = new MapWallData
            {
                SceneId = _scene.Id,
                X1 = start.X,
                Y1 = start.Y,
                X2 = end.X,
                Y2 = end.Y,
                Kind = string.Equals(kind, "door", StringComparison.OrdinalIgnoreCase) ? "door" : "wall"
            };

            if (MapService.SaveWall(wall) > 0)
            {
                _scene.Walls.Add(wall);
                var saved = wall;
                PushUndo(() =>
                {
                    MapService.DeleteWall(saved.Id);
                    _scene?.Walls.RemoveAll(w => w.Id == saved.Id);
                    PublishMap();
                    PushWebMap();
                });
                PublishMap();
                RenderWalls();
                BuildFogMask();
                PushWebMap();
            }
        }

        private void OnWebWallAction(int wallId, string action)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebWallAction(wallId, action)));
                return;
            }

            if (_scene == null || !_isDm) return;
            var wall = _scene.Walls.FirstOrDefault(w => w.Id == wallId);
            if (wall == null) return;
            WebLog($"wall action {wallId} {action}");

            if (string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase))
            {
                MapService.DeleteWall(wall.Id);
                _scene.Walls.RemoveAll(w => w.Id == wall.Id);
            }
            else
            {
                wall.IsOpen = !wall.IsOpen;
                MapService.UpdateWall(wall);
            }

            PublishMap();
            RenderWalls();
            BuildFogMask();
            PushWebMap();
        }

        /// <summary>Меню жетона в WebView: размер, обзор, поворот, рамка, удаление.</summary>
        private void OnWebTokenMenuAction(int tokenId, string action, string value)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokenMenuAction(tokenId, action, value)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null) return;

            // Игроку доступен только свой жетон, и то без ведущих действий.
            bool mine = CanMoveToken(token);
            WebLog($"token menu {tokenId} {action} '{value}' dm={_isDm} mine={mine}");
            if (!_isDm && !mine) return;

            switch ((action ?? "").ToLowerInvariant())
            {
                case "size":
                    if (!int.TryParse(value, out int cells)) return;
                    cells = Math.Max(1, Math.Min(8, cells));
                    if (_remote) SetRemoteTokenSize(token, cells);
                    else SetTokenSize(token, cells);
                    break;

                case "viewangle":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double angle)) return;
                    angle = Math.Max(5, Math.Min(360, angle));
                    if (_isDm) SetViewAngle(token, angle);
                    else SetRemoteViewAngle(token, angle);
                    break;

                case "facing":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double delta)) return;
                    if (_isDm) RotateFacing(token, delta);
                    else RotateFacingRemote(token, delta);
                    break;

                case "frame":
                    if (!_isDm) return;
                    SetTokenFrame(token, value);
                    break;

                case "reveal":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double reveal)) return;
                    reveal = Math.Max(1, Math.Min(40, reveal));
                    if (_isDm)
                    {
                        SetRevealCells(token, reveal);
                    }
                    else
                    {
                        // Игрок меняет дальность своего обзора — хост применит и разошлёт
                        token.RevealCells = reveal;
                        RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y,
                            double.NaN, -1, -1, reveal);
                        RenderFov();
                        RememberCurrentVision();
                        BuildFogMask();
                    }
                    break;

                case "remove":
                    if (!_isDm) return;
                    MapService.DeleteToken(token.Id);
                    _scene.Tokens.RemoveAll(t => t.Id == token.Id);
                    PublishMap();
                    RebuildTokens();
                    break;

                default:
                    return;
            }

            PushWebMap();
        }

        private void SetTokenFrame(MapTokenData token, string color)
        {
            if (token == null) return;
            token.FrameColor = string.IsNullOrWhiteSpace(color) ? "" : color.Trim();
            if (!_remote)
            {
                MapService.SaveToken(token);
                PublishMap();
            }
            PushWebMap();
        }

        /// <summary>
        /// Жетон выбрали на карте WebGL. Ползунок «Обзор» в панели двигает
        /// именно выделенный жетон, поэтому без этого он ничего не делал.
        /// </summary>
        private void OnWebTokenSelected(int tokenId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokenSelected(tokenId)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null) return;

            SelectToken(token);
            PushWebMap();
        }

        /// <summary>Жетон начали вести заново — старый маршрут стираем у всех.</summary>
        private void OnWebTrailCleared(int tokenId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTrailCleared(tokenId)));
                return;
            }

            ClearTrail(tokenId);

            // Пустой список — сигнал «след стёрт»: у клиентов он тоже убирается
            if (_inRoom) RoomSession.Instance.PublishMapTrail(tokenId, new List<MapTrailPoint>());
            PushWebMap();
        }

        /// <summary>Поворот, размер, цвет и удаление фигуры из меню WebGL.</summary>
        private void OnWebShapeAction(int shapeId, string action, string value)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebShapeAction(shapeId, action, value)));
                return;
            }

            if (_scene == null || (!_isDm && !_remote)) return;
            var shape = _scene.Shapes.FirstOrDefault(s => s.Id == shapeId);
            if (shape == null) return;

            WebLog($"shape action {shapeId} {action} '{value}'");

            switch ((action ?? "").ToLowerInvariant())
            {
                case "rotate":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double delta)) return;
                    shape.Angle = ((shape.Angle + delta) % 360 + 360) % 360;
                    break;

                case "radius":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double step)) return;
                    shape.RadiusCells = Math.Max(0.5, Math.Min(20, shape.RadiusCells + step));
                    break;

                case "color":
                    if (string.IsNullOrWhiteSpace(value)) return;
                    shape.Color = value.Trim();
                    break;

                case "delete":
                    CommitShape(shape, "delete");
                    PushWebMap();
                    return;

                default:
                    return;
            }

            CommitShape(shape, "update");
            PushWebMap();
        }

        private void OnWebShapePlaced(double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebShapePlaced(x, y)));
                return;
            }
            if (_scene == null || _pendingShape == null || (!_isDm && !_remote)) return;
            PlaceShape(new Point(x, y));
            PushWebMap();
        }

        private void OnWebPinged(double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebPinged(x, y)));
                return;
            }
            if (_scene == null) return;
            var point = new Point(x, y);
            ShowPing(point);
            RoomSession.Instance.SendMapPing(x, y);
            _pingCheck.IsChecked = false;
            PushWebMap();
        }

        private string WebAssetUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
            if (_webDataUrls.TryGetValue(path, out var cached)) return cached;

            try
            {
                string root = Path.GetFullPath(PhotoHelper.GetWritableRoot());
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "";

                string relative = full.Substring(root.Length).TrimStart('\\', '/');
                string url = "https://hmwt.assets/" + string.Join("/",
                    relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(Uri.EscapeDataString));
                _webDataUrls[path] = url;
                return url;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MapWeb] asset: " + ex.Message);
                return "";
            }
        }

        private void OnWebTokenMoved(int tokenId, double x, double y, List<MapTrailPoint> trail)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokenMoved(tokenId, x, y, trail)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null || !CanMoveToken(token)) return;

            if (trail != null && trail.Count >= 2)
            {
                _trails[tokenId] = trail
                    .Where(p => p != null)
                    .Select(p => new Point(p.X, p.Y))
                    .ToList();
                PublishLocalTrail(tokenId);
            }

            if (_scene.SnapToGrid)
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                x = Math.Round((x - cell / 2) / cell) * cell + cell / 2;
                y = Math.Round((y - cell / 2) / cell) * cell + cell / 2;
            }

            token.X = Math.Max(0, Math.Min(_scene.WidthPx, x));
            token.Y = Math.Max(0, Math.Min(_scene.HeightPx, y));

            if (_remote)
            {
                RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y);
                RememberCurrentVision();
            }
            else
            {
                MapService.SaveToken(token);
                PublishMap();
                RememberCurrentVision();
            }

            PushWebMap();
        }

        public void Refresh()
        {
            try
            {
                LoadCombatants();

                if (_remote)
                {
                    var remote = RoomSession.Instance.GetMapScene();
                    if (remote == null)
                    {
                        RoomSession.Instance.RequestMap();
                        _status.Text = "Ждём карту от мастера…";
                        return;
                    }

                    // Где жетон сдвинулся с прошлого снимка — добавляем след
                    TrackTokenMoves(_scene, remote);
                    OpenRemoteScene(remote);
                    return;
                }

                int keepId = _scene?.Id ?? 0;
                _scenes = MapService.LoadScenes(_characterId);

                if (_scenes.Count == 0 && _isDm)
                {
                    var created = MapService.CreateScene(_characterId, "Карта");
                    if (created != null && created.Id > 0) _scenes = MapService.LoadScenes(_characterId);
                }

                _sceneCombo.Items.Clear();
                foreach (var scene in _scenes) _sceneCombo.Items.Add(scene);

                var target = _scenes.FirstOrDefault(s => s.Id == keepId) ?? _scenes.FirstOrDefault();
#if DEBUG
                try
                {
                    string debugScene = Environment.GetEnvironmentVariable("HMWT_DEBUG_SCENE");
                    if (int.TryParse(debugScene, out int debugSceneId))
                        target = _scenes.FirstOrDefault(s => s.Id == debugSceneId) ?? target;
                }
                catch { }
#endif
                if (target != null)
                {
                    _sceneCombo.SelectedItem = target;
                    OpenScene(target);

                    // Открыли вкладку — сразу отдаём сцену в комнату. Раньше карта
                    // уходила только по событию изменения: если её загрузили до
                    // входа игрока, он не видел ничего.
                    PublishMap();
                }
                else
                {
                    _status.Text = "Сцен нет — мастер может создать первую кнопкой «+»";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] Refresh: {ex}");
                _status.Text = "Не удалось загрузить сцены: " + ex.Message;
            }
        }

        /// <summary>Показывает карту, пришедшую от мастера.</summary>
        private void OpenRemoteScene(MapSceneData remote)
        {
            _pendingLiveMoves.Clear();
            _liveMoveTimer.Stop();
            ClearMeasure();
            _scene = remote;

            _gridCheck.IsChecked = _scene.GridOn;
            _snapCheck.IsChecked = _scene.SnapToGrid;
            _cellField.Text = ((int)Math.Round(_scene.GridPx <= 4 ? 70 : _scene.GridPx)).ToString();
            _gridThicknessSlider.Value = Math.Max(_gridThicknessSlider.Minimum,
                Math.Min(_gridThicknessSlider.Maximum, _scene.GridThickness));
            _suppressFogEvents = true;
            try { _fogCheck.IsChecked = _scene.FogOn; }
            finally { _suppressFogEvents = false; }

            string path = string.IsNullOrWhiteSpace(_scene.ImageHash)
                ? null
                : RemoteImagePath(_scene.ImageHash);

            if (string.IsNullOrWhiteSpace(_scene.ImageHash))
            {
                // Сцена есть, а картинки в ней нет: просить нечего
                _mapImage.Source = null;
                _mapImage.Width = 0;
                _mapImage.Height = 0;
                _status.Text = "Мастер ещё не загрузил карту";
            }
            else if (path == null)
            {
                RequestMapAsset(_scene.ImageHash);
                _mapImage.Source = null;
                _mapImage.Width = 0;
                _mapImage.Height = 0;
                _status.Text = "Мастер прислал карту, ждём картинку…";
            }
            else
            {
                if (!string.Equals(path, _loadedImagePath, StringComparison.OrdinalIgnoreCase) ||
                    _mapImage.Source == null)
                    LoadImageFromFile(path);

                _status.Text = $"{_scene.Name}: {_scene.WidthPx}×{_scene.HeightPx}, жетонов {_scene.Tokens.Count}";
            }

            // недостающие портреты жетонов запрашивает таймер повторов:
            // он же лечит потерянные ответы
            foreach (var token in _scene.Tokens)
            {
                if (string.IsNullOrWhiteSpace(token.PhotoHash)) continue;
                if (RemoteImagePath(token.PhotoHash) != null) continue;

                RequestMapAsset(token.PhotoHash);
            }

            BuildGrid();
            RenderStrokes();
            RenderShapes();
            RenderWalls();
            RebuildTokens();

            // Разведанное у каждого клиента своё и лежит локально. Раньше для
            // сцены мастера его даже не читали из базы — поэтому у игрока карта
            // каждый раз была закрыта, сколько бы он ни ходил по ней.
            // Читаем только при смене сцены: иначе каждый снимок комнаты
            // заново разбирал 240 полигонов и сбрасывал кэш тумана.
            if (_seenLoadedSceneId != _scene.Id || _seenPoints.Count == 0)
                LoadSeenPoints(_scene.Id);

            RememberCurrentVision();

            RenderTrails();
            DumpMapState("карта от мастера");
            ApplyOrientation();
        }

        private static string RemoteImagePath(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return null;

            try
            {
                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Maps");
                if (!Directory.Exists(dir)) return null;

                foreach (string candidate in Directory.GetFiles(dir, "remote_" + hash + ".*"))
                    if (File.Exists(candidate)) return candidate;
            }
            catch { }

            return null;
        }

        private void LoadImageFromFile(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);

                int longSide = Math.Max(_scene.WidthPx, _scene.HeightPx);
                if (longSide > 4096)
                {
                    double k = 4096.0 / longSide;
                    bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(_scene.WidthPx * k));
                }

                bitmap.EndInit();
                bitmap.Freeze();

                _mapImage.Source = bitmap;
                _mapImage.Width = _scene.WidthPx;
                _mapImage.Height = _scene.HeightPx;
                _loadedImagePath = path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] картинка карты: {ex.Message}");
            }
        }

        /// <summary>Карта изменилась: у игрока — пришла от мастера, у ведущего — от игрока.</summary>
        private void OnMapChanged()
        {
            if (!_inRoom) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnMapChanged));
                return;
            }

            // Скрытая копия вкладки не должна пересобирать сцену: при возврате
            // IsVisibleChanged сделает Refresh.
            if (!IsVisible) return;

            // Пока жетон держат мышью, карту не пересобираем: иначе объект,
            // который тащат, заменится и перетаскивание оборвётся
            if (_dragToken != null) return;

            if (!_remote)
            {
                var fromServer = RoomSession.Instance.GetMapScene();
                if (fromServer != null && _scene != null && fromServer.Id == _scene.Id)
                {
                    // Полный снимок важнее очереди промежуточных шагов
                    _pendingLiveMoves.Clear();
                    _liveMoveTimer.Stop();
                    TrackTokenMoves(_scene, fromServer);
                    _scene = fromServer;
                    BuildGrid();
                    RenderStrokes();
                    RenderShapes();
                    RebuildTokens();
                    RenderTrails();

                    // Туман мог измениться вместе с картой (стена, жетон, рисунок)
                    BuildFogMask();
                }
                return;
            }

            int keepId = _scene?.Id ?? 0;
            double scale = _scale, tx = _tx, ty = _ty;
            bool zoomed = _userZoomed;

            Refresh();

            if (_scene != null && _scene.Id == keepId && zoomed)
            {
                _scale = scale;
                _tx = tx;
                _ty = ty;
                _userZoomed = true;
                ApplyTransform();
            }
        }

        /// <summary>Просит картинку один раз и не дублирует передачу каждые две секунды.</summary>
        private void RequestMapAsset(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return;

            DateTime now = DateTime.UtcNow;
            if (_mapAssetAsked.TryGetValue(hash, out var asked) &&
                (now - asked).TotalSeconds < MapAssetRetrySeconds)
                return;

            _mapAssetAsked[hash] = now;
            RoomSession.Instance.RequestMapImage(hash);
        }

        private void OnMapImageReady(string hash, string path)
        {
            if (!_remote) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnMapImageReady(hash, path)));
                return;
            }

            if (!IsVisible) return;
            _mapAssetAsked.Remove(hash);
            if (_scene == null) return;

            // Картинка карты — показываем её; портрет жетона — перерисовываем
            // жетоны. Раньше портреты приходили, но экран о них не узнавал,
            // и фото появлялось только после следующего изменения карты.
            if (string.Equals(hash, _scene.ImageHash, StringComparison.OrdinalIgnoreCase))
            {
                LoadImageFromFile(path);
                ApplyOrientation();
                _status.Text = $"{_scene.Name}: {_scene.WidthPx}×{_scene.HeightPx}, жетонов {_scene.Tokens.Count}";
                return;
            }

            if (_scene.Tokens.Any(t => string.Equals(t.PhotoHash, hash, StringComparison.OrdinalIgnoreCase)))
                RebuildTokens();
        }

        /// <summary>
        /// Чужой жетон сдвинулся во время перетаскивания. Двигаем только его
        /// визуал и обзор — без пересборки всей карты: иначе десять обновлений
        /// в секунду превращаются в слайд-шоу.
        /// </summary>
        private void OnTokenMovedLive(int tokenId, double x, double y, double facing)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnTokenMovedLive(tokenId, x, y, facing)));
                return;
            }

            if (!IsVisible) return;
            if (_scene == null) return;

            // Свой жетон уже двигает сама мышь — второй раз не трогаем
            if (_dragToken != null && _dragToken.Id == tokenId) return;

            PendingLiveMove pending;
            if (!_pendingLiveMoves.TryGetValue(tokenId, out pending))
            {
                pending = new PendingLiveMove();
                _pendingLiveMoves[tokenId] = pending;
            }
            pending.X = x;
            pending.Y = y;
            pending.Facing = facing;
            pending.ReceivedAt = DateTime.UtcNow;

            if (!_liveMoveTimer.IsEnabled) _liveMoveTimer.Start();
        }

        private void ApplyPendingLiveMoves(object sender, EventArgs e)
        {
            if (_scene == null || _pendingLiveMoves.Count == 0)
            {
                _liveMoveTimer.Stop();
                return;
            }

            DateTime now = DateTime.UtcNow;
            var ready = _pendingLiveMoves
                .Where(p => (now - p.Value.ReceivedAt).TotalMilliseconds >= LiveMoveDelayMs)
                .Select(p => p.Key)
                .ToList();

            foreach (int tokenId in ready)
            {
                PendingLiveMove pending = _pendingLiveMoves[tokenId];
                _pendingLiveMoves.Remove(tokenId);
                ApplyRemoteLiveMove(tokenId, pending.X, pending.Y, pending.Facing);
            }

            if (_pendingLiveMoves.Count == 0) _liveMoveTimer.Stop();
        }

        private void OnMapTrailReceived(int tokenId, List<MapTrailPoint> points)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnMapTrailReceived(tokenId, points)));
                return;
            }

            if (_scene == null || points == null || points.Count == 0) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null) return;

            // Сообщение несёт весь путь, а не приращение. Раньше точки
            // добавлялись к уже нарисованному маршруту, и на карте оставался
            // «путь», которым жетон не ходил.
            if (points.Count < 2)
            {
                ClearTrail(tokenId);
                return;
            }

            _trails[tokenId] = points
                .Where(p => p != null)
                .Select(p => new Point(p.X, p.Y))
                .ToList();
            _trailTouched[tokenId] = DateTime.UtcNow;

            RenderTrails();
            if (_webMapReady) PushWebMap();
        }

        private void ApplyRemoteLiveMove(int tokenId, double x, double y, double facing)
        {
            if (_scene == null) return;

            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null) return;

            token.X = x;
            token.Y = y;
            if (facing >= 0) token.Facing = facing;

            // Новая перетаскивалка начинается после паузы — старый след
            // стираем, чтобы у всех не копились «позапрошлые» маршруты
            if (_trailTouched.TryGetValue(tokenId, out var touched) &&
                (DateTime.UtcNow - touched).TotalSeconds > 1.0)
            {
                ClearTrail(tokenId);
            }

            MoveTokenVisual(token);
            RenderFov();
            RefreshTokenVisibility();
            AppendTrail(token, new Point(x, y));

            // Туман при большом числе разведанных секторов строится долго.
            // Во время чужого хода обновляем его реже, а после отпускания
            // придёт полный снимок карты.
            if ((DateTime.UtcNow - _lastLiveFog).TotalMilliseconds >= 700)
            {
                _lastLiveFog = DateTime.UtcNow;
                BuildFogMask();
            }
        }

        /// <summary>Передвигает уже нарисованный жетон, не пересобирая слой.</summary>
        private void MoveTokenVisual(MapTokenData token)
        {
            if (token == null) return;

            foreach (var child in _tokenLayer.Children)
            {
                if (!(child is Border host) || !(host.Tag is MapTokenData tagged)) continue;
                if (tagged.Id != token.Id) continue;

                double width = host.Width > 0 ? host.Width : 110;
                double targetLeft = token.X - width / 2;
                double targetTop = token.Y - (host.Height - 34) / 2;

                double currentLeft = Canvas.GetLeft(host);
                double currentTop = Canvas.GetTop(host);
                if (double.IsNaN(currentLeft)) currentLeft = targetLeft;
                if (double.IsNaN(currentTop)) currentTop = targetTop;

                _visualMoves[token.Id] = new VisualMove
                {
                    FromLeft = currentLeft,
                    FromTop = currentTop,
                    ToLeft = targetLeft,
                    ToTop = targetTop,
                    StartedAt = DateTime.UtcNow
                };
                if (!_visualTimer.IsEnabled) _visualTimer.Start();
                return;
            }
        }

        private void ApplyVisualMoves(object sender, EventArgs e)
        {
            if (_visualMoves.Count == 0)
            {
                _visualTimer.Stop();
                return;
            }

            DateTime now = DateTime.UtcNow;
            var done = new List<int>();

            foreach (var pair in _visualMoves.ToList())
            {
                Border host = null;
                foreach (var child in _tokenLayer.Children)
                {
                    if (child is Border border && border.Tag is MapTokenData token && token.Id == pair.Key)
                    {
                        host = border;
                        break;
                    }
                }

                if (host == null)
                {
                    done.Add(pair.Key);
                    continue;
                }

                double t = Math.Min(1.0, (now - pair.Value.StartedAt).TotalMilliseconds / VisualMoveMs);
                double eased = t * t * (3 - 2 * t);
                Canvas.SetLeft(host, pair.Value.FromLeft + (pair.Value.ToLeft - pair.Value.FromLeft) * eased);
                Canvas.SetTop(host, pair.Value.FromTop + (pair.Value.ToTop - pair.Value.FromTop) * eased);

                if (t >= 1.0) done.Add(pair.Key);
            }

            foreach (int id in done) _visualMoves.Remove(id);
            if (_visualMoves.Count == 0) _visualTimer.Stop();
        }

        /// <summary>
        /// Ведущий включил туман заново: у игрока своё «разведанное», поэтому
        /// очищаем его здесь и закрываем карту целиком, как у ведущего.
        /// </summary>
        private void OnMapSeenReset()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnMapSeenReset));
                return;
            }

            if (_scene == null) return;

            ClearSeenPoints();
            MapService.ClearSeen(_scene.Id);
            BuildFogMask();
        }

        /// <summary>Другой участник открыл область — добавляем и пересобираем туман.</summary>
        private void OnMapSeenAdded(string points)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnMapSeenAdded(points)));
                return;
            }

            if (_scene == null || string.IsNullOrWhiteSpace(points)) return;
            if (!AddSeenPoint(points)) return;

            // На хосте сектор уже сохранён сервером; у игрока сохраняем сами
            if (_remote) MapService.AddSeen(_scene.Id, points);
            BuildFogMask();
        }

        /// <summary>При открытии карты хост присылает всё, что отряд уже разведал.</summary>
        private void OnMapSeenSynced(List<string> points)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnMapSeenSynced(points)));
                return;
            }

            if (_scene == null || points == null || points.Count == 0) return;

            var added = new List<string>();
            foreach (string point in points)
                if (AddSeenPoint(point)) added.Add(point);

            if (added.Count == 0) return;

            MapService.AddSeenBatch(_scene.Id, added);
            BuildFogMask();
        }

        /// <summary>
        /// Вкладку открыли/закрыли, вошли в комнату или вышли из неё. Роль
        /// вкладки зависит от этого, поэтому перечитываем карту заново.
        /// </summary>
        private void OnRoomStateChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnRoomStateChanged));
                return;
            }

            // Имя участника могло появиться или смениться вместе со снимком
            _selfNameCache = null;

            bool active = _inRoom;
            if (active == _lastRoomActive) return;
            _lastRoomActive = active;
            if (!active) _mapAssetAsked.Clear();
            if (_shapeButton != null) _shapeButton.IsEnabled = _isDm || _remote;
            if (_shapeField != null) _shapeField.IsEnabled = _isDm || _remote;
            Refresh();
        }

        /// <summary>
        /// Повторные запросы раз в две секунды: сцена, картинка карты и портреты
        /// жетонов. Пока файла нет, в строке состояния видно, чего именно ждём.
        /// </summary>
        private void OnRetryTick(object sender, EventArgs e)
        {
            DumpUiGeometry();

            if (!_inRoom || !_remote) return;

            try
            {
                var session = RoomSession.Instance;
                if (!session.IsActive) return;

                var remote = session.GetMapScene();
                if (remote == null)
                {
                    session.RequestMap();
                    _status.Text = "Ждём карту от мастера…";
                    return;
                }

                if (_scene == null || _scene.Id != remote.Id)
                {
                    Refresh();
                    return;
                }

                bool waitImage = false;
                if (!string.IsNullOrWhiteSpace(_scene.ImageHash) &&
                    RemoteImagePath(_scene.ImageHash) == null)
                {
                    RequestMapAsset(_scene.ImageHash);
                    waitImage = true;
                }

                int waitPhotos = 0;
                foreach (var token in _scene.Tokens)
                {
                    if (string.IsNullOrWhiteSpace(token.PhotoHash)) continue;
                    if (RemoteImagePath(token.PhotoHash) != null) continue;

                    RequestMapAsset(token.PhotoHash);
                    waitPhotos++;
                }

                if (waitImage)
                {
                    _status.Text = "Мастер прислал карту, ждём картинку…";
                }
                else if (waitPhotos > 0)
                {
                    _status.Text = $"{_scene.Name}: {_scene.WidthPx}×{_scene.HeightPx}, " +
                                   $"ждём портреты жетонов ({waitPhotos})…";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] повторный запрос: {ex.Message}");
            }
        }

        /// <summary>Ведущий изменил карту — отправляем игрокам.</summary>
        private void PublishMap()
        {
            if (!_inRoom || _remote || _scene == null) return;

            try
            {
                PrepareTokenPhotos();
                RoomSession.Instance.PublishMapScene(_scene);
            }
            catch (Exception ex) { Debug.WriteLine($"[MapView] публикация карты: {ex.Message}"); }
        }

        /// <summary>
        /// Перед отправкой карты запоминаем хэши картинок жетонов: по ним игрок
        /// скачает нужный файл один раз. Без этого у игрока были буквы вместо
        /// портретов — бестиария на его машине нет.
        /// </summary>
        private void PrepareTokenPhotos()
        {
            if (_scene?.Tokens == null) return;

            bool changed = false;
            foreach (var token in _scene.Tokens)
            {
                // признак игрока нужен клиенту: у него нет своей базы боёв
                bool isPlayer = _combatants.TryGetValue(token.CombatantId, out var owner) &&
                                owner.Combatant != null && owner.Combatant.Kind == CombatantKind.Player;
                if (token.IsPlayer != isPlayer)
                {
                    token.IsPlayer = isPlayer;
                    MapService.SaveToken(token);
                    changed = true;
                }

                string path = ResolveTokenPhoto(token);
                if (string.IsNullOrWhiteSpace(path)) continue;

                string hash = HashFile(path);
                if (string.IsNullOrWhiteSpace(hash)) continue;
                if (string.Equals(hash, token.PhotoHash, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(path, token.PhotoPath, StringComparison.OrdinalIgnoreCase)) continue;

                token.PhotoHash = hash;
                token.PhotoPath = path;
                MapService.SaveToken(token);
                changed = true;
            }

            if (changed) MapService.SaveScene(_scene);
        }

        /// <summary>Картинка жетона: портрет монстра из бестиария или аватар игрока.</summary>
        private string ResolveTokenPhoto(MapTokenData token)
        {
            try
            {
                if (_combatants.TryGetValue(token.CombatantId, out var linked) &&
                    linked.Combatant != null && linked.Combatant.MonsterId > 0)
                {
                    var monster = BestiaryService.Load(linked.Combatant.MonsterId);
                    if (monster != null && !string.IsNullOrWhiteSpace(monster.PhotoPath))
                    {
                        string full = PhotoHelper.ResolvePhotoPath(monster.PhotoPath);
                        if (!string.IsNullOrEmpty(full) && File.Exists(full)) return full;
                    }
                }

                // игрок: берём его аватар из комнаты
                var session = RoomSession.Instance;
                if (session != null && session.IsActive)
                {
                    foreach (var player in session.GetPlayers())
                    {
                        if (player == null || string.IsNullOrWhiteSpace(player.AvatarPath)) continue;
                        string name = !string.IsNullOrWhiteSpace(player.CharacterName) ? player.CharacterName : player.Name;
                        if (!string.Equals((name ?? "").Trim(), (token.Label ?? "").Trim(),
                                StringComparison.CurrentCultureIgnoreCase)) continue;

                        string full = PhotoHelper.ResolvePhotoPath(player.AvatarPath);
                        if (!string.IsNullOrEmpty(full) && File.Exists(full)) return full;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] картинка жетона: {ex.Message}");
            }

            return null;
        }

        private static string HashFile(string path)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA1.Create();
                using var stream = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }
        private void LoadCombatants()
        {
            _combatants.Clear();
            try
            {
                foreach (var encounter in InitiativeService.LoadEncounters())
                {
                    foreach (var combatant in encounter.Combatants ?? new List<CombatantData>())
                    {
                        if (combatant != null && combatant.Id > 0)
                            _combatants[combatant.Id] = (encounter, combatant);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] LoadCombatants: {ex.Message}");
            }
        }

        private void OpenScene(MapSceneData scene)
        {
            try
            {
                ClearMeasure();
                _scene = MapService.LoadScene(scene.Id) ?? scene;
                _gridCheck.IsChecked = _scene.GridOn;
                _snapCheck.IsChecked = _scene.SnapToGrid;
                _cellField.Text = ((int)Math.Round(_scene.GridPx <= 4 ? 70 : _scene.GridPx)).ToString();
                _cellSlider.Value = Math.Max(_cellSlider.Minimum, Math.Min(_cellSlider.Maximum, _scene.GridPx));
                _gridThicknessSlider.Value = Math.Max(_gridThicknessSlider.Minimum,
                    Math.Min(_gridThicknessSlider.Maximum, _scene.GridThickness));

                string path = MapService.ResolveImagePath(_scene);
                if (path != null)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    _mapImage.Source = bitmap;
                    _mapImage.Width = _scene.WidthPx;
                    _mapImage.Height = _scene.HeightPx;
                }
                else
                {
                    _mapImage.Source = null;
                    _mapImage.Width = 0;
                    _mapImage.Height = 0;
                }

                // Размер берём из файла: если карту заменили снаружи, сцена не
                // должна растягивать её до старого размера
                if (path != null)
                {
                    try
                    {
                        var decoder = BitmapDecoder.Create(new Uri(path),
                            BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        int fileW = decoder.Frames[0].PixelWidth;
                        int fileH = decoder.Frames[0].PixelHeight;

                        if (fileW > 0 && fileH > 0 && (fileW != _scene.WidthPx || fileH != _scene.HeightPx))
                        {
                            _scene.WidthPx = fileW;
                            _scene.HeightPx = fileH;
                            MapService.SaveScene(_scene);
            PublishMap();
                            _mapImage.Width = fileW;
                            _mapImage.Height = fileH;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MapView] размер картинки: {ex.Message}");
                    }
                }

                _suppressFogEvents = true;
                try { _fogCheck.IsChecked = _scene.FogOn; }
                finally { _suppressFogEvents = false; }
                LoadSeenPoints(_scene.Id);
                BuildGrid();
                RenderStrokes();
                RenderShapes();
                RenderWalls();
                RebuildTokens();
                ApplyOrientation();

                _status.Text = path == null
                    ? (_isDm ? "Карта не загружена — «Открыть карту…»" : "Мастер ещё не загрузил карту")
                    : $"{_scene.Name}: {_scene.WidthPx}×{_scene.HeightPx}, жетонов {_scene.Tokens.Count}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] OpenScene: {ex}");
                _status.Text = "Ошибка открытия сцены: " + ex.Message;
            }
        }

        private void PickImage()
        {
            if (!_isDm) { DmRolls.Toast(this, "Карту загружает мастер", true); return; }
            if (_scene == null) { DmRolls.Toast(this, "Сначала создайте сцену кнопкой «+»", true); return; }

            var dialog = new OpenFileDialog
            {
                Title = "Карта боя",
                Filter = "Изображение (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Все файлы (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            if (MapService.ImportImage(_scene, dialog.FileName, out string error))
            {
                OpenScene(_scene);
                // Новая картинка сразу уходит игрокам: без этого они оставались
                // со старой до следующего изменения сцены
                PublishMap();
            }
            else
                DmRolls.Toast(this, "Не удалось открыть картинку: " + error, true);
        }

        private void NewScene()
        {
            if (!_isDm) return;

            string name = Windows.NotePromptWindow.AskText(Window.GetWindow(this),
                "Новая сцена", "Название сцены", "Сцена " + (_scenes.Count + 1), "Создать");
            if (string.IsNullOrWhiteSpace(name)) return;

            var scene = MapService.CreateScene(_characterId, name);
            if (scene == null || scene.Id <= 0)
            {
                DmRolls.Toast(this, "Не удалось создать сцену", true);
                return;
            }

            Refresh();
            var created = _scenes.FirstOrDefault(s => s.Id == scene.Id);
            if (created != null) _sceneCombo.SelectedItem = created;
        }

        private void DeleteScene()
        {
            if (!_isDm || _scene == null) return;

            if (!Windows.MessageDialog.Confirm(this,
                    "Удалить сцену «" + _scene.Name + "» вместе с жетонами?", "Удаление сцены", "Удалить"))
                return;

            MapService.DeleteScene(_scene.Id);
            _scene = null;
            Refresh();
        }

        private void SyncTokens()
        {
            if (!_isDm || _scene == null) return;

            var encounters = _combatants.Values.Select(v => v.Encounter)
                .GroupBy(e => e.Id).Select(g => g.First()).ToList();

            var encounter = encounters.FirstOrDefault(e => e.IsActive) ?? encounters.FirstOrDefault();
            if (encounter == null)
            {
                DmRolls.Toast(this, "Сначала добавьте участников в трекер инициативы", true);
                return;
            }

            int added = MapService.SyncTokensFromEncounter(_scene, encounter);
            OpenScene(_scene);
            PublishMap();
            DmRolls.Toast(this, added > 0 ? $"Добавлено жетонов: {added}" : "Все участники уже на карте");
        }

        // ══════════════════════ СЕТКА ══════════════════════

        private void ApplyGridSize()
        {
            if (!_isDm || _scene == null) return;

            string text = (_cellField.Text ?? "").Trim().Replace(',', '.');
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double size))
            {
                _cellField.Text = ((int)Math.Round(_scene.GridPx)).ToString();
                return;
            }

            size = Math.Max(16, Math.Min(400, size));
            if (Math.Abs(size - _scene.GridPx) < 0.5)
            {
                _cellField.Text = ((int)Math.Round(size)).ToString();
                return;
            }

            _scene.GridPx = size;
            MapService.SaveScene(_scene);
            PublishMap();
            BuildGrid();
            RebuildTokens();
            _cellField.Text = ((int)Math.Round(size)).ToString();
            _cellSlider.Value = Math.Max(_cellSlider.Minimum, Math.Min(_cellSlider.Maximum, size));
        }

        private void OpenTrailColorMenu(FrameworkElement target)
        {
            if (!_isDm || _scene == null) return;

            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");
            foreach (var pair in new[]
            {
                ("Фиолетовый", "#FF9B8BFF"), ("Бирюзовый", "#FF5FD4DD"),
                ("Золотой", "#FFE8C07F"), ("Красный", "#FFE06A63"),
                ("Зелёный", "#FF7FD98A"), ("Серый", "#FF9AA4BF")
            })
            {
                var item = new MenuItem { Header = pair.Item1 };
                ApplyMenuItemStyle(item);
                item.Click += (s, e) =>
                {
                    _scene.TrailColor = pair.Item2;
                    MapService.SaveScene(_scene);
                    PublishMap();
                    PushWebMap();
                };
                menu.Items.Add(item);
            }
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private void OpenGridColorMenu(FrameworkElement target, Border swatch)
        {
            if (!_isDm || _scene == null) return;

            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");

            var colors = new (string Title, string Hex)[]
            {
                ("Белая", "#33FFFFFF"),
                ("Серая", "#3395A0B8"),
                ("Чёрная", "#55000000"),
                ("Золотая", "#55FFD479"),
                ("Красная", "#55E06A63"),
                ("Зелёная", "#5566D19A"),
                ("Голубая", "#5574A9FF"),
                ("Скрыть сетку", "")
            };

            foreach (var color in colors)
            {
                var item = new MenuItem { Header = color.Title, IsCheckable = false };
                ApplyMenuItemStyle(item);
                string hex = color.Hex;
                item.Click += (s, e) =>
                {
                    if (string.IsNullOrEmpty(hex))
                    {
                        _scene.GridOn = false;
                        _gridCheck.IsChecked = false;
                    }
                    else
                    {
                        _scene.GridColor = hex;
                        _scene.GridOn = true;
                        _gridCheck.IsChecked = true;
                        if (TryBrush(hex, out var brush)) swatch.Background = brush;
                    }

                    MapService.SaveScene(_scene);
            PublishMap();
                    BuildGrid();
                };
                menu.Items.Add(item);
            }

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private void BuildGrid()
        {
            if (_webMapReady) return;
            _gridLayer.Children.Clear();
            if (_scene == null || !_scene.GridOn || _scene.WidthPx <= 0) return;

            double step = _scene.GridPx > 4 ? _scene.GridPx : 70;
            double thickness = Math.Max(0.5, Math.Min(6, _scene.GridThickness));
            if (!TryBrush(string.IsNullOrWhiteSpace(_scene.GridColor) ? "#33FFFFFF" : _scene.GridColor, out Brush brush))
                brush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

            for (double x = _scene.GridOffsetX % step; x <= _scene.WidthPx; x += step)
            {
                _gridLayer.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = _scene.HeightPx,
                    Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false
                });
            }

            for (double y = _scene.GridOffsetY % step; y <= _scene.HeightPx; y += step)
            {
                _gridLayer.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0, Y1 = y, X2 = _scene.WidthPx, Y2 = y,
                    Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false
                });
            }
        }

        private static bool TryBrush(string hex, out Brush brush)
        {
            brush = null;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                brush = new SolidColorBrush(color);
                brush.Freeze();
                return true;
            }
            catch { return false; }
        }

        // ══════════════════════ ПОВОРОТ ══════════════════════

        // ══════════════════════ ОРИЕНТАЦИЯ ВИДА ══════════════════════

        private void RotateView(int degrees)
        {
            if (!_isDm || _scene == null) return;

            _scene.Rotation = ((_scene.Rotation + degrees) % 360 + 360) % 360;
            MapService.SaveScene(_scene);
            PublishMap();
            ApplyOrientation();
        }

        private void FlipView(bool horizontal)
        {
            if (!_isDm || _scene == null) return;

            if (horizontal) _scene.FlipH = !_scene.FlipH;
            else _scene.FlipV = !_scene.FlipV;

            MapService.SaveScene(_scene);
            PublishMap();
            ApplyOrientation();
        }

        private void SetFog(bool on)
        {
            if (_scene == null || !_isDm) return;

            bool wasOn = _scene.FogOn;

            _scene.FogOn = on;
            MapService.SaveScene(_scene);
            PublishMap();
            _fogCheck.IsChecked = on;

            // Туман выключили и включили снова — начинаем разведку заново:
            // карта снова закрыта целиком, а не просвечена там, где уже были.
            if (on && !wasOn)
            {
                ClearSeenPoints();
                MapService.ClearSeen(_scene.Id);

                // У игроков «разведанное» своё, локальное: просим и их начать заново
                RoomSession.Instance.ResetClientsSeen();
            }

            // «Развеять туман» — это полный сброс разведки, а не только снятие
            // плёнки: иначе при следующем включении остаются старые пятна.
            if (!on)
            {
                ClearSeenPoints();
                MapService.ClearSeen(_scene.Id);
                RoomSession.Instance.ResetClientsSeen();
            }

            _fog.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            BuildFogMask();
            // У мастера туман — плёнка, у игроков сплошной чёрный
            _fog.Fill = new SolidColorBrush(_isDm
                ? Color.FromArgb(0x8C, 0x05, 0x07, 0x0E)
                : Color.FromArgb(0xFF, 0x02, 0x03, 0x06));
        }

        /// <summary>
        /// Пересчитывает показ под поворот и отражение. Поворот считается матрицей,
        /// поэтому карта и туман всегда лежат в одном прямоугольнике.
        /// </summary>
        private void ApplyOrientation()
        {
            if (_scene == null) return;

            int rot = ((_scene.Rotation % 360) + 360) % 360;
            double w = Math.Max(1, _scene.WidthPx);
            double h = Math.Max(1, _scene.HeightPx);
            _content.Width = w;
            _content.Height = h;

            var matrix = new Matrix();
            matrix.RotateAt(rot, w / 2, h / 2);
            double flipX = _scene.FlipH ? -1 : 1;
            double flipY = _scene.FlipV ? -1 : 1;
            if (flipX < 0 || flipY < 0) matrix.ScaleAt(flipX, flipY, w / 2, h / 2);

            var bounds = Rect.Transform(new Rect(0, 0, w, h), matrix);
            matrix.Translate(-bounds.X, -bounds.Y);
            _content.RenderTransform = new MatrixTransform(matrix);

            _world.Width = Math.Max(1, bounds.Width);
            _world.Height = Math.Max(1, bounds.Height);
            Canvas.SetLeft(_content, 0);
            Canvas.SetTop(_content, 0);

            _fog.Visibility = _scene.FogOn ? Visibility.Visible : Visibility.Collapsed;
            _fogDark.Visibility = Visibility.Collapsed;

            // У мастера — полупрозрачная плёнка (видно, что скрыто), у игрока —
            // сплошной чёрный: разведанное открыто, остальное не видно вовсе.
            _fog.Fill = new SolidColorBrush(_isDm
                ? Color.FromArgb(0x8C, 0x05, 0x07, 0x0E)
                : Color.FromArgb(0xFF, 0x02, 0x03, 0x06));

            BuildFogMask();

            _userZoomed = false;
            FitToWindow();
        }
        // ══════════════════════ РИСОВАНИЕ ══════════════════════

        private void OpenBrushColorMenu(FrameworkElement target)
        {
            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");

            var colors = new (string Title, string Hex)[]
            {
                ("Красная", "#FFE06A63"),
                ("Золотая", "#FFFFD479"),
                ("Голубая", "#FF74A9FF"),
                ("Зелёная", "#FF66D19A"),
                ("Фиолетовая", "#FFB98BFF"),
                ("Белая", "#FFF2F5FF"),
                ("Чёрная", "#FF10131C")
            };

            foreach (var color in colors)
            {
                var item = new MenuItem { Header = color.Title, IsCheckable = false };
                ApplyMenuItemStyle(item);
                string hex = color.Hex;
                item.Click += (s, e) =>
                {
                    _brushColor = hex;
                    if (TryBrush(hex, out var brush) && _brushSwatch != null) _brushSwatch.Background = brush;
                };
                menu.Items.Add(item);
            }

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private double BrushThickness()
        {
            string text = (_brushField.Text ?? "").Trim().Replace(',', '.');
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value) || value <= 0)
                return 16;

            return Math.Max(1, Math.Min(80, value));
        }

        private void RenderStrokes()
        {
            if (_webMapReady) return;
            _drawLayer.Children.Clear();
            if (_scene == null) return;

            foreach (var stroke in _scene.Strokes)
            {
                var polyline = BuildStrokeVisual(stroke);
                if (polyline != null) _drawLayer.Children.Add(polyline);
            }
        }

        private System.Windows.Shapes.Polyline BuildStrokeVisual(MapStrokeData stroke)
        {
            try
            {
                var points = ParsePoints(stroke.Points);
                if (points.Count == 0) return null;

                var polyline = new System.Windows.Shapes.Polyline
                {
                    Points = new PointCollection(points),
                    StrokeThickness = Math.Max(1, stroke.Thickness),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };

                if (TryBrush(stroke.Color, out var brush)) polyline.Stroke = brush;
                else polyline.Stroke = DmUiKit.B("Danger");

                return polyline;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] BuildStrokeVisual: {ex.Message}");
                return null;
            }
        }

        private static List<Point> ParsePoints(string text)
        {
            var list = new List<Point>();
            if (string.IsNullOrWhiteSpace(text)) return list;

            foreach (string pair in text.Split(';'))
            {
                string[] parts = pair.Split(',');
                if (parts.Length != 2) continue;

                if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double y))
                    list.Add(new Point(x, y));
            }

            return list;
        }

        private static string PointsToString(IEnumerable<Point> points)
        {
            var parts = new List<string>();
            foreach (var point in points)
                parts.Add(point.X.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "," +
                          point.Y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(";", parts);
        }

        private void BeginStroke(Point point)
        {
            _strokePoints = new List<Point> { point };

            _strokePreview = new System.Windows.Shapes.Polyline
            {
                StrokeThickness = Math.Max(1, BrushThickness()),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
                Opacity = 0.85
            };

            if (_eraserCheck.IsChecked == true) _strokePreview.Stroke = DmUiKit.B("Mute");
            else if (TryBrush(_brushColor, out var brush)) _strokePreview.Stroke = brush;

            _strokePreview.Points = new PointCollection { point };
            _drawLayer.Children.Add(_strokePreview);
        }

        private void ExtendStroke(Point point)
        {
            if (_strokePoints == null || _strokePreview == null) return;

            Point last = _strokePoints[_strokePoints.Count - 1];
            if (Math.Abs(point.X - last.X) < 1.5 && Math.Abs(point.Y - last.Y) < 1.5) return;

            _strokePoints.Add(point);
            _strokePreview.Points.Add(point);
        }

        private void EndStroke()
        {
            if (_strokePoints == null || _scene == null) { _strokePoints = null; _strokePreview = null; return; }

            try
            {
                if (_eraserCheck.IsChecked == true)
                {
                    EraseAt(_strokePoints, Math.Max(8, BrushThickness()));
                    if (_strokePreview != null) _drawLayer.Children.Remove(_strokePreview);
                }
                else if (_strokePoints.Count > 0)
                {
                    var stroke = new MapStrokeData
                    {
                        SceneId = _scene.Id,
                        Color = _brushColor,
                        Thickness = BrushThickness(),
                        Points = PointsToString(_strokePoints)
                    };

                    if (_remote)
                    {
                        // Игрок рисует у себя сразу, а ведущий сохраняет штрих
                        // с настоящим id и присылает карту всем. Временный id
                        // отрицательный, чтобы не совпасть с серверным.
                        stroke.Id = -Math.Abs(Guid.NewGuid().GetHashCode());
                        _scene.Strokes.Add(stroke);
                        if (_strokePreview != null) _drawLayer.Children.Remove(_strokePreview);
                        var mine = BuildStrokeVisual(stroke);
                        if (mine != null) _drawLayer.Children.Add(mine);
                        RoomSession.Instance.SendMapStroke(stroke);
                    }
                    else if (MapService.SaveStroke(stroke) > 0)
                    {
                        _scene.Strokes.Add(stroke);
                        PublishMap();
                        PushUndo(() =>
                        {
                            MapService.DeleteStroke(stroke.Id);
                            _scene.Strokes.RemoveAll(x => x.Id == stroke.Id);
                            PublishMap();
                        });
                        if (_strokePreview != null) _drawLayer.Children.Remove(_strokePreview);
                        var visual = BuildStrokeVisual(stroke);
                        if (visual != null) _drawLayer.Children.Add(visual);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] EndStroke: {ex.Message}");
            }
            finally
            {
                _strokePoints = null;
                _strokePreview = null;
            }
        }

        /// <summary>Ластик: удаляет штрихи, которые проходят рядом с его следом.</summary>
        private void EraseAt(List<Point> path, double radius)
        {
            if (_scene == null) return;

            var remove = new List<MapStrokeData>();
            foreach (var stroke in _scene.Strokes)
            {
                var points = ParsePoints(stroke.Points);
                if (points.Count == 0) continue;

                bool hit = path.Any(p => points.Any(q =>
                    Math.Abs(p.X - q.X) <= radius && Math.Abs(p.Y - q.Y) <= radius));
                if (hit) remove.Add(stroke);
            }

            foreach (var stroke in remove)
            {
                if (!_remote) MapService.DeleteStroke(stroke.Id);
                _scene.Strokes.Remove(stroke);
            }

            if (remove.Count > 0)
            {
                if (_remote)
                {
                    // У игрока нет базы ведущего: просим удалить те же штрихи
                    var ids = remove.Where(s => s.Id > 0).Select(s => s.Id).ToList();
                    if (ids.Count > 0) RoomSession.Instance.EraseMapStrokes(ids);
                }
                else
                {
                    // Стерли у себя — сразу отдаём всем: иначе у игроков остаются
                    // уже стёртые рисунки до следующего изменения карты
                    PublishMap();
                }
                RenderStrokes();
            }
        }

        private void ClearStrokes()
        {
            if (!_isDm || _scene == null) return;

            if (!Windows.MessageDialog.Confirm(this, "Стереть все рисунки с этой сцены?", "Рисунки", "Стереть")) return;

            MapService.ClearStrokes(_scene.Id);
            _scene.Strokes.Clear();
            PublishMap();
            RenderStrokes();
        }

        // ══════════════════════ СТЕНЫ ══════════════════════

        private void RenderWalls()
        {
            if (_webMapReady) return;
            _wallLayer.Children.Clear();
            if (_scene == null) return;

            var stroke = new SolidColorBrush(Color.FromArgb(0xE6, 0x7F, 0xD9, 0xE8));
            stroke.Freeze();

            foreach (var wall in _scene.Walls)
            {
                bool door = string.Equals(wall.Kind, "door", StringComparison.OrdinalIgnoreCase);
                Brush lineBrush = stroke;
                if (door)
                {
                    var doorColor = wall.IsOpen
                        ? Color.FromArgb(0xCC, 0x66, 0xD1, 0x9A)
                        : Color.FromArgb(0xE6, 0xFF, 0xD4, 0x79);
                    var brush = new SolidColorBrush(doorColor);
                    brush.Freeze();
                    lineBrush = brush;
                }

                var line = new System.Windows.Shapes.Line
                {
                    X1 = wall.X1, Y1 = wall.Y1, X2 = wall.X2, Y2 = wall.Y2,
                    Stroke = lineBrush,
                    StrokeDashArray = door ? new DoubleCollection { 3, 2 } : null,
                    StrokeThickness = Math.Max(3, (_scene.GridPx > 4 ? _scene.GridPx : 70) / 16),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Tag = wall,
                    ToolTip = "Стена: ПКМ — убрать"
                };

                if (_isDm)
                {
                    line.Cursor = Cursors.Hand;
                    line.MouseRightButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        ShowWallMenu(line, wall);
                    };
                }

                _wallLayer.Children.Add(line);
            }
        }

        private void BeginWall(Point point)
        {
            _wallStart = SnapWallPoint(point);

            _wallPreview = new System.Windows.Shapes.Line
            {
                X1 = _wallStart.Value.X,
                Y1 = _wallStart.Value.Y,
                X2 = _wallStart.Value.X,
                Y2 = _wallStart.Value.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x7F, 0xD9, 0xE8)),
                StrokeThickness = Math.Max(3, (_scene.GridPx > 4 ? _scene.GridPx : 70) / 16),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };

            _wallLayer.Children.Add(_wallPreview);
        }

        private void ExtendWall(Point point)
        {
            if (_wallStart == null || _wallPreview == null) return;

            Point snapped = SnapWallPoint(point);
            _wallPreview.X2 = snapped.X;
            _wallPreview.Y2 = snapped.Y;
        }

        private void EndWall()
        {
            if (_wallStart == null || _scene == null) { _wallStart = null; _wallPreview = null; return; }

            try
            {
                double x1 = _wallPreview.X1, y1 = _wallPreview.Y1;
                double x2 = _wallPreview.X2, y2 = _wallPreview.Y2;

                // совсем короткие отрезки не сохраняем: это случайный щелчок
                if (Math.Abs(x2 - x1) + Math.Abs(y2 - y1) < 8) return;

                var wall = new MapWallData { SceneId = _scene.Id, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
                if (MapService.SaveWall(wall) > 0)
                {
                    _scene.Walls.Add(wall);
                    PushUndo(() =>
                    {
                        MapService.DeleteWall(wall.Id);
                        _scene.Walls.RemoveAll(w => w.Id == wall.Id);
                        PublishMap();
                    });
                    RenderWalls();
                    PublishMap();
                    BuildFogMask();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] EndWall: {ex.Message}");
            }
            finally
            {
                if (_wallPreview != null) _wallLayer.Children.Remove(_wallPreview);
                _wallStart = null;
                _wallPreview = null;
            }
        }

        private Point SnapWallPoint(Point point)
        {
            if (_scene == null || !_scene.GridOn) return point;

            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            return new Point(
                Math.Round(point.X / cell) * cell,
                Math.Round(point.Y / cell) * cell);
        }

        /// <summary>
        /// Видимость жетона с учётом стен: на каждом луче ищем ближайшее
        /// пересечение со стеной, поэтому туман за углом не рассеивается.
        /// </summary>
        private Geometry BuildVisionGeometry(MapTokenData token, Matrix matrix)
        {
            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            double reveal = token.RevealCells > 0 ? token.RevealCells : 6;
            double radius = cell * Math.Max(1, reveal);

            var center = new Point(token.X, token.Y);
            bool full = token.ViewAngle >= 359.9;
            double half = full ? 180.0 : Math.Max(2.5, Math.Min(180.0, token.ViewAngle / 2.0));
            double startDeg = full ? 0 : token.Facing - half;
            double endDeg = full ? 360 : token.Facing + half;

            var walls = _scene.Walls;
            var figure = new PathFigure { StartPoint = center, IsClosed = true };

            // Лучи: равномерные + отдельные в углы стен. Без лучей в углы обзор
            // «просачивался» между соседними лучами и игрок видел за стеной.
            var angles = new List<double>();
            const int rays = 96;
            for (int i = 0; i <= rays; i++) angles.Add(startDeg + (endDeg - startDeg) * i / rays);

            foreach (var wall in walls)
            {
                if (wall.IsOpen) continue;

                foreach (var corner in new[] { new Point(wall.X1, wall.Y1), new Point(wall.X2, wall.Y2) })
                {
                    double dx = corner.X - center.X, dy = corner.Y - center.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) > radius) continue;

                    double cornerAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

                    // Сравниваем не абсолютный угол, а отклонение от взгляда:
                    // конус, смотрящий на 0°, раньше «терял» углы стен на 350°.
                    double rel = NormalizeDeg(cornerAngle - token.Facing);
                    if (rel < -half || rel > half) continue;

                    // Для полного круга угол приводим к 0..360, для конуса —
                    // к непрерывной шкале вокруг направления взгляда.
                    double angle = full ? NormalizeDeg(cornerAngle) : token.Facing + rel;

                    angles.Add(angle - 0.35);
                    angles.Add(angle);
                    angles.Add(angle + 0.35);
                }
            }

            angles.Sort();
            foreach (double angle in angles)
            {
                double distance = radius;

                foreach (var wall in walls)
                {
                    if (wall.IsOpen) continue;      // открытая дверь не мешает
                    double hit = RayHitDistance(center, angle, wall, radius);
                    if (hit < distance) distance = hit;
                }

                figure.Segments.Add(new LineSegment(PointOnCircle(center, distance, angle), false));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Transform = new MatrixTransform(matrix);
            return geometry;
        }

        /// <summary>Приводит угол к диапазону 0..360.</summary>
        private static double NormalizeDeg(double deg)
        {
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            return deg;
        }

        /// <summary>Расстояние до стены по лучу (или maxDist, если стена не мешает).</summary>
        private static double RayHitDistance(Point origin, double degrees, MapWallData wall, double maxDist)
        {
            double rad = degrees * Math.PI / 180.0;
            double dx = Math.Cos(rad), dy = Math.Sin(rad);

            double x3 = wall.X1, y3 = wall.Y1;
            double sx = wall.X2 - x3, sy = wall.Y2 - y3;

            // Пересечение луча p + t·r и отрезка q + u·s.
            // Раньше знак u был перевёрнут, и проверка 0..1 отбрасывала
            // все настоящие пересечения — стены не перекрывали обзор вовсе.
            double denom = dx * sy - dy * sx;
            if (Math.Abs(denom) < 1e-9) return maxDist;

            double t = ((x3 - origin.X) * sy - (y3 - origin.Y) * sx) / denom;
            double u = ((x3 - origin.X) * dy - (y3 - origin.Y) * dx) / denom;

            if (u < 0 || u > 1 || t <= 0) return maxDist;
            return Math.Min(t, maxDist);
        }

        /// <summary>Радиус обзора жетона: сколько видно и какой круг рассеивает туман.</summary>
        private void SetRevealCells(MapTokenData token, double cells)
        {
            token.RevealCells = Math.Max(1, Math.Min(40, cells));
            MapService.SaveToken(token);
            RenderFov();
            RememberCurrentVision();
            BuildFogMask();
        }
        private void ShowWallMenu(FrameworkElement target, MapWallData wall)
        {
            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");

            bool door = string.Equals(wall.Kind, "door", StringComparison.OrdinalIgnoreCase);

            if (door)
            {
                AddMenuItem(menu, wall.IsOpen ? "Закрыть дверь" : "Открыть дверь", () =>
                {
                    wall.IsOpen = !wall.IsOpen;
                    MapService.UpdateWall(wall);
                    RenderWalls();
                    PublishMap();
                    BuildFogMask();
                });
                AddMenuItem(menu, "Сделать стеной", () =>
                {
                    wall.Kind = "wall";
                    wall.IsOpen = false;
                    MapService.UpdateWall(wall);
                    RenderWalls();
                    PublishMap();
                    BuildFogMask();
                });
            }
            else
            {
                AddMenuItem(menu, "Сделать дверью", () =>
                {
                    wall.Kind = "door";
                    MapService.UpdateWall(wall);
                    RenderWalls();
                    PublishMap();
                    BuildFogMask();
                });
            }

            menu.Items.Add(BuildSeparator());
            AddMenuItem(menu, "Убрать стену", () =>
            {
                MapService.DeleteWall(wall.Id);
                _scene.Walls.RemoveAll(w => w.Id == wall.Id);
                RenderWalls();
                PublishMap();
                BuildFogMask();
            });

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        /// <summary>Полигон разведанного: точки через «;», координаты через запятую.</summary>
        private static Geometry SeenToGeometry(string points, Matrix matrix)
        {
            var points2 = ParsePoints(points);
            if (points2.Count < 3) return null;

            var figure = new PathFigure { StartPoint = points2[0], IsClosed = true };
            for (int i = 1; i < points2.Count; i++) figure.Segments.Add(new LineSegment(points2[i], false));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Transform = new MatrixTransform(matrix);
            return geometry;
        }

        /// <summary>Запоминает то, что игроки видят сейчас: эти области останутся открытыми.</summary>
        private void RememberCurrentVision()
        {
            var perfWatch = System.Diagnostics.Stopwatch.StartNew();

            if (_scene == null || !_scene.FogOn) return;

            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);
            var geometries = new List<Geometry>();
            var newSeen = new List<string>();

            foreach (var token in _scene.Tokens)
            {
                if (!RevealsFog(token, combined)) continue;

                // в координатах картинки: при показе применится поворот карты
                var geometry = BuildVisionGeometry(token, Matrix.Identity);
                if (geometry != null) geometries.Add(geometry);
            }

            foreach (var geometry in geometries)
            {
                try
                {
                    var path = geometry as PathGeometry;
                    if (path == null || path.Figures.Count == 0) continue;

                    // Центр — это StartPoint фигуры, а не точка сегмента. Без него
                    // сохранённый полигон замыкался хордой и превращался в «шапку»
                    // сектора: при движении жетона оставались куски, а не конус.
                    var points = new List<Point> { path.Figures[0].StartPoint };
                    foreach (var segment in path.Figures[0].Segments)
                        if (segment is LineSegment line) points.Add(line.Point);

                    if (points.Count < 4) continue;

                    string text = PointsToString(points);

                    // Такой участок уже запоминали: не плодим одинаковые строки
                    // при каждом обновлении сцены от мастера
                    if (!AddSeenPoint(text)) continue;
                    newSeen.Add(text);

                    // Общий туман: новый сектор сразу уходит ведущему и остальным.
                    // В режиме «свой туман» разведанное остаётся личным.
                    if (_inRoom &&
                        !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase))
                        RoomSession.Instance.ShareMapSeen(text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MapView] разведанное: {ex.Message}");
                }
            }

            // Одна запись в SQLite вместо отдельного открытия соединения на
            // каждый из четырёх жетонов — это заметная часть фриза при драге.
            if (newSeen.Count > 0)
            {
                try { MapService.AddSeenBatch(_scene.Id, newSeen); }
                catch (Exception ex) { Debug.WriteLine($"[MapView] запись разведанного: {ex.Message}"); }
            }

            PerfLog("разведано", perfWatch.ElapsedMilliseconds,
                $"всего={_seenPoints.Count}, жетонов={_scene.Tokens.Count}");
        }

        // ══════════════════════ ТУМАН ВОЙНЫ ══════════════════════

        /// <summary>
        /// Маска тумана: закрыто всё, кроме того, что видят жетоны игроков.
        /// Круговой обзор вырезает круг, конус — сектор. Мастеру туман
        /// полупрозрачный (видно, что скрыто), игрокам — сплошной чёрный.
        ///
        /// Режим «общий»: игроки видят суммарный обзор отряда, «свой» — только
        /// свой. Так мастер включает «не вижу, что за спиной у соседа».
        /// </summary>
        /// <summary>Загружает разведанное с жёстким пределом последних секторов.</summary>
        private void LoadSeenPoints(int sceneId)
        {
            _seenPoints.Clear();
            _seenSet.Clear();

            var loaded = MapService.LoadSeen(sceneId);
            int from = Math.Max(0, loaded.Count - MaxSeenSectors);
            for (int i = from; i < loaded.Count; i++)
            {
                _seenPoints.Add(loaded[i]);
                _seenSet.Add(loaded[i]);
            }

            _seenLoadedSceneId = sceneId;
            _seenVersion++;
        }

        private void ClearSeenPoints()
        {
            _seenPoints.Clear();
            _seenSet.Clear();
            _seenVersion++;
        }

        /// <summary>Добавляет сектор с дедупликацией и пределом; true — если он новый.</summary>
        private bool AddSeenPoint(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!_seenSet.Add(text)) return false;

            _seenPoints.Add(text);
            _seenVersion++;

            if (_seenPoints.Count > MaxSeenSectors)
            {
                int remove = _seenPoints.Count - MaxSeenSectors;
                for (int i = 0; i < remove; i++) _seenSet.Remove(_seenPoints[i]);
                _seenPoints.RemoveRange(0, remove);
            }

            return true;
        }

        /// <summary>
        /// Геометрия разведанного по одной на сектор. Старые сектора не
        /// разбираются заново: добавляем только новый полигон, а при
        /// повороте/масштабе карты кэш пересобирается целиком.
        /// </summary>
        private List<Geometry> GetSeenGeometries(Matrix matrix)
        {
            if (!matrix.Equals(_seenGeometryMatrix))
            {
                _seenGeometries.Clear();
                _seenGeometryMatrix = matrix;
            }

            var alive = new HashSet<string>(_seenPoints, StringComparer.Ordinal);
            foreach (string stale in _seenGeometries.Keys.Where(k => !alive.Contains(k)).ToList())
                _seenGeometries.Remove(stale);

            var result = new List<Geometry>(_seenPoints.Count);

            foreach (string text in _seenPoints)
            {
                if (!_seenGeometries.TryGetValue(text, out var geometry))
                {
                    geometry = SeenToGeometry(text, matrix);
                    if (geometry == null) continue;

                    try { geometry.Freeze(); }
                    catch { continue; }

                    _seenGeometries[text] = geometry;
                }

                result.Add(geometry);
            }

            return result;
        }

        private void BuildFogMask()
        {
            if (_webMapReady) return;
            var perfWatch = System.Diagnostics.Stopwatch.StartNew();

            if (_scene == null) return;

            if (!_scene.FogOn)
            {
                _fog.Data = null;
                _fog.Visibility = Visibility.Collapsed;
                return;
            }

            _fog.Visibility = Visibility.Visible;

            double worldW = Math.Max(1, _world.Width);
            double worldH = Math.Max(1, _world.Height);
            var outer = new RectangleGeometry(new Rect(0, 0, worldW, worldH));

            var vision = new GeometryGroup { FillRule = FillRule.Nonzero };
            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);
            Matrix matrix = _content.RenderTransform is MatrixTransform mt ? mt.Matrix : Matrix.Identity;

            foreach (var token in _scene.Tokens)
            {
                if (token.Hidden && !_isDm) continue;
                if (!RevealsFog(token, combined)) continue;

                var geometry = BuildVisionGeometry(token, matrix);
                if (geometry != null) vision.Children.Add(geometry);
            }

            // разведанные области: то, что уже видели, туманом не закрываем.
            // Геометрия кэшируется по секторам, иначе 240 полигонов разбирались
            // на каждый кадр.
            var seenGeometries = GetSeenGeometries(matrix);

            if (_seenUnion == null || _seenUnionVersion != _seenVersion ||
                !_seenUnionMatrix.Equals(matrix))
            {
                try
                {
                    // Не булево объединение, а группа: Combine на сотнях
                    // полигонов мог потерять кусок и сам по себе стоил дорого.
                    var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                    foreach (var geometry in seenGeometries)
                    {
                        if (geometry != null) group.Children.Add(geometry);
                    }

                    if (group.Children.Count > 0)
                    {
                        try { group.Freeze(); } catch { }
                        _seenUnion = group;
                    }
                    else
                    {
                        _seenUnion = null;
                    }
                }
                catch
                {
                    _seenUnion = null;
                }

                _seenUnionVersion = _seenVersion;
                _seenUnionMatrix = matrix;
            }

            var known = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var child in vision.Children) known.Children.Add(child);
            if (_seenUnion != null) known.Children.Add(_seenUnion);

            // Один слой «неизвестного»: и мастер, и игрок видят открытым всё,
            // что уже разведано. Раньше у игрока плёнка вырезала только текущий
            // обзор, а разведанное закрывал второй чёрный слой — поэтому карта
            // у клиента оставалась чёрной, хотя хост видел её целиком.
            _fog.Data = known.Children.Count == 0
                ? (Geometry)outer
                : new CombinedGeometry(GeometryCombineMode.Exclude, outer, known);

            // Второй слой больше не нужен: всё «неизвестное» уже закрыто выше.
            // Оставляем его пустым, чтобы не двоить заливку.
            _fogDark.Data = null;

            DumpMapState("после построения тумана");
            PerfLog("туман", perfWatch.ElapsedMilliseconds,
                $"разведано={_seenPoints.Count}, жетонов={_scene.Tokens.Count}, стен={_scene.Walls.Count}, " +
                $"штрихов={_scene.Strokes.Count}");
        }

        /// <summary>
        /// Диагностика тормозов: в map-perf.log пишутся только операции дольше
        /// 40 мс — по ним видно, растёт ли туман, следы или пересборка карты.
        /// </summary>
        private static void PerfLog(string what, long ms, string detail)
        {
            if (ms < 40) return;

            try
            {
                string dir = HMWT_MUI.PhotoHelper.GetWritableRoot();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "map-perf.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {what}: {ms} мс · {detail}\r\n");
            }
            catch { }
        }

        /// <summary>
        /// Куда на экране попали полотно карты и жетоны. Нужно проверкам
        /// интерфейса: нажать на жетон или провести ластиком можно только зная
        /// настоящие координаты, а Border в дереве автоматизации не виден.
        /// Пишется только при HMWT_MAP_DUMP=1.
        /// </summary>
        private void DumpUiGeometry()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("HMWT_MAP_DUMP") != "1") return;
                if (_scene == null || !IsVisible || _viewport.ActualWidth < 10) return;

                Point tl = _viewport.PointToScreen(new Point(0, 0));
                Point br = _viewport.PointToScreen(new Point(_viewport.ActualWidth, _viewport.ActualHeight));

                var text = new System.Text.StringBuilder();
                text.AppendLine("=== где карта на экране ===");
                text.AppendLine($"viewport: {tl.X:0},{tl.Y:0} - {br.X:0},{br.Y:0} " +
                                $"({_viewport.ActualWidth:0}x{_viewport.ActualHeight:0})");
                text.AppendLine($"scale: {_scale:0.####}; tx: {_tx:0.##}; ty: {_ty:0.##}; " +
                                $"scene: {_scene.WidthPx}x{_scene.HeightPx}");

                foreach (var child in _tokenLayer.Children)
                {
                    if (!(child is Border host) || !(host.Tag is MapTokenData token)) continue;
                    if (host.ActualWidth <= 0 || host.ActualHeight <= 0) continue;

                    Point c = host.PointToScreen(new Point(host.ActualWidth / 2, host.ActualHeight / 2));
                    text.AppendLine($"token {token.Id} «{token.Label}»: {c.X:0},{c.Y:0} " +
                                    $"({host.ActualWidth:0}x{host.ActualHeight:0})");
                }

                string dir = PhotoHelper.GetWritableRoot();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "map-ui.txt"), text.ToString(), new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] геометрия для проверки: {ex.Message}");
            }
        }

        /// <summary>
        /// Диагностика: при HMWT_MAP_DUMP=1 состояние карты пишется в
        /// map-dump.txt. Помогает разбирать «у игрока не рассеивается туман».
        /// </summary>
        private void DumpMapState(string where)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("HMWT_MAP_DUMP") != "1") return;
                if (_scene == null) return;

                string dir = PhotoHelper.GetWritableRoot();
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "map-dump.txt");

                var text = new System.Text.StringBuilder();
                text.AppendLine("=== карта: " + where + " ===");
                text.AppendLine("время: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                text.AppendLine("версия: " + NetVersions.AppVersion + ", режим: " + (_remote ? "игрок" : _isDm ? "мастер" : "просмотр"));
                text.AppendLine("сцена: " + _scene.Name + " " + _scene.WidthPx + "x" + _scene.HeightPx +
                                ", клетка " + _scene.GridPx + ", туман " + _scene.FogOn + ", режим тумана " + _scene.FogMode);
                text.AppendLine("жетонов: " + _scene.Tokens.Count + ", стен: " + _scene.Walls.Count +
                                ", штрихов: " + _scene.Strokes.Count + ", фигур: " + _scene.Shapes.Count);
                text.AppendLine("разведано участков: " + _seenPoints.Count);

                foreach (var token in _scene.Tokens)
                {
                    text.AppendLine($"  жетон {token.Id}: «{token.Label}», игрок={token.IsPlayer}, " +
                                    $"бой={token.CombatantId}, обзор={token.ViewAngle}°, дальность={token.RevealCells}, " +
                                    $"поз=({token.X:0},{token.Y:0}), скрыт={token.Hidden}");
                }

                text.AppendLine("слои тумана: плёнка=" + (_fog.Data != null) + ", чёрный=" + (_fogDark.Data != null));

                File.WriteAllText(file, text.ToString(), new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] дамп: {ex.Message}");
            }
        }
        /// <summary>Чей обзор рассеивает туман у этого зрителя.</summary>
        private bool RevealsFog(MapTokenData token, bool combined)
        {
            // У игрока своей базы боёв нет: признак «это жетон игрока» приходит
            // вместе с картой, иначе туман у него не рассеивался вообще.
            bool player = token.IsPlayer;
            if (!player && _combatants.TryGetValue(token.CombatantId, out var linked) &&
                linked.Combatant != null && linked.Combatant.Kind == CombatantKind.Player)
                player = true;

            if (!player) return false;          // монстры туман не рассеивают
            if (_isDm) return true;              // у мастера — обзор всего отряда
            if (combined) return true;           // общий туман: обзор всех игроков
            return CanMoveToken(token);          // свой туман: только собственный
        }

        /// <summary>
        /// Виден ли чужой жетон из-за стены. Туман закрывает область, но сам
        /// жетон рисовался поверх и «просвечивал» через стену. Проверяем
        /// прямую от каждого своего/отрядного жетона до центра и углов цели:
        /// если хоть одна прямая не пересекает закрытую стену — виден.
        /// </summary>
        private bool IsTokenVisibleByLos(MapTokenData token, bool combined)
        {
            if (token == null || _scene == null) return true;
            if (_showPlayersCheck.IsChecked == true && token.IsPlayer && !token.Hidden) return true;
            if (_isDm) return true;              // мастер видит всех
            if (CanMoveToken(token)) return true; // свой жетон виден всегда

            var viewers = _scene.Tokens
                .Where(t => t.Id != token.Id && RevealsFog(t, combined))
                .ToList();

            if (viewers.Count == 0) return false;

            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            double half = Math.Max(cell * 0.25, cell * 0.35 * Math.Max(1, token.SizeCells));

            var targets = new[]
            {
                new Point(token.X, token.Y),
                new Point(token.X - half, token.Y - half),
                new Point(token.X + half, token.Y - half),
                new Point(token.X - half, token.Y + half),
                new Point(token.X + half, token.Y + half)
            };

            foreach (var viewer in viewers)
            {
                var from = new Point(viewer.X, viewer.Y);
                foreach (var target in targets)
                    if (HasClearLine(from, target)) return true;
            }

            return false;
        }

        private bool HasClearLine(Point from, Point to)
        {
            if (_scene?.Walls == null || _scene.Walls.Count == 0) return true;

            foreach (var wall in _scene.Walls)
            {
                if (wall == null || wall.IsOpen) continue;
                if (SegmentsCross(from, to,
                        new Point(wall.X1, wall.Y1), new Point(wall.X2, wall.Y2)))
                    return false;
            }
            return true;
        }

        private static bool SegmentsCross(Point a, Point b, Point c, Point d)
        {
            double d1 = Cross(c, d, a);
            double d2 = Cross(c, d, b);
            double d3 = Cross(a, b, c);
            double d4 = Cross(a, b, d);

            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross(Point origin, Point a, Point b)
            => (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);

        /// <summary>Прячет чужие жетоны, закрытые стеной от всех своих жетонов.</summary>
        private void RefreshTokenVisibility()
        {
            if (_webMapReady) return;
            if (_scene == null) return;

            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);

            foreach (var child in _tokenLayer.Children)
            {
                if (!(child is Border host) || !(host.Tag is MapTokenData token)) continue;
                host.Visibility = IsTokenVisibleByLos(token, combined)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        /// <summary>Круг или сектор обзора жетона в координатах мира.</summary>
        private Geometry BuildRevealGeometry(MapTokenData token, Matrix matrix)
        {
            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            double radius = cell * 6;
            var center = new Point(token.X, token.Y);

            Geometry geometry;

            if (token.ViewAngle >= 359.9)
            {
                geometry = new EllipseGeometry(center, radius, radius);
            }
            else
            {
                double half = Math.Max(5, Math.Min(180, token.ViewAngle / 2));
                Point start = PointOnCircle(center, radius, token.Facing - half);
                Point end = PointOnCircle(center, radius, token.Facing + half);

                var figure = new PathFigure { StartPoint = center, IsClosed = true };
                figure.Segments.Add(new LineSegment(start, true));
                figure.Segments.Add(new ArcSegment
                {
                    Point = end,
                    Size = new Size(radius, radius),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = half * 2 > 180
                });
                figure.Segments.Add(new LineSegment(center, true));

                var path = new PathGeometry();
                path.Figures.Add(figure);
                geometry = path;
            }

            geometry.Transform = new MatrixTransform(matrix);
            return geometry;
        }

        /// <summary>Режим тумана: общий обзор отряда или у каждого свой.</summary>
        private void SetFogMode(string mode)
        {
            if (_scene == null || !_isDm) return;

            _scene.FogMode = string.Equals(mode, "own", StringComparison.OrdinalIgnoreCase) ? "own" : "combined";
            MapService.SaveScene(_scene);
            PublishMap();
            BuildFogMask();
        }

        // ══════════════════════ ПОЛЕ ЗРЕНИЯ ══════════════════════

        private void SetViewAngle(MapTokenData token, double angle)
        {
            token.ViewAngle = angle;
            MapService.SaveToken(token);
            PublishMap();
            RenderFov();
            RememberCurrentVision();
            BuildFogMask();
        }

        private void RotateFacing(MapTokenData token, double delta)
        {
            token.Facing = ((token.Facing + delta) % 360 + 360) % 360;
            MapService.SaveToken(token);
            PublishMap();
            RenderFov();
            RememberCurrentVision();
            BuildFogMask();
        }

        private void RenderFov()
        {
            if (_webMapReady) return;
            _fovLayer.Children.Clear();
            if (_scene == null) return;

            foreach (var token in _scene.Tokens)
            {
                if (token.Hidden && !_isDm) continue;
                if (!_isDm && !CanMoveToken(token)) continue;

                if (token.ViewAngle >= 359.9)
                {
                    // Круговой обзор: полупрозрачный круг дальности, чтобы игрок
                    // видел, докуда достаёт его взгляд
                    double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                    double radius = Math.Max(cell, cell * Math.Max(1, token.RevealCells));

                    var circle = new System.Windows.Shapes.Ellipse
                    {
                        Width = radius * 2,
                        Height = radius * 2,
                        Fill = new SolidColorBrush(Color.FromArgb(0x16, 0x5F, 0xD4, 0xDD)),
                        Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x5F, 0xD4, 0xDD)),
                        StrokeThickness = 1.2,
                        IsHitTestVisible = false
                    };

                    Canvas.SetLeft(circle, token.X - radius);
                    Canvas.SetTop(circle, token.Y - radius);
                    _fovLayer.Children.Add(circle);
                    continue;
                }

                var sector = BuildSectorVisual(token);
                if (sector != null) _fovLayer.Children.Add(sector);
            }
        }

        /// <summary>След жетона: добавляет точку маршрута и перерисовывает линию.</summary>
        private void AppendTrail(MapTokenData token, Point point)
        {
            if (_scene == null || token == null) return;

            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;

            if (!_trails.TryGetValue(token.Id, out var list))
            {
                list = new List<Point>();
                _trails[token.Id] = list;
            }

            if (list.Count > 0)
            {
                Point last = list[list.Count - 1];
                if (Math.Abs(last.X - point.X) < cell * 0.3 && Math.Abs(last.Y - point.Y) < cell * 0.3)
                    return;
            }

            list.Add(point);
            if (list.Count > 160) list.RemoveRange(0, list.Count - 160);

            _trailTouched[token.Id] = DateTime.UtcNow;
            if (!_trailTimer.IsEnabled) _trailTimer.Start();

            // Перерисовываем след не на каждый пиксель, иначе длинное
            // перетаскивание забивает Dispatcher очередью Polyline.
            if ((DateTime.UtcNow - _lastTrailRender).TotalMilliseconds >= 80)
            {
                _lastTrailRender = DateTime.UtcNow;
                RenderTrails();
            }
        }

        /// <summary>
        /// Сравнивает старую и новую карту: где жетон сдвинулся, там добавляем
        /// точку следа. Так след видят и ведущий, и остальные игроки.
        /// </summary>
        private void TrackTokenMoves(MapSceneData oldScene, MapSceneData newScene)
        {
            if (oldScene?.Tokens == null || newScene?.Tokens == null) return;

            foreach (var token in newScene.Tokens)
            {
                var old = oldScene.Tokens.FirstOrDefault(t => t.Id == token.Id);
                if (old == null) continue;
                if (Math.Abs(old.X - token.X) < 0.5 && Math.Abs(old.Y - token.Y) < 0.5) continue;

                AppendTrail(token, new Point(token.X, token.Y));
            }
        }

        private void RenderTrails()
        {
            if (_webMapReady) return;
            var perfWatch = System.Diagnostics.Stopwatch.StartNew();
            _trailLayer.Children.Clear();
            if (_scene == null) return;

            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            var brush = new SolidColorBrush(Color.FromArgb(0xB0, 0xE8, 0xC0, 0x7F));
            brush.Freeze();
            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);

            foreach (var pair in _trails)
            {
                var token = _scene.Tokens.FirstOrDefault(t => t.Id == pair.Key);
                if (token == null) continue;
                if (token.Hidden && !_isDm) continue;
                if (!IsTokenVisibleByLos(token, combined)) continue;
                if (pair.Value.Count < 2) continue;

                var line = new System.Windows.Shapes.Polyline
                {
                    Stroke = brush,
                    StrokeThickness = Math.Max(2, cell * 0.07),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };

                foreach (var point in pair.Value) line.Points.Add(point);
                _trailLayer.Children.Add(line);
            }

            PerfLog("следы", perfWatch.ElapsedMilliseconds,
                $"линий={_trailLayer.Children.Count}, точек={_trails.Sum(p => p.Value.Count)}");
        }

        /// <summary>След живёт несколько секунд после остановки, потом тает.</summary>
        private void RemoveOldTrails()
        {
            DateTime now = DateTime.UtcNow;

            var stale = _trailTouched
                .Where(p => (now - p.Value).TotalSeconds > 6)
                .Select(p => p.Key)
                .ToList();

            foreach (int id in stale)
            {
                _trails.Remove(id);
                _trailTouched.Remove(id);
            }

            // Жетон мог исчезнуть со сцены — его след тоже больше не нужен
            if (_scene != null)
            {
                var alive = new HashSet<int>(_scene.Tokens.Select(t => t.Id));
                foreach (int id in _trails.Keys.Where(id => !alive.Contains(id)).ToList())
                {
                    _trails.Remove(id);
                    _trailTouched.Remove(id);
                }
            }

            RenderTrails();
            if (_trails.Count == 0) _trailTimer.Stop();
        }

        private void ClearTrail(int tokenId)
        {
            _trails.Remove(tokenId);
            _trailTouched.Remove(tokenId);
            RenderTrails();
        }

        private System.Windows.Shapes.Path BuildSectorVisual(MapTokenData token)
        {
            try
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                double radius = cell * Math.Max(1, token.RevealCells > 0 ? token.RevealCells : 6);
                double half = Math.Max(5, Math.Min(180, token.ViewAngle / 2));

                _combatants.TryGetValue(token.CombatantId, out var linked);
                bool player = linked.Combatant != null && linked.Combatant.Kind == CombatantKind.Player;
                Color baseColor = player ? Color.FromRgb(0x74, 0xA9, 0xFF) : Color.FromRgb(0xE0, 0x6A, 0x63);

                var fill = new SolidColorBrush(Color.FromArgb(0x17, baseColor.R, baseColor.G, baseColor.B));
                var stroke = new SolidColorBrush(Color.FromArgb(0x66, baseColor.R, baseColor.G, baseColor.B));
                fill.Freeze();
                stroke.Freeze();

                Point center = new Point(token.X, token.Y);
                Point start = PointOnCircle(center, radius, token.Facing - half);
                Point end = PointOnCircle(center, radius, token.Facing + half);

                var figure = new PathFigure { StartPoint = center, IsClosed = true };
                figure.Segments.Add(new LineSegment(start, true));
                figure.Segments.Add(new ArcSegment
                {
                    Point = end,
                    Size = new Size(radius, radius),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = half * 2 > 180
                });
                figure.Segments.Add(new LineSegment(center, true));

                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                geometry.Freeze();

                return new System.Windows.Shapes.Path
                {
                    Data = geometry,
                    Fill = fill,
                    Stroke = stroke,
                    StrokeThickness = Math.Max(1, cell / 40),
                    IsHitTestVisible = false
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] BuildSectorVisual: {ex.Message}");
                return null;
            }
        }

        private static Point PointOnCircle(Point center, double radius, double degrees)
        {
            double rad = degrees * Math.PI / 180.0;
            return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
        }

        // ══════════════════════ ФИГУРЫ ОБЛАСТЕЙ ══════════════════════

        private static readonly (string Kind, string Title)[] ShapeKinds =
        {
            ("circle", "Круг"),
            ("square", "Квадрат"),
            ("cone", "Конус"),
            ("line", "Линия")
        };

        private void OpenShapeMenu(FrameworkElement target)
        {
            if (!_isDm && !_remote) return;

            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");
            double radius = ParseShapeSize();

            foreach (var kind in ShapeKinds)
            {
                string kindKey = kind.Kind;
                string title = kind.Title;
                var item = new MenuItem { Header = title, IsCheckable = false };
                ApplyMenuItemStyle(item);
                item.Click += (s, e) =>
                {
                    // размер берём из числового поля, а не из ползунка
                    _pendingShape = new MapShapeData { Kind = kindKey, RadiusCells = Math.Max(1, ParseShapeSize()) };
                    DmRolls.Toast(this, $"{title} {_pendingShape.RadiusCells:0.#} кл.: щёлкните по карте");
                };
                menu.Items.Add(item);
            }

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private double ParseShapeSize()
        {
            string text = (_shapeField.Text ?? "").Trim().Replace(',', '.');
            double value;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                value = 4;
            value = Math.Max(0.5, Math.Min(20, value));
            _shapeField.Text = value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return value;
        }

        private void PlaceShape(Point point)
        {
            if (_scene == null || _pendingShape == null) return;
            if (!_isDm && !_remote) return;

            var shape = new MapShapeData
            {
                SceneId = _scene.Id,
                Kind = _pendingShape.Kind,
                RadiusCells = _pendingShape.RadiusCells,
                X = point.X,
                Y = point.Y,
                Angle = 0,
                Color = _brushColor
            };

            if (_scene.SnapToGrid)
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                shape.X = Math.Round((shape.X - cell / 2) / cell) * cell + cell / 2;
                shape.Y = Math.Round((shape.Y - cell / 2) / cell) * cell + cell / 2;
            }

            if (_remote)
            {
                RoomSession.Instance.SendMapShape("create", shape);
                _pendingShape = null;
                return;
            }

            if (MapService.SaveShape(shape) > 0)
            {
                _scene.Shapes.Add(shape);
                PushUndo(() =>
                {
                    MapService.DeleteShape(shape.Id);
                    _scene.Shapes.RemoveAll(x => x.Id == shape.Id);
                    PublishMap();
                });
                PublishMap();
                RenderShapes();
            }

            _pendingShape = null;
        }

        private void RenderShapes()
        {
            if (_webMapReady) return;
            _shapeLayer.Children.Clear();
            if (_scene == null) return;

            foreach (var shape in _scene.Shapes)
            {
                var visual = BuildShapeVisual(shape);
                if (visual != null) _shapeLayer.Children.Add(visual);
            }
        }

        private FrameworkElement BuildShapeVisual(MapShapeData shape)
        {
            try
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                double radius = Math.Max(cell / 2, cell * Math.Max(0.5, shape.RadiusCells));

                Color color = Color.FromRgb(0xB9, 0x8B, 0xFF);
                if (!string.IsNullOrWhiteSpace(shape.Color) && TryBrush(shape.Color, out var custom) &&
                    custom is SolidColorBrush solid)
                    color = solid.Color;

                var fill = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
                var stroke = new SolidColorBrush(Color.FromArgb(0xCC, color.R, color.G, color.B));
                fill.Freeze();
                stroke.Freeze();

                if (string.Equals(shape.Kind, "circle", StringComparison.OrdinalIgnoreCase))
                {
                    var ellipse = new System.Windows.Shapes.Ellipse
                    {
                        Width = radius * 2,
                        Height = radius * 2,
                        Fill = fill,
                        Stroke = stroke,
                        StrokeThickness = Math.Max(1.5, cell / 24),
                        Tag = shape,
                        ToolTip = shape.Label
                    };
                    Canvas.SetLeft(ellipse, shape.X - radius);
                    Canvas.SetTop(ellipse, shape.Y - radius);
                    AttachShapeEvents(ellipse, shape);
                    return ellipse;
                }

                if (string.Equals(shape.Kind, "square", StringComparison.OrdinalIgnoreCase))
                {
                    var rect = new System.Windows.Shapes.Rectangle
                    {
                        Width = radius * 2,
                        Height = radius * 2,
                        Fill = fill,
                        Stroke = stroke,
                        StrokeThickness = Math.Max(1.5, cell / 24),
                        Tag = shape,
                        ToolTip = shape.Label
                    };
                    Canvas.SetLeft(rect, shape.X - radius);
                    Canvas.SetTop(rect, shape.Y - radius);
                    AttachShapeEvents(rect, shape);
                    return rect;
                }

                var geometry = new PathGeometry();
                var figure = new PathFigure { StartPoint = new Point(shape.X, shape.Y), IsClosed = true };

                if (string.Equals(shape.Kind, "cone", StringComparison.OrdinalIgnoreCase))
                {
                    double halfAngle = 45;
                    Point start = PointOnCircle(new Point(shape.X, shape.Y), radius, shape.Angle - halfAngle);
                    Point end = PointOnCircle(new Point(shape.X, shape.Y), radius, shape.Angle + halfAngle);

                    figure.Segments.Add(new LineSegment(start, true));
                    figure.Segments.Add(new ArcSegment
                    {
                        Point = end,
                        Size = new Size(radius, radius),
                        SweepDirection = SweepDirection.Clockwise,
                        IsLargeArc = false
                    });
                    figure.Segments.Add(new LineSegment(new Point(shape.X, shape.Y), true));
                }
                else
                {
                    Point end = PointOnCircle(new Point(shape.X, shape.Y), radius, shape.Angle);
                    figure.IsClosed = false;
                    figure.Segments.Add(new LineSegment(end, true));
                }

                geometry.Figures.Add(figure);
                geometry.Freeze();

                var path = new System.Windows.Shapes.Path
                {
                    Data = geometry,
                    Fill = figure.IsClosed ? fill : null,
                    Stroke = stroke,
                    StrokeThickness = Math.Max(4, cell / 6),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Tag = shape,
                    ToolTip = shape.Label
                };

                AttachShapeEvents(path, shape);
                return path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] BuildShapeVisual: {ex.Message}");
                return null;
            }
        }

        private void AttachShapeEvents(FrameworkElement visual, MapShapeData shape)
        {
            if (!_isDm && !_remote) return;

            visual.Cursor = Cursors.SizeAll;
            visual.MouseLeftButtonDown += (s, e) =>
            {
                _dragShape = shape;
                Point point = e.GetPosition(_shapeLayer);
                _shapeOffset = new Point(point.X - shape.X, point.Y - shape.Y);
                visual.CaptureMouse();
                e.Handled = true;
            };
            visual.MouseMove += (s, e) =>
            {
                if (_dragShape == null || _dragShape.Id != shape.Id) return;

                Point point = e.GetPosition(_shapeLayer);
                shape.X = point.X - _shapeOffset.X;
                shape.Y = point.Y - _shapeOffset.Y;

                if (_scene.SnapToGrid)
                {
                    double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                    shape.X = Math.Round((shape.X - cell / 2) / cell) * cell + cell / 2;
                    shape.Y = Math.Round((shape.Y - cell / 2) / cell) * cell + cell / 2;
                }

                RenderShapes();
            };
            visual.MouseLeftButtonUp += (s, e) =>
            {
                if (_dragShape == null) return;
                visual.ReleaseMouseCapture();
                CommitShape(shape, "update");
                _dragShape = null;
            };
            visual.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowShapeMenu(visual, shape);
            };
        }

        /// <summary>Хост сохраняет фигуру сам, игрок отправляет изменение ведущему.</summary>
        private void CommitShape(MapShapeData shape, string action)
        {
            if (shape == null) return;

            bool delete = string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase);

            if (_remote)
            {
                if (delete) _scene?.Shapes.RemoveAll(s => s.Id == shape.Id);
                RoomSession.Instance.SendMapShape(action, shape);
                RenderShapes();
                return;
            }

            if (delete)
            {
                MapService.DeleteShape(shape.Id);
                _scene?.Shapes.RemoveAll(s => s.Id == shape.Id);
            }
            else
            {
                MapService.SaveShape(shape);
            }

            PublishMap();
            RenderShapes();
        }

        private void ShowShapeMenu(FrameworkElement target, MapShapeData shape)
        {
            if (!_isDm && !_remote) return;

            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");

            AddMenuItem(menu, $"Размер: {shape.RadiusCells:0.#} кл. — изменить…", () =>
            {
                string answer = Windows.NotePromptWindow.AskText(Window.GetWindow(this),
                    "Размер фигуры", "Радиус в клетках", shape.RadiusCells.ToString("0.#"),
                    "Применить");
                if (string.IsNullOrWhiteSpace(answer)) return;
                double value;
                if (!double.TryParse(answer.Trim().Replace(',', '.'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
                    return;
                shape.RadiusCells = Math.Max(0.5, Math.Min(20, value));
                CommitShape(shape, "update");
            });
            AddMenuItem(menu, "Больше на 1 клетку", () =>
            {
                shape.RadiusCells = Math.Min(20, shape.RadiusCells + 1);
                CommitShape(shape, "update");
            });
            AddMenuItem(menu, "Меньше на 1 клетку", () =>
            {
                shape.RadiusCells = Math.Max(0.5, shape.RadiusCells - 1);
                CommitShape(shape, "update");
            });

            var colorMenu = new MenuItem { Header = "Цвет фигуры" };
            ApplyMenuItemStyle(colorMenu);
            foreach (var pair in new[]
            {
                ("Фиолетовый", "#FFB98BFF"), ("Красный", "#FFE06A63"),
                ("Зелёный", "#FF7FD98A"), ("Синий", "#FF6FA8FF"),
                ("Золотой", "#FFE8C07F"), ("Серый", "#FF9AA4BF")
            })
            {
                var colorItem = new MenuItem { Header = pair.Item1 };
                ApplyMenuItemStyle(colorItem);
                colorItem.Click += (s, e) =>
                {
                    shape.Color = pair.Item2;
                    CommitShape(shape, "update");
                };
                colorMenu.Items.Add(colorItem);
            }
            menu.Items.Add(colorMenu);

            AddMenuItem(menu, "Повернуть ⟳", () =>
            {
                shape.Angle = ((shape.Angle + 45) % 360 + 360) % 360;
                CommitShape(shape, "update");
            });
            AddMenuItem(menu, "Повернуть ⟲", () =>
            {
                shape.Angle = ((shape.Angle - 45) % 360 + 360) % 360;
                CommitShape(shape, "update");
            });
            menu.Items.Add(BuildSeparator());
            AddMenuItem(menu, "Убрать фигуру", () =>
            {
                CommitShape(shape, "delete");
            });

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        /// <summary>Запоминает, как отменить действие (Ctrl+Z).</summary>
        private void PushUndo(Action restore)
        {
            if (restore == null) return;

            _undoStack.Push(restore);
            while (_undoStack.Count > 60)
            {
                var keep = _undoStack.ToArray();
                _undoStack.Clear();
                for (int i = 0; i < 60 && i < keep.Length; i++) _undoStack.Push(keep[keep.Length - 1 - i]);
            }
        }

        /// <summary>Отмена последнего действия: Ctrl+Z.</summary>
        private void Undo()
        {
            if (_undoStack.Count == 0)
            {
                DmRolls.Toast(this, "Отменять нечего");
                return;
            }

            try
            {
                _undoStack.Pop().Invoke();
                RenderStrokes();
                RenderWalls();
                RenderShapes();
                RebuildTokens();
                BuildFogMask();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] отмена: {ex.Message}");
            }
        }
        /// <summary>Q/E крутят взгляд выбранного жетона: конус обзора едет за ним.</summary>
        private void MapView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Undo();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Q && e.Key != Key.E) return;
            if (_scene == null || _selectedTokenId <= 0) return;

            // во время перетаскивания крутим тот жетон, который держим
            var token = _dragToken ?? _scene.Tokens.FirstOrDefault(t => t.Id == _selectedTokenId);
            if (token == null) return;
            if (!CanMoveToken(token)) return;

            double delta = e.Key == Key.E ? 15 : -15;
            token.Facing = ((token.Facing + delta) % 360 + 360) % 360;

            if (_remote)
            {
                // У игрока своего файла сцены нет: поворот нужно отправить хосту,
                // иначе он вернётся прежним со следующим снимком карты
                RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, token.Facing);
                RememberCurrentVision();
            }
            else
            {
                MapService.SaveToken(token);
                PublishMap();
                RememberCurrentVision();
            }

            RenderFov();
            BuildFogMask();
            e.Handled = true;
        }

        /// <summary>
        /// Выделяет жетон. Раньше здесь пересобирался весь слой, из-за чего
        /// перетаскивание ломалось: элемент, который держал мышь, заменялся новым.
        /// Теперь только подсвечиваем кольцо у уже нарисованных жетонов.
        /// </summary>
        private void SelectToken(MapTokenData token)
        {
            if (token == null) return;

            _selectedTokenId = token.Id;

            _suppressRevealEvents = true;
            try
            {
                _revealSlider.Value = Math.Max(_revealSlider.Minimum,
                    Math.Min(_revealSlider.Maximum, token.RevealCells > 0 ? token.RevealCells : 6));
            }
            finally { _suppressRevealEvents = false; }

            UpdateSelectionVisuals();
            Focus();
        }

        private void UpdateSelectionVisuals()
        {
            foreach (var child in _tokenLayer.Children)
            {
                if (!(child is Border host) || !(host.Tag is MapTokenData token)) continue;
                if (!(host.Child is Grid grid) || grid.Children.Count == 0) continue;
                if (!(grid.Children[0] is Border circle)) continue;

                bool selected = token.Id == _selectedTokenId;
                circle.BorderThickness = new Thickness(selected ? 3 : 2);
                if (selected) circle.BorderBrush = DmUiKit.B("Gold");
            }
        }
        // ══════════════════════ ЖЕТОНЫ ══════════════════════

        /// <summary>Кто может двигать жетон: мастер — любые, игрок — только своего персонажа.</summary>
        private bool CanMoveToken(MapTokenData token)
        {
            if (token == null) return false;
            if (_isDm) return true;

            if (_remote)
                return token.CombatantId > 0 &&
                       NameMatch.SamePerson(token.Label, SelfName);

            if (!_combatants.TryGetValue(token.CombatantId, out var linked)) return false;
            if (linked.Combatant == null) return false;
            if (linked.Combatant.Kind != CombatantKind.Player) return false;

            return NameMatch.SamePerson(linked.Combatant.Name, SelfName);
        }

        private void RebuildTokens()
        {
            if (_webMapReady) return;
            _visualMoves.Clear();
            _visualTimer.Stop();
            _tokenLayer.Children.Clear();
            if (_scene == null) return;

            foreach (var token in _scene.Tokens)
            {
                // игрок скрытых жетонов не видит вовсе
                if (token.Hidden && !_isDm) continue;

                var host = BuildTokenVisual(token);
                if (host != null)
                {
                    // Игрок всегда выше НПС: иначе его жетон можно потерять
                    // под чужим и не найти мышью. Выбранный/перетаскиваемый
                    // дополнительно поднимается в момент клика.
                    bool playerToken = token.IsPlayer || CanMoveToken(token);
                    Panel.SetZIndex(host, playerToken ? 1000 : 100);
                    _tokenLayer.Children.Add(host);
                }
            }

            RenderFov();
            RefreshTokenVisibility();
        }

        private Border BuildTokenVisual(MapTokenData token)
        {
            try
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                double size = Math.Max(24, cell * (token.SizeCells <= 0 ? 1 : token.SizeCells));

                _combatants.TryGetValue(token.CombatantId, out var linked);
                CombatantData combatant = linked.Combatant;

                string label = !string.IsNullOrWhiteSpace(token.Label)
                    ? token.Label
                    : combatant?.Name ?? "Жетон";

                Brush fill = DmUiKit.B("Slate");
                bool hasPhoto = false;

                // картинка: скачанный файл по хэшу, затем путь с машины ведущего,
                // затем бестиарий — так жетон показывает портрет и у игрока
                string photoFile = RemoteImagePath(token.PhotoHash);
                if (photoFile == null && !string.IsNullOrWhiteSpace(token.PhotoPath) && File.Exists(token.PhotoPath))
                    photoFile = token.PhotoPath;

                var monster = combatant != null && combatant.MonsterId > 0
                    ? BestiaryService.Load(combatant.MonsterId)
                    : null;

                if (photoFile != null || (monster != null && !string.IsNullOrWhiteSpace(monster.PhotoPath)))
                {
                    string full = photoFile;
                    if (full == null)
                    {
                        full = PhotoHelper.ResolvePhotoPath(monster.PhotoPath);
                    }

                    if (!string.IsNullOrEmpty(full) && File.Exists(full))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(full);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        // Uniform: существо видно целиком, голову не режет
                        fill = new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
                        hasPhoto = true;
                    }
                }

                bool player = combatant != null && combatant.Kind == CombatantKind.Player;
                Brush ring = !string.IsNullOrWhiteSpace(token.Color) && TryBrush(token.Color, out var custom)
                    ? custom
                    : player ? DmUiKit.B("Frost") : DmUiKit.B("Danger");

                var circle = new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(size / 2),
                    Background = fill,
                    BorderBrush = token.Id == _selectedTokenId ? DmUiKit.B("Gold") : ring,
                    BorderThickness = new Thickness(token.Id == _selectedTokenId ? 3 : 2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock
                    {
                        Text = hasPhoto ? "" : label.Substring(0, 1).ToUpperInvariant(),
                        FontFamily = DmUiKit.Display,
                        FontSize = Math.Min(72, size / 2.4),
                        Foreground = DmUiKit.B("Ink"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };

                // подпись: имя и хиты отдельными строками, чтобы «44/44» не обрезалось
                var captionStack = new StackPanel();
                captionStack.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 10,
                    Foreground = DmUiKit.B("Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                if (combatant != null && combatant.MaxHitPoints > 0)
                {
                    captionStack.Children.Add(new TextBlock
                    {
                        Text = combatant.HitPoints + " / " + combatant.MaxHitPoints,
                        FontSize = 9.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = combatant.HitPoints <= 0
                            ? DmUiKit.B("Danger")
                            : combatant.HitPoints * 3 <= combatant.MaxHitPoints
                                ? DmUiKit.B("Danger")
                                : DmUiKit.B("Gold"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                }

                double captionWidth = Math.Max(size, 110);
                var caption = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x09, 0x0C, 0x16)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, size + 2, 0, 0),
                    Child = captionStack
                };

                var hostGrid = new Grid { Width = captionWidth, Height = size + 34 };
                hostGrid.Children.Add(circle);
                hostGrid.Children.Add(caption);

                var host = new Border
                {
                    Width = captionWidth,
                    Height = size + 34,
                    Background = Brushes.Transparent,
                    Child = hostGrid,
                    Opacity = token.Hidden ? 0.45 : 1.0,
                    ToolTip = label + (combatant != null && combatant.MaxHitPoints > 0
                        ? $"  {combatant.HitPoints}/{combatant.MaxHitPoints}" : ""),
                    Tag = token
                };

                // Идентификатор для проверок интерфейса и экранных дикторов:
                // жетон можно найти по имени, не вычисляя координаты на глаз
                System.Windows.Automation.AutomationProperties.SetAutomationId(host, "maptoken_" + token.Id);
                System.Windows.Automation.AutomationProperties.SetName(host, label);

                // карта может быть повёрнута: жетону даём обратный поворот,
                // чтобы имя и хиты остались прямыми
                int viewRotation = ((_scene.Rotation % 360) + 360) % 360;
                if (viewRotation != 0 || _scene.FlipH || _scene.FlipV)
                {
                    var inverse = new Matrix();
                    inverse.Scale(_scene.FlipH ? -1 : 1, _scene.FlipV ? -1 : 1);
                    inverse.Rotate(-viewRotation);
                    host.RenderTransformOrigin = new Point(0.5, 0.5);
                    host.RenderTransform = new MatrixTransform(inverse);
                }

                Canvas.SetLeft(host, token.X - captionWidth / 2);
                Canvas.SetTop(host, token.Y - size / 2);

                host.MouseLeftButtonDown += (s, e) =>
                {
                    Panel.SetZIndex(host, 3000);
                    SelectToken(token);
                };

                if (CanMoveToken(token))
                {
                    host.Cursor = Cursors.Hand;
                    host.MouseLeftButtonDown += (s, e) => BeginTokenDrag(host, token, e);
                    host.MouseMove += (s, e) => MoveTokenDrag(e);
                    host.MouseLeftButtonUp += (s, e) => EndTokenDrag();
                }

                // Меню жетона: ведущему — всё, игроку — свой жетон (обзор и размер)
                if (_isDm || _remote)
                    host.MouseRightButtonUp += (s, e) => { e.Handled = true; ShowTokenMenu(host, token); };

                return host;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] BuildTokenVisual: {ex.Message}");
                return null;
            }
        }

        private void BeginTokenDrag(Border host, MapTokenData token, MouseButtonEventArgs e)
        {
            if (!CanMoveToken(token)) return;

            // Новый маршрут начинается с чистого следа
            ClearTrail(token.Id);
            _pendingLiveMoves.Remove(token.Id);
            _visualMoves.Remove(token.Id);
            _lastLiveMoveSent = DateTime.MinValue;
            _lastSeenMemory = DateTime.MinValue;

            double startX = token.X, startY = token.Y;
            PushUndo(() =>
            {
                token.X = startX;
                token.Y = startY;
                MapService.SaveToken(token);
                PublishMap();
                RebuildTokens();
            });

            _dragToken = token;
            _dragHost = host;
            Point point = e.GetPosition(_tokenLayer);
            _dragOffset = new Point(point.X - token.X, point.Y - token.Y);
            host.CaptureMouse();
            e.Handled = true;
        }

        private void MoveTokenDrag(MouseEventArgs e)
        {
            if (_dragToken == null || _dragHost == null) return;

            Point point = e.GetPosition(_tokenLayer);
            double x = point.X - _dragOffset.X;
            double y = point.Y - _dragOffset.Y;

            if (_scene.SnapToGrid)
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                x = Math.Round((x - cell / 2) / cell) * cell + cell / 2;
                y = Math.Round((y - cell / 2) / cell) * cell + cell / 2;
            }

            _dragToken.X = Math.Max(0, Math.Min(_scene.WidthPx, x));
            _dragToken.Y = Math.Max(0, Math.Min(_scene.HeightPx, y));

            double width = _dragHost.Width > 0 ? _dragHost.Width : 110;
            Canvas.SetLeft(_dragHost, _dragToken.X - width / 2);
            Canvas.SetTop(_dragHost, _dragToken.Y - (_dragHost.Height - 34) / 2);

            // конус обзора должен ехать вместе с жетоном, а не отставать до Q/E.
            // Но не чаще 20 раз в секунду: MouseMove приходит намного чаще.
            if ((DateTime.UtcNow - _lastDragFov).TotalMilliseconds >= 50)
            {
                _lastDragFov = DateTime.UtcNow;
                RenderFov();
            }

            // След показываем сразу у себя, а остальным — промежуточными
            // сообщениями (не чаще ~10 раз в секунду), чтобы движение было живым
            AppendTrail(_dragToken, new Point(_dragToken.X, _dragToken.Y));

            if ((DateTime.UtcNow - _lastLiveMoveSent).TotalMilliseconds >= 200)
            {
                _lastLiveMoveSent = DateTime.UtcNow;

                if (_remote)
                {
                    RoomSession.Instance.SendMapMoveLive(
                        _dragToken.Id, _dragToken.X, _dragToken.Y, _dragToken.Facing);
                }
                else if (_inRoom)
                {
                    RoomSession.Instance.PublishLiveTokenMove(
                        _dragToken.Id, _dragToken.X, _dragToken.Y, _dragToken.Facing);
                }
            }

            // Раньше здесь каждые 300 мс вызывались RememberCurrentVision и
            // BuildFogMask. При 129 разведанных секторах это давало фризы
            // по 150–300 мс прямо во время перетаскивания. Разведанное
            // запоминаем один раз в EndTokenDrag, а не на каждом шаге мыши.
            // Живую разведку показываем по текущему обзору не чаще 2 раз/сек:
            // в базу и сеть при этом ничего не пишется.
            if ((DateTime.UtcNow - _lastLiveFog).TotalMilliseconds >= 500)
            {
                _lastLiveFog = DateTime.UtcNow;
                BuildFogMask();
            }
        }

        private void PublishLocalTrail(int tokenId)
        {
            if (!_inRoom) return;
            if (!_trails.TryGetValue(tokenId, out var points) || points.Count < 2) return;

            var payload = points
                .Select(p => new MapTrailPoint { X = p.X, Y = p.Y })
                .ToList();
            RoomSession.Instance.PublishMapTrail(tokenId, payload);
        }

        private void EndTokenDrag()
        {
            if (_dragToken == null) return;

            _dragHost?.ReleaseMouseCapture();

            AppendTrail(_dragToken, new Point(_dragToken.X, _dragToken.Y));

            // Передаём весь путь, а не только конечную точку: иначе у остальных
            // остаётся прямая между началом и концом.
            PublishLocalTrail(_dragToken.Id);

            if (_remote)
            {
                RoomSession.Instance.SendMapMove(_dragToken.Id, _dragToken.X, _dragToken.Y);

                // Свой путь запоминаем сразу: ответ хоста может прийти позже,
                // а разведанное не должно зависеть от сети
                RememberCurrentVision();
                BuildFogMask();
            }
            else
            {
                MapService.SaveToken(_dragToken);
                PublishMap();
                RememberCurrentVision();
                BuildFogMask();
            }

            RenderTrails();
            RefreshTokenVisibility();
            _dragToken = null;
            _dragHost = null;
        }

        // ══════════════════════ МЕНЮ ЖЕТОНА ══════════════════════

        private void ShowTokenMenu(Border host, MapTokenData token)
        {
            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");

            // ── Игрок в комнате: только свой жетон и только то, что ему можно ──
            if (_remote)
            {
                if (!CanMoveToken(token))
                {
                    AddDisabledItem(menu, "Это чужой жетон");
                }
                else
                {
                    AddDisabledItem(menu, "Свой жетон");

                    AddCheckItem(menu, "Обзор: круговой 360°", token.ViewAngle >= 359.9,
                        () => SetRemoteViewAngle(token, 360));
                    AddCheckItem(menu, "Конус 120°", Math.Abs(token.ViewAngle - 120) < 1,
                        () => SetRemoteViewAngle(token, 120));
                    AddCheckItem(menu, "Конус 90°", Math.Abs(token.ViewAngle - 90) < 1,
                        () => SetRemoteViewAngle(token, 90));
                    AddCheckItem(menu, "Конус 60°", Math.Abs(token.ViewAngle - 60) < 1,
                        () => SetRemoteViewAngle(token, 60));
                    AddMenuItem(menu, "Повернуть взгляд ⟳", () => RotateFacingRemote(token, 45));
                    AddMenuItem(menu, "Повернуть взгляд ⟲", () => RotateFacingRemote(token, -45));
                    menu.Items.Add(BuildSeparator());

                    AddCheckItem(menu, "Размер: 1 клетка", Math.Abs(token.SizeCells - 1) < 0.1,
                        () => SetRemoteTokenSize(token, 1));
                    AddCheckItem(menu, "Размер: 2 клетки", Math.Abs(token.SizeCells - 2) < 0.1,
                        () => SetRemoteTokenSize(token, 2));
                    AddCheckItem(menu, "Размер: 3 клетки", Math.Abs(token.SizeCells - 3) < 0.1,
                        () => SetRemoteTokenSize(token, 3));
                    AddCheckItem(menu, "Размер: 4 клетки", Math.Abs(token.SizeCells - 4) < 0.1,
                        () => SetRemoteTokenSize(token, 4));
                }

                menu.PlacementTarget = host;
                menu.IsOpen = true;
                return;
            }

            // ── Ведущий: полное меню ──
            _combatants.TryGetValue(token.CombatantId, out var linked);
            var combatant = linked.Combatant;

            if (combatant != null)
            {
                AddMenuItem(menu, "−1 хит", () => ShiftHp(combatant, -1));
                AddMenuItem(menu, "−5 хитов", () => ShiftHp(combatant, -5));
                AddMenuItem(menu, "−10 хитов", () => ShiftHp(combatant, -10));
                AddMenuItem(menu, "+5 хитов", () => ShiftHp(combatant, 5));
                menu.Items.Add(BuildSeparator());

                var conditions = new MenuItem { Header = "Состояния…", IsCheckable = false };
                ApplyMenuItemStyle(conditions);
                var condMenu = BuildConditionMenu(combatant);
                conditions.Click += (s, e) => { condMenu.PlacementTarget = host; condMenu.IsOpen = true; };
                menu.Items.Add(conditions);

                if (combatant.MonsterId > 0)
                    AddMenuItem(menu, "Открыть карточку", () => OpenMonsterRequested?.Invoke(combatant.MonsterId));

                menu.Items.Add(BuildSeparator());
            }
            else
            {
                AddDisabledItem(menu, "Жетон не связан с боем");
                menu.Items.Add(BuildSeparator());
            }

            AddCheckItem(menu, "Радиус обзора: 4 клетки", Math.Abs(token.RevealCells - 4) < 0.5, () => SetRevealCells(token, 4));
            AddCheckItem(menu, "Радиус обзора: 6 клеток", Math.Abs(token.RevealCells - 6) < 0.5, () => SetRevealCells(token, 6));
            AddCheckItem(menu, "Радиус обзора: 8 клеток", Math.Abs(token.RevealCells - 8) < 0.5, () => SetRevealCells(token, 8));
            AddCheckItem(menu, "Радиус обзора: 12 клеток", Math.Abs(token.RevealCells - 12) < 0.5, () => SetRevealCells(token, 12));
            menu.Items.Add(BuildSeparator());

            AddCheckItem(menu, "Обзор: круговой 360°", token.ViewAngle >= 359.9, () => SetViewAngle(token, 360));
            AddCheckItem(menu, "Конус 120°", Math.Abs(token.ViewAngle - 120) < 1, () => SetViewAngle(token, 120));
            AddCheckItem(menu, "Конус 90°", Math.Abs(token.ViewAngle - 90) < 1, () => SetViewAngle(token, 90));
            AddCheckItem(menu, "Конус 60°", Math.Abs(token.ViewAngle - 60) < 1, () => SetViewAngle(token, 60));
            AddMenuItem(menu, "Повернуть взгляд ⟳", () => RotateFacing(token, 45));
            AddMenuItem(menu, "Повернуть взгляд ⟲", () => RotateFacing(token, -45));
            menu.Items.Add(BuildSeparator());

            AddMenuItem(menu, "Размер: 1 клетка", () => SetTokenSize(token, 1));
            AddMenuItem(menu, "Размер: 2 клетки", () => SetTokenSize(token, 2));
            AddMenuItem(menu, "Размер: 3 клетки", () => SetTokenSize(token, 3));
            AddMenuItem(menu, "Размер: 4 клетки", () => SetTokenSize(token, 4));
            menu.Items.Add(BuildSeparator());

            if (_scene != null && _scene.FogOn)
            {
                bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);
                AddCheckItem(menu, "Туман: общий обзор отряда", combined, () => SetFogMode("combined"));
                AddCheckItem(menu, "Туман: у каждого свой", !combined, () => SetFogMode("own"));
                AddMenuItem(menu, "Развеять туман войны", () => SetFog(false));
            }

            AddMenuItem(menu, token.Hidden ? "Показать игрокам" : "Скрыть от игроков", () =>
            {
                token.Hidden = !token.Hidden;
                MapService.SaveToken(token);
                PublishMap();
                RebuildTokens();
            });

            AddMenuItem(menu, "Убрать с карты", () =>
            {
                MapService.DeleteToken(token.Id);
                _scene.Tokens.RemoveAll(t => t.Id == token.Id);
                PublishMap();
                RebuildTokens();
            });

            menu.PlacementTarget = host;
            menu.IsOpen = true;
        }

        /// <summary>Пункт меню, который только объясняет, а не действует.</summary>
        private void AddDisabledItem(ContextMenu menu, string header)
        {
            var item = new MenuItem { Header = header, IsEnabled = false };
            ApplyMenuItemStyle(item);
            menu.Items.Add(item);
        }

        /// <summary>Игрок меняет свой угол обзора: сразу у себя и отправкой хосту.</summary>
        private void SetRemoteViewAngle(MapTokenData token, double angle)
        {
            token.ViewAngle = angle;
            RenderFov();
            RememberCurrentVision();
            BuildFogMask();
            RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, double.NaN, angle);
        }

        /// <summary>Игрок меняет размер своего жетона.</summary>
        private void SetRemoteTokenSize(MapTokenData token, int cells)
        {
            token.SizeCells = cells;
            RebuildTokens();
            RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, double.NaN, -1, cells);
        }

        /// <summary>Игрок поворачивает свой взгляд из меню.</summary>
        private void RotateFacingRemote(MapTokenData token, double delta)
        {
            token.Facing = ((token.Facing + delta) % 360 + 360) % 360;
            RenderFov();
            RememberCurrentVision();
            BuildFogMask();
            RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, token.Facing);
        }

        private void SetTokenSize(MapTokenData token, double cells)
        {
            double before = token.SizeCells;
            PushUndo(() =>
            {
                token.SizeCells = before;
                MapService.SaveToken(token);
                PublishMap();
                RebuildTokens();
            });

            token.SizeCells = cells;
            MapService.SaveToken(token);
            PublishMap();
            RebuildTokens();
        }

        private void AddCheckItem(ContextMenu menu, string header, bool isChecked, Action click)
        {
            var item = new MenuItem { Header = header, IsCheckable = false, IsChecked = isChecked };
            ApplyMenuItemStyle(item);
            item.Click += (s, e) => click();
            menu.Items.Add(item);
        }
        private void AddMenuItem(ContextMenu menu, string header, Action click)
        {
            var item = new MenuItem { Header = header, IsCheckable = false };
            ApplyMenuItemStyle(item);
            item.Click += (s, e) => click();
            menu.Items.Add(item);
        }

        private Separator BuildSeparator()
        {
            var separator = new Separator();
            ApplyDarkStyle(separator, "DarkMenuSeparator");
            return separator;
        }

        private ContextMenu BuildConditionMenu(CombatantData combatant)
        {
            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");

            var active = ParseConditions(combatant.Conditions);

            foreach (var option in CharacterStateService.LoadConditions())
            {
                string key = option.Key;
                var item = new MenuItem
                {
                    Header = option.Name,
                    IsCheckable = false,
                    IsChecked = active.Contains(key),
                    StaysOpenOnClick = true
                };
                ApplyMenuItemStyle(item);
                item.Click += (s, e) =>
                {
                    ToggleCondition(combatant, key);
                    item.IsChecked = !item.IsChecked;
                };
                menu.Items.Add(item);
            }

            return menu;
        }

        private static List<string> ParseConditions(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;

            foreach (string part in text.Split(','))
            {
                string key = (part ?? "").Trim();
                if (key.Length > 0 && !list.Contains(key)) list.Add(key);
            }
            return list;
        }

        private void ToggleCondition(CombatantData combatant, string key)
        {
            if (combatant == null) return;

            var keys = ParseConditions(combatant.Conditions);
            if (keys.Remove(key)) { }
            else keys.Add(key);

            combatant.Conditions = string.Join(",", keys);

            if (_combatants.TryGetValue(combatant.Id, out var linked))
                InitiativeService.SaveCombatant(linked.Encounter, combatant);

            RebuildTokens();
        }

        private void ShiftHp(CombatantData combatant, int delta)
        {
            if (combatant == null) return;

            if (combatant.MaxHitPoints <= 0)
            {
                DmRolls.Toast(this, "У этого участника не заданы хиты", true);
                return;
            }

            combatant.HitPoints = Math.Max(0, Math.Min(combatant.MaxHitPoints, combatant.HitPoints + delta));

            if (_combatants.TryGetValue(combatant.Id, out var linked))
                InitiativeService.SaveCombatant(linked.Encounter, combatant);

            RebuildTokens();
        }

        private void ApplyDarkStyle(FrameworkElement element, string key)
        {
            try
            {
                if (Application.Current != null && Application.Current.TryFindResource(key) is Style style)
                    element.Style = style;
            }
            catch { }
        }

        private void ApplyMenuItemStyle(MenuItem item) => ApplyDarkStyle(item, "DarkMenuItem");

        // ══════════════════════ ЗУМ И ПАНОРАМА ══════════════════════

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_mapImage.Source == null) return;
            ZoomAt(e.GetPosition(_viewport), e.Delta > 0 ? 1.12 : 1 / 1.12);
            e.Handled = true;
        }

        private void ZoomAt(Point at, double factor)
        {
            _userZoomed = true;
            double next = Math.Max(0.05, Math.Min(5.0, _scale * factor));
            if (Math.Abs(next - _scale) < 0.0001) return;

            _tx = at.X - (at.X - _tx) * (next / _scale);
            _ty = at.Y - (at.Y - _ty) * (next / _scale);
            _scale = next;
            ApplyTransform();
        }

        private void ZoomAtCenter(double factor) =>
            ZoomAt(new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2), factor);

        private void FitToWindow()
        {
            if (_scene == null || _scene.WidthPx <= 0 || _scene.HeightPx <= 0) return;
            if (_viewport.ActualWidth < 10 || _viewport.ActualHeight < 10) return;

            // размер у мира: при повороте на 90° ширина и высота меняются местами
            double fitW = _world.Width > 1 ? _world.Width : _scene.WidthPx;
            double fitH = _world.Height > 1 ? _world.Height : _scene.HeightPx;
            if (fitW <= 0 || fitH <= 0) return;

            double sx = (_viewport.ActualWidth - 20) / fitW;
            double sy = (_viewport.ActualHeight - 20) / fitH;
            _scale = Math.Max(0.05, Math.Min(sx, sy));

            _tx = (_viewport.ActualWidth - fitW * _scale) / 2;
            _ty = (_viewport.ActualHeight - fitH * _scale) / 2;
            ApplyTransform();
        }

        private void ApplyTransform()
        {
            // Линейка нарисована в координатах окна; после сдвига/зума она
            // перестала бы соответствовать карте, поэтому убираем её.
            if (_measureStart == null && _measureLayer.Children.Count > 0) ClearMeasure();

            _zoom.ScaleX = _scale;
            _zoom.ScaleY = _scale;
            _shift.X = _tx;
            _shift.Y = _ty;
        }

        private void ClearMeasure()
        {
            _measureStart = null;
            _measureLine = null;
            _measureLabel = null;
            _measureLayer.Children.Clear();
        }

        /// <summary>Показывает пульс в точке карты: локально или пришедшую по сети.</summary>
        private void ShowPing(Point scenePoint)
        {
            if (_scene == null || _content == null || _viewport == null) return;

            try
            {
                Point screen = _content.TranslatePoint(scenePoint, _viewport);
                if (double.IsNaN(screen.X) || double.IsNaN(screen.Y)) return;

                var ring = new System.Windows.Shapes.Ellipse
                {
                    Width = 36,
                    Height = 36,
                    Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x5F, 0xD4, 0xDD)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x5F, 0xD4, 0xDD)),
                    StrokeThickness = 2.4,
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(ring, screen.X - 18);
                Canvas.SetTop(ring, screen.Y - 18);
                _pingLayer.Children.Add(ring);

                // Пинг должен быть виден и на WebGL-карте: WPF-слой лежит под
                // WebView, поэтому дублируем его туда же в координатах картинки.
                try
                {
                    _webMap.SendEphemeral(System.Text.Json.JsonSerializer.Serialize(
                        new { type = "ping", x = scenePoint.X, y = scenePoint.Y }, WebJsonOptions));
                }
                catch { }

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1100)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _pingLayer.Children.Remove(ring);
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] пинг: {ex.Message}");
            }
        }

        private void OnMapPing(double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnMapPing(x, y)));
                return;
            }

            ShowPing(new Point(x, y));
        }

        private void BeginMeasure(Point point)
        {
            ClearMeasure();

            if (_scene == null) return;
            _measureStart = point;

            _measureLine = new System.Windows.Shapes.Line
            {
                X1 = point.X,
                Y1 = point.Y,
                X2 = point.X,
                Y2 = point.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(0xEE, 0x5F, 0xD4, 0xDD)),
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 6, 4 },
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            _measureLayer.Children.Add(_measureLine);

            _measureLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0D, 0x16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x5F, 0xD4, 0xDD)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 3, 7, 3),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "0 кл · 0 фт",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xEA, 0xF5))
                }
            };
            _measureLayer.Children.Add(_measureLabel);
        }

        private void UpdateMeasure(Point point)
        {
            if (_measureStart == null || _measureLine == null || _measureLabel == null || _scene == null) return;

            Point start = _measureStart.Value;
            _measureLine.X2 = point.X;
            _measureLine.Y2 = point.Y;

            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            double pixels = Math.Sqrt((point.X - start.X) * (point.X - start.X) +
                                      (point.Y - start.Y) * (point.Y - start.Y));
            double cells = pixels / Math.Max(1, cell * _scale);
            double feet = cells * 5;

            if (_measureLabel.Child is TextBlock text)
                text.Text = $"{cells:0.#} кл · {feet:0} фт";

            Canvas.SetLeft(_measureLabel, (start.X + point.X) / 2 + 10);
            Canvas.SetTop(_measureLabel, (start.Y + point.Y) / 2 - 14);
        }

        private void EndMeasure()
        {
            _measureStart = null;
            _viewport.Cursor = _measureCheck.IsChecked == true ? Cursors.Cross : Cursors.Arrow;
        }

        private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Пинг: одиночный щелчок, режим после него снимается
            if (e.ChangedButton == MouseButton.Left && _pingCheck.IsChecked == true && _scene != null)
            {
                _userZoomed = true;
                Point contentPoint = e.GetPosition(_content);
                ShowPing(contentPoint);
                RoomSession.Instance.SendMapPing(contentPoint.X, contentPoint.Y);
                _pingCheck.IsChecked = false;
                _viewport.Cursor = Cursors.Arrow;
                e.Handled = true;
                return;
            }

            // Линейка важнее жетона: можно измерить и от самого жетона
            if (e.ChangedButton == MouseButton.Left && _measureCheck.IsChecked == true && _scene != null)
            {
                _userZoomed = true;
                BeginMeasure(e.GetPosition(_viewport));
                _viewport.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (e.OriginalSource is Border b && b.Tag is MapTokenData) return;

            // выбран тип фигуры — щелчок ставит её на карту
            if (e.ChangedButton == MouseButton.Left && _pendingShape != null && (_isDm || _remote) && _scene != null)
            {
                PlaceShape(e.GetPosition(_shapeLayer));
                _viewport.Cursor = Cursors.Arrow;
                e.Handled = true;
                return;
            }

            // стены: тянем мышью отрезок
            if (e.ChangedButton == MouseButton.Left && _wallCheck.IsChecked == true && _isDm && _scene != null)
            {
                _userZoomed = true;
                BeginWall(e.GetPosition(_wallLayer));
                _viewport.CaptureMouse();
                e.Handled = true;
                return;
            }

            // кисть: левая кнопка рисует, панорама уходит на среднюю.
            // Рисовать может и игрок: стены остаются только у ведущего.
            if (e.ChangedButton == MouseButton.Left && _drawCheck.IsChecked == true &&
                (_isDm || _remote) && _scene != null)
            {
                _userZoomed = true;
                BeginStroke(e.GetPosition(_drawLayer));
                _viewport.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Middle && _drawCheck.IsChecked == true)
            {
                _userZoomed = true;
                _panning = true;
                _panStart = e.GetPosition(_viewport);
                _panTx = _tx;
                _panTy = _ty;
                _viewport.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }

            _userZoomed = true;
            _panning = true;
            _panStart = e.GetPosition(_viewport);
            _panTx = _tx;
            _panTy = _ty;
            _viewport.Cursor = Cursors.SizeAll;
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (_measureStart != null)
            {
                UpdateMeasure(e.GetPosition(_viewport));
                return;
            }

            if (_wallStart != null)
            {
                ExtendWall(e.GetPosition(_wallLayer));
                return;
            }

            if (_strokePoints != null)
            {
                ExtendStroke(e.GetPosition(_drawLayer));
                return;
            }

            if (!_panning) return;

            Point point = e.GetPosition(_viewport);
            _tx = _panTx + (point.X - _panStart.X);
            _ty = _panTy + (point.Y - _panStart.Y);
            ApplyTransform();
        }

        private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_measureStart != null)
            {
                _viewport.ReleaseMouseCapture();
                EndMeasure();
                return;
            }

            if (_wallStart != null)
            {
                _viewport.ReleaseMouseCapture();
                EndWall();
                return;
            }

            if (_strokePoints != null)
            {
                _viewport.ReleaseMouseCapture();
                EndStroke();
                _viewport.Cursor = _drawCheck.IsChecked == true ? Cursors.Pen : Cursors.Arrow;
                return;
            }

            _panning = false;
            _viewport.Cursor = _drawCheck.IsChecked == true ? Cursors.Pen : Cursors.Arrow;
        }
    }
}
