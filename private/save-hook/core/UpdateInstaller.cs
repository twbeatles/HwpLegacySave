using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HwpSaveHook.Launcher;

/// <summary>헬퍼가 남기는 업데이트 결과. GUI가 다음 시작에서 한 번 읽고 지운다.</summary>
public sealed record UpdateResult(string Status, string? Version, string? Message);

/// <summary>
/// 서명된 매니페스트로 새 ZIP 배포본을 받아 설치 폴더 전체를 교체한다.
/// 실행 중인 HwpSaveHook.exe와 한글에 로드된 프로파일러 DLL은 덮어쓸 수 없으므로,
/// 런처를 종료한 뒤 임시 폴더로 복사한 헬퍼가 파일을 교체하고 스모크 검사에 실패하면 되돌린다.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>업데이트 매니페스트 서명 공개키(P-256, X||Y base64). 개인키는 저장소에 두지 않는다.</summary>
    public const string PublicKeyBase64 = "Z5SS5CCrMI3dgiRXhkvklUf1YByUaBg3hgjoqf5Gy6V+QdX+b3pRj+h3BYbjLf/yYznNW/MXaQjlSEw4wGVFOw==";

    public const string DefaultManifestUrl = "https://raw.githubusercontent.com/twbeatles/HwpLegacySave/main/updates/latest.json";
    public const string ReleasesUrl = "https://github.com/twbeatles/HwpLegacySave/releases/latest";
    public const string ManifestUrlVariable = "HWPSAVEHOOK_UPDATE_MANIFEST_URL";

    /// <summary>배포 ZIP에 반드시 있어야 하는 파일. 하나라도 없으면 설치하지 않는다.</summary>
    public static readonly string[] RequiredFiles = ["HwpSaveHook.exe", "HwpSaveHook.exe.config", @"profiler\HwpSaveHookProfiler.dll"];

    private const int MaxEntries = 64;
    private const string UserAgent = "HwpSaveHook-Updater";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public static Version CurrentVersion =>
        typeof(UpdateInstaller).Assembly.GetName().Version ?? new Version(0, 0);

    public static string ManifestUrl =>
        Environment.GetEnvironmentVariable(ManifestUrlVariable) is string custom && !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim() : DefaultManifestUrl;

    public static bool IsConfigured => PublicKeyBase64.Length == 88 && !PublicKeyBase64.StartsWith("__", StringComparison.Ordinal);

    public static string StagingRoot => Path.Combine(SaveHookSettings.DefaultSettingsDirectory, "updates");

    public static string ResultPath => Path.Combine(StagingRoot, "last-update-result.txt");

    /// <summary>새 버전이 있으면 매니페스트를, 최신이면 null을 돌려준다.</summary>
    public static UpdateManifest? Check(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("이 빌드에는 업데이트 서명 키가 설정되지 않았습니다.");
        byte[] body = Download(ManifestUrl, UpdateManifestVerifier.MaxManifestBytes, destination: null, cancellationToken);
        try
        {
            return UpdateManifestVerifier.Verify(Encoding.UTF8.GetString(body), PublicKeyBase64, CurrentVersion);
        }
        catch (NoUpdateAvailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// ZIP을 받아 해시·크기를 확인하고 임시 폴더에 풀어 둔다. 설치 폴더는 아직 건드리지 않는다.
    /// </summary>
    public static string Stage(UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(StagingRoot);
        CleanupStaging();
        string work = Path.Combine(StagingRoot, $"update-{manifest.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        string archive = Path.Combine(work, "package.zip");
        try
        {
            using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Download(manifest.ArtifactUrl, manifest.Size, file, cancellationToken);
            }
            VerifyArtifact(archive, manifest.Sha256, manifest.Size);
            string payload = Path.Combine(work, "payload");
            ExtractPackage(archive, payload);
            return payload;
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }
    }

    internal static void VerifyArtifact(string path, string sha256, long size)
    {
        var info = new FileInfo(path);
        if (info.Length != size) throw new InvalidDataException("받은 업데이트 파일의 크기가 다릅니다.");
        using FileStream stream = File.OpenRead(path);
        using SHA256 hash = SHA256.Create();
        string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("받은 업데이트 파일의 해시가 다릅니다.");
        }
    }

    /// <summary>경로 탈출·과도한 항목·필수 파일 누락을 거부하며 ZIP을 푼다.</summary>
    internal static void ExtractPackage(string archive, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using ZipArchive zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("업데이트 파일의 항목이 너무 많습니다.");
        long total = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("업데이트 파일에 허용되지 않은 경로가 있습니다.");
            }
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            total += entry.Length;
            if (total > UpdateManifestVerifier.MaxArtifactBytes * 4) throw new InvalidDataException("업데이트 파일이 너무 큽니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }

        foreach (string required in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(root, required)))
            {
                throw new InvalidDataException($"업데이트 파일에 {required}이(가) 없습니다.");
            }
        }
    }

    /// <summary>설치 폴더에 쓸 수 있는지 확인한다. Program Files 등에서는 수동 업데이트를 안내한다.</summary>
    public static bool CanWrite(string directory)
    {
        string probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// 현재 런처를 임시 폴더로 복사해 헬퍼로 실행한다. 호출자는 바로 종료해야 한다.
    /// </summary>
    public static Process LaunchHelper(string payload, string installDirectory, string launcherExecutable, bool relaunch)
    {
        string helperDir = Path.Combine(StagingRoot, $"helper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(helperDir);
        string helper = Path.Combine(helperDir, Path.GetFileName(launcherExecutable));
        File.Copy(launcherExecutable, helper);
        string config = launcherExecutable + ".config";
        if (File.Exists(config)) File.Copy(config, helper + ".config");

        using Process current = Process.GetCurrentProcess();
        var arguments = new List<string>
        {
            "--apply-update",
            "--payload", payload,
            "--install-dir", installDirectory,
            "--parent-pid", current.Id.ToString(CultureInfo.InvariantCulture),
        };
        if (relaunch) arguments.Add("--relaunch");
        var start = new ProcessStartInfo(helper, string.Join(" ", arguments.Select(QuoteArgument)))
        {
            UseShellExecute = false,
            WorkingDirectory = helperDir,
        };
        return Process.Start(start) ?? throw new InvalidOperationException("업데이트 도우미를 시작하지 못했습니다.");
    }

    /// <summary>헬퍼 프로세스의 본 작업. 파일을 교체하고 실패하면 원래 상태로 되돌린다.</summary>
    public static UpdateResult Apply(string payload, string installDirectory, int parentProcessId,
        Func<bool> isHangulRunning, Func<string, bool>? smoke = null, TimeSpan? parentTimeout = null)
    {
        string version = ReadPayloadVersion(payload);
        UpdateResult result;
        try
        {
            WaitForParent(parentProcessId, parentTimeout ?? TimeSpan.FromSeconds(60));
            if (isHangulRunning())
            {
                result = new("failed", version, "한글이 실행 중이라 업데이트하지 않았습니다. 한글을 닫은 뒤 다시 시도하세요.");
            }
            else
            {
                result = ReplaceFiles(payload, installDirectory, version, smoke ?? RunSmoke);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                                      or InvalidDataException or TimeoutException or Win32Exception or InvalidOperationException)
        {
            result = new("failed", version, error.Message);
        }

        WriteResult(result);
        if (result.Status == "applied" && StagedWorkDirectory(payload) is string work) TryDeleteDirectory(work);
        return result;
    }

    /// <summary>
    /// Stage가 만든 작업 폴더(updates\update-*)일 때만 그 경로를 돌려준다.
    /// 인자로 받은 경로의 상위 폴더를 무조건 지우지 않기 위한 확인이다.
    /// </summary>
    internal static string? StagedWorkDirectory(string payload, string? stagingRoot = null)
    {
        string full = Path.GetFullPath(payload).TrimEnd(Path.DirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(full);
        string root = Path.GetFullPath(stagingRoot ?? StagingRoot).TrimEnd(Path.DirectorySeparatorChar);
        return parent is not null &&
               string.Equals(Path.GetFileName(full), "payload", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetDirectoryName(parent), root, StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(parent).StartsWith("update-", StringComparison.Ordinal)
            ? parent : null;
    }

    internal static UpdateResult ReplaceFiles(string payload, string installDirectory, string version, Func<string, bool> smoke)
    {
        string source = Path.GetFullPath(payload);
        string target = Path.GetFullPath(installDirectory);
        string backup = Path.Combine(target, $".update-backup-{Guid.NewGuid():N}");
        // 예전 실패분의 백업은 지우지 않는다. 복원마저 실패한 백업은 사용자가 직접 복원하는
        // 최후 수단이므로(ISSUE-003), 다음 업데이트가 조용히 지우면 복원 재료가 사라진다.
        // 성공·롤백-성공 시에는 그 실행이 만든 백업만 정리하고, 남는 것은 수 MB 수준이라 둔다.
        var replaced = new List<(string Target, string? Backup)>();
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        try
        {
            foreach (string file in files)
            {
                string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar);
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // 같은 볼륨에 먼저 복사한 뒤 이름만 바꿔 중간 상태를 짧게 한다.
                string incoming = destination + $".update-{Guid.NewGuid():N}";
                File.Copy(file, incoming);
                string? saved = null;
                if (File.Exists(destination))
                {
                    saved = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    // 로드된 DLL도 이름 변경은 허용된다.
                    File.Move(destination, saved);
                }
                replaced.Add((destination, saved));
                File.Move(incoming, destination);
            }

            if (!smoke(Path.Combine(target, "HwpSaveHook.exe")))
            {
                throw new InvalidOperationException("새 버전의 시작 검사에 실패했습니다.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or Win32Exception or SecurityException)
        {
            string? rollbackError = Rollback(replaced);
            foreach (string leftover in Directory.GetFiles(target, "*.update-*", SearchOption.AllDirectories))
            {
                try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (rollbackError is null) TryDeleteDirectory(backup);
            return new("rolled-back", version, rollbackError is null
                ? $"업데이트를 되돌렸습니다. {error.Message}"
                : $"업데이트 실패 후 복원도 실패했습니다. {backup} 폴더의 파일로 직접 복원하세요. {rollbackError}");
        }

        // 이전 프로파일러 DLL이 다른 세션의 한글에 로드돼 있으면 지금은 지울 수 없다. 다음 업데이트 때 다시 정리한다.
        TryDeleteDirectory(backup);
        return new("applied", version, null);
    }

    private static string? Rollback(List<(string Target, string? Backup)> replaced)
    {
        var failures = new List<string>();
        for (int i = replaced.Count - 1; i >= 0; i--)
        {
            (string destination, string? saved) = replaced[i];
            try
            {
                if (File.Exists(destination)) File.Delete(destination);
                if (saved is not null) File.Move(saved, destination);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{destination}: {error.Message}");
            }
        }
        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private static bool RunSmoke(string executable)
    {
        // PE가 아닌 파일을 실행하면 Windows가 오류 대화상자를 띄워 헬퍼가 멈춘다. 실행 전에 구조를 확인한다.
        if (HwpBuildInspector.Inspect(executable).Kind != HwpBuildKind.Managed) return false;
        string profiler = Path.Combine(Path.GetDirectoryName(executable)!, "profiler", "HwpSaveHookProfiler.dll");
        if (HwpBuildInspector.Inspect(profiler).Kind != HwpBuildKind.Native) return false;

        using Process? process = Process.Start(new ProcessStartInfo(executable, "--smoke") { UseShellExecute = false });
        if (process is null) return false;
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            return false;
        }
        return process.ExitCode == 0;
    }

    private static void WaitForParent(int processId, TimeSpan timeout)
    {
        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (!parent.WaitForExit((int)timeout.TotalMilliseconds))
            {
                throw new TimeoutException("이전 런처가 종료되지 않아 업데이트하지 않았습니다.");
            }
        }
    }

    private static string ReadPayloadVersion(string payload)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(Path.Combine(payload, "HwpSaveHook.exe")).FileVersion ?? "";
        }
        catch (Exception error) when (error is IOException or FileNotFoundException or ArgumentException)
        {
            return "";
        }
    }

    public static void WriteResult(UpdateResult result)
    {
        try
        {
            Directory.CreateDirectory(StagingRoot);
            string text = string.Join("\n", result.Status, result.Version ?? "", (result.Message ?? "").Replace("\n", " "));
            SaveHookSettings.WriteSettingAtomic(ResultPath, text, StagingRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
    }

    /// <summary>헬퍼가 남긴 결과를 한 번만 읽는다.</summary>
    public static UpdateResult? ConsumeResult()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            string[] lines = File.ReadAllText(ResultPath).Split('\n');
            File.Delete(ResultPath);
            if (lines.Length < 1 || lines[0] is not ("applied" or "rolled-back" or "failed")) return null;
            return new UpdateResult(lines[0], lines.Length > 1 && lines[1].Length > 0 ? lines[1] : null,
                lines.Length > 2 && lines[2].Length > 0 ? lines[2] : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>지난 업데이트가 남긴 임시 폴더를 정리한다. 실행 중인 헬퍼 폴더는 잠겨 있어 다음에 다시 시도한다.</summary>
    public static void CleanupStaging()
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;
            foreach (string directory in Directory.GetDirectories(StagingRoot))
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith("update-", StringComparison.Ordinal) || name.StartsWith("helper-", StringComparison.Ordinal))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static byte[] Download(string url, long limit, Stream? destination, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("업데이트 주소는 HTTPS여야 합니다.");
        }

#if NETFRAMEWORK
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
#pragma warning disable SYSLIB0014 // .NET Framework 4.8 GUI와 같은 API를 쓴다.
        var request = (HttpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014
        request.UserAgent = UserAgent;
        request.Timeout = (int)RequestTimeout.TotalMilliseconds;
        request.ReadWriteTimeout = (int)RequestTimeout.TotalMilliseconds;
        request.AllowAutoRedirect = true;
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        using CancellationTokenRegistration registration = cancellationToken.Register(request.Abort);
        try
        {
            using var response = (HttpWebResponse)request.GetResponse();
            if (response.ResponseUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("업데이트 주소가 HTTPS가 아닌 곳으로 이동했습니다.");
            }
            using Stream body = response.GetResponseStream() ?? throw new IOException("응답 본문이 없습니다.");
            var memory = destination is null ? new MemoryStream() : null;
            Stream sink = destination ?? memory!;
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                if (total > limit) throw new InvalidDataException("받은 데이터가 허용 크기를 넘었습니다.");
                sink.Write(buffer, 0, read);
            }
            sink.Flush();
            return memory?.ToArray() ?? [];
        }
        catch (WebException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("업데이트 확인을 취소했습니다.", error, cancellationToken);
        }
        catch (WebException error)
        {
            throw new IOException($"업데이트 서버에 연결하지 못했습니다. {error.Message}", error);
        }
    }

    // Windows CommandLineToArgvW 규칙에 맞춘 인용.
    internal static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder().Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            quoted.Append('\\', character == '"' ? (backslashes * 2) + 1 : backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
