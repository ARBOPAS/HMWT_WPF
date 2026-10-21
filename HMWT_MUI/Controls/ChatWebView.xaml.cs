using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HMWT_MUI.Helpers;
using HMWT_MUI.Net;
using Microsoft.Web.WebView2.Core;

namespace HMWT_MUI.Controls
{
    /// <summary>
    /// Лента чата на WebView2. WPF рисует только контейнер: сообщения,
    /// картинки и гифки живут в браузерном движке, поэтому GIF не грузит
    /// поток интерфейса и не тормозит карту с листом.
    ///
    /// Если WebView2 не поднялся, панель остаётся скрытой, а RoomView
    /// продолжает показывать старую нативную ленту — ничего не теряется.
    /// </summary>
    public partial class ChatWebView : UserControl
    {
        public event Action Ready;

        public bool IsReady { get; private set; }

        private CoreWebView2 _core;
        private bool _initStarted;
        private string _pendingJson = "";
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public ChatWebView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_core != null || _initStarted) return;
            _initStarted = true;
            StartAsync();
        }

        private async void StartAsync()
        {
            try
            {
                var environment = await WebView2Host.GetEnvironmentAsync();
                await Web.EnsureCoreWebView2Async(environment);
                if (Web.CoreWebView2 == null) throw new InvalidOperationException("WebView2 не создан");

                _core = Web.CoreWebView2;
                _core.Settings.AreDefaultContextMenusEnabled = false;
                _core.Settings.IsStatusBarEnabled = false;
                _core.Settings.AreDevToolsEnabled = false;
                _core.Settings.IsZoomControlEnabled = false;
                _core.Settings.IsSwipeNavigationEnabled = false;

                Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x0D, 0x16);
                _core.WebMessageReceived += OnWebMessage;
                _core.NavigationCompleted += (s, ev) =>
                {
                    if (_core != null) TrySendPending();
                };

                Web.Visibility = Visibility.Visible;
                Fallback.Visibility = Visibility.Collapsed;
                _core.NavigateToString(PageHtml());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatWeb] WebView2 не поднялся: " + ex.Message);
                IsReady = false;
                Web.Visibility = Visibility.Collapsed;
                Fallback.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Передаёт всю ленту в страницу. Вызывается из UpdateFeed.</summary>
        public void SetFeed(IList<FeedEntry> entries)
        {
            if (entries == null) entries = new List<FeedEntry>();

            var payload = new
            {
                type = "feed",
                entries = entries.Select(e => new
                {
                    kind = (int)e.Kind,
                    time = e.At.ToString("HH:mm"),
                    author = e.AuthorName ?? "",
                    text = e.Text ?? "",
                    formula = e.Formula ?? "",
                    rollType = e.RollType ?? "",
                    total = e.Total,
                    rolls = e.IndividualRolls ?? new List<int>(),
                    image = e.ImageBase64 ?? "",
                    ext = e.ImageExtension ?? "",
                    name = e.ImageName ?? ""
                }).ToList()
            };

            _pendingJson = JsonSerializer.Serialize(payload, _json);
            TrySendPending();
        }

        private void TrySendPending()
        {
            if (!IsReady || _core == null || string.IsNullOrEmpty(_pendingJson)) return;

            try
            {
                _core.PostWebMessageAsJson(_pendingJson);
                _pendingJson = "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatWeb] отправка ленты: " + ex.Message);
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                string type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (type != "ready") return;

                IsReady = true;
                TrySendPending();
                Ready?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatWeb] сообщение из страницы: " + ex.Message);
            }
        }

        private static string PageHtml()
        {
            return @"<!doctype html>
<html><head><meta charset=""utf-8"">
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  html, body { margin: 0; padding: 0; height: 100%; background: #0B0D16; }
  body { font-family: 'Segoe UI', sans-serif; color: #E6EAF5; font-size: 13px; }
  #feed { padding: 6px 8px 14px 8px; display: flex; flex-direction: column; gap: 7px; }
  .row { border: 1px solid #2B3352; background: #151B2E; border-radius: 8px; padding: 8px 10px; }
  .row.me { border-color: #2A6A74; }
  .head { display: flex; justify-content: space-between; gap: 8px; font-size: 11px; color: #8B95B4; margin-bottom: 3px; }
  .author { color: #5FD4DD; font-weight: 600; }
  .text { white-space: pre-wrap; word-break: break-word; line-height: 1.35; }
  .roll { border-color: #6B5A2E; }
  .roll .total { color: #E8C07F; font-weight: 700; font-size: 16px; }
  .roll .meta { color: #C7B27A; font-size: 11px; margin-top: 2px; }
  .system { border-style: dashed; color: #8B95B4; text-align: center; padding: 6px 10px; }
  .image { display: block; margin-top: 7px; max-width: 100%; max-height: 360px; border-radius: 6px; }
  .missing { color: #E06A63; font-size: 11px; margin-top: 6px; }
  .empty { color: #8B95B4; text-align: center; padding: 14px 8px; }
</style></head>
<body><div id=""feed""></div>
<script>
const feed = document.getElementById('feed');
let autoScroll = true;
document.addEventListener('scroll', () => {
  autoScroll = (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 30);
});
window.chrome.webview.addEventListener('message', (e) => {
  const m = e.data;
  if (!m || m.type !== 'feed') return;
  render(m.entries || []);
});
window.chrome.webview.postMessage({ type: 'ready' });

function el(tag, cls, text) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text !== undefined && text !== null) n.textContent = String(text);
  return n;
}
function render(entries) {
  feed.innerHTML = '';
  if (!entries.length) { feed.appendChild(el('div', 'empty', 'Лента пуста. Броски и сообщения появятся здесь.')); return; }

  for (const e of entries) {
    if (e.kind === 2) {
      feed.appendChild(el('div', 'row system', e.text || ''));
      continue;
    }

    const row = el('div', 'row' + (e.kind === 1 ? ' roll' : ''));
    const head = el('div', 'head');
    head.appendChild(el('span', 'author', e.author || 'Игрок'));
    head.appendChild(el('span', '', e.time || ''));
    row.appendChild(head);

    if (e.kind === 1) {
      row.appendChild(el('div', 'text', e.rollType || e.text || 'Бросок'));
      const total = el('div', 'total', 'Итог: ' + (e.total ?? 0));
      row.appendChild(total);
      const details = [];
      if (e.formula) details.push(e.formula);
      if (e.rolls && e.rolls.length) details.push('кубы: ' + e.rolls.join(', '));
      if (details.length) row.appendChild(el('div', 'meta', details.join(' · ')));
    } else {
      if (e.text) row.appendChild(el('div', 'text', e.text));
      if (e.kind === 3 && e.image) {
        const img = document.createElement('img');
        img.className = 'image';
        const ext = (e.ext || '.png').replace('.', '');
        img.src = 'data:image/' + (ext === 'jpg' ? 'jpeg' : ext) + ';base64,' + e.image;
        img.alt = e.name || 'картинка';
        row.appendChild(img);
      } else if (e.kind === 3) {
        row.appendChild(el('div', 'missing', 'Картинка недоступна в истории'));
      }
    }
    feed.appendChild(row);
  }

  if (autoScroll) window.scrollTo(0, document.body.scrollHeight);
}
</script></body></html>";
        }
    }
}
