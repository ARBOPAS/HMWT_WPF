using System;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Локальная громкость игрока и общая громкость приложения.
    /// Хранится в Settings, в комнату не уходит: у каждого свои наушники.
    /// </summary>
    public static class AudioSettings
    {
        private const string MasterKey = "AudioMasterVolume";
        private const double DefaultMaster = 0.8;

        private static double _master = -1;

        /// <summary>Изменилась общая громкость (0…1).</summary>
        public static event Action<double> MasterChanged;

        public static double Master
        {
            get
            {
                if (_master < 0) _master = Load();
                return _master;
            }
        }

        public static void SetMaster(double volume)
        {
            volume = Math.Max(0, Math.Min(1, volume));
            if (Math.Abs(volume - Master) < 0.001) return;

            _master = volume;
            Save(volume);
            MasterChanged?.Invoke(volume);
        }

        private static double Load()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT Value FROM Settings WHERE Key = @k", conn);
                cmd.Parameters.AddWithValue("@k", MasterKey);
                var value = cmd.ExecuteScalar();
                if (value != null && value != DBNull.Value &&
                    double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double volume))
                {
                    return Math.Max(0, Math.Min(1, volume));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] громкость: {ex.Message}");
            }
            return DefaultMaster;
        }

        private static void Save(double volume)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    @"INSERT INTO Settings (Key, Value) VALUES (@k, @v)
                      ON CONFLICT(Key) DO UPDATE SET Value = @v;", conn);
                cmd.Parameters.AddWithValue("@k", MasterKey);
                cmd.Parameters.AddWithValue("@v", volume.ToString(
                    "0.###", System.Globalization.CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] сохранение громкости: {ex.Message}");
            }
        }
    }
}
