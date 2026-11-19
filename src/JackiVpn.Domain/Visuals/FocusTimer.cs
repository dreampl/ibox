namespace JackiVpn.Domain.Visuals;

/// <summary>Where the focus timer is.</summary>
public enum FocusPhase
{
    Idle = 0,
    Work,
    Break,
}

/// <summary>
/// The notch's focus timer: a work stretch followed by a break (25 / 5 minutes by default, each 1–60). Anchored to
/// a deadline, not counted by ticks, so a sleeping PC or a busy UI thread never makes it drift; the caller wakes it
/// with <see cref="Advance"/> when <see cref="NextDue"/> comes.
/// </summary>
public sealed class FocusTimer
{
    private DateTimeOffset _deadline;
    private TimeSpan _left;

    public FocusTimer(int workMinutes = 25, int breakMinutes = 5)
    {
        Work = TimeSpan.FromMinutes(Math.Clamp(workMinutes, 1, 60));
        Break = TimeSpan.FromMinutes(Math.Clamp(breakMinutes, 1, 60));
    }

    public TimeSpan Work { get; }

    public TimeSpan Break { get; }

    public FocusPhase Phase { get; private set; }

    public bool Paused { get; private set; }

    public bool Running => Phase != FocusPhase.Idle && !Paused;

    /// <summary>Length of the current phase.</summary>
    public TimeSpan PhaseLength => Phase == FocusPhase.Break ? Break : Work;

    public void Start(DateTimeOffset now)
    {
        Phase = FocusPhase.Work;
        Paused = false;
        _deadline = now + Work;
    }

    public void Pause(DateTimeOffset now)
    {
        if (!Running)
        {
            return;
        }

        _left = Max(TimeSpan.Zero, _deadline - now);
        Paused = true;
    }

    public void Resume(DateTimeOffset now)
    {
        if (Phase == FocusPhase.Idle || !Paused)
        {
            return;
        }

        Paused = false;
        _deadline = now + _left;
    }

    public void Stop()
    {
        Phase = FocusPhase.Idle;
        Paused = false;
    }

    /// <summary>Time left in the current phase (zero when idle).</summary>
    public TimeSpan Remaining(DateTimeOffset now) => Phase == FocusPhase.Idle ? TimeSpan.Zero
        : Paused ? _left
        : Max(TimeSpan.Zero, _deadline - now);

    /// <summary>0 at the start of the phase … 1 at its end.</summary>
    public double Progress(DateTimeOffset now) => Phase == FocusPhase.Idle ? 0
        : Math.Clamp(1 - (Remaining(now).TotalSeconds / PhaseLength.TotalSeconds), 0, 1);

    /// <summary>When the running phase ends (null when idle or paused).</summary>
    public DateTimeOffset? NextDue => Running ? _deadline : null;

    /// <summary>
    /// Moves on if the current phase is over: work becomes a break (its deadline counted from the end of the work,
    /// so it does not drift), a break ends the timer. Returns the phase that just ended, or null.
    /// </summary>
    public FocusPhase? Advance(DateTimeOffset now)
    {
        if (!Running || now < _deadline)
        {
            return null;
        }

        if (Phase == FocusPhase.Work)
        {
            Phase = FocusPhase.Break;
            _deadline += Break;
            if (now >= _deadline)
            {
                // Asleep through the break too: it is simply over.
                Stop();
            }

            return FocusPhase.Work;
        }

        Stop();
        return FocusPhase.Break;
    }

    /// <summary>"24:59", rounded up so it reads 25:00 at the start and 0:00 only at the end.</summary>
    public static string Clock(TimeSpan left)
    {
        var s = (int)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        return $"{s / 60}:{s % 60:00}";
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>What is in front of a notch's monitor, as far as the notch cares.</summary>
public enum FrontWindow
{
    Normal = 0,
    Maximized,
    FullScreen,
}

/// <summary>How the notch shows itself.</summary>
public enum NotchPresence
{
    /// <summary>The island as usual.</summary>
    Normal = 0,

    /// <summary>A thin line that still answers the pointer (a maximised window is in front).</summary>
    Line,

    /// <summary>A thin line that takes no clicks at all (a full-screen game, video or slides).</summary>
    GhostLine,
}

public static class NotchPresenceRules
{
    /// <summary>
    /// Full screen: a line that clicks pass through (nothing in a game or a video is ever covered). Maximised: a line
    /// that wakes on a resting pointer, so the window's title bar and tabs stay free.
    /// </summary>
    public static NotchPresence For(FrontWindow front, bool hideInFullScreen, bool lineWhenMaximized) => front switch
    {
        FrontWindow.FullScreen when hideInFullScreen => NotchPresence.GhostLine,
        FrontWindow.Maximized when lineWhenMaximized => NotchPresence.Line,
        FrontWindow.FullScreen when lineWhenMaximized => NotchPresence.Line,
        _ => NotchPresence.Normal,
    };
}
