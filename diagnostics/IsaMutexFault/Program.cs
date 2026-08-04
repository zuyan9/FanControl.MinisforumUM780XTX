const string MutexName = "Global\\Access_ISABUS.HTP.Method";

return args switch
{
    ["hold", string seconds] when int.TryParse(seconds, out int parsed) &&
        parsed is >= 1 and <= 30 => Hold(parsed),
    ["abandon", string seconds] when int.TryParse(seconds, out int parsed) &&
        parsed is >= 1 and <= 30 => Abandon(parsed),
    _ => Usage(),
};

static int Hold(int seconds)
{
    using Mutex mutex = new(false, MutexName);
    Console.WriteLine($"Waiting for {MutexName} at {DateTimeOffset.Now:O}");
    if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
    {
        Console.Error.WriteLine("Timed out acquiring the ISA mutex.");
        return 1;
    }

    try
    {
        Console.WriteLine($"Holding the ISA mutex for {seconds} seconds.");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }
    finally
    {
        mutex.ReleaseMutex();
    }

    Console.WriteLine($"Released the ISA mutex at {DateTimeOffset.Now:O}");
    return 0;
}

static int Abandon(int seconds)
{
    using Mutex mutex = new(false, MutexName);
    using ManualResetEventSlim acquired = new(false);
    Thread owner = new(() =>
    {
        if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
        {
            return;
        }

        acquired.Set();
        // Returning without ReleaseMutex deliberately abandons ownership.
    });
    owner.IsBackground = false;
    owner.Start();
    if (!acquired.Wait(TimeSpan.FromSeconds(11)))
    {
        Console.Error.WriteLine("The owner thread did not acquire the ISA mutex.");
        owner.Join();
        return 1;
    }

    owner.Join();
    Console.WriteLine($"Abandoned {MutexName} at {DateTimeOffset.Now:O}");
    Console.WriteLine($"Keeping the mutex handle open for {seconds} seconds.");
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: IsaMutexFault <hold|abandon> <seconds>");
    return 2;
}
