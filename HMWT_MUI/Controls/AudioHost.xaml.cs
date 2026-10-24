using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Services;

namespace HMWT_MUI.Controls
{
    /// <summary>
    /// Звук приложения через WPF MediaPlayer.
    ///
    /// Изначально здесь был WebView2 с Web Audio, но на практике звук не
    /// запускался: Chromium считал крошечный WebView невидимым/окклюдированным
    /// и приостанавливал медиа (слышно было только 0.1 с при закрытии окна).
    /// MediaPlayer не зависит от видимости и автоплей-политики, работает в
    /// свёрнутом окне и на виртуалках.
    ///
    /// Громкость трека и общая громкость перемножаются и ограничиваются
    /// единицей: громче исходного файла MediaPlayer не умеет.
    /// </summary>
    public partial class AudioHost : UserControl
    {
        private readonly MediaPlayer _player = new MediaPlayer();
        private double _master = 0.8;
        private double _gain = 1;
        private double _pendingStart;
        private bool _hasTrack;

        public AudioHost()
        {
            InitializeComponent();

            _player.MediaOpened += (_, __) =>
            {
                if (_hasTrack && _pendingStart > 0.05)
                {
                    try { _player.Position = TimeSpan.FromSeconds(_pendingStart); }
                    catch { }
                }
                _pendingStart = 0;
                ApplyVolume();
                _player.Play();
                Log($"opened, duration={SafeDuration()}, pos={_player.Position.TotalSeconds:0.00}, " +
                    $"volume={_player.Volume:0.00}");
            };
            _player.MediaEnded += (_, __) =>
            {
                // Пока всегда зациклено: музыка/эмбиент. Плейлист переключит
                // трек сам и пересоздаст источник.
                try
                {
                    _player.Position = TimeSpan.Zero;
                    _player.Play();
                }
                catch { }
            };
            _player.MediaFailed += (_, e) =>
            {
                Log($"FAILED: {e.ErrorException?.Message ?? "media failed"}");
            };

            Loaded += (_, __) => ApplyVolume();
        }

        public bool IsPlaying => _hasTrack;

        public TimeSpan Position
        {
            get { try { return _player.Position; } catch { return TimeSpan.Zero; } }
        }

        /// <summary>Играть по URL аудио-хоста (https://hmwt.audio/...) или по пути.</summary>
        public void Play(string url, double gain, double startSeconds = 0)
        {
            string path = ResolvePath(url);
            if (path == null)
            {
                Log($"не найден файл: {url}");
                return;
            }
            PlayFile(path, gain, startSeconds);
        }

        public void PlayFile(string path, double gain, double startSeconds)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log($"нет файла: {path}");
                return;
            }

            _gain = Math.Max(0, Math.Min(2, gain));
            _pendingStart = Math.Max(0, startSeconds);
            _hasTrack = true;

            try
            {
                _player.Close();
                _player.Open(new Uri(path, UriKind.Absolute));
                Log($"open {Path.GetFileName(path)} gain={_gain:0.00} start={_pendingStart:0.00}");
            }
            catch (Exception ex)
            {
                Log($"open error: {ex.Message}");
            }
        }

        public void Stop()
        {
            _hasTrack = false;
            _pendingStart = 0;
            try
            {
                _player.Stop();
                _player.Close();
            }
            catch { }
            Log("stop");
        }

        public void SetMaster(double volume)
        {
            _master = Math.Max(0, Math.Min(1, volume));
            ApplyVolume();
        }

        public void SetTrackGain(double gain)
        {
            _gain = Math.Max(0, Math.Min(2, gain));
            ApplyVolume();
        }

        private void ApplyVolume()
        {
            try { _player.Volume = Math.Max(0, Math.Min(1, _master * _gain)); }
            catch { }
        }

        private string ResolvePath(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            const string prefix = "https://hmwt.audio/";
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string relative = Uri.UnescapeDataString(url.Substring(prefix.Length));
                return Path.Combine(AudioService.Root, relative.Replace('/', '\\'));
            }

            return File.Exists(url) ? url : null;
        }

        private string SafeDuration()
        {
            try
            {
                return _player.NaturalDuration.HasTimeSpan
                    ? _player.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.0") + " с"
                    : "?";
            }
            catch { return "?"; }
        }

        /// <summary>Публичный журнал: окно аудио и сервис пишут сюда же.</summary>
        public static void LogLine(string message) => Log(message);

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "hmwt-audio.log");
                File.AppendAllText(path,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
