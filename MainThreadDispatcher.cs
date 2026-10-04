using System;
using System.Collections.Generic;
using UnityEngine;

// AdMob's rewarded ad callbacks (ad closed, reward earned, etc.) aren't
// guaranteed to fire on Unity's main thread — they can come from a native
// background thread. Any Unity API call made off the main thread is
// undefined behavior: sometimes fine, sometimes an intermittent native
// crash (SIGTRAP/SIGSEGV) with no catchable C# exception — exactly what
// was happening. This queues an action to run safely on the main thread
// during the next Update(), instead of running it immediately wherever it
// was called from.
public class MainThreadDispatcher : MonoBehaviour
{
    public static MainThreadDispatcher Instance { get; private set; }

    private readonly Queue<Action> queue = new Queue<Action>();
    private readonly object queueLock = new object();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public void Enqueue(Action action)
    {
        if (action == null) return;
        lock (queueLock)
        {
            queue.Enqueue(action);
        }
    }

    void Update()
    {
        lock (queueLock)
        {
            while (queue.Count > 0)
            {
                Action action = queue.Dequeue();
                try
                {
                    action.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"MainThreadDispatcher: exception running queued action: {e}");
                }
            }
        }
    }
}
