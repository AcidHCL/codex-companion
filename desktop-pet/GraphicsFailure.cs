using System;
using System.Collections.Generic;

namespace DesktopPet;

internal static class GraphicsFailure
{
    // A failed WPF render partition cannot be made healthy by swallowing the
    // exception or by replacing one WebView on the same render thread.
    internal const int RenderThreadFailure = unchecked((int)0x88980406);
    internal static Action<Exception>? FatalHandler { get; set; }

    internal static bool IsFatal(Exception error)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>();
        pending.Push(error);
        while (pending.Count > 0 && seen.Count < 32)
        {
            var current = pending.Pop();
            if (!seen.Add(current)) continue;
            if (current.HResult == RenderThreadFailure) return true;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException != null) pending.Push(current.InnerException);
        }
        return false;
    }

    internal static void Report(Exception error)
    {
        if (IsFatal(error)) FatalHandler?.Invoke(error);
    }
}
