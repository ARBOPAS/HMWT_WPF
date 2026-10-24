using System;
using System.IO;
using System.Windows;
using HMWT_MUI.Controls;
using HMWT_MUI.Net;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Играет то, что ведущий включил в комнате: находит файл в кэше (или
    /// в библиотеке у ведущего), докачивает недостающий и запускает с той
    /// позиции, на которой трек уже идёт.
    /// </summary>
    public static class AudioPlayback
    {
        private static AudioState _pending;
        private static string _currentHash;

        public static void Initialize()
        {
            var session = RoomSession.Instance;
            session.AudioReceived += state => OnUi(() => Play(state));
            session.AudioStopped += () => OnUi(Stop);
            session.AudioGainChanged += state => OnUi(() => SetGain(state));
            session.AudioChunkReceived += chunk => OnUi(() => Chunk(chunk));
        }

        private static AudioHost Host
            => (Application.Current?.MainWindow as MainWindow)?.AudioHost;

        /// <summary>Ведущий и сервер в одном процессе: играем напрямую, не полагаясь на события.</summary>
        public static void PlayState(AudioState state) => OnUi(() => Play(state));

        public static void StopState() => OnUi(Stop);

        public static void GainState(AudioState state) => OnUi(() => SetGain(state));

        private static void Play(AudioState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Hash)) return;
            // Если этот трек уже играет (например, только что включил сам
            // диджей), не перезапускаем его с нуля.
            if (string.Equals(_currentHash, state.Hash, StringComparison.OrdinalIgnoreCase) &&
                Host?.IsPlaying == true)
            {
                Host.SetTrackGain(state.Gain);
                return;
            }

            _currentHash = state.Hash;

            string path = FindFile(state.Hash, state.Extension);
            if (path == null)
            {
                _pending = state;
                RoomSession.Instance.RequestAudio(state.Hash);
                return;
            }

            Start(path, state);
        }

        private static string FindFile(string hash, string extension)
        {
            string cached = AudioTransfer.FindCached(hash, extension);
            if (cached != null) return cached;

            // У ведущего трек лежит в библиотеке, а не в кэше.
            var track = AudioService.FindByHash(hash);
            string library = AudioService.ResolvePath(track);
            return library != null && File.Exists(library) ? library : null;
        }

        private static void Start(string path, AudioState state)
        {
            string root = AudioService.Root.TrimEnd('\\', '/');
            string relative = path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                : path.Replace('\\', '/');
            double start = Math.Max(0, state.PositionMs) / 1000.0;
            Host?.Play("https://hmwt.audio/" + relative, state.Gain, start);
        }

        private static void Stop()
        {
            _pending = null;
            _currentHash = null;
            Host?.Stop();
        }

        private static void SetGain(AudioState state)
        {
            if (state == null || !string.Equals(_currentHash, state.Hash, StringComparison.OrdinalIgnoreCase))
                return;
            Host?.SetTrackGain(state.Gain);
        }

        private static void Chunk(AudioChunk chunk)
        {
            string path = AudioTransfer.AddChunk(chunk);
            if (path == null || _pending == null) return;
            if (!string.Equals(_pending.Hash, chunk.Hash, StringComparison.OrdinalIgnoreCase)) return;

            var state = _pending;
            _pending = null;
            Start(path, state);
        }

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "hmwt-audio.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [playback] {message}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private static void OnUi(Action action)
        {
            var app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }
    }
}
