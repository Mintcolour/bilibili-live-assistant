namespace BilibiliLiveAssistant.App;

// UI-thread state only: no assets, timers or queued animations. Retarget from
// the current visual position, keeping a new row's original fade deadline.
internal sealed class CompactRowMotion
{
    private double _fromY, _toY, _moveAt, _moveSeconds;
    private double _fromAlpha = 1, _toAlpha = 1, _fadeAt, _fadeSeconds;
    internal bool Positioned { get; private set; }
    internal bool Leaving { get; private set; }
    internal double TargetY => _toY;
    internal (float Y, float Alpha) Sample(double now) =>
        ((float)Value(_fromY, _toY, _moveAt, _moveSeconds, now), (float)Value(_fromAlpha, _toAlpha, _fadeAt, _fadeSeconds, now));
    internal bool Running(double now) => Positioned &&
        (now < _moveAt + _moveSeconds && _fromY != _toY || now < _fadeAt + _fadeSeconds && _fromAlpha != _toAlpha);
    private static double Value(double from, double to, double at, double duration, double now)
    {
        var progress = duration <= 0 ? 1 : Math.Clamp((now - at) / duration, 0, 1);
        return from + (to - from) * (1 - Math.Pow(1 - progress, 3));
    }
    internal void Place(float y, double now, double seconds, bool animate, bool entering)
    {
        if (!Positioned || !animate)
        {
            Positioned = true; Leaving = false;
            _fromY = y + (animate && entering ? 14 : 0); _toY = y; _moveAt = now; _moveSeconds = animate && entering ? seconds : 0;
            _fromAlpha = animate && entering ? 0 : 1; _toAlpha = 1; _fadeAt = now; _fadeSeconds = animate && entering ? seconds : 0;
            return;
        }
        if (Math.Abs(_toY - y) < .01 && !Leaving) return;
        var current = Sample(now);
        _fromY = current.Y; _toY = y; _moveAt = now; _moveSeconds = seconds;
        // Returning a leaving row is only possible after a layout change.
        if (Leaving) { _fromAlpha = current.Alpha; _toAlpha = 1; _fadeAt = now; _fadeSeconds = seconds; }
        Leaving = false;
    }
    internal void Leave(float top, int height, double now, double seconds)
    {
        if (Leaving) return;
        var current = Sample(now); Leaving = true;
        _fromY = current.Y; _toY = Math.Min(current.Y - 14, top - height - 4); _moveAt = now; _moveSeconds = seconds;
        _fromAlpha = current.Alpha; _toAlpha = 0; _fadeAt = now; _fadeSeconds = seconds;
    }
}
