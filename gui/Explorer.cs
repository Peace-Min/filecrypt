using System.Diagnostics;
using System.IO;

namespace FileCrypt
{
    /// <summary>결과를 탐색기로 보여 준다. 폴더면 그 폴더를 열고, 파일이면 그 파일을 선택한 채로 연다.</summary>
    internal static class Explorer
    {
        /// <summary>이 환경변수가 "1" 이면 탐색기를 열지 않는다(UI 자동 시험이 창을 띄우지 않도록).</summary>
        public const string SuppressEnvVar = "FILECRYPT_NO_EXPLORER";

        public static void Show(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (System.Environment.GetEnvironmentVariable(SuppressEnvVar) == "1") return;
            try
            {
                string args = Directory.Exists(path) ? "\"" + path + "\"" : "/select,\"" + path + "\"";
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = args, UseShellExecute = true });
            }
            catch { /* 탐색기를 못 열어도 복원 결과는 그대로다 */ }
        }
    }
}
