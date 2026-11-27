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

    /// <summary>이 PC에서 찾은 실행 가능한 Hwp.exe 전체(중복 제거, 탐지 순서).</summary>
    public static IReadOnlyList<string> FindAll(CancellationToken cancellationToken = default) =>
        Discover(cancellationToken).Distinct(PathComparer).Where(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return IsExecutable(path);
        }).ToList();

    internal static string? ChooseCandidate(IEnumerable<string> candidates, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        // 검증된 빌드 → 대상 메서드 구조가 같은 빌드(2018·2020·2024 등) → 첫 실행 파일 순으로 고른다.
        string? first = null, compatible = null;
        foreach (string path in candidates.Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsExecutable(path)) continue;
            first ??= path;
            string? hash = SaveHookLauncher.TryHashFile(path);
            if (PathComparer.Equals(hash, expectedSha256) || HwpBuildCatalog.Find(hash) is not null)
            {
                return path;
            }
            if (compatible is null && HwpBuildInspector.Inspect(path).HasCompatibleTarget)
            {
                compatible = path;
            }
        }
        return compatible ?? first;
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
            AddHancomRegistry(candidates, RegistryHive.LocalMachine, view, cancellationToken);
            AddHancomRegistry(candidates, RegistryHive.CurrentUser, view, cancellationToken);
        }

        foreach (RegistryView view in views)
        {
            AddInstalledProducts(candidates, RegistryHive.CurrentUser, view, cancellationToken);
            AddInstalledProducts(candidates, RegistryHive.LocalMachine, view, cancellationToken);
        }

        AddRunningHangul(candidates);

        var roots = new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>().ToList();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "Programs"));
        // 시스템 드라이브가 아닌 고정 디스크에 설치한 경우(D:\Program Files (x86)\HNC 등). 네트워크·이동식 드라이브는 지연될 수 있어 제외한다.
        foreach (string drive in FixedDriveRoots())
        {
            roots.Add(drive);
            roots.Add(Path.Combine(drive, "Program Files"));
            roots.Add(Path.Combine(drive, "Program Files (x86)"));
        }
        foreach (string root in roots.Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddInstallLocation(candidates, Path.Combine(root, "HNC"), cancellationToken);
            AddInstallLocation(candidates, Path.Combine(root, "Hancom"), cancellationToken);
        }

        return candidates;
    }

    /// <summary>
    /// 한컴오피스 설치 프로그램이 남기는 자체 레지스트리(HNC 키). 버전 번호(100·110·120·130 …)와 관계없이 열거한다.
    /// - HNC\Hwp\{버전}\Capabilities: ApplicationIcon = "...\Bin\hwp.exe",0
    /// - HNC\Shared: "Hnc Path{번호}" = 설치 루트
    /// - HNC\Shared\Install Information\Hwp{번호}: Path = 설치 루트
    /// - HNC\Shared\Common{버전}: "Common Path" = ...\Bin\
    /// - HNC\Shared\HncUpdate\HOffice_*: Path = 설치 루트
    /// </summary>
    private static void AddHancomRegistry(ICollection<string> candidates, RegistryHive hive, RegistryView view,
        CancellationToken cancellationToken)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? hnc = root.OpenSubKey(@"Software\HNC");
            if (hnc is null) return;

            using (RegistryKey? hwp = hnc.OpenSubKey("Hwp"))
            {
                foreach (string version in hwp?.GetSubKeyNames() ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using RegistryKey? capabilities = hwp!.OpenSubKey($@"{version}\Capabilities");
                    AddCandidate(candidates, capabilities?.GetValue("ApplicationIcon") as string);
                }
            }

            using RegistryKey? shared = hnc.OpenSubKey("Shared");
            if (shared is null) return;
            foreach (string name in shared.GetValueNames())
            {
                if (name.StartsWith("Hnc Path", StringComparison.OrdinalIgnoreCase)) AddRoot(shared.GetValue(name) as string);
            }
            foreach (string name in shared.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (name.StartsWith("Common", StringComparison.OrdinalIgnoreCase))
                {
                    using RegistryKey? common = shared.OpenSubKey(name);
                    if (common?.GetValue("Common Path") is string bin && NormalizeAbsolutePath(Clean(bin)) is string binPath)
                    {
                        candidates.Add(Path.Combine(binPath, "Hwp.exe"));
                    }
                }
            }
            AddRootsFrom(shared, "Install Information", key => key.StartsWith("Hwp", StringComparison.OrdinalIgnoreCase) ||
                                                              key.StartsWith("HOffice", StringComparison.OrdinalIgnoreCase));
            AddRootsFrom(shared, "HncUpdate", key => key.StartsWith("HOffice", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }

        void AddRootsFrom(RegistryKey shared, string subKey, Func<string, bool> include)
        {
            using RegistryKey? parent = shared.OpenSubKey(subKey);
            foreach (string name in parent?.GetSubKeyNames() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!include(name)) continue;
                using RegistryKey? entry = parent!.OpenSubKey(name);
                AddRoot(entry?.GetValue("Path") as string);
            }
        }

        void AddRoot(string? value)
        {
            if (value is not null && NormalizeAbsolutePath(Clean(value)) is string path)
            {
                AddInstallLocation(candidates, path, cancellationToken);
            }
        }

        static string Clean(string value) => Environment.ExpandEnvironmentVariables(value).Trim().Trim('"');
    }

    /// <summary>실행 중인 한글의 실제 경로. 휴대용·비표준 설치도 한글을 한 번 열어 두면 찾을 수 있다.</summary>
    private static void AddRunningHangul(ICollection<string> candidates)
    {
        System.Diagnostics.Process[] processes;
        try { processes = System.Diagnostics.Process.GetProcessesByName("Hwp"); }
        catch (InvalidOperationException) { return; }
        foreach (System.Diagnostics.Process process in processes)
        {
            using (process)
            {
                if (TryGetProcessPath(process.Id) is string path) candidates.Add(path);
            }
        }
    }

    private static string? TryGetProcessPath(int processId)
    {
        const uint queryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(queryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? NormalizeAbsolutePath(buffer.ToString()) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IEnumerable<string> FixedDriveRoots()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (DriveInfo drive in drives)
        {
            bool ready;
            try { ready = drive.DriveType == DriveType.Fixed && drive.IsReady; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ready = false; }
            if (ready) yield return drive.RootDirectory.FullName;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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
