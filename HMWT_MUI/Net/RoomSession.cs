using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;

namespace HMWT_MUI.Net
{
    /// <summary>Роль участника в текущей сессии приложения.</summary>
    public enum SessionMode
    {
        Offline = 0,
        Host = 1,
        Client = 2
    }

    /// <summary>
    /// Единая точка входа в комнату для всего приложения.
    ///
    /// Сессия живёт на уровне приложения, а не окна персонажа: иначе при закрытии
    /// или смене персонажа комната разваливалась бы. Ссылок на WPF здесь нет —
    /// события приходят из сетевых потоков, а переключение в UI-поток делает вызывающий.
    /// </summary>
    public sealed class RoomSession
    {
        private static RoomSession _instance;
        private static readonly object InstanceLock = new object();

        /// <summary>Общий экземпляр сессии.</summary>
        public static RoomSession Instance
        {
            get
            {
                lock (InstanceLock)
                    return _instance ??= new RoomSession();
            }
        }

        private RoomServer _server;
        private RoomClient _client;
        private RoomManifest _manifest;
        private PlayerInfo _hostPlayer;

        /// <summary>Скрытые броски ведущего: в общую ленту и по сети не попадают.</summary>
        private readonly List<FeedEntry> _hiddenFeed = new List<FeedEntry>();
        private readonly object _hiddenFeedLock = new object();

        private const string HiddenRollsSettingKey = "RoomHiddenDmRolls";
        private bool _hiddenDmRolls;

        /// <summary>
        /// Показывать ли броски ведущего только ему. Настройка общая для
        /// приложения: мастер включает её один раз и не думает о ней в бою.
        /// </summary>
        public bool HiddenDmRolls
        {
            get => _hiddenDmRolls;
            set
            {
                if (_hiddenDmRolls == value) return;
                _hiddenDmRolls = value;

                try { DatabaseHelper.SaveSetting(HiddenRollsSettingKey, value ? "1" : "0"); }
                catch (Exception ex) { Debug.WriteLine($"[RoomSession] скрытые броски: {ex.Message}"); }

                StateChanged?.Invoke();
            }
        }

        private RoomSession()
        {
            try { _hiddenDmRolls = DatabaseHelper.GetSetting(HiddenRollsSettingKey, "0") == "1"; }
            catch { }
        }

        /// <summary>
        /// Лента гостя. Собирается из снимка (история на момент входа) и из событий,
        /// которые приходят по каждому сообщению и броску. Хост свою ленту ведёт сам.
        /// </summary>
        private readonly List<FeedEntry> _clientFeed = new List<FeedEntry>();
        private readonly object _clientFeedLock = new object();
        private bool _clientFeedLoaded;

        private string _lastSelfSignature = "";

        public SessionMode Mode { get; private set; } = SessionMode.Offline;

        /// <summary>Ключ текущей комнаты. Хост показывает его игрокам.</summary>
        public string Key { get; private set; } = "";

        public string RoomId => _manifest?.Id ?? "";
        public string RoomName => _manifest?.Name ?? "";
        public string LastError { get; private set; } = "";
        public string StatusText { get; private set; } = "Не подключено";

        /// <summary>Адрес, который игроки вводят для подключения (только у хоста).</summary>
        public string HostAddress { get; private set; } = "";
        public int Port { get; private set; } = NetVersions.DefaultPort;

        /// <summary>
        /// Внешний (публичный) адрес хоста — для игры через интернет.
        ///
        /// Пусто, пока не спрашивали или если узнать не удалось: тогда игрокам
        /// показывается локальный адрес с пояснением. Заполняется только у хоста.
        /// </summary>
        public string PublicAddress { get; private set; } = "";

        /// <summary>Внешний адрес сейчас запрашивается: кнопка обновления должна быть недоступна.</summary>
        public bool IsPublicAddressLoading { get; private set; }

