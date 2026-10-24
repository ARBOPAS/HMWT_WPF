using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HMWT_MUI.Models;
using HMWT_MUI.Services;

namespace HMWT_MUI.Net
{
    /// <summary>События клиента комнаты для интерфейса.</summary>
    public sealed class RoomClientEvents
    {
        public Action<RoomSnapshot> SnapshotReceived { get; set; }

        /// <summary>Хост прислал карту боя.</summary>
        public Action<MapSceneData> MapStateReceived { get; set; }

        /// <summary>Картинка карты собрана из частей: (хэш, путь к файлу).</summary>
        public Action<string, string> MapImageReceived { get; set; }

        /// <summary>Ведущий начал разведку заново: очистить локальное «разведанное».</summary>
        public Action MapSeenResetReceived { get; set; }

        /// <summary>Жетон сдвинулся во время чужого перетаскивания: (id, x, y, поворот).</summary>
        public Action<int, double, double, double> TokenLiveReceived { get; set; }

        /// <summary>Фигуру ведут мышью: (id, x, y). Лёгкое обновление без карты.</summary>
        public Action<int, double, double> ShapeLiveReceived { get; set; }

        /// <summary>Пришёл путь жетона от другого участника.</summary>
        public Action<int, List<MapTrailPoint>> MapTrailReceived { get; set; }

        /// <summary>Пришёл новый разведанный сектор от другого игрока/хоста.</summary>
        public Action<string> MapSeenReceived { get; set; }

        /// <summary>Хост прислал всё разведанное по сцене.</summary>
        public Action<List<string>> MapSeenSynced { get; set; }

        /// <summary>Кто-то пингнул точку на карте.</summary>
        public Action<double, double> PingReceived { get; set; }

        /// <summary>Хост прислал картинку из истории чата: (id, base64, ext).</summary>
        public Action<string, string, string> ChatImageReceived { get; set; }
        public Action<FeedEntry> FeedReceived { get; set; }
        public Action<string, string, bool> ConnectionChanged { get; set; }  // (состояние, текст, подключён)
        public Action<string, string> Failed { get; set; }                   // (код ошибки, текст)
        public Action<string> Log { get; set; }

        /// <summary>
        /// Пришло чужое фото целиком: (идентификатор участника, байты, расширение).
        ///
        /// Фото передаётся через хоста, потому что у остальных игроков порт
        /// закрыт и принять файл напрямую они не могут.
        /// </summary>
        public Action<string, byte[], string> PhotoReceived { get; set; }

        /// <summary>У хоста нет аватара этого участника: повторно просить не нужно.</summary>
        public Action<string> PhotoMissing { get; set; }

        /// <summary>
        /// Хост просит прислать наш аватар.
        ///
        /// Так приходит запрос при входе: снимок содержит только путь к файлу,
        /// а самого файла у остальных нет.
        /// </summary>
        public Action<string> PhotoRequested { get; set; }

        /// <summary>Хост просит наши данные: отвечаем актуальным профилем.</summary>
        public Action<string> ProfileRequested { get; set; }
    }

    /// <summary>
    /// Клиент комнаты. Подключается к хосту, проходит рукопожатие, получает
    /// снимки состояния и события ленты.
    ///
    /// Переподключение делается с нарастающей паузой: если хост перезапускается,
    /// клиент не устраивает шторм подключений, но и не сдаётся сразу.
    /// </summary>
    public sealed class RoomClient : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _roomKey;
        private readonly string _roomId;
        private readonly string _dataFingerprint;
        private PlayerInfo _me;

        /// <summary>Сборщики частей фото по каждому отправителю.</summary>
        /// <summary>Сборщики частей картинки карты: хэш -> части.</summary>
        private readonly Dictionary<string, MapImageAssembler> _mapImages =
            new Dictionary<string, MapImageAssembler>(StringComparer.Ordinal);

        /// <summary>Последняя полученная карта боя.</summary>
        public MapSceneData LastMap { get; private set; }

