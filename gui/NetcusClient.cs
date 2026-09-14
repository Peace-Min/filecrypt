using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WV = Microsoft.Web.WebView2.Wpf;

namespace FileCrypt
{
    /// <summary>
    /// 사내 보고 시스템(netcus pjm)에 로그인해서 날짜별 보고 칸을 읽고 쓴다.
    /// 창은 최소화 상태로만 띄운다 — 작업할 때마다 화면 가운데 창이 튀어나오면 못 쓴다.
    ///
    /// 이 클래스가 지키는 선:
    ///   · 근태(status)와 초과시간(overtime)은 절대 바꾸지 않는다. 페이지의 현재 값을 그대로 되싣는다.
    ///   · 저장 후 반드시 되읽어 보낸 것과 글자 단위로 대조한다(조용한 잘림을 잡는 유일한 방법).
    ///   · 비밀번호는 로그·오류 메시지 어디에도 남기지 않는다.
    ///
    /// 페이지 구조와 제출 경로는 수행과제 캘린더의 NetcusService 에서 이미 실사용으로 검증된 것을 따른다.
    /// </summary>
    public sealed class NetcusClient : IDisposable
    {
        private const string LoginUrl = "https://www.netcus.com/pjm/login.htm";
        private const string Host     = "https://www.netcus.com/pjm/";

        private Window _win;
        private WV.WebView2 _wv;
        private string _id = "";
        private bool _ready;

        public event Action<string> Progress;
        private void Say(string s) { var h = Progress; if (h != null) h(s); }

        /// <summary>보이지 않는 창에 WebView2 를 올린다. 반드시 UI 스레드에서 부를 것.</summary>
        public async Task InitAsync()
        {
            if (_ready) return;

            string udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileCrypt", "wv2");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(null, udf);

            _wv = new WV.WebView2();
            _win = new Window
            {
                Title = "사내 보고 시스템 연결",
                Width = 1000,
                Height = 700,
                ShowActivated = false,              // 포커스를 빼앗지 않는다
                WindowState = WindowState.Minimized,
                ShowInTaskbar = false,
                Content = _wv
            };
            _win.Show();
            await _wv.EnsureCoreWebView2Async(env);

            // 페이지가 띄우는 alert/confirm 때문에 자동화가 멈추지 않도록 받아 넘긴다.
            _wv.CoreWebView2.ScriptDialogOpening += (s, e) =>
            {
                try { e.Accept(); } catch { }
                try { e.GetDeferral().Complete(); } catch { }
            };
            _ready = true;
        }

        // ------------------------------------------------------------ 이동 / 스크립트
        private Task<bool> NavOnce(int timeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> h = null;
            h = (s, e) =>
            {
                try { _wv.CoreWebView2.NavigationCompleted -= h; } catch { }
                tcs.TrySetResult(e.IsSuccess);
            };
            _wv.CoreWebView2.NavigationCompleted += h;
            Task.Delay(timeoutMs).ContinueWith(_ =>
            {
                try { _wv.CoreWebView2.NavigationCompleted -= h; } catch { }
                tcs.TrySetResult(false);
            });
            return tcs.Task;
        }

        private async Task<bool> NavTo(string url, int timeoutMs = 20000)
        {
            var done = NavOnce(timeoutMs);
            _wv.CoreWebView2.Navigate(url);
            return await done;
        }

        private async Task<string> Eval(string js)
        {
            try { return await _wv.CoreWebView2.ExecuteScriptAsync(js); }
            catch { return null; }
        }

