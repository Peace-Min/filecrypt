using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileCrypt
{
    /// <summary>
    /// FileCrypt 창들이 부르는 창구. 안에서는 검증된 NetcusService 가 일한다.
    ///
    /// 여기 있는 건 '부르는 방법' 뿐이고, 로그인·입력·저장검증 로직은 전부 NetcusService 것이다.
    /// 그래서 캘린더에서 고친 버그가 여기에도 그대로 반영된다(같은 파일을 쓰므로).
    /// NetcusService 에 FileCrypt 가 덧붙인 부분은 gui\NetcusService.FileCrypt.md 에 적어 둔다.
    /// </summary>
    internal sealed class NetcusGateway : IDisposable
    {
        private readonly NetcusHost _host;
        private readonly NetcusService _svc;

        /// <summary>날짜 하나 기록(페이지 이동 몇 번 + 저장 + 되읽기). 넉넉히 잡는다. 자가 테스트는 줄여서 쓴다.</summary>
        internal static TimeSpan SubmitTimeout = TimeSpan.FromMinutes(2);
        /// <summary>범위 읽기 = ReadBaseTimeout + 하루당 ReadPerDayTimeout.</summary>
        internal static TimeSpan ReadBaseTimeout = TimeSpan.FromSeconds(30);
        internal static TimeSpan ReadPerDayTimeout = TimeSpan.FromSeconds(15);

        public event Action<string> Progress;
        public event Action<string> Logged;

        public NetcusGateway()
        {
            _host = new NetcusHost(System.Windows.Application.Current != null
                ? System.Windows.Application.Current.Dispatcher
                : System.Windows.Threading.Dispatcher.CurrentDispatcher);

            _host.Progress += s => { DebugLog.Write("진행", s); var h = Progress; if (h != null) h(s); };
            _host.Logged   += s => { DebugLog.Write("netcus", s); var h = Logged; if (h != null) h(s); };

            _svc = new NetcusService(_host);

            // 날짜 수만큼 자동으로 도는 도구다. 창이 뜰 때마다 포커스를 가져가면
            // 그 동안 다른 일을 못 한다. 진행 상황은 창의 기록과 로그 파일로 본다.
            _svc.QuietWindows = true;
        }

        /// <summary>아이디·비밀번호가 실제로 통하는지 확인만 한다(저장·부수효과 없음).</summary>
        public async Task<bool> LoginVerifyAsync(string id, string pw)
        {
            await _host.InitAsync();
            DebugLog.Section("로그인 확인: " + id);
            bool ok = await _svc.LoginVerify(id, pw);
            DebugLog.Write("로그인", ok ? "성공" : "실패");
            if (!ok) return false;

            // 기록·읽기(SubmitDaily/WeekMerge)는 인자가 아니라 자기 자격증명 파일에서 읽는다.
            // 여기서 만들어 두지 않으면 로그인은 되는데 읽기가 no-creds 로 떨어진다.
            // SaveCredsForLogin 은 '방금 검증됐다' 는 전제로 만들어진 메서드라 창을 다시 띄우지 않는다.
            var saved = _svc.SaveCredsForLogin(id, pw);
            DebugLog.Write("자격증명", saved.ok ? "저장됨" : ("저장 실패: " + saved.msg));
            if (!saved.ok) throw new InvalidOperationException(saved.msg);
            return true;
        }

        /// <summary>
        /// 올리기·가져오기 전에 부른다. 저장된 계정이 NetcusService 의 자격증명 파일과 같고
        /// 그 파일이 '검증됨' 이면 확인 로그인을 건너뛴다 - 기록·읽기가 스스로 로그인(또는 세션 재사용)하고
        /// 실패하면 그쪽이 알려 준다. 예전에는 실행할 때마다 실제 로그인 POST 를 한 번 더 보내고
        /// 창을 새로 띄웠다(인증 차단 위험 + 수 초).
        /// 계정이 바뀌었거나 파일이 없으면 지금처럼 확인 로그인을 한다.
        /// </summary>
        /// <returns>로그인 가능 여부. skipped 는 확인을 건너뛰었는지.</returns>
        public async Task<KeyValuePair<bool, bool>> EnsureLoginAsync(string id, string pw)
        {
            if (CredsInSync(id, pw))
            {
                DebugLog.Write("로그인", "확인 생략 — 저장된 자격증명이 이미 검증돼 있음: " + id);
                return new KeyValuePair<bool, bool>(true, true);
            }
            bool ok = await LoginVerifyAsync(id, pw);
            return new KeyValuePair<bool, bool>(ok, false);
        }

        /// <summary>netcus.cred 가 이 아이디·비밀번호로, 검증된 상태로 저장돼 있는가.</summary>
        private static bool CredsInSync(string id, string pw)
        {
            try
            {
                string f = Path.Combine(AppConfig.Dir, "netcus.cred");
                if (!File.Exists(f)) return false;
                using (var doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8)))
                {
                    var root = doc.RootElement;
                    JsonElement e;
                    string fid = root.TryGetProperty("id", out e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    string fpw = root.TryGetProperty("pw", out e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    bool valid = root.TryGetProperty("valid", out e) && e.ValueKind == JsonValueKind.True;
                    if (!valid || !string.Equals(fid, id, StringComparison.Ordinal) || string.IsNullOrEmpty(fpw)) return false;
                    string plain = Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(fpw)));
                    return string.Equals(plain, pw, StringComparison.Ordinal);
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// NetcusService 의 자격증명 파일(netcus.cred)을 지운다. [잊기] 가 config.ini 만 지우고
        /// 이 파일을 남기면, 계정을 지운 뒤에도 기록·읽기가 그 자격으로 돌 수 있었다.
        /// </summary>
        public void ClearSavedCreds()
        {
            _svc.ClearCredsForLogout();
        }

        /// <summary>
        /// 그 날짜 보고 칸에 내용을 기록한다.
        /// 근태(status)는 빈 문자열로 넘겨 '건드리지 않음' 으로 둔다 — 사이트의 기존 값이 그대로 남는다.
        /// </summary>
        public async Task<KeyValuePair<bool, string>> SubmitDayAsync(DateTime d, string content, int overtime)
        {
            await _host.InitAsync();
            DebugLog.Section(string.Format("기록 {0:yyyy-MM-dd} ({1:N0}자)", d, (content ?? "").Length));
            var wait = _host.ExpectResult(SubmitTimeout);
            await _svc.SubmitDaily(d.Year, d.Month, d.Day, "", overtime, content, false, "");
            return await wait;
        }

        /// <summary>
        /// 그 날짜 보고 칸을 비우도록 제출만 한다. 확인은 하지 않는다.
        ///
        /// 확인을 날짜마다 따로 하면 하루당 로그인이 두 번이 된다. 사이트는 짧은 시간에
        /// 로그인이 몰리면 인증을 막는다(15일치에서 실제로 막혔다). 그래서 제출만 모아서 하고,
        /// 확인은 끝난 뒤 범위 읽기 한 번으로 처리한다 — 로그인 횟수가 절반 아래로 떨어진다.
        ///
        /// 돌려주는 값은 '사이트가 받아들였는가' 가 아니라 '제출이 끝났는가' 다.
        /// NetcusService 는 되읽은 내용이 비어 있으면 실패로 보는데, 비우기에서는 그게 정상이라
        /// 여기서는 그 판정을 쓰지 않는다. 진짜 판정은 호출측의 범위 읽기가 한다.
        /// </summary>
        public async Task<KeyValuePair<bool, string>> ClearDaySubmitAsync(DateTime d)
        {
            await _host.InitAsync();
            DebugLog.Section(string.Format("비우기 제출 {0:yyyy-MM-dd}", d));

            var wait = _host.ExpectResult(SubmitTimeout);
            await _svc.SubmitDaily(d.Year, d.Month, d.Day, "", 0, "", false, "");
            var r = await wait;
            DebugLog.Write("비우기", "제출 결과: " + r.Key + " / " + r.Value);

            // 로그인 자체가 막힌 경우는 계속 시도해 봐야 소용없고, 더 두드리면 더 막힌다.
            return new KeyValuePair<bool, string>(!NetcusPlan.LooksAuthBlocked(r.Key, r.Value), r.Value);
        }

        /// <summary>
        /// from~to 각 날짜의 보고 내용을 읽는다.
        /// NetcusService 의 주간범위 읽기를 그대로 쓴다 — 창 하나로 날짜를 훑어 한 번에 회신한다.
        /// 그 읽기는 한 번에 31일까지만 받으므로(넘으면 "range" 로 거절) 31일씩 나눠 차례로 읽고 합친다.
        /// 읽지 못한 날짜는 결과에 넣지 않는다(빈 칸과 구분하기 위해).
        /// </summary>
        public async Task<Dictionary<DateTime, string>> ReadDaysAsync(DateTime from, DateTime to)
        {
            await _host.InitAsync();

            var result = new Dictionary<DateTime, string>();
            foreach (var w in NetcusPlan.SplitRange(from, to, NetcusPlan.MaxReadDays))
            {
                DebugLog.Section(string.Format("읽기 {0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}", w.Key, w.Value));
                string reqId = "fc-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                int days = (int)(w.Value - w.Key).TotalDays + 1;
                // 날짜마다 페이지를 하나씩 연다. 30초 + 하루 15초면 느린 날에도 넉넉하다.
                var wait = _host.ExpectReply(reqId, ReadBaseTimeout + TimeSpan.FromTicks(ReadPerDayTimeout.Ticks * days));
                await _svc.WeekMerge(reqId, w.Key.ToString("yyyy-MM-dd"), w.Value.ToString("yyyy-MM-dd"));
                string json = await wait;
                DebugLog.Write("읽기", "회신 " + (json ?? "").Length + "자");

                foreach (var kv in NetcusPlan.ParseDaysReply(json)) result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>
        /// 숨겨 둔 전송 창을 닫는다. 전송 경로는 다음 날짜를 위해 창을 남겨 두므로,
        /// 닫지 않으면 화면 밖(-32000)에 WebView2 창이 앱을 끌 때까지 살아 있었다.
        /// </summary>
        public void Dispose()
        {
            _svc.CloseWindow();
        }
    }
}
