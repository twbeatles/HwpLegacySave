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
            return string.Equals(ReadSettingText(modeFile).Trim(), "on", StringComparison.Ordinal);
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

    /// <summary>
    /// 설정 파일을 읽는다. 다른 런처 창이 같은 파일을 원자적으로 교체하는 순간에는 공유 위반이 날 수 있어
    /// 잠시 기다렸다 다시 읽는다. 공유 위반 때문에 HWP 저장 모드가 꺼진 것으로 잘못 판정되지 않게 한다.
    /// </summary>
    internal static string ReadSettingText(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException error) when (attempt < 4 && error is not FileNotFoundException and not DirectoryNotFoundException)
            {
                System.Threading.Thread.Sleep(40 * (attempt + 1));
            }
        }
    }

    public static void WriteHookEnabled(bool enabled, string? settingsDirectory = null) =>
        WriteSettingAtomic(GetModeFilePath(settingsDirectory), enabled ? "on" : "off", settingsDirectory);

    public static string GetCompatibleModeFilePath(string? settingsDirectory = null) =>
        Path.Combine(settingsDirectory ?? DefaultSettingsDirectory, "compatible-mode.txt");

    /// <summary>
    /// 검증되지 않았지만 대상 메서드 구조가 같은 한글 빌드에도 적용할지 여부. 기본값은 OFF다.
    /// 파일을 읽지 못하면 안전하게 OFF로 본다.
    /// </summary>
    public static bool ReadCompatibleEnabled(string? settingsDirectory = null)
    {
        try
        {
            return string.Equals(ReadSettingText(GetCompatibleModeFilePath(settingsDirectory)).Trim(), "on", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static void WriteCompatibleEnabled(bool enabled, string? settingsDirectory = null) =>
        WriteSettingAtomic(GetCompatibleModeFilePath(settingsDirectory), enabled ? "on" : "off", settingsDirectory);

    public static string? ReadSelectedPathRaw(string? settingsDirectory = null) =>
        ReadPathFile(GetSelectedPathFilePath(settingsDirectory));

    /// <summary>
    /// 마지막으로 자동 탐지한 Hwp.exe 경로. 직접 지정(hwp-path.txt)과 구분한다.
    /// 더블클릭·바로가기 실행이 매번 전체 탐지를 하다가 느린 드라이브 때문에 시간 초과되는 일을 막는다.
    /// </summary>
    public static string GetDetectedPathFilePath(string? settingsDirectory = null) =>
        Path.Combine(settingsDirectory ?? DefaultSettingsDirectory, "hwp-path-detected.txt");

    public static string? ReadDetectedPathRaw(string? settingsDirectory = null) =>
        ReadPathFile(GetDetectedPathFilePath(settingsDirectory));

    /// <summary>자동 탐지 결과를 기록한다. 실패해도 실행에는 영향이 없다.</summary>
    public static void TryWriteDetectedPath(string? hwpPath, string? settingsDirectory = null)
    {
        try
        {
            string file = GetDetectedPathFilePath(settingsDirectory);
            if (string.IsNullOrWhiteSpace(hwpPath))
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }
            if (string.Equals(ReadPathFile(file), hwpPath, StringComparison.OrdinalIgnoreCase)) return;
            WriteSettingAtomic(file, hwpPath!, settingsDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { }
    }

    private static string? ReadPathFile(string file)
    {
        try
        {
            string stored = ReadSettingText(file).Trim();
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
