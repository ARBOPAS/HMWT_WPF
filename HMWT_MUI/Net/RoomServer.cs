using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;

namespace HMWT_MUI.Net
{
    /// <summary>События сервера комнаты для интерфейса.</summary>
    public sealed class RoomServerEvents
    {
        public Action<FeedEntry> FeedAdded { get; set; }
        public Action<PlayerInfo> PlayerChanged { get; set; }
        public Action<string, string> SystemNotice { get; set; }   // (текст, идентификатор)

        /// <summary>
        /// Участник ушёл. Второй параметр — причина: «таймаут» (перестал отвечать)
        /// или «соединение закрыто» (вышел сам либо пропала сеть).
        ///
        /// Нужно интерфейсу: лента об уходе уже пополняется, но у ведущего нет
        /// отдельного сигнала, и заметить уход напарника было легко.
        /// </summary>
        public Action<PlayerInfo, string> PeerLeft { get; set; }

        public Action StateChanged { get; set; }
        public Action<string> Log { get; set; }
    }

    /// <summary>Подключённый клиент на стороне хоста.</summary>
    internal sealed class ClientConnection
    {
        public string PlayerId;
        public TcpClient Tcp;
        public NetworkStream Stream;
        public PlayerInfo Player;
        public CancellationTokenSource Cts;
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public DateTime LastPingUtc = DateTime.MinValue;
        public DateTime LastChatImageUtc = DateTime.MinValue;
        public bool HandshakeDone;

        /// <summary>
        /// Почему оборвалась связь: «таймаут» — участник перестал отвечать на пинг,
        /// «соединение закрыто» — сокет закрылся (игрок вышел или упала сеть).
        /// Игроки должны видеть причину: таймаут и добровольный выход — разное.
        /// </summary>
        public string DisconnectReason = "соединение закрыто";

        /// <summary>
        /// Замок записи. КРИТИЧНО: в один сокет нельзя писать из нескольких потоков
        /// одновременно — заголовки длины перемешаются, и клиент прочитает мусор
        /// вместо сообщения. Именно так у нас и разрывалось соединение.
        /// </summary>
        public readonly SemaphoreSlim WriteLock = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Сервер комнаты: его поднимает хост.
    ///
    /// Модель — хост авторитетен: он ведёт участников и ленту, рассылает снимки.
    /// Кубы считает клиент (у него уже есть DiceRoller), а хост только фиксирует
    /// результат в общей ленте — это снимает кучу гонок.
    ///
    /// Ссылок на WPF здесь нет: сервер не знает, что существует Dispatcher.
    /// </summary>
    public sealed class RoomServer : IDisposable
    {
        private readonly string _roomId;
        private readonly string _keySecret;
        private readonly int _port;
        private readonly string _dataFingerprint;

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private Task _heartbeatTask;

        private readonly ConcurrentDictionary<string, ClientConnection> _clients =
            new ConcurrentDictionary<string, ClientConnection>();

        private readonly List<PlayerInfo> _players = new List<PlayerInfo>();
        private readonly List<FeedEntry> _feed = new List<FeedEntry>();
        private readonly object _stateLock = new object();

        /// <summary>
        /// Трекер инициативы ведущего: то, что уходит игрокам в снимке.
        /// Хост только хранит и рассылает — ведёт бой приложение ведущего.
        /// </summary>
        private InitiativeState _initiative = new InitiativeState();

        /// <summary>
        /// Отложенная синхронизация жетонов с трекером: SaveCombatant зовут
        /// пачками (броски инициативы, состояния), а карту на каждый вызов
        /// рассылать незачем.
        /// </summary>
        private System.Threading.Timer _combatantSyncTimer;

        /// <summary>Скрипт зоны сработал для персонажа ведущего (нет клиента).</summary>
        public event Action<ZoneRollRequest> ZoneRollRequested;

        /// <summary>Карта боя, которую видит комната. Снимок делается на хосте.</summary>
        private MapSceneData _map;

        /// <summary>Хост изменил карту (например, игрок подвинул своего жетона).</summary>
        public event Action MapChanged;

        /// <summary>Пришёл новый разведанный сектор от игрока: ведущий добавляет его себе.</summary>
        public event Action<string> MapSeenAdded;

        /// <summary>Кто-то пингнул точку на карте: показать всем.</summary>
        public event Action<double, double> Pinged;

        /// <summary>Жетон сдвинулся во время перетаскивания (лёгкое обновление).</summary>
        public event Action<int, double, double, double> TokenMovedLive;

        /// <summary>Фигуру ведут мышью: (id, x, y). Полную карту не шлём.</summary>
        public event Action<int, double, double> ShapeMovedLive;

        /// <summary>Игрок прислал пройденный путь жетона.</summary>
        public event Action<int, List<MapTrailPoint>> MapTrailAdded;

        private int _playerCounter;

        public RoomServerEvents Events { get; set; } = new RoomServerEvents();
        public string RoomId => _roomId;
        public string KeySecret => _keySecret;
        public int Port => _port;
        public bool IsRunning => _listener != null;

        /// <summary>
        /// Человеческое имя комнаты. Задаёт ведущий при создании/продолжении;
        /// уходит гостю в приветствии и снимке, чтобы он видел не только id.
        /// </summary>
        public string RoomName { get; set; } = "";

        /// <summary>Интервал heartbeat: по нему определяем отвалившихся.</summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Сколько ждать ответа, прежде чем считать участника отключённым.</summary>
        public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(18);

        public RoomServer(string roomId, string keySecret, int port, string dataFingerprint)
        {
            _roomId = roomId;
            _keySecret = keySecret;
            _port = port <= 0 ? NetVersions.DefaultPort : port;
            _dataFingerprint = dataFingerprint ?? "";
        }

        #region === ЗАПУСК И ОСТАНОВКА ===

        /// <summary>
        /// Поднимает слушатель. DualMode обязателен: без него часть игроков
        /// с IPv6 просто не подключится, и это трудно диагностировать.
        /// </summary>
        public bool Start(out string error)
        {
            error = "";
            try
            {
                _cts = new CancellationTokenSource();

                var listener = new TcpListener(IPAddress.IPv6Any, _port);
                listener.Server.DualMode = true;
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                _listener = listener;

                _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

                // Состояния и хиты меняют трекер, карта и лист персонажа. Как
                // только меняется участник боя — переносим это в жетоны карты
                // и рассылаем игрокам, иначе у клиента состояния «не доезжают»
                // до следующего случайного обновления карты. Подписка здесь,
                // а не в конструкторе: неудачный Start не должен оставлять
                // обработчик на статическом событии.
                Services.InitiativeService.Changed -= OnInitiativeChanged;
                Services.InitiativeService.Changed += OnInitiativeChanged;

                Log($"Комната запущена на порту {_port}");
                Events?.StateChanged?.Invoke();
                return true;
            }
            catch (SocketException ex)
            {
                error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Порт {_port} уже занят. Выберите другой порт."
                    : $"Не удалось открыть порт {_port}: {ex.Message}";
                Log(error);
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log("Ошибка запуска: " + ex.Message);
                return false;
            }
        }

        public void Stop()
        {
            try { Services.InitiativeService.Changed -= OnInitiativeChanged; } catch { }
            try { _combatantSyncTimer?.Dispose(); } catch { }
            _combatantSyncTimer = null;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }

            foreach (var c in _clients.Values)
            {
                try { c.Cts.Cancel(); } catch { }
                try { c.Tcp?.Close(); } catch { }
            }
            _clients.Clear();

            _listener = null;
            Log("Комната остановлена");
            Events?.StateChanged?.Invoke();
        }

        public void Dispose() => Stop();

        #endregion

        #region === ПРИЁМ ПОДКЛЮЧЕНИЙ ===

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient tcp = null;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    Log("Ошибка приёма: " + ex.Message);
                    break;
                }

