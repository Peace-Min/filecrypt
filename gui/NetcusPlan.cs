using System;
using System.Collections.Generic;
using System.Text;

namespace FileCrypt
{
    /// <summary>
    /// 사내 보고 시스템에 올리고/받아오는 "계획"과 "조립" 부분.
    /// 브라우저(WebView2)나 창을 하나도 쓰지 않는다 — 그래야 자동 테스트가 닿는다.
    /// 실제 로그인·입력은 NetcusClient 가 맡고, 여기서는 무엇을 어느 날짜에 넣을지만 정한다.
    ///
    /// 일간보고는 날짜당 입력칸이 하나다. 그래서 조각 1개 = 날짜 1개로 간다.
    /// </summary>
    public static class NetcusPlan
    {
        /// <summary>붙여넣는 쪽이 지연 없이 받아주는 한도(실측). 올릴 때는 이 값으로 나눈다.</summary>
        public const int DefaultLimit = 200000;

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
            string whole = FileCryptCore.ToArmor(container, FileCryptCore.DefaultWidth);

            if (whole.Length <= limit)
            {
                // 한도 안 — 나누지 않는다. 날짜 하나면 끝난다.
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
