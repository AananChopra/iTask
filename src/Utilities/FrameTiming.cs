using System.Runtime.InteropServices;

namespace iTask.Utilities;

/// <summary>
/// Fine timer resolution while something animates. By default Windows wakes threads on a 15.6 ms
/// tick, which held our per-frame animations to about 64 updates a second even on a 165 Hz
/// display. Asking for 1 ms (per process since Windows 10 2004) while an animation runs lets the
/// frame loop keep up with the screen; it's released as soon as the last animation ends.
/// UI thread only.
/// </summary>
public static class FrameTiming
{
    private static int _active;

    public static void Begin()
    {
        if (_active++ == 0)
            timeBeginPeriod(1);
    }

    public static void End()
    {
        if (_active > 0 && --_active == 0)
            timeEndPeriod(1);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