        /// <summary>ExecuteScriptAsync 가 돌려주는 JSON 값에서 문자열을 꺼낸다.</summary>
        private static string FromJson(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null") return null;
            if (json.Length < 2 || json[0] != '"') return json.Trim('"');
            var sb = new StringBuilder(json.Length);
            for (int i = 1; i < json.Length - 1; i++)
            {
                char c = json[i];
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= json.Length - 1) break;
                char e = json[i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'u':
                        if (i + 4 < json.Length - 1)
                        {
                            int code;
                            if (int.TryParse(json.Substring(i + 1, 4),
                                             System.Globalization.NumberStyles.HexNumber,
                                             System.Globalization.CultureInfo.InvariantCulture, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>문자열을 JS 리터럴로. 스크립트에 값을 실어 보낼 때 쓴다.</summary>
        private static string ToJs(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------ 로그인
        /// <summary>로그인 성공 여부만 돌려준다. 비밀번호는 어디에도 남기지 않는다.</summary>
        public async Task<bool> LoginAsync(string id, string pw)
        {
            _id = (id ?? "").Trim();
            Say("로그인 중…");

            if (!await NavTo(LoginUrl)) return false;

            // 페이지의 goLogin() 이 내부에서 암호화 후 form.submit() 을 부른다. 그대로 태운다.
            var nav = NavOnce(20000);
            string js = "(function(){try{"
                + "document.form.id.value=" + ToJs(_id) + ";"
                + "document.form.pass.value=" + ToJs(pw ?? "") + ";"
                + "var submitted=false;var orig=document.form.submit;"
                + "document.form.submit=function(){submitted=true;return orig.apply(this,arguments);};"
                + "try{goLogin();}finally{try{document.form.submit=orig;}catch(_){}}"
                + "return submitted?'SUBMITTED':'NOSUBMIT';"
                + "}catch(e){return 'ERR';}})()";
            string r = FromJson(await Eval(js));
            if (r != "SUBMITTED") return false;   // 아이디/비번 형식 오류 등으로 submit 까지 못 감
            await nav;

            // 보호 페이지가 열리는지로 인증을 확정한다(로그인 페이지 문구 판정은 못 믿는다).
            return await IsAuthenticated(DateTime.Today);
        }

        private async Task<bool> IsAuthenticated(DateTime d)
        {
            if (!await NavTo(DayUrl(d))) return false;
            for (int i = 0; i < 12; i++)
            {
                string v = FromJson(await Eval(
                    "(function(){if(document.querySelector('input[type=password]'))return 'LOGIN';"
                    + "if(document.getElementsByName('content')[0])return 'OK';return 'WAIT';})()"));
                if (v == "OK") return true;
                if (v == "LOGIN") return false;
                await Task.Delay(250);
            }
            return false;
        }

        private string DayUrl(DateTime d)
        {
            return string.Format("{0}pjm_work_view.jsp?y={1}&m={2}&d={3}&id={4}",
                Host, d.Year, d.Month, d.Day, Uri.EscapeDataString(_id));
        }

        // ------------------------------------------------------------ 읽기
        /// <summary>그 날짜 보고 칸의 내용. 접근 못 하면 null(빈 칸은 "" 로 구분).</summary>
        public async Task<string> ReadDayAsync(DateTime d)
        {
            Say(string.Format("{0:yyyy-MM-dd} 읽는 중…", d));
            if (!await NavTo(DayUrl(d))) return null;

            for (int i = 0; i < 12; i++)
            {
                string raw = await Eval(
                    "(function(){var c=document.getElementsByName('content')[0];return c?c.value:null;})()");
                if (!string.IsNullOrEmpty(raw) && raw != "null") return FromJson(raw) ?? "";

                string pw = FromJson(await Eval(
                    "(function(){return document.querySelector('input[type=password]')?'LOGIN':'NO';})()"));
                if (pw == "LOGIN") throw new InvalidOperationException("로그인이 풀렸습니다. 다시 시도하세요.");
                await Task.Delay(250);
            }
            return null;
        }

        // ------------------------------------------------------------ 쓰기
        public sealed class WriteResult
        {
            public bool Ok { get; set; }
            public string Message { get; set; }
            /// <summary>덮어쓰기 전에 그 칸에 있던 내용(되돌릴 때 쓴다).</summary>
            public string Replaced { get; set; }
            public int SavedChars { get; set; }
        }

        /// <summary>
        /// 그 날짜 보고 칸에 content 를 쓰고, 되읽어 글자 단위로 대조한다.
        /// 근태·초과시간은 페이지의 현재 값을 그대로 되실어 보존한다.
        /// </summary>
        public async Task<WriteResult> WriteDayAsync(DateTime d, string content)
        {
            var res = new WriteResult();
            string url = DayUrl(d);
            Say(string.Format("{0:yyyy-MM-dd} 올리는 중…", d));

            if (!await NavTo(url)) { res.Message = "페이지를 열지 못했습니다."; return res; }

            // 폼이 뜰 때까지 기다린다(레거시 마크업이라 document.form 대신 name 으로 찾는다).
            bool formOk = false;
            for (int i = 0; i < 12; i++)
            {
                string p = FromJson(await Eval(
                    "(function(){return (document.getElementsByName('status')[0]&&document.getElementsByName('content')[0])?'OK':'NO';})()"));
                if (p == "OK") { formOk = true; break; }
                await Task.Delay(250);
            }
            if (!formOk) { res.Message = "입력 폼을 찾지 못했습니다 — 페이지 구조가 바뀌었을 수 있습니다."; return res; }

            res.Replaced = FromJson(await Eval(
                "(function(){var c=document.getElementsByName('content')[0];return c?c.value:'';})()")) ?? "";

            // 제출 — 페이지가 euc-kr 이라 네이티브 폼 submit 경로를 쓴다(fetch 는 UTF-8 고정이라 안 됨).
            // status/overtime 은 '현재 페이지 값'을 그대로 되싣는다 = 근태를 건드리지 않는다.
            var nav = NavOnce(20000);
            string post = "(function(){try{"
                + "var db=document.getElementsByName('dbstatus')[0];"
                + "var st=document.getElementsByName('status')[0];"
                + "var ot=document.getElementsByName('overtime')[0];"
                + "var f=document.createElement('form');f.method='post';f.enctype='multipart/form-data';f.acceptCharset='euc-kr';"
                + "f.action='pjm_work_view.jsp?go=write&table=report_tbl&y=" + d.Year + "&m=" + d.Month + "&d=" + d.Day
                + "&id='+encodeURIComponent(" + ToJs(_id) + ");"
                + "function H(n,v){var i=document.createElement('input');i.type='hidden';i.name=n;i.value=v;f.appendChild(i);}"
                + "H('dbstatus',(db&&db.value)?db.value:'0');"
                + "H('status',(st&&st.value)?st.value:'1');"
                + "H('overtime',(ot&&ot.value)?ot.value:'0');"
                + "var ta=document.createElement('textarea');ta.name='content';ta.value=" + ToJs(content) + ";f.appendChild(ta);"
                + "document.body.appendChild(f);f.submit();return 'SUBMITTED';"
                + "}catch(e){return 'ERR';}})()";
            string fired = FromJson(await Eval(post));
            if (fired != "SUBMITTED") { res.Message = "제출 폼을 만들지 못했습니다."; return res; }
            await nav;

            // 되읽어 대조 — 조용한 잘림은 여기서만 드러난다.
            Say(string.Format("{0:yyyy-MM-dd} 저장 확인 중…", d));
            string got = null;
            for (int i = 0; i < 14; i++)
            {
                if (!await NavTo(url)) { await Task.Delay(300); continue; }
                got = await ReadDayAsync(d);
                if (!string.IsNullOrEmpty(got)) break;
                await Task.Delay(300);
            }
            if (string.IsNullOrEmpty(got)) { res.Message = "저장 확인 실패 — 내용이 비어 있습니다."; return res; }

            res.SavedChars = got.Length;
            if (Normalize(got) == Normalize(content))
            {
                res.Ok = true;
                res.Message = string.Format("{0:N0}자 저장 확인", got.Length);
                return res;
            }

            res.Message = got.Length < content.Length
                ? string.Format("잘렸습니다 — 보낸 {0:N0}자 중 {1:N0}자만 저장됨. 조각 크기를 줄이세요.", content.Length, got.Length)
                : string.Format("저장된 내용이 보낸 것과 다릅니다 (보낸 {0:N0}자 / 저장 {1:N0}자).", content.Length, got.Length);
            return res;
        }

        /// <summary>줄바꿈 표기 차이는 무시하고 비교한다(사이트가 CRLF/LF 를 바꿔 저장할 수 있다).</summary>
        private static string Normalize(string s)
        {
            if (s == null) return "";
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        public void Dispose()
        {
            try { if (_wv != null) _wv.Dispose(); } catch { }
            try { if (_win != null) _win.Close(); } catch { }
            _wv = null; _win = null; _ready = false;
        }
    }
}
