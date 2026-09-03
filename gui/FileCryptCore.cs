using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FileCrypt
{
    /// <summary>복호화 대상이 손상됐거나 암호가 틀렸을 때.</summary>
    public class FileCryptAuthException : Exception
    {
        public FileCryptAuthException(string message) : base(message) { }
    }

    /// <summary>FileCrypt 컨테이너가 아닐 때.</summary>
    public class FileCryptFormatException : Exception
    {
        public FileCryptFormatException(string message) : base(message) { }
    }

    public sealed class DecryptedFile
    {
        public string FileName { get; set; }
        public byte[] Data { get; set; }
        public bool Compressed { get; set; }
    }

    /// <summary>
    /// engine\filecrypt.ps1 과 완전히 동일한 컨테이너 형식(v2)을 다룬다.
    /// 한쪽에서 만든 것을 다른 쪽에서 반드시 열 수 있어야 한다.
    ///
    /// 헤더 112바이트
    ///   0..7    "FCRYPT01"
    ///   8       version = 2
    ///   9       flags : bit0 Deflate 압축됨, bit1 PBKDF2-SHA256
    ///   10..11  예약
    ///   12..27  salt(16)
    ///   28..43  IV(16)
    ///   44..47  PBKDF2 반복 횟수 (int32 LE)
    ///   48..79  원본 SHA-256
    ///   80..111 HMAC-SHA256 (헤더 0..79 + 암호문)
    ///   112..   암호문
    ///
    /// 페이로드 = [이름길이 2B LE][원본 파일명 UTF-8][원본 바이트]
    /// </summary>
    public static class FileCryptCore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FCRYPT01");

        private const int Version  = 2;
        private const int HdrSize  = 112;
        private const int OffVer   = 8;
        private const int OffFlags = 9;
        private const int OffSalt  = 12;
        private const int OffIv    = 28;
        private const int OffIter  = 44;
        private const int OffHash  = 48;
        private const int OffHmac  = 80;

        private const int FlagZip    = 1;
        private const int FlagKdf256 = 2;

        /// <summary>암호를 묻지 않기 위한 고정키. 비밀이 아니다.</summary>
        public const string DefaultKey = "FileCrypt/default/v2/no-password";

        public const string ArmorBegin = "-----BEGIN FCRYPT MESSAGE-----";
        public const string ArmorEnd   = "-----END FCRYPT MESSAGE-----";

        public const int DefaultIterations = 200000;
        public const int DefaultWidth      = 100;

        // ------------------------------------------------------------ 압축
        private static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Inflate(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                ds.CopyTo(outMs);
                return outMs.ToArray();
            }
        }

        // ------------------------------------------------------------ 키 유도
        private static bool Sha256KdfAvailable()
        {
            try
            {
                using (new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes("x"), new byte[8], 1, HashAlgorithmName.SHA256))
                    return true;
            }
            catch { return false; }
        }

        private static void DeriveKeys(string password, byte[] salt, int iterations, bool useSha256,
                                       out byte[] aesKey, out byte[] hmacKey)
        {
            byte[] pw = Encoding.UTF8.GetBytes(password);
            byte[] material;

            if (useSha256)
            {
                using (var kdf = new Rfc2898DeriveBytes(pw, salt, iterations, HashAlgorithmName.SHA256))
                    material = kdf.GetBytes(64);
            }
            else
            {
                using (var kdf = new Rfc2898DeriveBytes(pw, salt, iterations))
                    material = kdf.GetBytes(64);
            }

            aesKey  = new byte[32];
            hmacKey = new byte[32];
            Buffer.BlockCopy(material, 0,  aesKey,  0, 32);
            Buffer.BlockCopy(material, 32, hmacKey, 0, 32);
            Array.Clear(material, 0, material.Length);
        }

        // ------------------------------------------------------------ AES / HMAC
        private static byte[] Aes256Cbc(byte[] data, byte[] key, byte[] iv, bool encrypting)
        {
            using (var aes = new AesManaged())
            {
                aes.KeySize   = 256;
                aes.BlockSize = 128;
                aes.Mode      = CipherMode.CBC;
                aes.Padding   = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV  = iv;

                using (var tr = encrypting ? aes.CreateEncryptor() : aes.CreateDecryptor())
                    return tr.TransformFinalBlock(data, 0, data.Length);
            }
        }

        private static byte[] HmacTag(byte[] key, byte[] header80, byte[] cipher)
        {
            using (var h = new HMACSHA256(key))
            {
                h.TransformBlock(header80, 0, header80.Length, null, 0);
                h.TransformFinalBlock(cipher, 0, cipher.Length);
                return h.Hash;
            }
        }

        private static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        /// <summary>타이밍 공격을 피하기 위해 길이와 무관하게 전부 비교한다.</summary>
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static byte[] RandomBytes(int count)
        {
            var b = new byte[count];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(b);
            return b;
        }

        // ------------------------------------------------------------ 암호화
        public static byte[] Encrypt(string originalFileName, byte[] plain, string password, int iterations = DefaultIterations)
        {
            if (plain == null) plain = new byte[0];
            if (string.IsNullOrEmpty(originalFileName)) originalFileName = "restored.bin";

            byte[] origHash = Sha256(plain);

            byte[] nameBytes = Encoding.UTF8.GetBytes(originalFileName);
            if (nameBytes.Length > 65535) throw new ArgumentException("파일 이름이 너무 깁니다.");

            var payload = new byte[2 + nameBytes.Length + plain.Length];
            payload[0] = (byte)(nameBytes.Length & 0xFF);
            payload[1] = (byte)((nameBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(nameBytes, 0, payload, 2, nameBytes.Length);
            if (plain.Length > 0) Buffer.BlockCopy(plain, 0, payload, 2 + nameBytes.Length, plain.Length);

            int flags = 0;
            byte[] body = payload;
            byte[] z = Deflate(payload);
            if (z.Length < payload.Length) { body = z; flags |= FlagZip; }

            bool useSha256 = Sha256KdfAvailable();
            if (useSha256) flags |= FlagKdf256;

            byte[] salt = RandomBytes(16);
            byte[] iv   = RandomBytes(16);

            byte[] aesKey, hmacKey;
            DeriveKeys(password, salt, iterations, useSha256, out aesKey, out hmacKey);

            byte[] cipher = Aes256Cbc(body, aesKey, iv, true);

            var header = new byte[HdrSize];
            Buffer.BlockCopy(Magic, 0, header, 0, 8);
            header[OffVer]   = Version;
            header[OffFlags] = (byte)flags;
            Buffer.BlockCopy(salt, 0, header, OffSalt, 16);
            Buffer.BlockCopy(iv,   0, header, OffIv,   16);
            Buffer.BlockCopy(BitConverter.GetBytes(iterations), 0, header, OffIter, 4);
            Buffer.BlockCopy(origHash, 0, header, OffHash, 32);

            var h80 = new byte[80];
            Buffer.BlockCopy(header, 0, h80, 0, 80);
            byte[] mac = HmacTag(hmacKey, h80, cipher);
            Buffer.BlockCopy(mac, 0, header, OffHmac, 32);

            Array.Clear(aesKey, 0, aesKey.Length);
            Array.Clear(hmacKey, 0, hmacKey.Length);

            var container = new byte[HdrSize + cipher.Length];
            Buffer.BlockCopy(header, 0, container, 0, HdrSize);
            Buffer.BlockCopy(cipher, 0, container, HdrSize, cipher.Length);
            return container;
        }

        // ------------------------------------------------------------ 복호화
        public static DecryptedFile Decrypt(byte[] container, string password)
        {
            if (container == null || container.Length < HdrSize)
                throw new FileCryptFormatException("FileCrypt 데이터가 아닙니다 (너무 짧음).");

            for (int i = 0; i < 8; i++)
                if (container[i] != Magic[i])
                    throw new FileCryptFormatException("FileCrypt 데이터가 아닙니다 (표식 불일치).");

            if (container[OffVer] != Version)
                throw new FileCryptFormatException("지원하지 않는 버전입니다: " + container[OffVer]);

            int flags = container[OffFlags];

            var salt = new byte[16]; Buffer.BlockCopy(container, OffSalt, salt, 0, 16);
            var iv   = new byte[16]; Buffer.BlockCopy(container, OffIv,   iv,   0, 16);
            int iterations = BitConverter.ToInt32(container, OffIter);

            var origHash = new byte[32]; Buffer.BlockCopy(container, OffHash, origHash, 0, 32);
            var mac      = new byte[32]; Buffer.BlockCopy(container, OffHmac, mac,      0, 32);

            var cipher = new byte[container.Length - HdrSize];
            Buffer.BlockCopy(container, HdrSize, cipher, 0, cipher.Length);

            bool useSha256 = (flags & FlagKdf256) != 0;
            if (useSha256 && !Sha256KdfAvailable())
                throw new FileCryptFormatException("이 환경에서는 PBKDF2-SHA256 을 쓸 수 없습니다 (.NET Framework 4.7.2 이상 필요).");

            byte[] aesKey, hmacKey;
            DeriveKeys(password, salt, iterations, useSha256, out aesKey, out hmacKey);

            var h80 = new byte[80];
            Buffer.BlockCopy(container, 0, h80, 0, 80);
            byte[] calc = HmacTag(hmacKey, h80, cipher);

            if (!BytesEqual(calc, mac))
            {
                Array.Clear(aesKey, 0, aesKey.Length);
                Array.Clear(hmacKey, 0, hmacKey.Length);
                throw new FileCryptAuthException("암호가 틀렸거나 데이터가 손상되었습니다.");
            }

            byte[] body = Aes256Cbc(cipher, aesKey, iv, false);
            Array.Clear(aesKey, 0, aesKey.Length);
            Array.Clear(hmacKey, 0, hmacKey.Length);

            byte[] payload = ((flags & FlagZip) != 0) ? Inflate(body) : body;

            if (payload.Length < 2) throw new FileCryptFormatException("페이로드가 손상되었습니다.");
            int nameLen = payload[0] | (payload[1] << 8);
            if (payload.Length < 2 + nameLen) throw new FileCryptFormatException("페이로드가 손상되었습니다 (이름 길이).");

            string name = Encoding.UTF8.GetString(payload, 2, nameLen);
            var plain = new byte[payload.Length - 2 - nameLen];
            if (plain.Length > 0) Buffer.BlockCopy(payload, 2 + nameLen, plain, 0, plain.Length);

            if (!BytesEqual(Sha256(plain), origHash))
                throw new FileCryptAuthException("복원했지만 원본 해시가 일치하지 않습니다.");

            return new DecryptedFile
            {
                FileName   = name,
                Data       = plain,
                Compressed = (flags & FlagZip) != 0
            };
        }

        // ------------------------------------------------------------ 텍스트 포장
        public static string ToArmor(byte[] container, int lineWidth = DefaultWidth)
        {
            string b64 = Convert.ToBase64String(container);
            var sb = new StringBuilder();
            sb.Append(ArmorBegin).Append("\r\n");
            if (lineWidth <= 0)
            {
                sb.Append(b64).Append("\r\n");
            }
            else
            {
                for (int i = 0; i < b64.Length; i += lineWidth)
                {
                    int n = Math.Min(lineWidth, b64.Length - i);
                    sb.Append(b64, i, n).Append("\r\n");
                }
            }
            sb.Append(ArmorEnd);
            return sb.ToString();
        }

        private static bool IsBase64Char(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                   (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';
        }

        /// <summary>
        /// 텍스트에서 FCRYPT 블록을 전부 뽑는다.
        /// 메일/채팅이 끼워넣는 제로폭 문자, 인용부호, 줄바꿈 변형은 무시한다.
        /// 데이터가 실제로 상했다면 복호화 단계의 HMAC 이 잡는다.
        /// </summary>
        public static List<byte[]> ExtractBlocks(string text)
        {
            var result = new List<byte[]>();
            if (string.IsNullOrEmpty(text)) return result;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            StringBuilder cur = null;
            foreach (string raw in lines)
            {
                string t = raw.Trim();

                if (t.StartsWith("-----BEGIN FCRYPT", StringComparison.Ordinal))
                {
                    cur = new StringBuilder();
                    continue;
                }
                if (t.StartsWith("-----END FCRYPT", StringComparison.Ordinal))
                {
                    if (cur != null) { TryAdd(result, cur); cur = null; }
                    continue;
                }
                if (cur == null || t.Length == 0) continue;

                foreach (char c in t)
                    if (IsBase64Char(c)) cur.Append(c);
            }

            // END 없이 끝난 마지막 블록도 살린다.
            if (cur != null) TryAdd(result, cur);

            return result;
        }

        private static void TryAdd(List<byte[]> list, StringBuilder sb)
        {
            if (sb.Length == 0) return;
            try { list.Add(Convert.FromBase64String(sb.ToString())); }
            catch (FormatException) { /* 블록이 깨졌으면 조용히 버린다 */ }
        }

        public static bool LooksLikeArmor(string text)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf("-----BEGIN FCRYPT", StringComparison.Ordinal) >= 0;
        }

        /// <summary>파일명 한 조각에서 못 쓰는 문자를 걸러낸다.</summary>
        private static string SanitizeSegment(string seg)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(seg.Length);
            foreach (char c in seg)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString().Trim().TrimEnd('.');
        }

        /// <summary>
        /// 컨테이너에 기록된 이름을 안전한 상대 경로로 바꾼다.
        ///
        /// 컨테이너는 남이 만들어 보낸 것일 수 있으므로 그대로 믿으면 안 된다.
        /// 드라이브 문자, 루트 슬래시, ".." 는 전부 제거해서
        /// 지정한 폴더 밖으로는 절대 못 쓰게 만든다.
        /// </summary>
        public static string SanitizeRelativePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "restored.bin";

            string t = name.Replace('\\', '/');
            var parts = t.Split('/');
            var keep = new List<string>();

            foreach (string raw in parts)
            {
                string seg = raw.Trim();
                if (seg.Length == 0) continue;      // 루트 슬래시, 연속 슬래시
                if (seg == ".") continue;
                if (seg == "..") continue;          // 상위로 못 올라간다
                if (seg.Contains(":")) continue;    // "C:", 대체 데이터 스트림
                seg = SanitizeSegment(seg);
                if (seg.Length == 0) continue;
                keep.Add(seg);
            }

            if (keep.Count == 0) return "restored.bin";
            if (keep.Count > 32) keep = keep.GetRange(keep.Count - 32, 32);   // 비정상적으로 깊은 경로

            return string.Join(Path.DirectorySeparatorChar.ToString(), keep);
        }

        /// <summary>파일 하나의 이름만 걸러낸다 (경로 구분자는 밑줄로).</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "restored.bin";
            string s = SanitizeSegment(name.Replace('\\', '_').Replace('/', '_'));
            return s.Length == 0 ? "restored.bin" : s;
        }

        /// <summary>
        /// baseDir 아래에 relativePath 로 쓸 최종 경로를 정한다.
        /// 하위 폴더는 만들어 주고, 이미 있으면 "이름 (1).확장자" 로 비켜 간다.
        /// 결과가 baseDir 밖으로 나가면 예외.
        /// </summary>
        public static string ResolveNonClobbering(string baseDir, string relativePath)
        {
            string rel = SanitizeRelativePath(relativePath);
            string full = Path.GetFullPath(Path.Combine(baseDir, rel));

            string root = Path.GetFullPath(baseDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("저장 폴더 밖으로 나가는 경로입니다: " + relativePath);

            string dir = Path.GetDirectoryName(full);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (!File.Exists(full)) return full;

            string stem = Path.GetFileNameWithoutExtension(full);
            string ext  = Path.GetExtension(full);
            for (int i = 1; i < 10000; i++)
            {
                string cand = Path.Combine(dir, string.Format("{0} ({1}){2}", stem, i, ext));
                if (!File.Exists(cand)) return cand;
            }
            throw new IOException("출력 파일 이름을 정할 수 없습니다.");
        }
    }
}
