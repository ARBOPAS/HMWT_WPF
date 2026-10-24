using System;

namespace HMWT_MUI.Models
{
    /// <summary>Один звук/трек в аудиотеке ведущего.</summary>
    public sealed class AudioTrackData
    {
        public int Id { get; set; }

        /// <summary>SHA-256 содержимого: по нему файл находится и кэшируется у игроков.</summary>
        public string Hash { get; set; } = "";

        /// <summary>Путь относительно папки Audio (например, Library\ab12.mp3).</summary>
        public string Path { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>music, ambience, sfx.</summary>
        public string Kind { get; set; } = "music";

        /// <summary>
        /// Своя громкость трека (1 — как в файле). Файлы из разных источников
        /// звучат по-разному, поэтому выравнивает ведущий.
        /// </summary>
        public double Gain { get; set; } = 1.0;

        public int DurationMs { get; set; }

        public long Size { get; set; }

        public DateTime AddedAt { get; set; } = DateTime.Now;

        public override string ToString() => Name;
    }
}
