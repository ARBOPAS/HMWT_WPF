using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using HMWT_MUI.Services;
using Microsoft.Web.WebView2.Core;

namespace HMWT_MUI.Controls
{
    /// <summary>
    /// Звук приложения: WebView2 с одним аудиоэлементом. Громкость трека и
    /// общая громкость перемножаются, поэтому у каждого файла свой ползунок.
    ///
    /// Один хост на всё приложение (живёт в MainWindow), чтобы музыка не
    /// обрывалась при закрытии окна аудиотеки и чтобы в будущем команды из
    /// комнаты играли в тот же источник.
    /// </summary>
    public partial class AudioHost : UserControl
    {
        private const string HostName = "hmwt.audio";

        private bool _ready;
        private double _master = -1;
        private double _gain = 1;
        private string _pendingUrl;
        private double _pendingGain = 1;
        private bool _initializing;

        public AudioHost()
        {
            InitializeComponent();
            Loaded += async (_, __) => await InitAsync();
        }

        public bool IsReady => _ready;

        /// <summary>Общая громкость (0…1).</summary>
        public void SetMaster(double volume)
        {
            _master = Math.Max(0, Math.Min(1, volume));
            Run($"window.hmwtMaster({Num(_master)})");
        }

        /// <summary>Громкость текущего трека (0…2).</summary>
        public void SetTrackGain(double gain)
        {
            _gain = Math.Max(0, Math.Min(2, gain));
            Run($"window.hmwtGain({Num(_gain)})");
        }

        /// <summary>Играть файл по URL вида https://hmwt.audio/Library/….ogg.</summary>
        public void Play(string url, double gain)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            _gain = Math.Max(0, Math.Min(2, gain));
            if (!_ready)
            {
                _pendingUrl = url;
                _pendingGain = _gain;
                return;
            }

            _pendingUrl = null;
            Run($"window.hmwtPlay({JsonSerializer.Serialize(url)}, {Num(_gain)})");
        }

        public void Stop()
        {
            _pendingUrl = null;
            Run("window.hmwtStop()");
        }

        private async Task InitAsync()
        {
            if (_ready || _initializing) return;
            _initializing = true;

            try
            {
                AudioService.EnsureFolders();
                string page = Path.Combine(AudioService.Root, "host.html");
                File.WriteAllText(page, PageHtml(), new System.Text.UTF8Encoding(false));

                var environment = await Helpers.WebView2Host.GetEnvironmentAsync();
                await Web.EnsureCoreWebView2Async(environment);

                Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    HostName, AudioService.Root, CoreWebView2HostResourceAccessKind.Allow);
                Web.CoreWebView2.NavigationCompleted += (_, __) =>
                {
                    _ready = true;
                    _initializing = false;
                    if (_master >= 0) SetMaster(_master);
                    SetTrackGain(_gain);
                    if (!string.IsNullOrWhiteSpace(_pendingUrl))
                        Play(_pendingUrl, _pendingGain);
                };

                Web.CoreWebView2.Navigate($"https://{HostName}/host.html");
            }
            catch (Exception ex)
            {
                _initializing = false;
                Debug.WriteLine($"[AudioHost] запуск: {ex.Message}");
            }
        }

        private void Run(string js)
        {
            if (!_ready) return;
            try { Web.CoreWebView2.ExecuteScriptAsync(js); }
            catch (Exception ex) { Debug.WriteLine($"[AudioHost] {js}: {ex.Message}"); }
        }

        private static string Num(double value)
            => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static string PageHtml()
        {
            return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><style>html,body{margin:0;background:transparent}</style></head>
<body>
<audio id=""a"" preload=""auto""></audio>
<script>
  const el = document.getElementById('a');
  let master = 0.8, gain = 1;

  function apply() {
    const v = master * gain;
    el.volume = Math.max(0, Math.min(1, v));
  }

  window.hmwtPlay = (url, g) => {
    gain = g; apply();
    el.loop = true;
    el.src = url;
    el.currentTime = 0;
    el.play().catch(() => {});
  };

  window.hmwtStop = () => {
    el.pause();
    el.removeAttribute('src');
    el.load();
  };

  window.hmwtMaster = (g) => { master = g; apply(); };
  window.hmwtGain = (g) => { gain = g; apply(); };

  // Разблокировка после первого жеста (страховка от autoplay-политики).
  document.addEventListener('click', () => el.play().catch(() => {}), { once: true });
</script>
</body></html>";
        }
    }
}
