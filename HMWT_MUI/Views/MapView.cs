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
    public sealed partial class MapView : UserControl
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

        /// <summary>
        /// Персонаж, которым играю (id). В комнате — из отряда, иначе — лист
        /// этого окна. По нему карта понимает, какой жетон «свой».
        /// </summary>
        private int SelfCharacterId
        {
            get
            {
                try
                {
                    var session = RoomSession.Instance;
                    if (session != null && session.IsActive)
                    {
                        var me = session.GetPlayers().FirstOrDefault(p => p != null && p.Id == session.MyPlayerId);
                        if (me != null && me.CharacterId > 0) return me.CharacterId;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MapView] свой персонаж: {ex.Message}");
                }
                return _characterId;
            }
        }


        private readonly Border _viewport = new Border();
        private readonly Canvas _world = new Canvas();
        private readonly MapWebRenderer _webMap = new MapWebRenderer();
        private System.Windows.Threading.DispatcherTimer _webSyncTimer;
        private readonly Dictionary<string, string> _webDataUrls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int _webSeenVersion = -1;

        /// <summary>Сколько разведанных секторов уже отправлено в WebView.</summary>
        private int _webSeenSent;
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

        /// <summary>
        /// Жетоны подтягивают хиты/состояния из трекера: SaveCombatant зовут
        /// пачками (броски, отметки), поэтому обновляем карту с задержкой.
        /// </summary>
        private readonly System.Windows.Threading.DispatcherTimer _combatantRefreshTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };

        /// <summary>Когда последний раз отправляли промежуточное движение при перетаскивании.</summary>
        private DateTime _lastLiveMoveSent = DateTime.MinValue;

        /// <summary>Когда последний раз отправляли положение фигуры при перетаскивании.</summary>
        private DateTime _lastShapeLiveSent = DateTime.MinValue;

        /// <summary>Редкая отметка в логе: по ней видно, уходят ли живые шаги жетона.</summary>
        private DateTime _lastLiveMoveLog = DateTime.MinValue;

        /// <summary>Когда последний раз запоминали обзор по пути (не чаще 3 раз в секунду).</summary>
        private DateTime _lastSeenMemory = DateTime.MinValue;

        /// <summary>Когда последний раз пересчитывали туман по чужому live-ходу.</summary>
        private DateTime _lastLiveFog = DateTime.MinValue;

        /// <summary>Разведанное, ещё не записанное в базу (копится во время движения).</summary>
        private readonly List<string> _pendingSeenWrites = new List<string>();

        /// <summary>Диагностика поворота: сколько пакетов ушло и была ли пауза.</summary>
        private int _rotatePushes;
        private double _rotateMaxGap;

        /// <summary>
        /// Плавный поворот взгляда. Клавиши приходят в WPF (у WebView нет
        /// фокуса ввода), поэтому событие ловим здесь, но анимацию ведёт
        /// WebGL-рендерер: ему уходит только «начали/закончили» и скорость.
        /// Таймер ниже — резервный путь для WPF-карты, когда WebView не поднялся.
        /// С приоритетом Normal: Background-таймер голодает, пока WebView
        /// непрерывно шлёт сообщения, и поворот шёл рывками раз в секунду.
        /// </summary>
        private readonly System.Windows.Threading.DispatcherTimer _rotateTimer =
            new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal)
            { Interval = TimeSpan.FromMilliseconds(16) };

        private System.Windows.Threading.DispatcherTimer _rotateWatch;
        private System.Windows.Threading.DispatcherTimer _overlayWatch;
        private bool _coveredByWindow;
        private bool _combatantHooked;
        private int _liveMoveLog;
        private bool _rotating;
        private MapTokenData _rotateToken;
        private int _rotateDir;
        private DateTime _rotateLast = DateTime.UtcNow;
        private DateTime _lastRotatePush = DateTime.MinValue;
        private const double RotateSpeed = 75;   // градусов в секунду
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
            _combatantRefreshTimer.Tick += (s, e) =>
            {
                _combatantRefreshTimer.Stop();
                RefreshCombatantStateOnMap();
            };

            // Трекер, карта и лист пишут одного участника боя: любое изменение
            // (хиты, состояния) сразу отражаем на жетонах, не дожидаясь, пока
            // придёт следующий снимок сцены. Подписка — в Loaded/Unloaded:
            // статическое событие иначе держало бы закрытые окна.

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
            _webMap.PointPlaced += OnWebPointPlaced;
            _webMap.TokensMoved += OnWebTokensMoved;
            _webMap.ShapeMoved += OnWebShapeMoved;
            _webMap.StrokeAdded += OnWebStroke;
            _webMap.WallAdded += OnWebWall;
            _webMap.WallAction += OnWebWallAction;
            _webMap.TokenMenuAction += OnWebTokenMenuAction;
            _webMap.TokenSelected += OnWebTokenSelected;
            _webMap.TrailCleared += OnWebTrailCleared;
            _webMap.ShapeAction += OnWebShapeAction;
            _webMap.FacingChanged += OnWebRotate;
            _webMap.ImageNeeded += OnWebImageNeeded;
            _webMap.Pinged += OnWebPinged;

            _webSyncTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _webSyncTimer.Tick += (s, e) =>
            {
                // Пока крутим обзор, полную сцену не шлём: она тяжёлая и
                // сбивает плавность. Хватает лёгких пакетов с углом.
                if (!_rotating) PushWebMap();
            };

            _rotateTimer.Tick += RotateTick;
            KeyUp += MapView_KeyUp;

            // TAB — навигационная клавиша: до KeyDown она не доходит,
            // её перехватывает WPF. Поэтому ловим на туннелировании.
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Tab) return;
                PingAtCursor();
                e.Handled = true;
            };

            Loaded += (s, e) =>
            {
                SubscribeRoom();
                if (!_combatantHooked)
                {
                    _combatantHooked = true;
                    Services.InitiativeService.Changed += OnCombatantChanged;
                }
                Refresh();
                _lastRoomActive = _inRoom;
                _retryTimer.Start();
                _webSyncTimer.Start();

                // WebView2 — отдельное окно (HWND) и всегда выше WPF-слоя,
                // поэтому «перекрыть» его изнутри окна нельзя. Пока поверх
                // карты открыто другое окно приложения (лист, заметки,
                // справочник) — карту убираем, при закрытии возвращаем.
                if (_overlayWatch == null)
                {
                    _overlayWatch = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Background)
                    { Interval = TimeSpan.FromMilliseconds(300) };
                    _overlayWatch.Tick += WatchAppWindows;
                }
                _overlayWatch.Start();
            };
            Unloaded += (s, e) =>
            {
                UnsubscribeRoom();
                if (_combatantHooked)
                {
                    _combatantHooked = false;
                    Services.InitiativeService.Changed -= OnCombatantChanged;
                }
                _combatantRefreshTimer.Stop();
                _overlayWatch?.Stop();
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
            room.ShapeMovedLive += OnRemoteShapeMoved;
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
            room.ShapeMovedLive -= OnRemoteShapeMoved;
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

        // Построение панели карты вынесено в MapView.Shell.cs (partial).


        // ══════════════════════ ДАННЫЕ ══════════════════════
        // Сборка снимка сцены для WebGL вынесена в MapView.Web.cs (partial):
        // сетевой протокол карты не должен жить в одном файле с WPF-логикой.


        // Обработчики WebGL (жетоны, фигуры, стены, меню) вынесены в
        // MapView.Events.cs (partial).


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
        // Сцены и загрузка картинки вынесены в MapView.Scenes.cs (partial).


        /// <summary>
        /// Чужой жетон сдвинулся во время перетаскивания. Двигаем только его
        /// визуал и обзор — без пересборки всей карты: иначе десять обновлений
        /// в секунду превращаются в слайд-шоу.
        ///
        /// Применяем сразу, без очереди задержки: сглаживание делает WebGL,
        /// а очередь только добавляла лаг и могла теряться при обновлении
        /// сцены (в итоге жетон «оживал» лишь при отпускании мыши).
        /// </summary>
        private void OnTokenMovedLive(int tokenId, double x, double y, double facing)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnTokenMovedLive(tokenId, x, y, facing)));
                return;
            }

            if (_scene == null) return;

            // Свой жетон уже двигает сама мышь — второй раз не трогаем
            if (_dragToken != null && _dragToken.Id == tokenId) return;

            ApplyRemoteLiveMove(tokenId, x, y, facing);
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

        /// <summary>
        /// Лёгкое сообщение «жетон поехал» для WebGL. Полный снимок карты
        /// приходит редко (раз в 0.4 с), поэтому без этого чужой жетон
        /// «стоял» до отпускания мыши.
        /// </summary>
        private void PushWebTokenLive(int id, double x, double y, double facing)
        {
            if (!_webMapReady) return;
            try
            {
                _webMap.SendEphemeral(System.Text.Json.JsonSerializer.Serialize(
                    new { type = "tokenLive", id, x, y, facing }, WebJsonOptions));
            }
            catch { }
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

            // И в WebGL: без этого чужой жетон двигался только по полному
            // снимку, то есть «оживал» при отпускании мыши.
            PushWebTokenLive(tokenId, x, y, facing);

            // Редкая отметка в логе: по ней видно, доходят ли живые шаги.
            if (++_liveMoveLog % 20 == 0)
                WebLog($"live move #{_liveMoveLog} token={tokenId} -> web");
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

                // Хиты и состояния тоже едут с жетоном: у клиента нет базы
                // боёв, и локально он их не посчитает.
                if (owner.Combatant != null)
                {
                    token.Hp = owner.Combatant.HitPoints;
                    token.HpMax = owner.Combatant.MaxHitPoints;
                    token.Conditions = owner.Combatant.Conditions ?? "";
                }

                // Чей это персонаж: у клиента нет базы боёв, поэтому
                // «свой жетон» он определяет по character_id из карты,
                // а не по имени (его могли сменить или дополнить номером).
                token.OwnerCharacterId = owner.Combatant != null ? owner.Combatant.CharacterId : 0;

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

        /// <summary>Участника боя изменили — обновим карту чуть позже (пачкой).</summary>
        private void OnCombatantChanged()
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(OnCombatantChanged));
                    return;
                }
                _combatantRefreshTimer.Stop();
                _combatantRefreshTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] обновление участника: {ex.Message}");
            }
        }

        /// <summary>
        /// Переносит хиты, состояния и владельца из участников боя в жетоны.
        /// Раньше карта узнавала об изменениях трекера только случайно — со
        /// следующим снимком сцены, поэтому у клиента состояния «не доезжали».
        /// </summary>
        private void RefreshCombatantStateOnMap()
        {
            if (_scene?.Tokens == null) return;

            LoadCombatants();

            bool changed = false;
            foreach (var token in _scene.Tokens)
            {
                if (token.CombatantId <= 0) continue;
                if (!_combatants.TryGetValue(token.CombatantId, out var linked) ||
                    linked.Combatant == null) continue;

                var combatant = linked.Combatant;
                if (token.Hp != combatant.HitPoints) { token.Hp = combatant.HitPoints; changed = true; }
                if (token.HpMax != combatant.MaxHitPoints) { token.HpMax = combatant.MaxHitPoints; changed = true; }
                if (!string.Equals(token.Conditions ?? "", combatant.Conditions ?? "",
                        StringComparison.Ordinal))
                {
                    token.Conditions = combatant.Conditions ?? "";
                    changed = true;
                }
                if (token.OwnerCharacterId != combatant.CharacterId)
                {
                    token.OwnerCharacterId = combatant.CharacterId;
                    changed = true;
                }
                bool isPlayer = combatant.Kind == CombatantKind.Player;
                if (token.IsPlayer != isPlayer) { token.IsPlayer = isPlayer; changed = true; }
            }

            if (!changed) return;

            if (_webMapReady)
            {
                PushWebMap();
                if (_inRoom && !_remote) PublishMap();
            }
            else
            {
                RebuildTokens();
            }
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

                // Клиент: бестиария и чужих путей у него нет, но файл мог прийти
                // из комнаты по хешу — тогда берём скачанную копию. Без этого
                // аватарки НПС у игроков не появлялись никогда.
                if (!string.IsNullOrWhiteSpace(token.PhotoHash))
                {
                    string cached = RemoteImagePath(token.PhotoHash);
                    if (!string.IsNullOrEmpty(cached) && File.Exists(cached)) return cached;
                }

                // Последний шанс: путь, записанный при создании жетона
                if (!string.IsNullOrWhiteSpace(token.PhotoPath))
                {
                    string own = PhotoHelper.ResolvePhotoPath(token.PhotoPath);
                    if (!string.IsNullOrEmpty(own) && File.Exists(own)) return own;
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

        // Окно сцен и управление сценами вынесены в MapView.Scenes.cs.


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

            // Подгонку делает WebGL-рендерер сам: он знает, что игрок уже
            // приблизил карту, и не должен сбрасывать вид при каждом снимке
            // сцены (именно из-за этого у клиента «прыгал» масштаб при любом
            // ходе ведущего). Нативному слою подгонка нужна по-прежнему.
            if (!_webMapReady)
            {
                _userZoomed = false;
                FitToWindow();
            }
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
        // Туман, разведанное и геометрия обзора вынесены в MapView.Fog.cs.


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
            ("line", "Линия"),
            ("point", "Точка интереса"),
            ("darkzone", "Тёмная зона (только ДМ)"),
            ("darkzone_square", "Тёмная зона, квадрат (только ДМ)")
        };

        private void OpenShapeMenu(FrameworkElement target)
        {
            if (!_isDm && !_remote) return;

            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");
            double radius = ParseShapeSize();

            foreach (var kind in ShapeKinds)
            {
                // Тёмные зоны — инструмент мастера, игроку их в списке нет
                if (!_isDm && kind.Kind.StartsWith("darkzone", StringComparison.OrdinalIgnoreCase))
                    continue;

                string kindKey = kind.Kind;
                string title = kind.Title;
                var item = new MenuItem { Header = title, IsCheckable = false };
                ApplyMenuItemStyle(item);
                item.Click += (s, e) =>
                {
                    // размер берём из числового поля, а не из ползунка;
                    // у точки интереса размера нет — это метка «здесь»
                    double size = kindKey == "point" ? 0 : Math.Max(1, ParseShapeSize());
                    _pendingShape = new MapShapeData { Kind = kindKey, RadiusCells = size };
                    DmRolls.Toast(this, kindKey == "point"
                        ? "Точка интереса: щёлкните в нужном месте, затем подпишите её из меню ПКМ"
                        : $"{title} {size:0.#} кл.: щёлкните по карте");
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

            // Точку интереса ставим ровно туда, где курсор: она метит место,
            // а привязка к перекрестьям уводила её «куда попало».
            if (_scene.SnapToGrid &&
                !string.Equals(shape.Kind, "point", StringComparison.OrdinalIgnoreCase))
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                // Фигуры ставим на перекрестья сетки, а не в центр клетки
                shape.X = Math.Round(shape.X / cell) * cell;
                shape.Y = Math.Round(shape.Y / cell) * cell;
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

                if (_scene.SnapToGrid &&
                    !string.Equals(shape.Kind, "point", StringComparison.OrdinalIgnoreCase))
                {
                    double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                    // перекрестья сетки, как и при создании фигуры
                    shape.X = Math.Round(shape.X / cell) * cell;
                    shape.Y = Math.Round(shape.Y / cell) * cell;
                }

                RenderShapes();
                SendShapeLive(shape);
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

            if (e.Key == Key.Tab)
            {
                PingAtCursor();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Q && e.Key != Key.E) return;

            // Автоповтор Windows не нужен: угол ведёт таймер, пока клавиша нажата.
            if (e.IsRepeat)
            {
                e.Handled = true;
                return;
            }

            // На экране E — по часовой стрелке, Q — против. У отражённой карты
            // направление зеркалится, поэтому переводим его в угол картинки.
            int screenDir = e.Key == Key.E ? 1 : -1;
            bool mirrored = _scene != null && _scene.FlipH != _scene.FlipV;
            StartRotateToken(mirrored ? -screenDir : screenDir);
            e.Handled = true;
        }

        private void MapView_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Q || e.Key == Key.E) StopRotateToken();
        }

        /// <summary>Начали держать Q/E: запускаем непрерывный поворот.</summary>
        private void StartRotateToken(int direction)
        {
            if (_scene == null) return;

            // во время перетаскивания крутим тот жетон, который держим
            var token = _dragToken ?? _scene.Tokens.FirstOrDefault(t => t.Id == _selectedTokenId);
            if (token == null || !CanMoveToken(token)) return;

            if (_rotating && _rotateToken != null &&
                _rotateToken.Id == token.Id && _rotateDir == direction)
                return;

            _rotateToken = token;
            _rotateDir = direction;
            _rotateLast = DateTime.UtcNow;
            _lastRotatePush = DateTime.MinValue;
            _lastLiveFog = DateTime.MinValue;
            _rotatePushes = 0;
            _rotateMaxGap = 0;
            _rotating = true;

            if (_webMapReady)
            {
                // Анимацию ведёт WebGL-рендерер в своём процессе: 60 кадров
                // в секунду, без зависимости от загрузки UI-потока WPF.
                // C# получает только промежуточные углы (для сети и разведки)
                // и окончательный — при отпускании.
                SendWebRotate("rotateStart", token.Id, direction, token.Facing);
                StartRotateWatchdog();
                return;
            }

            if (!_rotateTimer.IsEnabled) _rotateTimer.Start();
        }

        /// <summary>
        /// Пинг по карте (TAB): круги расходятся от курсора и видны всем в
        /// комнате. Точку знает WebView — он присылает её в ответ на команду.
        /// </summary>
        /// <summary>
        /// Прячет карту, пока поверх неё открыто другое окно приложения.
        /// WebView2 — отдельное окно (HWND), и WPF-оверлеи оказываются под ним;
        /// проще убрать карту на время и вернуть её при закрытии окна.
        /// </summary>
        private void WatchAppWindows(object sender, EventArgs e)
        {
            var app = Application.Current;
            var own = Window.GetWindow(this);
            if (app == null || own == null) return;

            bool covered = false;
            foreach (Window window in app.Windows)
            {
                if (window == null || ReferenceEquals(window, own)) continue;
                if (!window.IsVisible) continue;
                if (window.WindowState == WindowState.Minimized) continue;

                // Всплывающие уведомления (тосты) — безрамочные окна без кнопки
                // на панели задач. Их не считаем «перекрытием»: иначе на время
                // каждого уведомления карта чернела.
                if (window.WindowStyle == WindowStyle.None && !window.ShowInTaskbar) continue;

                covered = true;
                break;
            }

            if (covered == _coveredByWindow) return;
            _coveredByWindow = covered;
            Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        }

        private void PingAtCursor()
        {
            if (!_webMapReady) return;
            try { _webMap.SendEphemeral("{\"type\":\"pingAtCursor\"}"); }
            catch { }
            WebLog("ping at cursor");
        }

        private void SendWebRotate(string type, int tokenId, int direction, double facing = 0)        {
            try
            {
                // NaN в JSON не сериализуется — подстраховываемся.
                if (double.IsNaN(facing) || double.IsInfinity(facing)) facing = 0;

                _webMap.SendEphemeral(System.Text.Json.JsonSerializer.Serialize(new
                {
                    type,
                    id = tokenId,
                    dir = direction,
                    speed = (int)RotateSpeed,
                    facing
                }, WebJsonOptions));
            }
            catch (Exception ex) { WebLog("rotate cmd: " + ex.Message); }
        }

        /// <summary>
        /// Сторож: если фокус ушёл из окна во время поворота, KeyUp не придёт.
        /// Проверяем раз в 250 мс, что клавиша всё ещё нажата.
        /// </summary>
        private void StartRotateWatchdog()
        {
            if (_rotateWatch == null)
            {
                _rotateWatch = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal)
                { Interval = TimeSpan.FromMilliseconds(250) };
                _rotateWatch.Tick += (s, e) =>
                {
                    if (!_rotating) { _rotateWatch.Stop(); return; }
                    if (Keyboard.IsKeyDown(Key.Q) || Keyboard.IsKeyDown(Key.E)) return;
                    StopRotateToken();
                };
            }

            if (!_rotateWatch.IsEnabled) _rotateWatch.Start();
        }

        /// <summary>Шаг непрерывного поворота: угол, обзор и промежуточные сообщения.</summary>
        private void RotateTick(object sender, EventArgs e)
        {
            // Клавишу могли отпустить вне окна — тогда KeyUp не придёт.
            if (!Keyboard.IsKeyDown(Key.Q) && !Keyboard.IsKeyDown(Key.E))
            {
                StopRotateToken();
                return;
            }

            if (_scene == null || _rotateToken == null || !CanMoveToken(_rotateToken))
            {
                StopRotateToken();
                return;
            }

            DateTime now = DateTime.UtcNow;
            double dt = Math.Min(0.12, (now - _rotateLast).TotalSeconds);
            _rotateLast = now;

            var token = _rotateToken;
            token.Facing = NormalizeDeg(token.Facing + _rotateDir * RotateSpeed * dt);

            if ((now - _lastRotatePush).TotalMilliseconds >= 33)
            {
                if (_lastRotatePush != DateTime.MinValue)
                {
                    double gap = (now - _lastRotatePush).TotalMilliseconds;
                    if (gap > _rotateMaxGap) _rotateMaxGap = gap;
                }
                _lastRotatePush = now;
                _rotatePushes++;

                if (_webMapReady) PushWebFacing();
                else
                {
                    RenderFov();
                    BuildFogMask();
                }
            }

            if ((now - _lastLiveMoveSent).TotalMilliseconds >= 150)
            {
                _lastLiveMoveSent = now;
                if (_remote)
                    RoomSession.Instance.SendMapMoveLive(token.Id, token.X, token.Y, token.Facing);
                else if (_inRoom)
                    RoomSession.Instance.PublishLiveTokenMove(token.Id, token.X, token.Y, token.Facing);
            }

            // Разведанное собираем по ходу поворота: иначе быстрый разворот
            // перепрыгивает сектора и часть обзора не попадает в туман.
            // В базу и сеть пишем не здесь, а одним пакетом в конце — иначе
            // каждый такой шаг подвешивает UI-поток на диске и сети.
            if (WebFogEnabled() && (now - _lastLiveFog).TotalMilliseconds >= 550)
            {
                _lastLiveFog = now;
                RememberCurrentVision(false);
                if (_webMapReady) PushWebSeen();
            }
        }

        /// <summary>Отпустили Q/E: фиксируем угол в базе и рассылаем карту.</summary>
        private void StopRotateToken()
        {
            _rotateTimer.Stop();
            _rotateWatch?.Stop();

            if (_webMapReady)
            {
                // Рендерер сам доведёт угол до места и пришлёт окончательный
                // (commit) — там и будет сохранение. Здесь только команда.
                if (_rotating) SendWebRotate("rotateStop", 0, 0);
                else _rotating = false;
                return;
            }

            _rotating = false;

            var token = _rotateToken;
            _rotateToken = null;
            if (token == null || _scene == null || !CanMoveToken(token))
            {
                _rotatePushes = 0;
                _rotateMaxGap = 0;
                return;
            }

            WebLog($"rotate pushes={_rotatePushes} maxgap={_rotateMaxGap:0}ms");

            if (_remote)
            {
                // У игрока своего файла сцены нет: поворот нужно отправить хосту,
                // иначе он вернётся прежним со следующим снимком карты
                RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, token.Facing);
            }
            else
            {
                MapService.SaveToken(token);
                PublishMap();
            }

            // persist = true: всё, что накопили за поворот, уходит в базу
            // и в комнату одним пакетом
            RememberCurrentVision();

            // Последний угол с пометкой stop: WebView перестаёт «додумывать»
            // движение и точно встаёт на сохранённое значение.
            if (_webMapReady) PushWebFacing(true);

            RenderFov();
            BuildFogMask();
            PushWebMap();

            _rotatePushes = 0;
            _rotateMaxGap = 0;
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
            {
                // У клиента нет базы боёв: владелец жетона приезжает вместе с
                // картой (ownerCharacterId, протокол 16). Имя — только запасной
                // путь для старых сцен, потому что у клиента имя персонажа
                // могло не совпасть с подписью жетона.
                int self = SelfCharacterId;
                if (token.OwnerCharacterId > 0 && self > 0)
                    return token.OwnerCharacterId == self;

                return token.CombatantId > 0 && NameMatch.SamePerson(token.Label, SelfName);
            }

            if (!_combatants.TryGetValue(token.CombatantId, out var linked)) return false;
            if (linked.Combatant == null) return false;
            if (linked.Combatant.Kind != CombatantKind.Player) return false;

            int myId = SelfCharacterId;
            if (linked.Combatant.CharacterId > 0 && myId > 0)
                return linked.Combatant.CharacterId == myId;

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

                    menu.Items.Add(BuildSizeMenu(token, true));
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

            menu.Items.Add(BuildSizeMenu(token, false));
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

        /// <summary>Игрок меняет размер своего жетона (1–10 клеток).</summary>
        private void SetRemoteTokenSize(MapTokenData token, int cells)
        {
            cells = Math.Max(1, Math.Min(10, cells));
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
            cells = Math.Max(1, Math.Min(10, cells));
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

        /// <summary>
        /// Подменю «Размер»: 1–10 клеток одним списком. Раньше десять пунктов
        /// подряд растягивали всё меню жетона на пол-экрана.
        /// </summary>
        private MenuItem BuildSizeMenu(MapTokenData token, bool remote)
        {
            var size = new MenuItem { Header = "Размер" };
            ApplyMenuItemStyle(size);

            for (int n = 1; n <= 10; n++)
            {
                string word = n == 1 ? "клетка" : (n < 5 ? "клетки" : "клеток");
                int chosen = n;
                bool selected = Math.Abs(token.SizeCells - n) < 0.1;

                var item = new MenuItem
                {
                    Header = n + " " + word,
                    IsCheckable = false,
                    IsChecked = selected
                };
                ApplyMenuItemStyle(item);
                item.Click += (s, e) =>
                {
                    if (remote) SetRemoteTokenSize(token, chosen);
                    else SetTokenSize(token, chosen);
                };
                size.Items.Add(item);
            }

            return size;
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

        private void ZoomAtCenter(double factor)
        {
            // Карту рисует WebGL, поэтому масштаб меняем там же: раньше кнопки
            // «+»/«−» двигали невидимый WPF-слой и не давали эффекта.
            if (_webMapReady)
            {
                try
                {
                    _webMap.SendEphemeral(System.Text.Json.JsonSerializer.Serialize(
                        new { type = "viewZoom", factor = factor }, WebJsonOptions));
                }
                catch { }
                return;
            }

            ZoomAt(new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2), factor);
        }

        private void FitToWindow()
        {
            if (_webMapReady)
            {
                try { _webMap.SendEphemeral("{\"type\":\"viewFit\"}"); }
                catch { }
                return;
            }
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
