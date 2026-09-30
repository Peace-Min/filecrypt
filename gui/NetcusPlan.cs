using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace FileCrypt
{
    /// <summary>
    /// 사내 보고 시스템에 올리고/받아오는 "계획"과 "조립" 부분.
    /// 브라우저(WebView2)나 창을 하나도 쓰지 않는다 — 그래야 자동 테스트가 닿는다.
    /// 실제 로그인·입력은 NetcusService(NetcusGateway 경유)가 맡고, 여기서는 무엇을 어느 날짜에 넣을지만 정한다.
    /// 사이트 회신 해석처럼 사이트 없이 확인할 수 있는 판단도 여기 둔다.
    ///
    /// 일간보고는 날짜당 입력칸이 하나다. 그래서 조각 1개 = 날짜 1개로 간다.
    /// </summary>
    public static class NetcusPlan
    {
        /// <summary>붙여넣는 쪽이 지연 없이 받아주는 한도(실측). 올릴 때는 이 값으로 나눈다.</summary>
        public const int DefaultLimit = 200000;

        /// <summary>NetcusService 의 범위 읽기가 한 번에 받는 최대 일수. 넘으면 "range" 로 거절한다.</summary>
        public const int MaxReadDays = 31;

        /// <summary>올릴 내용 한 건 — 어느 날짜에 어떤 텍스트를 넣을지.</summary>
        public sealed class Slot
        {
            public DateTime Date { get; set; }
            public string Text { get; set; }
            /// <summary>조각 번호(1부터). 나누지 않았으면 1.</summary>
            public int Index { get; set; }
            public int Total { get; set; }
            /// <summary>올리기 직전에 읽어 온 그 날짜의 기존 내용. 비어 있으면 빈 칸.</summary>
            public string Existing { get; set; }

            public bool ExistingHasContent
            {
                get { return !string.IsNullOrWhiteSpace(Existing); }
            }
        }

        /// <summary>
        /// 파일들을 묶어 한도에 맞춰 나누고, 시작 날짜부터 연속된 날짜에 하나씩 배치한다.
        /// 한 덩어리로 들어가면 조각내지 않고 그 날짜 하나만 쓴다.
        /// </summary>
        public static List<Slot> Build(byte[] container, DateTime startDate, int limit)
        {
            if (container == null) throw new ArgumentNullException("container");
            if (limit <= 0) limit = DefaultLimit;

            var slots = new List<Slot>();

            // 한도 안 — 나누지 않는다. 날짜 하나면 끝난다.
            // 길이는 계산으로 먼저 본다. 예전에는 통짜 텍스트를 만들어 재 보고, 넘으면 버리고 조각을 또 만들었다.
            if (FileCryptCore.ArmorLength(container.Length, FileCryptCore.DefaultWidth) <= limit)
            {
                string whole = FileCryptCore.ToArmor(container, FileCryptCore.DefaultWidth);
                slots.Add(new Slot { Date = startDate.Date, Text = whole, Index = 1, Total = 1 });
                return slots;
            }

            var parts = FileCryptCore.ToArmorParts(container, limit, FileCryptCore.DefaultWidth);
            for (int i = 0; i < parts.Count; i++)
            {
                slots.Add(new Slot
                {
                    Date  = startDate.Date.AddDays(i),
                    Text  = parts[i],
                    Index = i + 1,
                    Total = parts.Count
                });
            }
            return slots;
        }

        /// <summary>
        /// 날짜만 옮긴다. 시작 날짜를 바꿀 때 조각을 다시 만들 필요가 없다(내용은 날짜와 무관).
        /// </summary>
        public static void Redate(IList<Slot> slots, DateTime startDate)
        {
            if (slots == null) return;
            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].Date = startDate.Date.AddDays(i);
                slots[i].Existing = null;
            }
        }

        /// <summary>
        /// 사이트에 있는 내용이 올리려는 내용과 같은가. 사이트가 화면에 그리며 줄바꿈·공백을 바꾸므로
        /// 공백류를 전부 빼고 비교한다(본문은 ASCII 라 그 밖의 글자는 바뀌지 않는다).
        /// 중간에 멈췄다가 다시 올릴 때 이미 올라간 날짜를 건너뛰고, 올린 뒤 제대로 들어갔는지 확인하는 데 쓴다.
        /// </summary>
        public static bool SameContent(string onSite, string expected)
        {
            if (onSite == null || expected == null) return false;
            return StripSpace(onSite) == StripSpace(expected);
        }

        private static string StripSpace(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c) && c != '​' && c != '﻿') sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// from~to 를 maxDays 일 이하 구간들로 나눈다(양 끝 포함). 범위 읽기 한도(31일)를 넘는
        /// 가져오기를 여러 번에 나눠 읽기 위한 것이다. from &gt; to 면 둘을 바꾼다.
        /// </summary>
        public static List<KeyValuePair<DateTime, DateTime>> SplitRange(DateTime from, DateTime to, int maxDays)
        {
            if (maxDays < 1) maxDays = 1;
            DateTime a = from.Date, b = to.Date;
            if (a > b) { var t = a; a = b; b = t; }
            var list = new List<KeyValuePair<DateTime, DateTime>>();
            while (a <= b)
            {
                DateTime end = a.AddDays(maxDays - 1);
                if (end > b) end = b;
                list.Add(new KeyValuePair<DateTime, DateTime>(a, end));
                a = end.AddDays(1);
            }
            return list;
        }

        /// <summary>
        /// 범위 읽기 회신(JSON)을 날짜 -> 내용으로 바꾼다. ok=false 인 날짜(페이지 접근 실패)는 빼서
        /// 빈 칸과 구분한다. 회신 전체가 ok=false 면 사람이 읽을 이유로 예외를 던진다.
        /// </summary>
        public static Dictionary<DateTime, string> ParseDaysReply(string json)
        {
            var result = new Dictionary<DateTime, string>();
            foreach (var kv in ParseDayInfos(json)) result[kv.Key] = kv.Value.Content;
            return result;
        }

        /// <summary>범위 읽기로 받은 날짜 하나. 근태·초과시간·주간 합계는 주 52시간 검사에 쓴다.</summary>
        public sealed class DayInfo
        {
            public string Content { get; set; }
            /// <summary>근태 코드. "" = 아직 선택 안 함(기록하면 정근=1 이 된다)</summary>
            public string Status { get; set; }
            public int Overtime { get; set; }
            /// <summary>
            /// 그 주에서 이 날을 뺀 나머지 날들의 근무시간 합계. 사이트가 페이지마다 계산해 Bmodify() 에
            /// "totalWorkingTime = todayWorkingTime + N" 으로 박아 두는 N 이다. 없으면 -1.
            /// (실측 2024-08-14~16: 휴가 0h 인 수요일 35, 정근 8h 인 목·금 27 -> 주 전체 35h 로 맞아떨어짐)
            /// </summary>
            public int WeekOthers { get; set; }

            public DayInfo() { Content = ""; Status = ""; WeekOthers = -1; }
        }

        /// <summary>범위 읽기 회신을 날짜 -> DayInfo 로. 규칙은 ParseDaysReply 와 같다.</summary>
        public static Dictionary<DateTime, DayInfo> ParseDayInfos(string json)
        {
            var result = new Dictionary<DateTime, DayInfo>();
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("읽기 회신이 비었습니다.");
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                JsonElement okEl;
                if (root.TryGetProperty("ok", out okEl) && okEl.ValueKind == JsonValueKind.False)
                {
                    JsonElement errEl;
                    string err = root.TryGetProperty("error", out errEl) && errEl.ValueKind == JsonValueKind.String
                               ? (errEl.GetString() ?? "") : "";
                    if (err == "login")    throw new InvalidOperationException("로그인에 실패했습니다. [계정 정보] 에서 확인하세요.");
                    if (err == "session")  throw new InvalidOperationException("읽는 도중 로그인이 풀렸습니다. 다시 시도하세요.");
                    if (err == "no-creds") throw new InvalidOperationException("저장된 자격증명이 없습니다. [계정 정보] 에서 [로그인 확인] 을 한 번 눌러 주세요.");
                    if (err == "busy")     throw new InvalidOperationException("다른 근태관리 작업이 진행 중입니다. 끝난 뒤 다시 시도하세요.");
                    throw new InvalidOperationException("읽기에 실패했습니다" + (err.Length > 0 ? " (" + err + ")" : "") + ".");
                }

                JsonElement days;
                if (!root.TryGetProperty("days", out days) || days.ValueKind != JsonValueKind.Array) return result;

                foreach (var e in days.EnumerateArray())
                {
                    JsonElement dEl, v, okDay;
                    if (!e.TryGetProperty("date", out dEl) || dEl.ValueKind != JsonValueKind.String) continue;
                    DateTime dt;
                    if (!DateTime.TryParse(dEl.GetString(), out dt)) continue;
                    // ok=false 는 '그 날 페이지에 접근하지 못함' 이라 빈 칸과 구분해 버린다.
                    if (e.TryGetProperty("ok", out okDay) && okDay.ValueKind == JsonValueKind.False) continue;
                    var info = new DayInfo();
                    if (e.TryGetProperty("content", out v) && v.ValueKind == JsonValueKind.String) info.Content = v.GetString() ?? "";
                    if (e.TryGetProperty("status", out v) && v.ValueKind == JsonValueKind.String) info.Status = v.GetString() ?? "";
                    int n;
                    if (e.TryGetProperty("overtime", out v) && v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) info.Overtime = n;
                    if (e.TryGetProperty("weekOthers", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n)) info.WeekOthers = n;
                    result[dt.Date] = info;
                }
            }
            return result;
        }

        // ------------------------------------------------------------ 주 52시간
        /// <summary>근로기준법 주 최대 근로시간. 사이트의 Bmodify() 도 이 값을 넘으면 저장을 막는다.</summary>
        public const int WeeklyLimit = 52;

        /// <summary>근태가 비어 있는 날에 기록하면 붙는 근태(정근). 사이트가 근태 없이는 저장을 받지 않는다.</summary>
        public const string DefaultStatus = "1";

        /// <summary>
        /// 그 날의 근무시간. 사이트 getWorkingTime() 과 같다:
        /// 특근(3)·휴가(6)·병가(11) 0h, 반차(12) 4h, 그 밖의 근태 8h, 여기에 초과시간을 더한다. 근태 미선택은 0h.
        /// </summary>
        public static int Hours(string status, int overtime)
        {
            if (string.IsNullOrEmpty(status)) return 0;
            switch (status)
            {
                case "3": case "6": case "11": return 0 + overtime;
                case "12": return 4 + overtime;
                default: return 8 + overtime;
            }
        }

        /// <summary>그 날이 속한 주의 월요일. 주는 월~일.</summary>
        public static DateTime WeekStart(DateTime d)
        {
            int back = ((int)d.DayOfWeek + 6) % 7;   // 월=0 … 일=6
            return d.Date.AddDays(-back);
        }

        /// <summary>
        /// 시작 날짜부터 차례로, 조각을 넣어도 주 52시간을 넘지 않는 날짜를 count 개 고른다.
        ///
        /// 기록하면 근태가 비어 있던 날은 정근(8h)이 되고, 근태가 있던 날은 그대로다(초과시간도 그대로).
        /// 그 날 기록 후 시간 + 그 주 나머지 날 합계 &gt; 52 이면 그 날은 건너뛴다. 같은 주에 앞서 새로 채운 날은
        /// 나머지 합계에 더해 간다. (월~금 정근 40h + 토 48h 까지 되고 일요일은 56h 라 건너뛴다.)
        ///
        /// site 에 없는 날짜에 닿으면(더 읽어야 함) null. skipped 에는 건너뛴 날과 이유.
        /// 주간 합계(WeekOthers)가 없는 페이지면 site 에 있는 같은 주 다른 날들의 시간을 더해 쓴다.
        /// </summary>
        public static List<DateTime> PickDates(DateTime start, int count, IDictionary<DateTime, DayInfo> site,
                                               List<string> skipped)
        {
            var picked = new List<DateTime>();
            var added = new Dictionary<DateTime, int>();   // 주(월요일) -> 이번에 새로 더한 시간
            for (DateTime d = start.Date; picked.Count < count; d = d.AddDays(1))
            {
                DayInfo info;
                if (!site.TryGetValue(d, out info)) return null;

                int now = Hours(info.Status, info.Overtime);
                int after = string.IsNullOrEmpty(info.Status) ? Hours(DefaultStatus, 0) : now;

                int others = info.WeekOthers;
                if (others < 0)
                {
                    others = 0;
                    DateTime ws = WeekStart(d);
                    for (int i = 0; i < 7; i++)
                    {
                        DayInfo o;
                        DateTime x = ws.AddDays(i);
                        if (x != d && site.TryGetValue(x, out o)) others += Hours(o.Status, o.Overtime);
                    }
                }
                int extra;
                added.TryGetValue(WeekStart(d), out extra);
                int total = after + others + extra;

                if (total > WeeklyLimit)
                {
                    if (skipped != null)
                        skipped.Add(string.Format("{0:yyyy-MM-dd}({1}): 기록하면 주 {2}시간 — 52시간 초과라 건너뜀",
                                                  d, "월화수목금토일"[((int)d.DayOfWeek + 6) % 7], total));
                    continue;
                }
                picked.Add(d);
                if (after != now) added[WeekStart(d)] = extra + (after - now);
            }
            return picked;
        }

        /// <summary>PickDates 가 볼 수 있도록 읽어야 할 범위: 시작 주의 월요일 ~ 넉넉한 끝 주의 일요일.</summary>
        public static KeyValuePair<DateTime, DateTime> ReadWindow(DateTime start, int days)
        {
            DateTime from = WeekStart(start);
            DateTime to = WeekStart(start.Date.AddDays(Math.Max(1, days) - 1)).AddDays(6);
            return new KeyValuePair<DateTime, DateTime>(from, to);
        }
        /// <summary>
        /// 제출 실패가 '로그인 자체가 막힌' 경우인가. 그때는 더 두드리면 더 막히므로 멈춰야 한다.
        /// NetcusService 의 실패 문구에 기대는 판정이라, 문구가 바뀌면 여기만 고치면 된다.
        /// </summary>
        public static bool LooksAuthBlocked(bool ok, string message)
        {
            if (ok || message == null) return false;
            return message.Contains("로그인") || message.Contains("자격") || message.Contains("세션")
                || message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>계획을 사람이 읽을 한 줄로. 올리기 전에 무엇을 할지 보여 준다.</summary>
        public static string Describe(IList<Slot> slots)
        {
            if (slots == null || slots.Count == 0) return "올릴 것이 없습니다.";
            if (slots.Count == 1)
                return string.Format("{0:yyyy-MM-dd} 하루에 {1:N0}자", slots[0].Date, slots[0].Text.Length);

            int longest = 0;
            foreach (var s in slots) if (s.Text.Length > longest) longest = s.Text.Length;
            return string.Format("{0}조각 → {1:yyyy-MM-dd} ~ {2:yyyy-MM-dd} ({3}일, 각 최대 {4:N0}자)",
                                 slots.Count, slots[0].Date, slots[slots.Count - 1].Date, slots.Count, longest);
        }

        /// <summary>기존 내용이 이미 적혀 있어 덮어쓰게 되는 날짜들.</summary>
        public static List<Slot> Occupied(IList<Slot> slots)
        {
            var list = new List<Slot>();
            if (slots == null) return list;
            foreach (var s in slots) if (s.ExistingHasContent) list.Add(s);
            return list;
        }

        /// <summary>덮어쓰기 경고 문장. 어느 날짜가 막혀 있는지 짚어 준다.</summary>
        public static string DescribeOccupied(IList<Slot> occupied)
        {
            if (occupied == null || occupied.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendFormat("이미 내용이 적혀 있는 날짜가 {0}개 있습니다:", occupied.Count);
            for (int i = 0; i < occupied.Count && i < 10; i++)
            {
                string head = Preview(occupied[i].Existing, 24);
                sb.AppendFormat("\r\n  · {0:yyyy-MM-dd}  \"{1}\"", occupied[i].Date, head);
            }
            if (occupied.Count > 10) sb.Append("\r\n  · …");
            return sb.ToString();
        }

        /// <summary>긴 텍스트의 앞부분만 한 줄로. 줄바꿈은 공백으로 바꾼다.</summary>
        public static string Preview(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            if (max < 1) max = 1;
            return t.Length <= max ? t : t.Substring(0, max) + "…";
        }

        /// <summary>
        /// 날짜별로 읽어 온 내용을 하나로 잇는다. 조각 순서가 섞여 있어도 상관없다 —
        /// 조각 표식에 번호와 묶음 식별자가 들어 있어 FileCryptCore 가 알아서 맞춘다.
        /// </summary>
        public static string Assemble(IEnumerable<string> dayContents)
        {
            var sb = new StringBuilder();
            if (dayContents == null) return "";
            foreach (var c in dayContents)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                sb.Append(c);
                if (!c.EndsWith("\n")) sb.Append("\r\n");
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>시작 날짜부터 count 일간의 날짜 목록. 받아올 범위를 정할 때 쓴다.</summary>
        public static List<DateTime> DateRange(DateTime start, int count)
        {
            var list = new List<DateTime>();
            if (count < 1) count = 1;
            for (int i = 0; i < count; i++) list.Add(start.Date.AddDays(i));
            return list;
        }

        /// <summary>
        /// 받아온 날짜별 내용에서 무엇이 모였는지 살펴본 결과.
        /// 블록이 완성됐으면 Ready, 조각이 모자라면 무엇이 없는지 알려 준다.
        /// </summary>
        public sealed class Gathered
        {
            public string Text { get; set; }
            public int BlockCount { get; set; }
            public int DaysWithContent { get; set; }
            public List<FileCryptCore.PartGroup> Pending { get; set; }
            public bool Ready { get { return BlockCount > 0; } }

            public Gathered() { Pending = new List<FileCryptCore.PartGroup>(); }
        }

        /// <summary>날짜별 내용을 모아 복원 가능한 상태인지 판단한다.</summary>
        public static Gathered Gather(IEnumerable<string> dayContents)
        {
            var g = new Gathered();
            foreach (var c in dayContents ?? new string[0])
                if (!string.IsNullOrWhiteSpace(c)) g.DaysWithContent++;

            g.Text = Assemble(dayContents);
            g.BlockCount = FileCryptCore.ExtractBlocks(g.Text).Count;
            if (g.BlockCount == 0) g.Pending = FileCryptCore.InspectParts(g.Text);
            return g;
        }
    }
}
