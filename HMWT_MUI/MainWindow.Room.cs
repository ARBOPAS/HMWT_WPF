using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMWT_MUI.Helpers;
using HMWT_MUI.Net;
using HMWT_MUI.Windows;

namespace HMWT_MUI
{
    /// <summary>
    /// Главное окно: список персонажей и панель создания/подключения отряда.
    ///
    /// Комната живёт на уровне приложения (RoomSession), поэтому её состояние
    /// видно и здесь, и во вкладке окна персонажа.
    /// </summary>
    public partial class MainWindow : Window
    {
        #region === ОТРЯД: СОСТОЯНИЕ ===

        private readonly RoomSession _room = RoomSession.Instance;

        /// <summary>Внешний адрес, полученный из интернета. Пусто, пока не спрашивали.</summary>
        private string _publicAddress = "";

        /// <summary>Общий клиент для запросов: создавать новый на каждый запрос расточительно.</summary>
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        /// <summary>Открывает панель отряда. Вызывается кнопкой и из окна персонажа.</summary>
        public void OpenRoomPanel()
        {
            RefreshRoomPanel();
            RoomPanel.Visibility = Visibility.Visible;
        }

        private void CloseRoomPanel() => RoomPanel.Visibility = Visibility.Collapsed;

        private void RoomButton_Click(object sender, RoutedEventArgs e)
        {
            if (RoomPanel.Visibility == Visibility.Visible) CloseRoomPanel();
            else OpenRoomPanel();
        }

        private void BtnCloseRoomPanel_Click(object sender, RoutedEventArgs e) => CloseRoomPanel();

