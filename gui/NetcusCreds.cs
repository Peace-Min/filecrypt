using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileCrypt
{
    /// <summary>
    /// 사내 보고 시스템 로그인 정보 보관.
    /// 아이디는 평문, 비밀번호는 DPAPI(현재 사용자 전용)로 암호화해 이 PC에만 둔다.
    /// 다른 계정·다른 PC 로 파일을 옮겨도 풀리지 않는다.
    ///
    /// NuGet 을 쓰지 않고 crypt32 를 직접 부른다 — 폐쇄망 오프라인 빌드를 깨지 않으려고.
    /// 비밀번호는 화면·로그·오류 메시지 어디에도 남기지 않는다.
    /// </summary>
    public static class NetcusCreds
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

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
                    ? CryptProtectData(ref inBlob, "FileCrypt", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
                if (!ok) throw new InvalidOperationException("자격증명 보호에 실패했습니다.");

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

        /// <summary>%LOCALAPPDATA%\FileCrypt\netcus.cred</summary>
        public static string CredFile
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileCrypt");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "netcus.cred");
            }
        }

        public static bool HasCreds
        {
            get
            {
                try { return File.Exists(CredFile) && !string.IsNullOrEmpty(LoadId()); }
                catch { return false; }
            }
        }

        /// <summary>저장된 아이디(없으면 빈 문자열). 비밀번호는 여기서 돌려주지 않는다.</summary>
        public static string LoadId()
        {
            try
            {
                if (!File.Exists(CredFile)) return "";
                string[] lines = File.ReadAllLines(CredFile, Encoding.UTF8);
                return lines.Length > 0 ? lines[0].Trim() : "";
            }
            catch { return ""; }
        }

        /// <summary>비밀번호를 빈 값으로 주면 기존 비밀번호를 그대로 둔다.</summary>
        public static void Save(string id, string password)
        {
            if (id == null) id = "";
            string encoded;
            if (string.IsNullOrEmpty(password))
            {
                encoded = LoadRawPassword();   // 기존 것 유지
            }
            else
            {
                encoded = Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(password), true));
            }
            File.WriteAllLines(CredFile, new[] { id.Trim(), encoded ?? "" }, new UTF8Encoding(false));
        }

        private static string LoadRawPassword()
        {
            try
            {
                if (!File.Exists(CredFile)) return "";
                string[] lines = File.ReadAllLines(CredFile, Encoding.UTF8);
                return lines.Length > 1 ? lines[1].Trim() : "";
            }
            catch { return ""; }
        }

        /// <summary>로그인 직전에만 부른다. 돌려받은 값은 즉시 쓰고 버릴 것.</summary>
        public static string LoadPassword()
        {
            string raw = LoadRawPassword();
            if (string.IsNullOrEmpty(raw)) return "";
            try { return Encoding.UTF8.GetString(Run(Convert.FromBase64String(raw), false)); }
            catch { return ""; }
        }

        public static void Clear()
        {
            try { if (File.Exists(CredFile)) File.Delete(CredFile); } catch { }
        }
    }
}
