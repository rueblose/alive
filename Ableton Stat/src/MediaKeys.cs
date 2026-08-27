using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Медиаклавиши клавиатуры — Windows шлёт их окну с фокусом сообщением
    /// WM_APPCOMMAND. Обработать его надо самим и вернуть 1: необработанное DefWindowProc
    /// передаёт дальше по цепочке и в итоге отдаёт системному медиасеансу, то есть
    /// нажатие уедет в чужой плеер, хотя человек смотрит в наше окно.
    ///
    /// Работает, пока окно программы в фокусе. Глобальный перехват (на всю систему)
    /// потребовал бы RegisterHotKey на эти клавиши и отобрал бы их у всех остальных
    /// плееров — этого мы сознательно не делаем.
    /// </summary>
    internal static class MediaKeys
    {
        public const int WM_APPCOMMAND = 0x0319;

        public enum Cmd { None, PlayPause, Play, Pause, Stop, Next, Prev }

        public static Cmd Parse(Message m)
        {
            if (m.Msg != WM_APPCOMMAND) return Cmd.None;

            // GET_APPCOMMAND_LPARAM: старшее слово младшего DWORD без флагов устройства.
            int cmd = (int)(((long)m.LParam >> 16) & 0xFFFF) & 0x0FFF;
            switch (cmd)
            {
                case 11: return Cmd.Next;        // APPCOMMAND_MEDIA_NEXTTRACK
                case 12: return Cmd.Prev;        // APPCOMMAND_MEDIA_PREVIOUSTRACK
                case 13: return Cmd.Stop;        // APPCOMMAND_MEDIA_STOP
                case 14: return Cmd.PlayPause;   // APPCOMMAND_MEDIA_PLAY_PAUSE
                case 46: return Cmd.Play;        // APPCOMMAND_MEDIA_PLAY
                case 47: return Cmd.Pause;       // APPCOMMAND_MEDIA_PAUSE
                default: return Cmd.None;
            }
        }
    }
}
