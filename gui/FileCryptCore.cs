using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>아카이브에 담을 파일 하나.</summary>
    public sealed class ArchiveItem
    {
        public string Name { get; set; }
        public byte[] Data { get; set; }
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

        private const int FlagZip     = 1;
        private const int FlagKdf256  = 2;
        /// <summary>bit2: 페이로드가 여러 파일을 담은 아카이브</summary>
        private const int FlagArchive = 4;

        /// <summary>
        /// 이 도구에는 암호가 없다. 이 키는 소스에 박혀 있고 공개돼 있다.
        /// 하는 일: 텍스트를 눈으로 못 읽게 + 전송 중 훼손/변조 감지.
        /// 안 하는 일: 내용 보호. 이 도구를 가진 사람은 누구나 연다.
        /// </summary>
        private const string Key = "FileCrypt/default/v2/no-password";

        /// <summary>
        /// 공개된 키에 PBKDF2 스트레칭을 거는 것은 아무것도 지키지 않으면서
        /// 파일 수에 비례해 시간만 잡아먹으므로 반복은 1회.
        /// (헤더의 반복 횟수 필드는 그대로라 예전에 만든 텍스트도 열린다)
        /// </summary>
        private const int Iterations = 1;

        public const string ArmorBegin = "-----BEGIN FCRYPT MESSAGE-----";
        public const string ArmorEnd   = "-----END FCRYPT MESSAGE-----";

        // 분할 조각 표식. 한 번에 붙여넣을 수 없을 만큼 큰 결과를 나눌 때 쓴다.
        //   -----BEGIN FCRYPT PART 3/8 a1b2c3d4-----
        // 3/8 = 순서와 전체 개수, a1b2c3d4 = 어느 묶음 소속인지 (컨테이너 HMAC 앞 4바이트)
        private const string PartBegin = "-----BEGIN FCRYPT PART ";
        private const string PartEnd   = "-----END FCRYPT PART ";

        public const int DefaultWidth      = 100;

        // ------------------------------------------------------------ 압축
        // 페이로드를 조각(ArraySegment) 목록으로 받는다. 이름 헤더와 원본을 한 배열로 이어 붙이는
        // 복사를 없애려고 - 50 MB 파일이면 그 복사 하나가 50 MB 다.
        // 결과는 MemoryStream 내부 버퍼 그대로(ToArray 복사 없음). 유효 길이는 len.
        private static byte[] Deflate(IList<ArraySegment<byte>> segs, int total, out int len)
        {
            var ms = new MemoryStream(Math.Min(total, 1 << 22) + 64);
            using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                foreach (var s in segs)
                    if (s.Count > 0) ds.Write(s.Array, s.Offset, s.Count);
            len = (int)ms.Length;
            return ms.GetBuffer();
        }

        private static byte[] Inflate(byte[] data, out int len)
        {
            using (var ms = new MemoryStream(data))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                var outMs = new MemoryStream(Math.Max(256, data.Length * 2));
                ds.CopyTo(outMs);
                len = (int)outMs.Length;
                return outMs.GetBuffer();
            }
        }

        private static byte[] Concat(IList<ArraySegment<byte>> segs, int total)
        {
            if (segs.Count == 1 && segs[0].Offset == 0 && segs[0].Count == segs[0].Array.Length)
                return segs[0].Array;
            var all = new byte[total];
            int p = 0;
            foreach (var s in segs) { Buffer.BlockCopy(s.Array, s.Offset, all, p, s.Count); p += s.Count; }
            return all;
        }

        // ------------------------------------------------------------ 키 유도
        // .NET 4.7.2 이상이면 늘 true 다. 예전에는 컨테이너마다 PBKDF2 객체를 만들어 확인했다.
        private static readonly bool Kdf256 = ProbeKdf256();

        private static bool ProbeKdf256()
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
        // AesCryptoServiceProvider = Windows CNG 구현(AES-NI). AesManaged(순수 관리 코드)보다 빠르고,
        // FIPS 정책이 켜진 PC 에서도 예외가 나지 않는다. 출력 바이트는 같다.
        private static Aes NewAes(byte[] key, byte[] iv)
        {
            var aes = new AesCryptoServiceProvider();
            aes.KeySize   = 256;
            aes.BlockSize = 128;
            aes.Mode      = CipherMode.CBC;
            aes.Padding   = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV  = iv;
            return aes;
        }

        /// <summary>헤더 0..79 + 암호문(container 의 112.. 끝). 따로 떼어 복사하지 않는다.</summary>
        private static byte[] HmacTag(byte[] key, byte[] container)
        {
            using (var h = new HMACSHA256(key))
            {
                h.TransformBlock(container, 0, 80, null, 0);
                h.TransformFinalBlock(container, HdrSize, container.Length - HdrSize);
                return h.Hash;
            }
        }

        private static byte[] Sha256(byte[] data, int offset, int count)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data, offset, count);
        }

        private static byte[] Sha256(IList<ArraySegment<byte>> segs)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var s in segs)
                    if (s.Count > 0) sha.TransformBlock(s.Array, s.Offset, s.Count, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return sha.Hash;
            }
        }

        /// <summary>타이밍 공격을 피하기 위해 길이와 무관하게 전부 비교한다.</summary>
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // RNGCryptoServiceProvider.GetBytes 는 스레드 안전하다. 호출마다 새로 만들 이유가 없다.
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        private static byte[] RandomBytes(int count)
        {
            var b = new byte[count];
            Rng.GetBytes(b);
            return b;
        }

        // ------------------------------------------------------------ 암호화
        public static byte[] Encrypt(string originalFileName, byte[] plain)
        {
            if (plain == null) plain = new byte[0];
            if (string.IsNullOrEmpty(originalFileName)) originalFileName = "restored.bin";

            byte[] nameBytes = Encoding.UTF8.GetBytes(originalFileName);
            if (nameBytes.Length > 65535) throw new ArgumentException("파일 이름이 너무 깁니다.");

            // 페이로드 = [이름길이 2B][이름][원본]. 이어 붙이지 않고 조각으로 넘긴다.
            var head = new byte[2 + nameBytes.Length];
            head[0] = (byte)(nameBytes.Length & 0xFF);
            head[1] = (byte)((nameBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(nameBytes, 0, head, 2, nameBytes.Length);

            var segs = new[] { new ArraySegment<byte>(head), new ArraySegment<byte>(plain) };
            return Seal(segs, Sha256(plain, 0, plain.Length), 0);
        }

        /// <summary>페이로드를 압축 -> 암호화 -> 인증해 컨테이너로 만든다.</summary>
        private static byte[] Seal(IList<ArraySegment<byte>> segs, byte[] contentHash, int extraFlags)
        {
            int iterations = Iterations;

            long total64 = 0;
            foreach (var s in segs) total64 += s.Count;
            if (total64 > int.MaxValue - 1024) throw new ArgumentException("너무 큽니다 (2 GB 이상).");
            int total = (int)total64;

            int flags = extraFlags;
            int bodyLen;
            byte[] body = Deflate(segs, total, out bodyLen);
            if (bodyLen < total) flags |= FlagZip;
            else { body = Concat(segs, total); bodyLen = total; }   // 압축이 이득 없을 때만 한 번 잇는다

            if (Kdf256) flags |= FlagKdf256;

            byte[] salt = RandomBytes(16);
            byte[] iv   = RandomBytes(16);

            byte[] aesKey, hmacKey;
            DeriveKeys(Key, salt, iterations, Kdf256, out aesKey, out hmacKey);

            // 암호문을 컨테이너의 112 번째 바이트부터 바로 쓴다(암호문 배열 -> 컨테이너 복사 없음).
            int cipherLen = (bodyLen / 16 + 1) * 16;   // PKCS7 은 항상 1~16 바이트를 덧붙인다
            var container = new byte[HdrSize + cipherLen];
            using (var aes = NewAes(aesKey, iv))
            using (var enc = aes.CreateEncryptor())
            {
                int whole = bodyLen / 16 * 16;
                int n = whole > 0 ? enc.TransformBlock(body, 0, whole, container, HdrSize) : 0;
                byte[] last = enc.TransformFinalBlock(body, whole, bodyLen - whole);
                if (n + last.Length != cipherLen)
                    throw new CryptographicException("암호문 길이가 예상과 다릅니다.");
                Buffer.BlockCopy(last, 0, container, HdrSize + n, last.Length);
            }

            Buffer.BlockCopy(Magic, 0, container, 0, 8);
            container[OffVer]   = Version;
            container[OffFlags] = (byte)flags;
            Buffer.BlockCopy(salt, 0, container, OffSalt, 16);
            Buffer.BlockCopy(iv,   0, container, OffIv,   16);
            Buffer.BlockCopy(BitConverter.GetBytes(iterations), 0, container, OffIter, 4);
            Buffer.BlockCopy(contentHash, 0, container, OffHash, 32);

            byte[] mac = HmacTag(hmacKey, container);
            Buffer.BlockCopy(mac, 0, container, OffHmac, 32);

            Array.Clear(aesKey, 0, aesKey.Length);
            Array.Clear(hmacKey, 0, hmacKey.Length);
            return container;
        }

        // ------------------------------------------------------------ 아카이브
        //   [int32 개수]
        //   반복: [uint16 이름길이][이름 UTF-8][int64 크기][내용]
        //
        // 항목별 해시는 두지 않는다. HMAC-SHA256 이 암호문 전체를,
        // 헤더의 SHA-256 이 페이로드 전체를 이미 보증하므로 중복이고,
        // 작은 파일이 많을 때 항목당 32바이트가 결과 크기를 지배한다.
        public static byte[] EncryptArchive(IList<ArchiveItem> items)
        {
            if (items == null || items.Count == 0) throw new ArgumentException("담을 파일이 없습니다.");

            // 항목 헤더와 내용을 조각으로 넘긴다. 예전에는 전부를 MemoryStream 에 이어 쓴 뒤
            // ToArray 로 한 번 더 복사했다(원본 합계의 2배).
            var segs = new List<ArraySegment<byte>>(items.Count * 2 + 1);
            segs.Add(new ArraySegment<byte>(BitConverter.GetBytes(items.Count)));

            foreach (var it in items)
            {
                string name = string.IsNullOrEmpty(it.Name) ? "restored.bin" : it.Name;
                byte[] data = it.Data ?? new byte[0];
                byte[] nb = Encoding.UTF8.GetBytes(name);
                if (nb.Length > 65535) throw new ArgumentException("파일 이름이 너무 깁니다: " + name);

                var head = new byte[2 + nb.Length + 8];
                head[0] = (byte)(nb.Length & 0xFF);
                head[1] = (byte)((nb.Length >> 8) & 0xFF);
                Buffer.BlockCopy(nb, 0, head, 2, nb.Length);
                Buffer.BlockCopy(BitConverter.GetBytes((long)data.Length), 0, head, 2 + nb.Length, 8);

                segs.Add(new ArraySegment<byte>(head));
                if (data.Length > 0) segs.Add(new ArraySegment<byte>(data));
            }

            return Seal(segs, Sha256(segs), FlagArchive);
        }

        // ------------------------------------------------------------ 복호화
        public static List<DecryptedFile> DecryptAll(byte[] container)
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

            bool useSha256 = (flags & FlagKdf256) != 0;
            if (useSha256 && !Kdf256)
                throw new FileCryptFormatException("이 환경에서는 PBKDF2-SHA256 을 쓸 수 없습니다 (.NET Framework 4.7.2 이상 필요).");

            byte[] aesKey, hmacKey;
            DeriveKeys(Key, salt, iterations, useSha256, out aesKey, out hmacKey);

            // HMAC 과 AES 모두 컨테이너를 offset 으로 읽는다. 암호문을 따로 복사하지 않는다.
            byte[] calc = HmacTag(hmacKey, container);
            if (!BytesEqual(calc, mac))
            {
                Array.Clear(aesKey, 0, aesKey.Length);
                Array.Clear(hmacKey, 0, hmacKey.Length);
                throw new FileCryptAuthException("데이터가 손상되었거나 이 도구로 만든 것이 아닙니다.");
            }

            byte[] body;
            using (var aes = NewAes(aesKey, iv))
            using (var dec = aes.CreateDecryptor())
                body = dec.TransformFinalBlock(container, HdrSize, container.Length - HdrSize);
            Array.Clear(aesKey, 0, aesKey.Length);
            Array.Clear(hmacKey, 0, hmacKey.Length);

            bool compressed = (flags & FlagZip) != 0;
            int payloadLen = body.Length;
            byte[] payload = compressed ? Inflate(body, out payloadLen) : body;

            // ---- 아카이브: 파일 여러 개
            if ((flags & FlagArchive) != 0)
            {
                if (!BytesEqual(Sha256(payload, 0, payloadLen), origHash))
                    throw new FileCryptAuthException("복원했지만 아카이브 해시가 일치하지 않습니다.");
                return ReadArchive(payload, payloadLen, compressed);
            }

            // ---- 단일 파일
            if (payloadLen < 2) throw new FileCryptFormatException("페이로드가 손상되었습니다.");
            int nameLen = payload[0] | (payload[1] << 8);
            if (payloadLen < 2 + nameLen) throw new FileCryptFormatException("페이로드가 손상되었습니다 (이름 길이).");

            string name = Encoding.UTF8.GetString(payload, 2, nameLen);
            int plainLen = payloadLen - 2 - nameLen;

            // 해시를 먼저 대조하고(원본 자리 그대로) 맞을 때만 떼어 낸다.
            if (!BytesEqual(Sha256(payload, 2 + nameLen, plainLen), origHash))
                throw new FileCryptAuthException("복원했지만 원본 해시가 일치하지 않습니다.");

            var plain = new byte[plainLen];
            if (plainLen > 0) Buffer.BlockCopy(payload, 2 + nameLen, plain, 0, plainLen);

            return new List<DecryptedFile>
            {
                new DecryptedFile { FileName = name, Data = plain, Compressed = compressed }
            };
        }

        private static List<DecryptedFile> ReadArchive(byte[] payload, int payloadLen, bool compressed)
        {
            var list = new List<DecryptedFile>();
            int pos = 0;

            if (payloadLen < 4) throw new FileCryptFormatException("아카이브가 손상되었습니다.");
            int count = BitConverter.ToInt32(payload, pos); pos += 4;
            if (count < 0 || count > 1000000) throw new FileCryptFormatException("아카이브 항목 수가 이상합니다: " + count);

            for (int i = 0; i < count; i++)
            {
                if (pos + 2 > payloadLen) throw new FileCryptFormatException("아카이브가 잘렸습니다 (이름 길이).");
                int nl = payload[pos] | (payload[pos + 1] << 8); pos += 2;

                if (pos + nl > payloadLen) throw new FileCryptFormatException("아카이브가 잘렸습니다 (이름).");
                string nm = Encoding.UTF8.GetString(payload, pos, nl); pos += nl;

                if (pos + 8 > payloadLen) throw new FileCryptFormatException("아카이브가 잘렸습니다 (크기).");
                long dl = BitConverter.ToInt64(payload, pos); pos += 8;
                if (dl < 0 || dl > int.MaxValue) throw new FileCryptFormatException("아카이브 항목 크기가 이상합니다.");

                if (pos + dl > payloadLen) throw new FileCryptFormatException("아카이브가 잘렸습니다 (내용).");
                var data = new byte[dl];
                if (dl > 0) Buffer.BlockCopy(payload, pos, data, 0, (int)dl);
                pos += (int)dl;

                list.Add(new DecryptedFile { FileName = nm, Data = data, Compressed = compressed });
            }
            return list;
        }

        /// <summary>단일 파일 컨테이너용. 아카이브면 첫 항목만 돌려준다.</summary>
        public static DecryptedFile Decrypt(byte[] container)
        {
            var all = DecryptAll(container);
            if (all.Count == 0) throw new FileCryptFormatException("내용이 비어 있습니다.");
            return all[0];
        }

        // ------------------------------------------------------------ 텍스트 포장
        /// <summary>
        /// ToArmor 결과의 글자수를 만들지 않고 계산한다. "한도를 넘으면 나누기" 판정에 쓴다 -
        /// 예전에는 전체 텍스트를 만든 뒤 길이를 재고, 넘으면 그 텍스트를 다시 해석했다.
        /// </summary>
        public static long ArmorLength(int containerLength, int lineWidth = DefaultWidth)
        {
            long b64 = 4L * ((containerLength + 2) / 3);
            long lines = lineWidth <= 0 ? 1 : (b64 + lineWidth - 1) / lineWidth;
            return ArmorBegin.Length + 2 + b64 + 2 * lines + ArmorEnd.Length;
        }

        public static string ToArmor(byte[] container, int lineWidth = DefaultWidth)
        {
            var sb = new StringBuilder((int)Math.Min(int.MaxValue, ArmorLength(container.Length, lineWidth)));
            AppendArmor(sb, container, lineWidth);
            return sb.ToString();
        }

        /// <summary>여러 블록을 한 텍스트로 이을 때 중간 문자열 없이 바로 쓰도록.</summary>
        public static void AppendArmor(StringBuilder sb, byte[] container, int lineWidth = DefaultWidth)
        {
            string b64 = Convert.ToBase64String(container);
            sb.Append(ArmorBegin).Append("\r\n");
            AppendWrapped(sb, b64, 0, b64.Length, lineWidth);
            sb.Append(ArmorEnd);
        }

        /// <summary>s[start..start+len) 을 width 자마다 CRLF 로 접어 붙인다. width &lt;= 0 이면 한 줄.</summary>
        private static void AppendWrapped(StringBuilder sb, string s, int start, int len, int width)
        {
            if (width <= 0) { sb.Append(s, start, len).Append("\r\n"); return; }
            for (int k = 0; k < len; k += width)
                sb.Append(s, start + k, Math.Min(width, len - k)).Append("\r\n");
        }

        /// <summary>분할된 조각 묶음의 상태. UI 가 "몇 개 중 몇 개 모였는지" 를 알려줄 때 쓴다.</summary>
        public sealed class PartGroup
        {
            public string Id { get; set; }
            public int Total { get; set; }
            public List<int> Have { get; set; }
            public List<int> Missing { get; set; }
            public bool Complete { get { return Missing != null && Missing.Count == 0; } }
        }

        /// <summary>
        /// 컨테이너를 여러 조각의 텍스트로 나눈다.
        /// 크기가 줄지는 않는다. 한 번에 붙여넣을 수 없는 채널로 옮기기 위한 것이다.
        /// </summary>
        public static List<string> ToArmorParts(byte[] container, int maxCharsPerPart, int lineWidth = DefaultWidth)
        {
            if (container == null || container.Length < HdrSize)
                throw new FileCryptFormatException("FileCrypt 데이터가 아닙니다.");
            if (maxCharsPerPart < 1000) maxCharsPerPart = 1000;

            // 묶음 식별자 = HMAC 앞 4바이트. 컨테이너마다 다르므로 서로 섞이지 않는다.
            var id = new StringBuilder(8);
            for (int i = 0; i < 4; i++) id.Append(container[OffHmac + i].ToString("x2"));
            string gid = id.ToString();

            string b64 = Convert.ToBase64String(container);

            // 표식 줄과 줄바꿈이 차지하는 만큼 빼고 본문 몫을 잡는다.
            // 줄바꿈을 빼먹으면 지정한 글자수를 넘긴다 (줄폭 100 이면 본문 100자마다 CRLF 2자).
            int overhead = 140;
            int avail = Math.Max(500, maxCharsPerPart - overhead);
            int body = (lineWidth > 0)
                ? Math.Max(400, (int)((long)avail * lineWidth / (lineWidth + 2)))
                : avail;
            int count = (int)Math.Ceiling(b64.Length / (double)body);
            if (count < 1) count = 1;

            var parts = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                int start = i * body;
                int len = Math.Min(body, b64.Length - start);

                var sb = new StringBuilder(len + (lineWidth > 0 ? len / lineWidth * 2 : 0) + 160);
                sb.Append(PartBegin).Append(i + 1).Append('/').Append(count)
                  .Append(' ').Append(gid).Append("-----\r\n");
                AppendWrapped(sb, b64, start, len, lineWidth);
                sb.Append(PartEnd).Append(i + 1).Append('/').Append(count).Append(' ').Append(gid).Append("-----");
                parts.Add(sb.ToString());
            }
            return parts;
        }

        // ------------------------------------------------------------ 텍스트 해석
        // 예전에는 ExtractBlocks / ScanParts / InspectParts 가 각자 표식 정규화 + 줄 나누기를 해서
        // 같은 텍스트(클립보드 2천만 자까지)를 2~3번 복사하며 훑었다. 이제 Parse 한 번으로 끝낸다.

        private sealed class PartBucket
        {
            public int Total;
            public Dictionary<int, string> Chunks = new Dictionary<int, string>();
            public bool IsComplete
            {
                get
                {
                    for (int i = 1; i <= Total; i++) if (!Chunks.ContainsKey(i)) return false;
                    return true;
                }
            }
        }

        private sealed class ParsedText
        {
            /// <summary>MESSAGE 블록들의 base64 본문 (나타난 순서)</summary>
            public readonly List<string> Messages = new List<string>();
            public readonly Dictionary<string, PartBucket> Parts =
                new Dictionary<string, PartBucket>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// BEGIN 줄에서 "3/8 a1b2c3d4" 를 읽어낸다.
        /// 정규식을 쓰지 않는 이유: 표식 뒤에 하이픈이 몇 개 붙든, 공백이 늘어나든 견디게 하려고.
        /// </summary>
        private static bool TryParsePartHeader(string line, out int index, out int total, out string id)
        {
            index = 0; total = 0; id = null;
            int p = line.IndexOf(PartBegin, StringComparison.Ordinal);
            if (p < 0) return false;
            string rest = line.Substring(p + PartBegin.Length).Trim();
            rest = rest.TrimEnd('-').Trim();

            int sp = rest.IndexOf(' ');
            if (sp <= 0) return false;
            string nums = rest.Substring(0, sp);
            string tail = rest.Substring(sp + 1).Trim();

            int slash = nums.IndexOf('/');
            if (slash <= 0) return false;
            if (!int.TryParse(nums.Substring(0, slash), out index)) return false;
            if (!int.TryParse(nums.Substring(slash + 1), out total)) return false;
            if (index < 1 || total < 1 || index > total) return false;

            int sp2 = tail.IndexOf(' ');
            id = (sp2 > 0 ? tail.Substring(0, sp2) : tail).Trim();
            return id.Length > 0;
        }

        // base64 본문에는 하이픈이 없으므로, 텍스트 어디에 있든 표식을 찾아낼 수 있다.
        //   -{3,} BEGIN|END FCRYPT (MESSAGE | PART n/m 8자리hex) -{3,}
        // 공백·줄바꿈이 얼마든 뭉개져 있어도 매칭되게 사이사이 \s* 를 둔다.
        private static readonly Regex RxMarker = new Regex(
            @"-{3,}\s*(BEGIN|END)\s*FCRYPT\s*(MESSAGE|PART\s*\d+\s*/\s*\d+\s*[0-9a-fA-F]{8})\s*-{3,}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RxPartSpec = new Regex(
            @"(\d+)\s*/\s*(\d+)\s*([0-9a-fA-F]{8})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// netcus·메일·채팅처럼 줄바꿈/공백을 뭉개는 경로를 거친 텍스트도 복원되도록,
        /// 표식을 찾아 표준 형태(각자 한 줄, 표준 간격)로 되돌린다.
        /// base64 는 원래 공백과 무관하므로 이 정규화는 안전하다. 멱등이다(여러 번 돌려도 같다).
        /// </summary>
        private static string NormalizeMarkers(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("FCRYPT", StringComparison.OrdinalIgnoreCase) < 0) return text;
            return RxMarker.Replace(text, m =>
            {
                string kind = m.Groups[1].Value.ToUpperInvariant();     // BEGIN / END
                string spec = m.Groups[2].Value;
                string canon;
                if (spec.StartsWith("PART", StringComparison.OrdinalIgnoreCase))
                {
                    Match pm = RxPartSpec.Match(spec);
                    canon = "PART " + pm.Groups[1].Value + "/" + pm.Groups[2].Value
                          + " " + pm.Groups[3].Value.ToLowerInvariant();
                }
                else canon = "MESSAGE";
                return "\r\n-----" + kind + " FCRYPT " + canon + "-----\r\n";
            });
        }

        /// <summary>줄을 앞뒤 공백을 떼어 하나씩. Replace/Split 로 텍스트 전체를 복사하지 않는다. 빈 줄은 건너뛴다.</summary>
        private static IEnumerable<string> TrimmedLines(string s)
        {
            int start = 0, n = s.Length;
            for (int i = 0; i <= n; i++)
            {
                if (i < n && s[i] != '\r' && s[i] != '\n') continue;
                if (i > start)
                {
                    string line = s.Substring(start, i - start).Trim();
                    if (line.Length > 0) yield return line;
                }
                start = i + 1;
            }
        }

        private static bool IsBase64Char(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                   (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';
        }

        /// <summary>base64 글자만 붙인다. 대부분의 줄은 전부 base64 라 통째로 붙인다.</summary>
        private static void AppendBase64(StringBuilder sb, string line)
        {
            bool clean = true;
            foreach (char c in line) if (!IsBase64Char(c)) { clean = false; break; }
            if (clean) { sb.Append(line); return; }
            foreach (char c in line) if (IsBase64Char(c)) sb.Append(c);
        }

        /// <summary>
        /// 텍스트를 한 번 훑어 MESSAGE 블록과 PART 조각을 모은다.
        /// 메일/채팅이 끼워넣는 제로폭 문자, 인용부호, 줄바꿈 변형은 무시한다.
        /// 데이터가 실제로 상했다면 복호화 단계의 HMAC 이 잡는다.
        /// </summary>
        private static ParsedText Parse(string text)
        {
            var r = new ParsedText();
            if (string.IsNullOrEmpty(text)) return r;

            // 줄바꿈/공백이 뭉개진 채로 들어와도 표식을 되살린다(netcus HTML 렌더 등).
            text = NormalizeMarkers(text);

            const int None = 0, InMessage = 1, InPart = 2;
            int mode = None;
            StringBuilder cur = null;
            int curIdx = 0, curTot = 0;
            string curId = null;

            foreach (string t in TrimmedLines(text))
            {
                bool partBegin = t.IndexOf(PartBegin, StringComparison.Ordinal) >= 0;
                bool partEnd   = !partBegin && t.IndexOf(PartEnd, StringComparison.Ordinal) >= 0;
                bool msgBegin  = !partBegin && !partEnd && t.StartsWith("-----BEGIN FCRYPT", StringComparison.Ordinal);
                bool msgEnd    = !partBegin && !partEnd && t.StartsWith("-----END FCRYPT", StringComparison.Ordinal);

                if (partBegin || partEnd || msgBegin || msgEnd)
                {
                    // 표식은 무엇이든 지금 모으던 블록을 닫는다.
                    if (mode == InPart) FlushPart(r.Parts, cur, curIdx, curTot, curId);
                    // MESSAGE 는 자기 END 로 닫힐 때만 살린다. 조각 표식이나 다른 BEGIN 이
                    // 끼어들었으면 중간에 끊긴 것이라 버린다.
                    else if (mode == InMessage && msgEnd && cur.Length > 0) r.Messages.Add(cur.ToString());

                    mode = None; cur = null; curId = null;

                    if (partBegin)
                    {
                        int i, n; string gid;
                        if (TryParsePartHeader(t, out i, out n, out gid))
                        {
                            mode = InPart; cur = new StringBuilder();
                            curIdx = i; curTot = n; curId = gid;
                        }
                    }
                    else if (msgBegin) { mode = InMessage; cur = new StringBuilder(); }
                    continue;
                }

                if (mode != None) AppendBase64(cur, t);
            }

            // END 없이 끝난 마지막 블록도 살린다 (데이터가 온전하면 복원됨).
            if (mode == InPart) FlushPart(r.Parts, cur, curIdx, curTot, curId);
            else if (mode == InMessage && cur.Length > 0) r.Messages.Add(cur.ToString());
            return r;
        }

        private static void FlushPart(Dictionary<string, PartBucket> groups, StringBuilder cur, int idx, int tot, string id)
        {
            if (cur == null || id == null || cur.Length == 0) return;
            PartBucket bucket;
            if (!groups.TryGetValue(id, out bucket))
            {
                bucket = new PartBucket { Total = tot };
                groups[id] = bucket;
            }
            if (tot > bucket.Total) bucket.Total = tot;
            // 같은 조각을 두 번 붙여넣어도 문제되지 않는다.
            bucket.Chunks[idx] = cur.ToString();
        }

        private static List<PartGroup> ToGroups(Dictionary<string, PartBucket> parts)
        {
            var list = new List<PartGroup>();
            foreach (var kv in parts)
            {
                var have = new List<int>(kv.Value.Chunks.Keys);
                have.Sort();
                var missing = new List<int>();
                for (int i = 1; i <= kv.Value.Total; i++)
                    if (!kv.Value.Chunks.ContainsKey(i)) missing.Add(i);
                list.Add(new PartGroup { Id = kv.Key, Total = kv.Value.Total, Have = have, Missing = missing });
            }
            return list;
        }

        /// <summary>텍스트 안의 조각 묶음들이 얼마나 모였는지 살펴본다. 복원은 하지 않는다.</summary>
        public static List<PartGroup> InspectParts(string text)
        {
            return ToGroups(Parse(text).Parts);
        }

        /// <summary>복원하지 않고(base64 디코딩 없이) 무엇이 들어 있는지만 센 결과.</summary>
        public sealed class TextScan
        {
            /// <summary>통짜 MESSAGE 블록 수</summary>
            public int MessageBlocks { get; set; }
            public List<PartGroup> PartGroups { get; set; }
            public bool HasParts { get { return PartGroups.Count > 0; } }
            public int CompleteGroups
            {
                get { int n = 0; foreach (var g in PartGroups) if (g.Complete) n++; return n; }
            }
            /// <summary>되돌릴 수 있는 컨테이너 수 = 통짜 블록 + 다 모인 조각 묶음</summary>
            public int BlockCount { get { return MessageBlocks + CompleteGroups; } }
        }

        /// <summary>
        /// 화면에 "블록 몇 개 / 조각 몇 개 모임" 을 보여줄 때 쓴다. ExtractBlocks 와 달리
        /// base64 를 풀지 않으므로 훨씬 가볍다.
        /// </summary>
        public static TextScan Scan(string text)
        {
            var p = Parse(text);
            return new TextScan { MessageBlocks = p.Messages.Count, PartGroups = ToGroups(p.Parts) };
        }

        /// <summary>
        /// 텍스트에서 FCRYPT 블록을 전부 뽑는다. 다 모인 조각 묶음이 먼저, 그다음 통짜 블록.
        /// 모자란 조각 묶음은 조용히 건너뛴다 (UI 는 InspectParts 로 무엇이 없는지 따로 알려 준다).
        /// </summary>
        public static List<byte[]> ExtractBlocks(string text)
        {
            var result = new List<byte[]>();
            var p = Parse(text);

            foreach (var kv in p.Parts)
            {
                var bucket = kv.Value;
                if (!bucket.IsComplete) continue;
                var joined = new StringBuilder();
                for (int i = 1; i <= bucket.Total; i++) joined.Append(bucket.Chunks[i]);
                TryAdd(result, joined.ToString());
            }
            foreach (var m in p.Messages) TryAdd(result, m);
            return result;
        }

        private static void TryAdd(List<byte[]> list, string b64)
        {
            if (b64.Length == 0) return;
            try { list.Add(Convert.FromBase64String(b64)); }
            catch (FormatException) { /* 블록이 깨졌으면 조용히 버린다 */ }
        }

        public static bool LooksLikeArmor(string text)
        {
            // 줄바꿈/공백이 뭉개진 텍스트(netcus 등)도 표식이 있으면 armor 로 본다.
            return !string.IsNullOrEmpty(text) && RxMarker.IsMatch(text);
        }

        /// <summary>이 텍스트가 분할 조각을 담고 있는가.</summary>
        public static bool HasParts(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf(PartBegin, StringComparison.Ordinal) >= 0) return true;
            // 공백이 뭉개진 경우까지 — "BEGIN FCRYPT PART n/m id" 를 정규식으로 찾는다.
            Match m = RxMarker.Match(text);
            while (m.Success)
            {
                if (m.Groups[2].Value.StartsWith("PART", StringComparison.OrdinalIgnoreCase)) return true;
                m = m.NextMatch();
            }
            return false;
        }

        /// <summary>폴더를 훑은 결과 한 건.</summary>
        public sealed class FolderEntry
        {
            public string FullPath { get; set; }
            /// <summary>넣은 폴더의 이름을 최상위로 하는 상대 경로. 예: "프로젝트/src/ui/App.cs"</summary>
            public string RelativePath { get; set; }
        }

        /// <summary>
        /// 폴더 아래 모든 파일을 하위 폴더까지 훑어서 (실제 경로, 상대 경로) 로 돌려준다.
        /// GUI 와 테스트가 같은 코드를 쓰도록 여기에 둔다.
        /// </summary>
        public static List<FolderEntry> EnumerateFolder(string folderPath)
        {
            var result = new List<FolderEntry>();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return result;

            char sep  = Path.DirectorySeparatorChar;
            string root   = Path.GetFullPath(folderPath).TrimEnd(sep);
            string parent = Path.GetDirectoryName(root);
            string leaf   = Path.GetFileName(root);
            if (string.IsNullOrEmpty(leaf)) leaf = "folder";   // 드라이브 루트 등

            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(f);
                string rel;

                if (!string.IsNullOrEmpty(parent) &&
                    full.StartsWith(parent.TrimEnd(sep) + sep, StringComparison.OrdinalIgnoreCase))
                {
                    rel = full.Substring(parent.TrimEnd(sep).Length + 1);
                }
                else
                {
                    // 부모를 못 구하는 경우(드라이브 루트)는 폴더 이름을 앞에 붙여 준다.
                    string inner = full.StartsWith(root + sep, StringComparison.OrdinalIgnoreCase)
                                 ? full.Substring(root.Length + 1)
                                 : Path.GetFileName(full);
                    rel = leaf + sep + inner;
                }

                result.Add(new FolderEntry { FullPath = full, RelativePath = rel });
            }
            return result;
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
            return ResolveNonClobbering(baseDir, relativePath, null);
        }

        /// <param name="knownDirs">
        /// 이미 있는 줄 아는 폴더. 파일 수천 개를 같은 폴더들에 풀 때 Directory.Exists/Create 를
        /// 파일마다 반복하지 않으려고 호출하는 쪽이 들고 다닌다. null 이면 매번 확인한다.
        /// </param>
        public static string ResolveNonClobbering(string baseDir, string relativePath, ISet<string> knownDirs)
        {
            string rel = SanitizeRelativePath(relativePath);
            string full = Path.GetFullPath(Path.Combine(baseDir, rel));

            string root = Path.GetFullPath(baseDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("저장 폴더 밖으로 나가는 경로입니다: " + relativePath);

            // Windows 경로 길이 제한. 아무 예외나 던지면 원인을 알 수 없으므로 미리 잡아 준다.
            if (full.Length >= 250)
                throw new PathTooLongException(
                    string.Format("경로가 너무 깁니다 ({0}자). 저장 폴더를 더 짧은 곳으로 지정하세요: {1}",
                                  full.Length, rel));

            string dir = Path.GetDirectoryName(full);
            if (knownDirs == null || !knownDirs.Contains(dir))
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (knownDirs != null) knownDirs.Add(dir);
            }

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
