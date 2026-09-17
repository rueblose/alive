using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    public static class SmoothScrollEngine
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
        public static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
        public static extern uint TimeEndPeriod(uint uMilliseconds);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeMsg
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public Point pt;
        }

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    }

    /// <summary>
    /// The smooth scrolling controller (a game loop over Application.Idle with a 1 ms
    /// timeBeginPeriod). Gives maximum smoothness at the monitor's refresh rate (120–144 Hz and
    /// up) at zero cost when idle.
    /// </summary>
    public sealed class SmoothScroller : IDisposable
    {
        readonly Control _owner;
        readonly Action<int> _applyScroll;
        readonly Func<int> _getMaxScroll;

        float _current;
        float _target;

        /// <summary>
        /// Overshoot past the edge, signed: negative at the top, positive at the bottom. The
        /// owner shifts its content by -Overscroll and gets a rubber-band end. The wheel at an
        /// edge adds impulse here against resistance, and Step() springs the value back to
        /// zero.
        /// </summary>
        public float Overscroll { get { return _over; } }
        float _over;

        readonly Stopwatch _sw = new Stopwatch();
        bool _running;
        bool _idleHooked;

        public float Current { get { return _current; } }
        public float Target { get { return _target; } }
        public bool IsActive { get { return _running; } }

        public SmoothScroller(Control owner, Action<int> applyScroll, Func<int> getMaxScroll)
        {
            _owner = owner;
            _applyScroll = applyScroll;
            _getMaxScroll = getMaxScroll;
        }

        public void SyncPosition(int pos)
        {
            _current = pos;
            _target = pos;
            _over = 0f;
            if (_running) Stop();
        }

        /// <summary>The overshoot limit — the rubber band stretches no further.</summary>
        float MaxOver { get { return 54f * (_owner.DeviceDpi / 96f); } }

        public void OnMouseWheel(int delta, int stepPixels)
        {
            int max = _getMaxScroll();
            if (max < 0) max = 0;

            if (!Theme.SmoothScroll)
            {
                if (max <= 0) return;
                _target -= (delta / 120f) * stepPixels;
                _target = Math.Max(0, Math.Min(max, _target));
                _current = _target;
                _applyScroll((int)Math.Round(_current));
                _owner.Invalidate();
                return;
            }

            // Nothing to scroll means no rubber band: a list that fits on screen entirely must
            // not twitch under the wheel, and beneath a pinned header that reads as broken
            // layout rather than as a response.
            if (max <= 0) return;

            float want = _target - (delta / 120f) * stepPixels;

            // Whatever did not fit into the range goes into the rubber band — with resistance
            // and a hard stop, so the list cannot be dragged half a screen away.
            float excess = want < 0 ? want : (want > max ? want - max : 0f);
            _target = Math.Max(0, Math.Min(max, want));
            if (excess != 0f)
            {
                _over += excess * 0.30f;
                float lim = MaxOver;
                if (_over < -lim) _over = -lim;
                else if (_over > lim) _over = lim;
            }

            Start();
        }

        public void Start()
        {
            if (!_running)
            {
                _running = true;
                SmoothScrollEngine.TimeBeginPeriod(1);
                _sw.Restart();

                if (!_idleHooked)
                {
                    _idleHooked = true;
                    Application.Idle += OnApplicationIdle;
                }
            }
        }

        public void Stop()
        {
            if (_running)
            {
                _running = false;
                if (_idleHooked)
                {
                    _idleHooked = false;
                    Application.Idle -= OnApplicationIdle;
                }
                SmoothScrollEngine.TimeEndPeriod(1);
            }
        }

        void OnApplicationIdle(object sender, EventArgs e)
        {
            SmoothScrollEngine.NativeMsg msg;
            while (_running && !SmoothScrollEngine.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0))
            {
                if (!Step()) break;
                Thread.Sleep(1);
            }
        }

        bool Step()
        {
            float dt = (float)_sw.Elapsed.TotalSeconds;
            _sw.Restart();
            if (dt <= 0.0001f) dt = 0.001f;
            else if (dt > 0.05f) dt = 0.05f;

            int max = _getMaxScroll();
            bool done = false;

            float gFactor = 1.0f - (float)Math.Exp(-15.0f * dt);
            _current += (_target - _current) * gFactor;
            bool posDone = Math.Abs(_target - _current) < 0.25f;
            if (posDone) _current = _target;

            // The band always pulls back to zero — it holds only while the wheel keeps turning.
            _over += (0f - _over) * (1.0f - (float)Math.Exp(-11.0f * dt));
            bool overDone = Math.Abs(_over) < 0.4f;
            if (overDone) _over = 0f;

            done = posDone && overDone;

            if (_current < 0) _current = 0;
            else if (_current > max) _current = max;

            int newScroll = (int)Math.Round(_current);
            _applyScroll(newScroll);
            _owner.Invalidate();
            _owner.Update();

            if (done)
            {
                Stop();
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
