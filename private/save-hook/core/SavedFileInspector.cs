using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace HwpSaveHook.Launcher;

/// <summary>저장된 파일의 형식 판정 결과. 후보 수준이며 내용 fidelity는 검증하지 않는다.</summary>
public enum SavedFileKind
{
    /// <summary>HWP·HWPX로 보이지 않거나 읽지 못했다.</summary>
    Unknown,
    /// <summary>CFB 매직으로 HWP 후보로 보인다.</summary>
    HwpCandidate,
    /// <summary>mimetype·핵심 항목으로 HWPX 후보로 보인다.</summary>
    HwpxCandidate,
}

/// <summary>저장 결과 확인. 후보 수준이며 문서 내용은 검증하지 않는다.</summary>
public sealed record SavedFileCheck(SavedFileKind Kind, string Note);

/// <summary>
/// 사용자가 저장한 파일이 HWP 후보인지 HWPX 후보인지 확인한다.
/// tools/inspect_format.py와 같은 후보 수준 판정이며, HWPX 강제 전환 여부와 같은
/// 흔한 착오를 가리는 용도다. 제품은 저장 이벤트를 볼 수 없으므로 사용자가 고른 파일만 검사한다.
/// </summary>
public static class SavedFileInspector
{
    private static readonly byte[] CfbMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static SavedFileCheck Inspect(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Unknown("파일을 찾을 수 없습니다.");
            if (info.Length < 8) return Unknown("파일이 너무 작아 형식을 알 수 없습니다.");

            byte[] head = new byte[8];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read = 0;
                while (read < 8)
                {
                    int count = stream.Read(head, read, 8 - read);
                    if (count == 0) break;
                    read += count;
                }
                if (read < 8) return Unknown("파일을 끝까지 읽지 못했습니다.");
            }

            if (head.SequenceEqual(CfbMagic))
            {
                return new SavedFileCheck(SavedFileKind.HwpCandidate,
                    "선택한 파일은 HWP 후보 형식(CFB)으로 저장됐습니다. 문서 내용은 한글에서 직접 확인하세요.");
            }

            bool isZip = (head[2] == 0x03 && head[3] == 0x04) ||
                           (head[2] == 0x05 && head[3] == 0x06) ||
                           (head[2] == 0x07 && head[3] == 0x08);
            if (head[0] == (byte)'P' && head[1] == (byte)'K' && isZip)
            {
                return InspectZip(path);
            }

            return Unknown("HWP·HWPX 형식이 아닙니다.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                                      or ArgumentException or NotSupportedException)
        {
            return Unknown($"파일을 읽지 못했습니다. {error.Message}");
        }
    }

    private static SavedFileCheck InspectZip(string path)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(path);
            List<string> names = zip.Entries.Select(entry => entry.FullName).ToList();
            if (names.Count != new HashSet<string>(names, StringComparer.Ordinal).Count)
            {
                return Unknown("ZIP 항목에 중복이 있어 형식을 확정할 수 없습니다.");
            }

            ZipArchiveEntry? mime = zip.GetEntry("mimetype");
            if (mime is null || mime.Length > 256) return Unknown("HWPX 문서가 아닙니다(서명 파일 없음).");

            string text;
            using (Stream content = mime.Open())
            {
                var buffer = new char[257];
                using var reader = new StreamReader(content, Encoding.ASCII);
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                text = new string(buffer, 0, read).Trim();
            }

            bool hasHeader = zip.GetEntry("Contents/header.xml") is not null;
            bool hasSection = names.Any(name => name.StartsWith("Contents/section", StringComparison.Ordinal) &&
                                               name.EndsWith(".xml", StringComparison.Ordinal));
            if (string.Equals(text, "application/hwp+zip", StringComparison.Ordinal) && hasHeader && hasSection)
            {
                return new SavedFileCheck(SavedFileKind.HwpxCandidate,
                    "선택한 파일은 HWPX 후보 형식(ZIP)으로 저장됐습니다. HWP로 저장하려면 한글의 저장 형식을 확인하세요.");
            }
            return Unknown("HWPX 문서가 아닙니다(핵심 항목 없음).");
        }
        catch (InvalidDataException)
        {
            return Unknown("ZIP 형식이 아니거나 손상됐습니다.");
        }
    }

    private static SavedFileCheck Unknown(string note) => new(SavedFileKind.Unknown, note);
}
