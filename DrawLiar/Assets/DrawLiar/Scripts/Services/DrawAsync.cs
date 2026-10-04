using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DrawLiar
{
    internal static class DrawAsync
    {
        public static TaskCompletionSource<T> Completion<T>()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new TaskCompletionSource<T>();
#else
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
#endif
        }

        public static void CancelAfter(CancellationTokenSource source, TimeSpan delay)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DrawBrowserRuntime.CancelAfter(source, delay);
#else
            source.CancelAfter(delay);
#endif
        }

        public static Task Delay(TimeSpan delay, CancellationToken cancellation)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return DrawBrowserRuntime.Delay(delay, cancellation);
#else
            return Task.Delay(delay, cancellation);
#endif
        }

        public static int Increment(ref int value)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return ++value;
#else
            return Interlocked.Increment(ref value);
#endif
        }

        public static int Decrement(ref int value)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return --value;
#else
            return Interlocked.Decrement(ref value);
#endif
        }

        public static int Exchange(ref int value, int replacement)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            int previous = value; value = replacement; return previous;
#else
            return Interlocked.Exchange(ref value, replacement);
#endif
        }

        public static T Exchange<T>(ref T value, T replacement) where T : class
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            T previous = value; value = replacement; return previous;
#else
            return Interlocked.Exchange(ref value, replacement);
#endif
        }

        public static T CompareExchange<T>(ref T value, T replacement, T comparand) where T : class
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            T previous = value;
            if (ReferenceEquals(value, comparand)) value = replacement;
            return previous;
#else
            return Interlocked.CompareExchange(ref value, replacement, comparand);
#endif
        }

        public static int Read(ref int value)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return value;
#else
            return Volatile.Read(ref value);
#endif
        }

        public static T Read<T>(ref T value) where T : class
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return value;
#else
            return Volatile.Read(ref value);
#endif
        }
    }

    internal sealed class DrawAsyncGate
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private readonly Queue<TaskCompletionSource<bool>> _waiting = new Queue<TaskCompletionSource<bool>>();
        private bool _held;

        public async Task WaitAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!_held) { _held = true; return; }
            var completion = new TaskCompletionSource<bool>();
            _waiting.Enqueue(completion);
            using (cancellation.Register(() => completion.TrySetCanceled())) await completion.Task;
        }

        public void Release()
        {
            while (_waiting.Count > 0)
                if (_waiting.Dequeue().TrySetResult(true)) return;
            _held = false;
        }
#else
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        public Task WaitAsync(CancellationToken cancellation) => _semaphore.WaitAsync(cancellation);
        public void Release() => _semaphore.Release();
#endif
    }
}
