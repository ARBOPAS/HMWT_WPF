using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        /// <summary>
        /// Периодическая сверка своих данных с комнатой.
        ///
        /// Имя, аватар и здоровье меняются уже после входа, и без сверки остальные
        /// видели первоначальные значения. Событие об изменении здесь не подходит:
        /// правки идут прямо в базе, а не через наш код, поэтому проверяем по времени.
        /// Отправка происходит только при реальном изменении, так что трафик мизерный.
        /// </summary>
        private DispatcherTimer _selfSyncTimer;
        private static readonly TimeSpan SelfSyncInterval = TimeSpan.FromSeconds(10);

        public RoomView()
        {
            InitializeComponent();
            _ui = Dispatcher;

            _session.StateChanged += OnSessionStateChanged;
            _session.FeedReceived += OnFeedReceived;
            _session.SnapshotReceived += OnSnapshotReceived;
            _session.PeerDeparted += OnPeerDeparted;

            Loaded += (s, e) =>
            {
                RefreshAll();
                StartSelfSync();
                // Обновляемся ещё раз, когда WPF закончит раскладку: комната могла
                // измениться, пока вкладка была не открыта
                Dispatcher.BeginInvoke(new Action(RefreshAll), DispatcherPriority.Loaded);
            };
            Unloaded += (s, e) =>
            {
                _session.StateChanged -= OnSessionStateChanged;
                _session.FeedReceived -= OnFeedReceived;
                _session.SnapshotReceived -= OnSnapshotReceived;
                _session.PeerDeparted -= OnPeerDeparted;
                StopSelfSync();
            };

            // Обработчики кнопок объявлены в разметке (Click="...") — так их видно
            // в XAML и проверяет тест. Здесь ничего дополнительно вешать не нужно.
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

        private void OnSessionStateChanged() => _ui.BeginInvoke(new Action(RefreshAll));

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

        private void OnSnapshotReceived(RoomSnapshot snapshot) => _ui.BeginInvoke(new Action(RefreshAll));

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
                if (_departuresAnnounced.Contains(key)) return;
                _departuresAnnounced.Add(key);

                string tail = string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})";
                string message = $"Участник {name} отключился{tail}";

                ShowWindowToast(message);
                txtFeedHint.Text = message;

                RefreshAll();
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

        /// <summary>
        /// Запускает периодическую сверку с комнатой.
        ///
        /// Работает не только по событиям: если другой игрок сменил аватар или
        /// здоровье, событие об этом может не прийти, поэтому состояние
        /// запрашивается само. Интервал настраивается во вкладке настроек.
        /// </summary>
        private void StartSelfSync()
        {
            if (_selfSyncTimer != null) return;

            var interval = TimeSpan.FromSeconds(SettingsView.RefreshSeconds);

            _selfSyncTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = interval
            };
            _selfSyncTimer.Tick += OnSelfSyncTick;
            _selfSyncTimer.Start();

            // Сверяемся сразу: аватар и имя могли появиться до открытия вкладки
            OnSelfSyncTick(null, EventArgs.Empty);
        }

        private void StopSelfSync()
        {
            if (_selfSyncTimer == null) return;
            _selfSyncTimer.Stop();
            _selfSyncTimer.Tick -= OnSelfSyncTick;
            _selfSyncTimer = null;
        }

        /// <summary>
        /// Периодическая сверка: отправляем свои данные, если они изменились,
        /// и перечитываем чужие — так изменения видны без событий.
        /// </summary>
        private void OnSelfSyncTick(object sender, EventArgs e)
        {
            try
            {
                var interval = _selfSyncTimer?.Interval ?? TimeSpan.FromSeconds(10);

                // Свои данные наверх: если аватар или имя поменялись — уйдут другим
                _session.RefreshMyCharacterIfStale(interval);

                // Чужие данные заново: снимок мог не прийти после чужого изменения
                _session.RequestRefresh();

                // Лента и участники перечитываются целиком, поэтому новые записи
                // появятся даже если событие о них потерялось
                RefreshAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] сверка данных: {ex.Message}");
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

            txtChatInput.IsEnabled = active;
            btnSendChat.IsEnabled = active;
            btnSendChat.Opacity = active ? 1.0 : 0.45;
            txtChatInput.ToolTip = active ? "Enter — отправить" : "Сначала подключитесь к отряду";
        }

        private void UpdatePlayers()
        {
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
        /// Как показать участника: именем персонажа или учётной записью.
        ///
        /// По умолчанию — имя персонажа из листа (настройка «Показывать имя
        /// учётной записи Windows» только переключает этот способ). Если персонаж
        /// не открыт и имени нет, показываем учётную запись, а не «Без имени».
        /// </summary>
        private static string DisplayName(PlayerInfo p)
        {
            string character = (p?.CharacterName ?? "").Trim();
            string account = (p?.Name ?? "").Trim();

            bool preferAccount = false;
            try { preferAccount = Views.SettingsView.ShowWindowsName; } catch { }

            if (preferAccount || character.Length == 0)
                return account.Length > 0 ? account : "Без имени";

            return character;
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

            // ПКМ по своей карточке — редактирование профиля.
            // Ведущему это нужно, потому что у него может не быть персонажа,
            // а имя и аватар в отряде всё равно должны быть.
            if (isMe)
            {
                card.ToolTip = "Правый клик — изменить профиль";
                card.MouseRightButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    OpenProfileEditor();
                };
            }

            card.Child = grid;
            return card;
        }

        /// <summary>
        /// Показывает редактор профиля поверх ВСЕГО окна персонажа.
        ///
        /// Раньше редактор клался в корневой Grid вкладки «Отряд», а сама вкладка
        /// занимает лишь одну строку окна — под шапкой персонажа. Из-за этого
        /// окно профиля всплывало где-то сверху, а вкладка делилась пополам.
        ///
        /// Теперь редактор ставится в корневой Grid окна и растягивается на все
        /// его строки и столбцы, то есть перекрывает окно целиком — так же,
        /// как это делает окно подтверждения.
        /// </summary>
        private void OpenProfileEditor()
        {
            try
            {
                var window = Window.GetWindow(this);
                if (window == null)
                {
                    Debug.WriteLine("[RoomView] окно не найдено для редактора профиля");
                    return;
                }

                // Корневая панель окна: редактор должен перекрыть и шапку тоже
                var host = window.Content as System.Windows.Controls.Panel;
                if (host == null)
                {
                    Debug.WriteLine("[RoomView] у окна нет корневой панели");
                    return;
                }

                var editor = new Controls.DmProfileEditor();
                System.Windows.Controls.Panel.SetZIndex(editor, 400);
                editor.Saved = () =>
                {
                    // Профиль изменился: сразу отправляем его в комнату
                    var me = Services.DmProfileService.BuildPlayer(Environment.UserName);
                    _session.UpdateMyCharacter(me);
                    _renderedPlayerCount = -1;
                    RefreshAll();
                };

                // Растягиваем на всё окно: у корневого Grid окна персонажа
                // две колонки (рейка вкладок + содержимое) и три строки
                if (host is Grid grid)
                {
                    if (grid.RowDefinitions.Count > 0) Grid.SetRowSpan(editor, grid.RowDefinitions.Count);
                    if (grid.ColumnDefinitions.Count > 0)
                    {
                        Grid.SetColumn(editor, 0);
                        Grid.SetColumnSpan(editor, grid.ColumnDefinitions.Count);
                    }
                }

                host.Children.Add(editor);
                editor.Focus();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoomView] редактор профиля: {ex.Message}");
            }
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

            if (feed.Count == _renderedFeedCount) return;
            _renderedFeedCount = feed.Count;

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

        private void BtnCopyKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Копируем ключ и адрес вместе: игроку нужно и то, и другое.
                // Если известен внешний адрес — даём его: он подходит и для
                // интернета, а локальный в этом случае бесполезен.
                string address = string.IsNullOrEmpty(_session.PublicAddress)
                    ? _session.HostAddress
                    : _session.PublicAddress;

                string payload = RoomKey.Pretty(_session.Key) + "  " + address + ":" + _session.Port;
                Clipboard.SetText(payload);
                txtFeedHint.Text = "ключ и адрес скопированы";
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
        private void BtnCopyAddress_Click(object sender, RoutedEventArgs e)
        {
            try
            {
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
