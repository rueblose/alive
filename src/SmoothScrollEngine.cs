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
    /// Контроллер плавного скролла (GameLoop через Application.Idle и 1ms timeBeginPeriod).
    /// Обеспечивает максимальную плавность на частоте монитора (120–144Hz+) с нулевой нагрузкой в покое.
    /// </summary>
    public sealed class SmoothScroller : IDisposable
    {
        readonly Control _owner;
        readonly Action<int> _applyScroll;
        readonly Func<int> _getMaxScroll;

        float _current;
        float _target;

        /// <summary>
        /// Перелёт за край, со знаком: отрицательный — вверху, положительный — внизу.
        /// Владелец сдвигает содержимое на -Overscroll и получает резиновый конец.
        /// Колесо у края добавляет сюда импульс с сопротивлением, а Step() возвращает
        /// значение к нулю пружиной.
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

        /// <summary>Предел перелёта — дальше резинка не тянется.</summary>
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

            // Если прокручивать нечего — не резинить: список, который весь на экране,
            // от колеса дёргаться не должен, а под закреплённой шапкой это ещё и
            // читается как сползший вёрстку, а не как отклик.
            if (max <= 0) return;

            float want = _target - (delta / 120f) * stepPixels;

            // То, что не влезло в диапазон, уходит в резинку — с сопротивлением и
            // упором, чтобы список нельзя было утянуть на пол-экрана.
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

            // Резинка всегда тянет к нулю — она держится только пока колесо крутят.
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
