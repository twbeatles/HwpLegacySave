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
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HwpSaveHook", "logs");

    /// <summary>보관 기간. 이보다 오래된 실행 로그는 새 로그를 만들 때 지운다.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>보관 개수 상한. 더블클릭할 때마다 로그가 생기므로 오래 쓰면 폴더가 계속 커진다.</summary>
    public const int MaxFiles = 200;

    public static string NewPath()
    {
        string folder = Folder;
        Directory.CreateDirectory(folder);
        Prune(folder, Retention, MaxFiles, DateTime.UtcNow);
        return Path.Combine(folder, $"hwp-{Guid.NewGuid():N}.log");
    }

    /// <summary>오래된 실행 로그를 지운다. 실행 중인 한글이 쓰고 있는 파일은 건너뛴다.</summary>
    internal static int Prune(string folder, TimeSpan retention, int maxFiles, DateTime utcNow)
    {
        int deleted = 0;
        try
        {
            var files = new List<FileInfo>(new DirectoryInfo(folder).GetFiles("hwp-*.log"));
            files.Sort((left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
            for (int index = 0; index < files.Count; index++)
            {
                FileInfo file = files[index];
                if (index < maxFiles && utcNow - file.LastWriteTimeUtc < retention) continue;
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return deleted;
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