        /// <summary>Клик по затемнению закрывает панель (но не клик по самой карточке).</summary>
        private void RoomPanel_BackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) CloseRoomPanel();
        }

        #endregion

        #region === ОТРЯД: АДРЕСА ===

        /// <summary>
        /// Внешний адрес нужен тем, кто играет через интернет: локальный
        /// (192.168.x.x) работает только внутри домашней сети.
        ///
        /// Сам запрос живёт в <see cref="NetworkHelper"/> — им пользуется и
        /// вкладка «Отряд», чтобы источник адреса был один.
        /// </summary>
        private async Task<string> FetchPublicAddressAsync()
        {
            string address = await NetworkHelper.GetPublicAddressAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(address))
            {
                // Адрес узнали — отдаём его сессии, чтобы его видела вкладка «Отряд»
                _room.SetPublicAddress(address);
                Debug.WriteLine($"[MainWindow] внешний адрес: {address}");
            }
            return address;
        }

        private async void BtnRefreshIp_Click(object sender, RoutedEventArgs e)
        {
            btnRefreshIp.IsEnabled = false;
            btnRefreshIp.Content = "Спрашиваю…";
            try
            {
                string address = await FetchPublicAddressAsync();
                _publicAddress = address;

                if (string.IsNullOrEmpty(address))
                {
                    txtPublicAddress.Text = "Не удалось узнать внешний адрес — нет доступа в интернет. " +
                                            "Он всё равно показан на роутере в разделе WAN.";
                    txtPublicAddress.Foreground = (Brush)FindResource("B.Danger");
                    txtPublicAddress.Visibility = Visibility.Visible;
                }
                else
                {
                    ShowPublicAddress(address);
                }
            }
            finally
            {
                btnRefreshIp.IsEnabled = true;
                btnRefreshIp.Content = "Узнать для интернета";
            }
        }

        private void ShowPublicAddress(string address)
        {
            int port = ParsePort(txtNewRoomPort?.Text);
            txtPublicAddress.Text = $"Для друзей из интернета: {address}:{port}";
            txtPublicAddress.Foreground = (Brush)FindResource("B.Frost");
            txtPublicAddress.Visibility = Visibility.Visible;

            // Проверять доступность имеет смысл только у поднятой комнаты
            btnCheckPort.Visibility = _room.IsActive ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnCheckPort_Click(object sender, RoutedEventArgs e)
            => _ = CheckPortReachableAsync(_room.Port > 0 ? _room.Port : ParsePort(txtNewRoomPort?.Text), _publicAddress);

        /// <summary>
        /// Проверяет, отвечает ли комната снаружи: проброс порта на роутере —
        /// самая частая причина, по которой друзья не могут подключиться.
        /// </summary>
        private async Task CheckPortReachableAsync(int port, string publicAddress)
        {
            if (string.IsNullOrEmpty(publicAddress))
            {
                MessageDialog.Info(this,
                    "Сначала нажмите «Узнать для интернета», чтобы приложение узнало ваш внешний адрес.",
                    "Проверка доступности");
                return;
            }

            btnRefreshIp.IsEnabled = false;
            string oldContent = btnRefreshIp.Content?.ToString();
            btnRefreshIp.Content = "Проверяю…";

            try
            {
                bool external = await CanConnectAsync(_publicAddress, port).ConfigureAwait(true);
                bool local = await CanConnectAsync("127.0.0.1", port).ConfigureAwait(true);

                if (external)
                {
                    MessageDialog.Info(this,
                        $"Порт {port} доступен снаружи по адресу {_publicAddress}:{port}.\n\n" +
                        "Друзья могут подключаться: передайте им ключ комнаты и этот адрес.\n\n" +
                        BuildNetworkDiagnostics(port),
                        "Проверка доступности");
                }
                else if (local)
                {
                    MessageDialog.Info(this,
                        $"Внутри вашей сети комната отвечает, а снаружи порт {port} закрыт.\n" +
                        "Значит, наружу компьютер не пускают: чаще всего мешает роутер.\n\n" +
                        BuildNetworkDiagnostics(port),
                        "Порт закрыт снаружи");
                }
                else
                {
                    MessageDialog.Error(this,
                        $"Комната не отвечает даже на этом компьютере по порту {port}.\n\n" +
                        "Скорее всего она не создана. Нажмите «Создать» и попробуйте снова.",
                        "Комната не отвечает");
                }
            }
            finally
            {
                btnRefreshIp.IsEnabled = true;
                btnRefreshIp.Content = oldContent ?? "Узнать для интернета";
            }
        }

        private static async Task<bool> CanConnectAsync(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, port);
                var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                if (finished != connect) return false;
                await connect.ConfigureAwait(false);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Разбор доступности комнаты: что где открыто и что делать, если снаружи
        /// не пускает.
        ///
        /// Главная мысль, которую должен понять ведущий: правило брандмауэра
        /// Windows открывает порт только на самом компьютере. Наружу соединение
        /// пускает роутер, и без проброса порта (или UPnP) снаружи не зайти.
        /// </summary>
        private string BuildNetworkDiagnostics(int port)
        {
            var sb = new System.Text.StringBuilder();

            // ── что есть сейчас ──
            sb.AppendLine("ЧТО СЕЙЧАС");
            sb.AppendLine($"• этот компьютер: {DescribeLocalAddresses()}");
            sb.AppendLine($"• порт комнаты: {port}");

            bool firewalled;
            try { firewalled = FirewallHelper.RuleExists(); }
            catch { firewalled = false; }

            sb.AppendLine(firewalled
                ? "• брандмауэр Windows: правило для приложения есть"
                : "• брандмауэр Windows: правила нет — создайте комнату заново и разрешите доступ");

            sb.AppendLine(string.IsNullOrEmpty(_publicAddress)
                ? "• внешний адрес: неизвестен (нажмите «Узнать для интернета»)"
                : $"• внешний адрес: {_publicAddress}:{port}");

            // UPnP: спрашиваем роутер, готов ли он открывать порты сам.
            // Запрос читающий и короткий — ничего не меняет и не подвешивает окно.
            var upnp = UpnpProbe.Probe(2500);
            sb.AppendLine("• роутер и UPnP: " + upnp.Description);

            // ── что нужно сделать ──
            sb.AppendLine();
            sb.AppendLine("ЧТО НУЖНО ДЛЯ ИГРЫ ЧЕРЕЗ ИНТЕРНЕТ");
            sb.AppendLine("1. Проброс порта на роутере ведущего: TCP " + port +
                          $" → {_room.HostAddress ?? "локальный адрес этого компьютера"}.");
            sb.AppendLine("   Порт нужен только ведущему. Остальным открывать ничего не надо —");
            sb.AppendLine("   они подключаются сами.");
            sb.AppendLine("2. Если в роутере включён UPnP, проброс можно не настраивать вручную." +
                          (upnp.Responded
                              ? " Ваш роутер на UPnP отвечает — попробуйте включить его в настройках."
                              : " Ваш роутер на UPnP не отвечает — настраивайте проброс вручную."));
            sb.AppendLine();
            sb.AppendLine("ЕСЛИ НЕ ПОЛУЧАЕТСЯ");
            sb.AppendLine("• Сравните внешний адрес выше с адресом WAN в настройках роутера.");
            sb.AppendLine("  Если они РАЗНЫЕ — вы за NAT провайдера (CGNAT), и проброс порта");
            sb.AppendLine("  невозможен в принципе: адрес принадлежит провайдеру, а не вам.");
            sb.AppendLine("• Тогда играйте через виртуальную локальную сеть: Radmin VPN,");
            sb.AppendLine("  Tailscale или Hamachi. Обе стороны ставят программу, получают");
            sb.AppendLine("  адрес вида 26.x.x.x и подключаются по нему — порты не нужны.");
            sb.AppendLine();
            sb.AppendLine("ВНУТРИ ОДНОЙ СЕТИ (один Wi-Fi, одна квартира) проброс не нужен:");
            sb.AppendLine($"друзья подключаются по адресу {_room.HostAddress ?? "локальный адрес"}:{port}.");

            return sb.ToString();
        }

        /// <summary>Перечень локальных адресов: по ним подключаются внутри сети.</summary>
        private static string DescribeLocalAddresses()
        {
            try
            {
                var list = new List<string>();
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string text = addr.Address.ToString();
                        if (text.StartsWith("169.254.")) continue;   // самоприсвоенный адрес
                        list.Add(text);
                    }
                }
                return list.Count == 0 ? "локальный адрес не определён" : string.Join(", ", list.Distinct());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Сеть] локальные адреса: {ex.Message}");
                return "локальный адрес не определён";
            }
        }

        #endregion

        #region === ОТРЯД: ОТРИСОВКА ПАНЕЛИ ===

        private void RefreshRoomPanel()
        {
            bool active = _room.IsActive;

            // Состояние подключения
            brRoomConnected.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            btnRoomOpenTab.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            btnRoomPanelLeave.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            btnCreateRoom.IsEnabled = !active;
            btnJoinRoom.IsEnabled = !active;
            txtJoinHint.Text = active
                ? "Сначала выйдите из текущего отряда."
                : "Введите адрес и ключ, которые сообщил ведущий.";

            // Локальный адрес показываем всегда: он нужен для игры внутри одной сети
            txtLocalAddress.Text = active
                ? $"{_room.HostAddress}:{_room.Port}"
                : "определится при создании комнаты";

            // Проверка доступности нужна только когда комната поднята и известен внешний адрес
            btnCheckPort.Visibility = active && !string.IsNullOrEmpty(_publicAddress)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (active)
            {
                txtRoomConnectedTitle.Text = _room.IsHost
                    ? "ВЫ ВЕДУЩИЙ ЭТОЙ КОМНАТЫ"
                    : (_room.AmIGameMaster ? "ВЫ В ОТРЯДЕ (ВЕДУЩИЙ)" : "ВЫ В ОТРЯДЕ");
                txtRoomPanelKey.Text = _room.IsHost ? RoomKey.Pretty(_room.Key) : "скрыт";

                // Хосту показываем оба адреса: локальный для дома, внешний для интернета
                txtRoomPanelAddress.Text = _room.IsHost
                    ? (string.IsNullOrEmpty(_publicAddress)
                        ? $"{_room.HostAddress}:{_room.Port}"
                        : $"{_room.HostAddress}:{_room.Port}  ·  {_publicAddress}:{_room.Port}")
                    : "вы подключены к ведущему";
                txtRoomPanelPlayers.Text = _room.StatusText;
            }

            UpdateRoomIndicator();
            BuildSavedRooms();
        }

        /// <summary>Список сохранённых комнат: их можно продолжить, а не создавать заново.</summary>
        private void BuildSavedRooms()
        {
            spSavedRooms.Children.Clear();

            List<RoomSummary> rooms;
            try
            {
                rooms = RoomStore.ListRooms();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] ListRooms: {ex.Message}");
                rooms = new List<RoomSummary>();
            }

            // Текущую комнату в списке «продолжить» не показываем — мы уже в ней
            string currentId = _room.RoomId;
            rooms = rooms.Where(r => r.Id != currentId).ToList();

            txtNoRooms.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var room in rooms.Take(8))
                spSavedRooms.Children.Add(BuildSavedRoomRow(room));
        }

        private UIElement BuildSavedRoomRow(RoomSummary room)
        {
            var border = new Border
            {
                Background = (Brush)FindResource("B.Slate"),
                BorderBrush = (Brush)FindResource("B.Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(room.Name) ? room.Id : room.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("B.Ink")
            });

            // Дата последней игры важнее даты создания: по ней узнаёшь «вчерашнюю» комнату
            string when = room.LastPlayedAt > DateTime.MinValue
                ? room.LastPlayedAt.ToString("dd.MM.yyyy HH:mm")
                : room.CreatedAt.ToString("dd.MM.yyyy HH:mm");
            info.Children.Add(new TextBlock
            {
                Text = $"{when} · записей: {room.EntryCount} · участников: {room.PlayerCount}",
                FontSize = 10,
                Foreground = (Brush)FindResource("B.Mute"),
                Margin = new Thickness(0, 2, 0, 0)
            });

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var btnHost = new Button
            {
                Content = "Продолжить как хост",
                Style = (Style)FindResource("RoomGhostBtnStyle"),
                Margin = new Thickness(0, 0, 6, 0)
            };
            // Ключ комнаты хранится зашифрованным DPAPI, поэтому продолжить можно только на этом компьютере
            btnHost.Click += (s, e) => ContinueRoom(room.Id);
            actions.Children.Add(btnHost);

            var btnDelete = new Button
            {
                Content = "Удалить",
                Style = (Style)FindResource("RoomGhostBtnStyle"),
                Foreground = (Brush)FindResource("B.Danger")
            };
            btnDelete.Click += (s, e) => DeleteRoom(room);
            actions.Children.Add(btnDelete);

            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            border.Child = grid;
            return border;
        }

        private void ContinueRoom(string roomId)
        {
            try
            {
                var manifest = RoomStore.LoadManifest(roomId);
                if (manifest == null)
                {
                    MessageDialog.Error(this, "Комната не найдена на диске.", "Продолжение игры");
                    return;
                }

                string key = RoomStore.GetRoomKey(manifest);
                if (string.IsNullOrEmpty(key))
                {
                    // DPAPI привязан к учётной записи Windows: на другом компьютере ключ не прочитать
                    MessageDialog.Error(this,
                        "Не удалось прочитать ключ этой комнаты. Он зашифрован под учётную запись Windows " +
                        "и доступен только на том компьютере, где комната создавалась.",
                        "Продолжение игры");
                    return;
                }

                if (!_room.ContinueExistingRoom(manifest, key, BuildPlayerFromFirstCharacter(), out string error))
                {
                    MessageDialog.Error(this, error, "Не удалось продолжить игру");
                    return;
                }

                RefreshRoomPanel();
                MessageDialog.Info(this,
                    $"Комната «{manifest.Name}» снова открыта.\n\nКлюч: {RoomKey.Pretty(key)}\n" +
                    $"Адрес: {_room.HostAddress}:{_room.Port}",
                    "Игра продолжается");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] ContinueRoom: {ex}");
                MessageDialog.Error(this, ex.Message, "Ошибка");
            }
        }

        private void DeleteRoom(RoomSummary room)
        {
            if (!MessageDialog.Confirm(this,
                    $"Удалить комнату «{(string.IsNullOrWhiteSpace(room.Name) ? room.Id : room.Name)}» " +
                    "вместе с историей чата и бросков?",
                    "Удаление комнаты", "Удалить", "Отмена", danger: true))
                return;

            RoomStore.DeleteRoom(room.Id);
            BuildSavedRooms();
        }

        #endregion

        #region === ОТРЯД: ДЕЙСТВИЯ ===

        private void BtnCreateRoom_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int port = ParsePort(txtNewRoomPort.Text);
                var me = BuildPlayerFromFirstCharacter();

                if (!_room.CreateRoom(txtNewRoomName.Text, me, port, out string error))
                {
                    MessageDialog.Error(this, error, "Не удалось создать комнату");
                    return;
                }

                // Порт должен быть открыт на входящие, иначе игроки не подключатся
                EnsureFirewallRule(port);
                RefreshRoomPanel();

                string local = $"{_room.HostAddress}:{_room.Port}";
                string remote = string.IsNullOrEmpty(_publicAddress) ? "" :
                    $"\n\nДля друзей из интернета: {_publicAddress}:{_room.Port}";

                MessageDialog.Info(this,
                    $"Комната создана.\n\nКлюч: {RoomKey.Pretty(_room.Key)}\n" +
                    $"Для игры в одной сети: {local}{remote}\n\n" +
                    "Передайте игрокам ключ и адрес. Если играете через интернет, " +
                    "нажмите «Узнать для интернета» и проверьте доступность порта.",
                    "Отряд создан");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] CreateRoom: {ex}");
                MessageDialog.Error(this, ex.Message, "Ошибка");
            }
        }

        /// <summary>
        /// Предлагает разрешить программе входящие подключения.
        ///
        /// Без правила брандмауэра игроки не подключаются: Windows спрашивает
        /// разрешение только при первом внешнем подключении, а в «общественном»
        /// профиле сети молча блокирует входящие. Поэтому спрашиваем заранее и
        /// добавляем правило тем же запросом прав, что и другие программы.
        /// </summary>
        private void EnsureFirewallRule(int port)
        {
            try
            {
                if (FirewallHelper.RuleExists()) return;

                bool agreed = MessageDialog.Confirm(this, FirewallHelper.ExplainText(port),
                    "Доступ через брандмауэр", "Разрешить", "Не сейчас", false);
                if (!agreed) return;

                if (FirewallHelper.AddRule(port, out string message))
                    MessageDialog.Info(this, message, "Брандмауэр");
                else
                    MessageDialog.Error(this, message, "Брандмауэр");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] правило брандмауэра: {ex}");
            }
        }

        private void BtnJoinRoom_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string key = txtJoinKey.Text?.Trim() ?? "";

                // Адрес принимаем и по частям, и целой строкой «192.168.1.5:47777»:
                // ведущий присылает именно такую строку, а раньше она уходила в сокет
                // целиком, и подключение падало с «хост не отвечает»
                if (!NetworkHelper.TrySplitHostPort(txtJoinHost.Text, out string host, out int port, out string splitError))
                {
                    MessageDialog.Error(this, splitError, "Не хватает адреса");
                    return;
                }

                // Порт из отдельного поля имеет приоритет, если игрок его менял сам
                int typedPort = ParsePort(txtJoinPort.Text);
                if (typedPort != NetVersions.DefaultPort) port = typedPort;

                if (string.IsNullOrWhiteSpace(key))
                {
                    MessageDialog.Error(this, "Введите ключ комнаты, который сообщил ведущий.", "Не хватает ключа");
                    return;
                }

                var me = BuildPlayerFromFirstCharacter();

                if (!_room.JoinRoom(host, port, key, me, out string error))
                {
                    // Текст уже содержит ссылку на релизы при несовпадении версий
                    MessageDialog.Error(this, error, "Не удалось подключиться");
                    return;
                }

                RefreshRoomPanel();
                MessageDialog.Info(this, _room.StatusText, "Подключение выполнено");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] JoinRoom: {ex}");
                MessageDialog.Error(this, ex.Message, "Ошибка");
            }
        }

        private void BtnRoomPanelLeave_Click(object sender, RoutedEventArgs e)
        {
            if (!_room.IsActive) return;

            if (!MessageDialog.Confirm(this,
                    _room.IsHost
                        ? "Выйти из отряда? Комната сохранится, и её можно будет продолжить позже."
                        : "Выйти из отряда?",
                    "Выход из отряда", "Выйти", "Остаться", danger: true))
                return;

            _room.Leave();
            RefreshRoomPanel();
        }

        private void BtnRoomOpenTab_Click(object sender, RoutedEventArgs e)
        {
            CloseRoomPanel();

            // Открываем окно персонажа, который сейчас в комнате: там вкладка «Отряд»
            var player = _room.GetPlayers().FirstOrDefault(p => p.Id == _room.GetMyPlayerId());
            var target = player != null && player.CharacterId > 0
                ? player.CharacterId
                : GetAnyCharacterId();

            if (target <= 0)
            {
                MessageDialog.Info(this, "Сначала создайте персонажа — отряд показывает именно его.",
                    "Нет персонажа");
                return;
            }

            var window = new CharacterInfoWindow(target);
            window.Show();
        }

        /// <summary>Индикатор в шапке: цвет точки и подсказка.</summary>
        private void UpdateRoomIndicator()
        {
            if (brMainRoomDot == null) return;

            bool active = _room.IsActive;
            brMainRoomDot.Background = (Brush)FindResource(
                !active ? "B.Mute" : (_room.IsHost ? "B.Ok" : "B.Frost"));

            RoomButton.ToolTip = active
                ? _room.StatusText + "\nНажмите, чтобы открыть панель отряда"
                : "Отряд: создать комнату или подключиться";
        }

        private void Room_StateChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateRoomIndicator();
                if (RoomPanel.Visibility == Visibility.Visible) RefreshRoomPanel();
            }));
        }

        #endregion

        #region === ОТРЯД: ВСПОМОГАТЕЛЬНОЕ ===

        private static int ParsePort(string text)
        {
            if (int.TryParse((text ?? "").Trim(), out int port) && port > 0 && port <= 65535)
                return port;
            return NetVersions.DefaultPort;
        }

        /// <summary>
        /// Собирает данные игрока для комнаты.
        ///
        /// Приоритет у персонажа: если он есть, ведущий играет за него. Если персонажа
        /// нет, берётся профиль ведущего — иначе в отряде появляется безымянный участник,
        /// хотя у ведущего могут быть и никнейм, и аватар.
        /// </summary>
        private PlayerInfo BuildPlayerFromFirstCharacter()
        {
            // Имя участника — имя персонажа, а не профиль и не учётка Windows
            return Services.DmProfileService.BuildPartyPlayer();
        }

        private int GetAnyCharacterId()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();

                // Сначала тот персонаж, которого пользователь открывал последним:
                // раньше в отряд уходил первый по списку, и игрок видел чужое имя.
                using (var last = conn.CreateCommand())
                {
                    last.CommandText = "SELECT Value FROM Settings WHERE Key = 'LastCharacterId' LIMIT 1";
                    var stored = last.ExecuteScalar();
                    if (stored != null && stored != DBNull.Value &&
                        int.TryParse(Convert.ToString(stored), out int lastId) && lastId > 0)
                    {
                        using var check = conn.CreateCommand();
                        check.CommandText = "SELECT COUNT(*) FROM CharacterInfo WHERE id_character = @id";
                        check.Parameters.AddWithValue("@id", lastId);
                        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return lastId;
                    }
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id_character FROM CharacterInfo ORDER BY id_character LIMIT 1";
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] GetAnyCharacterId: {ex.Message}");
                return 0;
            }
        }

        private static PlayerInfo BuildPlayerFromCharacter(int characterId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT name, class, race, HP, ArmorClass, PhotoPath,
                                           COALESCE((SELECT Level FROM LevelTable WHERE id_character = CharacterInfo.id_character), 1)
                                    FROM CharacterInfo WHERE id_character = @id";
                cmd.Parameters.AddWithValue("@id", characterId);

                using var r = cmd.ExecuteReader();
                if (!r.Read()) return new PlayerInfo { Name = "Игрок", CharacterId = characterId };

                // Колонки читаем ДО Close(): у закрытого reader'а IsDBNull
                // бросает исключение, и раньше отсюда всегда возвращался
                // запасной «Игрок» — ведущий в отряде был безымянным.
                // Числа берём через Convert: SQLite отдаёт INTEGER как Int64,
                // а GetInt32 на нём падает и ронял весь метод в catch.
                int hp = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                string characterName = r.IsDBNull(0) ? "" : Convert.ToString(r.GetValue(0));
                if (string.IsNullOrWhiteSpace(characterName)) characterName = "Игрок";
                string charClass = r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1));
                string race = r.IsDBNull(2) ? "" : Convert.ToString(r.GetValue(2));
                int armor = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4));
                string avatar = r.IsDBNull(5) ? "" : Convert.ToString(r.GetValue(5));
                int level = r.IsDBNull(6) ? 1 : Convert.ToInt32(r.GetValue(6));
                r.Close();

                // В отряде у всех имя персонажа: личного профиля больше нет
                string display = characterName;

                // Максимум HP — из настроек персонажа, а не из колонки HP:
                // иначе в отряд уходит «185/185» вместо «185/210»
                int maxHp = Services.RestService.LoadMaxHp(characterId);
                if (maxHp <= 0) maxHp = hp;

                // Состояния — часть листа: по ним ведущий понимает, что
                // действует на персонажа, не открывая чужой лист.
                string conditions = "";
                try
                {
                    conditions = string.Join(", ",
                        Services.CharacterStateService.LoadConditionKeys(characterId));
                }
                catch { }

                return new PlayerInfo
                {
                    CharacterId = characterId,
                    CharacterName = characterName,
                    CharacterClass = charClass,
                    CharacterRace = race,
                    Hp = hp,
                    MaxHp = maxHp,
                    ArmorClass = armor,
                    Level = level,
                    AvatarPath = avatar,
                    Name = display,
                    Conditions = conditions
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] BuildPlayerFromCharacter: {ex.Message}");
                return new PlayerInfo { Name = "Игрок", CharacterId = characterId };
            }
        }

        #endregion
    }
}
