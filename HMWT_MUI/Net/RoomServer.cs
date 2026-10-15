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

        public RoomServerEvents Events { get; set; } = new RoomServerEvents();
        public string RoomId => _roomId;
        public string KeySecret => _keySecret;
        public int Port => _port;

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
                    RoomName = _roomId,
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
                await BroadcastSnapshotAsync().ConfigureAwait(false);

                // ── 4.5. Просим у нового игрока его аватар ──
                //
                // В снимке есть только путь к файлу, а самого файла у остальных
                // нет: фотографии передаются отдельно. Без этого запроса чужой
                // аватар не появлялся бы, пока владелец не сменит фото.
                _ = RequestPhotoAsync(conn);

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
            lock (_stateLock)
            {
                var index = _players.FindIndex(p => p.Id == chunk.OwnerId);
                if (index >= 0)
                {
                    _players[index].AvatarPath = relative;
                    updated = _players[index];
                }
            }

            if (updated != null)
            {
                Log($"{Describe(updated)}: получен аватар ({complete.Length} байт)");
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
        private async Task RequestPhotoAsync(ClientConnection conn)
        {
            if (conn == null || !conn.HandshakeDone) return;

            try
            {
                string relative = conn.Player?.AvatarPath ?? "";
                if (string.IsNullOrWhiteSpace(relative)) return;

                // Файл уже есть у хоста — рассылаем его остальным
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

                // Файла нет — просим прислать
                await SendSafeAsync(conn, new NetMessage(MsgType.PhotoRequest,
                    new PhotoRequest { OwnerId = conn.PlayerId })).ConfigureAwait(false);

                Log($"запрошен аватар у {conn.PlayerId}");
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
            if (req == null || string.IsNullOrWhiteSpace(req.Text)) return;

            string text = req.Text.Trim();
            if (text.Length > NetLimits.ChatMaxLength)
                text = text.Substring(0, NetLimits.ChatMaxLength);

            var entry = new FeedEntry
            {
                Kind = FeedKind.Chat,
                AuthorId = conn.PlayerId,
                AuthorName = Describe(conn.Player),
                Text = text
            };
            AddFeed(entry);

            // Рассылаем всем, включая отправителя: так у всех одинаковый порядок
            _ = BroadcastAsync(new NetMessage(MsgType.ChatMessage, entry));
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
            AddFeed(entry);

            if (hidden)
            {
                // Отправляем только мастеру
                _ = SendToAsync(conn.PlayerId, new NetMessage(MsgType.RollMessage, entry));
                Log($"скрытый бросок мастера: {entry.Formula} = {entry.Total}");
            }
            else
            {
                _ = BroadcastAsync(new NetMessage(MsgType.RollMessage, entry));
            }
        }

        #endregion

        #region === РАССЫЛКА ===

        /// <summary>Полный снимок: при 8 участниках это проще и надёжнее диффов.</summary>
        /// <summary>Собирает снимок текущего состояния комнаты.</summary>
        private RoomSnapshot BuildSnapshot()
        {
            lock (_stateLock)
            {
                return new RoomSnapshot
                {
                    RoomId = _roomId,
                    RoomName = _roomId,
                    Players = _players.Select(Clone).ToList(),
                    Feed = _feed.ToList(),
                    AppVersion = NetVersions.AppVersion,
                    ProtocolVersion = NetVersions.ProtocolVersion,
                    DataFingerprint = _dataFingerprint,
                    Initiative = CloneInitiative(_initiative)
                };
            }
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
                Combatants = new List<InitiativeCombatantDto>()
            };

            if (source.Combatants != null)
            {
                foreach (var combatant in source.Combatants)
                {
                    if (combatant == null) continue;
                    copy.Combatants.Add(new InitiativeCombatantDto
                    {
                        Name = combatant.Name ?? "",
                        Roll = combatant.Roll,
                        Modifier = combatant.Modifier,
                        Total = combatant.Total,
                        IsPlayer = combatant.IsPlayer,
                        IsCurrent = combatant.IsCurrent
                    });
                }
            }

            return copy;
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
            lock (_stateLock)
            {
                var existing = _players.FirstOrDefault(p => p.Id == playerId);
                if (existing != null)
                {
                    data.Id = existing.Id;
                    data.Role = existing.Role;
                    data.State = existing.State;
                    int idx = _players.IndexOf(existing);
                    _players[idx] = data;
                    found = true;
                }
            }
            if (found)
            {
                Events?.PlayerChanged?.Invoke(data);
                _ = BroadcastSnapshotAsync();
            }
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
            CharacterId = p.CharacterId
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

        #endregion
    }
}
