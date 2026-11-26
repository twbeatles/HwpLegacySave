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
}

public sealed record ShortcutInfo(string TargetPath, string Arguments, string IconLocation);

/// <summary>
/// 관리자 권한 없이 현재 사용자(HKCU) 수준에서 바탕화면 바로가기 및 .hwp 파일 연결을 관리한다.
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

        return Path.Combine(AppContext.BaseDirectory, "HwpSaveHook.exe");
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

    public static (string ProgId, bool HasExistingMapping) ResolveHwpProgId()
    {
        try
        {
            using RegistryKey? userChoice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.hwp\UserChoice");
            if (userChoice?.GetValue("Progid") is string userProgId && !string.IsNullOrWhiteSpace(userProgId))
            {
                return (userProgId.Trim(), true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }

        try
        {
            using RegistryKey? extKey = Registry.ClassesRoot.OpenSubKey(".hwp");
            if (extKey?.GetValue(null) is string rootProgId && !string.IsNullOrWhiteSpace(rootProgId))
            {
                return (rootProgId.Trim(), true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }

        return (DefaultFallbackProgId, false);
    }

    public static string BuildWrapperCommand(string launcherExePath) =>
        $"\"{Path.GetFullPath(launcherExePath)}\" {WrapperFlag} \"%1\"";

    public static IntegrationStatus GetFileAssociationStatus(
        string launcherExePath,
        string? classesSubKeyPrefix = null,
        string? customProgId = null)
    {
        string prefix = classesSubKeyPrefix ?? DefaultClassesPrefix;
        string progId = customProgId ?? ResolveHwpProgId().ProgId;
        string commandKeyPath = $@"{prefix}\{progId}\shell\open\command";

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(commandKeyPath);
            if (key?.GetValue(null) is not string command || string.IsNullOrWhiteSpace(command))
            {
                return IntegrationStatus.Disabled;
            }

            return ClassifyWrapperCommand(command, launcherExePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return IntegrationStatus.Disabled;
        }
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
        string progId = customProgId ?? resolvedProgId;
        bool hasMapping = hasExistingSystemMapping ?? existingMapping;

        string commandKeyPath = $@"{prefix}\{progId}\shell\open\command";
        string? currentCommand = ReadUserKeyDefault(commandKeyPath);
        bool alreadyWrapped = currentCommand is not null &&
                              ClassifyWrapperCommand(currentCommand, launcherExePath) != IntegrationStatus.Disabled;

        string backupFile = SaveHookSettings.GetAssociationBackupFilePath(settingsDirectory);
        if (!alreadyWrapped)
        {
            bool createdExt = false;
            if (!hasMapping)
            {
                string extKeyPath = $@"{prefix}\.hwp";
                if (string.IsNullOrWhiteSpace(ReadUserKeyDefault(extKeyPath)))
                {
                    using RegistryKey extKey = Registry.CurrentUser.CreateSubKey(extKeyPath)
                        ?? throw new InvalidOperationException(".hwp 확장자 레지스트리 키를 만들 수 없습니다.");
                    extKey.SetValue(null, progId, RegistryValueKind.String);
                    createdExt = true;
                }
            }

            WriteAssociationBackup(backupFile, progId, currentCommand, createdExt);
        }

        using (RegistryKey cmdKey = Registry.CurrentUser.CreateSubKey(commandKeyPath)
            ?? throw new InvalidOperationException("파일 연결 레지스트리 키를 만들 수 없습니다."))
        {
            cmdKey.SetValue(null, BuildWrapperCommand(launcherExePath), RegistryValueKind.String);
        }

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
        AssociationBackup? backup = ReadAssociationBackup(backupFile);
        string progId = customProgId ?? backup?.ProgId ?? ResolveHwpProgId().ProgId;
        string commandKeyPath = $@"{prefix}\{progId}\shell\open\command";

        if (backup?.PreviousCommand is string previous)
        {
            using RegistryKey cmdKey = Registry.CurrentUser.CreateSubKey(commandKeyPath)
                ?? throw new InvalidOperationException("기존 파일 연결 레지스트리 키를 복원할 수 없습니다.");
            cmdKey.SetValue(null, previous, RegistryValueKind.String);
        }
        else
        {
            DeleteKeyAndEmptyParents(prefix, progId);
        }

        if (backup?.CreatedExtensionMapping == true)
        {
            TryDeleteDefaultAndEmptyKey($@"{prefix}\.hwp");
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

    private sealed record AssociationBackup(string ProgId, string? PreviousCommand, bool CreatedExtensionMapping);

    private static void WriteAssociationBackup(string backupFile, string progId, string? previousCommand, bool createdExt)
    {
        var lines = new List<string>
        {
            $"progId={progId}",
            $"createdExt={(createdExt ? "1" : "0")}",
        };
        if (previousCommand is not null)
        {
            lines.Add($"previousCommand={previousCommand}");
        }
        SaveHookSettings.WriteSettingAtomic(backupFile, string.Join(Environment.NewLine, lines),
            Path.GetDirectoryName(backupFile));
    }

    private static AssociationBackup? ReadAssociationBackup(string backupFile)
    {
        if (!File.Exists(backupFile)) return null;
        try
        {
            string? progId = null;
            string? previousCommand = null;
            bool createdExt = false;

            foreach (string line in File.ReadAllLines(backupFile))
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

            return string.IsNullOrWhiteSpace(progId)
                ? null
                : new AssociationBackup(progId!, previousCommand, createdExt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
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
