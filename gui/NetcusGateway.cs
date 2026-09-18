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

            _host.Progress += s => { var h = Progress; if (h != null) h(s); };
            _host.Logged   += s => { var h = Logged;   if (h != null) h(s); };

            _svc = new NetcusService(_host);
        }

        public Task InitAsync() { return _host.InitAsync(); }

        /// <summary>아이디·비밀번호가 실제로 통하는지 확인만 한다(저장·부수효과 없음).</summary>
        public async Task<bool> LoginVerifyAsync(string id, string pw)
        {
            await _host.InitAsync();
            return await _svc.LoginVerify(id, pw);
        }

        /// <summary>
        /// 그 날짜 보고 칸에 내용을 기록한다.
        /// 근태(status)는 빈 문자열로 넘겨 '건드리지 않음' 으로 둔다 — 사이트의 기존 값이 그대로 남는다.
        /// </summary>
        public async Task<KeyValuePair<bool, string>> SubmitDayAsync(DateTime d, string content, int overtime)
        {
            await _host.InitAsync();
            var wait = _host.ExpectResult();
            await _svc.SubmitDaily(d.Year, d.Month, d.Day, "", overtime, content, false, "");
            return await wait;
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

            string reqId = "fc-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var wait = _host.ExpectReply(reqId);
            await _svc.WeekMerge(reqId, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
            string json = await wait;

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