        /// <summary>
        /// Запрашивает внешний адрес у публичного сервиса.
        ///
        /// Ошибки наружу не пробрасываются: нет интернета — покажем локальный
        /// адрес и предупредим, игра из-за этого падать не должна.
        /// </summary>
        public async Task RequestPublicAddressAsync()
        {
            if (IsPublicAddressLoading) return;

            IsPublicAddressLoading = true;
            StateChanged?.Invoke();
            try
            {
                string address = await NetworkHelper.GetPublicAddressAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(address)) PublicAddress = address;
                Debug.WriteLine($"[RoomSession] внешний адрес: " +
                                (string.IsNullOrEmpty(address) ? "не получен" : address));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomSession] запрос внешнего адреса: {ex.Message}");
            }
            finally
            {
                IsPublicAddressLoading = false;
                StateChanged?.Invoke();
            }
        }

        /// <summary>Записывает уже известный внешний адрес (например, найденный главным окном).</summary>
        public void SetPublicAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            if (string.Equals(PublicAddress, address, StringComparison.Ordinal)) return;

            PublicAddress = address.Trim();
            StateChanged?.Invoke();
        }

        /// <summary>Подключены ли мы к комнате в любом качестве.</summary>
        public bool IsActive => Mode != SessionMode.Offline;

        /// <summary>Меня зан ли я её (хост) или присоединился.</summary>
        public bool IsHost => Mode == SessionMode.Host;

        public string MyPlayerId => IsHost
            ? (_hostPlayer?.Id ?? _server?.GetPlayers().FirstOrDefault(p => p.Role == RoomRole.GameMaster)?.Id ?? "")
            : _client?.PlayerId ?? "";

        public bool AmIGameMaster
        {
            get
            {
                if (IsHost)
                {
                    var me = _server?.GetPlayers().FirstOrDefault(p => p.Id == MyPlayerId);
                    return me == null || me.Role == RoomRole.GameMaster;
                }
                return _client?.Role == RoomRole.GameMaster;
            }
        }

        // ── события: приходят из сетевых потоков, UI обязан переключаться сам ──
        public event Action StateChanged;
        public event Action<FeedEntry> FeedReceived;
        public event Action<RoomSnapshot> SnapshotReceived;

        /// <summary>Карта боя изменилась: у хоста — своя, у игрока — пришла от хоста.</summary>
        public event Action MapChanged;

        /// <summary>Картинка карты собрана у игрока: (хэш, путь к файлу).</summary>
        public event Action<string, string> MapImageReady;

        /// <summary>Ведущий начал разведку заново: игроку нужно очистить «разведанное».</summary>
        public event Action MapSeenReset;

        /// <summary>Жетон сдвинулся во время перетаскивания: (id, x, y, поворот).</summary>
        public event Action<int, double, double, double> TokenMovedLive;

        /// <summary>Получен чужой аватар: файл появился, карточку нужно перерисовать.</summary>
        public event Action<string> PhotoUpdated;

        /// <summary>
        /// Участник ушёл из отряда: (участник, причина).
        /// Приходит из сетевых потоков — интерфейс обязан сам перейти в UI-поток.
        /// </summary>
        public event Action<PlayerInfo, string> PeerDeparted;

        #region === ОТПЕЧАТОК ДАННЫХ ===

        /// <summary>
        /// Отпечаток справочников: по нему понимаем, совпадают ли наборы данных.
        ///
        /// Значение задаёт приложение (оно знает про импортёры), а сетевой слой
        /// только передаёт его в рукопожатие. Так комната не зависит от базы данных
        /// и может собираться отдельно от приложения.
        /// </summary>
        public static string DataFingerprint { get; set; } = "";

        /// <summary>
        /// Откуда брать мои актуальные данные для комнаты.
        ///
        /// Задаёт окно персонажа. Нужно потому, что имя, аватар и здоровье меняются
        /// уже после входа в комнату, и без периодической сверки остальные участники
        /// продолжали видеть первоначальные значения — «безымянных без аватарок».
        /// </summary>
        public static Func<PlayerInfo> MyCharacterProvider { get; set; }

        #endregion

        #region === СОЗДАНИЕ И ПОДКЛЮЧЕНИЕ ===

        /// <summary>Создаёт комнату: хост-сервер + сохранение в AppData. Возвращает ключ.</summary>
        public bool CreateRoom(string name, PlayerInfo me, int port, out string error)
        {
            error = "";
            try
            {
                Leave();

                Port = port <= 0 ? NetVersions.DefaultPort : port;
                string key = RoomKey.Generate();
                string fingerprint = DataFingerprint;

                // Комната в AppData получает идентификатор, который затем использует сервер:
                // так история сессии и папка совпадают, и вчерашнюю игру можно продолжить
                _manifest = RoomStore.CreateRoom(name, key, fingerprint);
                Key = key;

                _server = new RoomServer(_manifest.Id, key, Port, fingerprint);
                _server.RoomName = _manifest.Name ?? "";
                // Событие в ленту (в том числе от гостя) должно дойти до интерфейса:
                // иначе ведущий увидит реплику гостя только после своего действия
                _server.Events.FeedAdded = e => FeedReceived?.Invoke(e);
                _server.Events.StateChanged = () =>
                {
                    StatusText = BuildStatus();
                    StateChanged?.Invoke();
                };
                _server.Events.SystemNotice = (text, id) => { /* уже приходит записью ленты */ };

                // Уход участника: лента пополняется сама, а интерфейсу нужен
                // отдельный сигнал — иначе заметить уход напарника легко
                _server.MapChanged += () => MapChanged?.Invoke();
                _server.TokenMovedLive += (id, x, y, f) => TokenMovedLive?.Invoke(id, x, y, f);
                _server.Events.PeerLeft = (p, reason) => PeerDeparted?.Invoke(p, reason);
                _server.Events.PlayerChanged = p =>
                {
                    StatusText = BuildStatus();
                    StateChanged?.Invoke();
                };

                // История прошлой сессии поднимается сразу: «продолжаем вчерашнюю игру»
                _server.RestoreFeedFromDisk();

                if (!_server.Start(out string startError))
                {
                    error = startError;
                    _server = null;
                    _manifest = null;
                    return false;
                }

                // Хост становится первым участником и ведущим: иначе он не видел бы
                // себя в собственном отряде, ведь он ни к чему не подключается
                _hostPlayer = _server.RegisterHost(me);

                // Свой аватар сразу кладём в папку комнаты и рассылаем как файл:
                // гости просят его по снимку, а снимок указывает уже на эту папку
                TrySendMyPhoto(me);

                Mode = SessionMode.Host;
                HostAddress = NetworkHelper.GetLocalAddress();
                StatusText = BuildStatus();
                StateChanged?.Invoke();

                Debug.WriteLine($"[RoomSession] комната создана: {_manifest.Id}, ключ {key}, порт {Port}");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[RoomSession] CreateRoom: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Подключается к чужой комнате по адресу и ключу.
        /// Порт комнаты и её имя узнаются из приветствия хоста.
        /// </summary>
        public bool JoinRoom(string host, int port, string key, PlayerInfo me, out string error)
        {
            error = "";
            try
            {
                Leave();

                if (string.IsNullOrWhiteSpace(host)) { error = "Укажите адрес хоста."; return false; }
                if (!HMWT_MUI.Net.RoomKey.LooksValid(key)) { error = "Ключ комнаты выглядит неверно."; return false; }

                Port = port <= 0 ? NetVersions.DefaultPort : port;
                Key = HMWT_MUI.Net.RoomKey.Normalize(key);

                _client = new RoomClient(host.Trim(), Port, Key, "", DataFingerprint);
                _client.Events.SnapshotReceived = snap =>
                {
                    // Снимок приносит историю ленты, накопленную до нашего входа
                    MergeSnapshotFeed(snap);
                    StatusText = BuildStatus();
                    SnapshotReceived?.Invoke(snap);
                    StateChanged?.Invoke();

                    // В снимке только ПУТИ к аватарам, а самих файлов у нового
                    // игрока нет: просим недостающие у хоста
                    RequestMissingPhotos();
                };
                _client.Events.FeedReceived = e =>
                {
                    // Каждое сообщение и каждый бросок приходят отдельным событием:
                    // без этого лента гостя не пополнялась до следующего события
                    AppendClientFeed(e);
                    FeedReceived?.Invoke(e);
                };
                _client.Events.Failed = (code, text) =>
                {
                    LastError = text;
                    StatusText = DescribeFailure(code, text);
                    if (code == NetError.VersionMismatch || code == NetError.ProtocolMismatch ||
                        code == NetError.BadKey || code == NetError.DataMismatch)
                    {
                        // Принципиальные отказы: переподключаться бессмысленно
                        _client.AutoReconnect = false;
                    }
                    StateChanged?.Invoke();
                };

                // Чужой аватар пришёл по сети: сохраняем у себя в папке комнаты.
                //
                // Фото передаётся через хоста — у остальных порт закрыт. Здесь
                // оно уже собрано из частей, остаётся записать файл и показать.
                _client.Events.MapStateReceived = scene => MapChanged?.Invoke();
                _client.Events.MapImageReceived = (hash, path) => MapImageReady?.Invoke(hash, path);
                _client.Events.MapSeenResetReceived = () => MapSeenReset?.Invoke();
                _client.Events.TokenLiveReceived = (id, x, y, f) => TokenMovedLive?.Invoke(id, x, y, f);

                _client.Events.PhotoReceived = (ownerId, bytes, extension) =>
                {
                    try
                    {
                        string relative = PhotoTransferService.SaveReceived(RoomId, ownerId, bytes, extension);
                        if (string.IsNullOrEmpty(relative)) return;

                        ApplyReceivedPhoto(ownerId, relative);
                        Debug.WriteLine($"[Фото] получен аватар игрока {ownerId}: {relative}");
                        LogPhotoDiag($"получен аватар {ownerId}: {relative} ({bytes.Length} байт)");
                        StateChanged?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Фото] сохранение чужого аватара: {ex.Message}");
                    }
                };

                // Хост просит наш аватар: отвечаем. Так остальные получают
                // наше фото сразу при входе, а не когда мы его сменим.
                _client.Events.PhotoRequested = ownerId =>
                {
                    try
                    {
                        var me = MyCharacterProvider?.Invoke();
                        string relative = me?.AvatarPath ?? _lastSelfAvatarPath ?? "";
                        if (string.IsNullOrWhiteSpace(relative)) return;

                        string full = HMWT_MUI.PhotoHelper.ResolvePhotoPath(relative);
                        if (string.IsNullOrEmpty(full) || !System.IO.File.Exists(full)) return;

                        Debug.WriteLine($"[Фото] отвечаю на запрос аватара: {relative}");
                        _lastSentAvatarPath = relative;
                        LogPhotoDiag($"отправляю свой аватар по запросу: {relative}");
                        _ = _client.SendPhotoAsync(full, MyPlayerId, RoomId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Фото] ответ на запрос: {ex.Message}");
                    }
                };

                // Хост просит наши данные: отвечаем сразу и безусловно, не глядя
                // на подпись — иначе повторный вход в комнату остался бы без данных
                _client.Events.ProfileRequested = ownerId =>
                {
                    try
                    {
                        var me = MyCharacterProvider?.Invoke();
                        if (me == null) return;

                        Debug.WriteLine("[Отряд] отправляю свои данные по запросу хоста");
                        _lastSelfSignature = "";
                        _ = _client.SendPlayerUpdateAsync(me);
                        TrySendMyPhoto(me);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Отряд] ответ на запрос данных: {ex.Message}");
                    }
                };

                // У хоста нашего аватара нет: не просим его повторно
                _client.Events.PhotoMissing = ownerId =>
                {
                    Debug.WriteLine($"[Фото] у хоста нет аватара {ownerId}");
                    MarkPhotoMissing(ownerId);
                };
                _client.Events.ConnectionChanged = (state, err, connected) =>
                {
                    StatusText = connected ? BuildStatus() : state;
                    StateChanged?.Invoke();
                };

                var task = _client.ConnectAsync(me);
                if (!task.Wait(TimeSpan.FromSeconds(15)))
                {
                    error = "Хост не ответил за 15 секунд.";
                    Leave();
                    return false;
                }

                if (!task.Result)
                {
                    error = _client?.LastError ?? "Не удалось подключиться.";
                    LastError = error;
                    Leave();
                    return false;
                }

                // Комната на диске у клиента не создаётся: история живёт у хоста,
                // клиент получает её снимком. Так нет двух расходящихся копий.
                Mode = SessionMode.Client;

                // У гостя нет манифеста комнаты, но RoomId нужен для папки
                // фотографий: без него полученные аватары сохранялись в одну
                // папку («Rooms/room»), а разыскивались в другой («Rooms/player»)
                // и не показывались никогда.
                _manifest = new RoomManifest
                {
                    Id = _client.RoomId,
                    Name = _client.RoomName,
                    AppVersion = NetVersions.AppVersion,
                    ProtocolVersion = NetVersions.ProtocolVersion,
                    DataFingerprint = DataFingerprint
                };

                // Свой аватар отправляем сразу, не ожидая запроса: ответ мог
                // потеряться, и тогда остальные видели букву вместо фото
                TrySendMyPhoto(me);

                // Комната попадает в историю игрока: в следующий раз достаточно
                // выбрать её в списке, а не вписывать адрес и ключ заново
                HMWT_MUI.Services.ClientRoomHistory.Remember(host, Port, Key, _client.RoomName);

                // Никаких интервалов синхронизации: свои данные уходят сразу при
                // сохранении карточки, чужие — снимком на каждое изменение.

                // Ждём первый снимок от хоста.
                //
                // Без ожидания получалось так: вход уже выполнен, режим выставлен,
                // но снимок с участниками ещё не пришёл — и в отряде показывалось
                // «0/8» и пустой список, хотя игрок уже в комнате. Хост отправляет
                // снимок сразу после подтверждения, поэтому ждём недолго.
                WaitForFirstSnapshot(TimeSpan.FromSeconds(3));

                StatusText = BuildStatus();
                StateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[RoomSession] JoinRoom: {ex}");
                Leave();
                return false;
            }
        }

        /// <summary>
        /// Просит хост прислать свежее состояние комнаты.
        ///
        /// Нужно, когда снимок мог потеряться (например, после переподключения).
        /// Периодической сверки по таймеру больше нет: любое изменение уходит сразу.
        /// </summary>
        public void RequestRefresh()
        {
            try
            {
                if (IsHost || _client == null) return;
                _ = _client.SendSnapshotRequestAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomSession] запрос снимка: {ex.Message}");
            }
        }

        /// <summary>
        /// Ждёт первый снимок от хоста, но не дольше указанного срока.
        ///
        /// Нужно, чтобы сразу после входа в отряде были видны участники, а не
        /// пустой список с надписью «0/8». Снимок приходит почти мгновенно,
        /// поэтому ожидание короткое и на скорость входа не влияет.
        /// </summary>
        private void WaitForFirstSnapshot(TimeSpan timeout)
        {
            var client = _client;
            if (client == null) return;

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = client.LastSnapshot;
                if (snapshot != null)
                {
                    // Снимок уже обработан событием SnapshotReceived, но на случай
                    // гонки применяем его ещё раз — повтор безопасен, дубли отсекаются
                    MergeSnapshotFeed(snapshot);
                    StatusText = BuildStatus();
                    return;
                }

                System.Threading.Thread.Sleep(25);
            }

            Debug.WriteLine("[RoomSession] первый снимок не пришёл за отведённое время");
        }

        /// <summary>Выходит из комнаты и останавливает всё сетевое.</summary>
        public void Leave()        {
            // Сначала прощаемся, пока соединение живо: иначе хост узнает о выходе
            // только по таймауту (около 18 секунд) и всё это время игрок висит
            // в комнате, а у остальных он «в отряде»
            if (Mode == SessionMode.Client && _client != null)
            {
                try { _client.SendLeaveAsync().Wait(400); } catch { }
            }

            try { _client?.Disconnect(); } catch { }
            try { _server?.Stop(); } catch { }

            _client = null;
            _server = null;
            _manifest = null;
            _hostPlayer = null;
            Key = "";
            HostAddress = "";
            Mode = SessionMode.Offline;
            LastError = "";
            StatusText = "Не подключено";

            // Скрытые броски относятся к прежней комнате: в новой ленте
            // они были бы чужими и мешали бы мастеру
            lock (_hiddenFeedLock) { _hiddenFeed.Clear(); }

            // Лента гостя относится к прежней комнате: в новой она была бы чужой
            lock (_clientFeedLock)
            {
                _clientFeed.Clear();
                _clientFeedLoaded = false;
            }
            _lastSelfSignature = "";

            // Аватары и фото относятся к прежней комнате: в новой их нужно
            // отправить и запросить заново
            _lastSentAvatarPath = "";
            _lastHostSourceAvatar = "";
            lock (_receivedPhotos) { _receivedPhotos.Clear(); }
            lock (_requestedLock)
            {
                _requestedPhotos.Clear();
                _photosMissing.Clear();
            }

            StateChanged?.Invoke();
        }

        /// <summary>
        /// Продолжает уже существующую комнату на этом компьютере: поднимает хост
        /// поверх той же папки, поэтому история чата и бросков не теряется.
        /// Ключ читается из манифеста через DPAPI и наружу не передаётся.
        /// </summary>
        public bool ContinueExistingRoom(RoomManifest manifest, string key, PlayerInfo me, out string error)
        {
            error = "";
            try
            {
                if (manifest == null) { error = "Комната не найдена."; return false; }
                if (string.IsNullOrEmpty(key)) { error = "Ключ комнаты недоступен."; return false; }

                Leave();

                _manifest = manifest;
                Key = key;
                Port = NetVersions.DefaultPort;

                _server = new RoomServer(manifest.Id, key, Port, DataFingerprint);
                _server.RoomName = manifest.Name ?? "";
                _server.MapChanged += () => MapChanged?.Invoke();
                _server.TokenMovedLive += (id, x, y, f) => TokenMovedLive?.Invoke(id, x, y, f);
                // Событие в ленту (в том числе от гостя) должно дойти до интерфейса:
                // иначе ведущий увидит реплику гостя только после своего действия
                _server.Events.FeedAdded = e => FeedReceived?.Invoke(e);
                _server.Events.StateChanged = () =>
                {
                    StatusText = BuildStatus();
                    StateChanged?.Invoke();
                };
                _server.Events.PlayerChanged = p =>
                {
                    StatusText = BuildStatus();
                    StateChanged?.Invoke();
                };

                // Поднимаем историю прошлой игры — ради этого комнаты и хранятся
                _server.RestoreFeedFromDisk();

                if (!_server.Start(out string startError))
                {
                    error = startError;
                    _server = null;
                    _manifest = null;
                    return false;
                }

                if (me != null) _hostPlayer = _server.RegisterHost(me);

                // Отмечаем время игры: по нему список показывает «свежие» комнаты
                manifest.LastPlayedAt = DateTime.Now;
                RoomStore.SaveManifest(manifest);

                Mode = SessionMode.Host;
                HostAddress = NetworkHelper.GetLocalAddress();
                StatusText = BuildStatus();
                StateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[RoomSession] ContinueExistingRoom: {ex}");
                return false;
            }
        }

        #endregion

        #region === ИГРОВЫЕ ДЕЙСТВИЯ ===

        /// <summary>
        /// Отправляет бросок в комнату. Скрытый бросок ведущего остаётся
        /// только у него: в общую ленту и в снимок он не попадает вообще,
        /// поэтому игроки не узнают даже, что мастер что-то бросал.
        /// </summary>
        public void PublishRoll(string formula, string rollType, int total,
            List<int> rolls, bool hidden = false)
        {
            if (!IsActive) return;

            if (IsHost)
            {
                // Глобальный переключатель мастера + явный флаг отдельного броска
                bool hide = AmIGameMaster && (hidden || HiddenDmRolls);

                var entry = new FeedEntry
                {
                    Kind = FeedKind.Roll,
                    AuthorId = MyPlayerId,
                    AuthorName = MyName(),
                    Text = string.IsNullOrWhiteSpace(rollType) ? "Бросок" : rollType,
                    Formula = formula ?? "",
                    Total = total,
                    IndividualRolls = rolls ?? new List<int>(),
                    Hidden = hide
                };

                if (hide)
                {
                    // Только локальная лента ведущего. Сервер об этом броске
                    // не знает и разослать его не может.
                    lock (_hiddenFeedLock)
                    {
                        _hiddenFeed.Add(entry);
                        if (_hiddenFeed.Count > 200) _hiddenFeed.RemoveRange(0, _hiddenFeed.Count - 200);
                    }

                    FeedReceived?.Invoke(entry);
                    return;
                }

                _server.AddFeed(entry);
                _ = _server.BroadcastSnapshotAsync();
            }
            else
            {
                _ = _client.SendRollAsync(formula, rollType, total, rolls, hidden && AmIGameMaster);
            }
        }

        public void PublishChat(string text)
        {
            if (!IsActive || string.IsNullOrWhiteSpace(text)) return;

            if (IsHost)
            {
                var entry = new FeedEntry
                {
                    Kind = FeedKind.Chat,
                    AuthorId = MyPlayerId,
                    AuthorName = MyName(),
                    Text = text.Trim()
                };
                _server.AddFeed(entry);
                _ = _server.BroadcastSnapshotAsync();
            }
            else
            {
                _ = _client.SendChatAsync(text);
            }
        }

        /// <summary>
        /// Публикует состояние трекера инициативы.
        ///
        /// Ведёт бой ведущий (он же хост): игрокам уходит только имя и результат
        /// броска, чтобы они видели очередь и свой ход. У гостя вызов тихо
        /// игнорируется — права на трекер у него нет.
        /// </summary>
        public void PublishInitiative(InitiativeState state)
        {
            if (!IsActive || !IsHost || state == null) return;

            _server.SetInitiative(state);
            StateChanged?.Invoke();
        }

        // ══════════════════════ КАРТА БОЯ ══════════════════════

        /// <summary>Текущая карта боя: у хоста — своя, у игрока — присланная.</summary>
        public MapSceneData GetMapScene()
        {
            if (!IsActive) return null;
            if (IsHost) return _server?.GetMap();
            return _client?.LastMap;
        }

        /// <summary>Ведущий изменил карту — раскладываем игрокам.</summary>
        public void PublishMapScene(MapSceneData scene)
        {
            if (!IsActive || !IsHost || scene == null || _server == null) return;

            _server.SetMap(scene);
        }

        /// <summary>Ведущий начал разведку заново — просим клиентов очистить «разведанное».</summary>
        public void ResetClientsSeen()
        {
            if (!IsActive || !IsHost || _server == null) return;

            _server.BroadcastSeenReset();
        }

        /// <summary>Игрок подвинул своего жетона: отправляем хосту.</summary>
        public bool SendMapMove(int tokenId, double x, double y, double facing = double.NaN,
            double viewAngle = -1, int sizeCells = -1)
        {
            if (!IsActive || IsHost || _client == null || !_client.IsConnected) return false;

            if (viewAngle >= 0 || sizeCells >= 1)
                _ = _client.SendMapMoveEditAsync(tokenId, x, y, facing, viewAngle, sizeCells);
            else
                _ = _client.SendMapMoveAsync(tokenId, x, y, facing);
            return true;
        }

        /// <summary>Промежуточное движение игрока: остальные видят жетон сразу.</summary>
        public void SendMapMoveLive(int tokenId, double x, double y, double facing = double.NaN)
        {
            if (!IsActive || IsHost || _client == null || !_client.IsConnected) return;

            _ = _client.SendMapMoveLiveAsync(tokenId, x, y, facing);
        }

        /// <summary>Ведущий двигает жетон: рассылаем без записи в базу на каждом шаге.</summary>
        public void PublishLiveTokenMove(int tokenId, double x, double y, double facing = double.NaN)
        {
            if (!IsActive || !IsHost || _server == null) return;

            _server.UpdateTokenLive(tokenId, x, y, facing);
        }

        /// <summary>Игрок просит карту (например, только что открыл вкладку).</summary>
        public void RequestMap()
        {
            if (!IsActive || IsHost || _client == null || !_client.IsConnected) return;

            _ = _client.SendMapRequestAsync();
        }

        /// <summary>Игрок просит картинку карты по хэшу.</summary>
        public void RequestMapImage(string hash)
        {
            if (!IsActive || IsHost || _client == null || !_client.IsConnected) return;
            if (string.IsNullOrWhiteSpace(hash)) return;

            _ = _client.SendMapImageRequestAsync(hash);
        }

        /// <summary>Игрок нарисовал штрих — отправляем ведущему.</summary>
        public void SendMapStroke(MapStrokeData stroke)        {
            if (!IsActive || IsHost || _client == null || !_client.IsConnected) return;
            if (stroke == null || string.IsNullOrWhiteSpace(stroke.Points)) return;

            _ = _client.SendMapStrokeAsync(new MapStrokeRequest
            {
                Color = stroke.Color ?? "",
                Thickness = stroke.Thickness,
                Points = stroke.Points
            });
        }

        /// <summary>Игрок стёр штрихи — просим ведущего удалить их у всех.</summary>
        public void EraseMapStrokes(List<int> ids)
        {
            if (!IsActive || IsHost || _client == null || !_client.IsConnected) return;
            if (ids == null || ids.Count == 0) return;

            _ = _client.SendMapStrokeEraseAsync(ids);
        }
        /// <summary>Состояние трекера инициативы: у хоста — своё, у гостя — из снимка.</summary>
        public InitiativeState GetInitiative()
        {
            if (!IsActive) return new InitiativeState();

            if (IsHost) return _server.GetInitiative();
            return _client?.LastSnapshot?.Initiative ?? new InitiativeState();
        }

        /// <summary>Системная запись в ленту: «Инициатива: Гоблин — 17, ...».</summary>
        public void PublishSystemNotice(string text)
        {
            if (!IsActive || !IsHost || string.IsNullOrWhiteSpace(text)) return;

            var entry = new FeedEntry
            {
                Kind = FeedKind.System,
                AuthorId = MyPlayerId,
                AuthorName = MyName(),
                Text = text.Trim()
            };
            _server.AddFeed(entry);
            _ = _server.BroadcastSnapshotAsync();
        }

        /// <summary>
        /// Обновляет мои данные в комнате (изменилось ХП, класс и т.п.).
        ///
        /// Хост рассылает обновление сразу всем, гость отправляет запрос.
        /// Отправляем только при реальном изменении: сравниваем подпись данных,
        /// чтобы не гонять трафик на каждый вызов.
        /// </summary>
        /// <summary>
        /// Путь к аватару, который уже отправлен в комнату.
        ///
        /// Нужен, чтобы не гонять файл на каждую сверку: фото уходит только
        /// когда аватар действительно сменился.
        /// </summary>
        private string _lastSentAvatarPath = "";

        /// <summary>
        /// Исходный (из карточки) путь аватара, который ведущий уже скопировал
        /// в папку комнаты. Нужен, чтобы не копировать тот же файл повторно.
        /// </summary>
        private string _lastHostSourceAvatar = "";

        /// <summary>
        /// Последний известный путь к своему аватару.
        ///
        /// Нужен для ответа на запрос хоста: окно персонажа может быть уже
        /// закрыто, и взять путь из него не получится.
        /// </summary>
        private string _lastSelfAvatarPath = "";

        /// <summary>
        /// Пути к чужим аватарам, полученным по сети: идентификатор → путь.
        ///
        /// Снимок может прийти раньше, чем файл, поэтому полученный путь
        /// подставляем сами — иначе аватар не показался бы до следующего снимка.
        /// </summary>
        private readonly Dictionary<string, string> _receivedPhotos =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Файл пришёл — просить его больше не нужно.</summary>
        private void ApplyReceivedPhoto(string ownerId, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(relativePath)) return;

            lock (_receivedPhotos)
            {
                _receivedPhotos[ownerId] = relativePath;
            }

            // Файл пришёл — просить его больше не нужно
            lock (_requestedLock)
            {
                _requestedPhotos.Remove(ownerId);
                _photosMissing.Remove(ownerId);
            }

            // Карточка участника уже нарисована с путём, который был в снимке:
            // подпись данных не меняется, и без этого сигнала аватар появлялся
            // только после перезахода на вкладку
            PhotoUpdated?.Invoke(ownerId);
        }

        /// <summary>
        /// Владельцы, у которых хоста нет файла аватара. Их больше не просим:
        /// иначе «ждём фото» горело бы вечно, хотя картинки не существует.
        /// </summary>
        private readonly HashSet<string> _photosMissing = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Аватар этого участника запросить невозможно: у хоста его нет.</summary>
        public bool IsPhotoMissing(string ownerId)
            => !string.IsNullOrEmpty(ownerId) && _photosMissing.Contains(ownerId);

        /// <summary>Запоминает, что у хоста нет аватара участника.</summary>
        public void MarkPhotoMissing(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) return;

            lock (_requestedLock)
            {
                _requestedPhotos.Remove(ownerId);
                if (_photosMissing.Count < 64) _photosMissing.Add(ownerId);
            }
        }

        /// <summary>Чьи аватары уже запрошены, чтобы не спрашивать повторно.</summary>
        /// <summary>
        /// Уже запрошенные аватары: игрок → путь, который мы просили.
        ///
        /// Путь важен: если игрок сменил фото, путь в снимке становится другим,
        /// и файл нужно просить заново. Раньше здесь лежал только идентификатор
        /// игрока и запрос делался один раз — после смены аватара остальные
        /// больше ничего не получали.
        /// </summary>
        private readonly Dictionary<string, string> _requestedPhotos =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Отдельный замок для списка запрошенных аватаров.</summary>
        private readonly object _requestedLock = new object();

        /// <summary>
        /// Просит у хоста аватары участников, которых у нас ещё нет.
        ///
        /// В снимке комнаты есть только путь к файлу, а самого файла у нового
        /// игрока нет: фотографии передаются отдельно. Без этого запроса чужие
        /// аватары не появлялись до смены фото их владельцем.
        /// </summary>
        public void RequestMissingPhotos(bool force = false)
        {
            try
            {
                if (IsHost || _client == null || !_client.IsConnected) return;

                foreach (var player in GetPlayers())
                {
                    if (player == null) continue;
                    if (string.IsNullOrWhiteSpace(player.AvatarPath)) continue;
                    if (string.Equals(player.Id, MyPlayerId, StringComparison.Ordinal)) continue;

                    // У хоста этого файла нет — повторные просьбы бессмысленны
                    if (IsPhotoMissing(player.Id)) continue;

                    // Файл уже есть на диске — просить не нужно
                    string full = HMWT_MUI.PhotoHelper.ResolvePhotoPath(player.AvatarPath);
                    if (!string.IsNullOrEmpty(full) && System.IO.File.Exists(full)) continue;

                    lock (_requestedLock)
                    {
                        // Обычно просим только при смене пути: значит, у игрока новое
                        // фото. Повтор (force) нужен таймеру: ответ мог потеряться.
                        if (!force &&
                            _requestedPhotos.TryGetValue(player.Id, out string asked) &&
                            string.Equals(asked, player.AvatarPath, StringComparison.Ordinal))
                            continue;

                        _requestedPhotos[player.Id] = player.AvatarPath;
                    }

                    Debug.WriteLine($"[Фото] запрашиваю аватар игрока {player.Id}");
                    _ = _client.SendPhotoRequestAsync(player.Id);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Фото] запрос недостающих: {ex.Message}");
            }
        }

        /// <summary>
        /// Подставляет полученные по сети аватары в список участников.
        ///
        /// Снимок от хоста приходит со своим путём к файлу, но у нас файл мог
        /// лечь под другим именем — поэтому берём то, что реально сохранили.
        /// </summary>
        private List<PlayerInfo> ApplyReceivedPhotos(List<PlayerInfo> players)
        {
            if (players == null || players.Count == 0) return players;

            foreach (var player in players)
            {
                if (player == null) continue;

                // Свой аватар показываем из своего файла: в снимке от хоста лежит
                // ЕГО путь к комнатной папке, и такого файла у нас нет — из-за этого
                // игрок не видел собственную новую картинку
                if (string.Equals(player.Id, MyPlayerId, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(_lastSelfAvatarPath))
                {
                    player.AvatarPath = _lastSelfAvatarPath;
                    continue;
                }

                lock (_receivedPhotos)
                {
                    if (_receivedPhotos.TryGetValue(player.Id ?? "", out string path))
                        player.AvatarPath = path;
                }
            }

            return players;
        }

        public void UpdateMyCharacter(PlayerInfo me)
        {
            if (!IsActive || me == null) return;

            string signature = SignatureOf(me);
            if (signature == _lastSelfSignature) return;

            _lastSelfSignature = signature;

            LogPartySend(me);

            if (IsHost)
            {
                _server.UpdatePlayer(MyPlayerId, me);
            }
            else if (_client != null && _client.IsConnected)
            {
                _ = _client.SendPlayerUpdateAsync(me);
            }

            // Сменился аватар — отправляем сам файл.
            //
            // Напрямую другим игрокам его передать нельзя: порт открыт только
            // у ведущего. Поэтому файл идёт хосту, а тот рассылает остальным.
            TrySendMyPhoto(me);

            StateChanged?.Invoke();
        }

        /// <summary>
        /// Пишет в room-log, какие данные участника ушли в отряд.
        ///
        /// Без этого «у меня 210 максимума, а в отряде 185» приходилось
        /// разбирать догадками: в журнале видно и текущее, и максимум.
        /// </summary>
        private static void LogPartySend(PlayerInfo me)
        {
            try
            {
                string dir = HMWT_MUI.PhotoHelper.GetWritableRoot();
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "room-log.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} отправка участника: персонаж {me.CharacterId} " +
                    $"«{me.CharacterName}», хиты {me.Hp}/{me.MaxHp}, класс «{me.CharacterClass}», " +
                    $"аватар «{me.AvatarPath}», в отряде «{me.Name}»\r\n");
            }
            catch { }
        }

        /// <summary>Строка о передаче аватара в room-log (отправка/получение).</summary>
        private static void LogPhotoDiag(string text)
        {
            try
            {
                string dir = HMWT_MUI.PhotoHelper.GetWritableRoot();
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "room-log.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} фото: {text}\r\n");
            }
            catch { }
        }

        /// <summary>
        /// Отправляет свой аватар в комнату, если он ещё не отправлялся.
        /// </summary>
        private void TrySendMyPhoto(PlayerInfo me)
        {
            try
            {
                string relative = me?.AvatarPath ?? "";
                if (string.IsNullOrWhiteSpace(relative)) return;

                // Запоминаем: может понадобиться для ответа на запрос хоста,
                // когда окно персонажа уже закрыто
                _lastSelfAvatarPath = relative;

                // Такой же путь уже отправляли — файл гонять не нужно
                if (string.Equals(relative, _lastSentAvatarPath, StringComparison.OrdinalIgnoreCase)) return;

                string full = HMWT_MUI.PhotoHelper.ResolvePhotoPath(relative);
                if (string.IsNullOrEmpty(full) || !System.IO.File.Exists(full))
                {
                    Debug.WriteLine($"[Фото] файл аватара не найден: {relative}");
                    return;
                }

                if (!PhotoTransferService.IsSupportedImage(full))
                {
                    Debug.WriteLine($"[Фото] формат не поддерживается: {full}");
                    return;
                }

                // Ведущий кладёт файл себе сам, гости отправляют его по сети
                if (IsHost)
                {
                    // Исходный путь из карточки не меняется, а копия в папке
                    // комнаты каждый раз получает новое имя — сравниваем исходник,
                    // иначе аватар копировался бы при каждом сохранении профиля
                    if (string.Equals(relative, _lastHostSourceAvatar, StringComparison.OrdinalIgnoreCase)) return;

                    string saved = PhotoTransferService.SaveReceived(
                        RoomId, MyPlayerId, System.IO.File.ReadAllBytes(full),
                        System.IO.Path.GetExtension(full));

                    if (!string.IsNullOrEmpty(saved) && _server != null)
                    {
                        me.AvatarPath = saved;
                        _server.UpdatePlayer(MyPlayerId, me);
                        _lastHostSourceAvatar = relative;

                        // Сравнивать в следующий раз нужно с ПУТЁМ В ПАПКЕ КОМНАТЫ:
                        // иначе профиль с уже скопированным путём копировался бы
                        // заново при каждом сохранении карточки
                        _lastSentAvatarPath = saved;
                        _lastSelfAvatarPath = saved;
                        _ = _server.BroadcastSnapshotAsync();

                        // И сразу шлём сам файл: иначе игроки видят только путь
                        // к файлу, которого у них нет
                        _ = _server.SendPhotoToAllAsync(saved, MyPlayerId);

                        Debug.WriteLine($"[Фото] аватар ведущего сохранён и разослан: {saved}");
                        LogPhotoDiag($"ведущий сохранил свой аватар: {saved}");
                    }
                    return;
                }

                if (_client == null || !_client.IsConnected) return;

                _lastSentAvatarPath = relative;
                LogPhotoDiag($"отправляю свой аватар: {relative} → комната {RoomId}");
                _ = _client.SendPhotoAsync(full, MyPlayerId, RoomId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Фото] отправка своего аватара: {ex.Message}");
            }
        }

        /// <summary>Короткая подпись значимых полей: по ней видно, менялись ли данные.</summary>
        private static string SignatureOf(PlayerInfo p)
            => $"{p.CharacterName}|{p.Name}|{p.CharacterClass}|{p.CharacterRace}|{p.Level}|{p.Hp}|{p.MaxHp}|{p.ArmorClass}|{p.AvatarPath}";

        /// <summary>
        /// Отправляет свои данные заново (например, включили имя из профиля).
        ///
        /// Сравнение подписей не даст пропустить изменение: имя участника
        /// входит в подпись, поэтому новый вариант уходит в комнату сразу.
        /// </summary>
        public void RepublishMyProfile()
        {
            if (!IsActive) return;

            var provider = MyCharacterProvider;
            if (provider == null) return;

            try
            {
                var me = provider();
                if (me != null) UpdateMyCharacter(me);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Отряд] повторная отправка профиля: {ex.Message}");
            }
        }

        #endregion

        #region === ЛЕНТА ГОСТЯ ===

        /// <summary>
        /// Кладёт запись в ленту гостя. Вызывается для каждой записи, пришедшей
        /// от хоста, и повторно для записей из снимка — дубли отсекаются.
        /// </summary>
        public void AddPeerFeedEntry(FeedEntry entry) => AppendClientFeed(entry);

        /// <summary>
        /// Применяет историю из снимка к ленте гостя.
        ///
        /// Публичный, потому что снимок приходит от хоста и должен проходить
        /// ровно через эту логику: она сохраняет свежие записи, которых в снимке
        /// ещё нет, вместо того чтобы затирать ими ленту.
        /// </summary>
        public void MergeHistoryForTest(RoomSnapshot snapshot) => MergeSnapshotFeed(snapshot);

        /// <summary>
        /// Переносит историю из снимка в локальную ленту.
        ///
        /// Раньше лента заменялась целиком, и это теряло свежие записи: снимок
        /// формируется хостом чуть раньше, чем приходит, поэтому только что
        /// полученное сообщение им же и затиралось — гость не видел собственных
        /// реплик до следующего действия. Теперь снимок служит основой, а записи,
        /// которых в нём ещё нет, добавляются следом.
        /// </summary>
        private void MergeSnapshotFeed(RoomSnapshot snapshot)
        {
            if (snapshot?.Feed == null) return;

            lock (_clientFeedLock)
            {
                var snapshotFeed = snapshot.Feed;

                // Что уже есть в снимке — по этим признакам отличаем дубли
                var snapshotKeys = new HashSet<string>(
                    snapshotFeed.Select(KeyOf), StringComparer.Ordinal);

                // Свежие локальные записи, которых в снимке ещё нет
                var newer = _clientFeed.Where(e => !snapshotKeys.Contains(KeyOf(e))).ToList();

                _clientFeed.Clear();
                _clientFeed.AddRange(snapshotFeed);
                _clientFeed.AddRange(newer);
                _clientFeedLoaded = true;
            }
        }

        /// <summary>Ключ записи для отсечения дублей между снимком и событиями.</summary>
        private static string KeyOf(FeedEntry e)
            => $"{(int)e.Kind}|{e.At.Ticks}|{e.AuthorId}|{e.Text}";

        /// <summary>Добавляет событие в ленту гостя.</summary>
        private void AppendClientFeed(FeedEntry entry)
        {
            if (entry == null) return;


            lock (_clientFeedLock)
            {
                // Одно и то же событие приходит и отдельным сообщением, и в снимке —
                // отсекаем по автору, времени и тексту
                string key = KeyOf(entry);
                bool duplicate = _clientFeed.Any(e => KeyOf(e) == key);

                if (duplicate) return;

                _clientFeed.Add(entry);

                // Держим разумный размер: старая история всё равно есть у хоста
                int limit = NetLimits.MaxChatHistory + NetLimits.MaxRollHistory;
                if (_clientFeed.Count > limit)
                    _clientFeed.RemoveRange(0, _clientFeed.Count - limit);
            }
        }

        /// <summary>Подтягивает ленту из последнего снимка, если она ещё не собрана.</summary>
        private void EnsureClientFeedLoaded()
        {
            if (_clientFeedLoaded) return;

            var snapshot = _client?.LastSnapshot;
            if (snapshot?.Feed != null && snapshot.Feed.Count > 0)
                MergeSnapshotFeed(snapshot);
        }

        #endregion

        #region === ЧТЕНИЕ СОСТОЯНИЯ ===

        public List<PlayerInfo> GetPlayers()
        {
            var players = IsHost
                ? (_server?.GetPlayers() ?? new List<PlayerInfo>())
                : (_client?.GetPlayers() ?? new List<PlayerInfo>());

            // Полученные по сети аватары подставляем сами: снимок может прийти
            // раньше, чем файл, и тогда чужое фото не показалось бы
            return ApplyReceivedPhotos(players);
        }

        public List<FeedEntry> GetFeed(int max = 200)
        {
            if (IsHost)
            {
                var feed = _server?.GetFeed(max) ?? new List<FeedEntry>();

                // Скрытые броски лежат только в памяти ведущего: в снимке
                // и на диске комнаты их нет, поэтому игроки их не получат
                lock (_hiddenFeedLock)
                    feed.AddRange(_hiddenFeed);

                return feed;
            }

            // У гостя лента своя: снимок приходит редко (при входе и смене состава),
            // а сообщения и броски — отдельными событиями. Раньше здесь читался
            // только снимок, поэтому новые записи не появлялись до следующего
            // события: своё сообщение игрок видел лишь после второй отправки.
            EnsureClientFeedLoaded();
            lock (_clientFeedLock)
            {
                int take = Math.Min(max, _clientFeed.Count);
                return _clientFeed.Skip(_clientFeed.Count - take).ToList();
            }
        }

        /// <summary>Идентификатор моей записи в списке участников.</summary>
        public string GetMyPlayerId() => MyPlayerId;

        #endregion

        #region === ВСПОМОГАТЕЛЬНОЕ ===

        private string MyName()
        {
            var me = GetPlayers().FirstOrDefault(p => p.Id == MyPlayerId);
            if (me != null)
            {
                if (!string.IsNullOrWhiteSpace(me.CharacterName)) return me.CharacterName;
                if (!string.IsNullOrWhiteSpace(me.Name)) return me.Name;
            }
            return "Я";
        }

        private string BuildStatus()
        {
            if (!IsActive) return "Не подключено";

            int players = GetPlayers().Count;
            string who = IsHost ? "хост" : (AmIGameMaster ? "ведущий" : "игрок");
            return $"В отряде: {players}/{NetLimits.MaxPlayers} · вы {who}";
        }

        /// <summary>Переводит код отказа в понятный игроку текст со ссылкой на обновление.</summary>
        private static string DescribeFailure(string code, string text)
        {
            switch (code)
            {
                case NetError.VersionMismatch:
                case NetError.ProtocolMismatch:
                    return $"{text} Скачайте новую версию: {NetVersions.ReleasesUrl}";
                case NetError.DataMismatch:
                    return text + " Уточните, что у всех одинаковые файлы справочников.";
                case NetError.BadKey:
                    return "Неверный ключ комнаты. Проверьте раскладку и регистр — ключ не чувствителен к регистру.";
                case NetError.RoomFull:
                    return text;
                default:
                    return string.IsNullOrEmpty(text) ? "Не удалось подключиться." : text;
            }
        }

        /// <summary>Локальный IPv4 — его хост сообщает игрокам для подключения.</summary>
        private static string LocalAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var ipv4 = host.AddressList.FirstOrDefault(a =>
                    a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(a));
                return ipv4?.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        #endregion
    }
}
