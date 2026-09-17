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

        /// <summary>
        /// 보고 입력칸이 최상위 문서에 있을 수도, 프레임 안에 있을 수도 있다.
        /// (사내 포털은 왼쪽 메뉴 + 본문 구조라 본문이 프레임인 경우가 있다.)
        /// 그래서 모든 스크립트 앞에 이 찾기 함수를 붙여, 어느 쪽이든 같은 코드로 접근한다.
        ///   __fcDoc()      : 'content' 입력칸을 가진 문서 (없으면 null)
        ///   __fcGet(name)  : 그 문서에서 name 으로 요소 찾기
        /// 같은 사이트(동일 출처)라 프레임 안을 들여다볼 수 있다.
        /// </summary>
        private const string Finder =
            "function __fcHas(d){try{return d&&d.getElementsByName&&d.getElementsByName('content')[0]?d:null;}catch(e){return null;}}"
          + "function __fcDoc(){var r=__fcHas(document);if(r)return r;"
          + "var q=[window];for(var i=0;i<q.length&&i<40;i++){var w=q[i];"
          + "try{for(var j=0;j<w.frames.length;j++){var f=w.frames[j];var d=null;"
          + "try{d=f.document;}catch(e){continue;}var h=__fcHas(d);if(h)return h;q.push(f);}}catch(e){}}return null;}"
          + "function __fcGet(n){var d=__fcDoc();if(!d)return null;try{return d.getElementsByName(n)[0]||null;}catch(e){return null;}}";

        /// <summary>찾기 함수를 붙여 즉시실행 함수로 감싼다.</summary>
        private static string Js(string body)
        {
            return "(function(){" + Finder + "try{" + body + "}catch(e){return 'ERR:'+((e&&e.message)?e.message:String(e));}})()";
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
        /// <summary>로그인 결과. 실패했으면 왜 실패했는지 사람이 읽을 수 있게 담는다.</summary>
        public sealed class LoginResult
        {
            public bool Ok { get; set; }
            /// <summary>실패 사유. 성공이면 빈 문자열.</summary>
            public string Reason { get; set; }
            /// <summary>실패했을 때 사이트 화면에 떠 있던 문구(있으면).</summary>
            public string PageText { get; set; }

            public LoginResult() { Reason = ""; PageText = ""; }
        }

        private static LoginResult Fail(string why, string page = "")
        {
            return new LoginResult { Ok = false, Reason = why, PageText = page ?? "" };
        }

        /// <summary>
        /// 로그인한다. 비밀번호는 반환값·로그 어디에도 싣지 않는다.
        /// 실패하면 어느 단계에서 왜 막혔는지 Reason 에 남긴다 — 이게 없으면 사용자가 손쓸 방법이 없다.
        /// </summary>
        public async Task<LoginResult> LoginAsync(string id, string pw)
        {
            _id = (id ?? "").Trim();

            Say("로그인 페이지 여는 중…");
            if (!await NavTo(LoginUrl))
                return Fail("로그인 페이지를 열지 못했습니다. 인터넷 연결이나 사내 차단을 확인하세요.");

            // 폼이 준비될 때까지 기다린다. 바로 넣으면 스크립트가 아직 없어 그냥 실패한다.
            Say("로그인 폼 확인 중…");
            string formState = "";
            for (int i = 0; i < 16; i++)
            {
                formState = FromJson(await Eval(
                    "(function(){try{"
                    + "if(!(document.form&&document.form.id&&document.form.pass))return 'NOFORM';"
                    + "if(typeof goLogin!=='function')return 'NOFUNC';"
                    + "return 'OK';}catch(e){return 'NOFORM';}})()")) ?? "";
                if (formState == "OK") break;
                await Task.Delay(250);
            }
            if (formState == "NOFORM")
                return Fail("로그인 폼을 찾지 못했습니다. 사이트 구조가 바뀌었거나 다른 페이지가 열렸습니다.",
                            await PageSummary());
            if (formState != "OK")
                return Fail("로그인 스크립트(goLogin)를 찾지 못했습니다. 사이트가 바뀌었을 수 있습니다.",
                            await PageSummary());

            // 페이지의 goLogin() 이 내부에서 암호화 후 form.submit() 을 부른다. 그대로 태운다.
            // 제출까지 갔는지 페이지가 직접 알려 준다 — 제출이 없으면 기다릴 이동도 없다.
            Say("로그인 시도 중…");
            var nav = NavOnce(20000);
            string js = "(function(){try{"
                + "document.form.id.value=" + ToJs(_id) + ";"
                + "document.form.pass.value=" + ToJs(pw ?? "") + ";"
                + "var submitted=false;var orig=document.form.submit;"
                + "document.form.submit=function(){submitted=true;return orig.apply(this,arguments);};"
                + "try{goLogin();}finally{try{document.form.submit=orig;}catch(_){}}"
                + "return submitted?'SUBMITTED':'NOSUBMIT';"
                + "}catch(e){return 'ERR:'+((e&&e.message)?e.message:String(e));}})()";
            string r = FromJson(await Eval(js)) ?? "";

            if (r.StartsWith("ERR:", StringComparison.Ordinal))
                return Fail("로그인 스크립트에서 오류가 났습니다: " + r.Substring(4), await PageSummary());

            if (r != "SUBMITTED")
            {
                // goLogin() 이 자체 검사(빈 값·형식 등)에서 멈춘 것. 사이트가 띄운 문구가 유일한 단서다.
                return Fail("사이트가 로그인 시도를 접수하지 않았습니다. 아이디·비밀번호를 확인하세요.",
                            await PageSummary());
            }

            await nav;

            // 보호 페이지가 열리는지로 인증을 확정한다(로그인 페이지 문구 판정은 못 믿는다).
            Say("인증 확인 중…");
            if (await IsAuthenticated(DateTime.Today)) return new LoginResult { Ok = true };

            // 여기까지 왔으면 제출은 됐다. 로그인 자체가 실패한 건지, 로그인은 됐는데
            // 우리가 입력칸을 못 찾은 건지 구분해 준다 — 둘은 해야 할 일이 완전히 다르다.
            string diag = await DiagnoseAsync();
            bool loginFormShown = diag.Contains("비밀번호칸=있음");
            string why = loginFormShown
                ? "로그인이 거부됐습니다. 아이디·비밀번호를 확인하세요."
                : "로그인은 된 것으로 보이는데 보고 입력칸을 찾지 못했습니다. 사이트 화면 구조가 바뀌었을 수 있습니다.";
            return Fail(why, diag);
        }

        /// <summary>
        /// 비밀번호 없이, 로그인 페이지가 우리가 기대하는 모양인지만 확인한다.
        /// 여기가 깨져 있으면 어떤 비밀번호를 넣어도 실패하므로 원인 분리에 쓴다.
        /// </summary>
        public async Task<LoginResult> CheckLoginPageAsync()
        {
            Say("로그인 페이지 확인 중…");
            if (!await NavTo(LoginUrl))
                return Fail("로그인 페이지를 열지 못했습니다. 인터넷 연결이나 사내 차단을 확인하세요.");

            for (int i = 0; i < 16; i++)
            {
                string s = FromJson(await Eval(
                    "(function(){try{"
                    + "var f=document.form, hasId=!!(f&&f.id), hasPw=!!(f&&f.pass),"
                    + "hasFn=(typeof goLogin==='function');"
                    + "if(hasId&&hasPw&&hasFn)return 'OK';"
                    + "return 'form='+(f?'Y':'N')+' id='+(hasId?'Y':'N')+' pass='+(hasPw?'Y':'N')+' goLogin='+(hasFn?'Y':'N');"
                    + "}catch(e){return 'ERR:'+((e&&e.message)?e.message:String(e));}})()")) ?? "";
                if (s == "OK")
                    return new LoginResult { Ok = true, Reason = "로그인 페이지 정상 (아이디·비밀번호 칸과 goLogin 확인)" };
                if (i == 15)
                    return Fail("로그인 페이지 구조가 예상과 다릅니다 — " + s, await PageSummary());
                await Task.Delay(250);
            }
            return Fail("로그인 페이지를 확인하지 못했습니다.");
        }

        /// <summary>지금 화면에 보이는 글의 앞부분. 실패 원인을 사람이 판단할 유일한 단서다.</summary>
        private async Task<string> PageSummary()
        {
            try
            {
                string t = FromJson(await Eval(
                    "(function(){try{var b=document.body;var s=b?(b.innerText||b.textContent||''):'';"
                    + "return s.replace(/\\s+/g,' ').trim().slice(0,300);}catch(e){return '';}})()"));
                return t ?? "";
            }
            catch { return ""; }
        }

        /// <summary>자동화 창을 사용자에게 보여 준다. 무슨 화면이 떠 있는지 눈으로 확인할 때.</summary>
        public void ShowWindow()
        {
            try
            {
                if (_win == null) return;
                _win.ShowInTaskbar = true;
                _win.WindowState = WindowState.Normal;
                _win.Show();
                _win.Activate();
            }
            catch { }
        }

        /// <summary>
        /// 보호 페이지가 열리는지로 인증을 확정한다.
        /// 이동이 한 번 실패해도 바로 포기하지 않는다 — 로그인 직후에는 리다이렉트가 겹쳐
        /// 우리 이동이 밀려날 수 있다. 그럴 땐 현재 페이지를 그대로 보고 판단한다.
        /// </summary>
        private async Task<bool> IsAuthenticated(DateTime d)
        {
            await NavTo(DayUrl(d));

            for (int i = 0; i < 20; i++)
            {
                string v = FromJson(await Eval(Js(
                    "if(__fcDoc())return 'OK';"
                  + "if(document.querySelector('input[type=password]'))return 'LOGIN';"
                  + "return 'WAIT';"))) ?? "";
                if (v == "OK") return true;
                // 로그인 폼이 보이면 실패지만, 리다이렉트 도중일 수 있으니 한 번 더 확인한다.
                if (v == "LOGIN" && i > 2) return false;
                await Task.Delay(300);
            }
            return false;
        }

        /// <summary>
        /// 인증 판정이 틀렸을 때 원인을 좁히기 위한 정보.
        /// "로그인은 됐는데 못 찾는다" 와 "정말 로그인이 안 됐다" 를 가른다.
        /// </summary>
        public async Task<string> DiagnoseAsync()
        {
            string js = Js(
                "var d=__fcDoc();"
              + "var names=[];try{var ta=document.getElementsByTagName('textarea');"
              + "for(var i=0;i<ta.length&&i<10;i++)names.push(ta[i].name||'(이름없음)');}catch(e){}"
              + "var fc=0;try{fc=window.frames.length;}catch(e){}"
              + "return 'URL='+location.href"
              + "+' | 제목='+(document.title||'')"
              + "+' | 프레임='+fc"
              + "+' | 입력칸찾음='+(d?'예':'아니오')"
              + "+' | 최상위textarea=['+names.join(',')+']'"
              + "+' | 비밀번호칸='+(document.querySelector('input[type=password]')?'있음':'없음');");
            return FromJson(await Eval(js)) ?? "(진단 정보를 읽지 못했습니다)";
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
            await NavTo(DayUrl(d));

            for (int i = 0; i < 16; i++)
            {
                string raw = await Eval(Js("var c=__fcGet('content');return c?c.value:null;"));
                if (!string.IsNullOrEmpty(raw) && raw != "null") return FromJson(raw) ?? "";

                string pw = FromJson(await Eval(
                    "(function(){return document.querySelector('input[type=password]')?'LOGIN':'NO';})()"));
                if (pw == "LOGIN" && i > 2)
                    throw new InvalidOperationException("로그인이 풀렸습니다. 다시 시도하세요.");
                await Task.Delay(300);
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

            await NavTo(url);

            // 폼이 뜰 때까지 기다린다(레거시 마크업이라 document.form 대신 name 으로 찾는다).
            bool formOk = false;
            for (int i = 0; i < 16; i++)
            {
                string p = FromJson(await Eval(Js("return __fcGet('content')?'OK':'NO';")));
                if (p == "OK") { formOk = true; break; }
                await Task.Delay(300);
            }
            if (!formOk)
            {
                res.Message = "입력 폼을 찾지 못했습니다 — " + await DiagnoseAsync();
                return res;
            }

            res.Replaced = FromJson(await Eval(Js("var c=__fcGet('content');return c?c.value:'';"))) ?? "";

            // 제출 — 페이지가 euc-kr 이라 네이티브 폼 submit 경로를 쓴다(fetch 는 UTF-8 고정이라 안 됨).
            // status/overtime 은 '현재 페이지 값'을 그대로 되싣는다 = 근태를 건드리지 않는다.
            // 입력칸이 프레임 안에 있을 수 있으므로, 제출 폼도 그 문서 안에서 만들어 보낸다.
            var nav = NavOnce(20000);
            string post = Js(
                  "var doc=__fcDoc();if(!doc)return 'ERR:입력칸을 찾지 못함';"
                + "var db=doc.getElementsByName('dbstatus')[0];"
                + "var st=doc.getElementsByName('status')[0];"
                + "var ot=doc.getElementsByName('overtime')[0];"
                + "var f=doc.createElement('form');f.method='post';f.enctype='multipart/form-data';f.acceptCharset='euc-kr';"
                + "f.action='pjm_work_view.jsp?go=write&table=report_tbl&y=" + d.Year + "&m=" + d.Month + "&d=" + d.Day
                + "&id='+encodeURIComponent(" + ToJs(_id) + ");"
                + "function H(n,v){var i=doc.createElement('input');i.type='hidden';i.name=n;i.value=v;f.appendChild(i);}"
                + "H('dbstatus',(db&&db.value)?db.value:'0');"
                + "H('status',(st&&st.value)?st.value:'1');"
                + "H('overtime',(ot&&ot.value)?ot.value:'0');"
                + "var ta=doc.createElement('textarea');ta.name='content';ta.value=" + ToJs(content) + ";f.appendChild(ta);"
                + "doc.body.appendChild(f);f.submit();return 'SUBMITTED';");
            string fired = FromJson(await Eval(post)) ?? "";
            if (fired != "SUBMITTED")
            {
                res.Message = "제출 폼을 만들지 못했습니다" + (fired.StartsWith("ERR:") ? " (" + fired.Substring(4) + ")" : "");
                return res;
            }
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
