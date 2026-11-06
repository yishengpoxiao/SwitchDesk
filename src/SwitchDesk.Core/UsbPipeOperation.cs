namespace SwitchDesk.Core;

internal static class UsbPipeOperation
{
    internal static async Task<T> RunAsync<T>(SemaphoreSlim gate, Func<T> operation, Action abort, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => cancelled.TrySetResult());
            var worker = Task.Run(() => { ct.ThrowIfCancellationRequested(); return operation(); }, ct);
            var aborts = AbortUntilFinishedAsync(worker, cancelled.Task, abort);
            try
            {
                var result = await worker.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is IOException or ObjectDisposedException)
            { throw new OperationCanceledException("USB 传输已取消", ex, ct); }
            finally
            {
                // Do not release this pipe to the next operation while an abort is still
                // running. A late abort must never cancel that operation instead.
                await aborts.ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    private static async Task AbortUntilFinishedAsync(Task worker, Task cancelled, Action abort)
    {
        if (await Task.WhenAny(worker, cancelled).ConfigureAwait(false) == worker) return;
        while (!worker.IsCompleted)
        {
            // Cancellation may precede the native submission. A single AbortPipe in
            // that window cancels nothing, leaving the worker on its long I/O timeout.
            abort();
            await Task.WhenAny(worker, Task.Delay(25)).ConfigureAwait(false);
        }
    }
}
