using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Проверка UPnP: отвечает ли роутер на поиск устройств.
    ///
    /// Зачем только проверка, а не автоматический проброс порта. UPnP работает
    /// далеко не везде: провайдеры его выключают, а часть роутеров не отвечает
    /// на поиск вовсе (проверено на машине разработчика — ответов нет). Если
    /// роутер не отвечает, «автоматический проброс» превращается в код, который
    /// молча ничего не делает и создаёт ложное ощущение, что порт открыт.
    ///
    /// Поэтому приложение честно сообщает результат проверки и предлагает
    /// рабочие пути: ручной проброс порта либо виртуальную локальную сеть.
    ///
    /// Запрос читающий: мы только спрашиваем роутер, ничего не меняем.
    /// </summary>
    public static class UpnpProbe
    {
        /// <summary>Результат проверки: отвечает ли роутер и что он о себе сообщает.</summary>
        public sealed class Result
        {
            public bool Responded { get; set; }
            public string Location { get; set; } = "";
            public string Description { get; set; } = "";
        }

        /// <summary>Ищем шлюз по протоколу SSDP. Время ожидания — не больше указанного.</summary>
        public static Result Probe(int timeoutMs = 3000)
        {
            var result = new Result();
            UdpClient udp = null;
            try
            {
                const string request =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

                udp = new UdpClient();
                udp.Client.ReceiveTimeout = timeoutMs;
                byte[] payload = Encoding.ASCII.GetBytes(request);
                udp.Send(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = udp.Receive(ref remote);
                        string text = Encoding.ASCII.GetString(data);

                        int at = text.IndexOf("LOCATION:", StringComparison.OrdinalIgnoreCase);
                        if (at < 0) continue;

                        string location = text.Substring(at + 9).Split('\n')[0].Trim();
                        result.Responded = true;
                        result.Location = location;
                        result.Description = "роутер отвечает на UPnP";
                        return result;
                    }
                    catch (SocketException)
                    {
                        break;   // истёк таймаут ожидания
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UPnP] проверка: {ex.Message}");
                result.Description = "проверка UPnP не удалась: " + ex.Message;
            }
            finally
            {
                try { udp?.Close(); } catch { }
            }

            if (!result.Responded && string.IsNullOrEmpty(result.Description))
                result.Description = "роутер не отвечает на UPnP (проброс порта придётся настроить вручную)";

            return result;
        }
    }
}
