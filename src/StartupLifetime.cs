using System;

namespace HarmonyStartupOptimizer;

// Shares startup completion between the optional display and module shutdown.
internal sealed class StartupLifetime
{
    private readonly Func<bool> menuVisible;
    private readonly Action? completed;
    private readonly bool showLoadingStatus;
    private bool waitForDisplay;
    private float nextSample, finishAt = -1;
    internal bool MenuDetected { get; private set; }
    internal bool Finished { get; private set; }
    internal bool CanDrawStatus => !Finished && (showLoadingStatus || MenuDetected);

    internal StartupLifetime(Func<bool> menuVisible, bool displayEnabled, bool showLoadingStatus, Action? completed)
    {
        this.menuVisible = menuVisible;
        waitForDisplay = displayEnabled;
        this.showLoadingStatus = showLoadingStatus;
        this.completed = completed;
    }

    internal bool Tick(float now)
    {
        if (Finished) return false;
        if (finishAt >= 0 && now >= finishAt)
        {
            Finished = true;
            completed?.Invoke();
            return false;
        }
        if (now < nextSample) return false;
        nextSample = now + 0.25f;
        if (!MenuDetected) MenuDetected = menuVisible();
        if (!waitForDisplay) StartCountdown(now);
        return true;
    }

    internal void DisplayRendered(float now) => StartCountdown(now);

    internal void DisplayUnavailable(float now)
    {
        waitForDisplay = false;
        StartCountdown(now);
    }

    private void StartCountdown(float now)
    {
        if (MenuDetected && finishAt < 0) finishAt = now + 5;
    }
}
