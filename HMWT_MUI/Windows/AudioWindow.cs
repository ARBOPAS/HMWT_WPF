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
        private List<AudioTrackData> _queue = new List<AudioTrackData>();
        private int _queueIndex;
        private bool _playlistMode;

        public AudioWindow()
        {
            Title = "Аудиотека · " + HMWT_MUI.Net.NetVersions.AppVersion;
            Width = 640;
            Height = 560;
            MinWidth = 460;
            MinHeight = 380;
            Background = Bg;
            Foreground = Ink;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Build();
            Reload();

            Loaded += (_, __) => { var h = Host; if (h != null) h.Ended += OnTrackEnded; };
            Closed += (_, __) => { var h = Host; if (h != null) h.Ended -= OnTrackEnded; };
        }

        private void OnTrackEnded()
        {
            if (!_playlistMode) return;
            Dispatcher.BeginInvoke(new Action(NextTrack));
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

            var add = MakeButton("Добавить файлы…", 140);
            add.Click += (s, e) => ImportFiles();
            bar.Children.Add(add);

            var stop = MakeButton("Стоп", 70);
            stop.Click += (s, e) =>
            {
                _playlistMode = false;
                _queue.Clear();
                if (Host != null) Host.Loop = true;
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

            // Плейлисты: локальный инструмент диджея. В комнату уходит
            // только текущий трек, поэтому синхронизация не усложняется.
            var plBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            plBar.Children.Add(new TextBlock
            {
                Text = "Плейлист", FontSize = 11, Foreground = Mute,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            _playlists = new ComboBox
            {
                Width = 170, Height = 28, Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            DmUiKit.ApplyDarkListStyle(_playlists);
            _playlists.SelectionChanged += (s, e) => ReloadPlaylistTracks();
            plBar.Children.Add(_playlists);

            var create = MakeButton("Создать…", 96);
            create.Click += (s, e) => CreatePlaylist();
            plBar.Children.Add(create);

            var playList = MakeButton("Играть плейлист", 126);
            playList.Click += (s, e) => PlayPlaylist();
            plBar.Children.Add(playList);

            var next = MakeButton("Дальше", 80);
            next.Click += (s, e) => NextTrack();
            plBar.Children.Add(next);

            var delete = MakeButton("Удалить", 82);
            delete.Click += (s, e) => DeletePlaylist();
            plBar.Children.Add(delete);

            top.Children.Add(plBar);

            // Содержимое плейлиста: видно, что в нём лежит, и «Убрать»
            // убирает только из плейлиста, а не из библиотеки.
            _playlistTitle = new TextBlock
            {
                Text = "Плейлист пуст", FontSize = 11, Foreground = Mute,
                Margin = new Thickness(2, 0, 0, 4)
            };
            top.Children.Add(_playlistTitle);

            _playlistBox = new StackPanel();
            var playlistBorder = new Border
            {
                Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6),
                MaxHeight = 150, Margin = new Thickness(0, 0, 0, 8)
            };
            playlistBorder.Child = new ScrollViewer
            {
                Content = _playlistBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            top.Children.Add(playlistBorder);

            ReloadPlaylists();

            root.Children.Add(top);

            var scroll = new ScrollViewer
            {
                Content = _list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

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

            Content = root;
        }

        private void Reload()
        {
            _list.Children.Clear();
            var tracks = AudioService.List();
            if (tracks.Count == 0)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = "Пока пусто. Нажмите «Добавить файлы…» — mp3, ogg, wav, flac.",
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

            int? id = SelectedPlaylistId;
            if (id == null)
            {
                if (_playlistTitle != null) _playlistTitle.Text = "Плейлист не выбран";
                return;
            }

            var tracks = AudioService.PlaylistTracks(id.Value);
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

                var play = MakeButton("Играть", 70);
                play.Margin = new Thickness(6, 0, 6, 0);
                play.Click += (s, e) => { _playlistMode = true; PlayTrack(track); };
                Grid.SetColumn(play, 1);
                row.Children.Add(play);

                var remove = MakeButton("Убрать", 70);
                remove.Margin = new Thickness(0);
                remove.Click += (s, e) =>
                {
                    AudioService.RemoveFromPlaylist(id.Value, trackId);
                    ReloadPlaylistTracks();
                };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);

                _playlistBox.Children.Add(row);
                position++;
            }
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
            if (Host != null) Host.Loop = true;
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

        private void NextTrack()
        {
            if (_queue.Count == 0)
            {
                PlayPlaylist();
                return;
            }

            _queueIndex = (_queueIndex + 1) % _queue.Count;
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

            AudioService.AddToPlaylist(id.Value, track.Id);
            ReloadPlaylistTracks();
        }

        private FrameworkElement BuildRow(AudioTrackData track)
        {
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
                Text = $"{SizeText(track.Size)} · {(track.Id == _playingId ? "играет" : "готов")}",
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

            var play = MakeButton(track.Id == _playingId ? "Заново" : "Играть", 86);
            play.Click += (s, e) => { _playlistMode = false; PlayTrack(track); };
            buttons.Children.Add(play);

            var toPlaylist = MakeButton("В плейлист", 92);
            toPlaylist.Click += (s, e) => AddToPlaylist(track);
            buttons.Children.Add(toPlaylist);

            var del = MakeButton("Удалить", 76);
            del.Click += (s, e) =>
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
            };
            buttons.Children.Add(del);

            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            row.Child = grid;
            return row;
        }

        private void PlayTrack(AudioTrackData track)
        {
            if (Host != null) Host.Loop = !_playlistMode;
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

        private static Button MakeButton(string text, double width)
        {
            // Кнопки — из общего набора: у них тёмный шаблон приложения,
            // а не системный «виндусовский» вид.
            var button = DmUiKit.Action(text, (s, e) => { }, DmUiKit.ButtonKind.Ghost,
                null, width, 28);
            button.Margin = new Thickness(0, 0, 8, 0);
            return button;
        }
    }
}
