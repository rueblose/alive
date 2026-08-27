using System.Drawing;

namespace AbletonManager
{
    /// <summary>
    /// Палитра клипов и треков Live: 70 цветов, сетка 14×5 — ровно та, что открывается
    /// в контекстном меню трека. В сете хранится только номер цвета (&lt;Color Value="23"/&gt;),
    /// сами значения зашиты в бинарник Live, отдельным файлом их в установке нет.
    ///
    /// Раскладка сверена с собственным файлом Ableton
    /// Program\Push2\qml\Ableton\Push\Assets\css\dark.css: там лежит таблица
    /// live_to_push2_colors — соответствие всех 70 индексов Live цветам экрана Push,
    /// пятью строками по 14. Порядок оттенков в каждой строке (красный, оранжевый,
    /// коричневый, жёлтый, салатовый, зелёный, бирюзовый, голубой, синий, фиолетовый,
    /// пурпурный, розовый) совпадает с таблицей ниже колонка в колонку, а 14-я колонка
    /// у Ableton отдана серому — в палитре Push серого нет, поэтому там она отображена
    /// в произвольный оттенок.
    /// </summary>
    public static class LiveColors
    {
        static readonly int[] Hex = new int[]
        {
            // строка 1 — пастельные
            0xFD95A7, 0xFDA43A, 0xCB9834, 0xF7F384, 0xC0F932, 0x32FD42, 0x39FDAA,
            0x65FEE8, 0x8DC6FD, 0x5682E1, 0x93A9FC, 0xD670E2, 0xE3569F, 0xFFFFFF,
            // строка 2 — насыщенные
            0xFC393D, 0xF46C20, 0x98714E, 0xFEEE4A, 0x8BFD70, 0x44C121, 0x1EBEAF,
            0x31E9FD, 0x22A5EB, 0x127EBE, 0x8870E1, 0xB579C4, 0xFD42D2, 0xD0D0D0,
            // строка 3 — светлые приглушённые
            0xE0685D, 0xFDA378, 0xD2AC75, 0xEDFEB2, 0xD2E39C, 0xBACF79, 0x9CC38F,
            0xD5FDE2, 0xCEF1F8, 0xB9C2E2, 0xCDBCE3, 0xAE9AE3, 0xE5DCE1, 0xA9A9A9,
            // строка 4 — землистые
            0xC5928C, 0xB68259, 0x98836B, 0xBFB96E, 0xA6BC25, 0x7EAF52, 0x8AC2BA,
            0x9CB3C3, 0x86A5C1, 0x8494CA, 0xA596B4, 0xBEA0BD, 0xBB7296, 0x7B7B7B,
            // строка 5 — тёмные
            0xAD3436, 0xA75135, 0x714F42, 0xDAC229, 0x85952B, 0x559E38, 0x1B9B8E,
            0x266383, 0x1B3393, 0x3154A0, 0x624EAB, 0xA24EAB, 0xCA326E, 0x3C3C3C,
        };

        public const int Count = 70;

        /// <summary>Серый на случай, когда цвет не записан (старые сеты) или индекс чужой.</summary>
        public static readonly Color Fallback = Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93);

        public static Color Get(int index)
        {
            if (index < 0 || index >= Hex.Length) return Fallback;
            int v = Hex[index];
            return Color.FromArgb(0xFF, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }
    }
}
