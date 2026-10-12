using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HMWT_MUI.Net
{
    /// <summary>
    /// Чтение и запись сообщений поверх потока.
    ///
    /// TCP — это поток байтов, а не сообщения: без префикса длины два сообщения
    /// склеятся в одно, а третье придёт половинкой. Поэтому каждое сообщение
    /// пишется как [4 байта длины][UTF-8 JSON].
    /// </summary>
    public static class NetFraming
    {
        public static async Task WriteAsync(Stream stream, NetMessage message, CancellationToken token)
        {
            if (stream == null || message == null) return;

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, NetJson.Options);
            if (payload.Length > NetLimits.MaxMessageBytes)
                throw new InvalidOperationException($"Сообщение слишком большое: {payload.Length} байт");

            byte[] header = BitConverter.GetBytes(payload.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);

            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        /// <summary>
        /// Читает одно сообщение. Возвращает null, если соединение закрыто штатно.
        /// Заголовок читается строго целиком: частичное чтение — обычное дело в сети.
        /// </summary>
        public static async Task<NetMessage> ReadAsync(Stream stream, CancellationToken token)
        {
            byte[] header = new byte[4];
            if (!await ReadExactAsync(stream, header, 4, token).ConfigureAwait(false))
                return null;

            int length = BitConverter.ToInt32(header, 0);
            if (!BitConverter.IsLittleEndian) length = System.Net.IPAddress.NetworkToHostOrder(length);

            if (length <= 0) return null;
            if (length > NetLimits.MaxMessageBytes)
                throw new InvalidOperationException($"Недопустимая длина сообщения: {length} байт");

            byte[] payload = new byte[length];
            if (!await ReadExactAsync(stream, payload, length, token).ConfigureAwait(false))
                return null;

            try
            {
                return JsonSerializer.Deserialize<NetMessage>(payload, NetJson.Options);
            }
            catch (JsonException)
            {
                // Битого сообщения достаточно, чтобы пропустить его и не рвать сессию
                return new NetMessage { Type = "" };
            }
        }

        /// <summary>Читает ровно count байт. false — поток закончился.</summary>
        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, offset, count - offset, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return false; }
                catch (IOException) { return false; }
                catch (ObjectDisposedException) { return false; }

                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        public static byte[] EncodeUtf8(string text) => Encoding.UTF8.GetBytes(text ?? "");
    }
}
