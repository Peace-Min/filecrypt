using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileCrypt
{
    /// <summary>
    /// FileCrypt 창들이 부르는 창구. 안에서는 검증된 NetcusService 가 일한다.
    ///
    /// 여기 있는 건 '부르는 방법' 뿐이고, 로그인·입력·저장검증 로직은 전부 NetcusService 것이다.
    /// 그래서 캘린더에서 고친 버그가 여기에도 그대로 반영된다(같은 파일을 쓰므로).
    /// </summary>
    internal sealed class NetcusGateway : IDisposable
    {
        private readonly NetcusHost _host;
        private readonly NetcusService _svc;

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
        }

        public Task InitAsync() { return _host.InitAsync(); }

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
        /// 그 날짜 보고 칸에 내용을 기록한다.
        /// 근태(status)는 빈 문자열로 넘겨 '건드리지 않음' 으로 둔다 — 사이트의 기존 값이 그대로 남는다.
        /// </summary>
        public async Task<KeyValuePair<bool, string>> SubmitDayAsync(DateTime d, string content, int overtime)
        {
            await _host.InitAsync();
            DebugLog.Section(string.Format("기록 {0:yyyy-MM-dd} ({1:N0}자)", d, (content ?? "").Length));
            var wait = _host.ExpectResult();
            await _svc.SubmitDaily(d.Year, d.Month, d.Day, "", overtime, content, false, "");
            return await wait;
        }

        /// <summary>
        /// 그 날짜 보고 칸을 비운다.
        ///
        /// NetcusService 의 저장 검증은 '되읽었더니 비어 있다' 를 실패로 본다 — 보고를 쓰는 게
        /// 본래 목적이라 맞는 판정이다. 하지만 비우기에서는 빈 칸이 바로 성공이다.
        /// 그래서 메시지를 해석하지 않고, 실제로 비었는지 다시 읽어서 판정한다.
        /// (NetcusService 는 고치지 않는다.)
        /// </summary>
        public async Task<KeyValuePair<bool, string>> ClearDayAsync(DateTime d)
        {
            await _host.InitAsync();
            DebugLog.Section(string.Format("비우기 {0:yyyy-MM-dd}", d));

            var wait = _host.ExpectResult();
            await _svc.SubmitDaily(d.Year, d.Month, d.Day, "", 0, "", false, "");
            var submitted = await wait;
            DebugLog.Write("비우기", "제출 결과: " + submitted.Key + " / " + submitted.Value);

            string now = await ReadDayAsync(d);
            bool empty = (now != null) && string.IsNullOrWhiteSpace(now);
            DebugLog.Write("비우기", empty ? "확인: 비었음" : ("확인 실패: " + (now == null ? "읽지 못함" : now.Length + "자 남음")));

            return new KeyValuePair<bool, string>(empty,
                empty ? "비움 확인" : (now == null ? "비운 뒤 확인하지 못함" : now.Length.ToString("N0") + "자가 남아 있음"));
        }

        /// <summary>날짜 하나의 보고 내용을 읽는다. 없으면 빈 문자열, 접근 실패면 null.</summary>
        public async Task<string> ReadDayAsync(DateTime d)
        {
            var map = await ReadDaysAsync(d, d);
            string v;
            return map.TryGetValue(d.Date, out v) ? v : null;
        }

        /// <summary>
        /// from~to 각 날짜의 보고 내용을 읽는다.
        /// NetcusService 의 주간범위 읽기를 그대로 쓴다 — 창 하나로 날짜를 훑어 한 번에 회신한다.
        /// 읽지 못한 날짜는 결과에 넣지 않는다(빈 칸과 구분하기 위해).
        /// </summary>
        public async Task<Dictionary<DateTime, string>> ReadDaysAsync(DateTime from, DateTime to)
        {
            await _host.InitAsync();

            DebugLog.Section(string.Format("읽기 {0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}", from, to));
            string reqId = "fc-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var wait = _host.ExpectReply(reqId);
            await _svc.WeekMerge(reqId, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
            string json = await wait;
            DebugLog.Write("읽기", "회신 " + (json ?? "").Length + "자");

            var result = new Dictionary<DateTime, string>();
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                JsonElement okEl;
                if (root.TryGetProperty("ok", out okEl) && okEl.ValueKind == JsonValueKind.False)
                {
                    JsonElement errEl;
                    string err = root.TryGetProperty("error", out errEl) ? (errEl.GetString() ?? "") : "";
                    if (err == "login")   throw new InvalidOperationException("로그인에 실패했습니다. [계정 정보] 에서 확인하세요.");
                    if (err == "session") throw new InvalidOperationException("읽는 도중 로그인이 풀렸습니다. 다시 시도하세요.");
                    throw new InvalidOperationException("읽기에 실패했습니다" + (err.Length > 0 ? " (" + err + ")" : "") + ".");
                }

                JsonElement days;
                if (!root.TryGetProperty("days", out days) || days.ValueKind != JsonValueKind.Array) return result;

                foreach (var e in days.EnumerateArray())
                {
                    JsonElement dEl, cEl, okDay;
                    if (!e.TryGetProperty("date", out dEl)) continue;
                    DateTime dt;
                    if (!DateTime.TryParse(dEl.GetString(), out dt)) continue;
                    // ok=false 는 '그 날 페이지에 접근하지 못함' 이라 빈 칸과 구분해 버린다.
                    if (e.TryGetProperty("ok", out okDay) && okDay.ValueKind == JsonValueKind.False) continue;
                    string content = e.TryGetProperty("content", out cEl) ? (cEl.GetString() ?? "") : "";
                    result[dt.Date] = content;
                }
            }
            return result;
        }

        public void Dispose() { }
    }
}
