using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HwpSaveHook.Launcher;

public enum RejitStatus
{
    NotObserved,
    RequestAccepted,
    Applied,
    Failed,
    ProcessExited
}

public static class RejitLog
{
    public static string NewPath()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HwpSaveHook", "logs");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"hwp-{Guid.NewGuid():N}.log");
    }

    internal static RejitStatus ReadStatus(IEnumerable<string> lines, int processId,
        int targetToken = SaveHookLauncher.DefaultTargetToken)
    {
        string token = targetToken.ToString("X8", CultureInfo.InvariantCulture);
        string marker = $" [pid {processId.ToString(CultureInfo.InvariantCulture)}] ReJITState: token=0x{token} state=";
        var status = RejitStatus.NotObserved;
        bool installed = false;
        foreach (string line in lines)
        {
            int offset = line.IndexOf(marker, StringComparison.Ordinal);
            if (offset < 0) continue;

            switch (line.Substring(offset + marker.Length))
            {
                case "failed":
                    return RejitStatus.Failed;
                case "request-accepted" when status == RejitStatus.NotObserved:
                    status = RejitStatus.RequestAccepted;
                    break;
                case "il-installed":
                    installed = true;
                    break;
                case "compiled" when installed:
                    status = RejitStatus.Applied;
                    break;
            }
        }
        return status;
    }

    public static async Task<RejitStatus> WaitAsync(Process process, string log, bool waitForCompilation,
        TimeSpan? timeout, CancellationToken cancellationToken = default,
        int targetToken = SaveHookLauncher.DefaultTargetToken)
    {
        var elapsed = Stopwatch.StartNew();
        int processId = process.Id;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejitStatus status = await Task.Run(() =>
            {
                try { return ReadStatus(File.ReadAllLines(log), processId, targetToken); }
                catch (IOException) { return RejitStatus.NotObserved; }
            }, cancellationToken).ConfigureAwait(false);

            if (status is RejitStatus.Applied or RejitStatus.Failed) return status;
            if (process.HasExited) return RejitStatus.ProcessExited;
            if (!waitForCompilation && status == RejitStatus.RequestAccepted) return status;
            if (timeout is TimeSpan limit && elapsed.Elapsed >= limit) return status;
            await Task.Delay(waitForCompilation ? 500 : 150, cancellationToken).ConfigureAwait(false);
        }
    }
}
