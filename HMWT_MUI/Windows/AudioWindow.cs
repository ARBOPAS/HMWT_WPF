using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Controls;
using HMWT_MUI.Models;
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

        public AudioWindow()
        {
            Title = "Аудиотека";
            Width = 560;
            Height = 560;
            MinWidth = 460;
            MinHeight = 380;
            Background = Bg;
            Foreground = Ink;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Build();
            Reload();
        }

        private AudioHost Host
        {
            get
            {
                var main = Application.Current?.MainWindow as MainWindow;
                return main?.AudioHost;
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
                Host?.Stop();
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

            var scroll = new ScrollViewer
            {
                Content = _list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var hint = new TextBlock
            {
                Text = "Передача звука игрокам — следующий шаг. Сейчас проверяем сам плеер.",
                FontSize = 11, Foreground = Mute, Margin = new Thickness(0, 10, 0, 0),
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
                if (track.Id == _playingId) Host?.SetTrackGain(gain);
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
            play.Click += (s, e) => PlayTrack(track);
            buttons.Children.Add(play);

            var del = MakeButton("Удалить", 76);
            del.Click += (s, e) =>
            {
                if (MessageBox.Show(this, $"Удалить «{track.Name}» из библиотеки?", "Аудиотека",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (track.Id == _playingId) { Host?.Stop(); _playingId = 0; }
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
            string full = AudioService.ResolvePath(track);
            if (full == null || !File.Exists(full))
            {
                MessageBox.Show(this, "Файл трека не найден.", "Аудиотека",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var host = Host;
            if (host == null)
            {
                MessageBox.Show(this, "Аудио-хост ещё не готов. Попробуйте через пару секунд.",
                    "Аудиотека", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string relative = track.Path.Replace('\\', '/').TrimStart('/');
            host.Play("https://hmwt.audio/" + relative, track.Gain);
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
                MessageBox.Show(this,
                    $"Добавлено: {added}. Пропущено (не аудио или уже есть): {skipped}.",
                    "Аудиотека", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static string SizeText(long bytes)
        {
            if (bytes <= 0) return "—";
            double mb = bytes / 1024.0 / 1024.0;
            return mb >= 1 ? $"{mb:0.0} МБ" : $"{bytes / 1024} КБ";
        }

        private static Button MakeButton(string text, double width) => new Button
        {
            Content = text,
            MinWidth = width,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Background = ButtonBg,
            Foreground = Ink,
            BorderBrush = Line,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }
}
