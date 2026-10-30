using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Controls;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Аудиотека ведущего: импорт файлов, воспроизведение и своя громкость
    /// у каждого трека. Окно не модальное: можно оставить музыку играть и
    /// вернуться к карте.
    ///
    /// Передача звука игрокам появится следующим шагом; здесь — фундамент,
    /// чтобы проверить сам звук и ползунки.
    /// </summary>
    public sealed class AudioWindow : Window
    {
        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF5));
        private static readonly Brush Mute = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xB4));
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x1F));
        private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x2E));
        private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x68));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x7F));
        private static readonly Brush ButtonBg = new SolidColorBrush(Color.FromRgb(0x22, 0x2B, 0x45));

        private readonly StackPanel _list = new StackPanel();
        private int _playingId;
        private Slider _master;

        private ComboBox _playlists;
        private StackPanel _playlistBox;
        private TextBlock _playlistTitle;
        private TextBox _search;
        private HashSet<int> _playlistTrackIds = new HashSet<int>();
        private List<AudioTrackData> _queue = new List<AudioTrackData>();
        private int _queueIndex;
        private bool _playlistMode;

        public AudioWindow()
        {
            Title = "Аудиотека · " + HMWT_MUI.Net.NetVersions.AppVersion;
            Width = 880;
            Height = 680;
            MinWidth = 760;
            MinHeight = 520;
            Background = Bg;
            Foreground = Ink;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Build();
            Reload();

            DarkWindowChrome.Apply(this, "Аудиотека · " + HMWT_MUI.Net.NetVersions.AppVersion,
                (UIElement)Content, Card, Line, Ink);

            Loaded += (_, __) => { var h = Host; if (h != null) h.Ended += OnTrackEnded; };
            Closed += (_, __) => { var h = Host; if (h != null) h.Ended -= OnTrackEnded; };
        }

        private void OnTrackEnded()
        {
            if (_playlistMode)
            {
                Dispatcher.BeginInvoke(new Action(NextTrack));
                return;
            }

            // Из библиотеки трек просто заканчивается, без повтора.
            _playingId = 0;
            if (Host != null) Host.Loop = false;
            Dispatcher.BeginInvoke(new Action(Reload));
        }

        private AudioHost Host
        {
            get
            {
                return AudioHost.Shared ?? (Application.Current?.MainWindow as MainWindow)?.AudioHost;
            }
        }

        private void Build()
        {
            var root = new Grid { Margin = new Thickness(16, 12, 16, 12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var top = new StackPanel();
            top.Children.Add(new TextBlock
            {
                Text = "Музыка, эмбиент и звуки",
                FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2)
            });
            top.Children.Add(new TextBlock
            {
                Text = "Громкость у каждого трека своя — файлы из разных источников звучат по-разному.",
                FontSize = 11, Foreground = Mute, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };

            var add = MakeButton("Добавить файлы…", 160, DmUiKit.ButtonKind.Soft, "plus");
            add.Click += (s, e) => ImportFiles();
            bar.Children.Add(add);

            var stop = MakeButton("Стоп", 82, DmUiKit.ButtonKind.Danger);
            stop.Click += (s, e) =>
            {
                _playlistMode = false;
                _queue.Clear();
                _queueIndex = 0;
                if (Host != null) Host.Loop = false;
                Host?.Stop();
                if (RoomSession.Instance.IsActive)
                    RoomSession.Instance.PublishAudioStop();
                _playingId = 0;
                Reload();
            };
            bar.Children.Add(stop);

            _master = new Slider
            {
                Minimum = 0, Maximum = 100, Width = 130,
                Value = Math.Round(AudioSettings.Master * 100),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 6, 0),
                ToolTip = "Общая громкость"
            };
            _master.ValueChanged += (s, e) =>
            {
                AudioSettings.SetMaster(_master.Value / 100.0);
            };

            bar.Children.Add(new TextBlock
            {
                Text = "Общая громкость", FontSize = 11, Foreground = Mute,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0)
            });
            bar.Children.Add(_master);

            top.Children.Add(bar);
            root.Children.Add(top);

            // Две колонки: слева плейлист, справа библиотека. В одном
            // столбце плайлист и сотня треков мешали друг другу.
            var split = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(split, 1);

            var left = new Grid { Margin = new Thickness(0, 0, 14, 0) };
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            left.Children.Add(new TextBlock
            {
                Text = "ПЛЕЙЛИСТ", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = Accent, Margin = new Thickness(2, 0, 0, 4)
            });

            var plBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _playlists = new ComboBox
            {
                Width = 170, Height = 28, Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            DmUiKit.ApplyDarkListStyle(_playlists);
            _playlists.SelectionChanged += (s, e) => ReloadPlaylistTracks();
            plBar.Children.Add(_playlists);

            var create = IconButton("plus", (s, e) => CreatePlaylist(), "Создать плейлист",
                30, DmUiKit.ButtonKind.Soft);
            plBar.Children.Add(create);

            var playList = IconButton("play", (s, e) => PlayPlaylist(), "Играть плейлист",
                30, DmUiKit.ButtonKind.Primary, true);
            plBar.Children.Add(playList);

            var next = IconButton("arrowRight", (s, e) => NextTrack(), "Следующий трек",
                30, DmUiKit.ButtonKind.Ghost);
            plBar.Children.Add(next);

            var delete = IconButton("trash", (s, e) => DeletePlaylist(), "Удалить плейлист",
                30, DmUiKit.ButtonKind.Danger);
            plBar.Children.Add(delete);

            Grid.SetRow(plBar, 1);
            left.Children.Add(plBar);

            _playlistTitle = new TextBlock
            {
                Text = "Плейлист пуст", FontSize = 11, Foreground = Mute,
                Margin = new Thickness(2, 0, 0, 4)
            };
            Grid.SetRow(_playlistTitle, 2);
            left.Children.Add(_playlistTitle);

            _playlistBox = new StackPanel();
            var playlistBorder = new Border
            {
                Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6)
            };
            playlistBorder.Child = new ScrollViewer
            {
                Content = _playlistBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(playlistBorder, 3);
            left.Children.Add(playlistBorder);

            split.Children.Add(left);

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var libHead = new Grid { Margin = new Thickness(2, 0, 0, 4) };
            libHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            libHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            libHead.Children.Add(new TextBlock
            {
                Text = "БИБЛИОТЕКА", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = Accent, VerticalAlignment = VerticalAlignment.Center
            });

            _search = DmUiKit.Field("", 230, "Поиск по библиотеке");
            _search.TextChanged += (s, e) => Reload();
            Grid.SetColumn(_search, 1);
            libHead.Children.Add(_search);
            right.Children.Add(libHead);

            var scroll = new ScrollViewer
            {
                Content = _list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(scroll, 1);
            right.Children.Add(scroll);

            Grid.SetColumn(right, 1);
            split.Children.Add(right);
            root.Children.Add(split);

            string roomText = !RoomSession.Instance.IsActive
                ? "ВНИМАНИЕ: комната не активна — трек слышите только вы. Подключитесь к отряду и включите снова."
                : (RoomSession.Instance.IsHost
                    ? "Комната: вы ведущий — трек слышат все."
                    : "Комната: вы игрок — трек уйдёт ведущему и разойдётся всем.");

            int otherCopies = 0;
            try
            {
                otherCopies = System.Diagnostics.Process.GetProcessesByName("HMWT_MUI")
                    .Count(x => x.Id != System.Diagnostics.Process.GetCurrentProcess().Id);
            }
            catch { }
            if (otherCopies > 0)
                roomText += $" ВНИМАНИЕ: запущено ещё копий HMWT: {otherCopies} — закройте их, иначе звук и комната могут идти из другой копии.";

            var hint = new TextBlock
            {
                Text = roomText,
                FontSize = 11,
                Foreground = RoomSession.Instance.IsActive ? Mute : new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x6A)),
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(hint, 2);
            root.Children.Add(hint);

            ReloadPlaylists();
            Content = root;
        }
        private void Reload()
        {
            _list.Children.Clear();
            var tracks = AudioService.List();

            string query = (_search?.Text ?? "").Trim();
            if (query.Length > 0)
            {
                tracks = tracks
                    .Where(t => (t.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                (t.Path ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            if (tracks.Count == 0)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = query.Length > 0
                        ? "По запросу «" + query + "» ничего не найдено."
                        : "Пока пусто. Нажмите «Добавить файлы…» — mp3, ogg, wav, flac.",
                    FontSize = 12, Foreground = Mute, Margin = new Thickness(4, 12, 4, 12),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var track in tracks) _list.Children.Add(BuildRow(track));
        }

        private int? SelectedPlaylistId
            => (_playlists?.SelectedItem as ComboBoxItem)?.Tag is int id && id > 0
                ? id : (int?)null;

        private void ReloadPlaylists()
        {
            if (_playlists == null) return;
            int? keep = SelectedPlaylistId;
            _playlists.Items.Clear();
            foreach (var playlist in AudioService.Playlists())
                _playlists.Items.Add(new ComboBoxItem { Content = playlist.Name, Tag = playlist.Id });

            if (_playlists.Items.Count == 0) return;

            int index = 0;
            if (keep != null)
            {
                for (int i = 0; i < _playlists.Items.Count; i++)
                {
                    if ((_playlists.Items[i] as ComboBoxItem)?.Tag is int id && id == keep)
                    {
                        index = i;
                        break;
                    }
                }
            }
            _playlists.SelectedIndex = index;
            ReloadPlaylistTracks();
        }

        private void ReloadPlaylistTracks()
        {
            if (_playlistBox == null) return;
            _playlistBox.Children.Clear();
            _playlistTrackIds.Clear();

            int? id = SelectedPlaylistId;
            if (id == null)
            {
                if (_playlistTitle != null) _playlistTitle.Text = "Плейлист не выбран";
                Reload();
                return;
            }

            var tracks = AudioService.PlaylistTracks(id.Value);
            _playlistTrackIds = new HashSet<int>(tracks.Select(t => t.Id));

            if (_playlistTitle != null)
                _playlistTitle.Text = tracks.Count == 0
                    ? "Плейлист пуст"
                    : $"В плейлисте: {tracks.Count}";

            if (tracks.Count == 0)
            {
                _playlistBox.Children.Add(new TextBlock
                {
                    Text = "Пусто. Нажмите «В плейлист» у нужного трека в библиотеке.",
                    FontSize = 11, Foreground = Mute, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 4, 4, 4)
                });
                Reload();
                return;
            }

            int position = 1;
            foreach (var track in tracks)
            {
                int trackId = track.Id;
                var row = new Grid { Margin = new Thickness(2, 2, 2, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = position + ". " + track.Name,
                    FontSize = 12, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                var play = IconButton("play", (s, e) => PlayPlaylistTrack(track),
                    "Играть", 28, DmUiKit.ButtonKind.Primary, true);
                Grid.SetColumn(play, 1);
                row.Children.Add(play);

                var remove = IconButton("minus", (s, e) =>
                {
                    AudioService.RemoveFromPlaylist(id.Value, trackId);
                    ReloadPlaylistTracks();
                }, "Убрать из плейлиста", 28, DmUiKit.ButtonKind.Ghost);
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);

                _playlistBox.Children.Add(row);
                position++;
            }

            // Обновить плюсики в библиотеке: трек уже в плейлисте.
            Reload();
        }

        private void CreatePlaylist()
        {
            string name = NotePromptWindow.AskText(this, "Плейлист", "Название", "Новый плейлист", "Создать");
            if (string.IsNullOrWhiteSpace(name)) return;
            AudioService.CreatePlaylist(name);
            ReloadPlaylists();
        }

        private void DeletePlaylist()
        {
            int? id = SelectedPlaylistId;
            if (id == null) return;
            if (!NotePromptWindow.Confirm(this, "Удалить плейлист?",
                    "Треки останутся в библиотеке — удалится только список.",
                    "Удалить")) return;

            AudioService.DeletePlaylist(id.Value);
            _playlistMode = false;
            if (Host != null) Host.Loop = false;
            ReloadPlaylists();
            ReloadPlaylistTracks();
        }

        private void PlayPlaylist()
        {
            int? id = SelectedPlaylistId;
            if (id == null)
            {
                NotePromptWindow.Message(this, "Плейлист",
                    "Сначала создайте плейлист и добавьте в него треки.");
                return;
            }

            var tracks = AudioService.PlaylistTracks(id.Value);
            if (tracks.Count == 0)
            {
                NotePromptWindow.Message(this, "Плейлист",
                    "В плейлисте пока нет треков. Нажмите «В плейлист» у трека в библиотеке.");
                return;
            }

            _queue = tracks;
            _queueIndex = 0;
            _playlistMode = true;
            PlayTrack(tracks[0]);
        }

        /// <summary>
        /// Запуск конкретного трека из плейлиста: очередь должна начинаться
        /// с него, иначе «Дальше»/автопереход снова начинали плейлист с нуля.
        /// </summary>
        private void PlayPlaylistTrack(AudioTrackData track)
        {
            int? id = SelectedPlaylistId;
            if (id == null)
            {
                _playlistMode = false;
                PlayTrack(track);
                return;
            }

            var tracks = AudioService.PlaylistTracks(id.Value);
            int index = tracks.FindIndex(t => t.Id == track.Id);
            _queue = tracks;
            _queueIndex = index >= 0 ? index : 0;
            _playlistMode = true;
            PlayTrack(track);
        }

        private void NextTrack()
        {
            if (_queue.Count == 0)
            {
                PlayPlaylist();
                return;
            }

            if (_queueIndex + 1 >= _queue.Count)
            {
                // Конец плейлиста: не начинаем сначала.
                _playlistMode = false;
                _queue.Clear();
                _queueIndex = 0;
                _playingId = 0;
                Host?.Stop();
                if (RoomSession.Instance.IsActive)
                    RoomSession.Instance.PublishAudioStop();
                Reload();
                return;
            }

            _queueIndex++;
            PlayTrack(_queue[_queueIndex]);
        }

        private void AddToPlaylist(AudioTrackData track)
        {
            int? id = SelectedPlaylistId;
            if (id == null)
            {
                NotePromptWindow.Message(this, "Плейлист", "Сначала создайте плейлист.");
                return;
            }

            if (_playlistTrackIds.Contains(track.Id))
            {
                NotePromptWindow.Message(this, "Плейлист",
                    $"«{track.Name}» уже есть в этом плейлисте.");
                return;
            }

            AudioService.AddToPlaylist(id.Value, track.Id);
            ReloadPlaylistTracks();
        }

        private FrameworkElement BuildRow(AudioTrackData track)
        {
            bool inPlaylist = _playlistTrackIds.Contains(track.Id);
            var row = new Border
            {
                Background = Card,
                BorderBrush = track.Id == _playingId ? Accent : Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            text.Children.Add(new TextBlock
            {
                Text = track.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = track.Name
            });
            text.Children.Add(new TextBlock
            {
                Text = $"{SizeText(track.Size)} · {(track.Id == _playingId ? "играет" : inPlaylist ? "в плейлисте" : "готов")}",
                FontSize = 10,
                Foreground = track.Id == _playingId ? Accent : Mute
            });

            var gainRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };
            gainRow.Children.Add(new TextBlock
            {
                Text = "Громкость", FontSize = 10, Foreground = Mute,
                VerticalAlignment = VerticalAlignment.Center, Width = 62
            });

            var gainLabel = new TextBlock
            {
                Text = Math.Round(track.Gain * 100) + "%",
                FontSize = 10, Foreground = Ink, Width = 38,
                VerticalAlignment = VerticalAlignment.Center
            };

            var slider = new Slider
            {
                Minimum = 0, Maximum = 200, Width = 120,
                Value = Math.Round(track.Gain * 100),
                VerticalAlignment = VerticalAlignment.Center
            };
            slider.ValueChanged += (s, e) =>
            {
                double gain = slider.Value / 100.0;
                track.Gain = gain;
                gainLabel.Text = Math.Round(gain * 100) + "%";
                AudioService.SetGain(track.Id, gain);
                if (track.Id == _playingId)
                {
                    Host?.SetTrackGain(gain);
                    if (RoomSession.Instance.IsActive)
                        RoomSession.Instance.PublishAudioGain(track.Hash, gain);
                }
            };

            gainRow.Children.Add(slider);
            gainRow.Children.Add(gainLabel);
            text.Children.Add(gainRow);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var play = IconButton("play", (s, e) => { _playlistMode = false; PlayTrack(track); },
                _playingId == track.Id ? "Заново" : "Играть", 30, DmUiKit.ButtonKind.Primary, true);
            buttons.Children.Add(play);

            var toPlaylist = inPlaylist
                ? IconButton("check", (s, e) =>
                {
                    NotePromptWindow.Message(this, "Плейлист",
                        $"«{track.Name}» уже есть в выбранном плейлисте.");
                }, "Уже в плейлисте", 30, DmUiKit.ButtonKind.Ghost)
                : IconButton("plus", (s, e) => AddToPlaylist(track), "В плейлист",
                    30, DmUiKit.ButtonKind.Ghost);
            buttons.Children.Add(toPlaylist);

            var del = IconButton("trash", (s, e) =>
            {
                if (!NotePromptWindow.Confirm(this, "Удалить трек?",
                        $"«{track.Name}» исчезнет из библиотеки и из всех плейлистов.",
                        "Удалить")) return;
                if (track.Id == _playingId)
                {
                    Host?.Stop();
                    if (RoomSession.Instance.IsActive)
                        RoomSession.Instance.PublishAudioStop();
                    _playingId = 0;
                }
                AudioService.Delete(track);
                Reload();
            }, "Удалить из библиотеки", 30, DmUiKit.ButtonKind.Danger);
            buttons.Children.Add(del);

            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            row.Child = grid;
            return row;
        }

        private void PlayTrack(AudioTrackData track)
        {
            if (!_playlistMode)
            {
                _queue.Clear();
                _queueIndex = 0;
            }

            // Плейлист сам переключает треки по Ended; одиночный трек
            // из библиотеки не должен зацикливаться.
            if (Host != null) Host.Loop = false;
            AudioLog.Write($"window play pid={System.Diagnostics.Process.GetCurrentProcess().Id} " +
                $"hash={track.Hash} room={RoomSession.Instance.IsActive} host={RoomSession.Instance.IsHost}");

            string full = AudioService.ResolvePath(track);
            if (full == null || !File.Exists(full))
            {
                NotePromptWindow.Message(this, "Аудиотека", "Файл трека не найден.");
                return;
            }

            var host = Host;
            if (host == null)
            {
                NotePromptWindow.Message(this, "Аудиотека",
                    "Аудио-хост ещё не готов. Попробуйте через пару секунд.");
                return;
            }

            string relative = track.Path.Replace('\\', '/').TrimStart('/');

            // В комнате трек запускает сервер: у ведущего — сразу, у игрока
            // файл сначала уедет ведущему и разойдётся всем. Локально играем
            // только вне комнаты.
            if (RoomSession.Instance.IsActive)
            {
                RoomSession.Instance.PublishAudio(new AudioState
                {
                    Hash = track.Hash,
                    Extension = Path.GetExtension(track.Path),
                    Name = track.Name,
                    Gain = track.Gain,
                    On = true
                });
                _playingId = track.Id;
                Reload();
                return;
            }

            host.Play("https://hmwt.audio/" + relative, track.Gain, 0);
            _playingId = track.Id;
            Reload();
        }

        private void ImportFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "Добавить звуки и музыку",
                Filter = "Аудио|*.mp3;*.ogg;*.wav;*.m4a;*.aac;*.flac;*.opus|Все файлы|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;

            int added = 0, skipped = 0;
            foreach (string file in dialog.FileNames)
            {
                if (AudioService.Import(file) != null) added++; else skipped++;
            }

            Reload();
            if (skipped > 0)
            {
                NotePromptWindow.Message(this, "Импорт",
                    $"Добавлено: {added}. Пропущено (не аудио или уже есть): {skipped}.");
            }
        }

        private static string SizeText(long bytes)
        {
            if (bytes <= 0) return "—";
            double mb = bytes / 1024.0 / 1024.0;
            return mb >= 1 ? $"{mb:0.0} МБ" : $"{bytes / 1024} КБ";
        }

        private static Button IconButton(string icon, RoutedEventHandler onClick, string tooltip,
            double size = 30, DmUiKit.ButtonKind kind = DmUiKit.ButtonKind.Ghost, bool filled = false)
        {
            var button = DmUiKit.IconButton(icon, onClick, tooltip, size, kind, filled);
            button.Margin = new Thickness(0, 0, 6, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            return button;
        }

        private static Button MakeButton(string text, double width,
            DmUiKit.ButtonKind kind = DmUiKit.ButtonKind.Ghost, string icon = null)
        {
            // Кнопки — из общего набора: у них тёмный шаблон приложения,
            // а не системный «виндусовский» вид. Иконка — из IconLibrary.
            var button = DmUiKit.Action(text, (s, e) => { }, kind, null, width, 28, icon);
            button.Margin = new Thickness(0, 0, 8, 0);
            return button;
        }
    }
}
