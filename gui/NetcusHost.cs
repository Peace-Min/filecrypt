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
        /// <summary>자세한 로그(문제 추적용).</summary>
        public event Action<string> Logged;

        private readonly Dictionary<string, TaskCompletionSource<string>> _waiters
            = new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        private TaskCompletionSource<KeyValuePair<bool, string>> _result;

        public NetcusHost(Dispatcher dispatcher) { Dispatcher = dispatcher; }

        /// <summary>
        /// 이 환경변수에 포트(예: "54831")가 있으면 www.netcus.com 을 127.0.0.1:포트 의 목업 서버
        /// (tools\netcus-mock)로 돌린다. 테스트용이다 - 실제 사이트에 한 번도 닿지 않는다.
        /// 목업은 자체 서명 인증서를 쓰므로 이때만 인증서 검사를 끈다. 쿠키가 섞이지 않게 WebView2 프로필도 따로 쓴다.
        /// </summary>
        public const string MockEnvVar = "FILECRYPT_NETCUS_MOCK";

        public static int MockPort
        {
            get
            {
                int p;
                return int.TryParse(Environment.GetEnvironmentVariable(MockEnvVar), out p) && p > 0 && p < 65536 ? p : 0;
            }
        }

        public async Task InitAsync()
        {
            if (Env != null) return;
            int mock = MockPort;
            string udf = System.IO.Path.Combine(AppConfig.Dir, mock > 0 ? "wv2-mock" : "wv2");
            System.IO.Directory.CreateDirectory(udf);
            CoreWebView2EnvironmentOptions opt = null;
            if (mock > 0)
            {
                opt = new CoreWebView2EnvironmentOptions(
                    "--host-resolver-rules=\"MAP www.netcus.com 127.0.0.1:" + mock + "\" --ignore-certificate-errors");
                DebugLog.Write("목업", "www.netcus.com -> 127.0.0.1:" + mock);
            }
            Env = await CoreWebView2Environment.CreateAsync(null, udf, opt);
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
            // 메시지에 따옴표·역슬래시가 있어도 깨지지 않도록 직렬화기로 만든다.
            catch (Exception ex) { json = JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }

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
        /// <summary>
        /// 다음 작업 완료(__netcusResult)를 기다릴 준비. 작업을 시작하기 '전에' 부른다.
        /// 회신이 끝내 안 오는 경로가 생겨도 창이 영원히 '진행 중' 으로 남지 않도록 timeout 이 지나면
        /// (false, "시간 초과") 로 끝난다. 앞선 대기가 남아 있으면 취소해 결과가 엇갈리지 않게 한다.
        /// </summary>
        public Task<KeyValuePair<bool, string>> ExpectResult(TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<KeyValuePair<bool, string>>();
            var prev = System.Threading.Interlocked.Exchange(ref _result, tcs);
            if (prev != null) prev.TrySetCanceled();
            _ = Task.Delay(timeout).ContinueWith(_ => tcs.TrySetResult(new KeyValuePair<bool, string>(
                    false, string.Format("시간 초과 — {0:N0}초 동안 응답이 없었습니다.", timeout.TotalSeconds))),
                TaskScheduler.Default);
            return tcs.Task;
        }

        /// <summary>reqId 로 오는 회신(Reply)을 기다릴 준비. timeout 이 지나면 TimeoutException.</summary>
        public Task<string> ExpectReply(string reqId, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<string>();
            lock (_waiters) { _waiters[reqId] = tcs; }
            _ = Task.Delay(timeout).ContinueWith(_ =>
            {
                lock (_waiters) { _waiters.Remove(reqId); }
                tcs.TrySetException(new TimeoutException(string.Format(
                    "사이트에서 {0:N0}초 동안 응답이 없었습니다. 잠시 뒤 다시 시도하세요.", timeout.TotalSeconds)));
            }, TaskScheduler.Default);
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
