// 근태관리(www.netcus.com/pjm) 목업 서버.
//
// 실제 사이트에서 떠 온 구조(FileCrypt.exe --netcus-capture, 2026-09-30)를 기준으로
// FileCrypt / NetcusService 가 쓰는 흐름만 흉내 낸다. 사이트의 스크립트·이미지·내용은 가져오지 않고 새로 썼다.
//
//   GET  login.htm            폼 name=form, action=loginRSA.jsp, 필드 UserName_Enc/Password_Enc/id/pass,
//                             goLogin() -> Encrypt('SST/PublicKey.xml') -> submit. (실제는 RSA, 여기서는 base64)
//   POST loginRSA.jsp         성공: JSESSIONID(Path=/pjm; Secure) + meta refresh -> pjm.jsp
//                             실패: meta refresh -> login.htm (실제 실패 응답은 캡처하지 않았다 - 가정)
//   GET  pjm_work_view.jsp    로그인 안 됨: 빈 줄 + <meta http-equiv='Refresh' content='0; URL=login.htm'> (실측 그대로)
//                             로그인 됨: euc-kr, form name='form' multipart, dbstatus/status/overtime/content, Bmodify()
//   POST pjm_work_view.jsp?go=write&table=report_tbl&y=&m=&d=&id=
//                             multipart(euc-kr) 로 받은 status/overtime/content/dbstatus 저장 -> 그 날짜로 되돌아감
//                             (실제 기록 응답은 캡처하지 않았다 - 실제 일간보고에 쓰게 되므로. 가정)
//
// 오프라인·관리자 권한 없이 돈다: 127.0.0.1 에서 자체 서명 인증서로 TLS 를 연다.
// 브라우저 쪽은 --host-resolver-rules 로 www.netcus.com 을 여기로 돌리고 인증서 검사를 끈다(목업일 때만).
//
// PowerShell 5.1 의 Add-Type(C# 5)으로 컴파일되므로 C# 6 이후 문법($"", ?., =>)을 쓰지 않는다.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FileCryptMock
{
    public sealed class MockDay
    {
        public string Status = "";     // "" = 아직 근태 미선택
        public int Overtime;
        public string Content = "";
        public bool Exists;            // 한 번이라도 기록됐는가 (dbstatus)
    }

    public sealed class NetcusMock : IDisposable
    {
        private static readonly Encoding Kr = Encoding.GetEncoding(949);

        private TcpListener _listener;
        private X509Certificate2 _cert;
        private Thread _thread;
        private volatile bool _stop;

        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _users = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sessions = new Dictionary<string, string>(StringComparer.Ordinal);   // sid -> user
        private readonly Dictionary<string, int> _sessionHits = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, MockDay> _days = new Dictionary<string, MockDay>(StringComparer.Ordinal);    // "user|yyyy-MM-dd"

        public int Port { get; private set; }

        // ---------------------------------------------------------------- 장애 주입 (테스트가 직접 바꾼다)
        /// <summary>로그인 POST 가 이 횟수를 넘으면 이후 로그인은 전부 실패(사이트가 몰린 로그인을 막는 상황). -1 = 없음</summary>
        public int BlockLoginsAfter = -1;
        /// <summary>세션 하나로 이만큼 요청하면 세션이 풀린다. -1 = 없음</summary>
        public int ExpireSessionAfter = -1;
        /// <summary>0 보다 크면 저장할 때 내용을 이 글자수로 자른다(사이트가 긴 글을 잘라 저장하는 상황)</summary>
        public int TruncateContentAt;
        /// <summary>이 날짜(yyyy-MM-dd)들은 기록을 받는 척하고 저장하지 않는다</summary>
        public readonly HashSet<string> DropWritesOn = new HashSet<string>();
        /// <summary>이 날짜들은 첫 기록 한 번만 버린다(일시 오류 뒤 다시 올리기)</summary>
        public readonly HashSet<string> DropWritesOnceOn = new HashSet<string>();
        /// <summary>모든 응답 전에 쉬는 시간(ms)</summary>
        public int DelayMs;
        /// <summary>이 경로(예: "pjm_work_view.jsp")로 오는 요청은 응답하지 않고 끊지도 않는다(무응답)</summary>
        public string HangOnPath;
        /// <summary>
        /// 기록 후 그 주(월~일) 합계가 52시간을 넘으면 저장을 거부한다. 실제 사이트는 페이지의 Bmodify() 가 막는 것만
        /// 확인됐고 서버가 막는지는 모른다 - FileCrypt 는 어느 쪽이든 스스로 넘기지 않아야 한다.
        /// </summary>
        public bool Enforce52 = true;

        // ---------------------------------------------------------------- 관찰 (테스트가 읽는다)
        public int LoginPosts;
        public int LoginFailures;
        public int Requests;
        public readonly List<string> WriteLog = new List<string>();     // "yyyy-MM-dd" 순서대로
        public readonly List<string> RequestLog = new List<string>();   // "METHOD path?query"
        public readonly List<string> Rejected52 = new List<string>();   // 주 52시간 때문에 거부한 날짜

        public void AddUser(string id, string pw) { lock (_gate) { _users[id] = pw; } }

        public MockDay GetDay(string user, DateTime d)
        {
            lock (_gate)
            {
                MockDay v;
                return _days.TryGetValue(user + "|" + d.ToString("yyyy-MM-dd"), out v) ? v : null;
            }
        }

        public void SetDay(string user, DateTime d, string status, int overtime, string content)
        {
            lock (_gate)
            {
                _days[user + "|" + d.ToString("yyyy-MM-dd")] =
                    new MockDay { Status = status ?? "", Overtime = overtime, Content = content ?? "", Exists = true };
            }
        }

        /// <summary>모든 세션을 없앤다(다음 요청부터 로그인 필요).</summary>
        public void DropSessions() { lock (_gate) { _sessions.Clear(); _sessionHits.Clear(); } }

        // ---------------------------------------------------------------- 시작/정지
        public void Start(int port)
        {
            _cert = MakeCert();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(Loop) { IsBackground = true, Name = "netcus-mock" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
        }

        /// <summary>
        /// 자체 서명 인증서. CreateSelfSigned 가 돌려주는 임시 키는 SChannel 이 못 쓰는 경우가 있어
        /// PFX 로 내보냈다가 다시 읽어 들인다.
        /// </summary>
        private static X509Certificate2 MakeCert()
        {
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=www.netcus.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("www.netcus.com");
                san.AddIpAddress(IPAddress.Loopback);
                req.CertificateExtensions.Add(san.Build());
                using (var c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30)))
                    return new X509Certificate2(c.Export(X509ContentType.Pfx, "mock"), "mock", X509KeyStorageFlags.Exportable);
            }
        }

        private void Loop()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_stop) return; continue; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    using (var ssl = new SslStream(client.GetStream(), false))
                    {
                        ssl.AuthenticateAsServer(_cert, false, SslProtocols.Tls12, false);
                        // 연결 하나에 요청 여러 개(keep-alive)가 올 수 있다.
                        while (!_stop)
                        {
                            var req = HttpReq.Read(ssl);
                            if (req == null) return;
                            if (!Handle(req, ssl)) return;
                        }
                    }
                }
                catch { /* 브라우저가 연결을 먼저 끊는 것은 정상 */ }
            }
        }

        // ---------------------------------------------------------------- 라우팅
        private bool Handle(HttpReq r, Stream s)
        {
            Interlocked.Increment(ref Requests);
            lock (_gate) { RequestLog.Add(r.Method + " " + r.Target); }
            if (DelayMs > 0) Thread.Sleep(DelayMs);

            string path = r.Path;
            if (!string.IsNullOrEmpty(HangOnPath) && path.EndsWith(HangOnPath, StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(Timeout.Infinite);   // 무응답
            }

            if (path == "/pjm/login.htm")                 return Send(s, 200, "text/html", Kr.GetBytes(LoginPage()), null);
            if (path == "/pjm/SST/SST.js")                return Send(s, 200, "text/javascript", Encoding.UTF8.GetBytes(SstJs), null);
            if (path == "/pjm/SST/Login.js")              return Send(s, 200, "text/javascript", Encoding.UTF8.GetBytes(LoginJs), null);
            if (path == "/pjm/SST/PublicKey.xml")         return Send(s, 200, "application/xml", Encoding.UTF8.GetBytes(PublicKeyXml), null);
            if (path == "/pjm/loginRSA.jsp" && r.Method == "POST") return Login(r, s);

            string user = SessionUser(r);
            if (path == "/pjm/pjm.jsp")
            {
                if (user == null) return Send(s, 200, "text/html;charset=EUC-KR", Kr.GetBytes(Redirect("login.htm")), null);
                return Send(s, 200, "text/html;charset=EUC-KR", Kr.GetBytes(BoardPage(user)), null);
            }
            if (path == "/pjm/pjm_work_view.jsp")
            {
                if (user == null) return Send(s, 200, "text/html;charset=EUC-KR", Kr.GetBytes(Redirect("login.htm")), null);
                DateTime d;
                if (!TryDate(r, out d)) return Send(s, 200, "text/html;charset=EUC-KR", Kr.GetBytes("<html><body>잘못된 날짜</body></html>"), null);
                if (r.Method == "POST" && r.Query("go") == "write") return Write(r, s, user, d);
                return Send(s, 200, "text/html;charset=EUC-KR", Kr.GetBytes(WorkPage(user, d)), null);
            }
            return Send(s, 404, "text/html", Encoding.ASCII.GetBytes("<html><body>404</body></html>"), null);
        }

        private string SessionUser(HttpReq r)
        {
            string sid = r.Cookie("JSESSIONID");
            if (sid == null) return null;
            lock (_gate)
            {
                string u;
                if (!_sessions.TryGetValue(sid, out u)) return null;
                int hits;
                _sessionHits.TryGetValue(sid, out hits);
                hits++;
                _sessionHits[sid] = hits;
                if (ExpireSessionAfter >= 0 && hits > ExpireSessionAfter) { _sessions.Remove(sid); return null; }
                return u;
            }
        }

        private bool Login(HttpReq r, Stream s)
        {
            var f = HttpReq.ParseUrlEncoded(r.Body, Kr);
            string id = f.ContainsKey("id") ? f["id"] : "";
            string enc = f.ContainsKey("Password_Enc") ? f["Password_Enc"] : "";
            string pw = "";
            try { if (enc.StartsWith("MOCK:")) pw = Encoding.UTF8.GetString(Convert.FromBase64String(enc.Substring(5))); } catch { }

            bool ok;
            lock (_gate)
            {
                LoginPosts++;
                string want;
                ok = _users.TryGetValue(id, out want) && want == pw
                  && (BlockLoginsAfter < 0 || LoginPosts <= BlockLoginsAfter);
                if (!ok) LoginFailures++;
            }
            if (!ok) return Send(s, 200, "text/html;charset=EUC-KR", Kr.GetBytes(Redirect("login.htm")), null);

            string sid = Guid.NewGuid().ToString("N").ToUpperInvariant();
            lock (_gate) { _sessions[sid] = id; _sessionHits[sid] = 0; }
            return Send(s, 200, "text/html;charset=EUC-KR", Kr.GetBytes(Redirect("pjm.jsp")),
                        "JSESSIONID=" + sid + "; Path=/pjm; Secure");
        }

        // ---------------------------------------------------------------- 주 52시간 (사이트 getWorkingTime 과 같은 표)
        public static int Hours(string status, int overtime)
        {
            if (string.IsNullOrEmpty(status)) return 0;
            if (status == "3" || status == "6" || status == "11") return overtime;
            if (status == "12") return 4 + overtime;
            return 8 + overtime;
        }

        public static DateTime WeekStart(DateTime d) { return d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)); }

        /// <summary>그 주(월~일)에서 d 를 뺀 나머지 날들의 시간 합계. 페이지의 Bmodify() 에 박히는 숫자.</summary>
        public int WeekOthers(string user, DateTime d)
        {
            int sum = 0;
            DateTime ws = WeekStart(d);
            lock (_gate)
            {
                for (int i = 0; i < 7; i++)
                {
                    DateTime x = ws.AddDays(i);
                    MockDay v;
                    if (x != d.Date && _days.TryGetValue(user + "|" + x.ToString("yyyy-MM-dd"), out v)) sum += Hours(v.Status, v.Overtime);
                }
            }
            return sum;
        }

        private bool Write(HttpReq r, Stream s, string user, DateTime d)
        {
            var f = HttpReq.ParseMultipart(r.Body, r.Header("Content-Type"), Kr);
            string key = d.ToString("yyyy-MM-dd");
            int newOt; int.TryParse(f.ContainsKey("overtime") ? f["overtime"] : "0", out newOt);
            string newSt = f.ContainsKey("status") ? f["status"] : "";
            if (Enforce52 && Hours(newSt, newOt) + WeekOthers(user, d) > 52)
            {
                lock (_gate) { WriteLog.Add(key); Rejected52.Add(key); }
                return Send(s, 200, "text/html;charset=EUC-KR",
                    Kr.GetBytes("<script>alert('근무시간은 주 52시간을 초과할 수 없습니다.');history.go(-1);</script>"), null);
            }
            lock (_gate)
            {
                WriteLog.Add(key);
                if (!DropWritesOn.Contains(key) && !DropWritesOnceOn.Remove(key))
                {
                    string content = f.ContainsKey("content") ? f["content"] : "";
                    if (TruncateContentAt > 0 && content.Length > TruncateContentAt) content = content.Substring(0, TruncateContentAt);
                    int ot; int.TryParse(f.ContainsKey("overtime") ? f["overtime"] : "0", out ot);
                    _days[user + "|" + key] = new MockDay
                    {
                        Status = f.ContainsKey("status") ? f["status"] : "",
                        Overtime = ot,
                        Content = content,
                        Exists = true
                    };
                }
            }
            return Send(s, 200, "text/html;charset=EUC-KR",
                Kr.GetBytes(Redirect(string.Format("pjm_work_view.jsp?y={0}&m={1}&d={2}&id={3}", d.Year, d.Month, d.Day, Uri.EscapeDataString(user)))), null);
        }

        private static bool TryDate(HttpReq r, out DateTime d)
        {
            d = DateTime.MinValue;
            int y, m, dd;
            if (!int.TryParse(r.Query("y"), out y) || !int.TryParse(r.Query("m"), out m) || !int.TryParse(r.Query("d"), out dd)) return false;
            try { d = new DateTime(y, m, dd); return true; } catch { return false; }
        }

        private bool Send(Stream s, int code, string ctype, byte[] body, string setCookie)
        {
            var h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(code).Append(code == 200 ? " OK" : " Not Found").Append("\r\n");
            h.Append("Server: Apache\r\n");
            if (setCookie != null) h.Append("Set-Cookie: ").Append(setCookie).Append("\r\n");
            h.Append("Content-Type: ").Append(ctype).Append("\r\n");
            h.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            h.Append("Cache-Control: no-store\r\n\r\n");
            byte[] hb = Encoding.ASCII.GetBytes(h.ToString());
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
            return true;
        }

        // ---------------------------------------------------------------- 페이지
        /// <summary>실측: 빈 줄 여러 개 뒤에 meta refresh 한 줄(64바이트).</summary>
        private static string Redirect(string to)
        {
            return "\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n<meta http-equiv='Refresh' content='0; URL=" + to + "'>";
        }

        private static string LoginPage()
        {
            return "<html><head><title>넷커스터마이즈 주간보고</title>"
                 + "<script src=\"SST/SST.js\"></script><script src=\"SST/Login.js\"></script>"
                 + "<SCRIPT LANGUAGE=javascript>\r\n"
                 + "function goLogin(){var form=document.form;"
                 + "if(form.id.value==''){alert('아이디를 입력하세요.');return;}"
                 + "if(form.pass.value==''){alert('패스워드를 입력하세요.');return;}"
                 + "if(form.id.value.length>20){alert('아이디 길이가 범위를 초과하였습니다.');return;}"
                 + "document.form.action='loginRSA.jsp';"
                 + "if(Encrypt('SST/PublicKey.xml')==true){document.form.submit();}}\r\n"
                 + "</SCRIPT></head><body>"
                 + "<form name=\"form\" method=\"post\" action=\"loginRSA.jsp\">"
                 + "<input type=\"hidden\" name=\"UserName_Enc\" id=\"UserName_Enc\" />"
                 + "<input type=\"hidden\" name=\"Password_Enc\" id=\"Password_Enc\" />"
                 + "<table><tr><td>ID :</td><td><input type=\"text\" id=\"id\" name=\"id\" value=\"\" size=\"15\"></td></tr>"
                 + "<tr><td>Password :</td><td><input type=\"password\" id=\"pass\" name=\"pass\" value=\"\" size=\"15\"></td></tr>"
                 + "<tr><td colspan=2><a href=\"javascript:goLogin()\">로그인</a></td></tr></table>"
                 + "</form><p style='color:#c00'>[목업] FileCrypt 테스트용 가짜 근태관리입니다.</p></body></html>";
        }

        // 실제는 RSA(PublicKey.xml). 목업은 서버가 풀 수 있게 base64 로만 감싼다 - 흐름(숨은 칸 채우기, pass 덮어쓰기)만 같다.
        private const string SstJs = "function LoadPublicKey(u){window.__mockKey=u;}\r\n"
            + "function EncryptRsa(v){try{return 'MOCK:'+btoa(unescape(encodeURIComponent(v)));}catch(e){return null;}}\r\n";
        private const string LoginJs = "function Encrypt(url){var passwd=document.form.pass;var Password_Enc=document.getElementById('Password_Enc');"
            + "if(passwd.value==''){alert('암호화 전송할 아이디 혹은 패스워드를 입력해 주십시오.');return false;}"
            + "try{LoadPublicKey(url);Password_Enc.value=EncryptRsa(passwd.value);if(Password_Enc.value==null){alert('암호화에 실패했습니다.');return false;}"
            + "passwd.value='xxxxxxxxx';}catch(e){alert('암호화에 실패했습니다..');return false;}return true;}\r\n";
        private const string PublicKeyXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<RSAKeyValue>\r\n\t<Modulus>MOCK</Modulus>\r\n\t<Exponent>AQAB</Exponent>\r\n</RSAKeyValue>";

        private static string BoardPage(string user)
        {
            return "<html><head><title>넷커스터마이즈 주간보고</title></head><body>"
                 + "<form name=form method=post><input type=hidden name=word_code><input type=hidden name=n_code>"
                 + "<input type=hidden name=s_code><input type=hidden name=c_code><input type=hidden name=table_code>"
                 + "<input type=hidden name=id value='" + Html(user) + "'></form>[목업] 게시판</body></html>";
        }

        private static readonly string[][] StatusOptions =
        {
            new[] { "", "선택" }, new[] { "1", "정근" }, new[] { "2", "야근" }, new[] { "3", "특근" },
            new[] { "4", "외근" }, new[] { "5", "출장" }, new[] { "6", "휴가" }, new[] { "12", "반차" },
            new[] { "7", "조퇴" }, new[] { "9", "지각" }, new[] { "10", "지각+야근" }, new[] { "11", "병가" }
        };

        private string WorkPage(string user, DateTime d)
        {
            MockDay day;
            lock (_gate) { if (!_days.TryGetValue(user + "|" + d.ToString("yyyy-MM-dd"), out day)) day = new MockDay(); }

            var sb = new StringBuilder();
            sb.Append("<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=euc-kr\"><title>넷커스터마이즈 주간보고</title>");
            sb.Append("<script language=\"JavaScript\">\r\nfunction Bmodify(){if(document.form.status.value==''){alert('근태를 선택하세요.');return;}");
            // 실제 페이지와 같은 모양: 그 주 나머지 날 합계를 숫자로 박아 둔다(서버가 페이지마다 계산).
            sb.Append("var todayWorkingTime = getWorkingTime(document.form.status.value, document.form.overtime.selectedIndex);");
            sb.AppendFormat("var totalWorkingTime = todayWorkingTime + {0};", WeekOthers(user, d));
            sb.Append("if(totalWorkingTime > 52){alert('근무시간은 주 52시간을 초과할 수 없습니다. Total='+totalWorkingTime);return;}");
            sb.AppendFormat("document.form.action='pjm_work_view.jsp?go=write&table=report_tbl&y={0}&m={1}&d={2}&id={3}';document.form.submit();}}\r\n",
                            d.Year, d.Month, d.Day, Uri.EscapeDataString(user));
            sb.Append("function getWorkingTime(s,o){if(s=='3'||s=='6'||s=='11')return o;if(s=='12')return 4+o;return 8+o;}\r\n");
            sb.Append("function syncNight(){}\r\nfunction Bback(){history.go(-1);}\r\n</script></head><body>");
            // 실제 페이지처럼 form 이 table 안에 있다(옛 마크업) - NetcusService 가 getElementsByName 을 쓰는 이유.
            sb.Append("<table><tr><td><form name='form' ENCTYPE='multipart/form-data' method='post' >");
            sb.AppendFormat("<input type=\"hidden\" name=\"dbstatus\" value=\"{0}\">", day.Exists ? "1" : "0");
            sb.Append("</td></tr><tr><td>근태 <select name=\"status\" onchange='javascript:syncNight()'>");
            foreach (var o in StatusOptions)
                sb.AppendFormat("<option value=\"{0}\"{1}>{2}</option>", o[0], o[0] == day.Status ? " selected" : "", o[1]);
            sb.Append("</select> 초과 <select name=\"overtime\">");
            for (int i = 0; i <= 11; i++)
                sb.AppendFormat("<option value=\"{0}\"{1}>{2}</option>", i, i == day.Overtime ? " selected" : "", i == 0 ? "선택" : "+" + i + "시간");
            sb.Append("</select></td></tr><tr><td>");
            sb.AppendFormat("<input type=text name=sdate size='10' value='{0:yyyy-MM-dd}' readonly>", d);
            sb.Append("<textarea name='content' rows='10' cols='60'>").Append(Html(day.Content)).Append("</textarea>");
            sb.Append("<input type=\"button\" name=\"wr\" value=\"수정\" onclick='javascript:Bmodify()'>");
            sb.Append("<input type=\"button\" name=\"re\" value=\"다시\" onclick='javascript:document.form.reset()'>");
            sb.Append("<input type=\"button\" name=\"ca\" value=\"취소\" onclick='javascript:Bback()'>");
            sb.Append("</form></td></tr></table><p style='color:#c00'>[목업] ").Append(Html(user)).Append(" / ").Append(d.ToString("yyyy-MM-dd")).Append("</p></body></html>");
            return sb.ToString();
        }

        private static string Html(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }

    /// <summary>아주 작은 HTTP/1.1 요청 파서. 목업에 필요한 만큼만.</summary>
    internal sealed class HttpReq
    {
        public string Method, Target, Path;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        private Dictionary<string, string> _query;

        public static HttpReq Read(Stream s)
        {
            var head = new MemoryStream();
            int state = 0;
            while (state < 4)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                if ((state == 0 || state == 2) && b == '\r') state++;
                else if ((state == 1 || state == 3) && b == '\n') state++;
                else state = (b == '\r') ? 1 : 0;
                if (head.Length > 65536) return null;
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var r = new HttpReq { Method = first[0].ToUpperInvariant(), Target = first[1] };
            int q = r.Target.IndexOf('?');
            r.Path = q >= 0 ? r.Target.Substring(0, q) : r.Target;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) r.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            int len;
            if (int.TryParse(r.Header("Content-Length"), out len) && len > 0)
            {
                r.Body = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int k = s.Read(r.Body, got, len - got);
                    if (k <= 0) return null;
                    got += k;
                }
            }
            return r;
        }

        public string Header(string name) { string v; return Headers.TryGetValue(name, out v) ? v : ""; }

        public string Query(string name)
        {
            if (_query == null)
            {
                _query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int q = Target.IndexOf('?');
                if (q >= 0)
                    foreach (var kv in Target.Substring(q + 1).Split('&'))
                    {
                        int e = kv.IndexOf('=');
                        if (e > 0) _query[Uri.UnescapeDataString(kv.Substring(0, e))] = Uri.UnescapeDataString(kv.Substring(e + 1));
                    }
            }
            string v;
            return _query.TryGetValue(name, out v) ? v : null;
        }

        public string Cookie(string name)
        {
            foreach (var p in Header("Cookie").Split(';'))
            {
                string t = p.Trim();
                int e = t.IndexOf('=');
                if (e > 0 && t.Substring(0, e) == name) return t.Substring(e + 1);
            }
            return null;
        }

        public static Dictionary<string, string> ParseUrlEncoded(byte[] body, Encoding enc)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in Encoding.ASCII.GetString(body).Split('&'))
            {
                int e = kv.IndexOf('=');
                if (e <= 0) continue;
                d[Decode(kv.Substring(0, e), enc)] = Decode(kv.Substring(e + 1), enc);
            }
            return d;
        }

        private static string Decode(string s, Encoding enc)
        {
            // %XX 를 바이트로 모은 뒤 페이지 인코딩(euc-kr)으로 푼다. '+' 는 공백.
            var bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '+') bytes.Add((byte)' ');
                else if (c == '%' && i + 2 < s.Length) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
                else bytes.Add((byte)c);
            }
            return enc.GetString(bytes.ToArray());
        }

        public static Dictionary<string, string> ParseMultipart(byte[] body, string contentType, Encoding enc)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            var m = Regex.Match(contentType ?? "", "boundary=\"?([^\";]+)\"?");
            if (!m.Success) return d;
            // 바이트를 그대로 문자로 옮겨(28591) 경계를 찾고, 값은 다시 바이트로 되돌려 euc-kr 로 푼다.
            var latin = Encoding.GetEncoding(28591);
            string all = latin.GetString(body);
            string b = "--" + m.Groups[1].Value;
            foreach (var part in all.Split(new[] { b }, StringSplitOptions.None))
            {
                int sep = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (sep < 0) continue;
                var nm = Regex.Match(part.Substring(0, sep), "name=\"([^\"]*)\"");
                if (!nm.Success) continue;
                string raw = part.Substring(sep + 4);
                if (raw.EndsWith("\r\n")) raw = raw.Substring(0, raw.Length - 2);
                d[nm.Groups[1].Value] = enc.GetString(latin.GetBytes(raw));
            }
            return d;
        }
    }
}
