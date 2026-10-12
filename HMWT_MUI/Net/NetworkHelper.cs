using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace HMWT_MUI.Net
{
    /// <summary>
    /// Сетевые вспомогательные функции: внешний (публичный) адрес, локальный адрес
    /// и разбор строки «адрес:порт».
    ///
    /// Внешний адрес нужен тем, кто играет через интернет: локальный 192.168.x.x
    /// работает только внутри домашней сети. Узнать его можно лишь со стороны
    /// провайдера, поэтому спрашиваем публичные сервисы по HTTPS.
    /// </summary>
    public static class NetworkHelper
    {
        /// <summary>Куда обращаемся за внешним адресом. Если сервис недоступен — пробуем следующий.</summary>
        private static readonly string[] PublicAddressSources =
        {
            "https://api.ipify.org",
            "https://checkip.amazonaws.com",
            "https://ifconfig.me/ip"
        };

        /// <summary>Общий клиент: создавать новый на каждый запрос расточительно.</summary>
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            try
            {
                // На .NET Framework по умолчанию может быть выключен TLS 1.2,
                // и тогда HTTPS-запрос падает без понятной причины.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetworkHelper] SecurityProtocol: {ex.Message}");
            }

            return new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        }

        /// <summary>
        /// Внешний адрес или пустая строка, если узнать не удалось
        /// (нет интернета, сервис недоступен, ответ не похож на адрес).
        /// </summary>
        public static async Task<string> GetPublicAddressAsync()
        {
            foreach (var url in PublicAddressSources)
            {
                try
                {
                    var text = (await Http.GetStringAsync(url).ConfigureAwait(false))?.Trim();
                    if (LooksLikeAddress(text))
                    {
                        Debug.WriteLine($"[NetworkHelper] внешний адрес из {url}: {text}");
                        return text;
                    }

                    Debug.WriteLine($"[NetworkHelper] {url} вернул не адрес: {text}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NetworkHelper] {url}: {ex.Message}");
                }
            }
            return "";
        }

        /// <summary>Похож ли ответ на адрес: отсекаем HTML-страницы и прочий мусор.</summary>
        private static bool LooksLikeAddress(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            text = text.Trim();
            if (text.Length > 45 || text.Contains("<") || text.Contains(" ")) return false;

            return IPAddress.TryParse(text, out _);
        }

        /// <summary>
        /// Локальный IPv4 — адрес для игры внутри одной сети. Он же запасной
        /// вариант, когда внешний узнать не удалось.
        /// </summary>
        public static string GetLocalAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ipv4 = Array.Find(host.AddressList, a =>
                    a.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a));
                return ipv4?.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        /// <summary>
        /// Разбирает строку вида «192.168.1.5:47777» на адрес и порт.
        ///
        /// Ведущий обычно присылает адрес и порт одной строкой, а в форме
        /// подключения это два отдельных поля: без разбора такая вставка
        /// уходила в сокет целиком и подключение не удавалось.
        /// </summary>
        public static bool TrySplitHostPort(string text, out string host, out int port, out string error)
        {
            host = "";
            port = NetVersions.DefaultPort;
            error = "";

            text = (text ?? "").Trim();
            if (string.IsNullOrEmpty(text))
            {
                error = "Укажите адрес ведущего.";
                return false;
            }

            string address = text;
            string portText = "";

            // IPv6 в квадратных скобках: [::1]:47777
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                int close = text.IndexOf(']');
                if (close > 0)
                {
                    address = text.Substring(1, close - 1);
                    if (text.Length > close + 1 && text[close + 1] == ':')
                        portText = text.Substring(close + 2);
                }
            }
            else
            {
                int colon = text.LastIndexOf(':');
                // Двоеточий два и больше (без скобок) — это IPv6 без порта, не трогаем
                if (colon > 0 && text.IndexOf(':') == colon)
                {
                    address = text.Substring(0, colon);
                    portText = text.Substring(colon + 1);
                }
            }

            address = address.Trim();
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "Адрес ведущего пустой.";
                return false;
            }

            if (!string.IsNullOrEmpty(portText))
            {
                if (!int.TryParse(portText.Trim(), out int parsed) || parsed <= 0 || parsed > 65535)
                {
                    error = $"Порт «{portText.Trim()}» не подходит: нужно число от 1 до 65535.";
                    return false;
                }
                port = parsed;
            }

            host = address;
            return true;
        }
    }
}
