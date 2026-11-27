using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HwpSaveHook.Launcher;

/// <summary>서명 검증을 통과한 업데이트 정보.</summary>
public sealed record UpdateManifest(Version Version, string ArtifactUrl, string Sha256, long Size, DateTimeOffset ExpiresAt);

/// <summary>검증은 통과했지만 현재 버전보다 새 버전이 아니다.</summary>
public sealed class NoUpdateAvailableException(string message) : Exception(message);

/// <summary>
/// 업데이트 매니페스트(updates/latest.json)의 형식·서명 검증.
/// 서명은 ECDSA P-256 + SHA-256(IEEE P1363, r||s)이며, .NET Framework 4.8과 .NET 10 모두 기본 제공 API로 검증한다.
/// </summary>
public static class UpdateManifestVerifier
{
    public const int MaxManifestBytes = 64 * 1024;
    public const long MaxArtifactBytes = 64L * 1024 * 1024;
    private const string Domain = "hwpsavehook-update-v1";

    /// <summary>서명 대상 문자열. 필드 순서와 형식이 고정돼 있어 JSON 직렬화 차이에 영향을 받지 않는다.</summary>
    public static byte[] CanonicalPayload(string version, string artifactUrl, string sha256, long size, string expiresAt) =>
        Encoding.UTF8.GetBytes(string.Join("\n",
            Domain,
            "version=" + version,
            "artifact_url=" + artifactUrl,
            "sha256=" + sha256.ToLowerInvariant(),
            "size=" + size.ToString(CultureInfo.InvariantCulture),
            "expires_at=" + expiresAt) + "\n");

    public static UpdateManifest Verify(string json, string publicKeyBase64, Version currentVersion, DateTimeOffset? now = null)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes) throw new FormatException("매니페스트가 너무 큽니다.");
        Dictionary<string, string> fields = FlatJson.Parse(json);
        string Field(string name) => fields.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim() : throw new FormatException($"매니페스트에 {name} 항목이 없습니다.");

        string versionText = Field("version");
        string artifactUrl = Field("artifact_url");
        string sha256 = Field("sha256").ToLowerInvariant();
        string sizeText = Field("size");
        string expiresText = Field("expires_at");
        byte[] signature;
        try { signature = Convert.FromBase64String(Field("signature")); }
        catch (FormatException) { throw new FormatException("매니페스트 서명 형식이 잘못됐습니다."); }

        if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out long size))
        {
            throw new FormatException("매니페스트 크기 값이 잘못됐습니다.");
        }

        byte[] payload = CanonicalPayload(versionText, artifactUrl, sha256, size, expiresText);
        if (!VerifySignature(payload, signature, publicKeyBase64))
        {
            throw new CryptographicException("매니페스트 서명 검증에 실패했습니다.");
        }

        // 서명이 맞은 뒤에도 값 자체를 검사한다(서명 키 유출 시 피해를 줄이는 이중 확인).
        if (!Version.TryParse(versionText, out Version? version) || version is null)
        {
            throw new FormatException("매니페스트 버전 형식이 잘못됐습니다.");
        }
        if (!Uri.TryCreate(artifactUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new FormatException("업데이트 파일 주소는 HTTPS여야 합니다.");
        }
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new FormatException("매니페스트 해시가 잘못됐습니다.");
        if (size <= 0 || size > MaxArtifactBytes) throw new FormatException("업데이트 파일 크기가 허용 범위를 벗어났습니다.");
        if (!DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset expiresAt))
        {
            throw new FormatException("매니페스트 만료 시각이 잘못됐습니다.");
        }
        if (expiresAt <= (now ?? DateTimeOffset.UtcNow)) throw new FormatException("매니페스트가 만료됐습니다.");
        if (Normalize(version) <= Normalize(currentVersion))
        {
            throw new NoUpdateAvailableException("현재 버전이 최신입니다.");
        }

        return new UpdateManifest(version, artifactUrl, sha256, size, expiresAt);
    }

    /// <summary>1.2와 1.2.0.0을 같게 비교하도록 빠진 자리를 0으로 채운다.</summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public static bool VerifySignature(byte[] payload, byte[] signature, string publicKeyBase64)
    {
        byte[] key;
        try { key = Convert.FromBase64String(publicKeyBase64); }
        catch (FormatException) { return false; }
        if (key.Length != 64 || signature.Length != 64) return false;

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = key.Take(32).ToArray(), Y = key.Skip(32).ToArray() },
        };
        try
        {
            using ECDsa verifier = ECDsa.Create(parameters);
            return verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>값이 문자열 또는 정수인 1단계 JSON 객체만 읽는다. 매니페스트 외 용도로 쓰지 않는다.</summary>
    internal static class FlatJson
    {
        public static Dictionary<string, string> Parse(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            Skip();
            Expect('{');
            Skip();
            if (Peek() == '}') { i++; return End(); }
            while (true)
            {
                Skip();
                string key = ReadString();
                Skip();
                Expect(':');
                Skip();
                string value = Peek() == '"' ? ReadString() : ReadNumber();
                if (result.ContainsKey(key)) throw new FormatException($"중복된 항목: {key}");
                result[key] = value;
                Skip();
                char next = Next();
                if (next == '}') return End();
                if (next != ',') throw new FormatException("JSON 형식이 잘못됐습니다.");
            }

            Dictionary<string, string> End()
            {
                Skip();
                if (i != json.Length) throw new FormatException("JSON 뒤에 불필요한 내용이 있습니다.");
                return result;
            }
            char Peek() => i < json.Length ? json[i] : throw new FormatException("JSON이 중간에 끝났습니다.");
            char Next() { char c = Peek(); i++; return c; }
            void Expect(char c) { if (Next() != c) throw new FormatException("JSON 형식이 잘못됐습니다."); }
            void Skip() { while (i < json.Length && char.IsWhiteSpace(json[i])) i++; }
            string ReadNumber()
            {
                int start = i;
                if (i < json.Length && json[i] == '-') i++;
                while (i < json.Length && char.IsDigit(json[i])) i++;
                if (i == start) throw new FormatException("JSON 값은 문자열이나 정수여야 합니다.");
                return json.Substring(start, i - start);
            }
            string ReadString()
            {
                Expect('"');
                var text = new StringBuilder();
                while (true)
                {
                    char c = Next();
                    if (c == '"') return text.ToString();
                    if (c < ' ') throw new FormatException("JSON 문자열에 제어 문자가 있습니다.");
                    if (c != '\\') { text.Append(c); continue; }
                    char escape = Next();
                    switch (escape)
                    {
                        case '"': case '\\': case '/': text.Append(escape); break;
                        case 'n': text.Append('\n'); break;
                        case 't': text.Append('\t'); break;
                        case 'r': text.Append('\r'); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'u':
                            if (i + 4 > json.Length) throw new FormatException("JSON 이스케이프가 잘못됐습니다.");
                            text.Append((char)int.Parse(json.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException("JSON 이스케이프가 잘못됐습니다.");
                    }
                }
            }
        }
    }
}
