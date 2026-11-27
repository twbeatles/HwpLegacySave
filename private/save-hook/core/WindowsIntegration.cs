using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
#if !NETFRAMEWORK
using System.Runtime.Versioning;
#endif
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace HwpSaveHook.Launcher;

public enum IntegrationStatus
{
    Disabled,
    Enabled,
    PointsElsewhere,
    /// <summary>.hwp 기본 앱이 한글이 아닌 다른 앱(HOP, 이음오피스 등)이다. 더블클릭이 한글로 가지 않는다.</summary>
    OtherDefaultApp,
}

public sealed record ShortcutInfo(string TargetPath, string Arguments, string IconLocation);

/// <summary>
/// 관리자 권한 없이 현재 사용자(HKCU) 수준에서 바탕화면 바로가기 및 한글 문서 파일 연결을 관리한다.
/// </summary>
#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
public static class WindowsIntegration
{
    public const string DefaultShortcutName = "한글 2022 (HWP 저장).lnk";
    private const string DefaultClassesPrefix = @"Software\Classes";
    private const string DefaultFallbackProgId = "Hwp.Document.120";
    private const string WrapperFlag = "--open";
    private const string LauncherExecutableName = "HwpSaveHook.exe";
    private const string HangulExecutableName = "Hwp.exe";
    private const string FileExtsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    /// <summary>
    /// 한글이 여는 문서 형식. .hwp만 감싸면 .hwpx·서식 파일로 먼저 연 한글이 훅 없이 실행되고,
    /// 그 뒤에 더블클릭한 .hwp도 그 창으로 전달돼 HWPX로 저장된다. 그래서 한글 문서 형식 전체를 감싼다.
    /// 프로파일러는 .hwp 경로를 HWPX로 바꾸는 메서드에만 작용하므로 다른 형식의 저장 결과는 그대로다.
    /// </summary>
    public static IReadOnlyList<string> HangulDocumentExtensions { get; } =
        [".hwp", ".hwpx", ".hwt", ".hwtx", ".hml", ".owpml"];

    public static string GetCurrentLauncherExecutablePath()
    {
        string? candidate = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(candidate) &&
            candidate!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        using Process current = Process.GetCurrentProcess();
        string? modulePath = current.MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(modulePath))
        {
            return Path.GetFullPath(modulePath!);
        }