        private readonly Dictionary<string, PhotoAssembler> _photoAssemblers =
            new Dictionary<string, PhotoAssembler>(StringComparer.Ordinal);

        /// <summary>
        /// Просит хост прислать аватар участника.
        ///
        /// Нужно при входе: в снимке есть только путь к файлу, а самого файла
        /// у нового игрока нет. Без запроса чужой аватар не появлялся бы,
        /// пока его владелец не сменит фото.
        /// </summary>
        public Task SendPhotoRequestAsync(string ownerId)
            => SendAsync(new NetMessage(MsgType.PhotoRequest, new PhotoRequest { OwnerId = ownerId ?? "" }));

        /// <summary>
        /// Отправляет фотографию хосту частями.
        ///
        /// Напрямую другим игрокам отправить нельзя: у них порт закрыт, роутер
        /// входящее соединение не пропустит. Хост соберёт файл и разошлёт всем.
        /// </summary>
        public async Task SendPhotoAsync(string fullPath, string ownerId, string roomId)
        {
            var parts = PhotoTransferService.Split(fullPath, ownerId, roomId);
            if (parts.Count == 0)
            {
                Log("фото не отправлено: файл не подошёл или слишком большой");
                return;
            }

            foreach (var part in parts)
            {
                await SendAsync(new NetMessage(MsgType.PhotoChunk, part)).ConfigureAwait(false);
            }

            Log($"фото отправлено частями: {parts.Count}");
        }

        /// <summary>
        /// Принимает часть фото. Возвращает готовые байты, когда файл собран.
        ///
        /// Части приходят от хоста: чужие аватары передаются только так.
        /// </summary>
        public byte[] AddPhotoChunk(PhotoChunk chunk)
        {
            if (chunk == null) return null;

            PhotoAssembler assembler;
            lock (_photoAssemblers)
            {
                if (!_photoAssemblers.TryGetValue(chunk.OwnerId ?? "", out assembler))
                {
                    assembler = new PhotoAssembler();
                    _photoAssemblers[chunk.OwnerId ?? ""] = assembler;
                }
            }

            lock (assembler)
            {
                return assembler.Add(chunk);
            }
        }

        private TcpClient _tcp;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private int _reconnectAttempt;
        private bool _wantConnected;
        private readonly Random _jitter = new Random();

        public RoomClientEvents Events { get; set; } = new RoomClientEvents();

        /// <summary>Идентификатор, который присвоил хост. Пусто, пока не подключены.</summary>
        public string PlayerId { get; private set; } = "";

        /// <summary>
        /// Идентификатор комнаты, названный хостом в приветствии.
        /// Нужен для папки фотографий: у гостя своей комнаты на диске нет,
        /// и раньше RoomId оставался пустым — полученные аватары сохранялись
        /// в одну папку, а искались в другой.
        /// </summary>
        public string RoomId { get; private set; } = "";

        /// <summary>Имя комнаты из приветствия хоста.</summary>
        public string RoomName { get; private set; } = "";

        public RoomRole Role { get; private set; } = RoomRole.Player;
        public bool IsConnected { get; private set; }
        public RoomSnapshot LastSnapshot { get; private set; }
        public string LastError { get; private set; } = "";

        /// <summary>Ждать ли переподключения автоматически.</summary>
        public bool AutoReconnect { get; set; } = true;

        public RoomClient(string host, int port, string roomKey, string roomId, string dataFingerprint)
        {
            _host = (host ?? "").Trim();
            _port = port <= 0 ? NetVersions.DefaultPort : port;
            _roomKey = roomKey ?? "";
            _roomId = roomId ?? "";
            _dataFingerprint = dataFingerprint ?? "";
        }

        #region === ПОДКЛЮЧЕНИЕ ===

        /// <summary>Первое подключение. Возвращает false и текст ошибки при отказе хоста.</summary>
        public async Task<bool> ConnectAsync(PlayerInfo me, CancellationToken token = default)
        {
            _me = me;
            _wantConnected = true;
            _reconnectAttempt = 0;
            return await ConnectOnceAsync(token).ConfigureAwait(false);
        }

