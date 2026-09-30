using System;
using System.IO;
using System.Text;

namespace FileCrypt
{
    /// <summary>
    /// 문제를 쫓을 때 쓰는 기록. 창을 닫아도 남는다.
    ///
    /// 근태관리 연동은 사이트가 있어야만 돌아가서 개발 중에는 재현할 수 없다.
    /// 그래서 무엇이 어느 단계에서 어그러졌는지는 이 기록이 유일한 단서다.
    ///   %LOCALAPPDATA%\FileCrypt\logs\filecrypt-YYYYMMDD.log
    ///
    /// 비밀번호는 절대 쓰지 않는다 — 넘어온 문장에 섞여 있어도 가려서 남긴다.
    /// </summary>
    public static class DebugLog
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 4 * 1024 * 1024;   // 4MB 넘으면 한 번 갈아끼운다
        private static string _dir;
        private static int _sinceSizeCheck = int.MaxValue;   // 첫 줄에서 한 번은 크기를 본다

        public static string Dir
        {
            get
            {
                string d = _dir;
                if (d != null && Directory.Exists(d)) return d;
                d = Path.Combine(AppConfig.Dir, "logs");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                _dir = d;
                return d;
            }
        }

        public static string TodayFile
        {
            get { return Path.Combine(Dir, string.Format("filecrypt-{0:yyyyMMdd}.log", DateTime.Now)); }
        }

        /// <summary>한 줄 남긴다. 실패해도 앱 동작을 막지 않는다.</summary>
        public static void Write(string category, string message)
        {
            try
            {
                string line = string.Format("{0:HH:mm:ss.fff} [{1}] {2}",
                                            DateTime.Now, category ?? "-", Redact(message));
                lock (Gate)
                {
                    string f = TodayFile;
                    // 크기는 100줄마다 한 번만 본다. 한 줄은 길어야 수백 바이트라 4MB 한도를 크게 넘기지 않는다.
                    if (++_sinceSizeCheck >= 100)
                    {
                        _sinceSizeCheck = 0;
                        var fi = new FileInfo(f);
                        if (fi.Exists && fi.Length > MaxBytes)
                        {
                            string old = f + ".1";
                            try { if (File.Exists(old)) File.Delete(old); File.Move(f, old); } catch { }
                        }
                    }
                    File.AppendAllText(f, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { /* 기록 실패가 기능을 막아서는 안 된다 */ }
        }

        /// <summary>새 작업의 시작을 눈에 띄게 구분해 둔다.</summary>
        public static void Section(string title)
        {
            Write("=====", title + "  (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ")");
        }

        /// <summary>
        /// 저장된 비밀번호가 문장에 섞여 들어오면 가린다.
        /// 넘겨받는 쪽을 믿지 않는다 — 기록에 한 번 남으면 되돌릴 수 없다.
        /// </summary>
        private static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                string pw = AppConfig.NetcusPassword;
                if (pw.Length >= 4 && s.IndexOf(pw, StringComparison.Ordinal) >= 0)
                    s = s.Replace(pw, "***");
            }
            catch { }
            return s;
        }
    }
}