        return Path.Combine(AppContext.BaseDirectory, LauncherExecutableName);
    }

    public static string GetDesktopShortcutPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), DefaultShortcutName);

    public static void CreateShortcut(string shortcutPath, string launcherExePath, string? hwpExePath = null)
    {
        string fullShortcut = Path.GetFullPath(shortcutPath);
        string fullLauncher = Path.GetFullPath(launcherExePath);
        string? launcherDir = Path.GetDirectoryName(fullLauncher);
        string iconTarget = !string.IsNullOrWhiteSpace(hwpExePath) && File.Exists(hwpExePath)
            ? Path.GetFullPath(hwpExePath!)
            : fullLauncher;

        string? shortcutDir = Path.GetDirectoryName(fullShortcut);
        if (!string.IsNullOrEmpty(shortcutDir))
        {
            Directory.CreateDirectory(shortcutDir!);
        }

        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            link.SetPath(fullLauncher);
            link.SetArguments(WrapperFlag);
            if (!string.IsNullOrEmpty(launcherDir))
            {
                link.SetWorkingDirectory(launcherDir!);
            }
            link.SetIconLocation(iconTarget, 0);
            link.SetDescription("한글 문서를 HWP(.hwp) 형식으로 유지하여 실행합니다.");

            var file = (IPersistFile)link;
            file.Save(fullShortcut, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static ShortcutInfo? ReadShortcut(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return null;

        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            var file = (IPersistFile)link;
            file.Load(Path.GetFullPath(shortcutPath), 0);

            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);

            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);

            var icon = new StringBuilder(1024);
            link.GetIconLocation(icon, icon.Capacity, out _);

            return new ShortcutInfo(target.ToString(), args.ToString(), icon.ToString());
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static IntegrationStatus GetShortcutStatus(string shortcutPath, string launcherExePath)
    {
        ShortcutInfo? info = ReadShortcut(shortcutPath);
        if (info is null) return IntegrationStatus.Disabled;

        bool hasOpenArg = info.Arguments.Trim().Equals(WrapperFlag, StringComparison.OrdinalIgnoreCase);
        if (!hasOpenArg) return IntegrationStatus.PointsElsewhere;

        return PathsEqual(info.TargetPath, launcherExePath)
            ? IntegrationStatus.Enabled
            : IntegrationStatus.PointsElsewhere;
    }

    public static void RemoveShortcut(string shortcutPath)
    {
        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }

    public static (string ProgId, bool HasExistingMapping) ResolveHwpProgId() => ResolveProgId(".hwp");

    /// <summary>탐색기가 실제로 쓰는 확장자의 ProgId. Windows 11의 UserChoiceLatest가 UserChoice보다 우선한다.</summary>
    internal static (string ProgId, bool HasExistingMapping) ResolveProgId(string extension)
    {
        foreach (string userChoice in new[] { $@"{FileExtsKey}\{extension}\UserChoiceLatest\ProgId", $@"{FileExtsKey}\{extension}\UserChoice" })
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(userChoice);
                if (key?.GetValue("ProgId") is string userProgId && !string.IsNullOrWhiteSpace(userProgId))
                {
                    return (userProgId.Trim(), true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
        }

        try
        {
            using RegistryKey? extKey = Registry.ClassesRoot.OpenSubKey(extension);
            if (extKey?.GetValue(null) is string rootProgId && !string.IsNullOrWhiteSpace(rootProgId))
            {
                return (rootProgId.Trim(), true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }

        return (DefaultFallbackProgId, false);
    }

    /// <summary>확장자에 연결될 수 있는 ProgId 후보(사용자 선택, 시스템 기본, [연결 프로그램] 목록).</summary>
    private static List<string> CandidateProgIds(string extension)
    {
        var candidates = new List<string>();
        (string resolved, bool mapped) = ResolveProgId(extension);
        if (mapped) candidates.Add(resolved);

        try
        {
            using RegistryKey? extKey = Registry.ClassesRoot.OpenSubKey(extension);
            if (extKey?.GetValue(null) is string rootProgId && !string.IsNullOrWhiteSpace(rootProgId)) candidates.Add(rootProgId.Trim());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }

        foreach ((RegistryKey hive, string listKey) in new[]
                 {
                     (Registry.CurrentUser, $@"{FileExtsKey}\{extension}\OpenWithProgids"),
                     (Registry.ClassesRoot, $@"{extension}\OpenWithProgids"),
                 })
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(listKey);
                foreach (string name in key?.GetValueNames() ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(name)) candidates.Add(name.Trim());
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return candidates;
    }

    /// <summary>
    /// 한글(Hwp.exe)로 문서를 여는 ProgId 전체. 다른 앱(HOP, 이음오피스 등)의 ProgId는 건드리지 않는다.
    /// 이미 런처로 감싼 ProgId도 포함한다.
    /// </summary>
    public static IReadOnlyList<string> FindHangulProgIds()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string extension in HangulDocumentExtensions)
        {
            foreach (string progId in CandidateProgIds(extension))
            {
                if (seen.Add(progId) && IsHangulProgId(progId)) result.Add(progId);
            }
        }
        return result;
    }

    /// <summary>ProgId의 열기 명령이 Hwp.exe를 실행하거나 이미 런처로 감싸져 있는지.</summary>
    internal static bool IsHangulProgId(string progId, string classesPrefix = DefaultClassesPrefix)
    {
        if (string.Equals(progId, $@"Applications\{HangulExecutableName}", StringComparison.OrdinalIgnoreCase)) return true;

        string? userCommand = ReadUserKeyDefault($@"{classesPrefix}\{progId}\shell\open\command");
        if (userCommand is not null && (IsWrapperCommand(userCommand) || IsHangulCommand(userCommand))) return true;
        return IsHangulCommand(ReadMachineCommand(progId));
    }

    internal static bool IsHangulCommand(string? command)
    {
        string? executable = CommandExecutable(command);
        return executable is not null &&
               string.Equals(Path.GetFileName(executable), HangulExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>위치와 관계없이 이 런처의 투명 실행 명령인지.</summary>
    internal static bool IsWrapperCommand(string command)
    {
        string? executable = CommandExecutable(command);
        return executable is not null &&
               string.Equals(Path.GetFileName(executable), LauncherExecutableName, StringComparison.OrdinalIgnoreCase) &&
               command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                   .Any(token => string.Equals(token, WrapperFlag, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? CommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string trimmed = Environment.ExpandEnvironmentVariables(command!).Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            int closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed.Substring(1, closing - 1) : null;
        }
        int exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return trimmed.Substring(0, exe + 4);
        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed.Substring(0, space) : trimmed;
    }

    /// <summary>시스템(HKLM)에 등록된 원래 열기 명령.</summary>
    private static string? ReadMachineCommand(string progId)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = root.OpenSubKey($@"Software\Classes\{progId}\shell\open\command");
                if (key?.GetValue(null) is string command && !string.IsNullOrWhiteSpace(command)) return command;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { }
        }
        return null;
    }

    public static string BuildWrapperCommand(string launcherExePath) =>
        $"\"{Path.GetFullPath(launcherExePath)}\" {WrapperFlag} \"%1\"";

    /// <summary>
    /// 원래 한글 열기 명령의 인자를 유지한 래퍼 명령. 한글 버전에 따라 "%1" 뒤에 옵션이 붙을 수 있어
    /// 실행 파일 부분만 런처로 바꾸고 나머지는 그대로 Hwp.exe에 넘긴다.
    /// </summary>
    internal static string BuildWrapperCommand(string launcherExePath, string? originalCommand)
    {
        if (!IsHangulCommand(originalCommand)) return BuildWrapperCommand(launcherExePath);
        string trimmed = originalCommand!.Trim();
        string rest = trimmed.StartsWith("\"", StringComparison.Ordinal)
            ? trimmed.Substring(trimmed.IndexOf('"', 1) + 1)
            : trimmed.Substring(trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4);
        rest = rest.Trim();
        if (rest.IndexOf("%1", StringComparison.Ordinal) < 0 && rest.IndexOf("%L", StringComparison.OrdinalIgnoreCase) < 0)
        {
            rest = (rest + " \"%1\"").Trim();
        }
        return $"\"{Path.GetFullPath(launcherExePath)}\" {WrapperFlag} {rest}";
    }

    /// <summary>
    /// .hwp 더블클릭 연동 상태. 기본 앱이 한글이 아닌 다른 앱이면 <see cref="IntegrationStatus.OtherDefaultApp"/>.
    /// </summary>
    public static IntegrationStatus GetFileAssociationStatus(
        string launcherExePath,
        string? classesSubKeyPrefix = null,
        string? customProgId = null)
    {
        string prefix = classesSubKeyPrefix ?? DefaultClassesPrefix;
        (string resolvedProgId, bool mapped) = customProgId is null ? ResolveHwpProgId() : (customProgId, true);
        string progId = customProgId ?? resolvedProgId;

        string? command = ReadUserKeyDefault($@"{prefix}\{progId}\shell\open\command");
        if (!string.IsNullOrWhiteSpace(command))
        {
            IntegrationStatus status = ClassifyWrapperCommand(command!, launcherExePath);
            if (status != IntegrationStatus.Disabled) return status;
        }

        if (customProgId is null && classesSubKeyPrefix is null && mapped && !IsHangulProgId(progId))
        {
            return IntegrationStatus.OtherDefaultApp;
        }
        return IntegrationStatus.Disabled;
    }

    /// <summary>현재 .hwp 기본 앱의 실행 파일 이름(안내 문구용). 알 수 없으면 null.</summary>
    public static string? DescribeDefaultHwpApp()
    {
        (string progId, bool mapped) = ResolveHwpProgId();
        if (!mapped) return null;
        string? command = ReadUserKeyDefault($@"{DefaultClassesPrefix}\{progId}\shell\open\command") ?? ReadMachineCommand(progId);
        string? executable = CommandExecutable(command);
        return executable is null ? progId : Path.GetFileName(executable);
    }

    /// <summary>사용자가 더블클릭 연동을 켠 적이 있는지(되돌리기 정보가 남아 있는지).</summary>
    public static bool IsFileAssociationOptedIn(string? settingsDirectory = null) =>
        File.Exists(SaveHookSettings.GetAssociationBackupFilePath(settingsDirectory));

    /// <summary>
    /// 연동을 켠 뒤 한컴 업데이트·기본 앱 변경으로 감싸지지 않은 한글 ProgId가 생겼는지.
    /// 다른 위치의 런처를 가리키는 연동은 사용자가 고를 일이므로 복구 대상에서 뺀다.
    /// </summary>
    public static bool NeedsAssociationRepair(string launcherExePath, string? settingsDirectory = null)
    {
        if (!IsFileAssociationOptedIn(settingsDirectory)) return false;
        if (GetFileAssociationStatus(launcherExePath) == IntegrationStatus.PointsElsewhere) return false;
        return FindHangulProgIds().Any(progId =>
        {
            string? command = ReadUserKeyDefault($@"{DefaultClassesPrefix}\{progId}\shell\open\command");
            return command is null || ClassifyWrapperCommand(command, launcherExePath) == IntegrationStatus.Disabled;
        });
    }

    /// <summary>연동을 켜 둔 사용자에게서 풀린 연결을 다시 감싼다. 바꾼 것이 있으면 true.</summary>
    public static bool RepairFileAssociation(string launcherExePath, string? settingsDirectory = null)
    {
        if (!NeedsAssociationRepair(launcherExePath, settingsDirectory)) return false;
        EnableFileAssociation(launcherExePath, settingsDirectory);
        return true;
    }

    public static void EnableFileAssociation(
        string launcherExePath,
        string? settingsDirectory = null,
        string? classesSubKeyPrefix = null,
        string? customProgId = null,
        bool? hasExistingSystemMapping = null)
    {
        string prefix = classesSubKeyPrefix ?? DefaultClassesPrefix;
        (string resolvedProgId, bool existingMapping) = ResolveHwpProgId();
        bool hasMapping = hasExistingSystemMapping ?? existingMapping;

        var progIds = new List<string>();
        if (customProgId is not null)
        {
            progIds.Add(customProgId);
        }
        else
        {
            progIds.AddRange(FindHangulProgIds());
            if (progIds.Count == 0)
            {
                if (hasMapping)
                {
                    throw new InvalidOperationException(
                        $".hwp 문서가 한글이 아닌 다른 앱({DescribeDefaultHwpApp() ?? resolvedProgId})에 연결돼 있고 한글 문서 연결을 찾지 못했습니다. " +
                        "Windows 설정 > 기본 앱에서 .hwp를 한글로 지정한 뒤 다시 켜 주세요.");
                }
                progIds.Add(resolvedProgId);
            }
        }

        string backupFile = SaveHookSettings.GetAssociationBackupFilePath(settingsDirectory);
        List<AssociationBackup> backup = ReadAssociationBackup(backupFile);
        bool backupWritten = File.Exists(backupFile);
        bool extensionHandled = hasMapping;

        foreach (string progId in progIds)
        {
            string commandKeyPath = $@"{prefix}\{progId}\shell\open\command";
            string? currentCommand = ReadUserKeyDefault(commandKeyPath);
            bool alreadyWrapped = currentCommand is not null && IsWrapperCommand(currentCommand);

            if (!alreadyWrapped && !backup.Any(entry => string.Equals(entry.ProgId, progId, StringComparison.OrdinalIgnoreCase)))
            {
                bool createdExt = false;
                // .hwp 연결이 아예 없는 PC에서만 첫 ProgId로 사용자 연결을 만든다.
                if (!extensionHandled)
                {
                    extensionHandled = true;
                    string extKeyPath = $@"{prefix}\.hwp";
                    if (string.IsNullOrWhiteSpace(ReadUserKeyDefault(extKeyPath)))
                    {
                        using RegistryKey extKey = Registry.CurrentUser.CreateSubKey(extKeyPath)
                            ?? throw new InvalidOperationException(".hwp 확장자 레지스트리 키를 만들 수 없습니다.");
                        extKey.SetValue(null, progId, RegistryValueKind.String);
                        createdExt = true;
                    }
                }
                backup.Add(new AssociationBackup(progId, currentCommand, createdExt));
                // 되돌리기 정보를 먼저 남긴 뒤 명령을 바꾼다. 중간에 실패해도 원래 명령을 잃지 않는다.
                WriteAssociationBackup(backupFile, backup);
                backupWritten = true;
            }

            string? originalCommand = alreadyWrapped
                ? backup.FirstOrDefault(entry => string.Equals(entry.ProgId, progId, StringComparison.OrdinalIgnoreCase))?.PreviousCommand
                : currentCommand;
            if (!IsHangulCommand(originalCommand) && customProgId is null) originalCommand = ReadMachineCommand(progId);

            using RegistryKey cmdKey = Registry.CurrentUser.CreateSubKey(commandKeyPath)
                ?? throw new InvalidOperationException("파일 연결 레지스트리 키를 만들 수 없습니다.");
            cmdKey.SetValue(null, BuildWrapperCommand(launcherExePath, originalCommand), RegistryValueKind.String);
        }

        // 연동을 켰다는 표식(되돌리기 정보)은 항상 남긴다. 자동 복구가 이 파일로 사용자 선택을 판단한다.
        if (!backupWritten) WriteAssociationBackup(backupFile, backup);

        if (classesSubKeyPrefix is null)
        {
            NotifyShellAssociationChanged();
        }
    }

    public static void DisableFileAssociation(
        string? settingsDirectory = null,
        string? classesSubKeyPrefix = null,
        string? customProgId = null)
    {
        string prefix = classesSubKeyPrefix ?? DefaultClassesPrefix;
        string backupFile = SaveHookSettings.GetAssociationBackupFilePath(settingsDirectory);
        List<AssociationBackup> backup = ReadAssociationBackup(backupFile);

        var targets = new List<AssociationBackup>();
        if (customProgId is not null)
        {
            targets.Add(backup.FirstOrDefault(entry => string.Equals(entry.ProgId, customProgId, StringComparison.OrdinalIgnoreCase))
                        ?? new AssociationBackup(customProgId, null, false));
        }
        else
        {
            targets.AddRange(backup);
            // 되돌리기 정보에 없지만 런처로 감싸진 한글 ProgId(이전 버전·정보 유실)도 정리한다.
            foreach (string progId in FindHangulProgIds())
            {
                if (targets.Any(entry => string.Equals(entry.ProgId, progId, StringComparison.OrdinalIgnoreCase))) continue;
                string? command = ReadUserKeyDefault($@"{prefix}\{progId}\shell\open\command");
                if (command is not null && IsWrapperCommand(command)) targets.Add(new AssociationBackup(progId, null, false));
            }
            if (targets.Count == 0) targets.Add(new AssociationBackup(ResolveHwpProgId().ProgId, null, false));
        }

        foreach (AssociationBackup entry in targets)
        {
            string commandKeyPath = $@"{prefix}\{entry.ProgId}\shell\open\command";
            string? current = ReadUserKeyDefault(commandKeyPath);
            // 그 사이 다른 앱이 명령을 바꿨다면 그대로 둔다.
            if (current is not null && !IsWrapperCommand(current)) continue;

            if (entry.PreviousCommand is string previous)
            {
                using RegistryKey cmdKey = Registry.CurrentUser.CreateSubKey(commandKeyPath)
                    ?? throw new InvalidOperationException("기존 파일 연결 레지스트리 키를 복원할 수 없습니다.");
                cmdKey.SetValue(null, previous, RegistryValueKind.String);
            }
            else
            {
                DeleteKeyAndEmptyParents(prefix, entry.ProgId);
            }

            if (entry.CreatedExtensionMapping)
            {
                TryDeleteDefaultAndEmptyKey($@"{prefix}\.hwp");
            }
        }

        if (File.Exists(backupFile))
        {
            File.Delete(backupFile);
        }

        if (classesSubKeyPrefix is null)
        {
            NotifyShellAssociationChanged();
        }
    }

    internal static IntegrationStatus ClassifyWrapperCommand(string command, string expectedLauncherExePath)
    {
        string trimmed = command.Trim();
        string? exePart = null;
        string rest = "";

        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            int closing = trimmed.IndexOf('"', 1);
            if (closing > 1)
            {
                exePart = trimmed.Substring(1, closing - 1);
                rest = trimmed.Substring(closing + 1);
            }
        }
        else
        {
            int space = trimmed.IndexOf(' ');
            if (space > 0)
            {
                exePart = trimmed.Substring(0, space);
                rest = trimmed.Substring(space + 1);
            }
        }

        if (string.IsNullOrWhiteSpace(exePart)) return IntegrationStatus.Disabled;

        bool hasOpenFlag = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => string.Equals(token, WrapperFlag, StringComparison.OrdinalIgnoreCase));
        if (!hasOpenFlag) return IntegrationStatus.Disabled;

        string expectedFileName = Path.GetFileName(expectedLauncherExePath);
        if (!string.Equals(Path.GetFileName(exePart), expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            return IntegrationStatus.Disabled;
        }

        return PathsEqual(exePart!, expectedLauncherExePath)
            ? IntegrationStatus.Enabled
            : IntegrationStatus.PointsElsewhere;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? ReadUserKeyDefault(string subKeyPath)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath);
            return key?.GetValue(null) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static void DeleteKeyAndEmptyParents(string prefix, string progId)
    {
        string[] hierarchy =
        [
            $@"{prefix}\{progId}\shell\open\command",
            $@"{prefix}\{progId}\shell\open",
            $@"{prefix}\{progId}\shell",
            $@"{prefix}\{progId}",
        ];

        for (int i = 0; i < hierarchy.Length; i++)
        {
            string keyPath = hierarchy[i];
            if (i == 0)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
                continue;
            }

            if (!IsRegistryKeyEmpty(keyPath)) break;
            try { Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { break; }
        }
    }

    private static void TryDeleteDefaultAndEmptyKey(string keyPath)
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            {
                if (key is null) return;
                key.DeleteValue("", throwOnMissingValue: false);
            }
            if (IsRegistryKeyEmpty(keyPath))
            {
                Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
    }

    private static bool IsRegistryKeyEmpty(string keyPath)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key is null) return false;
        return key.SubKeyCount == 0 && key.ValueCount == 0;
    }

    internal sealed record AssociationBackup(string ProgId, string? PreviousCommand, bool CreatedExtensionMapping);

    // 형식 2: ProgId마다 "entry=ProgId<TAB>createdExt<TAB>hasPrevious<TAB>previousCommand" 한 줄.
    // 형식 1(이전 버전): progId=, createdExt=, previousCommand= 각 한 줄. 읽기만 지원한다.
    private const string BackupFormatLine = "format=2";

    internal static void WriteAssociationBackup(string backupFile, IReadOnlyList<AssociationBackup> entries)
    {
        var lines = new List<string> { BackupFormatLine };
        foreach (AssociationBackup entry in entries)
        {
            lines.Add(string.Join("\t",
                "entry=" + entry.ProgId,
                entry.CreatedExtensionMapping ? "1" : "0",
                entry.PreviousCommand is null ? "0" : "1",
                entry.PreviousCommand ?? ""));
        }
        SaveHookSettings.WriteSettingAtomic(backupFile, string.Join(Environment.NewLine, lines),
            Path.GetDirectoryName(backupFile));
    }

    internal static List<AssociationBackup> ReadAssociationBackup(string backupFile)
    {
        var entries = new List<AssociationBackup>();
        if (!File.Exists(backupFile)) return entries;
        try
        {
            string[] lines = SaveHookSettings.ReadSettingText(backupFile)
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

            if (lines.Any(line => line.Trim() == BackupFormatLine))
            {
                foreach (string line in lines)
                {
                    if (!line.StartsWith("entry=", StringComparison.Ordinal)) continue;
                    string[] parts = line.Substring("entry=".Length).Split(['\t'], 4);
                    if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0])) continue;
                    string? previous = parts[2] == "1" ? (parts.Length == 4 ? parts[3] : "") : null;
                    if (!entries.Any(entry => string.Equals(entry.ProgId, parts[0], StringComparison.OrdinalIgnoreCase)))
                    {
                        entries.Add(new AssociationBackup(parts[0], previous, parts[1] == "1"));
                    }
                }
                return entries;
            }

            string? progId = null;
            string? previousCommand = null;
            bool createdExt = false;
            foreach (string line in lines)
            {
                int sep = line.IndexOf('=');
                if (sep <= 0) continue;
                string key = line.Substring(0, sep);
                string value = line.Substring(sep + 1);
                switch (key)
                {
                    case "progId":
                        progId = value;
                        break;
                    case "previousCommand":
                        previousCommand = value;
                        break;
                    case "createdExt":
                        createdExt = value == "1";
                        break;
                }
            }
            if (!string.IsNullOrWhiteSpace(progId)) entries.Add(new AssociationBackup(progId!, previousCommand, createdExt));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
        return entries;
    }

    private static void NotifyShellAssociationChanged()
    {
        const int ShcneAssocChanged = 0x08000000;
        const uint ShcnfIdList = 0x0000;
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); }
        catch { /* best effort */ }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
