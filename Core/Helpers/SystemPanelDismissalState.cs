namespace WinCal.Core.Helpers;

internal enum SystemPanelDismissalAction { None, BeginAnimation, CancelAnimation, Hide }

internal sealed class SystemPanelDismissalState
{
    public int Generation { get; private set; }
    public bool IsDismissing { get; private set; }
    private bool _sawForeground, _closed, _animationCompleted;

    public void Reset()
    {
        Generation++;
        IsDismissing = _sawForeground = _closed = _animationCompleted = false;
    }

    public SystemPanelDismissalAction Observe(bool nativeVisible, bool nativeForeground,
        bool hasForeground, bool animationsEnabled)
    {
        if (_closed) return SystemPanelDismissalAction.None;
        if (!nativeVisible) return Close();
        if (nativeForeground)
        {
            _sawForeground = true;
            if (!IsDismissing) return SystemPanelDismissalAction.None;
            Generation++;
            IsDismissing = _animationCompleted = false;
            return SystemPanelDismissalAction.CancelAnimation;
        }
        // Shell passes through foreground=0, including during a normal opening.
        if (!hasForeground || !_sawForeground || IsDismissing)
            return SystemPanelDismissalAction.None;
        if (!animationsEnabled) return Close();
        Generation++;
        IsDismissing = true;
        _animationCompleted = false;
        return SystemPanelDismissalAction.BeginAnimation;
    }

    public bool Complete(int generation)
    {
        if (_closed || !IsDismissing || _animationCompleted || generation != Generation) return false;
        _animationCompleted = true;
        return true;
    }

    private SystemPanelDismissalAction Close()
    {
        Generation++;
        _closed = true;
        IsDismissing = false;
        return SystemPanelDismissalAction.Hide;
    }
}
