namespace AbletonManager
{
    /// <summary>
    /// Общая тональность сета: в .als она лежит как два числа —
    /// &lt;ScaleInformation&gt;&lt;Root Value="0"/&gt;&lt;Name Value="0"/&gt;&lt;/ScaleInformation&gt;
    /// прямо в LiveSet (такие же узлы есть у каждого клипа, их брать нельзя).
    ///
    /// Порядок ладов не выдуман: он взят из документации LOM, вшитой в сам
    /// Ableton Live 12 Suite.exe, где перечислены «default scale names that can be saved
    /// with a set and recalled». Оттуда же: «The root can be a number between 0 and 11,
    /// with 0 corresponding to C and 11 corresponding to B».
    /// </summary>
    public static class Scales
    {
        static readonly string[] Names =
        {
            "Major", "Minor", "Dorian", "Mixolydian", "Lydian", "Phrygian", "Locrian",
            "Whole Tone", "Half-whole Dim.", "Whole-half Dim.", "Minor Blues",
            "Minor Pentatonic", "Major Pentatonic", "Harmonic Minor", "Harmonic Major",
            "Dorian #4", "Phrygian Dominant", "Melodic Minor", "Lydian Augmented",
            "Lydian Dominant", "Super Locrian", "Bhairav", "Hungarian Minor",
            "8-Tone Spanish", "Hirajoshi", "In-Sen", "Iwato", "Kumoi", "Pelog Selisir",
            "Pelog Tembung", "Messiaen 3", "Messiaen 4", "Messiaen 5", "Messiaen 6",
            "Messiaen 7"
        };

        static readonly string[] Sharp = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        static readonly string[] Flat  = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

        /// <summary>
        /// Ноты для выбора в фильтре. Обе записи сразу: как ноту напишут в сете, зависит
        /// от PreferFlatRootNote, а нота при этом одна и та же.
        /// </summary>
        public static readonly string[] RootChoices =
        {
            "C", "C# / Db", "D", "D# / Eb", "E", "F",
            "F# / Gb", "G", "G# / Ab", "A", "A# / Bb", "B"
        };

        public static int ScaleCount { get { return Names.Length; } }

        public static string RootName(int root, bool preferFlat)
        {
            if (root < 0 || root > 11) return "";
            return preferFlat ? Flat[root] : Sharp[root];
        }

        public static string ScaleName(int index)
        {
            return index >= 0 && index < Names.Length ? Names[index] : "";
        }

        /// <summary>«C Major». Пусто, если сет сохранён версией Live без общей тональности.</summary>
        public static string Format(int root, int scaleIndex, bool preferFlat)
        {
            string r = RootName(root, preferFlat);
            if (r.Length == 0) return "";
            string s = ScaleName(scaleIndex);
            return s.Length == 0 ? r : r + " " + s;
        }
    }
}
