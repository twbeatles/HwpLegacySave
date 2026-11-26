using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
#if !NETFRAMEWORK
using System.Runtime.Versioning;
#endif
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace HwpSaveHook.Launcher;

#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
public static class HwpInstallations
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex Executable = new(
        @"^(?:""(?<path>[^""]+)""|(?<path>.+?\.exe))(?=\s|,|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] ProductNames = ["Hancom", "HNC", "한컴", "한글"];
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static async Task<T> InspectWithTimeoutAsync<T>(Func<CancellationToken, T> inspect, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        Task<T> work = Task.Run(() => inspect(token), token);
        Task delay = Task.Delay(timeout ?? TimeSpan.FromSeconds(8), token);
        try
        {
            if (await Task.WhenAny(work, delay).ConfigureAwait(false) == work)
            {
                return await work.ConfigureAwait(false);
            }

            // A Windows file operation already in progress may finish later.
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new TimeoutException("Timed out while checking the Hangul installation.");
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    public static string? FindPreferred(string? explicitPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (explicitPath is not null)
        {
            string path = Path.GetFullPath(explicitPath);
            if (!PathComparer.Equals(Path.GetFileName(path), "Hwp.exe"))
            {
                throw new ArgumentException("Select Hwp.exe.");
            }
            return IsExecutable(path) ? path : null;
        }

        return ChooseCandidate(Discover(cancellationToken), SaveHookLauncher.DefaultExpectedSha256, cancellationToken);
    }

    internal static string? ChooseCandidate(IEnumerable<string> candidates, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        string? first = null;
        foreach (string path in candidates.Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsExecutable(path)) continue;
            first ??= path;
            if (PathComparer.Equals(SaveHookLauncher.TryHashFile(path), expectedSha256))
            {
                return path;
            }
        }
        return first;
    }

    private static List<string> Discover(CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        RegistryView[] views = Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry32, RegistryView.Registry64] : [RegistryView.Registry32];
        foreach (RegistryView view in views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                AddCandidate(candidates, ReadRegistry(hive, view,
                    @"Software\Microsoft\Windows\CurrentVersion\App Paths\Hwp.exe"));
            }

            string? classId = ReadRegistry(RegistryHive.ClassesRoot, view, @"HWPFrame.HwpObject\CLSID");
            if (Guid.TryParse(classId, out Guid clsid))
            {
                AddCandidate(candidates, ReadRegistry(RegistryHive.ClassesRoot, view, $@"CLSID\{clsid:B}\LocalServer32"));
            }

            AddCandidate(candidates, ReadRegistry(RegistryHive.ClassesRoot, view,
                @"Applications\Hwp.exe\shell\open\command"));
            string? documentType = ReadRegistry(RegistryHive.ClassesRoot, view, ".hwp");
            if (!string.IsNullOrWhiteSpace(documentType))
            {
                AddCandidate(candidates, ReadRegistry(RegistryHive.ClassesRoot, view, $@"{documentType}\shell\open\command"));
            }
        }

        foreach (RegistryView view in views)
        {
            AddInstalledProducts(candidates, RegistryHive.CurrentUser, view, cancellationToken);
            AddInstalledProducts(candidates, RegistryHive.LocalMachine, view, cancellationToken);
        }

        var roots = new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>().ToList();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "Programs"));
        foreach (string root in roots.Distinct(PathComparer))
        {
            AddInstallLocation(candidates, Path.Combine(root, "HNC"), cancellationToken);
            AddInstallLocation(candidates, Path.Combine(root, "Hancom"), cancellationToken);
        }

        return candidates;
    }

    private static string? ReadRegistry(RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? entry = root.OpenSubKey(subKey);
            return entry?.GetValue(null) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static void AddCandidate(ICollection<string> candidates, string? command)
    {
        if (command is null) return;
        string path = Executable.Match(Environment.ExpandEnvironmentVariables(command).TrimStart()).Groups["path"].Value;
        if (NormalizeAbsolutePath(path) is string executable &&
            PathComparer.Equals(Path.GetFileName(executable), "Hwp.exe"))
        {
            candidates.Add(executable);
        }
    }

    private static void AddInstalledProducts(ICollection<string> candidates, RegistryHive hive, RegistryView view,
        CancellationToken cancellationToken)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null) return;

            foreach (string subKey in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using RegistryKey? product = uninstall.OpenSubKey(subKey);
                if (product is null) continue;
                string identity = $"{product.GetValue("DisplayName")} {product.GetValue("Publisher")}";
                if (!ProductNames.Any(name => identity.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                AddCandidate(candidates, product.GetValue("DisplayIcon") as string);
                if (product.GetValue("InstallLocation") is string location &&
                    NormalizeAbsolutePath(Environment.ExpandEnvironmentVariables(location).Trim().Trim('"')) is string path)
                {
                    AddInstallLocation(candidates, path, cancellationToken);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
    }

    internal static void AddInstallLocation(ICollection<string> candidates, string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        candidates.Add(Path.Combine(path, "Hwp.exe"));
        candidates.Add(Path.Combine(path, "Bin", "Hwp.exe"));
        foreach (string version in Directories(path, "HOffice*"))
        {
            candidates.Add(Path.Combine(version, "Bin", "Hwp.exe"));
        }
        foreach (string office in Directories(path, "Office*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string version in Directories(office, "HOffice*"))
            {
                candidates.Add(Path.Combine(version, "Bin", "Hwp.exe"));
            }
        }
    }

    private static string? NormalizeAbsolutePath(string path)
    {
        bool drivePath = path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        if (!drivePath && !path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static string[] Directories(string root, string pattern)
    {
        try { return Directory.GetDirectories(root, pattern); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsExecutable(string path)
    {
        const uint binary32Bit = 0;
        const uint binary64Bit = 6;
        return GetBinaryType(path, out uint type) && type is binary32Bit or binary64Bit;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetBinaryTypeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBinaryType(string applicationName, out uint binaryType);
}
