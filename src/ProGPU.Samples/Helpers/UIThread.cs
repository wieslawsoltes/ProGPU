using System;
using System.Collections.Generic;

namespace ProGPU.Samples;

public static class UIThread
{
    private static readonly Queue<Action> _queue = new();

    public static void Post(Action action)
    {
        lock (_queue)
        {
            _queue.Enqueue(action);
        }
    }

    public static void RunPending()
    {
        List<Action>? local = null;
        lock (_queue)
        {
            if (_queue.Count > 0)
            {
                local = new List<Action>(_queue);
                _queue.Clear();
            }
        }

        if (local != null)
        {
            foreach (var act in local)
            {
                try
                {
                    act();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error running posted UI action: {ex.Message}");
                }
            }
        }
    }
}
