using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;

namespace HwpSaveHook.Launcher;

/// <summary>
/// 런처 GUI와 투명 실행(--open) 모드가 공유하는 사용자별 설정 및 인자 파서.
/// </summary>
public static class SaveHookSettings
{
    public static string DefaultSettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HwpSaveHook");

    public static string GetModeFilePath(string? settingsDirectory = null) =>
        Path.Combine(settingsDirectory ?? DefaultSettingsDirectory, "save-mode.txt");

    public static string GetSelectedPathFilePath(string? settingsDirectory = null) =>
        Path.Combine(settingsDirectory ?? DefaultSettingsDirectory, "hwp-path.txt");

    public static string GetAssociationBackupFilePath(string? settingsDirectory = null) =>
        Path.Combine(settingsDirectory ?? DefaultSettingsDirectory, "assoc-backup.txt");

    public static bool ReadHookEnabled(string? settingsDirectory = null)
    {
        string modeFile = GetModeFilePath(settingsDirectory);
        try
        {
            return string.Equals(File.ReadAllText(modeFile).Trim(), "on", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static void WriteHookEnabled(bool enabled, string? settingsDirectory = null) =>
        WriteSettingAtomic(GetModeFilePath(settingsDirectory), enabled ? "on" : "off", settingsDirectory);

    public static string? ReadSelectedPathRaw(string? settingsDirectory = null)
    {
        string selectedPathFile = GetSelectedPathFilePath(settingsDirectory);
        try
        {
            string stored = File.ReadAllText(selectedPathFile).Trim();
            return string.IsNullOrWhiteSpace(stored) ? null : stored;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    public static void WriteSelectedPath(string? hwpPath, string? settingsDirectory = null)
    {
        string selectedPathFile = GetSelectedPathFilePath(settingsDirectory);
        if (string.IsNullOrWhiteSpace(hwpPath))
        {
            if (File.Exists(selectedPathFile)) File.Delete(selectedPathFile);
            return;
        }
        WriteSettingAtomic(selectedPathFile, hwpPath!, settingsDirectory);
    }

    /// <summary>
    /// 첫 번째 인자가 "--open"이면 투명 래퍼 모드로 판정하고 이후 인자들을 한글로 전달한다.
    /// </summary>
    public static (bool IsOpenMode, string[] ForwardedArgs) ParseOpenArgs(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && string.Equals(args[0], "--open", StringComparison.OrdinalIgnoreCase))
        {
            return (true, args.Skip(1).ToArray());
        }
        return (false, []);
    }

    internal static void WriteSettingAtomic(string path, string value, string? settingsDirectory = null)
    {
        string dir = settingsDirectory ?? Path.GetDirectoryName(path) ?? DefaultSettingsDirectory;
        Directory.CreateDirectory(dir);
        string temporaryFile = Path.Combine(dir, $"setting-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryFile, value);
            if (File.Exists(path))
            {
                File.Replace(temporaryFile, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryFile, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                try { File.Delete(temporaryFile); } catch { /* best effort */ }
            }
        }
    }
}
