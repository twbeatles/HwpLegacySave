using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace HwpSaveHook.Launcher;

/// <summary>Hwp.exe의 실행 파일 구조 분류.</summary>
public enum HwpBuildKind
{
    /// <summary>파일을 읽지 못했거나 PE가 아니다.</summary>
    Unreadable,
    /// <summary>CLR 헤더가 없는 네이티브 실행 파일. CLR 프로파일러 방식을 적용할 수 없다.</summary>
    Native,
    /// <summary>.NET 어셈블리.</summary>
    Managed,
}

/// <summary>
/// 설치된 Hwp.exe를 읽기 전용으로 분석한 결과.
/// <see cref="TargetToken"/>은 <c>Hwp.HwpFrame::ConvertToHwpxPath(ref string, ref string) : bool</c>를
/// 정확한 시그니처로 찾았을 때만 값이 있다.
/// </summary>
public sealed record HwpBuildInfo(
    HwpBuildKind Kind,
    string? FileVersion,
    string? ProductName,
    bool IsX86,
    Guid? Mvid,
    string? RuntimeVersion,
    int? TargetToken,
    string? Error)
{
    public bool HasCompatibleTarget => Kind == HwpBuildKind.Managed && IsX86 && TargetToken is not null;
}

/// <summary>
/// 외부 패키지 없이 ECMA-335 메타데이터에서 대상 메서드를 찾는다.
/// .NET Framework 4.8 GUI와 .NET 10 CLI가 같은 코드를 쓴다.
/// </summary>
public static class HwpBuildInspector
{
    public const string TargetTypeNamespace = "Hwp";
    public const string TargetTypeName = "HwpFrame";
    public const string TargetMethodName = "ConvertToHwpxPath";

    /// <summary>instance bool (ref string, ref string)</summary>
    internal static readonly byte[] TargetSignature = [0x20, 0x02, 0x02, 0x10, 0x0E, 0x10, 0x0E];

    private const long MaxImageBytes = 512L * 1024 * 1024;

    public static HwpBuildInfo Inspect(string path)
    {
        string? fileVersion = null;
        try { fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException) { }

        byte[] image;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Fail("file-not-found");
            if (info.Length > MaxImageBytes) return Fail("file-too-large");
            image = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Fail(error.Message);
        }

        HwpBuildInfo result = InspectImage(image);
        return result with { FileVersion = fileVersion, ProductName = DescribeProduct(fileVersion) };