        private async Task<bool> ConnectOnceAsync(CancellationToken token)
        {
            CloseSocket();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            try
            {
                NotifyState("Подключение…", false);

                _tcp = new TcpClient { NoDelay = true };
                _tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                var connectTask = _tcp.ConnectAsync(_host, _port);
                var timeout = Task.Delay(TimeSpan.FromSeconds(8), _cts.Token);
                var finished = await Task.WhenAny(connectTask, timeout).ConfigureAwait(false);
                if (finished != connectTask)
                {
                    Fail(NetError.Internal, $"Хост {_host}:{_port} не отвечает.");
                    return false;
                }
                await connectTask.ConfigureAwait(false);

                _stream = _tcp.GetStream();

                // ── 1. Получаем приветствие хоста с nonce ──
                var first = await NetFraming.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (first == null)
                {
                    Fail(NetError.Internal, "Хост разорвал соединение до рукопожатия.");
                    return false;
                }

                if (first.Type == MsgType.Error)
                {
                    var err = first.As<HelloResponse>();
                    Fail(err?.ErrorCode ?? NetError.Internal, err?.ErrorText ?? "Хост отказал в подключении.");
                    return false;
                }

                if (first.Type != MsgType.Hello)
                {
                    Fail(NetError.ProtocolMismatch, "Хост отвечает не по протоколу комнаты.");
                    return false;
                }

                var challenge = first.As<HelloChallenge>();
                if (challenge == null)
                {
                    Fail(NetError.ProtocolMismatch, "Не удалось разобрать приветствие хоста.");
                    return false;
                }

                // ── 2. Отвечаем: версии + доказательство ключа ──
                RoomId = string.IsNullOrEmpty(_roomId) ? challenge.RoomId : _roomId;
                RoomName = challenge.RoomName ?? "";

                var hello = new HelloRequest
                {
                    AppVersion = NetVersions.AppVersion,
                    ProtocolVersion = NetVersions.ProtocolVersion,
                    DataFingerprint = _dataFingerprint,
                    RoomId = RoomId,
                    Nonce = challenge.Nonce,
                    Proof = RoomKey.ComputeProof(_roomKey, challenge.Nonce),
                    Player = _me ?? new PlayerInfo()
                };

                await NetFraming.WriteAsync(_stream, new NetMessage(MsgType.Hello, hello), _cts.Token)
                    .ConfigureAwait(false);

                // ── 3. Ответ хоста ──
                var second = await NetFraming.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (second == null)
                {
                    Fail(NetError.Internal, "Хост не ответил на рукопожатие.");
                    return false;
                }

                if (second.Type == MsgType.Error)
                {
                    var err = second.As<HelloResponse>();
                    Fail(err?.ErrorCode ?? NetError.Internal, err?.ErrorText ?? "Хост отказал в подключении.");
                    return false;
                }

                var ok = second.As<HelloResponse>();
                if (ok == null || !ok.Ok)
                {
                    Fail(ok?.ErrorCode ?? NetError.Internal, ok?.ErrorText ?? "Подключение отклонено.");
                    return false;
                }

                PlayerId = ok.PlayerId;
                Role = ok.Role;
                IsConnected = true;
                _reconnectAttempt = 0;

                Log($"подключено к {_host}:{_port}, я {PlayerId}, роль {Role}");

                // ── 4. Только теперь читаем поток ──
                //
                // Раньше цикл чтения стартовал ДО сообщения «Подключено», и первый
                // снимок хоста успевал прийти раньше, чем вызывающий код выставлял
                // режим сессии. Статус считался по пустому списку участников и
                // показывал «0/8», хотя игрок уже был в комнате.
                NotifyState("Подключено", true);
                _ = Task.Run(() => ReadLoopAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                Fail(NetError.Internal, $"Не удалось подключиться к {_host}:{_port}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Отключается от комнаты и запрещает переподключение.</summary>
        public void Disconnect()
        {
            _wantConnected = false;
            IsConnected = false;
            try { _cts?.Cancel(); } catch { }
            CloseSocket();
            NotifyState("Отключено", false);
        }

        public void Dispose() => Disconnect();

        private void CloseSocket()
        {
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
        }

        #endregion

        #region === ПРИЁМ ===

        private async Task ReadLoopAsync(CancellationToken token)
        {
            string disconnectReason = "Соединение закрыто хостом.";

            while (!token.IsCancellationRequested)
            {
                NetMessage msg;
                try
                {
                    msg = await NetFraming.ReadAsync(_stream, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    disconnectReason = ex.Message;
                    break;
                }

                if (msg == null) break;
                if (string.IsNullOrEmpty(msg.Type)) continue;


                switch (msg.Type)
                {
                    case MsgType.Snapshot:
                        var snap = msg.As<RoomSnapshot>();
                        if (snap != null)
                        {
                            LastSnapshot = snap;
                            Events?.SnapshotReceived?.Invoke(snap);
                        }
                        break;

                    case MsgType.ProfileRequest:
                        // Хост просит наши данные: отвечаем тем, что есть сейчас
                        var askProfile = msg.As<ProfileRequest>();
                        if (askProfile != null)
                        {
                            Debug.WriteLine($"[Отряд] хост просит данные игрока {askProfile.owner}");
                            Events?.ProfileRequested?.Invoke(askProfile.owner);
                        }
                        break;

                    case MsgType.PhotoRequest:
                        // Хост просит наш аватар: отвечаем теми же частями.
                        // Так чужой игрок получает наше фото сразу при входе.
                        var ask = msg.As<PhotoRequest>();
                        if (ask != null)
                        {
                            Debug.WriteLine($"[Фото] хост просит аватар {ask.OwnerId}");
                            Events?.PhotoRequested?.Invoke(ask.OwnerId);
                        }
                        break;

                    case MsgType.PhotoMissing:
                        // У хоста этого аватара нет: не просим повторно
                        var missing = msg.As<PhotoRequest>();
                        if (missing != null)
                        {
                            Debug.WriteLine($"[Фото] у хоста нет аватара {missing.OwnerId}");
                            Events?.PhotoMissing?.Invoke(missing.OwnerId);
                        }
                        break;

                    case MsgType.PhotoChunk:
                        // Чужой аватар приходит частями через хоста
                        var photoChunk = msg.As<PhotoChunk>();
                        var photoBytes = AddPhotoChunk(photoChunk);
                        if (photoBytes != null && photoChunk != null)
                        {
                            Events?.PhotoReceived?.Invoke(
                                photoChunk.OwnerId, photoBytes,
                                string.IsNullOrWhiteSpace(photoChunk.Extension) ? ".png" : photoChunk.Extension);
                        }
                        break;

                    case MsgType.MapState:
                        // Хост разослал карту: запоминаем и показываем
                        var scene = msg.As<MapSceneData>();
                        if (scene != null)
                        {
                            LastMap = scene;
                            Events?.MapStateReceived?.Invoke(scene);
                        }
                        break;

                    case MsgType.MapImageChunk:
                        var mapChunk = msg.As<MapImageChunk>();
                        string saved = AddMapImageChunk(mapChunk);
                        if (!string.IsNullOrEmpty(saved) && mapChunk != null)
                            Events?.MapImageReceived?.Invoke(mapChunk.Hash, saved);
                        break;

                    case MsgType.MapSeenReset:
                        Events?.MapSeenResetReceived?.Invoke();
                        break;

                    case MsgType.MapTokenLive:
                        var live = msg.As<MapTokenLive>();
                        if (live != null)
                            Events?.TokenLiveReceived?.Invoke(live.TokenId, live.X, live.Y, live.Facing);
                        break;

                    case MsgType.MapShapeLive:
                        var shapeLive = msg.As<MapShapeLive>();
                        if (shapeLive != null)
                            Events?.ShapeLiveReceived?.Invoke(shapeLive.ShapeId, shapeLive.X, shapeLive.Y);
                        break;

                    case MsgType.MapTrail:
                        var trail = msg.As<MapTrailMessage>();
                        if (trail?.Points != null)
                            Events?.MapTrailReceived?.Invoke(trail.TokenId, trail.Points);
                        break;

                    case MsgType.MapSeenAdd:
                        var seen = msg.As<MapSeenAddRequest>();
                        if (seen != null) Events?.MapSeenReceived?.Invoke(seen.Points);
                        break;

                    case MsgType.MapSeenSync:
                        var seenSync = msg.As<MapSeenSync>();
                        if (seenSync?.Seen != null) Events?.MapSeenSynced?.Invoke(seenSync.Seen);
                        break;

                    case MsgType.MapPing:
                        var ping = msg.As<MapPingRequest>();
                        if (ping != null) Events?.PingReceived?.Invoke(ping.X, ping.Y);
                        break;

                    case MsgType.ChatImageResponse:
                        var image = msg.As<ChatImageResponse>();
                        if (image != null)
                            Events?.ChatImageReceived?.Invoke(image.ImageId, image.ImageBase64, image.ImageExtension);
                        break;

                    case MsgType.ChatMessage:
                    case MsgType.RollMessage:
                    case MsgType.SystemNotice:
                        var entry = msg.As<FeedEntry>();
                        if (entry != null) Events?.FeedReceived?.Invoke(entry);
                        break;

                    case MsgType.Ping:
                        // На пинг сразу отвечаем: хост по этому определяет живых
                        await SendAsync(new NetMessage(MsgType.Pong, new { at = DateTime.UtcNow }))
                            .ConfigureAwait(false);
                        break;

                    case MsgType.Pong:
                        break;

                    case MsgType.Error:
                        var err = msg.As<HelloResponse>();
                        if (err != null) Fail(err.ErrorCode ?? NetError.Internal, err.ErrorText ?? "Ошибка от хоста.");
                        break;
                }
            }

            if (!token.IsCancellationRequested)
                HandleDisconnect(disconnectReason);
        }

        private void HandleDisconnect(string reason)
        {
            IsConnected = false;
            CloseSocket();
            NotifyState("Соединение потеряно", false);
            Log("разрыв: " + reason);

            if (!_wantConnected || !AutoReconnect) return;

            // Нарастающая пауза с разбросом: 1, 2, 4, 8… до 30 секунд.
            // Разброс нужен, чтобы несколько клиентов не били по хосту одновременно.
            _reconnectAttempt++;
            int seconds = Math.Min(30, (int)Math.Pow(2, Math.Min(5, _reconnectAttempt)));
            seconds += _jitter.Next(0, 1000) / 1000;

            Log($"переподключение через {seconds} с (попытка {_reconnectAttempt})");
            NotifyState($"Переподключение через {seconds} с", false);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
                    if (!_wantConnected) return;
                    await ConnectOnceAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RoomClient] переподключение: {ex.Message}");
                }
            });
        }

        #endregion

        #region === ОТПРАВКА ===

        /// <summary>Сообщение в общий чат.</summary>
        public Task SendChatAsync(string text)
            => SendAsync(new NetMessage(MsgType.ChatSend, new ChatSendRequest { Text = text ?? "" }));

        /// <summary>Картинка/гифка в чат вместе с подписью.</summary>
        public Task SendChatImageAsync(string text, string base64, string extension, string fileName)
            => SendAsync(new NetMessage(MsgType.ChatSend, new ChatSendRequest
            {
                Text = text ?? "",
                ImageBase64 = base64 ?? "",
                ImageExtension = extension ?? "",
                ImageName = fileName ?? ""
            }));

        /// <summary>
        /// Сообщает о броске. Кубы считает сам клиент своим DiceRoller —
        /// хост только фиксирует результат в общей ленте.
        /// </summary>
        public Task SendRollAsync(string formula, string rollType, int total,
            List<int> rolls, bool hidden = false)
            => SendAsync(new NetMessage(MsgType.RollSend, new RollSendRequest
            {
                Formula = formula ?? "",
                RollType = rollType ?? "",
                Total = total,
                IndividualRolls = rolls ?? new List<int>(),
                Hidden = hidden
            }));

        /// <summary>Обновляет свои данные (например, изменилось ХП на листе).</summary>
        /// <summary>
        /// Просит хост прислать снимок заново.
        ///
        /// Нужно для периодического обновления: изменение чужого аватара или
        /// здоровья могло не дойти событием, поэтому состояние запрашивается само.
        /// </summary>
        /// <summary>Просит у хоста текущую карту боя.</summary>
        public Task SendMapRequestAsync()
            => SendAsync(new NetMessage(MsgType.MapRequest, new { }));

        /// <summary>Сообщает хосту, что игрок подвинул своего жетона.</summary>
        public Task SendMapMoveAsync(int tokenId, double x, double y, double facing = double.NaN)
            => SendAsync(new NetMessage(MsgType.MapMove, new MapMoveRequest
            {
                TokenId = tokenId,
                X = x,
                Y = y,
                Facing = double.IsNaN(facing) ? -1 : facing
            }));

        /// <summary>
        /// Ход жетона вместе со сменой обзора/размера. Отдельное имя, а не
        /// перегрузка: проверки и старые вызовы ищут SendMapMoveAsync, и две
        /// перегрузки ломали поиск метода через reflection.
        /// </summary>
        public Task SendMapMoveEditAsync(int tokenId, double x, double y, double facing,
            double viewAngle, int sizeCells, double revealCells = -1, string viewColor = null,
            string conditions = null)
            => SendAsync(new NetMessage(MsgType.MapMove, new MapMoveRequest
            {
                TokenId = tokenId,
                X = x,
                Y = y,
                Facing = double.IsNaN(facing) ? -1 : facing,
                ViewAngle = viewAngle,
                SizeCells = sizeCells,
                RevealCells = revealCells,
                ViewColor = viewColor,
                Conditions = conditions
            }));

        /// <summary>Просит картинку карты по хэшу (если файла ещё нет).</summary>
        public Task SendMapImageRequestAsync(string hash)
            => SendAsync(new NetMessage(MsgType.MapImageRequest, new MapImageRequest { Hash = hash ?? "" }));

        /// <summary>
        /// Промежуточное движение при перетаскивании: без записи в базу на
        /// каждом шаге, только чтобы остальные видели жетон в реальном времени.
        /// </summary>
        public Task SendMapMoveLiveAsync(int tokenId, double x, double y, double facing)
            => SendAsync(new NetMessage(MsgType.MapMove, new MapMoveRequest
            {
                TokenId = tokenId,
                X = x,
                Y = y,
                Facing = double.IsNaN(facing) ? -1 : facing,
                Live = true
            }));

        public Task SendMapTrailAsync(int tokenId, List<MapTrailPoint> points)
            => SendAsync(new NetMessage(MsgType.MapTrail, new MapTrailMessage
            {
                TokenId = tokenId,
                Points = points ?? new List<MapTrailPoint>()
            }));

        public Task SendMapShapeAsync(string action, MapShapeData shape)
            => SendAsync(new NetMessage(MsgType.MapShape, new MapShapeRequest
            {
                Action = action ?? "update",
                Shape = shape
            }));

        /// <summary>Ведём фигуру: только координаты, без полной карты.</summary>
        public Task SendShapeLiveAsync(int shapeId, double x, double y)
            => SendAsync(new NetMessage(MsgType.MapShapeLive, new MapShapeLive
            {
                ShapeId = shapeId,
                X = x,
                Y = y
            }));

        /// <summary>Отправляет нарисованный штрих ведущему.</summary>
        public Task SendMapStrokeAsync(MapStrokeRequest stroke)
            => SendAsync(new NetMessage(MsgType.MapStroke, stroke));

        /// <summary>Отправляет новый разведанный сектор ведущему.</summary>
        public Task SendMapSeenAsync(string points)
            => SendAsync(new NetMessage(MsgType.MapSeenAdd, new MapSeenAddRequest { Points = points ?? "" }));

        /// <summary>Пинг по карте: ведущий разошлёт всем.</summary>
        public Task SendMapPingAsync(double x, double y)
            => SendAsync(new NetMessage(MsgType.MapPing, new MapPingRequest { X = x, Y = y }));

        /// <summary>Просит у хоста картинку из истории чата.</summary>
        public Task RequestChatImageAsync(string imageId)
            => SendAsync(new NetMessage(MsgType.ChatImageRequest, new ChatImageRequest { ImageId = imageId ?? "" }));

        /// <summary>Сообщает ведущему, какие штрихи стёрты ластиком.</summary>
        public Task SendMapStrokeEraseAsync(List<int> ids)
            => SendAsync(new NetMessage(MsgType.MapStrokeErase,
                new MapStrokeEraseRequest { Ids = ids ?? new List<int>() }));

        /// <summary>
        /// Складывает часть картинки карты. Когда пришли все части — распаковывает
        /// gzip и кладёт файл в папку данных, чтобы он там и остался.
        /// </summary>
        private string AddMapImageChunk(MapImageChunk chunk)
        {
            if (chunk == null || string.IsNullOrWhiteSpace(chunk.Hash) || chunk.Total <= 0) return null;

            if (!_mapImages.TryGetValue(chunk.Hash, out var assembler))
            {
                assembler = new MapImageAssembler(chunk.Total, chunk.Extension);
                _mapImages[chunk.Hash] = assembler;
            }

            byte[] ready = assembler.Add(chunk.Index, chunk.Data);
            if (ready == null) return null;

            _mapImages.Remove(chunk.Hash);

            try
            {
                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Maps");
                Directory.CreateDirectory(dir);

                string ext = string.IsNullOrWhiteSpace(chunk.Extension) ? ".png" : chunk.Extension;
                string path = Path.Combine(dir, "remote_" + chunk.Hash + ext);

                using (var output = File.Create(path))
                using (var gzip = new System.IO.Compression.GZipStream(
                    new MemoryStream(ready), System.IO.Compression.CompressionMode.Decompress))
                {
                    gzip.CopyTo(output);
                }

                return path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Карта] картинка не распаковалась: {ex.Message}");
                return null;
            }
        }
        public Task SendSnapshotRequestAsync()
            => SendAsync(new NetMessage(MsgType.SnapshotRequest, new { }));

