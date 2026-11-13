using WinCal.Core.Helpers;

internal static class SystemPanelDismissalStateTests
{
    public static void OpeningAndZeroForegroundDoNotDismiss()
    {
        var state = new SystemPanelDismissalState();
        Equal(SystemPanelDismissalAction.None, state.Observe(true, false, true, true));
        Equal(SystemPanelDismissalAction.None, state.Observe(true, true, true, true));
        Equal(SystemPanelDismissalAction.None, state.Observe(true, false, false, true));
        Check(!state.IsDismissing, "opening and zero foreground must remain visible");
    }

    public static void FocusLossStartsOnlyOneAnimation()
    {
        var state = Open();
        Equal(SystemPanelDismissalAction.BeginAnimation, state.Observe(true, false, true, true));
        int generation = state.Generation;
        Check(state.IsDismissing, "focus loss must start dismissal while native is still visible");
        Equal(SystemPanelDismissalAction.None, state.Observe(true, false, true, true));
        Check(generation == state.Generation, "duplicate events must not restart the animation");
    }

    public static void NativeHideEndsDismissalOnce()
    {
        var state = Open();
        Equal(SystemPanelDismissalAction.BeginAnimation, state.Observe(true, false, true, true));
        int generation = state.Generation;
        Equal(SystemPanelDismissalAction.Hide, state.Observe(false, false, true, true));
        Equal(SystemPanelDismissalAction.None, state.Observe(false, false, true, true));
        Check(!state.Complete(generation), "native hide must invalidate pending animation completion");
        Check(!state.IsDismissing, "native hide must finish dismissal");
    }

    public static void AnimationKeepsHoleUntilNativeHide()
    {
        var state = Open();
        state.Observe(true, false, true, true);
        Check(state.Complete(state.Generation), "current animation must complete");
        Check(!state.Complete(state.Generation), "duplicate completion must be ignored");
        Check(state.IsDismissing, "keep the calendar hole until native has finished sliding");
        Equal(SystemPanelDismissalAction.None, state.Observe(true, false, true, true));
        Equal(SystemPanelDismissalAction.Hide, state.Observe(false, false, true, true));
    }

    public static void ForegroundReturnCancelsOldAnimation()
    {
        var state = Open();
        state.Observe(true, false, true, true);
        int generation = state.Generation;
        Equal(SystemPanelDismissalAction.CancelAnimation, state.Observe(true, true, true, true));
        Check(!state.IsDismissing && !state.Complete(generation), "old completion must not hide a reopened panel");
        Equal(SystemPanelDismissalAction.BeginAnimation, state.Observe(true, false, true, true));
        Check(state.Generation != generation && state.Complete(state.Generation), "next dismissal needs its own completion");
    }

    public static void ForegroundReturnAfterAnimationStillRestores()
    {
        var state = Open();
        state.Observe(true, false, true, true);
        int generation = state.Generation;
        Check(state.Complete(generation), "current animation must complete");
        Equal(SystemPanelDismissalAction.CancelAnimation, state.Observe(true, true, true, true));
        Check(!state.IsDismissing && !state.Complete(generation), "completed outgoing content must be restorable on rapid reopen");
    }

    public static void DisabledAnimationsHideImmediately()
    {
        var state = Open();
        Equal(SystemPanelDismissalAction.Hide, state.Observe(true, false, true, false));
        Equal(SystemPanelDismissalAction.None, state.Observe(true, false, true, false));
        Check(!state.IsDismissing, "motion disabled must not wait for an animation");
    }

    public static void NewSessionRejectsOldCompletion()
    {
        var state = Open();
        state.Observe(true, false, true, true);
        int generation = state.Generation;
        state.Reset();
        Check(!state.Complete(generation), "reset must invalidate old animation callbacks");
        Equal(SystemPanelDismissalAction.None, state.Observe(true, false, true, true));
        Equal(SystemPanelDismissalAction.Hide, state.Observe(false, false, false, true));
    }

    private static SystemPanelDismissalState Open()
    {
        var state = new SystemPanelDismissalState();
        state.Reset();
        state.Observe(true, true, true, true);
        return state;
    }
    private static void Equal(SystemPanelDismissalAction expected, SystemPanelDismissalAction actual)
        => Check(expected == actual, $"expected {expected}, got {actual}");
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