        HwpBuildInfo Fail(string reason) =>
            new(HwpBuildKind.Unreadable, fileVersion, DescribeProduct(fileVersion), false, null, null, null, reason);
    }

    /// <summary>
    /// 파일 버전의 주 번호로 제품 이름을 추정한다(한컴오피스 설치 폴더 HOffice{주번호}0과 같은 규칙).
    /// 9.x는 2014 VP와 NEO가 같은 주 번호를 써서 함께 표기한다.
    /// </summary>
    public static string? DescribeProduct(string? fileVersion)
    {
        if (fileVersion is null) return null;
        string head = fileVersion.Split('.', ',', ' ')[0];
        if (!int.TryParse(head, out int major)) return null;
        return major switch
        {
            8 => "한글 2010",
            9 => "한글 2014/NEO",
            10 => "한글 2018",
            11 => "한글 2020",
            12 => "한글 2022",
            13 => "한글 2024",
            > 13 => $"한글 (버전 {major})",
            _ => null,
        };
    }

    internal static HwpBuildInfo InspectImage(byte[] image)
    {
        try
        {
            return InspectCore(new Image(image));
        }
        catch (Exception error) when (error is BadImageException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return new(HwpBuildKind.Unreadable, null, null, false, null, null, null,
                error is BadImageException ? error.Message : "invalid-image");
        }
    }

    private static HwpBuildInfo InspectCore(Image pe)
    {
        if (pe.U16(0) != 0x5A4D) throw new BadImageException("not-pe");
        int peHeader = checked((int)pe.U32(0x3C));
        if (pe.U32(peHeader) != 0x00004550) throw new BadImageException("not-pe");

        int coff = peHeader + 4;
        ushort machine = pe.U16(coff);
        ushort sectionCount = pe.U16(coff + 2);
        ushort optionalSize = pe.U16(coff + 16);
        int optional = coff + 20;
        ushort magic = pe.U16(optional);
        int directories = magic switch
        {
            0x10B => optional + 96,
            0x20B => optional + 112,
            _ => throw new BadImageException("bad-optional-header"),
        };
        uint directoryCount = pe.U32(directories - 4);
        bool isX86 = machine == 0x014C;

        var sections = new List<(uint Va, uint VirtualSize, uint RawPointer, uint RawSize)>();
        int sectionTable = optional + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int s = sectionTable + (i * 40);
            sections.Add((pe.U32(s + 12), pe.U32(s + 8), pe.U32(s + 20), pe.U32(s + 16)));
        }

        int Offset(uint rva)
        {
            foreach (var section in sections)
            {
                uint span = Math.Max(section.VirtualSize, section.RawSize);
                if (rva >= section.Va && rva < section.Va + span)
                {
                    return checked((int)(rva - section.Va + section.RawPointer));
                }
            }
            throw new BadImageException("rva-out-of-range");
        }

        if (directoryCount <= 14 || pe.U32(directories + (14 * 8)) == 0)
        {
            return new(HwpBuildKind.Native, null, null, isX86, null, null, null, null);
        }

        int cli = Offset(pe.U32(directories + (14 * 8)));
        uint corFlags = pe.U32(cli + 16);
        // 32BITREQUIRED가 없는 AnyCPU 어셈블리는 64비트 OS에서 x64로 뜰 수 있어 x86 프로파일러를 쓸 수 없다.
        bool requires32Bit = (corFlags & 0x2) != 0;
        int metadata = Offset(pe.U32(cli + 8));
        if (pe.U32(metadata) != 0x424A5342) throw new BadImageException("bad-metadata-signature");
        int versionLength = checked((int)pe.U32(metadata + 12));
        string runtime = Encoding.ASCII.GetString(pe.Bytes, metadata + 16, versionLength).TrimEnd('\0');
        int cursor = metadata + 16 + versionLength;
        ushort streamCount = pe.U16(cursor + 2);
        cursor += 4;

        int tables = -1, strings = -1, blob = -1, guid = -1;
        for (int i = 0; i < streamCount; i++)
        {
            int offset = metadata + checked((int)pe.U32(cursor));
            cursor += 8;
            int nameStart = cursor;
            while (pe.Bytes[cursor] != 0) cursor++;
            string name = Encoding.ASCII.GetString(pe.Bytes, nameStart, cursor - nameStart);
            cursor = nameStart + (((cursor - nameStart) + 4) & ~3);
            switch (name)
            {
                case "#~": tables = offset; break;
                case "#-": throw new BadImageException("uncompressed-metadata");
                case "#Strings": strings = offset; break;
                case "#Blob": blob = offset; break;
                case "#GUID": guid = offset; break;
            }
        }
        if (tables < 0 || strings < 0 || blob < 0 || guid < 0) throw new BadImageException("missing-metadata-stream");

        byte heapSizes = pe.Bytes[tables + 6];
        ulong valid = pe.U64(tables + 8);
        var rows = new uint[64];
        int rowCursor = tables + 24;
        for (int t = 0; t < 64; t++)
        {
            if ((valid & (1UL << t)) == 0) continue;
            rows[t] = pe.U32(rowCursor);
            rowCursor += 4;
        }
        if ((heapSizes & 0x40) != 0) rowCursor += 4;

        int stringIndex = (heapSizes & 0x01) != 0 ? 4 : 2;
        int guidIndex = (heapSizes & 0x02) != 0 ? 4 : 2;
        int blobIndex = (heapSizes & 0x04) != 0 ? 4 : 2;
        int Simple(int table) => rows[table] < 0x10000 ? 2 : 4;
        int Coded(int tagBits, params int[] tablesInCode) =>
            tablesInCode.Max(t => rows[t]) < (1u << (16 - tagBits)) ? 2 : 4;

        const int Module = 0x00, TypeRef = 0x01, TypeDef = 0x02, FieldPtr = 0x03, Field = 0x04,
            MethodPtr = 0x05, MethodDef = 0x06, Param = 0x08, ModuleRef = 0x1A, TypeSpec = 0x1B, AssemblyRef = 0x23;
        if (rows[FieldPtr] != 0 || rows[MethodPtr] != 0) throw new BadImageException("indirect-method-table");

        int moduleRow = 2 + stringIndex + (3 * guidIndex);
        int typeRefRow = Coded(2, Module, ModuleRef, AssemblyRef, TypeRef) + (2 * stringIndex);
        int fieldIndex = Simple(Field);
        int methodIndex = Simple(MethodDef);
        int typeDefRow = 4 + (2 * stringIndex) + Coded(2, TypeDef, TypeRef, TypeSpec) + fieldIndex + methodIndex;
        int fieldRow = 2 + stringIndex + blobIndex;
        int methodRow = 4 + 2 + 2 + stringIndex + blobIndex + Simple(Param);

        int moduleTable = rowCursor;
        int typeRefTable = moduleTable + (checked((int)rows[Module]) * moduleRow);
        int typeDefTable = typeRefTable + (checked((int)rows[TypeRef]) * typeRefRow);
        int fieldTable = typeDefTable + (checked((int)rows[TypeDef]) * typeDefRow);
        int methodTable = fieldTable + (checked((int)rows[Field]) * fieldRow);

        uint Index(int offset, int size) => size == 2 ? pe.U16(offset) : pe.U32(offset);
        string ReadString(uint index)
        {
            int start = strings + checked((int)index);
            int end = start;
            while (pe.Bytes[end] != 0) end++;
            return Encoding.UTF8.GetString(pe.Bytes, start, end - start);
        }
        byte[] ReadBlob(uint index)
        {
            int at = blob + checked((int)index);
            byte first = pe.Bytes[at];
            int length;
            if ((first & 0x80) == 0) { length = first; at += 1; }
            else if ((first & 0xC0) == 0x80) { length = ((first & 0x3F) << 8) | pe.Bytes[at + 1]; at += 2; }
            else { length = ((first & 0x1F) << 24) | (pe.Bytes[at + 1] << 16) | (pe.Bytes[at + 2] << 8) | pe.Bytes[at + 3]; at += 4; }
            var value = new byte[length];
            Buffer.BlockCopy(pe.Bytes, at, value, 0, length);
            return value;
        }

        Guid? mvid = null;
        if (rows[Module] > 0)
        {
            uint mvidIndex = Index(moduleTable + 2 + stringIndex, guidIndex);
            if (mvidIndex > 0)
            {
                var raw = new byte[16];
                Buffer.BlockCopy(pe.Bytes, guid + checked((int)((mvidIndex - 1) * 16)), raw, 0, 16);
                mvid = new Guid(raw);
            }
        }

        int? token = null;
        int methodListOffset = 4 + (2 * stringIndex) + Coded(2, TypeDef, TypeRef, TypeSpec) + fieldIndex;
        for (uint type = 0; type < rows[TypeDef] && token is null; type++)
        {
            int row = typeDefTable + checked((int)(type * typeDefRow));
            if (ReadString(Index(row + 4, stringIndex)) != TargetTypeName ||
                ReadString(Index(row + 4 + stringIndex, stringIndex)) != TargetTypeNamespace)
            {
                continue;
            }

            uint first = Index(row + methodListOffset, methodIndex);
            uint last = type + 1 < rows[TypeDef]
                ? Index(row + typeDefRow + methodListOffset, methodIndex)
                : rows[MethodDef] + 1;
            for (uint method = first; method < last && method <= rows[MethodDef]; method++)
            {
                int methodRowOffset = methodTable + checked((int)((method - 1) * methodRow));
                if (ReadString(Index(methodRowOffset + 8, stringIndex)) != TargetMethodName) continue;
                if (!ReadBlob(Index(methodRowOffset + 8 + stringIndex, blobIndex)).SequenceEqual(TargetSignature)) continue;
                token = unchecked((int)(0x06000000u | method));
                break;
            }
        }

        return new(HwpBuildKind.Managed, null, null, isX86 && requires32Bit, mvid, runtime, token, null);
    }

    private sealed class Image(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;
        public ushort U16(int offset) => (ushort)(Bytes[offset] | (Bytes[offset + 1] << 8));
        public uint U32(int offset) => (uint)(Bytes[offset] | (Bytes[offset + 1] << 8) | (Bytes[offset + 2] << 16) | (Bytes[offset + 3] << 24));
        public ulong U64(int offset) => U32(offset) | ((ulong)U32(offset + 4) << 32);
    }

    private sealed class BadImageException(string message) : Exception(message);
}
