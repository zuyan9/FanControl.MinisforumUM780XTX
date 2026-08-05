using System.Diagnostics;

namespace FanControl.MinisforumUM780XTX.CpuBurn;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 ||
            !int.TryParse(args[0], out int durationSeconds) ||
            durationSeconds is < 1 or > 3600)
        {
            Console.Error.WriteLine("Usage: CpuBurn <seconds:1..3600> [workers:1..256]");
            return 2;
        }

        int workers = Environment.ProcessorCount;
        if (args.Length == 2 &&
            (!int.TryParse(args[1], out workers) || workers is < 1 or > 256))
        {
            Console.Error.WriteLine("Workers must be from 1 through 256.");
            return 2;
        }

        using CancellationTokenSource cancellation = new(
            TimeSpan.FromSeconds(durationSeconds));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Priority is an optimization, not a test requirement.
        }

        Console.WriteLine(
            $"{DateTimeOffset.Now:O} CPU_BURN_START seconds={durationSeconds} " +
            $"workers={workers} logical={Environment.ProcessorCount}");

        Task[] tasks = Enumerable.Range(0, workers)
            .Select(worker => Task.Factory.StartNew(
                () => Burn(worker, cancellation.Token),
                cancellation.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        Stopwatch elapsed = Stopwatch.StartNew();
        while (!cancellation.IsCancellationRequested)
        {
            Thread.Sleep(1000);
            Console.WriteLine(
                $"{DateTimeOffset.Now:O} CPU_BURN_HEARTBEAT " +
                $"elapsed={elapsed.Elapsed.TotalSeconds:F1}");
        }

        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item =>
                item is OperationCanceledException))
        {
        }

        Console.WriteLine(
            $"{DateTimeOffset.Now:O} CPU_BURN_END " +
            $"elapsed={elapsed.Elapsed.TotalSeconds:F1}");
        return 0;
    }

    private static void Burn(int worker, CancellationToken cancellation)
    {
        ulong state = unchecked(0x9E3779B97F4A7C15UL * (ulong)(worker + 1));
        double accumulator = worker + 1;
        while (!cancellation.IsCancellationRequested)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            accumulator = Math.Sqrt(accumulator + (state & 0xffff) + 1.0);
        }

        GC.KeepAlive(accumulator);
    }
}
