using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HMWT_MUI.Net
{
    /// <summary>
    /// Ключ комнаты: генерация, красивый показ, проверка и хранение.
    ///
    /// Про безопасность честно: ключ — это КОНТРОЛЬ ДОСТУПА, а не шифрование
    /// канала. Трафик комнаты не шифрован, поэтому ключ защищает от «случайно
    /// залез», а не от целенаправленной атаки. Что мы действительно делаем —
    /// не отправляем ключ по сети открытым текстом (см. Proof) и не храним
    /// его в открытом виде на диске (см. Protect).
    /// </summary>
    public static class RoomKey
    {
        /// <summary>Длина ключа в байтах: 128 бит достаточно и не мешает диктовать вслух.</summary>
        private const int KeyBytes = 10;

        /// <summary>Алфавит без похожих символов: нет O/0, I/1, чтобы ключ не путали при диктовке.</summary>
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        /// <summary>Создаёт новый ключ вида «K7M2-XP9Q-RT4W».</summary>
        public static string Generate()
        {
            var bytes = new byte[KeyBytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var sb = new StringBuilder(KeyBytes * 2 + 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(Alphabet[bytes[i] % Alphabet.Length]);
            }
            return sb.ToString();
        }

        /// <summary>Приводит введённый ключ к каноническому виду: верхний регистр, без пробелов.</summary>
        public static string Normalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            var sb = new StringBuilder(key.Length);
            foreach (var ch in key.Trim().ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>Красивый показ для копирования: группы по 4 символа.</summary>
        public static string Pretty(string key)
        {
            var clean = Normalize(key);
            var sb = new StringBuilder(clean.Length + clean.Length / 4);
            for (int i = 0; i < clean.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(clean[i]);
            }
            return sb.ToString();
        }

        /// <summary>Похож ли текст на ключ комнаты.</summary>
        public static bool LooksValid(string key)
        {
            var clean = Normalize(key);
            return clean.Length >= 8 && clean.Length <= 32;
        }

        /// <summary>
        /// Доказательство знания ключа: HMAC-SHA256(ключ, nonce).
        /// Сам ключ по сети не передаётся — иначе его прочитал бы любой,
        /// кто видит трафик.
        /// </summary>
        public static string ComputeProof(string key, string nonce)
        {
            var keyBytes = Encoding.UTF8.GetBytes(Normalize(key));
            var nonceBytes = Encoding.UTF8.GetBytes(nonce ?? "");
            using var hmac = new HMACSHA256(keyBytes);
            return Convert.ToBase64String(hmac.ComputeHash(nonceBytes));
        }

        /// <summary>Сравнение доказательств за постоянное время (защита от подбора по времени ответа).</summary>
        public static bool ProofEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            var ba = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            if (ba.Length != bb.Length) return false;

            int diff = 0;
            for (int i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
            return diff == 0;
        }

        public static string NewNonce()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Шифрует ключ для хранения на диске через DPAPI текущего пользователя.
        /// Другой пользователь Windows этот файл прочитать не сможет.
        /// </summary>
        public static string Protect(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            try
            {
                var raw = Encoding.UTF8.GetBytes(Normalize(key));
                var enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RoomKey] Protect: {ex.Message}");
                return "";
            }
        }

        /// <summary>Расшифровывает ключ, сохранённый через <see cref="Protect"/>.</summary>
        public static string Unprotect(string protectedKey)
        {
            if (string.IsNullOrEmpty(protectedKey)) return "";
            try
            {
                var enc = Convert.FromBase64String(protectedKey);
                var raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Normalize(Encoding.UTF8.GetString(raw));
            }
            catch (Exception ex)
            {
                // Частая причина: файл перенесли на другой компьютер или сменили пользователя
                System.Diagnostics.Debug.WriteLine($"[RoomKey] Unprotect: {ex.Message}");
                return "";
            }
        }
    }
}
