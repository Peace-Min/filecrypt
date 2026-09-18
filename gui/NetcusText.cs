using System;
using System.Text;
using System.Text.Json;

namespace FileCrypt
{
    // netcus 크롤/전송에서 쓰는 DOM/WebView 무의존 '순수 정적' 요소 모음(행위보존 추출 Phase1).
    // 날짜/기간 파싱 · JS 스니펫 생성 · 추출 스크립트 상수 · JSON 이스케이프 · 응답 DTO.
    // 부작용 없음(Log/Dispatcher/WebView 의존 없음). 문자열·로직은 원본 그대로.
    internal static class NetcusText
    {
        // ----- 주간범위 읽기 응답 DTO -----
        internal sealed class NcRow { public string viewNo = "", regDate = "", title = ""; }
        internal sealed class NcCell { public string period = "", title = "", regdate = "", endwork = "", content = "", plan = ""; }

        internal static string J(string s) => JsonSerializer.Serialize(s);

        // 본문에서 첫 한글 토막(연속 한글 2~4자)을 뽑아 저장 후 되읽기 대조용 needle로 사용. 한글 없으면 "".
        internal static string NetcusHangulNeedle(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "")
            {
                if (ch >= 0xAC00 && ch <= 0xD7A3) { sb.Append(ch); if (sb.Length >= 4) break; }
                else { if (sb.Length >= 2) break; sb.Clear(); }
            }
            return sb.Length >= 2 ? sb.ToString() : "";
        }

        // "YYYY-MM-DD"/"YYYY/MM/DD"/"YYYY.MM.DD"(+선택 시각) → DateTime. 실패 시 false.
        internal static bool NcDate(string s, out DateTime d)
        {
            d = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim().Replace('/', '-').Replace('.', '-');
            int sp = t.IndexOf(' '); if (sp > 0) t = t.Substring(0, sp);
            return DateTime.TryParseExact(t, new[] { "yyyy-M-d", "yyyy-MM-dd", "yyyy-M-dd", "yyyy-MM-d" },
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d);
        }

        // 기간 "YYYY-MM-DD ~ YYYY-MM-DD" → 시작/끝. '~'(또는 '∼') 기준으로만 분리(날짜의 '-'와 혼동 방지).
        internal static bool NcPeriod(string s, out DateTime a, out DateTime b)
        {
            a = default; b = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            int i = s.IndexOf('~'); if (i < 0) i = s.IndexOf('∼');
            if (i < 0) return false;
            return NcDate(s.Substring(0, i), out a) && NcDate(s.Substring(i + 1), out b);
        }

        internal static string NcDigits(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "") if (c >= '0' && c <= '9') sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "0";
        }

        // go_list(p) 재현 — pjm.jsp?list=go&start=p 로 동적 POST(hidden: table_code=report_tbl/id + 빈 코드들).
        internal static string GoListJs(int p, string id) =>
            "(function(){try{var f=document.createElement('form');f.method='post';f.action='pjm.jsp?list=go&start=" + p + "';"
          + "function H(n,v){var i=document.createElement('input');i.type='hidden';i.name=n;i.value=v;f.appendChild(i);}"
          + "H('word_code','');H('n_code','');H('s_code','');H('c_code','');H('table_code','report_tbl');H('id'," + J(id) + ");"
          + "document.body.appendChild(f);f.submit();return 'ok';}catch(e){return 'err';}})()";

        // go_view(viewNo) 재현 — pjm_view.jsp?start=1&view_no=viewNo 로 동적 POST(동일 hidden 필드).
        internal static string GoViewJs(string viewNo, string id) =>
            "(function(){try{var f=document.createElement('form');f.method='post';f.action='pjm_view.jsp?start=1&view_no=" + NcDigits(viewNo) + "';"
          + "function H(n,v){var i=document.createElement('input');i.type='hidden';i.name=n;i.value=v;f.appendChild(i);}"
          + "H('word_code','');H('n_code','');H('s_code','');H('c_code','');H('table_code','report_tbl');H('id'," + J(id) + ");"
          + "document.body.appendChild(f);f.submit();return 'ok';}catch(e){return 'err';}})()";

        // 목록 페이지 행 추출 — a[href*=go_view]마다 {viewNo, regDate(YYYY-MM-DD), title}. JSON 배열 문자열 반환.
        internal const string RowExtractJs = @"(function(){var out=[];var as=document.getElementsByTagName('a');for(var i=0;i<as.length;i++){var a=as[i];var h=a.getAttribute('href')||'';if(h.indexOf('go_view')<0)continue;var m=/go_view\(\s*['""]?(\d+)['""]?\s*\)/.exec(h);if(!m)continue;var title=((a.innerText||a.textContent||'')+'').replace(/\s+/g,' ').trim();var reg='';var tr=a.closest?a.closest('tr'):null;if(tr){var tds=tr.getElementsByTagName('td');for(var j=0;j<tds.length;j++){var tx=((tds[j].innerText||'')+'').trim();var dm=/(\d{4})[\/\-.](\d{1,2})[\/\-.](\d{1,2})/.exec(tx);if(dm){var mm=dm[2].length<2?'0'+dm[2]:dm[2];var dd=dm[3].length<2?'0'+dm[3]:dm[3];reg=dm[1]+'-'+mm+'-'+dd;break;}}}out.push({viewNo:m[1],regDate:reg,title:title});}return JSON.stringify(out);})()";

        // 조회 페이지 라벨셀 추출 — 라벨 div/td 텍스트 정규화 정확일치 → 다음 셀 innerText. JSON 객체 문자열 반환.
        internal const string CellExtractJs = @"(function(){function C(lb){var tds=document.getElementsByTagName('td');for(var i=0;i<tds.length;i++){var d=tds[i].querySelector&&tds[i].querySelector('div');var tx=((d?d.innerText:tds[i].innerText)||'').replace(/\s+/g,' ').trim();if(tx===lb){var n=tds[i].nextElementSibling;if(n)return n.innerText;}}return '';}return JSON.stringify({period:C('기간'),title:C('제목'),regdate:C('작성일'),endwork:C('진행사항'),content:C('과제투입시간'),plan:C('차주계획')});})()";
    }
}
