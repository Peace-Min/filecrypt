using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileCrypt
{
    /// <summary>
    /// 비밀번호 같은 값을 이 PC·이 계정에서만 풀리게 감싼다(Windows DPAPI).
    /// 파일을 통째로 다른 PC 로 옮겨도 풀리지 않는다.
    /// NuGet 을 쓰지 않고 crypt32 를 직접 부른다 — 폐쇄망 오프라인 빌드를 깨지 않으려고.
    /// </summary>
    internal static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int UI_FORBIDDEN = 0x1;

        private static byte[] Run(byte[] input, bool protect)
        {
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            try
            {
                inBlob.cbData = input.Length;
                inBlob.pbData = Marshal.AllocHGlobal(input.Length);
                Marshal.Copy(input, 0, inBlob.pbData, input.Length);

                bool ok = protect
                    ? CryptProtectData(ref inBlob, "FileCrypt", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, ref outBlob);
                if (!ok) throw new InvalidOperationException("보호된 값을 처리하지 못했습니다.");

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            return Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plain), true));
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try { return Encoding.UTF8.GetString(Run(Convert.FromBase64String(stored), false)); }
            catch { return ""; }   // 다른 PC/계정에서 복사해 온 값 — 조용히 빈 값으로 본다
        }
    }

    /// <summary>
    /// 앱 설정. %LOCALAPPDATA%\FileCrypt\config.ini 에 key=value 로 둔다.
    /// 한 번 저장하면 앱을 껐다 켜도 그대로 유지된다.
    ///
    /// 비밀번호는 평문으로 두지 않는다 — DPAPI 로 감싼 값만 파일에 들어간다.
    /// 그래서 파일을 열어 봐도 비밀번호는 보이지 않고, 다른 PC 로 옮겨도 못 푼다.
    /// </summary>
    public static class AppConfig
    {
        private const string KeyId       = "netcus.id";
        private const string KeyPw       = "netcus.pw";        // DPAPI 로 감싼 값
        private const string KeyVerified = "netcus.verified";  // 마지막으로 로그인 확인된 시각
        private const string KeyLimit    = "netcus.limit";     // 한 날짜에 넣을 최대 글자수

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _cache;

        public static string Dir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileCrypt");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string File_ { get { return Path.Combine(Dir, "config.ini"); } }

        private static Dictionary<string, string> Load()
        {
            lock (Gate)
            {
                if (_cache != null) return _cache;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (System.IO.File.Exists(File_))
                    {
                        foreach (string raw in System.IO.File.ReadAllLines(File_, Encoding.UTF8))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line[0] == '#') continue;
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                    }
                }
                catch { /* 설정을 못 읽어도 앱은 떠야 한다 */ }
                _cache = map;
                return _cache;
            }
        }

        private static void Save()
        {
            lock (Gate)
            {
                var map = _cache ?? new Dictionary<string, string>();
                var sb = new StringBuilder();
                sb.AppendLine("# FileCrypt 설정. 비밀번호는 이 PC 에서만 풀리게 보호돼 있습니다.");
                foreach (var kv in map) sb.AppendLine(kv.Key + "=" + kv.Value);
                System.IO.File.WriteAllText(File_, sb.ToString(), new UTF8Encoding(false));
            }
        }

        public static string Get(string key, string fallback)
        {
            string v;
            return Load().TryGetValue(key, out v) && !string.IsNullOrEmpty(v) ? v : fallback;
        }

        public static void Set(string key, string value)
        {
            lock (Gate)
            {
                var map = Load();
                if (string.IsNullOrEmpty(value)) map.Remove(key);
                else map[key] = value;
            }
            Save();
        }

        // ------------------------------------------------------------ 근태관리 계정
        public static string NetcusId
        {
            get { return Get(KeyId, ""); }
            set { Set(KeyId, (value ?? "").Trim()); }
        }

        /// <summary>읽으면 평문, 쓰면 보호해서 저장. 빈 값을 쓰면 지운다.</summary>
        public static string NetcusPassword
        {
            get { return Dpapi.Unprotect(Get(KeyPw, "")); }
            set { Set(KeyPw, string.IsNullOrEmpty(value) ? "" : Dpapi.Protect(value)); }
        }

        /// <summary>아이디와 비밀번호가 둘 다 있는가. 있으면 매번 묻지 않는다.</summary>
        public static bool HasNetcusAccount
        {
            get { return NetcusId.Length > 0 && Get(KeyPw, "").Length > 0; }
        }

        /// <summary>마지막으로 실제 로그인이 확인된 시각(없으면 null).</summary>
        public static DateTime? NetcusVerifiedAt
        {
            get
            {
                DateTime d;
                string s = Get(KeyVerified, "");
                if (s.Length > 0 && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out d)) return d;
                return null;
            }
            set
            {
                Set(KeyVerified, value.HasValue
                    ? value.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture) : "");
            }
        }

        /// <summary>한 날짜에 넣을 최대 글자수. 붙여넣는 쪽 한도에 맞춘다.</summary>
        public static int NetcusLimit
        {
            get
            {
                int v;
                if (int.TryParse(Get(KeyLimit, ""), out v) && v >= 1000) return v;
                return NetcusPlan.DefaultLimit;
            }
            set { Set(KeyLimit, value >= 1000 ? value.ToString() : ""); }
        }

        /// <summary>계정 정보만 지운다(다른 설정은 둔다).</summary>
        public static void ClearNetcusAccount()
        {
            Set(KeyId, "");
            Set(KeyPw, "");
            Set(KeyVerified, "");
        }

        /// <summary>테스트용 — 다음 읽기에서 파일을 다시 읽게 한다.</summary>
        public static void Reload() { lock (Gate) { _cache = null; } }
    }
}
