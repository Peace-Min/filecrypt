using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileCrypt
{
    /// <summary>
    /// 근태관리 올리기·가져오기 절차. 창(NetcusWindow)에서 떼어 낸 것 - FileCryptJobs 와 같은 이유다:
    /// 창 안에 있으면 자동 테스트가 닿지 않는다. 여기에는 WPF 타입이 없다.
    /// 사람에게 물어야 하는 곳(덮어쓰기 확인)만 ConfirmOverwrite 로 받는다.
    ///
    /// 창과 자가 테스트(--netcus-selftest, 목업 사이트)가 이 클래스를 똑같이 부른다.
    /// </summary>
    internal sealed class NetcusJobs
    {
        private readonly NetcusGateway _gw;
        private readonly Action<string> _log;

        /// <summary>이미 내용이 있는 날짜를 덮어쓸지. 인자는 사람에게 보여 줄 문장. null 이면 덮어쓰지 않는다.</summary>
        public Func<string, bool> ConfirmOverwrite { get; set; }

        /// <summary>날짜와 날짜 사이 쉬는 시간(ms). 기본은 설정값.</summary>
        public int PaceMs { get; set; }

        public NetcusJobs(NetcusGateway gw, Action<string> log)
        {
            _gw = gw;
            _log = log ?? (s => { });
            PaceMs = AppConfig.NetcusPaceMs;
        }

        private void Log(string s) { _log(s); }

        public static string BackupDir { get { return Path.Combine(AppConfig.Dir, "backup"); } }

        // ------------------------------------------------------------ 결과
        public enum UploadOutcome { Done, Cancelled, PageMissing, Aborted }

        public sealed class UploadResult
        {
            public UploadOutcome Outcome;
            public int Total;              // 계획한 날짜 수
            public int Skipped;            // 이미 같은 조각이 있어 건너뜀
            public int Written;            // 이번에 기록함
            public int Verified;           // 다시 읽어 같음을 확인한 날짜
            public bool RoundTrip;         // 다시 모아 원본 컨테이너와 바이트 단위로 같음
            public DateTime? FailedAt;
            public string FailedWhy;
            public string BackupFile;
            public DateTime Start;
            public DateTime End;
            /// <summary>가져올 때 쓸 일수 = 첫 날짜 ~ 마지막 날짜 (주 52시간 때문에 건너뛴 날 포함)</summary>
            public int Span { get { return (int)(End - Start).TotalDays + 1; } }
            /// <summary>주 52시간을 넘겨 건너뛴 날과 이유</summary>
            public List<string> Skipped52 = new List<string>();
        }

        public sealed class DownloadResult
        {
            public bool Ready;             // 블록을 하나 이상 모았는가
            public string Why;             // 못 모았을 때 이유
            public FileCryptJobs.UnpackResult Unpacked;
            public int ClearTargets;       // 지우려던 날짜 수
            public int Cleared;            // 비워진 것을 확인한 날짜 수
            public bool ClearAborted;      // 지우다 멈췄는가(로그인 막힘 등)
        }

        // ------------------------------------------------------------ 공통 절차
        private sealed class SubmitOutcome
        {
            public readonly List<DateTime> Submitted = new List<DateTime>();
            public bool Aborted;
            public DateTime? FailedAt;
            public string FailedWhy;
        }

        /// <summary>
        /// 날짜마다 제출한다(text 가 null 이면 비우기). 제출이 거절되거나 오류가 나면 그 자리에서 멈춘다 —
        /// 올리기는 실패한 채로 계속 가면 조각이 빠지고, 비우기는 인증이 막힌 상태에서 더 두드리면 더 막힌다.
        /// </summary>
        private async Task<SubmitOutcome> SubmitAllAsync(IList<KeyValuePair<DateTime, string>> jobs)
        {
            var o = new SubmitOutcome();
            for (int i = 0; i < jobs.Count; i++)
            {
                DateTime d = jobs[i].Key;
                string text = jobs[i].Value;
                if (i > 0 && PaceMs > 0) await Task.Delay(PaceMs);
                try
                {
                    // 근태는 건드리지 않는다(status="" 규약). 저장 후 되읽기 검증도 NetcusService 가 한다.
                    var r = text != null ? await _gw.SubmitDayAsync(d, text, 0) : await _gw.ClearDaySubmitAsync(d);
                    Log(string.Format("  {0:yyyy-MM-dd} ({1}/{2}) {3} — {4}", d, i + 1, jobs.Count,
                        r.Key ? (text != null ? "기록함" : "제출함") : "실패", r.Value));
                    if (!r.Key) { o.Aborted = true; o.FailedAt = d; o.FailedWhy = r.Value; break; }
                    o.Submitted.Add(d);
                }
                catch (Exception ex)
                {
                    Log(string.Format("  {0:yyyy-MM-dd} 오류: {1}", d, ex.Message));
                    o.Aborted = true; o.FailedAt = d; o.FailedWhy = ex.Message;
                    break;
                }
            }
            return o;
        }

        /// <summary>dates 를 범위 읽기 한 번으로 다시 읽어 good(날짜, 사이트 내용) 인 날짜 수를 센다.</summary>
        private async Task<KeyValuePair<int, Dictionary<DateTime, string>>> VerifyAsync(
            IList<DateTime> dates, Func<DateTime, string, bool> good, string okWord)
        {
            int n = 0;
            var after = new Dictionary<DateTime, string>();
            if (dates.Count == 0) return new KeyValuePair<int, Dictionary<DateTime, string>>(0, after);
            try
            {
                after = await _gw.ReadDaysAsync(dates.Min(), dates.Max());
                foreach (var d in dates)
                {
                    string c;
                    bool have = after.TryGetValue(d, out c);
                    bool ok = have && good(d, c);
                    if (ok) n++;
                    Log(string.Format("  {0:yyyy-MM-dd} {1}", d,
                        ok ? okWord : (have ? string.Format("다름 ({0:N0}자 있음)", c.Length) : "확인 못 함")));
                }
            }
            catch (Exception ex) { Log("확인 실패: " + ex.Message); }
            return new KeyValuePair<int, Dictionary<DateTime, string>>(n, after);
        }

        // ------------------------------------------------------------ 올리기
        /// <summary>
        /// slots(날짜별 조각)를 기록한다. 먼저 전부 읽어 덮어쓸 날짜를 확인하고(물어봄 + 백업),
        /// 이미 같은 조각이 있는 날짜는 건너뛴다. 끝나면 다시 읽어 날짜마다 같은지, 모아서 원본과 같은지 본다.
        /// </summary>
        public async Task<UploadResult> UploadAsync(byte[] container, List<NetcusPlan.Slot> slots)
        {
            var res = new UploadResult { Total = slots.Count, Start = slots[0].Date, End = slots[slots.Count - 1].Date };
            Log(string.Format("{0}조각을 {1:yyyy-MM-dd} 부터 하루 한 조각씩 넣습니다.", slots.Count, slots[0].Date));

            // 먼저 읽어 본다 — 무엇을 덮어쓰게 되는지, 그리고 어느 날에 넣어도 주 52시간을 넘지 않는지.
            // 근태가 비어 있던 날은 기록하면 정근(8h)이 된다. 사이트의 Bmodify() 는 주 합계가 52시간을
            // 넘으면 저장을 막는데, 우리는 Bmodify 를 거치지 않고 제출하므로 같은 검사를 여기서 한다.
            // 주 합계를 보려고 시작 주의 월요일부터 끝 주의 일요일까지 읽고, 건너뛴 날이 많으면 더 읽는다.
            Log("대상 날짜의 기존 내용과 근태를 확인하는 중…");
            DateTime start = slots[0].Date;
            var site = new Dictionary<DateTime, NetcusPlan.DayInfo>();
            List<DateTime> dates = null;
            int span = slots.Count;
            while (dates == null)
            {
                var w = NetcusPlan.ReadWindow(start, span);
                if ((w.Value - start).TotalDays > 400)
                    throw new InvalidOperationException("주 52시간 안에서 조각을 넣을 날짜를 찾지 못했습니다 (1년 넘게 찾아봄).");
                DateTime from = w.Key;
                while (from <= w.Value && site.ContainsKey(from)) from = from.AddDays(1);
                if (from <= w.Value)
                    foreach (var kv in await _gw.ReadDayInfosAsync(from, w.Value)) site[kv.Key] = kv.Value;
                for (DateTime d = start; d <= w.Value; d = d.AddDays(1))
                    if (!site.ContainsKey(d))
                    {
                        Log(string.Format("  {0:yyyy-MM-dd}: 페이지를 열지 못했습니다 — 중단합니다.", d));
                        res.Outcome = UploadOutcome.PageMissing; res.FailedAt = d;
                        return res;
                    }
                res.Skipped52.Clear();
                dates = NetcusPlan.PickDates(start, slots.Count, site, res.Skipped52);
                span = span * 2 + 7;
            }
            foreach (var why in res.Skipped52) Log("  " + why);
            for (int i = 0; i < slots.Count; i++) slots[i].Date = dates[i];
            res.Start = dates[0]; res.End = dates[dates.Count - 1];
            if (res.Skipped52.Count > 0)
                Log(string.Format("주 52시간 때문에 {0}일을 건너뛰어 {1:yyyy-MM-dd} ~ {2:yyyy-MM-dd} ({3}일)에 넣습니다.",
                                  res.Skipped52.Count, res.Start, res.End, res.Span));

            var todo = new List<NetcusPlan.Slot>();
            var occupied = new List<NetcusPlan.Slot>();
            foreach (var s in slots)
            {
                string cur = site[s.Date].Content;
                s.Existing = cur;
                // 지난번에 중간에 멈췄다면 이미 올라간 조각이 있다. 그 날짜는 덮어쓰기 경고도, 다시 올리기도 하지 않는다.
                if (NetcusPlan.SameContent(cur, s.Text))
                {
                    Log(string.Format("  {0:yyyy-MM-dd}: 이미 같은 조각이 올라가 있음 — 건너뜀", s.Date));
                    res.Skipped++;
                    continue;
                }
                todo.Add(s);
                if (s.ExistingHasContent) occupied.Add(s);
                Log(string.Format("  {0:yyyy-MM-dd}: {1}", s.Date,
                    s.ExistingHasContent ? "내용 있음 (" + NetcusPlan.Preview(cur, 30) + ")" : "빈 칸"));
            }

            if (occupied.Count > 0)
            {
                string msg = NetcusPlan.DescribeOccupied(occupied)
                           + "\r\n\r\n덮어쓰면 그 날짜의 기존 보고 내용이 사라집니다."
                           + "\r\n덮어쓰기 전 내용은 아래에 백업해 둡니다:"
                           + "\r\n" + BackupDir
                           + "\r\n\r\n계속할까요?";
                var ask = ConfirmOverwrite;
                if (ask == null || !ask(msg))
                {
                    Log("사용자가 취소했습니다. 아무것도 올리지 않았습니다.");
                    res.Outcome = UploadOutcome.Cancelled;
                    return res;
                }
                res.BackupFile = BackupOccupied(occupied);
            }

            var outcome = await SubmitAllAsync(todo.Select(s => new KeyValuePair<DateTime, string>(s.Date, s.Text)).ToList());
            res.Written = outcome.Submitted.Count;
            if (outcome.Aborted)
            {
                Log("→ 중단합니다. 이미 올라간 날짜는 그대로 남아 있습니다. 다시 [올리기] 하면 남은 날짜만 올립니다.");
                res.Outcome = UploadOutcome.Aborted; res.FailedAt = outcome.FailedAt; res.FailedWhy = outcome.FailedWhy;
                return res;
            }

            // 확인: 저장 직후 검증(NetcusService)은 내용이 비어 있지 않은지만 본다 - 본문이 ASCII 라 한글 대조가
            // 빠지기 때문이다. 사이트가 글을 잘라 저장해도 성공으로 나올 수 있어, 전체 범위를 다시 읽어
            // 날짜마다 내용이 같은지, 그리고 모아서 원래 컨테이너로 되돌아오는지까지 본다.
            Log("올라간 내용을 다시 읽어 확인하는 중…");
            var check = await VerifyAsync(slots.Select(s => s.Date).ToList(),
                (d, c) => NetcusPlan.SameContent(c, slots.First(s => s.Date == d).Text), "일치");
            res.Verified = check.Key;
            try
            {
                var back = FileCryptCore.ExtractBlocks(NetcusPlan.Assemble(slots.Select(s =>
                {
                    string c; return check.Value.TryGetValue(s.Date, out c) ? c : "";
                })));
                res.RoundTrip = back.Count == 1 && back[0].SequenceEqual(container);
            }
            catch { }
            Log(res.RoundTrip ? "→ 다시 모아 원본과 바이트 단위로 같음을 확인했습니다."
                              : "→ 다시 모았을 때 원본과 같지 않습니다. 위에서 '다름' 인 날짜를 확인하세요.");
            Log(string.Format("완료: {0}일치 중 {1}일치 확인{2}. 받아올 때는 {3:yyyy-MM-dd} 부터 {4}일로 가져오세요.",
                res.Total, res.Verified, res.Skipped > 0 ? string.Format(" (이미 있던 {0}일치 건너뜀)", res.Skipped) : "", res.Start, res.Span));
            res.Outcome = UploadOutcome.Done;
            return res;
        }

        /// <summary>덮어쓰기 전 내용을 로컬에 남긴다. 사라지면 되돌릴 방법이 없다. 실패하면 null.</summary>
        private string BackupOccupied(IList<NetcusPlan.Slot> occupied)
        {
            try
            {
                string dir = BackupDir;
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, string.Format("일간보고 백업 {0:yyyyMMdd-HHmmss-fff}.txt", DateTime.Now));

                var sb = new StringBuilder();
                sb.AppendLine("FileCrypt 가 덮어쓰기 전에 저장해 둔 원래 보고 내용입니다.");
                sb.AppendLine();
                foreach (var s in occupied)
                {
                    sb.AppendFormat("===== {0:yyyy-MM-dd} =====", s.Date).AppendLine();
                    sb.AppendLine(s.Existing);
                    sb.AppendLine();
                }
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
                Log("기존 내용 백업: " + file);
                return file;
            }
            catch (Exception ex) { Log("백업 실패(계속 진행): " + ex.Message); return null; }
        }

        // ------------------------------------------------------------ 받아오기
        /// <summary>start 부터 days 일을 읽어 모아 outDir 에 되돌린다. clear 면 복원에 성공한 뒤 그 날짜들을 비운다.</summary>
        public async Task<DownloadResult> DownloadAsync(DateTime start, int days, string outDir, bool clear)
        {
            var res = new DownloadResult();
            var dates = NetcusPlan.DateRange(start, days);
            Log(string.Format("{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd} 읽는 중…", dates[0], dates[dates.Count - 1]));

            var map = await _gw.ReadDaysAsync(dates[0], dates[dates.Count - 1]);
            var contents = new List<string>();
            foreach (var d in dates)
            {
                string c;
                if (!map.TryGetValue(d, out c)) { Log(string.Format("  {0:yyyy-MM-dd}: 열지 못함 — 건너뜀", d)); continue; }
                contents.Add(c);
                Log(string.Format("  {0:yyyy-MM-dd}: {1:N0}자", d, c.Length));
            }

            var g = NetcusPlan.Gather(contents);
            if (!g.Ready)
            {
                res.Why = g.Pending.Count > 0
                    ? FileCryptJobs.DescribePending(g.Pending)
                    : "FileCrypt 블록을 찾지 못했습니다. 날짜 범위를 확인하세요.";
                Log("→ " + res.Why);
                return res;
            }
            res.Ready = true;

            res.Unpacked = await Task.Run(() => FileCryptJobs.Unpack(new List<string> { g.Text }, outDir));
            foreach (var err in res.Unpacked.Errors) Log("  ! " + err);
            Log(string.Format("완료: {0}개 복원 ({1:N0} B) → {2}", res.Unpacked.OkCount, res.Unpacked.TotalBytes, res.Unpacked.TargetDir));

            // 복원에 성공한 뒤에만 지운다. 실패했는데 지우면 되돌릴 방법이 없다.
            if (res.Unpacked.OkCount > 0 && clear) await ClearDays(dates, map, res);
            return res;
        }

        /// <summary>
        /// 가져온 날짜들의 보고 칸을 비운다. 복원이 성공한 뒤에만 부른다.
        /// 근태·초과시간은 건드리지 않는다(status="" 규약).
        /// </summary>
        private async Task ClearDays(List<DateTime> dates, Dictionary<DateTime, string> had, DownloadResult res)
        {
            var targets = new List<DateTime>();
            foreach (var d in dates)
            {
                string c;
                if (had.TryGetValue(d, out c) && !string.IsNullOrWhiteSpace(c)) targets.Add(d);
            }
            res.ClearTargets = targets.Count;
            if (targets.Count == 0) return;

            Log(string.Format("사이트에서 {0}일치 내용을 지우는 중…", targets.Count));
            // 제출만 먼저 몰아서 하고 확인은 범위 읽기 한 번으로 - 날짜마다 확인하면 로그인·페이지 이동이 두 배.
            var outcome = await SubmitAllAsync(targets.Select(d => new KeyValuePair<DateTime, string>(d, null)).ToList());
            res.ClearAborted = outcome.Aborted;
            if (outcome.Submitted.Count > 0)
            {
                Log("지워졌는지 확인하는 중…");
                res.Cleared = (await VerifyAsync(outcome.Submitted, (d, c) => string.IsNullOrWhiteSpace(c), "비움 확인")).Key;
            }
            Log("지우기 완료: " + res.Cleared + "/" + res.ClearTargets + (res.ClearAborted ? " (중단됨)" : ""));
        }
    }
}