        public Task SendPlayerUpdateAsync(PlayerInfo me)
            => SendAsync(new NetMessage(MsgType.PlayerUpdated, me));

        /// <summary>
        /// Прощание перед выходом. Автопереподключение выключаем: иначе клиент
        /// молча возвращался в комнату и на хосте появлялся второй такой же игрок.
        /// </summary>
        public Task SendLeaveAsync()
        {
            AutoReconnect = false;
            return SendAsync(new NetMessage(MsgType.Leave, new LeaveRequest { owner = PlayerId }));
        }

        private async Task SendAsync(NetMessage message)
        {
            if (!IsConnected || _stream == null) return;

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await NetFraming.WriteAsync(_stream, message, _cts?.Token ?? CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomClient] отправка: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        #endregion

        #region === ВСПОМОГАТЕЛЬНОЕ ===

        private void Fail(string code, string text)
        {
            LastError = text;
            IsConnected = false;
            Log($"отказ ({code}): {text}");
            Events?.Failed?.Invoke(code ?? NetError.Internal, text ?? "");
            NotifyState(text ?? "Ошибка", false);
        }

        private void NotifyState(string state, bool connected)
            => Events?.ConnectionChanged?.Invoke(state, LastError, connected);

        private void Log(string text)
        {
            Debug.WriteLine("[RoomClient] " + text);
            Events?.Log?.Invoke(text);
        }

        /// <summary>Лента из последнего снимка (для восстановления вида после переподключения).</summary>
        public List<FeedEntry> GetFeed()
            => LastSnapshot?.Feed ?? new List<FeedEntry>();

        public List<PlayerInfo> GetPlayers()
            => LastSnapshot?.Players ?? new List<PlayerInfo>();

        #endregion
    }
}
