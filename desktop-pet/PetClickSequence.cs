using System;

namespace DesktopPet;

// Count a burst of pokes, not WPF's alternating single/double-click count.
// Three quiet seconds let the companion calm down; a drag resets the burst.
internal sealed class PetClickSequence
{
    internal static readonly TimeSpan QuietReset = TimeSpan.FromSeconds(3);
    private DateTimeOffset? _lastClick;
    private int _count;

    internal int Register(DateTimeOffset now)
    {
        _count = _lastClick is not { } previous || now < previous || now - previous >= QuietReset
            ? 1 : Math.Min(100, _count + 1);
        _lastClick = now;
        return _count;
    }

    internal void Reset() { _lastClick = null; _count = 0; }
    internal static string Expression(int count) => count >= 3 ? "angry" : count == 2 ? "startled" : "happy";
}
