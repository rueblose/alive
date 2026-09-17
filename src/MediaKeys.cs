using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Keyboard media keys — Windows sends them to the focused window as WM_APPCOMMAND. We have
    /// to handle it ourselves and return 1: left unhandled, DefWindowProc passes it further
    /// down the chain and eventually hands it to the system media session, so the keypress
    /// lands in somebody else's player while the person is looking at our window.
    ///
    /// This works while our window has focus. Catching them globally, across the system, would
    /// mean RegisterHotKey on those keys and taking them away from every other player — which
    /// we deliberately do not do.
    /// </summary>
    internal static class MediaKeys
    {
        public const int WM_APPCOMMAND = 0x0319;

        public enum Cmd { None, PlayPause, Play, Pause, Stop, Next, Prev }

        public static Cmd Parse(Message m)
        {
            if (m.Msg != WM_APPCOMMAND) return Cmd.None;

            // GET_APPCOMMAND_LPARAM: the high word of the low DWORD, without the device flags.
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