                if (tcp == null) continue;
                _ = Task.Run(() => HandleClientAsync(tcp, token));
            }
        }

        private async Task HandleClientAsync(TcpClient tcp, CancellationToken serverToken)
        {
            var conn = new ClientConnection
            {
                Tcp = tcp,
                Stream = tcp.GetStream(),
                PlayerId = Guid.NewGuid().ToString("N").Substring(0, 8),
                Cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken)
            };

            try
            {
                tcp.NoDelay = true;

                // ── 1. Приветствие: сообщаем требования и выдаём nonce ──
                var challenge = new HelloChallenge
                {
                    RoomId = _roomId,
                    RoomName = string.IsNullOrWhiteSpace(RoomName) ? _roomId : RoomName,
                    AppVersion = NetVersions.AppVersion,
                    ProtocolVersion = NetVersions.ProtocolVersion,
                    DataFingerprint = _dataFingerprint,
                    Nonce = RoomKey.NewNonce(),
                    PlayerCount = _players.Count,
                    MaxPlayers = NetLimits.MaxPlayers
                };
                await SendSafeAsync(conn, new NetMessage(MsgType.Hello, challenge)).ConfigureAwait(false);

                // ── 2. Ответ клиента ──
                var reply = await NetFraming.ReadAsync(conn.Stream, conn.Cts.Token).ConfigureAwait(false);
                if (reply == null || reply.Type != MsgType.Hello)
                {
                    await SendErrorAsync(conn, NetError.Internal, "Не получено приветствие").ConfigureAwait(false);
                    return;
                }

                var hello = reply.As<HelloRequest>();
                if (hello == null)
                {
                    await SendErrorAsync(conn, NetError.Internal, "Повреждённое приветствие").ConfigureAwait(false);
                    return;
                }

                // ── 3. Проверки: версия, протокол, ключ, место ──
                string failCode, failText;
                if (!Validate(hello, challenge.Nonce, out failCode, out failText))
                {
                    await SendErrorAsync(conn, failCode, failText).ConfigureAwait(false);
                    Log($"Отказано подключению: {failText}");
                    return;
                }

                // ── 4. Регистрируем участника ──
                // Проверка места и добавление идут под ОДНИМ замком: иначе два
                // одновременных подключения могут оба увидеть пустой список,
                // и роль мастера получат оба, а участники «потеряются».
                var player = hello.Player ?? new PlayerInfo();
                player.Id = conn.PlayerId;
                player.State = PeerState.Online;



                lock (_stateLock)
                {
                    if (_players.Count >= NetLimits.MaxPlayers)
                    {
                        _ = SendErrorAsync(conn, NetError.RoomFull,
                            $"В комнате уже {NetLimits.MaxPlayers} участников.");
                        return;
                    }

                    player.Role = _players.Count == 0 ? RoomRole.GameMaster : RoomRole.Player;
                    _players.Add(player);
                }

                conn.Player = player;
                conn.HandshakeDone = true;

                if (!_clients.TryAdd(conn.PlayerId, conn))
                {
                    lock (_stateLock) _players.RemoveAll(p => p.Id == conn.PlayerId);
                    await SendErrorAsync(conn, NetError.Internal, "Не удалось зарегистрировать").ConfigureAwait(false);
                    return;
                }

                var ok = new HelloResponse
                {
                    Ok = true,
                    PlayerId = player.Id,
                    Role = player.Role,
                    RequiredAppVersion = NetVersions.AppVersion,
                    RequiredProtocolVersion = NetVersions.ProtocolVersion,
                    ReleasesUrl = NetVersions.ReleasesUrl
                };
                await SendSafeAsync(conn, new NetMessage(MsgType.HelloOk, ok)).ConfigureAwait(false);

                Log($"{Describe(player)} вошёл в комнату ({_players.Count}/{NetLimits.MaxPlayers})");

                AddFeed(new FeedEntry
                {
                    Kind = FeedKind.System,
                    AuthorName = "Система",
                    Text = $"{Describe(player)} подключился"
                });

                Events?.PlayerChanged?.Invoke(player);

                // Связка «персонаж ↔ участник боя» при входе игрока: если
                // ведущий уже добавил его в трекер по имени, дописываем id,
                // чтобы карта и лист нашли участника без сверки имён (А).
                // Заодно подхватываем состояния листа, если у участника их нет.
                try { SyncPlayerConditions(player); }
                catch (Exception ex) { Debug.WriteLine("[RoomServer] связка с персонажем: " + ex.Message); }

                await BroadcastSnapshotAsync().ConfigureAwait(false);

                // ── 4.5. Просим у нового игрока его аватар ──
                //
                // В снимке есть только путь к файлу, а самого файла у остальных
                // нет: фотографии передаются отдельно. Без этого запроса чужой
                // аватар не появлялся бы, пока владелец не сменит фото.
                _ = RequestPhotoAsync(conn);

            // Просим и сами данные: аватар — это только картинка, а имя, класс
            // и здоровье могли измениться уже после рукопожатия
            _ = RequestProfileAsync(conn);

                // ── 5. Читаем сообщения до разрыва ──
                await ReadLoopAsync(conn).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] HandleClient: {ex.Message}");
            }
            finally
            {
                await DropClientAsync(conn).ConfigureAwait(false);
            }
        }

        /// <summary>Проверки рукопожатия. Возвращает false и причину при отказе.</summary>
        private bool Validate(HelloRequest hello, string nonce, out string code, out string text)
        {
            code = ""; text = "";

            // Ключ проверяем первым: не тратим ресурсы на чужие подключения
            if (!string.IsNullOrEmpty(_keySecret))
            {
                var expected = HMWT_MUI.Net.RoomKey.ComputeProof(_keySecret, nonce);
                if (!RoomKey.ProofEquals(expected, hello.Proof))
                {
                    code = NetError.BadKey;
                    text = "Неверный ключ комнаты.";
                    return false;
                }
            }

            // Версия приложения
            if (!string.Equals(hello.AppVersion, NetVersions.AppVersion, StringComparison.OrdinalIgnoreCase))
            {
                code = NetError.VersionMismatch;
                text = $"Хост на версии {NetVersions.AppVersion}, у вас {hello.AppVersion}.";
                return false;
            }

            // Версия протокола — отдельно от версии приложения
            if (hello.ProtocolVersion != NetVersions.ProtocolVersion)
            {
                code = NetError.ProtocolMismatch;
                text = $"Несовместимый протокол: у хоста {NetVersions.ProtocolVersion}, у вас {hello.ProtocolVersion}.";
                return false;
            }

            // Идентификатор комнаты: подключение к постоянной комнате
            if (!string.IsNullOrEmpty(hello.RoomId) &&
                !string.Equals(hello.RoomId, _roomId, StringComparison.OrdinalIgnoreCase))
            {
                code = NetError.NoSuchRoom;
                text = $"Это комната «{_roomId}», а вы подключаетесь к «{hello.RoomId}».";
                return false;
            }

            // ── Отпечаток справочников НЕ проверяем ──
            //
            // Раньше расхождение наборов данных блокировало подключение. Это было
            // неверно: справочники у каждого свои, предметы и заклинания по сети
            // не передаются, а в общую ленту уходит только результат броска.
            // Разные базы никак не мешают играть вместе, поэтому здесь только
            // фиксируем расхождение в журнале — для диагностики.
            if (!string.IsNullOrEmpty(_dataFingerprint) && !string.IsNullOrEmpty(hello.DataFingerprint) &&
                !string.Equals(_dataFingerprint, hello.DataFingerprint, StringComparison.Ordinal))
            {
                Log($"справочники различаются (у хоста {_dataFingerprint}, у гостя {hello.DataFingerprint}) — " +
                    "это не мешает игре");
            }

            return true;
        }

        #endregion

        #region === ЧТЕНИЕ СООБЩЕНИЙ КЛИЕНТА ===

        private async Task ReadLoopAsync(ClientConnection conn)
        {
            while (!conn.Cts.IsCancellationRequested)
            {
                var msg = await NetFraming.ReadAsync(conn.Stream, conn.Cts.Token).ConfigureAwait(false);
                if (msg == null) break;               // соединение закрыто
                if (string.IsNullOrEmpty(msg.Type)) continue;

                conn.LastSeenUtc = DateTime.UtcNow;

                switch (msg.Type)
                {
                    case MsgType.Pong:
                        break;

                    case MsgType.Leave:
                        // Игрок попрощался: закрываем соединение сразу, чтобы он
                        // не висел в комнате до таймаута
                        conn.DisconnectReason = "выход игрока";
                        try { conn.Cts.Cancel(); } catch { }
                        break;

                    case MsgType.ChatSend:
                        HandleChat(conn, msg);
                        break;

                    case MsgType.RollSend:
                        HandleRoll(conn, msg);
                        break;

                    case MsgType.PlayerUpdated:
                        // Гость сообщает об изменении своего персонажа: без этого
                        // смена аватара или имени не доходила до остальных
                        HandlePlayerUpdate(conn, msg);
                        break;

                    case MsgType.SnapshotRequest:
                        // Периодическое обновление: игрок просит свежее состояние,
                        // потому что чужое изменение могло не дойти событием
                        _ = SendSnapshotToAsync(conn);
                        break;

                    case MsgType.PhotoChunk:
                        // Фото идёт через хоста: у остальных порт закрыт,
                        // принять напрямую они не могут
                        HandlePhotoChunk(conn, msg);
                        break;

                    case MsgType.MapRequest:
                        _ = SendMapStateToAsync(conn);
                        _ = SendMapSeenSyncToAsync(conn);
                        break;

                    case MsgType.MapSeenAdd:
                        ApplyMapSeenAdd(conn, msg.As<MapSeenAddRequest>());
                        break;

                    case MsgType.MapPing:
                        ApplyMapPing(conn, msg.As<MapPingRequest>());
                        break;

                    case MsgType.ChatImageRequest:
                        HandleChatImageRequest(conn, msg.As<ChatImageRequest>());
                        break;

                    case MsgType.MapMove:
                        ApplyMapMove(conn, msg.As<MapMoveRequest>());
                        break;

                    case MsgType.MapTrail:
                        HandleMapTrail(conn, msg.As<MapTrailMessage>());
                        break;

                    case MsgType.MapShape:
                        ApplyMapShape(conn, msg.As<MapShapeRequest>());
                        break;

                    case MsgType.MapShapeLive:
                        ApplyMapShapeLive(msg.As<MapShapeLive>(), conn?.PlayerId);
                        break;

                    case MsgType.MapImageRequest:
                        _ = SendMapImageToAsync(conn, msg.As<MapImageRequest>());
                        break;

                    case MsgType.MapStroke:
                        ApplyMapStroke(conn, msg.As<MapStrokeRequest>());
                        break;

                    case MsgType.MapStrokeErase:
                        ApplyMapStrokeErase(conn, msg.As<MapStrokeEraseRequest>());
                        break;

                    case MsgType.PhotoRequest:
                        // Новый игрок вошёл и просит чужой аватар: в снимке есть
                        // только путь, а самого файла у него нет
                        _ = SendPhotoToAsync(conn, msg.As<PhotoRequest>());
                        break;

                    default:
                        Debug.WriteLine($"[RoomServer] неизвестный тип: {msg.Type}");
                        break;
                }
            }
        }

        /// <summary>Применяет обновление данных участника, присланное им самим.</summary>
        private void HandlePlayerUpdate(ClientConnection conn, NetMessage msg)
        {
            var incoming = msg.As<PlayerInfo>();
            if (incoming == null || conn?.Player == null) return;

            // Идентификатор и роль берём со стороны хоста: клиент не должен
            // подменять их и выдавать себя за другого участника
            incoming.Id = conn.PlayerId;
            incoming.Role = conn.Player.Role;
            incoming.State = PeerState.Online;

            PlayerInfo updated;
            lock (_stateLock)
            {
                var index = _players.FindIndex(p => p.Id == conn.PlayerId);
                if (index < 0) return;

                // Аватар уже лежит в папке комнаты (пришёл файлом). Локальный
                // путь из карточки клиента не должен затирать его: иначе у всех
                // пропадает картинка, пока файл не пришлют заново.
                string previous = _players[index].AvatarPath ?? "";
                if (InRoomPhotos(previous) && !InRoomPhotos(incoming.AvatarPath))
                    incoming.AvatarPath = previous;

                _players[index] = incoming;
                updated = incoming;
            }

            conn.Player = updated;
            Log($"{Describe(updated)} обновил данные персонажа");

            Events?.PlayerChanged?.Invoke(updated);
            _ = BroadcastSnapshotAsync();
        }

        /// <summary>Сборщики частей фото: по одному на отправителя.</summary>
        private readonly Dictionary<string, PhotoAssembler> _photoAssemblers =
            new Dictionary<string, PhotoAssembler>(StringComparer.Ordinal);

        /// <summary>
        /// Принимает часть фото от игрока.
        ///
        /// У остальных участников порт закрыт, поэтому напрямую они файл принять
        /// не могут. Хост собирает фото целиком, сохраняет у себя и рассылает
        /// остальным — так каждый видит аватары других игроков.
        /// </summary>
        private void HandlePhotoChunk(ClientConnection conn, NetMessage msg)
        {
            var chunk = msg.As<PhotoChunk>();
            if (chunk == null || conn == null) return;

            // Владельца берём со стороны хоста: клиент не должен подменять его
            chunk.OwnerId = conn.PlayerId;

            PhotoAssembler assembler;
            lock (_stateLock)
            {
                if (!_photoAssemblers.TryGetValue(chunk.OwnerId, out assembler))
                {
                    assembler = new PhotoAssembler();
                    _photoAssemblers[chunk.OwnerId] = assembler;
                }
            }

            // Пересылаем часть остальным сразу: не ждём сборки, чтобы файл
            // доходил до всех за то же время
            _ = BroadcastPhotoChunkAsync(chunk, conn.PlayerId);

            byte[] complete;
            lock (assembler)
            {
                complete = assembler.Add(chunk);
            }

            if (complete == null) return;

            // Файл собран: сохраняем, привязываем к игроку и рассылаем состояние
            string ext = string.IsNullOrWhiteSpace(chunk.Extension) ? ".png" : chunk.Extension;
            string relative = PhotoTransferService.SaveReceived(_roomId, chunk.OwnerId, complete, ext);
            if (string.IsNullOrEmpty(relative)) return;

            PlayerInfo updated = null;
            string previousPath = "";
            lock (_stateLock)
            {
                var index = _players.FindIndex(p => p.Id == chunk.OwnerId);
                if (index >= 0)
                {
                    previousPath = _players[index].AvatarPath ?? "";
                    _players[index].AvatarPath = relative;
                    updated = _players[index];
                }
            }

            if (updated != null)
            {
                // Тот же файл уже был — не рассылаем снимок и не дёргаем всех:
                // повторные запросы не должны «мигать» аватаром
                if (string.Equals(previousPath, relative, StringComparison.OrdinalIgnoreCase))
                {
                    LogPhoto($"аватар {chunk.OwnerId} уже был: {relative}");
                    return;
                }

                Log($"{Describe(updated)}: получен аватар ({complete.Length} байт)");
                LogPhoto($"получен аватар {chunk.OwnerId}: {complete.Length} байт → {relative}");
                Events?.PlayerChanged?.Invoke(updated);
                _ = BroadcastSnapshotAsync();
            }
        }

        /// <summary>
        /// Рассылает файл аватара всем участникам.
        ///
        /// Нужно, когда фото сменил сам ведущий: у него нет «отправителя» выше,
        /// и раньше файл оставался только на его машине — игроки видели пустое
        /// место до следующего запроса, а тот делался один раз при входе.
        /// </summary>
        public async Task SendPhotoToAllAsync(string relativePath, string ownerId)
        {
            try
            {
                string full = HMWT_MUI.PhotoHelper.ResolvePhotoPath(relativePath);
                if (string.IsNullOrEmpty(full) || !System.IO.File.Exists(full)) return;

                var parts = PhotoTransferService.Split(full, ownerId, _roomId);
                var targets = _clients.Values.Where(c => c.HandshakeDone).ToList();

                foreach (var part in parts)
                    foreach (var connection in targets)
                        await SendSafeAsync(connection, new NetMessage(MsgType.PhotoChunk, part))
                            .ConfigureAwait(false);

                Debug.WriteLine($"[RoomServer] аватар ведущего разослан: {relativePath} ({targets.Count} участникам)");
                LogPhoto($"разослан аватар ведущего {ownerId}: {targets.Count} участникам");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] рассылка аватара: {ex.Message}");
            }
        }

        /// <summary>
        /// Просит игрока прислать свой аватар.
        ///
        /// Нужно при входе: снимок содержит только путь к файлу, а самого файла
        /// у остальных нет. Игрок отвечает теми же частями, что и обычно.
        /// </summary>
        /// <summary>
        /// Просит игрока прислать свои данные. Ответ придёт обычным
        /// player_updated, и его увидят все — как при смене данных вручную.
        /// </summary>
        private async Task RequestProfileAsync(ClientConnection conn)
        {
            if (conn == null || !conn.HandshakeDone) return;

            try
            {
                await SendSafeAsync(conn, new NetMessage(MsgType.ProfileRequest,
                    new ProfileRequest { owner = conn.PlayerId })).ConfigureAwait(false);
                Log($"запрошены данные игрока {conn.PlayerId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Room] запрос данных: {ex.Message}");
            }
        }

        private async Task RequestPhotoAsync(ClientConnection conn)
        {
            if (conn == null || !conn.HandshakeDone) return;

            try
            {
                string relative = conn.Player?.AvatarPath ?? "";
                if (string.IsNullOrWhiteSpace(relative)) return;

                // Путь в снимке — это путь НА МАШИНЕ ИГРОКА. Проверять его на
                // диске ведущего нельзя: у обоих персонажи могут быть с одним
                // номером, и тогда хост отправлял свою картинку как чужую.
                // Своим считаем только файл, уже лежащий в папке комнаты.
                bool alreadyInRoom = InRoomPhotos(relative);

                if (alreadyInRoom)
                {
                    string full = HMWT_MUI.PhotoHelper.ResolvePhotoPath(relative);
                    if (!string.IsNullOrEmpty(full) && System.IO.File.Exists(full))
                    {
                        var parts = PhotoTransferService.Split(full, conn.PlayerId, _roomId);
                        foreach (var c in _clients.Values.Where(c => c.HandshakeDone && c.PlayerId != conn.PlayerId))
                        {
                            foreach (var part in parts)
                                await SendSafeAsync(c, new NetMessage(MsgType.PhotoChunk, part)).ConfigureAwait(false);
                        }

                        Log($"аватар {conn.PlayerId} разослан остальным: частей {parts.Count}");
                        return;
                    }
                }

                // Файла у ведущего нет — просим прислать
                await SendSafeAsync(conn, new NetMessage(MsgType.PhotoRequest,
                    new PhotoRequest { OwnerId = conn.PlayerId })).ConfigureAwait(false);

                Log($"запрошен аватар у {conn.PlayerId}");
                LogPhoto($"запрошен у игрока {conn.PlayerId} (путь «{relative}»)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] запрос аватара: {ex.Message}");
            }
        }

        /// <summary>
        /// Отправляет одному игроку аватар другого участника.
        ///
        /// Нужно при входе: в снимке комнаты есть только путь к файлу, а самого
        /// файла у нового игрока нет. Без этого чужие аватары не появлялись,
        /// пока их владелец не сменит фото.
        /// </summary>
        private async Task SendPhotoToAsync(ClientConnection conn, PhotoRequest request)
        {
            if (conn == null || request == null || string.IsNullOrWhiteSpace(request.OwnerId)) return;

            try
            {
                PlayerInfo player;
                lock (_stateLock)
                {
                    player = _players.FirstOrDefault(p => p.Id == request.OwnerId);
                }

                string relative = player?.AvatarPath ?? "";
                if (string.IsNullOrWhiteSpace(relative))
                {
                    Log($"аватар {request.OwnerId} запрошен, но его нет");
                    await SendSafeAsync(conn, new NetMessage(MsgType.PhotoMissing, new { owner = request.OwnerId }))
                        .ConfigureAwait(false);
                    return;
                }

                string full = HMWT_MUI.PhotoHelper.ResolvePhotoPath(relative);
                if (string.IsNullOrEmpty(full) || !System.IO.File.Exists(full))
                {
                    Log($"файл аватара {request.OwnerId} не найден: {relative}");
                    await SendSafeAsync(conn, new NetMessage(MsgType.PhotoMissing, new { owner = request.OwnerId }))
                        .ConfigureAwait(false);
                    return;
                }

                var parts = PhotoTransferService.Split(full, request.OwnerId, _roomId);
                if (parts.Count == 0)
                {
                    await SendSafeAsync(conn, new NetMessage(MsgType.PhotoMissing, new { owner = request.OwnerId }))
                        .ConfigureAwait(false);
                    return;
                }

                foreach (var part in parts)
                {
                    await SendSafeAsync(conn, new NetMessage(MsgType.PhotoChunk, part)).ConfigureAwait(false);
                }

                Log($"аватар {request.OwnerId} отправлен по запросу: частей {parts.Count}");
                LogPhoto($"отправлен по запросу {request.OwnerId}: частей {parts.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] отправка аватара по запросу: {ex.Message}");
            }
        }

        /// <summary>Пересылает часть фото всем, кроме отправителя.</summary>
        private async Task BroadcastPhotoChunkAsync(PhotoChunk chunk, string exceptPlayerId)
        {
            var message = new NetMessage(MsgType.PhotoChunk, chunk);

            var targets = _clients.Values
                .Where(c => c.HandshakeDone && c.PlayerId != exceptPlayerId)
                .ToList();

            foreach (var c in targets)
            {
                try { await SendSafeAsync(c, message).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RoomServer] пересылка фото: {ex.Message}");
                }
            }
        }

        private void HandleChat(ClientConnection conn, NetMessage msg)
        {
            var req = msg.As<ChatSendRequest>();
            if (req == null) return;

            bool hasImage = !string.IsNullOrWhiteSpace(req.ImageBase64);
            if (!hasImage && string.IsNullOrWhiteSpace(req.Text)) return;

            string text = (req.Text ?? "").Trim();
            if (text.Length > NetLimits.ChatMaxLength)
                text = text.Substring(0, NetLimits.ChatMaxLength);

            string image = "";
            string extension = "";
            string imageName = "";
            if (hasImage)
            {
                try
                {
                    byte[] bytes = Convert.FromBase64String(req.ImageBase64);
                    if (bytes.Length == 0 || bytes.Length > NetLimits.ChatImageMaxBytes) return;

                    extension = NormalizeImageExtension(req.ImageExtension);
                    if (extension.Length == 0) return;

                    image = req.ImageBase64;
                    imageName = (req.ImageName ?? "").Trim();
                    if (imageName.Length > 120) imageName = imageName.Substring(0, 120);
                }
                catch { return; }
            }

            var entry = new FeedEntry
            {
                Kind = hasImage ? FeedKind.Image : FeedKind.Chat,
                AuthorId = conn.PlayerId,
                AuthorName = Describe(conn.Player),
                Text = text,
                ImageBase64 = image,
                ImageExtension = extension,
                ImageName = imageName,
                ImageId = hasImage ? Guid.NewGuid().ToString("N") : ""
            };
            AddFeed(entry);

            // Рассылаем всем, включая отправителя: так у всех одинаковый порядок
            _ = BroadcastAsync(new NetMessage(MsgType.ChatMessage, entry));
        }

        private static string NormalizeImageExtension(string extension)
        {
            string ext = (extension ?? "").Trim().ToLowerInvariant();
            if (!ext.StartsWith(".")) ext = "." + ext;
            switch (ext)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".webp":
                case ".bmp":
                    return ext;
                default:
                    return "";
            }
        }

        private void HandleChatImageRequest(ClientConnection conn, ChatImageRequest request)
        {
            if (conn == null || request == null || string.IsNullOrWhiteSpace(request.ImageId)) return;
            if ((DateTime.UtcNow - conn.LastChatImageUtc).TotalMilliseconds < 300) return;
            conn.LastChatImageUtc = DateTime.UtcNow;

            FeedEntry entry;
            lock (_stateLock)
            {
                entry = _feed.FirstOrDefault(e => e.Kind == FeedKind.Image &&
                                                  string.Equals(e.ImageId, request.ImageId, StringComparison.Ordinal));
            }

            if (entry == null || string.IsNullOrEmpty(entry.ImageBase64)) return;

            _ = SendSafeAsync(conn, new NetMessage(MsgType.ChatImageResponse, new ChatImageResponse
            {
                ImageId = entry.ImageId,
                ImageBase64 = entry.ImageBase64,
                ImageExtension = entry.ImageExtension
            }));
        }

        private void HandleRoll(ClientConnection conn, NetMessage msg)
        {
            var req = msg.As<RollSendRequest>();
            if (req == null) return;

            // Скрытый бросок — привилегия мастера. Игроки его не получат вообще,
            // поэтому они даже не узнают, что мастер что-то скрытно бросал.
            bool hidden = req.Hidden && conn.Player?.Role == RoomRole.GameMaster;

            var entry = new FeedEntry
            {
                Kind = FeedKind.Roll,
                AuthorId = conn.PlayerId,
                AuthorName = Describe(conn.Player),
                Text = string.IsNullOrWhiteSpace(req.RollType) ? "Бросок" : req.RollType,
                Formula = req.Formula ?? "",
                Total = req.Total,
                IndividualRolls = req.IndividualRolls ?? new List<int>(),
                Hidden = hidden
            };

            if (hidden)
            {
                // В общую ленту не кладём вообще: иначе скрытый бросок уехал бы
                // игрокам следующим снимком комнаты. Отправляем только мастеру.
                _ = SendToAsync(conn.PlayerId, new NetMessage(MsgType.RollMessage, entry));
                Log($"скрытый бросок мастера: {entry.Formula} = {entry.Total}");
                return;
            }

            AddFeed(entry);
            _ = BroadcastAsync(new NetMessage(MsgType.RollMessage, entry));
        }

        #endregion

        #region === РАССЫЛКА ===

        /// <summary>Полный снимок: при 8 участниках это проще и надёжнее диффов.</summary>
        /// <summary>Собирает снимок текущего состояния комнаты.</summary>
        private RoomSnapshot BuildSnapshot()
        {
            // Если ведущий ещё ничего не публиковал, состав боя собираем из
            // базы: иначе у клиента лист персонажа не нашёл бы своего
            // участника, а снимок приходил бы с пустым трекером.
            InitiativeState initiative = CloneInitiative(_initiative);
            if (initiative == null ||
                ((initiative.Roster == null || initiative.Roster.Count == 0) &&
                 (initiative.Combatants == null || initiative.Combatants.Count == 0)))
            {
                initiative = BuildLocalInitiative() ?? initiative ?? new InitiativeState();
            }

            lock (_stateLock)
            {
                return new RoomSnapshot
                {
                    RoomId = _roomId,
                    RoomName = string.IsNullOrWhiteSpace(RoomName) ? _roomId : RoomName,
                    Players = _players.Select(Clone).ToList(),
                    Feed = _feed.Where(e => !e.Hidden).Select(e => new FeedEntry
                    {
                        Kind = e.Kind,
                        At = e.At,
                        AuthorId = e.AuthorId,
                        AuthorName = e.AuthorName,
                        Text = e.Text,
                        Formula = e.Formula,
                        RollType = e.RollType,
                        Total = e.Total,
                        IndividualRolls = e.IndividualRolls,
                        Hidden = e.Hidden,
                        // Тяжёлые картинки в снимок не кладём: они уже ушли
                        // отдельным сообщением; иначе снимок раздувается.
                        ImageBase64 = "",
                        ImageExtension = e.ImageExtension,
                        ImageName = e.ImageName,
                        ImageId = e.ImageId
                    }).ToList(),
                    AppVersion = NetVersions.AppVersion,
                    ProtocolVersion = NetVersions.ProtocolVersion,
                    DataFingerprint = _dataFingerprint,
                    Initiative = initiative
                };
            }
        }

        /// <summary>
        /// Состав боя из локальной базы ведущего — на случай, когда он ещё не
        /// открывал трекер и ничего не публиковал. Клиенту нужны character_id
        /// и состояния участников (у него своей базы боёв нет).
        ///
        /// Результат кэшируется на пару секунд: снимок собирается часто, а
        /// чтение всех групп боя каждый раз — лишняя работа с базой.
        /// </summary>
        private static InitiativeState _localInitiativeCache;
        private static DateTime _localInitiativeAt;

        private static InitiativeState BuildLocalInitiative()
        {
            if (_localInitiativeCache != null &&
                (DateTime.UtcNow - _localInitiativeAt).TotalSeconds < 3)
                return _localInitiativeCache;

            try
            {
                var encounter = InitiativeService.ActiveEncounter() ??
                                InitiativeService.LoadEncounters().FirstOrDefault();
                _localInitiativeCache = encounter == null
                    ? new InitiativeState()
                    : InitiativeService.BuildState(encounter);
                _localInitiativeAt = DateTime.UtcNow;
                return _localInitiativeCache;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] состав боя из базы: {ex.Message}");
                return null;
            }
        }

        // ══════════════════════ КАРТА БОЯ ══════════════════════

        /// <summary>Ведущий обновил карту — раскладываем её всем игрокам.</summary>
        public void SetMap(MapSceneData scene)
        {
            lock (_stateLock)
            {
                _map = CloneMap(scene);
            }
            _ = BroadcastMapStateAsync();
        }

        /// <summary>Текущая карта (копия: наружу отдаём снимок, а не рабочий объект).</summary>
        public MapSceneData GetMap()
        {
            lock (_stateLock)
            {
                return CloneMap(_map);
            }
        }

        private static MapSceneData CloneMap(MapSceneData source)
            => source == null ? null : NetJson.FromElement<MapSceneData>(NetJson.ToElement(source));

        private async Task BroadcastMapStateAsync()
        {
            var scene = GetMap();
            if (scene == null) return;

            var message = new NetMessage(MsgType.MapState, scene);
            foreach (var c in _clients.Values.Where(x => x.HandshakeDone).ToList())
            {
                try { await SendSafeAsync(c, message).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[RoomServer] карта не ушла: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Ведущий начал разведку заново: у каждого игрока «разведанное»
        /// хранится локально, поэтому его нужно попросить очистить.
        /// </summary>
        public void BroadcastSeenReset()
        {
            var message = new NetMessage(MsgType.MapSeenReset, new { });
            foreach (var c in _clients.Values.Where(x => x.HandshakeDone).ToList())
                _ = SendSafeAsync(c, message);
        }

        /// <summary>
        /// Разведанное общее: ведущий нашёл новый сектор — отправляем игрокам.
        /// В режиме «свой туман» разведанное остаётся личным и не рассылается.
        /// </summary>
        public void BroadcastMapSeen(string points)
        {
            if (!SeenIsShared() || string.IsNullOrWhiteSpace(points)) return;

            var message = new NetMessage(MsgType.MapSeenAdd, new MapSeenAddRequest { Points = points });
            foreach (var c in _clients.Values.Where(x => x.HandshakeDone).ToList())
                _ = SendSafeAsync(c, message);
        }

        /// <summary>Ведущий пингнул карту — отправляем всем игрокам.</summary>
        public void BroadcastPing(double x, double y)
        {
            var message = new NetMessage(MsgType.MapPing, new MapPingRequest { X = x, Y = y });
            foreach (var c in _clients.Values.Where(x => x.HandshakeDone).ToList())
                _ = SendSafeAsync(c, message);
        }

        private void ApplyMapPing(ClientConnection conn, MapPingRequest request)
        {
            if (request == null || conn == null) return;

            // Не даём залить канал десятками пингов в секунду
            if ((DateTime.UtcNow - conn.LastPingUtc).TotalMilliseconds < 250) return;
            conn.LastPingUtc = DateTime.UtcNow;

            MapSceneData scene;
            lock (_stateLock) scene = _map;
            if (scene == null) return;

            double x = Math.Max(0, Math.Min(scene.WidthPx, request.X));
            double y = Math.Max(0, Math.Min(scene.HeightPx, request.Y));
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return;

            Pinged?.Invoke(x, y);

            var message = new NetMessage(MsgType.MapPing, new MapPingRequest { X = x, Y = y });
            foreach (var c in _clients.Values.Where(x2 => x2.HandshakeDone &&
                         !string.Equals(x2.PlayerId, conn.PlayerId, StringComparison.Ordinal)).ToList())
            {
                _ = SendSafeAsync(c, message);
            }
        }

        private bool SeenIsShared()
        {
            lock (_stateLock)
                return _map != null &&
                       !string.Equals(_map.FogMode, "own", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyMapSeenAdd(ClientConnection conn, MapSeenAddRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Points)) return;
            if (!SeenIsShared()) return;

            string points = request.Points.Trim();
            if (points.Length < 10 || points.Length > 40000) return;

            MapSceneData scene;
            lock (_stateLock) scene = _map;
            if (scene == null) return;

            // Ведущий — хранитель общего разведанного
            MapService.AddSeen(scene.Id, points);
            MapSeenAdded?.Invoke(points);

            var message = new NetMessage(MsgType.MapSeenAdd, new MapSeenAddRequest { Points = points });
            foreach (var c in _clients.Values.Where(x => x.HandshakeDone &&
                         !string.Equals(x.PlayerId, conn.PlayerId, StringComparison.Ordinal)).ToList())
            {
                _ = SendSafeAsync(c, message);
            }

            LogRoomLine($"разведано от {conn.Player?.CharacterName}: сектор {points.Length} байт");
        }

        /// <summary>Новому игроку отправляем всё, что отряд уже разведал.</summary>
        private async Task SendMapSeenSyncToAsync(ClientConnection conn)
        {
            if (conn == null || !SeenIsShared()) return;

            MapSceneData scene;
            lock (_stateLock) scene = _map;
            if (scene == null) return;

            var seen = MapService.LoadSeen(scene.Id);
            if (seen.Count > 240) seen = seen.Skip(seen.Count - 240).ToList();

            try
            {
                await SendSafeAsync(conn, new NetMessage(MsgType.MapSeenSync, new MapSeenSync { Seen = seen }))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) { Debug.WriteLine($"[RoomServer] разведанное не ушло: {ex.Message}"); }
        }

        private async Task SendMapStateToAsync(ClientConnection conn)
        {
            var scene = GetMap();
            if (scene == null || conn == null) return;
            try { await SendSafeAsync(conn, new NetMessage(MsgType.MapState, scene)).ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine($"[RoomServer] карта игроку не ушла: {ex.Message}"); }
        }

        /// <summary>Путь указывает на файл, уже сохранённый в папке комнаты.</summary>
        private static bool InRoomPhotos(string relativePath)
            => !string.IsNullOrWhiteSpace(relativePath) &&
               relativePath.Replace('\\', '/').StartsWith("Rooms/", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Игрок двигает жетон: принимаем только своего персонажа. Проверка на
        /// стороне хоста — клиенту нельзя доверять выбор чужого жетона.
        /// </summary>
        private void ApplyMapMove(ClientConnection conn, MapMoveRequest request)
        {
            if (request == null || conn?.Player == null) return;

            bool applied = false;
            MapTokenData changedToken = null;
            double fromX = 0, fromY = 0;
            string rejectLabel = "", rejectCharacter = "", rejectNickname = "";

            lock (_stateLock)
            {
                if (_map?.Tokens != null)
                {
                    var token = _map.Tokens.FirstOrDefault(t => t.Id == request.TokenId);
                    if (token != null)
                    {
                        // чей это жетон: имя участника боя совпадает с именем игрока
                        // Имя персонажа у игрока может быть пустым (тогда в отряде
                        // виден ник), поэтому сверяем с обоими: иначе ход игрока
                        // молча отбрасывался и ведущий его не видел.
                        string character = (conn.Player.CharacterName ?? "").Trim();
                        string nickname = (conn.Player.Name ?? "").Trim();
                        string label = (token.Label ?? "").Trim();

                        // Главная проверка — по character_id (протокол 16):
                        // владелец жетона записан ведущим при публикации карты.
                        // Имя остаётся запасным путём для старых сцен.
                        bool mine = (token.OwnerCharacterId > 0 &&
                                     token.OwnerCharacterId == conn.Player.CharacterId) ||
                                    NameMatch.SamePerson(label, character) ||
                                    NameMatch.SamePerson(label, nickname);
                        if (mine)
                        {
                            fromX = token.X;
                            fromY = token.Y;
                            token.X = request.X;
                            token.Y = request.Y;
                            if (request.Facing >= 0) token.Facing = request.Facing;

                            // Свой обзор и размер жетона игрок тоже может менять:
                            // раньше такие правки оставались только у него на экране
                            if (request.ViewAngle >= 0 && request.ViewAngle <= 360)
                                token.ViewAngle = request.ViewAngle;
                            if (request.SizeCells >= 1 && request.SizeCells <= 10)
                                token.SizeCells = request.SizeCells;
                            if (request.RevealCells >= 0 && request.RevealCells <= 40)
                                token.RevealCells = request.RevealCells;
                            // свой цвет сектора обзора (протокол 14)
                            if (request.ViewColor != null)
                                token.ViewColor = request.ViewColor.Trim();

                            // состояния ключами (протокол 15): жетон и участник боя
                            if (request.Conditions != null)
                            {
                                token.Conditions = request.Conditions.Trim();
                            }

                            changedToken = token;
                            applied = true;
                        }
                        else
                        {
                            rejectLabel = label;
                            rejectCharacter = character;
                            rejectNickname = nickname;
                        }
                    }
                    else
                    {
                        LogRoomLine($"ход: жетон {request.TokenId} не найден у ведущего");
                    }
                }
            }

            if (changedToken != null && !request.Live)
            {
                // Итоговый ход сохраняем: промежуточные (live) не пишем, иначе
                // перетаскивание било бы по базе десятки раз в секунду
                try { MapService.SaveToken(changedToken); } catch { }

                // Состояния, присланные игроком из меню жетона, — это те же
                // состояния участника боя: пишем в трекер и рассылаем всем,
                // чтобы лист персонажа у клиента увидел их сразу.
                if (request.Conditions != null && changedToken.CombatantId > 0)
                {
                    var encounter = InitiativeService.UpdateCombatantConditions(
                        changedToken.CombatantId, changedToken.Conditions);
                    if (encounter != null)
                        SetInitiative(InitiativeService.BuildState(encounter));
                }
            }

            if (!applied)
            {
                // Отказ пишем всегда: без этого «двигал, а у ведущего ничего»
                // остаётся гадать, чей это жетон на самом деле
                LogRoomLine($"ход жетона {request.TokenId} отклонён: жетон «{rejectLabel}», " +
                            $"игрок «{rejectCharacter}»/«{rejectNickname}»");
            }

            try
            {
                if (Environment.GetEnvironmentVariable("HMWT_MAP_LOG") == "1")
                {
                    string dir = HMWT_MUI.PhotoHelper.GetWritableRoot();
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "map-log.txt"),
                        $"{DateTime.Now:HH:mm:ss.fff} ход жетона {request.TokenId} → ({request.X:0},{request.Y:0}), " +
                        $"игрок «{conn.Player?.CharacterName}»/«{conn.Player?.Name}», принят={applied}\r\n");
                }
            }
            catch { }

            if (!applied) return;

            // Скрипты тёмных зон: проверяем вход жетона (только переход
            // снаружи внутрь), иначе спасбросок бросался бы каждые 50 мс.
            try { CheckZoneScripts(changedToken, fromX, fromY); } catch { }

            if (request.Live)
            {
                // Промежуточный шаг: лёгкое сообщение всем, кроме отправителя,
                // и событие ведущему. Полную карту шлём только в конце.
                _ = BroadcastTokenLiveAsync(changedToken.Id, changedToken.X, changedToken.Y,
                    changedToken.Facing, conn.PlayerId);
                TokenMovedLive?.Invoke(changedToken.Id, changedToken.X, changedToken.Y, changedToken.Facing);
                return;
            }

            _ = BroadcastMapStateAsync();
            MapChanged?.Invoke();
        }

        /// <summary>
        /// Промежуточное движение ведущего: двигаем жетон и рассылаем всем,
        /// но не пишем в базу — итог сохранится при отпускании.
        /// </summary>
        public void UpdateTokenLive(int tokenId, double x, double y, double facing)
        {
            bool changed = false;
            MapTokenData moved = null;
            double fromX = 0, fromY = 0;

            lock (_stateLock)
            {
                var token = _map?.Tokens?.FirstOrDefault(t => t.Id == tokenId);
                if (token != null)
                {
                    fromX = token.X;
                    fromY = token.Y;
                    token.X = x;
                    token.Y = y;
                    if (facing >= 0) token.Facing = facing;
                    moved = token;
                    changed = true;
                }
            }

            if (!changed) return;

            try { CheckZoneScripts(moved, fromX, fromY); } catch { }

            _ = BroadcastTokenLiveAsync(tokenId, x, y, facing, null);
        }

        private void HandleMapTrail(ClientConnection conn, MapTrailMessage message)
        {
            if (conn == null || message == null) return;
            var points = (message.Points ?? new List<MapTrailPoint>())
                .Where(p => p != null)
                .Take(2000)
                .ToList();

            // Пустой список — «след стёрт»: игрок начал новый маршрут.
            MapTrailAdded?.Invoke(message.TokenId, points);
            _ = BroadcastMapTrailAsync(message.TokenId, points, conn.PlayerId);
        }

        /// <summary>Игрок создал/сдвинул/перекрасил/удалил фигуру.</summary>
        private void ApplyMapShape(ClientConnection conn, MapShapeRequest request)
        {
            if (conn == null || request == null || request.Shape == null) return;

            string action = (request.Action ?? "").ToLowerInvariant();
            bool changed = false;

            lock (_stateLock)
            {
                var scene = _map;
                if (scene == null) return;

                if (action == "create")
                {
                    var incoming = request.Shape;
                    var shape = new MapShapeData
                    {
                        SceneId = scene.Id,
                        Kind = string.IsNullOrWhiteSpace(incoming.Kind) ? "circle" : incoming.Kind,
                        X = incoming.X,
                        Y = incoming.Y,
                        RadiusCells = Math.Max(0.5, Math.Min(20, incoming.RadiusCells)),
                        Angle = incoming.Angle,
                        Color = string.IsNullOrWhiteSpace(incoming.Color) ? "#FFB98BFF" : incoming.Color,
                        Label = incoming.Label ?? ""
                    };

                    if (MapService.SaveShape(shape) > 0)
                    {
                        scene.Shapes.Add(shape);
                        changed = true;
                    }
                }
                else if (action == "delete")
                {
                    var existing = scene.Shapes.FirstOrDefault(s => s.Id == request.Shape.Id);
                    if (existing != null)
                    {
                        MapService.DeleteShape(existing.Id);
                        scene.Shapes.Remove(existing);
                        changed = true;
                    }
                }
                else
                {
                    var existing = scene.Shapes.FirstOrDefault(s => s.Id == request.Shape.Id);
                    if (existing != null)
                    {
                        existing.X = request.Shape.X;
                        existing.Y = request.Shape.Y;
                        existing.RadiusCells = Math.Max(0.5, Math.Min(20, request.Shape.RadiusCells));
                        existing.Angle = request.Shape.Angle;
                        existing.Color = string.IsNullOrWhiteSpace(request.Shape.Color)
                            ? existing.Color : request.Shape.Color;
                        existing.Label = request.Shape.Label ?? existing.Label;
                        MapService.SaveShape(existing);
                        changed = true;
                    }
                }
            }

            if (!changed) return;
            _ = BroadcastMapStateAsync();
            MapChanged?.Invoke();
        }

        /// <summary>
        /// Фигуру ведут мышью: обновляем координаты у хоста и рассылаем только
        /// их. Полная карта на каждый шаг заставляла фигуру прыгать у всех.
        /// </summary>
        private void ApplyMapShapeLive(MapShapeLive request, string exceptPlayerId)
        {
            if (request == null || request.ShapeId <= 0) return;

            bool changed = false;
            lock (_stateLock)
            {
                var shape = _map?.Shapes?.FirstOrDefault(s => s.Id == request.ShapeId);
                if (shape != null && (shape.X != request.X || shape.Y != request.Y))
                {
                    shape.X = request.X;
                    shape.Y = request.Y;
                    changed = true;
                }
            }
            if (!changed) return;

            var message = new NetMessage(MsgType.MapShapeLive, request);
            foreach (var c in _clients.Values.Where(c => c.HandshakeDone &&
                         !string.Equals(c.PlayerId, exceptPlayerId, StringComparison.Ordinal)).ToList())
            {
                _ = SendSafeAsync(c, message);
            }

            ShapeMovedLive?.Invoke(request.ShapeId, request.X, request.Y);
        }

        /// <summary>Ведущий ведёт фигуру: обновляем копию и рассылаем игрокам.</summary>
        public void BroadcastShapeLive(int shapeId, double x, double y)
        {
            ApplyMapShapeLive(new MapShapeLive { ShapeId = shapeId, X = x, Y = y }, null);
        }

        /// <summary>Ведущий прошёл жетоном — рассылаем путь остальным.</summary>
        public void BroadcastMapTrail(int tokenId, List<MapTrailPoint> points)
        {
            if (points == null) return;
            _ = BroadcastMapTrailAsync(tokenId, points, null);
        }

        private async Task BroadcastMapTrailAsync(int tokenId, List<MapTrailPoint> points,
            string exceptPlayerId)
        {
            var message = new NetMessage(MsgType.MapTrail, new MapTrailMessage
            {
                TokenId = tokenId,
                Points = points
            });

            foreach (var c in _clients.Values.Where(c => c.HandshakeDone &&
                         !string.Equals(c.PlayerId, exceptPlayerId, StringComparison.Ordinal)).ToList())
            {
                try { await SendSafeAsync(c, message).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[RoomServer] trail: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Лёгкое обновление положения жетона: без полной карты. Полная карта
        /// на каждом шаге перетаскивания перегружала и канал, и интерфейс.
        /// </summary>
        private async Task BroadcastTokenLiveAsync(int tokenId, double x, double y,
            double facing, string exceptPlayerId)
        {
            var message = new NetMessage(MsgType.MapTokenLive, new MapTokenLive
            {
                TokenId = tokenId,
                X = x,
                Y = y,
                Facing = facing
            });

            foreach (var c in _clients.Values.Where(c => c.HandshakeDone &&
                         !string.Equals(c.PlayerId, exceptPlayerId, StringComparison.Ordinal)).ToList())
            {
                try { await SendSafeAsync(c, message).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[RoomServer] live-ход: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Игрок нарисовал штрих. Проверяем размер и координаты, сохраняем
        /// у ведущего и рассылаем карту всем — рисунки общие для отряда.
        /// </summary>
        private void ApplyMapStroke(ClientConnection conn, MapStrokeRequest request)
        {
            if (request == null) return;

            string points = request.Points ?? "";
            if (points.Length < 5 || points.Length > 40000)
            {
                LogRoomLine($"рисунок отклонён: длина данных {points.Length}");
                return;
            }

            int count = points.Count(c => c == ';') + 1;
            if (count > 4000) return;

            double thickness = Math.Max(1, Math.Min(40, request.Thickness));
            string color = string.IsNullOrWhiteSpace(request.Color) ? "#FFE06A63" : request.Color.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6,8}$"))
                color = "#FFE06A63";

            MapSceneData scene;
            lock (_stateLock) scene = _map;
            if (scene == null) return;

            var stroke = new MapStrokeData
            {
                SceneId = scene.Id,
                Color = color,
                Thickness = thickness,
                Points = points
            };

            int id = MapService.SaveStroke(stroke);
            if (id <= 0) return;
            stroke.Id = id;

            lock (_stateLock) { _map?.Strokes.Add(stroke); }

            _ = BroadcastMapStateAsync();

            // Ведущий тоже должен увидеть чужой рисунок: без этого его сцена
            // оставалась без штриха, и следующая публикация карты стирала его
            // у всех игроков
            MapChanged?.Invoke();
            LogRoomLine($"рисунок от {conn?.Player?.CharacterName}: штрих {id}, точек {count}");
        }

        /// <summary>Игрок стёр штрихи ластиком — удаляем у ведущего и у всех.</summary>
        private void ApplyMapStrokeErase(ClientConnection conn, MapStrokeEraseRequest request)
        {
            if (request?.Ids == null || request.Ids.Count == 0) return;

            var ids = request.Ids.Where(i => i > 0).Distinct().Take(500).ToList();
            if (ids.Count == 0) return;

            var removed = new List<int>();
            lock (_stateLock)
            {
                if (_map?.Strokes == null) return;

                foreach (int id in ids)
                {
                    var stroke = _map.Strokes.FirstOrDefault(s => s.Id == id);
                    if (stroke == null) continue;
                    _map.Strokes.Remove(stroke);
                    removed.Add(id);
                }
            }

            foreach (int id in removed)
            {
                try { MapService.DeleteStroke(id); } catch { }
            }

            if (removed.Count == 0) return;

            _ = BroadcastMapStateAsync();
            MapChanged?.Invoke();
            LogRoomLine($"стёрты штрихи: {string.Join(",", removed)} (игрок {conn?.Player?.CharacterName})");
        }

        /// <summary>
        /// Отправляет картинку карты частями: gzip + base64, куски по 128 КБ.
        /// Игрок соберёт файл у себя и больше не качает.
        /// </summary>
        private async Task SendMapImageToAsync(ClientConnection conn, MapImageRequest request)
        {
            if (conn == null) return;

            string path;
            string hash;
            string wanted = request?.Hash ?? "";
            lock (_stateLock)
            {
                path = _map == null ? null : MapService.ResolveImagePath(_map);
                hash = _map?.ImageHash ?? "";

                // фото жетона запрашивается по своему хэшу: ищем владельца
                if (_map?.Tokens != null &&
                    !string.Equals(wanted, hash, StringComparison.OrdinalIgnoreCase))
                {
                    var owner = _map.Tokens.FirstOrDefault(x =>
                        !string.IsNullOrWhiteSpace(x.PhotoHash) &&
                        string.Equals(x.PhotoHash, wanted, StringComparison.OrdinalIgnoreCase));
                    if (owner != null && !string.IsNullOrWhiteSpace(owner.PhotoPath))
                    {
                        path = owner.PhotoPath;
                        hash = owner.PhotoHash;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(hash)) return;
            if (!string.IsNullOrWhiteSpace(wanted) &&
                !string.Equals(wanted, hash, StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                byte[] raw = File.ReadAllBytes(path);
                byte[] packed = Gzip(raw);
                const int chunkSize = 128 * 1024;
                int total = Math.Max(1, (packed.Length + chunkSize - 1) / chunkSize);

                for (int i = 0; i < total; i++)
                {
                    int offset = i * chunkSize;
                    int length = Math.Min(chunkSize, packed.Length - offset);
                    var chunk = new MapImageChunk
                    {
                        Hash = hash,
                        Extension = Path.GetExtension(path),
                        Index = i,
                        Total = total,
                        Data = Convert.ToBase64String(packed, offset, length)
                    };
                    await SendSafeAsync(conn, new NetMessage(MsgType.MapImageChunk, chunk)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] картинка карты не ушла: {ex.Message}");
            }
        }

        private static byte[] Gzip(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(
                output, System.IO.Compression.CompressionLevel.Fastest, true))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        /// <summary>Ведущий обновил трекер инициативы — раскладываем его игрокам.</summary>
        public void SetInitiative(InitiativeState state)
        {
            lock (_stateLock)
            {
                _initiative = CloneInitiative(state) ?? new InitiativeState();
            }
            _ = BroadcastSnapshotAsync();
        }

        /// <summary>Текущее состояние трекера (копия: наружу отдаём только снимок).</summary>
        public InitiativeState GetInitiative()
        {
            lock (_stateLock)
            {
                return CloneInitiative(_initiative) ?? new InitiativeState();
            }
        }

        /// <summary>
        /// Копия состояния инициативы. Список жетонов нельзя отдавать наружу
        /// по ссылке: пока снимок сериализуется, ведущий может его изменить.
        /// </summary>
        private static InitiativeState CloneInitiative(InitiativeState source)
        {
            if (source == null) return null;

            var copy = new InitiativeState
            {
                IsActive = source.IsActive,
                Name = source.Name ?? "",
                Round = source.Round,
                Combatants = new List<InitiativeCombatantDto>(),
                Roster = new List<InitiativeCombatantDto>()
            };

            if (source.Combatants != null)
            {
                foreach (var combatant in source.Combatants)
                {
                    if (combatant == null) continue;
                    copy.Combatants.Add(CloneCombatant(combatant));
                }
            }

            // Состав боя — отдельный список: по нему клиент находит своего
            // участника для листа персонажа. Без копирования он терялся бы
            // прямо на сервере при каждом SetInitiative.
            if (source.Roster != null)
            {
                foreach (var member in source.Roster)
                {
                    if (member == null) continue;
                    copy.Roster.Add(CloneCombatant(member));
                }
            }

            return copy;
        }

        private static InitiativeCombatantDto CloneCombatant(InitiativeCombatantDto source)
        {
            return new InitiativeCombatantDto
            {
                Id = source.Id,
                Name = source.Name ?? "",
                Roll = source.Roll,
                Modifier = source.Modifier,
                Total = source.Total,
                IsPlayer = source.IsPlayer,
                IsCurrent = source.IsCurrent,
                CharacterId = source.CharacterId,
                Conditions = source.Conditions ?? "",
                HitPoints = source.HitPoints,
                MaxHitPoints = source.MaxHitPoints
            };
        }

        /// <summary>
        /// Отправляет снимок одному участнику.
        ///
        /// Нужно для периодического обновления: игрок сам просит свежее состояние,
        /// если чужое изменение не дошло отдельным событием.
        /// </summary>
        private async Task SendSnapshotToAsync(ClientConnection conn)
        {
            if (conn == null || !conn.HandshakeDone) return;

            try
            {
                await SendSafeAsync(conn, new NetMessage(MsgType.Snapshot, BuildSnapshot()))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] снимок по запросу: {ex.Message}");
            }
        }

        public async Task BroadcastSnapshotAsync()
        {
            var snapshot = BuildSnapshot();
            await BroadcastAsync(new NetMessage(MsgType.Snapshot, snapshot)).ConfigureAwait(false);
        }

        private async Task BroadcastAsync(NetMessage message)
        {
            var targets = _clients.Values.Where(c => c.HandshakeDone).ToList();
            foreach (var c in targets)
            {
                try
                {
                    await SendSafeAsync(c, message).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RoomServer] рассылка не удалась: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Рассылает системную запись всем участникам.
        ///
        /// Нужна отдельно от общей рассылки: уведомление об уходе должно уйти
        /// ровно один раз и уже после того, как ушедший убран из списка, иначе
        /// он получил бы сообщение о собственном выходе.
        /// </summary>
        private async Task BroadcastSystemNoticeAsync(FeedEntry entry)
        {
            foreach (var c in _clients.Values.Where(c => c.HandshakeDone).ToList())
            {
                try
                {
                    await SendSafeAsync(c, new NetMessage(MsgType.SystemNotice, entry)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RoomServer] системная запись не ушла: {ex.Message}");
                }
            }
        }

        private async Task SendToAsync(string playerId, NetMessage message)
        {
            if (!_clients.TryGetValue(playerId, out var c)) return;
            try
            {
                await NetFraming.WriteAsync(c.Stream, message, c.Cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] отправка не удалась: {ex.Message}");
            }
        }

        /// <summary>
        /// Единственный путь записи в сокет клиента: под замком.
        /// Иначе рассылка снимка и ответ на рукопожатие пишут одновременно,
        /// и поток превращается в кашу.
        /// </summary>
        private static async Task SendSafeAsync(ClientConnection conn, NetMessage message)
        {
            if (conn?.Stream == null) return;
            try
            {
                await conn.WriteLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await NetFraming.WriteAsync(conn.Stream, message, conn.Cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    conn.WriteLock.Release();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] запись не удалась: {ex.Message}");
            }
        }

        private async Task SendErrorAsync(ClientConnection conn, string code, string text)
        {
            try
            {
                var payload = new HelloResponse
                {
                    Ok = false,
                    ErrorCode = code,
                    ErrorText = text,
                    RequiredAppVersion = NetVersions.AppVersion,
                    RequiredProtocolVersion = NetVersions.ProtocolVersion,
                    ReleasesUrl = NetVersions.ReleasesUrl
                };
                await SendSafeAsync(conn, new NetMessage(MsgType.Error, payload)).ConfigureAwait(false);
            }
            catch { }
        }

        #endregion

        #region === ОТКЛЮЧЕНИЯ И HEARTBEAT ===

        /// <summary>
        /// Каждые несколько секунд проверяем, кто молчит. Так отвалившиеся
        /// не остаются «призраками» в списке участников.
        /// </summary>
        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }

                var ping = new NetMessage(MsgType.Ping, new { at = DateTime.UtcNow });
                foreach (var c in _clients.Values.Where(c => c.HandshakeDone).ToList())
                {
                    if (DateTime.UtcNow - c.LastSeenUtc > PeerTimeout)
                    {
                        Log($"{Describe(c.Player)} не отвечает — отключаю");
                        c.DisconnectReason = "таймаут";
                        try { c.Cts.Cancel(); } catch { }
                        continue;
                    }
                    try
                    {
                        await SendSafeAsync(c, ping).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }

        private async Task DropClientAsync(ClientConnection conn)
        {
            if (conn == null) return;

            try { conn.Cts.Cancel(); } catch { }
            try { conn.Tcp?.Close(); } catch { }

            // Если рукопожатие не завершилось, участника в списке нет — молча выходим

            if (!conn.HandshakeDone) return;
            if (!_clients.TryRemove(conn.PlayerId, out _)) return;

            PlayerInfo removed;
            lock (_stateLock)
            {
                removed = _players.FirstOrDefault(p => p.Id == conn.PlayerId);
                if (removed != null) _players.Remove(removed);
            }

            if (removed != null)
            {
                string reason = string.IsNullOrEmpty(conn.DisconnectReason)
                    ? "соединение закрыто"
                    : conn.DisconnectReason;

                Log($"{Describe(removed)} покинул комнату ({_players.Count}/{NetLimits.MaxPlayers}), причина: {reason}");

                // Текст один и для ленты ведущего, и для уведомления гостям
                string text = $"{Describe(removed)} отключился ({reason})";

                AddFeed(new FeedEntry
                {
                    Kind = FeedKind.System,
                    AuthorName = "Система",
                    Text = text
                });

                // Гостю снимок приходит уже БЕЗ ушедшего, поэтому об отключении он
                // узнаёт только этим сообщением. Тип сообщения в протоколе уже есть,
                // версию протокола менять не нужно.
                await BroadcastSystemNoticeAsync(new FeedEntry
                {
                    Kind = FeedKind.System,
                    AuthorName = "Система",
                    Text = text
                }).ConfigureAwait(false);

                Events?.SystemNotice?.Invoke(text, removed.Id);
                Events?.PeerLeft?.Invoke(removed, reason);
                Events?.PlayerChanged?.Invoke(removed);

                // Мастер ушёл — роль переходит следующему, иначе скрытые броски
                // и инструменты ведущего остаются недоступны всем
                if (removed.Role == RoomRole.GameMaster)
                {
                    PlayerInfo promoted;
                    lock (_stateLock) promoted = _players.FirstOrDefault();
                    if (promoted != null)
                    {
                        promoted.Role = RoomRole.GameMaster;
                        AddFeed(new FeedEntry
                        {
                            Kind = FeedKind.System,
                            AuthorName = "Система",
                            Text = $"{Describe(promoted)} теперь ведущий"
                        });
                    }
                }
            }

            await BroadcastSnapshotAsync().ConfigureAwait(false);
        }

        #endregion

        #region === ЛЕНТА И УЧАСТНИКИ ===

        /// <summary>Добавляет запись в общую ленту и в историю комнаты на диске.</summary>
        /// <summary>Рассылает запись чата всем, включая автора (картинки идут так).</summary>
        public void BroadcastChat(FeedEntry entry)
        {
            if (entry == null) return;
            _ = BroadcastAsync(new NetMessage(MsgType.ChatMessage, entry));
        }

        public FeedEntry AddFeed(FeedEntry entry)
        {
            if (entry == null) return null;

            lock (_stateLock)
            {
                _feed.Add(entry);
                if (_feed.Count > NetLimits.MaxRollHistory + NetLimits.MaxChatHistory)
                    _feed.RemoveRange(0, _feed.Count - (NetLimits.MaxRollHistory + NetLimits.MaxChatHistory));
            }

            RoomStore.AppendFeed(_roomId, entry);
            Events?.FeedAdded?.Invoke(entry);
            return entry;
        }

        /// <summary>Восстанавливает ленту прошлой сессии при открытии постоянной комнаты.</summary>
        public void RestoreFeedFromDisk(int maxEntries = 200)
        {
            try
            {
                var history = RoomStore.LoadFeed(_roomId, maxEntries);
                lock (_stateLock)
                {
                    _feed.Clear();
                    _feed.AddRange(history);
                }
                Log($"восстановлено записей из истории: {history.Count}");
            }
            catch (Exception ex) { Log("не удалось восстановить историю: " + ex.Message); }
        }

        public List<PlayerInfo> GetPlayers()
        {
            lock (_stateLock)
            {
                return _players.Select(Clone).ToList();
            }
        }

        public List<FeedEntry> GetFeed(int max = 200)
        {
            lock (_stateLock)
            {
                int take = Math.Min(max, _feed.Count);
                return _feed.Skip(_feed.Count - take).ToList();
            }
        }

        public bool IsGameMaster(string playerId)
        {
            lock (_stateLock)
                return _players.FirstOrDefault(p => p.Id == playerId)?.Role == RoomRole.GameMaster;
        }

        /// <summary>Передаёт роль мастера другому участнику.</summary>
        public bool TransferGameMaster(string toPlayerId)
        {
            bool changed = false;
            lock (_stateLock)
            {
                foreach (var p in _players)
                {
                    var role = p.Id == toPlayerId ? RoomRole.GameMaster : RoomRole.Player;
                    if (p.Role != role) { p.Role = role; changed = true; }
                }
            }
            if (changed)
            {
                var newGm = _players.FirstOrDefault(p => p.Id == toPlayerId);
                AddFeed(new FeedEntry
                {
                    Kind = FeedKind.System,
                    AuthorName = "Система",
                    Text = $"{Describe(newGm)} теперь ведущий"
                });
                _ = BroadcastSnapshotAsync();
            }
            return changed;
        }

        #endregion

        #region === ВСПОМОГАТЕЛЬНОЕ ===

        /// <summary>Обновляет данные участника (например, изменилось ХП на листе).</summary>
        public void UpdatePlayer(string playerId, PlayerInfo data)
        {
            if (data == null) return;
            bool found = false;
            string previousConditions = null;
            lock (_stateLock)
            {
                var existing = _players.FirstOrDefault(p => p.Id == playerId);
                if (existing != null)
                {
                    data.Id = existing.Id;
                    data.Role = existing.Role;
                    data.State = existing.State;
                    previousConditions = existing.Conditions;

                    // Уже принятый файл аватара важнее локального пути из
                    // карточки: иначе каждое сохранение профиля сбрасывало
                    // картинку у всех остальных
                    if (InRoomPhotos(existing.AvatarPath) && !InRoomPhotos(data.AvatarPath))
                        data.AvatarPath = existing.AvatarPath;

                    int idx = _players.IndexOf(existing);
                    _players[idx] = data;
                    found = true;
                }
            }
            if (found)
            {
                // Связку «персонаж ↔ участник боя» восстанавливаем при каждом
                // обновлении профиля: игрок мог войти раньше, чем ведущий
                // добавил его в трекер. Состояния применяем, только если сам
                // игрок их изменил в листе (иначе периодическая отправка
                // профиля затирала бы отметки ведущего).
                bool conditionsChanged = !string.Equals(previousConditions ?? "",
                    data.Conditions ?? "", StringComparison.Ordinal);
                SyncPlayerConditions(data, conditionsChanged);
                Events?.PlayerChanged?.Invoke(data);
                _ = BroadcastSnapshotAsync();
            }
        }

        /// <summary>
        /// Начальное заполнение состояний из листа персонажа и восстановление
        /// связки по character_id.
        ///
        /// Состояния применяем только к пустому участнику: если писать из
        /// листа на каждом обновлении профиля, устаревшая копия у игрока
        /// затирала бы отметки, которые ведущий поставил в трекере/на карте.
        /// Хиты не трогаем вовсе: их ведёт мастер.
        /// </summary>
        private void SyncPlayerConditions(PlayerInfo data, bool explicitChange = false)
        {
            if (data == null || data.CharacterId <= 0) return;

            try
            {
                string name = string.IsNullOrWhiteSpace(data.CharacterName)
                    ? (data.Name ?? "") : data.CharacterName;

                InitiativeService.LinkCombatantToCharacter(name, data.CharacterId);

                var found = InitiativeService.FindForCharacter(data.CharacterId, name);
                if (found == null) return;

                var encounter = found.Value.Encounter;
                var combatant = found.Value.Combatant;
                string conditions = (data.Conditions ?? "").Trim();
                bool changed = false;

                if (explicitChange)
                {
                    // Игрок сам поменял отметки в листе — переносим их.
                    if (!string.Equals(combatant.Conditions ?? "", conditions, StringComparison.Ordinal))
                    {
                        combatant.Conditions = conditions;
                        changed = true;
                    }
                }
                else if (string.IsNullOrWhiteSpace(combatant.Conditions) && conditions.Length > 0)
                {
                    // Первый вход: у участника боя ещё нет состояний.
                    combatant.Conditions = conditions;
                    changed = true;
                }

                if (changed)
                {
                    InitiativeService.SaveCombatant(encounter, combatant);
                    SetInitiative(InitiativeService.BuildState(encounter));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] состояния участника: {ex.Message}");
            }
        }

        /// <summary>
        /// Участника боя изменили (трекер, карта, лист) — переносим хиты и
        /// состояния в жетоны карты и рассылаем игрокам. Так изменения видны
        /// сразу, а не после следующего случайного обновления сцены.
        /// </summary>
        private void OnInitiativeChanged()
        {
            try
            {
                if (_combatantSyncTimer == null)
                {
                    _combatantSyncTimer = new System.Threading.Timer(
                        _ => SyncMapTokenStates(), null,
                        System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                }
                _combatantSyncTimer.Change(120, System.Threading.Timeout.Infinite);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] план синхронизации: {ex.Message}");
            }
        }

        private void SyncMapTokenStates()
        {
            try
            {
                var byId = new Dictionary<int, CombatantData>();
                foreach (var encounter in InitiativeService.LoadEncounters())
                {
                    foreach (var combatant in encounter.Combatants ?? new List<CombatantData>())
                    {
                        if (combatant != null && combatant.Id > 0) byId[combatant.Id] = combatant;
                    }
                }

                bool changed = false;
                lock (_stateLock)
                {
                    var tokens = _map?.Tokens;
                    if (tokens == null) return;

                    foreach (var token in tokens)
                    {
                        if (token.CombatantId <= 0) continue;
                        if (!byId.TryGetValue(token.CombatantId, out var combatant)) continue;

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
                    }
                }

                if (!changed) return;
                _ = BroadcastMapStateAsync();
                MapChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomServer] синхронизация состояний: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверяет вход жетона в тёмные зоны со скриптом. Смотрим именно
        /// переход «снаружи → внутрь» по старой и новой точке: пока жетон
        /// внутри, проверка не повторяется, а загрузка карты броски не
        /// запускает (вызывается только при движении).
        /// </summary>
        private void CheckZoneScripts(MapTokenData token, double fromX, double fromY)
        {
            if (token == null || token.OwnerCharacterId <= 0) return;

            MapSceneData scene;
            lock (_stateLock) scene = _map;
            if (scene?.Shapes == null || scene.Shapes.Count == 0) return;

            double cell = scene.GridPx > 4 ? scene.GridPx : 70;

            foreach (var shape in scene.Shapes)
            {
                if (shape == null) continue;
                if (string.IsNullOrWhiteSpace(shape.ScriptKind)) continue;

                string kind = shape.Kind ?? "";
                if (!kind.StartsWith("darkzone", StringComparison.OrdinalIgnoreCase)) continue;

                bool wasInside = ZoneContains(shape, fromX, fromY, cell);
                bool inside = ZoneContains(shape, token.X, token.Y, cell);
                if (inside && !wasInside) TriggerZoneRoll(token, shape);
            }
        }

        private static bool ZoneContains(MapShapeData shape, double x, double y, double cell)
        {
            double radius = Math.Max(cell * 0.5, Math.Max(0.5, shape.RadiusCells) * cell);
            double dx = x - shape.X, dy = y - shape.Y;

            if ((shape.Kind ?? "").IndexOf("square", StringComparison.OrdinalIgnoreCase) >= 0)
                return Math.Abs(dx) <= radius && Math.Abs(dy) <= radius;

            return Math.Sqrt(dx * dx + dy * dy) <= radius;
        }

        private void TriggerZoneRoll(MapTokenData token, MapShapeData shape)
        {
            var request = new ZoneRollRequest
            {
                TokenId = token.Id,
                ShapeId = shape.Id,
                Kind = shape.ScriptKind,
                Ability = string.IsNullOrWhiteSpace(shape.ScriptAbility) ? "DEX" : shape.ScriptAbility,
                Skill = shape.ScriptSkill ?? "",
                DC = Math.Max(0, shape.ScriptDC),
                Name = string.IsNullOrWhiteSpace(shape.ScriptName)
                    ? (string.IsNullOrWhiteSpace(shape.Label) ? "Тёмная зона" : shape.Label)
                    : shape.ScriptName,
                CharacterId = token.OwnerCharacterId
            };

            ClientConnection target = null;
            foreach (var c in _clients.Values)
            {
                if (!c.HandshakeDone || c.Player == null) continue;
                if (c.Player.CharacterId == token.OwnerCharacterId) { target = c; break; }
            }

            if (target != null)
            {
                _ = SendSafeAsync(target, new NetMessage(MsgType.ZoneRollRequest, request));
                Log($"скрипт зоны «{request.Name}»: {Describe(target.Player)} бросает " +
                    $"{request.Kind} {request.Ability} (DC {request.DC})");
                return;
            }

            // Своего клиента нет: возможно, это жетон персонажа самого ведущего.
            ZoneRollRequested?.Invoke(request);
        }

        private static PlayerInfo Clone(PlayerInfo p) => new PlayerInfo
        {
            Id = p.Id,
            Name = p.Name,
            CharacterName = p.CharacterName,
            CharacterClass = p.CharacterClass,
            CharacterRace = p.CharacterRace,
            Level = p.Level,
            Hp = p.Hp,
            MaxHp = p.MaxHp,
            ArmorClass = p.ArmorClass,
            AvatarPath = p.AvatarPath,
            Role = p.Role,
            State = p.State,
            CharacterId = p.CharacterId,
            Conditions = p.Conditions ?? ""
        };

        /// <summary>Понятное имя участника: персонаж, иначе имя игрока.</summary>
        private static string Describe(PlayerInfo p)
        {
            if (p == null) return "Участник";
            if (!string.IsNullOrWhiteSpace(p.CharacterName)) return p.CharacterName;
            if (!string.IsNullOrWhiteSpace(p.Name)) return p.Name;
            return "Участник";
        }

        /// <summary>
        /// Регистрирует хоста как первого участника комнаты.
        ///
        /// Без этого ведущий не видел себя в собственном отряде: его добавлял
        /// только сетевой путь, а хост ни к чему не подключается.
        /// </summary>
        public PlayerInfo RegisterHost(PlayerInfo me)
        {
            if (me == null) return null;

            var player = Clone(me);
            player.Id = string.IsNullOrEmpty(player.Id)
                ? "host-" + Guid.NewGuid().ToString("N").Substring(0, 6)
                : player.Id;
            player.State = PeerState.Online;

            lock (_stateLock)
            {
                if (_players.Any(p => p.Id == player.Id)) return _players.First(p => p.Id == player.Id);

                player.Role = _players.Count == 0 ? RoomRole.GameMaster : RoomRole.Player;
                _players.Add(player);
            }

            AddFeed(new FeedEntry
            {
                Kind = FeedKind.System,
                AuthorName = "Система",
                Text = $"{Describe(player)} открыл комнату"
            });

            Events?.PlayerChanged?.Invoke(player);
            return player;
        }

        private void Log(string text)
        {
            Debug.WriteLine("[RoomServer] " + text);
            Events?.Log?.Invoke(text);
        }

        /// <summary>
        /// Строка о передаче аватара в room-log. По ней видно, попросили файл,
        /// получили его или отправили — без этого «аватар не виден» приходилось
        /// разбирать догадками.
        /// </summary>
        private static void LogPhoto(string text) => LogRoomLine("фото: " + text);

        /// <summary>Короткая строка в room-log: отказы и важные события комнаты.</summary>
        private static void LogRoomLine(string text)
        {
            try
            {
                string dir = HMWT_MUI.PhotoHelper.GetWritableRoot();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "room-log.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} {text}\r\n");
            }
            catch { }
        }

        #endregion
    }
}
