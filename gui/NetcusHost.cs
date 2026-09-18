using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace FileCrypt
{
    /// <summary>
    /// 수행과제 캘린더(task-calendar-db)의 NetcusService 를 FileCrypt 에서 그대로 쓰기 위한 연결부.
    /// NetcusService 는 한 글자도 고치지 않는다 — 실사용으로 검증된 로직이라 손대면 그 가치가 사라진다.
    ///
    /// NetcusService 는 결과를 '웹페이지의 JS 함수 호출' 로 돌려주도록 만들어져 있다(캘린더는 HTML 앱이라).
    /// FileCrypt 에는 그 페이지가 없으므로, 여기서 그 호출을 받아 C# 이벤트로 바꿔 준다.
    /// </summary>
    internal sealed class NetcusHost : INetcusHost
    {
        public Dispatcher Dispatcher { get; private set; }
        public CoreWebView2Environment Env { get; private set; }
        public string DataDir { get { return AppConfig.Dir; } }

        /// <summary>진행 상황 한 줄.</summary>
        public event Action<string> Progress;
        /// <summary>작업 하나가 끝났을 때 (성공여부, 메시지).</summary>
        public event Action<bool, string> Finished;
        /// <summary>자세한 로그(문제 추적용).</summary>
        public event Action<string> Logged;

        private readonly Dictionary<string, TaskCompletionSource<string>> _waiters
            = new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        private TaskCompletionSource<KeyValuePair<bool, string>> _result;

        public NetcusHost(Dispatcher dispatcher) { Dispatcher = dispatcher; }

        public async Task InitAsync()
        {
            if (Env != null) return;
            string udf = System.IO.Path.Combine(AppConfig.Dir, "wv2");
            System.IO.Directory.CreateDirectory(udf);
            Env = await CoreWebView2Environment.CreateAsync(null, udf);
        }

        // ------------------------------------------------------------ INetcusHost
        public void Log(string msg)
        {
            var h = Logged; if (h != null) h(msg);
        }

        public void Reply(string reqId, object payload)
        {
            string json;
            try { json = JsonSerializer.Serialize(payload); }
            catch (Exception ex) { json = "{\"ok\":false,\"error\":\"" + ex.Message + "\"}"; }

            TaskCompletionSource<string> tcs;
            lock (_waiters)
            {
                if (!_waiters.TryGetValue(reqId, out tcs)) return;
                _waiters.Remove(reqId);
            }
            tcs.TrySetResult(json);
        }

        /// <summary>
        /// NetcusService 가 웹페이지로 보내려던 호출을 가로채 뜻을 읽는다.
        /// 형태: window.__netcusResult &amp;&amp; window.__netcusResult(true,"메시지")
        /// </summary>
        public void Eval(string js)
        {
            if (string.IsNullOrEmpty(js)) return;
            string args;

            if (TryArgs(js, "__netcusResult", out args) || TryArgs(js, "__netcusCredsResult", out args))
            {
                int comma = args.IndexOf(',');
                bool ok = comma > 0 && args.Substring(0, comma).Trim() == "true";
                string msg = comma > 0 ? JsonText(args.Substring(comma + 1)) : "";
                var h = Finished; if (h != null) h(ok, msg);
                var r = _result; if (r != null) r.TrySetResult(new KeyValuePair<bool, string>(ok, msg));
                return;
            }

            if (TryArgs(js, "__netcusProgress", out args) || TryArgs(js, "__netcusCredsCheck", out args))
            {
                var h = Progress; if (h != null) h(JsonText(args));
            }
            // __netcusBusy / __netcusStatus 는 진행바용이라 여기서는 쓰지 않는다.
        }

        public void SaveDailyReport(int y, int m, int d, string status, int overtime, string content, string hoursJson)
        {
            // FileCrypt 에는 보고 기록 DB 가 없다. 캘린더 쪽 기능이라 여기서는 할 일이 없다.
        }

        public void SaveWeeklyReport(string sdate, string edate, string subject, string content, string endwork, string planwork)
        {
        }

        // ------------------------------------------------------------ 대기 헬퍼
        /// <summary>다음 작업 완료(__netcusResult)를 기다릴 준비. 작업을 시작하기 '전에' 부른다.</summary>
        public Task<KeyValuePair<bool, string>> ExpectResult()
        {
            _result = new TaskCompletionSource<KeyValuePair<bool, string>>();
            return _result.Task;
        }

        /// <summary>reqId 로 오는 회신(Reply)을 기다릴 준비.</summary>
        public Task<string> ExpectReply(string reqId)
        {
            var tcs = new TaskCompletionSource<string>();
            lock (_waiters) { _waiters[reqId] = tcs; }
            return tcs.Task;
        }

        // ------------------------------------------------------------ 파싱
        private static bool TryArgs(string js, string fn, out string args)
        {
            args = null;
            string marker = fn + "(";
            int i = js.LastIndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return false;
            int start = i + marker.Length;
            int end = js.LastIndexOf(')');
            if (end <= start) return false;
            args = js.Substring(start, end - start);
            return true;
        }

        /// <summary>JSON 문자열 리터럴을 평범한 문자열로.</summary>
        private static string JsonText(string jsonLiteral)
        {
            string s = (jsonLiteral ?? "").Trim();
            if (s.Length == 0) return "";
            try { return JsonSerializer.Deserialize<string>(s) ?? ""; }
            catch { return s.Trim('"'); }
        }
    }
}
