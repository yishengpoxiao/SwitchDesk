using System.IO;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task UsbPipeOperationTestsAsync()
    {
        await Test("USB cancellation retries an abort that preceded native submission", async () =>
        {
            using var gate = new SemaphoreSlim(1);
            using var cancel = new CancellationTokenSource();
            using var submit = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int pending = 0, aborts = 0;
            var operation = UsbPipeOperation.RunAsync<int>(gate, () =>
            {
                started.SetResult();
                if (!submit.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("first abort was never delivered");
                Volatile.Write(ref pending, 1);
                if (!finish.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("cancel missed the native submission");
                throw new IOException("fixture native I/O aborted");
            }, () =>
            {
                Interlocked.Increment(ref aborts);
                if (Volatile.Read(ref pending) != 0) finish.Set();
                submit.Set();
            }, cancel.Token);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
                await Throws<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert(aborts >= 2, "the pre-submission abort was not repeated after native I/O became pending");
                Assert(gate.CurrentCount == 1, "cancelled operation retained the pipe gate");
            }
            finally { submit.Set(); finish.Set(); await IgnorePipeFixtureFailure(operation); }
        });
        await Test("USB cancellation drains its abort before the next operation can enter", async () =>
        {
            using var gate = new SemaphoreSlim(1);
            using var cancel = new CancellationTokenSource();
            using var finishWorker = new ManualResetEventSlim();
            using var finishAbort = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var abortEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int aborts = 0, nextStarted = 0;
            var first = UsbPipeOperation.RunAsync(gate, () =>
            {
                started.SetResult();
                if (!finishWorker.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("worker fixture timed out");
                workerExited.SetResult(); return 1;
            }, () =>
            {
                Interlocked.Increment(ref aborts); abortEntered.TrySetResult();
                if (!finishAbort.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("abort fixture timed out");
            }, cancel.Token);
            Task<int>? next = null;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
                await abortEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                next = UsbPipeOperation.RunAsync(gate, () => { Interlocked.Increment(ref nextStarted); return 2; },
                    () => throw new Exception("uncancelled operation was aborted"), default);
                finishWorker.Set(); await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!first.IsCompleted && Volatile.Read(ref nextStarted) == 0 && gate.CurrentCount == 0,
                    "next operation acquired the pipe while a preceding abort was still in progress");
                finishAbort.Set();
                await Throws<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert(await next.WaitAsync(TimeSpan.FromSeconds(5)) == 2);
                var settled = Volatile.Read(ref aborts); await Task.Delay(75);
                Assert(aborts == settled && gate.CurrentCount == 1, "an abort escaped its operation lifetime");
            }
            finally
            {
                finishWorker.Set(); finishAbort.Set(); await IgnorePipeFixtureFailure(first);
                if (next != null) await IgnorePipeFixtureFailure(next);
            }
        });
        await Test("USB pre-cancelled or waiting operations do not abort an active pipe", async () =>
        {
            using var gate = new SemaphoreSlim(0);
            using var cancel = new CancellationTokenSource();
            int calls = 0;
            var waiting = UsbPipeOperation.RunAsync(gate, () => ++calls, () => calls++, cancel.Token);
            cancel.Cancel(); await Throws<OperationCanceledException>(() => waiting);
            gate.Release();
            await Throws<OperationCanceledException>(() => UsbPipeOperation.RunAsync(gate, () => ++calls, () => calls++, cancel.Token));
            Assert(calls == 0 && gate.CurrentCount == 1, "queued cancellation touched the active native operation");
            Assert(await UsbPipeOperation.RunAsync(gate, () => 7, () => calls++, default) == 7 && calls == 0);
        });
    }

    private static async Task IgnorePipeFixtureFailure(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(6)); } catch (Exception) { } }
}
