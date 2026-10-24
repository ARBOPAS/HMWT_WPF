using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HMWT_MUI.Helpers;
using HMWT_MUI.Net;
using HMWT_MUI.Services;
using HMWT_MUI.Windows;
using Microsoft.Win32;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Вкладка «Отряд»: участники комнаты, общая лента бросков и чат.
    ///
    /// События комнаты приходят из сетевых потоков, поэтому любые изменения
    /// интерфейса идут через Dispatcher — иначе WPF упадёт при первом же броске.
    /// </summary>
    public partial class RoomView : UserControl
    {
        private readonly RoomSession _session = RoomSession.Instance;
        private readonly Dispatcher _ui;
        private int _renderedFeedCount = -1;
        private int _renderedPlayerCount = -1;

        /// <summary>
        /// Подпись данных участников на момент последней перерисовки.
        ///
        /// null означает «ещё ни разу не рисовали». Это важно: у пустого списка
        /// подпись тоже пустая, и если начать с пустой строки, то пустое
        /// состояние («Пока никого») никогда не отрисуется.
        /// </summary>
        private string _renderedPlayersSignature = null;

        /// <summary>Персонаж этого окна: два окна на одном ПК должны брать разных.</summary>
        public int CharacterId { get; set; }

        /// <summary>
        /// Раз в две секунды доспрашиваем недостающие аватары. Если ответ потерялся
        /// (а такое бывало: «фото через раз»), запрос повторяется, пока файл не
        /// придёт. Рядом с заголовком участников видно «ждём фото…».
        /// </summary>
        private readonly DispatcherTimer _photoRetryTimer =
            new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        /// <summary>Подписки на события комнаты уже висят.</summary>
        private bool _subscribed;

        /// <summary>Обработчик таймера фото уже подписан.</summary>
        private bool _photoRetryHooked;

        /// <summary>Список истории комнат заполняется программой, а не игроком.</summary>
        private bool _recentRoomsLoading;
        private bool _chatOccluded;
        private readonly HashSet<string> _requestedChatImages = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _chatImageRequestedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private DispatcherTimer _chatImageTimer;

        /// <summary>Спрятать WebView под оверлеем: HWND WebView2 всегда выше WPF.</summary>
        public void SetChatOccluded(bool hidden)
        {
            _chatOccluded = hidden;
            if (ChatWeb == null) return;
            ChatWeb.SetOwnerOccluded(hidden);
        }

        private bool _audioVolumeReady;

        public RoomView()
        {
            InitializeComponent();
            _ui = Dispatcher;

            // Громкость общая для приложения: ползунок живёт и в «Отряде»,
            // и в аудиотеке ведущего, значение одно.
            sldVolume.Value = Math.Round(Services.AudioSettings.Master * 100);
            _audioVolumeReady = true;

            // Картинки догружаем по одной: хост не любит пачку запросов
            // и отбрасывает те, что пришли слишком часто.
            _chatImageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _chatImageTimer.Tick += (s, e) => UpdateFeed();

            // Лента на WebView2: гифки и картинки не грузят поток WPF.
            // Пока движок не поднялся — остаётся старая нативная лента.
            ChatWeb.Ready += () =>
            {
                ChatWeb.Width = double.NaN;
                ChatWeb.Height = double.NaN;
                ChatWeb.HorizontalAlignment = HorizontalAlignment.Stretch;
                ChatWeb.VerticalAlignment = VerticalAlignment.Stretch;
                ChatWeb.Opacity = 1;
                ChatWeb.SetOwnerOccluded(_chatOccluded);
                feedScroll.Visibility = Visibility.Collapsed;
                _renderedFeedCount = -1;
                UpdateFeed();
            };

            // Вставка целой строки от ведущего раскладывается по полям
            DataObject.AddPastingHandler(txtJoinHost, JoinField_Paste);
            DataObject.AddPastingHandler(txtJoinKey, JoinField_Paste);

            Loaded += (s, e) =>
            {
                if (ChatWeb != null) ChatWeb.SetOwnerOccluded(_chatOccluded);
                // Вкладку пересоздают при каждом возврате: подписки живут ровно
                // столько, сколько вкладка на экране. Раньше они вешались один
                // раз в конструкторе и снимались при уходе — после первого
                // переключения вкладка переставала получать события, и чат
                // обновлялся только при повторном открытии.
                SubscribeSession();
                RefreshAll();

                // Повторные запросы фото: пока файла нет, карточка остаётся
                // с буквой, а «ждём фото…» объясняет, что происходит
                if (!_photoRetryHooked)
                {
                    _photoRetryTimer.Tick += OnPhotoRetryTick;
                    _photoRetryHooked = true;
                }
                _photoRetryTimer.Start();
                OnPhotoRetryTick(null, EventArgs.Empty);
                // Обновляемся ещё раз, когда WPF закончит раскладку: комната могла
                // измениться, пока вкладка была не открыта
                Dispatcher.BeginInvoke(new Action(RefreshAll), DispatcherPriority.Loaded);
            };
            Unloaded += (s, e) =>
            {
                if (ChatWeb != null) ChatWeb.SetOwnerOccluded(true);
                UnsubscribeSession();
                _photoRetryTimer.Stop();
            };

            // Обработчики кнопок объявлены в разметке (Click="...") — так их видно
            // в XAML и проверяет тест. Здесь ничего дополнительно вешать не нужно.
        }

        /// <summary>Подписка на события комнаты, пока вкладка на экране.</summary>
        private void SubscribeSession()
        {
            if (_subscribed) return;
            _subscribed = true;

            _session.StateChanged += OnSessionStateChanged;
            _session.FeedReceived += OnFeedReceived;
            _session.SnapshotReceived += OnSnapshotReceived;
            _session.PeerDeparted += OnPeerDeparted;
            _session.PhotoUpdated += OnPhotoUpdated;
            _session.ChatImageReceived += OnChatImageReceived;
        }

        /// <summary>Отписка при уходе с вкладки. Подписки не должны копиться.</summary>
        private void UnsubscribeSession()
        {
            if (!_subscribed) return;
            _subscribed = false;

            _session.StateChanged -= OnSessionStateChanged;
            _session.FeedReceived -= OnFeedReceived;
            _session.SnapshotReceived -= OnSnapshotReceived;
            _session.PeerDeparted -= OnPeerDeparted;
            _session.PhotoUpdated -= OnPhotoUpdated;
            _session.ChatImageReceived -= OnChatImageReceived;
        }

        #region === ПОИСК РЕСУРСОВ ===

        /// <summary>
        /// Ищет ресурс в дереве, а если его там нет — в ресурсах приложения.
        ///
        /// FindResource здесь использовать нельзя: он бросает исключение, когда
        /// элемент ещё не подключён к визуальному дереву (вкладку могут показать
        /// до запуска цикла диспетчера), и вкладка падала с «ресурс не найден».
        /// </summary>
        private T Res<T>(string key) where T : class
        {
            var value = TryFindResource(key) ?? Application.Current?.TryFindResource(key);
            if (value is T typed) return typed;

            Debug.WriteLine($"[RoomView] ресурс '{key}' не найден");
            return null;
        }

        /// <summary>Шрифт по токену. Если токен недоступен, берётся системный — вкладка не падает.</summary>
        private FontFamily FontRes(string key)
            => Res<FontFamily>(key) ?? new FontFamily("Segoe UI");

        /// <summary>Кисть по токену с запасным значением: цвет не должен ронять отрисовку.</summary>
        private Brush BrushRes(string key, Color fallback)
            => Res<Brush>(key) ?? new SolidColorBrush(fallback);

        #endregion

        #region === СОБЫТИЯ КОМНАТЫ (из сетевых потоков) ===

        private void OnSessionStateChanged() => _ui.BeginInvoke(new Action(() =>
        {
            if (!_session.IsActive)
            {
                _requestedChatImages.Clear();
                _chatImageRequestedAt.Clear();
                _chatImageTimer.Stop();
                _renderedFeedCount = -1;
            }
            RefreshAll();
        }));

        private void OnFeedReceived(FeedEntry entry)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                // Участник ушёл: сервер (или ведущий) присылает системную запись
                // «X отключился». Раньше её было видно только в ленте, и пропустить
                // уход напарника было легко — теперь показываем уведомление.
                AnnounceDepartureFromFeed(entry);
                RefreshAll();
            }));
        }

        private void OnSnapshotReceived(RoomSnapshot snapshot) => _ui.BeginInvoke(new Action(() =>
        {
            // Снимок — самый достоверный источник списка участников. Сбрасываем
            // подпись, иначе карточка ушедшего могла остаться до переоткрытия вкладки.
            _renderedPlayersSignature = null;
            _renderedPlayerCount = -1;
            RefreshAll();
        }));

        private void OnChatImageReceived(string imageId, string base64, string extension)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                _requestedChatImages.Remove(imageId);
                _chatImageRequestedAt.Remove(imageId);
                if (ChatWeb.IsReady) ChatWeb.UpdateImage(imageId, base64, extension);
            }));
        }

        /// <summary>
        /// Участник ушёл (приходит у ведущего от сетевого слоя, у гостя — из
        /// разбора системной записи). Показываем уведомление с причиной.
        /// </summary>
        private void OnPeerDeparted(PlayerInfo player, string reason)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                string name = !string.IsNullOrWhiteSpace(player?.CharacterName)
                    ? player.CharacterName
                    : (!string.IsNullOrWhiteSpace(player?.Name) ? player.Name : "Участник");

                string key = name + "|" + reason;
                bool alreadyAnnounced = _departuresAnnounced.Contains(key);
                if (!alreadyAnnounced)
                {
                    _departuresAnnounced.Add(key);

                    string tail = string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})";
                    string message = $"Участник {name} отключился{tail}";

                    ShowWindowToast(message);
                    txtFeedHint.Text = message;
                }

                // Событие может прийти раньше снимка: обновляем сразу и ещё раз
                // чуть позже, когда список участников точно уже без ушедшего.
                _renderedPlayersSignature = null;
                _renderedPlayerCount = -1;
                RefreshAll();

                var retry = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(600)
                };
                retry.Tick += (s, e) =>
                {
                    retry.Stop();
                    _renderedPlayersSignature = null;
                    UpdatePlayers();
                    UpdatePhotoHint();
                };
                retry.Start();
            }));
        }

        /// <summary>
        /// Показывает тост средствами окна, в котором лежит вкладка.
        ///
        /// Вызов через dynamic, а не через прямой тип окна: исходники вкладки
        /// линкуются в тестовый проект, где окна персонажа нет, и прямая ссылка
        /// ломала бы сборку тестов. Если у окна нет ShowToast, просто пишем в лог.
        /// </summary>
        private void ShowWindowToast(string message, bool isError = false)
        {
            try
            {
                var window = Window.GetWindow(this);
                if (window == null) return;

                // Вызов через рефлексию: надёжнее, чем dynamic (не требует
                // Microsoft.CSharp), и переживает смену типа окна.
                var method = window.GetType().GetMethod("ShowToast",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, new[] { typeof(string), typeof(bool) }, null);

                if (method != null) method.Invoke(window, new object[] { message, isError });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] тост: {message} ({ex.GetType().Name})");
            }
        }
        /// <summary>Имена участников, о выходе которых уже сообщили.</summary>
        private readonly HashSet<string> _departuresAnnounced = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Показывает уведомление, если системная запись сообщает об уходе участника.
        ///
        /// Разбор по тексту, а не по отдельному событию: и хост, и гость получают
        /// уход именно записью ленты, и отдельный тип сообщения для этого не нужен —
        /// протокол остаётся прежним, старые сборки продолжают работать.
        /// </summary>
        private void AnnounceDepartureFromFeed(FeedEntry entry)
        {
            try
            {
                if (entry == null || entry.Kind != FeedKind.System) return;

                string text = entry.Text ?? "";
                if (!text.Contains("отключился")) return;

                // Имя — часть текста до слова «отключился»
                int at = text.IndexOf("отключился", StringComparison.Ordinal);
                string name = at > 0 ? text.Substring(0, at).Trim() : "Участник";
                if (name.Length == 0) name = "Участник";

                if (_departuresAnnounced.Contains(text)) return;
                _departuresAnnounced.Add(text);

                ShowWindowToast($"Участник {name} отключился");
                txtFeedHint.Text = $"Участник {name} отключился";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] уведомление об уходе: {ex.Message}");
            }
        }

        #endregion

        #region === ОБНОВЛЕНИЕ ВИДА ===

        /// <summary>
        /// Перерисовывает вкладку по текущему состоянию комнаты.
        ///
        /// Вызывается из кода, а не только по Loaded: событие Loaded приходит
        /// от цикла диспетчера, и если вкладку показывают до его запуска, она
        /// осталась бы пустой, хотя в комнате уже есть участники и записи.
        /// </summary>
        public void Refresh() => RefreshAll();

        private void RefreshAll()
        {
            UpdateHeader();
            UpdatePlayers();
            UpdateInitiative();
            UpdateFeed();
        }

        /// <summary>
        /// Порядок хода, который ведёт мастер.
        ///
        /// Игрокам приходит только имя и результат броска: бестиарий у каждого
        /// свой, а бой ведёт один человек. Когда бой не идёт, блока не видно —
        /// лента не должна съезжать из-за пустой рамки.
        /// </summary>
        private void UpdateInitiative()
        {
            try
            {
                var state = _session.GetInitiative();
                if (state == null || !state.HasContent)
                {
                    brInitiative.Visibility = Visibility.Collapsed;
                    spInitiative.Children.Clear();
                    return;
                }

                brInitiative.Visibility = Visibility.Visible;
                txtInitiativeRound.Text = string.IsNullOrWhiteSpace(state.Name)
                    ? state.RoundLine()
                    : $"{state.RoundLine()} · {state.Name}";

                spInitiative.Children.Clear();

                int position = 1;
                foreach (var combatant in state.Combatants)
                {
                    if (combatant == null) continue;

                    bool isCurrent = combatant.IsCurrent;

                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var number = new TextBlock
                    {
                        Text = position.ToString(),
                        FontFamily = FontRes("F.Display"),
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushRes(isCurrent ? "B.Accent" : "B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(number, 0);
                    row.Children.Add(number);

                    var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    nameRow.Children.Add(new TextBlock
                    {
                        Text = combatant.Name,
                        FontSize = 12,
                        FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = BrushRes(isCurrent ? "B.Accent" : "B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    if (isCurrent)
                    {
                        nameRow.Children.Add(new Border
                        {
                            Background = BrushRes("B.Accent", Color.FromRgb(0x5F, 0xD4, 0xDD)),
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(5, 1, 5, 1),
                            Margin = new Thickness(6, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = "ХОД",
                                FontSize = 9,
                                FontWeight = FontWeights.Bold,
                                Foreground = BrushRes("B.Void", Color.FromRgb(0x09, 0x0C, 0x16))
                            }
                        });
                    }
                    else if (combatant.IsPlayer)
                    {
                        nameRow.Children.Add(new TextBlock
                        {
                            Text = " игрок",
                            FontSize = 9.5,
                            Foreground = BrushRes("B.Frost", Color.FromRgb(0x4F, 0xB6, 0xAC)),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }

                    Grid.SetColumn(nameRow, 1);
                    row.Children.Add(nameRow);

                    var total = new TextBlock
                    {
                        Text = combatant.Total.ToString(),
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        FontFamily = FontRes("F.Display"),
                        Foreground = BrushRes("B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(total, 2);
                    row.Children.Add(total);

                    spInitiative.Children.Add(new Border
                    {
                        Background = isCurrent
                            ? BrushRes("B.Slate", Color.FromRgb(0x11, 0x17, 0x29))
                            : Brushes.Transparent,
                        BorderBrush = isCurrent
                            ? BrushRes("B.AccentDim", Color.FromRgb(0x2A, 0x6A, 0x74))
                            : Brushes.Transparent,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 3, 6, 3),
                        Margin = new Thickness(0, 0, 0, 2),
                        Child = row
                    });

                    position++;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] инициатива: {ex.Message}");
                brInitiative.Visibility = Visibility.Collapsed;
            }
        }

        private bool _suppressHiddenToggle;

        private void ChkHiddenRolls_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressHiddenToggle || _session == null) return;
            if (!_session.IsActive || !_session.IsHost) return;

            _session.HiddenDmRolls = chkHiddenRolls.IsChecked == true;
        }

        private void UpdateHeader()
        {
            bool active = _session.IsActive;

            txtRoomTitle.Text = active
                ? (string.IsNullOrWhiteSpace(_session.RoomName) ? "ОТРЯД" : _session.RoomName.ToUpperInvariant())
                : "ОТРЯД";
            txtRoomStatus.Text = _session.StatusText;

            // Цвет точки: серая — не подключены, зелёная — хост, синяя — участник
            brStatusDot.Background = BrushRes(!active ? "B.Mute" : (_session.IsHost ? "B.Ok" : "B.Frost"), Color.FromRgb(0x8B, 0x95, 0xB4));

            // Ключ и адрес видит только хост: остальным они не нужны
            bool host = _session.IsHost;
            lblKeyCaption.Visibility = txtRoomKey.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
            lblHostCaption.Visibility = txtHostAddress.Visibility = host ? Visibility.Visible : Visibility.Collapsed;

            txtRoomKey.Text = host ? RoomKey.Pretty(_session.Key) : "—";
            txtHostAddress.Text = host ? $"{_session.HostAddress}:{_session.Port}" : "";

            // Внешний адрес нужен только ведущему: игрокам его сообщает он сам.
            // Если узнать не удалось — честно предупреждаем: без него друзья
            // из интернета не подключатся, и человек должен понимать почему.
            brPublicRow.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
            btnRefreshIp.IsEnabled = host && !_session.IsPublicAddressLoading;
            btnRefreshIp.Opacity = btnRefreshIp.IsEnabled ? 1.0 : 0.45;
            btnCopyAddress.IsEnabled = host;

            if (host)
            {
                bool hasPublic = !string.IsNullOrEmpty(_session.PublicAddress);
                txtPublicAddress.Text = hasPublic
                    ? $"{_session.PublicAddress}:{_session.Port}"
                    : (_session.IsPublicAddressLoading ? "узнаём…" : "неизвестен");
                txtPublicAddress.Foreground = BrushRes(hasPublic ? "B.Frost" : "B.Mute",
                    hasPublic ? Color.FromRgb(0x4F, 0xB6, 0xAC) : Color.FromRgb(0x8B, 0x95, 0xB4));

                txtPublicHint.Text = hasPublic
                    ? "Друзьям нужен проброс этого порта на роутере. В одной сети подойдёт локальный адрес выше."
                    : "Внешний адрес получить не удалось: нет доступа в интернет или сервис недоступен. " +
                      "Играйте по локальному адресу в одной сети либо посмотрите адрес в настройках роутера (раздел WAN).";
            }

            btnCopyKey.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
            btnLeaveRoom.IsEnabled = active;
            btnLeaveRoom.Opacity = active ? 1.0 : 0.45;

            // Скрытые броски — только у ведущего: это его инструмент, игрокам
            // переключатель не нужен и только путал бы
            chkHiddenRolls.Visibility = active && host ? Visibility.Visible : Visibility.Collapsed;
            if (active && host)
            {
                _suppressHiddenToggle = true;
                chkHiddenRolls.IsChecked = _session.HiddenDmRolls;
                _suppressHiddenToggle = false;
            }

            txtChatInput.IsEnabled = active;
            btnSendChat.IsEnabled = active;
            btnSendChat.Opacity = active ? 1.0 : 0.45;
            txtChatInput.ToolTip = active ? "Enter — отправить" : "Сначала подключитесь к отряду";
        }

        private void UpdatePlayers()
        {
            UpdatePhotoHint();

            var players = _session.GetPlayers();
            txtPlayersHeader.Text = players.Count > 0
                ? $"УЧАСТНИКИ · {players.Count}/{NetLimits.MaxPlayers}"
                : "УЧАСТНИКИ";

            // Перерисовываем не только когда изменилось ЧИСЛО участников.
            //
            // Раньше здесь сравнивалось только количество, поэтому смена имени
            // или аватара у уже вошедшего игрока не появлялась: число-то не
            // менялось. Теперь сравниваем подпись всех данных — как только
            // кто-то сменил аватар, карточки перерисуются.
            string signature = BuildPlayersSignature(players);
            if (signature == _renderedPlayersSignature) return;            _renderedPlayersSignature = signature;
            _renderedPlayerCount = players.Count;

            spPlayers.Children.Clear();

            if (players.Count == 0)
            {
                spPlayers.Children.Add(new TextBlock
                {
                    Text = "Пока никого. Создайте отряд, чтобы играть вместе.",
                    Foreground = BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                return;
            }

            foreach (var p in players)
                spPlayers.Children.Add(BuildPlayerCard(p));
        }

        /// <summary>
        /// Пришёл чужой аватар. Путь в снимке не изменился, поэтому подпись
        /// участников прежняя — сбрасываем её и перерисовываем карточки сами,
        /// иначе фото появлялось только после перезахода на вкладку.
        /// </summary>
        private void OnPhotoUpdated(string ownerId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnPhotoUpdated(ownerId)));
                return;
            }

            _renderedPlayersSignature = null;
            UpdatePlayers();
            UpdatePhotoHint();
        }

        /// <summary>Строка «ждём фото…» у заголовка участников.</summary>
        private void UpdatePhotoHint()
        {
            if (txtPlayersHint == null) return;

            int waiting = 0;
            if (_session.IsActive)
            {
                foreach (var p in _session.GetPlayers())
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.AvatarPath)) continue;
                    if (string.Equals(p.Id, _session.MyPlayerId, StringComparison.Ordinal)) continue;
                    if (_session.IsPhotoMissing(p.Id)) continue;

                    string full = PhotoHelper.ResolvePhotoPath(p.AvatarPath);
                    if (string.IsNullOrEmpty(full) || !System.IO.File.Exists(full)) waiting++;
                }
            }

            bool show = waiting > 0;
            txtPlayersHint.Text = show
                ? (waiting == 1 ? "ждём фото…" : $"ждём фото ({waiting})…")
                : "";
            txtPlayersHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Повторный запрос недостающих аватаров раз в две секунды.
        ///
        /// Просить может только гость: у ведущего файлы уже на месте.
        /// </summary>
        private void OnPhotoRetryTick(object sender, EventArgs e)
        {
            try
            {
                if (!_session.IsActive || _session.IsHost)
                {
                    if (txtPlayersHint != null) txtPlayersHint.Visibility = Visibility.Collapsed;
                    return;
                }

                _session.RequestMissingPhotos(force: true);
                UpdatePhotoHint();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] повторный запрос фото: {ex.Message}");
            }
        }

        /// <summary>
        /// Подпись данных участников: имя, персонаж, класс, здоровье и аватар.
        /// По ней понимаем, нужно ли перерисовывать карточки.
        /// </summary>
        private static string BuildPlayersSignature(List<PlayerInfo> players)
        {
            if (players == null || players.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            foreach (var p in players)
            {
                sb.Append(p.Id).Append('|')
                  .Append(p.Name).Append('|')
                  .Append(p.CharacterName).Append('|')
                  .Append(p.CharacterClass).Append('|')
                  .Append(p.CharacterRace).Append('|')
                  .Append(p.Level).Append('|')
                  .Append(p.Hp).Append('|')
                  .Append(p.MaxHp).Append('|')
                  .Append(p.ArmorClass).Append('|')
                  .Append(p.Role).Append('|')
                  .Append(p.AvatarPath).Append('|')
                  .Append(p.State).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Карточка участника: аватар, персонаж, класс, полоса здоровья.</summary>
        /// <summary>
        /// Как показать участника: всегда имя персонажа.
        ///
        /// Личного профиля для отряда больше нет — и броски, и трекер, и карта
        /// называют игрока персонажем, поэтому и в отряде то же имя.
        /// </summary>
        private static string DisplayName(PlayerInfo p)
        {
            string character = (p?.CharacterName ?? "").Trim();
            if (character.Length > 0) return character;

            string name = (p?.Name ?? "").Trim();
            return name.Length > 0 ? name : "Игрок";
        }

        private Border BuildPlayerCard(PlayerInfo p)
        {
            var isMe = p.Id == _session.GetMyPlayerId();

            var card = new Border
            {
                Background = BrushRes(isMe ? "B.PanelHi" : "B.Slate", Color.FromRgb(0x8B, 0x95, 0xB4)),
                BorderBrush = BrushRes(isMe ? "B.Accent" : "B.Line", Color.FromRgb(0x8B, 0x95, 0xB4)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── Аватар ──
            //
            // Круг даёт Clip с эллипсом, а НЕ CornerRadius у Border: Border со
            // скруглением углов содержимое не обрезает, поэтому квадратная
            // картинка вылезала за края круга.
            var avatar = TryLoadAvatar(p.AvatarPath);
            var avatarHost = RoundAvatarHost(46, avatar, Initial(p),
                BrushRes("B.AccentEdge", Color.FromRgb(0x2A, 0x6A, 0x74)),
                BrushRes("B.Panel", Color.FromRgb(0x15, 0x1B, 0x2E)));
            avatarHost.Margin = new Thickness(0, 0, 10, 0);
            avatarHost.VerticalAlignment = VerticalAlignment.Top;

            Grid.SetColumn(avatarHost, 0);
            grid.Children.Add(avatarHost);

            // ── Текст ──
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            // Имя и метки. Раскладка — сетка, а не горизонтальная панель: длинное
            // имя раньше выдавливало метку «ВЕДУЩИЙ» за край карточки, и её съедало.
            // Имя занимает всю ширину, но обрезается с многоточием, метки — справа.
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = DisplayName(p),
                FontFamily = FontRes("F.Display"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushRes("B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            nameText.ToolTip = nameText.Text;
            Grid.SetColumn(nameText, 0);
            nameRow.Children.Add(nameText);

            var marks = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            // Метка ведущего: она объясняет, почему у кого-то есть скрытые броски.
            // «вы» пишем в той же метке, а не отдельным словом рядом: иначе на узкой
            // карточке оно уезжало за край
            if (p.Role == RoomRole.GameMaster)
            {
                marks.Children.Add(new Border
                {
                    Background = BrushRes("B.Accent", Color.FromRgb(0x5F, 0xD4, 0xDD)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = isMe ? "ВЕДУЩИЙ · ВЫ" : "ВЕДУЩИЙ",
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushRes("B.Void", Color.FromRgb(0x09, 0x0C, 0x16))
                    }
                });
            }
            else if (isMe)
            {
                marks.Children.Add(new TextBlock
                {
                    Text = "· вы",
                    FontSize = 10,
                    Foreground = BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }

            Grid.SetColumn(marks, 1);
            nameRow.Children.Add(marks);
            info.Children.Add(nameRow);

            // Класс и уровень
            string subtitle = BuildSubtitle(p);
            if (!string.IsNullOrEmpty(subtitle))
            {
                info.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11,
                    Foreground = BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            // ── Полоса здоровья ──
            info.Children.Add(BuildHealthRow(p));

            // Состояние связи: отвалившийся участник должен быть заметен
            if (p.State != PeerState.Online)
            {
                info.Children.Add(new TextBlock
                {
                    Text = p.State == PeerState.Disconnected ? "отключился" : "нет связи",
                    FontSize = 10,
                    FontStyle = FontStyles.Italic,
                    Foreground = BrushRes("B.Danger", Color.FromRgb(0xC0, 0x56, 0x4F)),
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }

            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            card.Child = grid;
            return card;
        }

        private UIElement BuildHealthRow(PlayerInfo p)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Цвет полосы зависит от доли здоровья: зелёный, жёлтый, красный
            double ratio = p.MaxHp > 0 ? Math.Max(0, Math.Min(1.0, (double)p.Hp / p.MaxHp)) : 0;
            string colorKey = ratio > 0.5 ? "B.Ok" : (ratio > 0.25 ? "B.Accent" : "B.Danger");

            var track = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = BrushRes("B.Panel", Color.FromRgb(0x15, 0x1B, 0x2E)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var fillHost = new Grid();
            var fill = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = BrushRes(colorKey, Color.FromRgb(0x8B, 0x95, 0xB4)),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            fillHost.Children.Add(fill);
            track.Child = fillHost;

            // Ширину полосы задаём после измерения, иначе ActualWidth ещё равен нулю
            track.SizeChanged += (s, e) =>
            {
                if (track.ActualWidth > 0) fill.Width = track.ActualWidth * ratio;
            };

            Grid.SetColumn(track, 0);
            row.Children.Add(track);

            var hpText = new TextBlock
            {
                Text = p.MaxHp > 0 ? $"{p.Hp}/{p.MaxHp}" : $"{p.Hp}",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushRes(colorKey, Color.FromRgb(0x8B, 0x95, 0xB4)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hpText, 1);
            row.Children.Add(hpText);

            return row;
        }

        private void UpdateFeed()
        {
            var feed = _session.GetFeed(300);

            // Картинки, которых нет в памяти после переподключения, догружаем
            // у ведущего по id. По одной за проход: два запроса подряд хост
            // отбрасывал из-за защиты от спама, и вторая гифка оставалась пустой.
            if (!_session.IsActive)
            {
                _requestedChatImages.Clear();
                _chatImageRequestedAt.Clear();
                _chatImageTimer.Stop();
            }

            FeedEntry missingImage = null;
            foreach (var entry in feed)
            {
                if (entry.Kind != FeedKind.Image) continue;
                if (!string.IsNullOrEmpty(entry.ImageBase64)) continue;
                if (string.IsNullOrWhiteSpace(entry.ImageId)) continue;

                if (!_requestedChatImages.Contains(entry.ImageId))
                {
                    missingImage = entry;
                    break;
                }

                // Ответ мог потеряться — через несколько секунд пробуем ещё раз
                if (_chatImageRequestedAt.TryGetValue(entry.ImageId, out var askedAt) &&
                    (DateTime.UtcNow - askedAt).TotalSeconds > 4)
                {
                    missingImage = entry;
                    break;
                }
            }

            if (missingImage != null)
            {
                _requestedChatImages.Add(missingImage.ImageId);
                _chatImageRequestedAt[missingImage.ImageId] = DateTime.UtcNow;
                _session.RequestChatImage(missingImage.ImageId);
                _chatImageTimer.Start();
            }
            else
            {
                bool stillWaiting = feed.Any(e => e.Kind == FeedKind.Image &&
                                                  string.IsNullOrEmpty(e.ImageBase64) &&
                                                  _requestedChatImages.Contains(e.ImageId ?? ""));
                if (stillWaiting) _chatImageTimer.Start();
                else _chatImageTimer.Stop();
            }

            // Когда WebView уже на экране, нативную ленту вообще не собираем:
            // иначе на каждое сообщение заново строились все карточки.
            if (ChatWeb.IsReady)
            {
                if (_renderedFeedCount < 0 || feed.Count < _renderedFeedCount)
                {
                    ChatWeb.SetFeed(feed);
                }
                else if (feed.Count > _renderedFeedCount)
                {
                    ChatWeb.AppendFeed(feed.Skip(_renderedFeedCount).ToList());
                }

                feedScroll.Visibility = Visibility.Collapsed;
                _renderedFeedCount = feed.Count;
                return;
            }

            if (feed.Count == _renderedFeedCount) return;

            // Дописываем только новые записи: пересборка всей ленты на каждое
            // сообщение тормозила чат и обновление хитов.
            int previousCount = _renderedFeedCount;
            _renderedFeedCount = feed.Count;

            if (previousCount > 0 && feed.Count > previousCount)
            {
                for (int i = previousCount; i < feed.Count; i++)
                    spFeed.Children.Add(BuildFeedRow(feed[i]));

                txtFeedHint.Text = $"записей: {feed.Count}";
                feedScroll.UpdateLayout();
                feedScroll.ScrollToEnd();
                return;
            }

            spFeed.Children.Clear();

            txtFeedHint.Text = feed.Count == 0
                ? "здесь появятся броски и сообщения"
                : $"записей: {feed.Count}";

            if (feed.Count == 0)
            {
                spFeed.Children.Add(new TextBlock
                {
                    Text = "Лента пуста. Броски кубов и сообщения отряда появятся здесь.",
                    Foreground = BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                return;
            }

            foreach (var entry in feed)
                spFeed.Children.Add(BuildFeedRow(entry));

            // Прокручиваем к последней записи: свежее должно быть на виду
            feedScroll.UpdateLayout();
            feedScroll.ScrollToEnd();
        }

        /// <summary>
        /// Круглая рамка с аватаром.
        ///
        /// Круг получается через Clip с эллипсом. Через CornerRadius у Border
        /// этого сделать нельзя: Border со скруглением углов содержимое не
        /// обрезает, и квадратная картинка вылезает за края круга.
        /// </summary>
        /// <param name="size">Диаметр круга.</param>
        /// <param name="image">Картинка либо null — тогда показывается буква.</param>
        /// <param name="letter">Буква-заглушка для случая без картинки.</param>
        private static Grid RoundAvatarHost(double size, BitmapSource image, string letter,
            Brush borderBrush, Brush backgroundBrush)
        {
            var host = new Grid { Width = size, Height = size };

            // Кольцо-рамка
            host.Children.Add(new Ellipse
            {
                Fill = backgroundBrush,
                Stroke = borderBrush,
                StrokeThickness = 1
            });

            if (image != null)
            {
                // Внутренний круг с обрезкой: на два пикселя меньше, чтобы не залезать на рамку
                double inner = size - 2;
                var clipHost = new Grid { Width = inner, Height = inner };
                clipHost.Clip = new EllipseGeometry(
                    new Point(inner / 2, inner / 2), inner / 2, inner / 2);

                clipHost.Children.Add(new Image
                {
                    Source = image,
                    Stretch = Stretch.UniformToFill
                });

                host.Children.Add(clipHost);
            }
            else
            {
                host.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(letter) ? "?" : letter,
                    FontFamily = new FontFamily("Cinzel, Segoe UI"),
                    FontSize = size * 0.42,
                    FontWeight = FontWeights.Bold,
                    Foreground = borderBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            return host;
        }

        /// <summary>Строка ленты. Броски — с крупным итогом, системные — серым курсивом.</summary>
        private UIElement BuildFeedRow(FeedEntry entry)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var content = new StackPanel();
            string time = entry.At.ToString("HH:mm");

            switch (entry.Kind)
            {
                case FeedKind.Roll:
                    // Бросок — самое важное в ленте, поэтому он заметно выделяется:
                    // цветной корешок слева, крупный итог в плашке и кубы чипами.
                    bool hidden = entry.Hidden;
                    var rollAccent = BrushRes(hidden ? "B.Arcane" : "B.Accent",
                        hidden ? Color.FromRgb(0xA9, 0x8B, 0xEC) : Color.FromRgb(0x5F, 0xD4, 0xDD));

                    card.Background = BrushRes("B.PanelHi", Color.FromRgb(0x1E, 0x25, 0x40));
                    card.BorderBrush = rollAccent;
                    card.BorderThickness = new Thickness(hidden ? 1 : 0, 0, 0, 0);
                    card.Padding = new Thickness(0, 10, 14, 10);

                    var rollRow = new Grid();

                    // Цветная полоса слева: сразу видно, что это бросок
                    var accentBar = new Border
                    {
                        Width = 4,
                        Background = rollAccent,
                        CornerRadius = new CornerRadius(2),
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Margin = new Thickness(0, 0, 12, 0)
                    };

                    var rollGrid = new Grid();
                    rollGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rollGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rollGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // Аватар бросавшего: настоящий круг, а не Border со скруглением
                    var authorImage = TryLoadAvatar(_authorAvatar(entry));
                    var authorHost = RoundAvatarHost(38, authorImage, FirstLetter(entry.AuthorName),
                        rollAccent, BrushRes("B.Panel", Color.FromRgb(0x15, 0x1B, 0x2E)));
                    authorHost.Margin = new Thickness(0, 0, 12, 0);
                    authorHost.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(authorHost, 0);
                    rollGrid.Children.Add(authorHost);

                    var rollLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

                    // Имя автора и время
                    var rollHead = new StackPanel { Orientation = Orientation.Horizontal };
                    rollHead.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(entry.AuthorName) ? "Кто-то" : entry.AuthorName,
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushRes("B.Frost", Color.FromRgb(0x4F, 0xB6, 0xAC))
                    });
                    if (hidden)
                    {
                        rollHead.Children.Add(new Border
                        {
                            Background = BrushRes("B.Arcane", Color.FromRgb(0xA9, 0x8B, 0xEC)),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 1, 6, 1),
                            Margin = new Thickness(8, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = "ТОЛЬКО ВЕДУЩЕМУ",
                                FontSize = 8,
                                FontWeight = FontWeights.Bold,
                                Foreground = BrushRes("B.Void", Color.FromRgb(0x09, 0x0C, 0x16))
                            }
                        });
                    }
                    rollHead.Children.Add(new TextBlock
                    {
                        Text = "  " + time,
                        FontSize = 10,
                        Foreground = BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    rollLeft.Children.Add(rollHead);

                    // Название броска — заметнее обычного текста
                    rollLeft.Children.Add(new TextBlock
                    {
                        Text = entry.Text,
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BrushRes("B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5)),
                        Margin = new Thickness(0, 3, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });

                    // Формула и выпавшие кубы отдельными чипами
                    var diceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
                    if (!string.IsNullOrWhiteSpace(entry.Formula))
                    {
                        diceRow.Children.Add(new Border
                        {
                            Background = BrushRes("B.Slate", Color.FromRgb(0x11, 0x17, 0x29)),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(7, 2, 7, 2),
                            Margin = new Thickness(0, 0, 6, 0),
                            Child = new TextBlock
                            {
                                Text = entry.Formula,
                                FontFamily = new FontFamily("Consolas"),
                                FontSize = 12,
                                FontWeight = FontWeights.Bold,
                                Foreground = rollAccent
                            }
                        });
                    }

                    if (entry.IndividualRolls != null && entry.IndividualRolls.Count > 0)
                    {
                        // Показываем не больше десяти кубов, иначе строка разрастётся
                        int shown = Math.Min(entry.IndividualRolls.Count, 10);
                        for (int i = 0; i < shown; i++)
                        {
                            int value = entry.IndividualRolls[i];
                            diceRow.Children.Add(new Border
                            {
                                Background = BrushRes("B.Panel", Color.FromRgb(0x15, 0x1B, 0x2E)),
                                BorderBrush = BrushRes("B.Line", Color.FromRgb(0x2B, 0x33, 0x52)),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(4),
                                Padding = new Thickness(7, 2, 7, 2),
                                Margin = new Thickness(0, 0, 4, 0),
                                Child = new TextBlock
                                {
                                    Text = value.ToString(),
                                    FontSize = 12,
                                    FontWeight = FontWeights.Bold,
                                    Foreground = BrushRes("B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5))
                                }
                            });
                        }

                        if (entry.IndividualRolls.Count > shown)
                        {
                            diceRow.Children.Add(new TextBlock
                            {
                                Text = $"ещё {entry.IndividualRolls.Count - shown}",
                                FontSize = 11,
                                Foreground = BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(2, 0, 0, 0)
                            });
                        }
                    }
                    if (diceRow.Children.Count > 0) rollLeft.Children.Add(diceRow);

                    Grid.SetColumn(rollLeft, 1);
                    rollGrid.Children.Add(rollLeft);

                    // Крупный итог в плашке — то, ради чего бросок и делали
                    var totalBox = new Border
                    {
                        Background = rollAccent,
                        CornerRadius = new CornerRadius(10),
                        MinWidth = 74,
                        Padding = new Thickness(12, 6, 12, 6),
                        Margin = new Thickness(14, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = entry.Total.ToString(),
                            FontFamily = FontRes("F.Display"),
                            FontSize = 30,
                            FontWeight = FontWeights.Bold,
                            Foreground = BrushRes("B.Void", Color.FromRgb(0x09, 0x0C, 0x16)),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    Grid.SetColumn(totalBox, 2);
                    rollGrid.Children.Add(totalBox);

                    rollRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rollRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(accentBar, 0);
                    Grid.SetColumn(rollGrid, 1);
                    rollRow.Children.Add(accentBar);
                    rollRow.Children.Add(rollGrid);

                    content.Children.Add(rollRow);
                    break;

                case FeedKind.System:
                    // Системные события — серым: их не должно быть видно как реплики игроков
                    card.Background = Brushes.Transparent;
                    card.Padding = new Thickness(12, 2, 12, 2);
                    content.Children.Add(new TextBlock
                    {
                        Text = $"— {entry.Text} · {time}",
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Foreground = BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                        TextWrapping = TextWrapping.Wrap
                    });
                    break;

                case FeedKind.Image:
                    // Запасной нативный вид: если WebView2 не поднялся,
                    // не пытаемся рисовать гифку в WPF — только подпись.
                    card.Background = BrushRes("B.PanelHi", Color.FromRgb(0x1E, 0x25, 0x40));
                    card.Child = new TextBlock
                    {
                        Text = $"{entry.AuthorName} · {(string.Equals(entry.ImageExtension, ".gif", StringComparison.OrdinalIgnoreCase) ? "гифка" : "картинка")}" +
                               (string.IsNullOrWhiteSpace(entry.Text) ? "" : " · " + entry.Text) + $" · {time}",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = BrushRes("B.Muted", Color.FromRgb(0x8B, 0x95, 0xB4))
                    };
                    return card;

                default: // чат
                    card.Background = BrushRes("B.PanelHi", Color.FromRgb(0x1E, 0x25, 0x40));
                    var head = new StackPanel { Orientation = Orientation.Horizontal };
                    head.Children.Add(new TextBlock
                    {
                        Text = entry.AuthorName,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushRes("B.Accent", Color.FromRgb(0x5F, 0xD4, 0xDD))
                    });
                    head.Children.Add(new TextBlock
                    {
                        Text = "  " + time,
                        FontSize = 10,
                        Foreground = BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    content.Children.Add(head);
                    content.Children.Add(new TextBlock
                    {
                        Text = entry.Text,
                        FontSize = 12,
                        Foreground = BrushRes("B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                    break;
            }

            card.Child = content;
            return card;
        }

        #endregion

        /// <summary>Первая буква имени для кружка-заглушки.</summary>
        private static string FirstLetter(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            return name.Trim().Substring(0, 1).ToUpperInvariant();
        }

        /// <summary>
        /// Аватар автора записи: ищем участника по идентификатору.
        /// У хоста и гостей он свой, а у системных записей автора нет.
        /// </summary>
        private string _authorAvatar(FeedEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.AuthorId)) return null;

            var player = _session.GetPlayers()
                .FirstOrDefault(p => string.Equals(p.Id, entry.AuthorId, StringComparison.Ordinal));

            return player?.AvatarPath;
        }

        #region === ДЕЙСТВИЯ ===

        private void BtnSendChat_Click(object sender, RoutedEventArgs e) => SendChat();

        private void BtnAttachImage_Click(object sender, RoutedEventArgs e)
        {
            if (!_session.IsActive)
            {
                ShowWindowToast("Сначала подключитесь к отряду.");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Картинка или гифка в чат",
                Filter = "Картинки (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|Все файлы (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var info = new FileInfo(dialog.FileName);
                if (!info.Exists || info.Length <= 0)
                {
                    ShowWindowToast("Файл пустой.");
                    return;
                }
                if (info.Length > NetLimits.ChatImageMaxBytes)
                {
                    ShowWindowToast($"Файл больше {NetLimits.ChatImageMaxBytes / (1024 * 1024)} МБ — уменьшите картинку.");
                    return;
                }

                string extension = info.Extension.ToLowerInvariant();
                byte[] bytes = File.ReadAllBytes(dialog.FileName);
                string caption = txtChatInput.Text ?? "";
                if (!_session.PublishChatImage(caption, bytes, extension, info.Name))
                {
                    ShowWindowToast("Не удалось отправить картинку.");
                    return;
                }

                txtChatInput.Clear();
                ShowWindowToast("Картинка отправлена.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] картинка в чат: {ex}");
                ShowWindowToast("Ошибка: " + ex.Message);
            }
        }

        private void TxtChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                SendChat();
                e.Handled = true;
            }
        }

        private void SendChat()
        {
            string text = txtChatInput.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!_session.IsActive) return;

            _session.PublishChat(text);
            txtChatInput.Clear();
            txtChatInput.Focus();
        }

        /// <summary>
        /// Копирует готовое приглашение: ключ и ОБА адреса — внешний и локальный.
        ///
        /// Одного локального адреса другу из интернета мало, а одного внешнего
        /// мало тому, кто сидит в той же сети (NAT не всегда пускает обратно).
        /// Поэтому в сообщении есть оба, с подписями — и его можно переслать целиком:
        /// вкладка «Отряд» разберёт такую строку при вставке.
        /// </summary>
        private async void BtnCopyKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await EnsurePublicAddressAsync();

                string local = $"{_session.HostAddress}:{_session.Port}";
                bool hasPublic = !string.IsNullOrEmpty(_session.PublicAddress);
                string remote = hasPublic ? $"{_session.PublicAddress}:{_session.Port}" : "";

                var text = new StringBuilder();
                text.AppendLine("Ключ: " + RoomKey.Pretty(_session.Key));
                if (hasPublic) text.AppendLine("Адрес для интернета: " + remote);
                text.AppendLine("Адрес в одной сети: " + local);

                Clipboard.SetText(text.ToString().TrimEnd());

                txtFeedHint.Text = hasPublic
                    ? "скопированы ключ, внешний и локальный адрес"
                    : "скопированы ключ и локальный адрес: внешний не определился";
                ShowWindowToast(hasPublic
                    ? "Скопировано: ключ и оба адреса — можно отправлять другу"
                    : "Скопировано: ключ и локальный адрес (внешний не определился)");
            }
            catch (Exception ex)
            {
                txtFeedHint.Text = "не удалось скопировать: " + ex.Message;
            }
        }

        /// <summary>
        /// Копирует только адрес и порт одной строкой: игрок вставляет её
        /// в поле подключения целиком — форма умеет разбирать такой вид.
        /// </summary>
        private async void BtnCopyAddress_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Сначала пробуем узнать внешний адрес: именно он нужен другу
                // из интернета, локальный работает только в своей сети
                await EnsurePublicAddressAsync();

                string address = string.IsNullOrEmpty(_session.PublicAddress)
                    ? _session.HostAddress
                    : _session.PublicAddress;

                string payload = $"{address}:{_session.Port}";
                Clipboard.SetText(payload);

                txtFeedHint.Text = "адрес скопирован: " + payload;
                ShowWindowToast("Скопировано: " + payload);
            }
            catch (Exception ex)
            {
                txtFeedHint.Text = "не удалось скопировать: " + ex.Message;
            }
        }

        /// <summary>Обновляет внешний адрес: у провайдера он может меняться между сессиями.</summary>
        private async void BtnRefreshIp_Click(object sender, RoutedEventArgs e)
        {
            btnRefreshIp.IsEnabled = false;
            txtPublicAddress.Text = "узнаём…";
            try
            {
                await _session.RequestPublicAddressAsync();

                if (string.IsNullOrEmpty(_session.PublicAddress))
                    ShowWindowToast("Внешний адрес не получен — играйте по локальному", true);
                else
                    ShowWindowToast("Внешний адрес: " + _session.PublicAddress);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] обновление внешнего адреса: {ex.Message}");
            }
            finally
            {
                UpdateHeader();
            }
        }

        /// <summary>
        /// Показывает поля подключения прямо во вкладке.
        ///
        /// Раньше кнопка открывала панель в главном окне, но при открытом листе
        /// персонажа главное окно скрыто — нажатие выглядело как «не работает».
        /// </summary>
        private void BtnJoinRoomTab_Click(object sender, RoutedEventArgs e)
        {
            if (_session.IsActive)
            {
                ShowWindowToast("Вы уже в отряде. Сначала выйдите из комнаты.");
                return;
            }

            brJoinPanel.Visibility = Visibility.Visible;
            LoadRecentRooms();
            SetJoinHint("Можно вставить всю строку от ведущего — ключ и адрес разберутся сами", false);
            txtJoinHost.Focus();
        }

        /// <summary>
        /// Заполняет список комнат, к которым уже подключались.
        ///
        /// У гостя нет сохранённой комнаты на диске, поэтому запоминаем адрес
        /// и ключ отдельно: иначе каждый раз приходится вписывать их заново.
        /// </summary>
        private void LoadRecentRooms()
        {
            if (cmbRecentRooms == null) return;

            _recentRoomsLoading = true;
            try
            {
                var items = ClientRoomHistory.Load();
                cmbRecentRooms.Items.Clear();
                foreach (var entry in items) cmbRecentRooms.Items.Add(entry);
                cmbRecentRooms.SelectedIndex = -1;

                bool any = items.Count > 0;
                if (spRecentRooms != null) spRecentRooms.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] история комнат: {ex.Message}");
            }
            finally
            {
                _recentRoomsLoading = false;
            }
        }

        private void CmbRecentRooms_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_recentRoomsLoading) return;
            if (!(cmbRecentRooms.SelectedItem is ClientRoomEntry entry)) return;

            txtJoinHost.Text = entry.Address;
            string key = ClientRoomHistory.GetKey(entry);
            txtJoinKey.Text = string.IsNullOrEmpty(key) ? "" : RoomKey.Pretty(key);

            SetJoinHint(string.IsNullOrEmpty(key)
                ? "Ключ этой комнаты не читается — введите его заново"
                : "Комната выбрана — нажмите «Подключиться»", string.IsNullOrEmpty(key));
            txtJoinKey.Focus();
        }

        private void BtnForgetRoom_Click(object sender, RoutedEventArgs e)
        {
            if (!(cmbRecentRooms.SelectedItem is ClientRoomEntry entry))
            {
                SetJoinHint("Сначала выберите комнату в списке «Недавние»", true);
                return;
            }

            ClientRoomHistory.Forget(entry.Id);
            LoadRecentRooms();
            SetJoinHint("Комната убрана из истории", false);
        }

        private void BtnJoinCancel_Click(object sender, RoutedEventArgs e) =>
            brJoinPanel.Visibility = Visibility.Collapsed;

        private void JoinField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                BtnJoinConnect_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                brJoinPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void SetJoinHint(string text, bool isProblem)
        {
            txtJoinHint.Text = text;
            txtJoinHint.Foreground = isProblem
                ? BrushRes("B.Danger", Color.FromRgb(0xC0, 0x56, 0x4F))
                : BrushRes("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4));
        }

        // Адрес вида «192.168.1.5:47777» или «my-host.example:47777»
        private static readonly Regex InviteAddress = new Regex(
            @"(\d{1,3}(?:\.\d{1,3}){3}|[A-Za-z0-9][A-Za-z0-9.-]*):(\d{1,5})",
            RegexOptions.Compiled);

        // Ключ комнаты: три группы по четыре символа; букв I и O в ключе не бывает
        private static readonly Regex InviteKey = new Regex(
            @"[A-HJ-NP-Z2-9]{4}-?[A-HJ-NP-Z2-9]{4}-?[A-HJ-NP-Z2-9]{4}",
            RegexOptions.Compiled);

        /// <summary>
        /// Достаёт адрес, порт и ключ из того, что ввели или вставили.
        ///
        /// Ведущий присылает всё одной строкой («Ключ: … Адрес для интернета: …»),
        /// поэтому разбираем текст целиком, а поля только подсказывают, где искать.
        /// </summary>
        private static bool TryReadInvite(string allText, string hostField, string keyField,
            out string host, out int port, out string key, out string problem)
        {
            host = "";
            port = NetVersions.DefaultPort;
            key = "";
            problem = "";

            string address = (hostField ?? "").Trim();
            var addressMatch = InviteAddress.Match(address);
            if (!addressMatch.Success) addressMatch = InviteAddress.Match(allText ?? "");

            if (addressMatch.Success)
            {
                host = addressMatch.Groups[1].Value;
                if (int.TryParse(addressMatch.Groups[2].Value, out int parsed) && parsed > 0 && parsed <= 65535)
                    port = parsed;
            }
            else if (address.Length > 0 && !address.Contains(":") && !address.Contains(" "))
            {
                // Ввели один адрес без порта — берём порт по умолчанию
                host = address;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                problem = "Не вижу адрес. Нужен вид 192.168.1.5:47777 — можно вставить всю строку от ведущего.";
                return false;
            }

            var keyMatch = InviteKey.Match(keyField ?? "");
            if (!keyMatch.Success) keyMatch = InviteKey.Match(allText ?? "");

            if (keyMatch.Success) key = RoomKey.Normalize(keyMatch.Value);
            else if (RoomKey.LooksValid(keyField)) key = RoomKey.Normalize(keyField);

            if (!RoomKey.LooksValid(key))
            {
                problem = "Не вижу ключ. Он выглядит так: K7M2-XP9Q-RT4W";
                return false;
            }

            return true;
        }

        /// <summary>Подключение по адресу и ключу — то же, что делает главное окно.</summary>
        private void BtnJoinConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string allText = (txtJoinHost.Text ?? "") + " " + (txtJoinKey.Text ?? "");

                if (!TryReadInvite(allText, txtJoinHost.Text, txtJoinKey.Text,
                        out string host, out int port, out string key, out string problem))
                {
                    SetJoinHint(problem, true);
                    return;
                }

                var me = DmProfileService.BuildPartyPlayer(CharacterId);

                if (!_session.JoinRoom(host, port, key, me, out string error))
                {
                    SetJoinHint("Не удалось подключиться", true);
                    MessageDialog.Error(Window.GetWindow(this), error, "Не удалось подключиться");
                    return;
                }

                brJoinPanel.Visibility = Visibility.Collapsed;
                txtJoinHost.Clear();
                txtJoinKey.Clear();
                RefreshAll();
                ShowWindowToast("Подключено: " + _session.StatusText);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] подключение из вкладки: {ex}");
                SetJoinHint("Ошибка: " + ex.Message, true);
            }
        }

        /// <summary>
        /// Вставка целой строки от ведущего: раскладываем её по полям.
        /// Если в тексте нет ключа или адреса, вставка работает как обычно.
        /// </summary>
        private void JoinField_Paste(object sender, DataObjectPastingEventArgs e)
        {
            try
            {
                if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) return;

                string text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
                if (string.IsNullOrWhiteSpace(text)) return;

                var keyMatch = InviteKey.Match(text);
                var addressMatch = InviteAddress.Match(text);
                if (!keyMatch.Success || !addressMatch.Success) return;

                e.CancelCommand();

                txtJoinKey.Text = RoomKey.Pretty(keyMatch.Value);
                txtJoinHost.Text = addressMatch.Groups[1].Value + ":" + addressMatch.Groups[2].Value;
                SetJoinHint("Разобрал: адрес " + txtJoinHost.Text + ", ключ " + txtJoinKey.Text, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] вставка строки подключения: {ex.Message}");
            }
        }

        /// <summary>
        /// Узнаёт внешний адрес, если он ещё не известен: без него другу
        /// из интернета нечего вписывать, а копировать нужно сразу готовое.
        /// </summary>
        private async Task EnsurePublicAddressAsync()
        {
            if (!_session.IsHost) return;
            if (!string.IsNullOrEmpty(_session.PublicAddress)) return;

            txtFeedHint.Text = "узнаём внешний адрес…";
            try
            {
                await _session.RequestPublicAddressAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] внешний адрес: {ex.Message}");
            }

            UpdateHeader();
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_audioVolumeReady) return;
            Services.AudioSettings.SetMaster(e.NewValue / 100.0);
        }

        private void BtnLeaveRoom_Click(object sender, RoutedEventArgs e)
        {
            if (!_session.IsActive) return;

            bool confirmed = MessageDialog.Confirm(Window.GetWindow(this),
                _session.IsHost
                    ? "Выйти из отряда? Комната сохранится на диске, и вы сможете продолжить её позже."
                    : "Выйти из отряда?",
                "Выход из отряда", "Выйти", "Остаться", danger: true);

            if (!confirmed) return;

            _session.Leave();
            _renderedFeedCount = -1;
            _renderedPlayerCount = -1;
            RefreshAll();
        }

        #endregion

        #region === ВСПОМОГАТЕЛЬНОЕ ===

        private BitmapSource TryLoadAvatar(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;

            try
            {
                string full = PhotoHelper.ResolvePhotoPath(relativePath);
                if (string.IsNullOrEmpty(full) || !System.IO.File.Exists(full)) return null;

                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(full, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 92;   // маленький размер: экономит память в списке
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static string Initial(PlayerInfo p)
        {
            string source = !string.IsNullOrWhiteSpace(p.CharacterName) ? p.CharacterName
                          : !string.IsNullOrWhiteSpace(p.Name) ? p.Name : "?";
            return source.Substring(0, 1).ToUpperInvariant();
        }

        /// <summary>Строка вида «Воин 3 · Полуорк · КД 16».</summary>
        private static string BuildSubtitle(PlayerInfo p)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(p.CharacterClass))
                parts.Add(p.Level > 0 ? $"{p.CharacterClass} {p.Level}" : p.CharacterClass);
            if (!string.IsNullOrWhiteSpace(p.CharacterRace))
                parts.Add(p.CharacterRace);
            if (p.ArmorClass > 0)
                parts.Add($"КД {p.ArmorClass}");

            return string.Join(" · ", parts);
        }

        #endregion
    }
}
