using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WV = Microsoft.Web.WebView2.Wpf;
using static FileCrypt.NetcusText;

namespace FileCrypt
{
    // NetcusService가 호스트(MainWindow)에 요구하는 최소 의존 — 메인 웹뷰 실행/회신, 로그, 보조 WebView2 환경, 데이터 폴더, 디스패처.
    internal interface INetcusHost
    {
        System.Windows.Threading.Dispatcher Dispatcher { get; }
        CoreWebView2Environment? Env { get; }         // 초기화 후 할당되므로 라이브 getter(프로퍼티)
        string DataDir { get; }
        void Eval(string js);                          // 메인 웹뷰 ExecuteScriptAsync(dispatcher 마샬 + try/catch)
        void Reply(string reqId, object payload);      // window.__hostReply(reqId, payload)
        void Log(string msg);

        //  보고 기록 저장(설계 §5.9). 반환값이 없는 이유 — 이 시점에는 **이미 사이트로 보고서가 나갔다**.
        //  저장 실패로 전송을 실패처럼 보이게 하면 사용자가 재전송하게 되고 그게 더 큰 사고다.
        //  실패는 ReportDb 안에서 잡아 로그로 남는다(조용히 삼키는 것이 아니다).
        void SaveDailyReport(int y, int m, int d, string status, int overtime, string content, string hoursJson);
        void SaveWeeklyReport(string sdate, string edate, string subject, string content, string endwork, string planwork);
    }

    // 회사 일간보고(netcus pjm) 자동 전송. 보조 WebView2(메인과 같은 환경=쿠키/세션 공유)로
    // login.htm→goLogin() 로그인 → pjm_work_view.jsp?y&m&d&id 이동 → status/overtime/content 채움 →
    // (실제 제출) Bmodify()로 서버 POST. 자격증명은 DPAPI로 로컬 암호화 저장.
    // Phase1(행위보존 추출): MainWindow.partial(Netcus.cs)에서 전용 서비스로 이관. 로직·타임아웃·문자열·흐름 동일.
    internal sealed class NetcusService
    {
        private readonly INetcusHost _host;
        private WV.WebView2? _w2;                      // 보조 WebView2(가시 창)
        private Window? _w2win;
        private bool _ncBusy;                   // netcus 단일 실행 가드("한 번에 하나" — 단일 _w2 레이스 방지). 6개 op 공유.
        private bool _probeOpen;                // 캡처 창이 열려 있는지(열려 있는 동안 _ncBusy 계속 보유)
        private bool _ncProbeFolderOpened;      // 최초 캡처 저장 시 저장 폴더 1회만 자동 열기

        public NetcusService(INetcusHost host) { _host = host; }

        // ----- 호스트 어댑터(기존 멤버 시그니처 유지 → 이관한 본문 무변경) -----
        private void JsCall(string js) => _host.Eval(js);
        private void GitReply(string reqId, object payload) => _host.Reply(reqId, payload);
        private void Log(string msg) => _host.Log(msg);
        private System.Windows.Threading.Dispatcher Dispatcher => _host.Dispatcher;

        private string CredFile => Path.Combine(_host.DataDir, "netcus.cred");

        // ----- 공개 API(원시값 입력 — MainWindow는 Req 타입을 몰라도 됨) -----
        // status: 사이트 근태 코드값 또는 ""(=미기록 — 사이트의 기존 근태를 건드리지 않는다. NetcusReq.Status 주석 참조)
        public Task SubmitDaily(int y, int m, int d, string status, int overtime, string content, bool dryRun, string hoursJson = "")
            => NetcusSubmit(new NetcusReq { Y = y, M = m, D = d, Status = status, Overtime = overtime, Content = content, DryRun = dryRun, HoursJson = hoursJson });

        public Task WeekFill(string sdate, string edate, string subject, string content, string endwork, string planwork)
            => NetcusWeekFill(new NetcusWeekReq { Sdate = sdate, Edate = edate, Subject = subject, Content = content, Endwork = endwork, Planwork = planwork });

        public Task WeekMerge(string reqId, string from, string to) => NetcusWeekMerge(reqId, from, to);
        public Task WeeklyRangeRead(string reqId, string from, string to) => NetcusWeeklyRangeRead(reqId, from, to);
        public Task Probe() => NetcusProbeStart();
        public void SaveCreds(string id, string pw) => NetcusSaveCreds(id, pw);
        public void SendCredsState() => NetcusSendCredsState();

        // netcus 단일 실행 가드의 읽기 전용 노출. 로그인 핸들러가 인증을 시도하기 '전에' 이걸 보고
        // "다른 회사 시스템 작업이 진행 중"으로 회신한다 — 진행 중을 'ID/비밀번호 오류'로 오표시하던 결함 방지.
        public bool IsBusy => _ncBusy;

        /// <summary>
        /// FileCrypt 추가분. true 면 전송 창도 최소화·비활성으로 띄운다.
        ///
        /// 캘린더는 전송 결과를 사람이 눈으로 확인하도록 가시 창을 쓴다(하루 한 번 쓰는 기능).
        /// FileCrypt 는 날짜 수만큼 자동으로 돌기 때문에, 날짜마다 창이 떠서 포커스를 가져가면
        /// 그 동안 다른 일을 전혀 못 한다. 읽기 경로가 이미 쓰고 있는 background 모드를 전송에도 쓴다.
        /// </summary>
        public bool QuietWindows { get; set; }

        // ================================================================================
        // 사용자 로그인(위젯 진입) — netcus를 '신원 확인'에만 쓴다. 보고 전송과 같은 인증 판정을 공유한다.
        //   ★ 이 경로는 로그인 게이트에서 사용자가 직접 [로그인]을 눌렀을 때만 돈다.
        //     부팅(세션 복원)에서는 절대 호출되지 않는다 — 부팅 경로에 netcus 접속은 하나도 없어야 한다.
        // ================================================================================

        // ID/비밀번호로 netcus 로그인 성공 여부만 반환. 저장·부수효과 없음(저장은 SaveCredsForLogin이 따로 한다).
        // ★ 창은 반드시 background(최소화·비활성) — 포그라운드로 띄우면 로그인할 때마다 화면 중앙에
        //   netcus 창이 떴다 사라진다(1차 구현이 폐기된 원인). 주간범위 읽기와 같은 방식이다.
        // ※ 비밀번호는 로그·회신 어디에도 남기지 않는다.
        public async Task<bool> LoginVerify(string id, string pw)
        {
            if (_ncBusy) { Log("사용자 로그인: 다른 netcus 작업이 진행 중 — 거부"); return false; }
            _ncBusy = true; NetcusBusy(true);
            Action? detach = null;
            try
            {
                Log("사용자 로그인 확인 시작: " + id);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool warm = _w2 != null && _w2.CoreWebView2 != null;   // 재사용인가 신규 생성인가
                await EnsureW2(background: true);   // 최소화·비활성 창 — 포커스를 뺏지 않는다
                Log($"  WebView2 준비 {sw.ElapsedMilliseconds}ms ({(warm ? "재사용" : "신규 생성")})");
                // EnsureW2가 실패하면 CoreWebView2가 null일 수 있다. '!'로 덮으면 여기서 NRE가 나고
                // 로그인 예외로만 보인다 — 원인이 드러나는 실패 경로를 명시한다.
                var cw = _w2?.CoreWebView2;
                if (cw == null) { Log("사용자 로그인 확인 실패 — WebView2를 준비하지 못했습니다."); return false; }
                detach = AttachDialogAutoAccept(cw, "userlogin");   // 레거시 pjm alert() 자동 수락(최소화 창 무한대기 방어)
                NetcusStatus("userlogin", "login");
                // ★ navTimeoutMs=4000: 실패 시 netcus는 페이지 이동을 하지 않아 기본 15초를 꽉 기다렸다(실측 15.7초).
                //   그 대기 결과는 애초에 판정에 쓰이지 않는다(판정은 이어지는 work_view 도달 폴링이 한다) → 줄여도 정확도 무변경.
                // allowSessionReuse:false — 자격증명 자체를 확인하는 자리다. 살아 있는 세션으로 통과시키면
                // 비밀번호가 바뀐 것을 못 잡는다(FileCrypt 추가분).
                bool ok = await NetcusLoginVerify(cw, id, pw, 4000, false);   // 보호 페이지 도달로 판정(공유 헬퍼)
                sw.Stop();
                // 소요시간을 남긴다 — 로그인 지연 회귀를 로그만으로 관측할 수 있게.
                Log("사용자 로그인 확인 결과: " + (ok ? "성공" : "실패") + " (" + id + ") "
                    + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "초");
                return ok;
            }
            catch (Exception ex) { Log("사용자 로그인 확인 예외: " + ex); return false; }
            finally
            {
                detach?.Invoke();
                try { Dispatcher.Invoke(() => { try { _w2win?.Close(); } catch { } }); } catch { }   // 확인용 창은 남기지 않는다
                _ncBusy = false; NetcusBusy(false);
            }
        }

        // 로그인 성공 직후 자격증명 저장 — 보고서(netcus 전송)가 이 자격을 그대로 쓴다.
        // ★ NetcusSaveCreds를 쓰지 않는다: 그쪽은 저장 직후 NetcusValidateCreds로 재검증(=또 창을 띄워 재로그인)을 건다.
        //   여기 도달했다는 것 자체가 방금 LoginVerify로 검증에 성공했다는 뜻이라 재검증이 중복이고, 창까지 뜬다.
        // ★ valid=true로 쓴다 — 이게 없으면(null) 보고 전송이 자격을 '미검증'으로 보고 사용자에게 다시 묻는다.
        // ★ 되읽어 확인된 경우에만 성공을 반환한다 — "저장했다"는 로그는 되읽기 없이는 거짓말일 수 있다(실관측 2회).
        //   호출측(로그인 핸들러)은 실패 시 세션까지 함께 지워 반쪽 상태를 남기지 않는다.
        public (bool ok, string msg) SaveCredsForLogin(string id, string pw)
        {
            const string Fail = "로그인 정보를 이 PC에 저장하지 못했습니다 — 다시 시도하세요.";
            try
            {
                Directory.CreateDirectory(_host.DataDir);
                string enc = pw.Length > 0 ? Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(pw))) : "";
                File.WriteAllText(CredFile, JsonSerializer.Serialize(new { id, pw = enc, valid = (bool?)true }), Encoding.UTF8);

                // 되읽기 확인 — id가 그대로고 비밀번호가 복호화되는지, valid까지 실제로 기록됐는지 본다.
                var (bid, bpw) = NetcusLoadCreds();
                if (!string.Equals(bid, id, StringComparison.Ordinal) || (pw.Length > 0 && bpw.Length == 0))
                {
                    Log("netcus 자격증명 저장 확인 실패(되읽기 불일치): " + CredFile);
                    return (false, Fail);
                }
                if (NetcusCredsValid() != true)
                {
                    Log("netcus 자격증명 저장 확인 실패(valid 미기록): " + CredFile);
                    return (false, Fail);
                }
                Log("netcus 자격증명 저장(로그인 연동, 검증됨): " + id);
                NetcusSendCredsState();
                return (true, "");
            }
            // ※ ex.Message만 남긴다 — 예외 문자열에 비밀번호가 실려 로그로 새지 않게.
            catch (Exception ex) { Log("netcus 자격증명 저장 실패(로그인 연동): " + ex.Message + " / " + CredFile); return (false, Fail); }
        }

        // 로그아웃 — netcus 자격증명 파일 삭제 + 설정 화면 상태 갱신.
        // (지우지 않으면 로그아웃이 이름만 지우고 보고 전송은 계속 그 사람 자격으로 돈다.)
        public void ClearCredsForLogout()
        {
            try
            {
                if (File.Exists(CredFile)) File.Delete(CredFile);
                Log("netcus 자격증명 삭제(로그아웃)");
            }
            catch (Exception ex) { Log("netcus 자격증명 삭제 실패: " + ex.Message + " / " + CredFile); }
            NetcusSendCredsState();
        }

        private sealed class NetcusReq
        {
            public int Y, M, D, Overtime;
            // ★ Status 빈 문자열("") = 미기록. 웹은 fields.status에 null을 싣고, 경계(MainWindow.GetStr)가
            //   JSON null을 ""로 환원한다 — 그래서 '빈 문자열 = 미기록'을 웹→호스트 공통 규약으로 고정한다.
            //   미기록이면 폼의 status를 건드리지 않고, 제출 때는 페이지의 현재 값을 되싣는다(사이트 기존 근태 보존).
            public string Status = "", Content = "";
            //  웹이 실어 보낸 과제별 시간 배열(JSON 문자열). NetcusService 는 **해석하지 않고 실어 나를 뿐**이다 —
            //  보고 페이로드를 아는 건 호스트(MainWindow)이고, 여기에 파싱을 두면 통신 책임과 섮인다.
            public string HoursJson = "";
            public bool DryRun = true;
        }

        private sealed class NetcusWeekReq
        {
            public string Sdate = "", Edate = "", Subject = "", Content = "", Endwork = "", Planwork = "";
        }

        // ----- 자격증명 (DPAPI, CurrentUser) -----
        private void NetcusSaveCreds(string id, string pw)
        {
            try
            {
                // 비밀번호 칸을 비워서 저장하면 기존 비밀번호 유지(실수로 지워지는 것 방지)
                string enc;
                if (pw.Length > 0) enc = Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(pw)));
                else
                {
                    enc = "";
                    try { if (File.Exists(CredFile)) { using var d = JsonDocument.Parse(File.ReadAllText(CredFile, Encoding.UTF8)); if (d.RootElement.TryGetProperty("pw", out var p)) enc = p.GetString() ?? ""; } } catch { }
                }
                File.WriteAllText(CredFile, JsonSerializer.Serialize(new { id, pw = enc, valid = (bool?)null }), Encoding.UTF8);   // 새 저장=미검증(null) — 직후 NetcusValidateCreds가 채움
                Log("netcus 자격증명 저장");
                NetcusSendCredsState();
                // 저장 후 실제 로그인 검증(보이는 창) — 성공/실패 표시
                var (eid, epw) = NetcusLoadCreds();
                if (string.IsNullOrEmpty(eid) || string.IsNullOrEmpty(epw)) JsCredsResult(false, "ID와 비밀번호를 모두 입력하세요.");
                else _ = NetcusValidateCreds(eid, epw);
            }
            catch (Exception ex) { Log("netcus 자격증명 저장 실패: " + ex.Message); JsCredsResult(false, "저장 오류: " + ex.Message); }
        }

        // 로그인 + '양성' 인증 확정(5개 netcus 흐름 공유). login.htm→goLogin 후, 보호 페이지(오늘자 work_view)로
        // 이동해 인증 전용 요소(content textarea) 존재를 확인한다. 실패면 netcus가 로그인 폼으로 되돌리므로
        // 비밀번호칸이 '안정적으로' 존재 → 즉시 실패로 판정.
        // ※ 기존 login.htm '비번칸 소멸' 폴링은 실패 리다이렉트 찰나의 공백에 오탐 → 틀린 자격이 성공으로 통과해
        //    읽기 무한대기·저장검증 OK오탐·실패한 창 방치를 유발하던 버그. 이 헬퍼가 그 판정을 목적지 기반 양성확인으로 대체.
        // navTimeoutMs: goLogin() 후 '페이지 이동 1회'를 기다리는 상한. 이 대기 결과는 판정에 쓰지 않으므로(아래 폴링이 판정)
        //   실패가 잦은 경로(사용자 로그인)만 짧게 줄일 수 있다. 기존 호출부는 기본값 15000 유지 = 동작 무변경.
        // ※ 제출이 아예 안 된 경우(goLogin이 submit에 닿지 못함)에는 그 상한마저 기다리지 않는다 —
        //   주입 스크립트가 제출 여부를 돌려주므로 '기다릴 이동이 없다'는 걸 대기 전에 안다(실측 4.5초 → 즉시).
        //   제출된 경우("1")의 흐름은 종전과 완전히 동일하다 = 성공 경로 무변경. 공유 호출부 6곳도 이득만 받는다.
        /// <summary>
        /// FileCrypt 추가분. 지금 세션이 아직 살아 있는지만 본다(로그인 시도는 하지 않는다).
        /// 보호 페이지를 열어 content 요소가 있으면 인증된 것, 비밀번호칸이 보이면 아닌 것.
        /// 판정은 짧게 끝낸다 — 여기서 오래 기다리면 '로그인 아낀 시간' 을 도로 까먹는다.
        /// </summary>
        private async Task<bool> NetcusSessionAlive(CoreWebView2 cw, string id)
        {
            try
            {
                var t = DateTime.Now;
                if (!await NavTo(cw, $"https://www.netcus.com/pjm/pjm_work_view.jsp?y={t.Year}&m={t.Month}&d={t.Day}&id={Uri.EscapeDataString(id)}"))
                    return false;

                for (int i = 0; i < 8; i++)   // ~2초
                {
                    var st = (await cw.ExecuteScriptAsync(
                        "(function(){try{"
                        + "if(document.getElementsByName('content')[0])return 1;"
                        + "if(document.querySelector('input[type=password]'))return -1;"
                        + "return 0;}catch(e){return 0;}})()")).Trim();
                    if (st == "1") return true;
                    if (st == "-1") return false;
                    await Task.Delay(250);
                }
            }
            catch { }
            return false;   // 알 수 없으면 정상 로그인 경로로 간다(보수적)
        }

        private async Task<bool> NetcusLoginVerify(CoreWebView2 cw, string id, string pw, int navTimeoutMs = 15000,
                                                   bool allowSessionReuse = true)
        {
            // 단계별 소요를 남긴다 — 실사용에서 '실패가 느리다'는 보고가 반복돼, 어느 구간이 먹는지
            // 추정하지 않고 로그로 확정하기 위함이다(네트워크 왕복 자체는 실측 47ms로 무시할 수준).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long tA, tB, tC;

            // ★ FileCrypt 추가분(캘린더 원본과의 유일한 동작 차이):
            //   이미 인증된 세션이면 로그인 POST 를 건너뛴다.
            //   이유 — 여기서는 날짜 하나당 작업이 하나씩 일어난다. 15일치를 한 번에 돌리면
            //   짧은 시간에 로그인이 열댓 번 몰리고, 사이트가 그걸 막아 중간에 ID/비밀번호
            //   오류로 떨어진다(실측). 세션은 같은 WebView2 프로필에 남아 있으므로 재로그인은 불필요하다.
            //   ※ 판정 기준을 낮추지 않는다 — 아래 본 흐름이 쓰는 것과 똑같이 '보호 페이지의
            //     content 요소가 있는가' 로만 본다. (비번칸 소멸 폴링 같은 약한 기준은 쓰지 않는다.)
            if (allowSessionReuse && await NetcusSessionAlive(cw, id))
            {
                Log($"  로그인 생략: 이미 인증된 세션 ({sw.ElapsedMilliseconds}ms)");
                return true;
            }

            await NavTo(cw, "https://www.netcus.com/pjm/login.htm");
            tA = sw.ElapsedMilliseconds;
            // ★ 리스너는 '제출 전에' 붙인다 — 제출 후에 붙이면 그 사이 끝난 이동을 놓쳐 성공 경로가 상한까지 늘어진다.
            var nav = NavOnce(cw, navTimeoutMs);
            // 주입 스크립트가 '제출까지 갔는지'를 돌려준다(1=제출 · 0=미제출 · "ERR:…"=예외).
            //   근거(실측): 실패는 4.5s로 상한과 정확히 일치했고 성공은 0.6s였다. 즉 실패 때는 '이동이 아예 없어'
            //   기다릴 이벤트가 없다 — goLogin()은 Encrypt()가 true가 아니면 document.form.submit()에 닿지 않는다.
            //   '이동 실패'라는 이벤트는 존재하지 않으므로(안 일어난 일에는 이벤트가 없다) 제출 여부를 페이지가 직접 알려준다.
            // ※ document.form.submit은 이 폼(name="submit"인 input 존재)에서도 네이티브 메서드다 —
            //   goLogin()이 그걸 호출해 실제 로그인이 성공해 왔다는 사실이 그 증거다. 감쌌다가 finally에서 원복한다.
            // ※ 예외를 삼키지 않는다(기존 빈 catch는 원인을 통째로 지웠다). 단, 비밀번호는 반환값·로그 어디에도 싣지 않는다.
            string res = (await cw.ExecuteScriptAsync(
                "(function(){try{"
                + $"document.form.id.value={J(id)};document.form.pass.value={J(pw)};"
                + "var submitted=false;var orig=document.form.submit;"
                + "document.form.submit=function(){submitted=true;return orig.apply(this,arguments);};"
                + "try{goLogin();}finally{try{document.form.submit=orig;}catch(_){}}"
                + "return submitted?1:0;"
                + "}catch(e){return 'ERR:'+((e&&e.message)?e.message:String(e));}})()")).Trim();
            // ExecuteScriptAsync는 JSON을 돌려준다(아래 판정 폴링이 1/-1/0을 그렇게 받는다). 문자열은 따옴표까지 포함된다.
            string errMsg = "";
            if (res.Length > 1 && res[0] == '"') { try { errMsg = JsonSerializer.Deserialize<string>(res) ?? ""; } catch { errMsg = ""; } }
            bool scriptErr = errMsg.StartsWith("ERR:", StringComparison.Ordinal);
            string mark = res == "1" ? "1" : scriptErr ? "ERR" : res == "0" ? "0" : "?";   // ?=알 수 없는 반환값 → 보수적으로 기존 경로
            long tS = sw.ElapsedMilliseconds;
            if (res == "0" || scriptErr)
            {
                // ★ nav를 기다리지 않는다. 제출이 없었으니 이동도 없고, 기다리면 상한을 통째로 버린다(실측 4.5초).
                //   NavOnce는 상한이 지나면 스스로 핸들러를 떼므로 방치해도 누수가 없다(결과만 버려진다).
                _ = nav;
                Log($"  로그인 구간: login.htm {tA}ms · 제출 {tS - tA}ms · 제출={mark} — 이동 대기 생략");
                Log(scriptErr
                    ? "  로그인 스크립트 예외: " + errMsg.Substring(4) + " — 즉시 실패 처리"
                    : "  로그인 제출 안 됨(goLogin 이 submit 까지 가지 않음) — 즉시 실패 처리");
                return false;
            }
            // 여기부터는 기존 경로 그대로 — 제출됨("1")과 알 수 없는 반환값("?") 둘 다 대기 후 도달 폴링으로 판정한다.
            await nav;
            tB = sw.ElapsedMilliseconds;
            // 보호 페이지(오늘자 work_view)로 이동 후 안정 상태에서 판정.
            var t = DateTime.Now;
            await NavTo(cw, $"https://www.netcus.com/pjm/pjm_work_view.jsp?y={t.Year}&m={t.Month}&d={t.Day}&id={Uri.EscapeDataString(id)}");
            tC = sw.ElapsedMilliseconds;
            Log($"  로그인 구간: login.htm {tA}ms · 제출대기 {tB - tA}ms(상한 {navTimeoutMs}) · work_view {tC - tB}ms · 제출={mark}");
            for (int i = 0; i < 16; i++)   // ~4s(제출 사후검증과 동일 규모). 타임아웃=불확실→안전하게 실패
            {
                var st = (await cw.ExecuteScriptAsync(
                    "(function(){try{"
                    + "if(document.getElementsByName('content')[0])return 1;"          // 인증됨: work_view content textarea
                    + "if(document.querySelector('input[type=password]'))return -1;"   // 로그인 폼 리다이렉트 = 실패
                    + "return 0;}catch(e){return 0;}})()")).Trim();
                if (st == "1") return true;
                if (st == "-1") return false;
                await Task.Delay(250);
            }
            return false;   // 불확실한 인증으로 읽기/쓰기 진행 금지
        }

        // 저장된 자격증명으로 실제 로그인해 성공/실패 검증(보조 WebView2, 가시 창)
        private async Task NetcusValidateCreds(string id, string pw)
        {
            if (_ncBusy) { JsCredsResult(false, "다른 netcus 작업이 진행 중입니다 — 잠시 후 다시 시도하세요."); return; }
            _ncBusy = true; NetcusBusy(true);
            Action? detach = null;
            try
            {
                JsCredsCheck("로그인 확인 중…");
                await EnsureW2();
                var cw = _w2!.CoreWebView2;
                detach = AttachDialogAutoAccept(cw, "validate");   // alert 시 가시창 모달 블로킹 방지(기존 누락 수정)
                NetcusStatus("validate", "login");
                bool ok = await NetcusLoginVerify(cw, id, pw, 15000, false);   // 명시적 검증 — 세션 재사용 금지(FileCrypt 추가분)
                SetCredsValid(ok);   // 명시적 검증 결과만 영속(op 로그인 성패로는 갱신 안 함) → 이후 op의 전제 게이트
                JsCredsResult(ok, ok ? "로그인 확인됨 — 자격증명 OK" : "로그인 실패 — ID/비밀번호를 확인하세요");
            }
            catch (Exception ex) { Log("netcus 자격증명 검증 예외: " + ex); JsCredsResult(false, "검증 오류: " + ex.Message); }
            finally
            {
                detach?.Invoke();
                // 검증은 결과만 설정창에 표시하면 되므로 성공/실패 무관 확인용 창은 닫는다.
                try { await Task.Delay(700); } catch { }
                try { Dispatcher.Invoke(() => { try { _w2win?.Close(); } catch { } }); } catch { }
                _ncBusy = false; NetcusBusy(false);
            }
        }

        private void JsCredsCheck(string m) { JsCall("window.__netcusCredsCheck && window.__netcusCredsCheck(" + JsonSerializer.Serialize(m) + ")"); }
        private void JsCredsResult(bool ok, string m) { Log("netcus creds validate: " + ok + " / " + m); JsCall("window.__netcusCredsResult && window.__netcusCredsResult(" + (ok ? "true" : "false") + "," + JsonSerializer.Serialize(m) + ")"); }

        private (string id, string pw) NetcusLoadCreds()
        {
            try
            {
                if (!File.Exists(CredFile)) return ("", "");
                using var d = JsonDocument.Parse(File.ReadAllText(CredFile, Encoding.UTF8));
                string id = d.RootElement.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
                string encPw = d.RootElement.TryGetProperty("pw", out var p) ? (p.GetString() ?? "") : "";
                string pw = encPw.Length > 0 ? Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(encPw))) : "";
                return (id, pw);
            }
            catch (Exception ex) { Log("netcus 자격증명 로드 실패: " + ex.Message); return ("", ""); }
        }

        // 마지막 '명시적 검증'(NetcusValidateCreds) 결과. true=확인됨, false=로그인 실패 확정, null=미검증(기존 파일 하위호환 포함).
        private bool? NetcusCredsValid()
        {
            try
            {
                if (!File.Exists(CredFile)) return null;
                using var d = JsonDocument.Parse(File.ReadAllText(CredFile, Encoding.UTF8));
                if (d.RootElement.TryGetProperty("valid", out var v))
                {
                    if (v.ValueKind == JsonValueKind.True) return true;
                    if (v.ValueKind == JsonValueKind.False) return false;
                }
                return null;
            }
            catch { return null; }
        }

        // cred 파일의 valid만 갱신(id·pw enc 보존). 명시적 검증에서만 호출 — 파일 없으면 no-op.
        private void SetCredsValid(bool ok)
        {
            try
            {
                if (!File.Exists(CredFile)) return;
                string id, enc;
                using (var d = JsonDocument.Parse(File.ReadAllText(CredFile, Encoding.UTF8)))
                {
                    id = d.RootElement.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
                    enc = d.RootElement.TryGetProperty("pw", out var p) ? (p.GetString() ?? "") : "";
                }
                File.WriteAllText(CredFile, JsonSerializer.Serialize(new { id, pw = enc, valid = (bool?)ok }), Encoding.UTF8);
            }
            catch (Exception ex) { Log("netcus 자격증명 valid 갱신 실패: " + ex.Message); }
        }

        private void NetcusSendCredsState()
        {
            var (id, pw) = NetcusLoadCreds();
            JsCall("window.__netcusCreds && window.__netcusCreds(" + JsonSerializer.Serialize(id) + "," + (string.IsNullOrEmpty(pw) ? "false" : "true") + ")");
        }

        // ----- 진행/결과 보고(메인 웹뷰로) -----
        private void NetcusProgress(string msg) { Log("netcus: " + msg); JsCall("window.__netcusProgress && window.__netcusProgress(" + JsonSerializer.Serialize(msg) + ")"); }
        private void NetcusResult(bool ok, string msg) { Log("netcus result: " + ok + " / " + msg); JsCall("window.__netcusResult && window.__netcusResult(" + (ok ? "true" : "false") + "," + JsonSerializer.Serialize(msg) + ")"); }

        // ----- 진행 프로토콜(진행바용 — JS 렌더는 다음 Phase). 텍스트 NetcusProgress와 병행(제거 안 함). -----
        private void NetcusBusy(bool on) { JsCall("window.__netcusBusy && window.__netcusBusy(" + (on ? "true" : "false") + ")"); }
        private void NetcusStatus(string op, string phase, int done = -1, int total = -1)
        {
            var payload = JsonSerializer.Serialize(new { op, phase, done, total });
            JsCall("window.__netcusStatus && window.__netcusStatus(" + payload + ")");
        }

        // 레거시 pjm alert()/confirm() 자동 수락 부착(공유) — 각 op가 자기 tag로 부착하고 detach()를 finally(또는 창 Closed)에서 호출.
        private Action AttachDialogAutoAccept(CoreWebView2 cw, string tag)
        {
            // ★ WebView2는 AreDefaultScriptDialogsEnabled=false 일 때만 ScriptDialogOpening 을 raise 한다.
            //   이 한 줄이 없어서 아래 핸들러가 7개 op 전부에서 한 번도 불리지 않았다(로그에 alert 0건).
            //   그 결과 netcus 의 alert() 가 최소화·화면 밖 창에 뜬 채 아무도 못 눌러 파서가 멈추고
            //   NavigationCompleted 가 오지 않아 각 op 가 타임아웃을 꽉 썼다(로그인 실패 실측 4.5초).
            // ★ detach 는 설정을 원복하고 핸들러를 뗀다. 단 이 원복은 '앞으로 로드될 문서'에만 먹는다 —
            //   AreDefaultScriptDialogsEnabled 는 문서 로드 시점에 스냅샷되기 때문이다(WebView2 실기 측정).
            //   그래서 호출부 계약은 "사용자에게 넘길 문서를 로드하기 전에 detach 를 끝낸다"이다.
            //   이미 로드된 문서에서 늦게 detach 하면 무음 취소가 아니라 '응답 주체 없는 영구 정지'가 된다
            //   (핸들러는 없고 기본 다이얼로그도 꺼진 상태) — 주간 채움의 confirm('제출하시겠습니까?')이 죽는다.
            bool prev = cw.Settings.AreDefaultScriptDialogsEnabled;
            try { cw.Settings.AreDefaultScriptDialogsEnabled = false; } catch { }
            void H(object? s, CoreWebView2ScriptDialogOpeningEventArgs ev) { Log("netcus(" + tag + ") alert: " + (ev.Message ?? "")); try { ev.Accept(); } catch { } }
            cw.ScriptDialogOpening += H;
            return () =>
            {
                try { cw.ScriptDialogOpening -= H; } catch { }
                try { cw.Settings.AreDefaultScriptDialogsEnabled = prev; } catch { }
            };
        }

        private async Task EnsureW2(bool background = false)
        {
            // background=true(주간병합 읽기): 포그라운드를 뺏지 않고 최소화 상태로만 띄운다.
            // (읽기 창을 Show+Activate하면 닫힐 때 최소화돼 있던 다른 창(탐색기 등)이 복원돼 바닥 위젯을 덮는 문제)
            if (_w2 != null && _w2.CoreWebView2 != null) { if (!background) { try { _w2win?.Show(); _w2win?.Activate(); } catch { } } return; }
            _w2win = new Window { Title = "회사 일간보고 전송 (확인용)", Width = 920, Height = 720, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            if (background)
            {
                // 활성화·포그라운드 없이 최소화 생성. 여기에 더해 '보이지 않게' 만드는 두 가지:
                //   ShowInTaskbar=false → 작업표시줄 버튼이 생기지 않는다(최소화 창 조각이 화면에 렌더되던 원인).
                //   화면 밖 좌표      → 어떤 이유로 복원되더라도 사용자 화면에 나타나지 않는다.
                // (Manual 이어야 Left/Top이 먹는다 — CenterScreen이면 무시된다)
                _w2win.ShowActivated = false;
                _w2win.WindowState = WindowState.Minimized;
                _w2win.ShowInTaskbar = false;
                _w2win.WindowStartupLocation = WindowStartupLocation.Manual;
                _w2win.Left = -32000; _w2win.Top = -32000;
            }
            _w2 = new WV.WebView2();
            _w2win.Content = _w2;
            _w2win.Closed += (_, __) => { try { _w2?.Dispose(); } catch { } _w2 = null; _w2win = null; };
            _w2win.Show();
            await _w2.EnsureCoreWebView2Async(_host.Env);
        }

        private Task<bool> NavTo(CoreWebView2 cw, string url)
        {
            var tcs = new TaskCompletionSource<bool>();
            void H(object? s, CoreWebView2NavigationCompletedEventArgs ev) { cw.NavigationCompleted -= H; tcs.TrySetResult(ev.IsSuccess); }
            cw.NavigationCompleted += H;
            _ = Task.Delay(20000).ContinueWith(_ => { try { cw.NavigationCompleted -= H; } catch { } tcs.TrySetResult(false); });
            cw.Navigate(url);
            return tcs.Task;
        }

        // 다음 NavigationCompleted 1회 대기(폼 submit로 인한 이동). 핸들러는 호출 전에 부착할 것.
        private Task<bool> NavOnce(CoreWebView2 cw, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>();
            void H(object? s, CoreWebView2NavigationCompletedEventArgs ev) { cw.NavigationCompleted -= H; tcs.TrySetResult(true); }
            cw.NavigationCompleted += H;
            _ = Task.Delay(timeoutMs).ContinueWith(_ => { try { cw.NavigationCompleted -= H; } catch { } tcs.TrySetResult(false); });
            return tcs.Task;
        }

        private async Task NetcusSubmit(NetcusReq req)
        {
            if (_ncBusy) { NetcusResult(false, "다른 netcus 작업이 진행 중입니다 — 잠시 후 다시 시도하세요."); return; }
            _ncBusy = true; NetcusBusy(true);
            CoreWebView2? cw = null;
            Action? detach = null;
            try
            {
                var (id, pw) = NetcusLoadCreds();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw)) { NetcusResult(false, "자격증명이 없습니다 — 설정 → 회사 일간보고에서 ID/비밀번호를 저장하세요."); return; }
                if (NetcusCredsValid() == false) { NetcusResult(false, "저장된 자격증명이 로그인 실패 상태입니다 — 설정에서 자격증명을 다시 확인하세요."); return; }   // 검증 실패 확정 → 로그인 시도 없이 차단(busy는 finally 해제)

                NetcusProgress("창 준비 중…");
                await EnsureW2(background: QuietWindows);   // FileCrypt 추가분: 조용히 돌 때는 포커스를 뺏지 않는다
                cw = _w2!.CoreWebView2;
                detach = AttachDialogAutoAccept(cw, "submit");

                NetcusProgress("로그인 중…");
                NetcusStatus("submit", "login");
                if (!await NetcusLoginVerify(cw, id, pw))
                {
                    try { Dispatcher.Invoke(() => { try { _w2win?.Close(); } catch { } }); } catch { }   // 실패한 창 남기지 않음
                    NetcusResult(false, "netcus 로그인 실패 — 설정에서 ID/비밀번호를 확인하세요.");
                    return;
                }

                NetcusProgress("일간보고 페이지 여는 중…");
                NetcusStatus("submit", "opening");
                string url = $"https://www.netcus.com/pjm/pjm_work_view.jsp?y={req.Y}&m={req.M}&d={req.D}&id={Uri.EscapeDataString(id)}";
                // ★ 미제출(테스트)이면 여기서 로드되는 문서를 그대로 사용자에게 넘긴다(아래 DryRun 분기가 창을 남긴다)
                //   → 넘길 문서를 로드하기 전에 자동수락을 끈다. AreDefaultScriptDialogsEnabled 는 문서 로드 시점에
                //   스냅샷되므로 로드 뒤 원복은 이미 늦고, 핸들러를 붙였다 떼면 '응답 주체 없는 정지'가 된다(실측).
                //   실제 제출(DryRun=false)은 바로 아래 go=write 자동 제출 구간에서 netcus 의 실패 alert() 를
                //   받아내야 하므로 여기서 끄지 않는다 — 그쪽은 제출이 끝난 뒤 되읽기 직전에 끈다.
                if (req.DryRun) { detach?.Invoke(); detach = null; }
                await NavTo(cw, url);

                // 폼 탐지 — <form>이 <table> 안에 있는 옛 마크업이라 document.form.* 접근이 안 될 수 있어 getElementsByName 사용. 로드 타이밍 대비 재시도.
                NetcusProgress("페이지 확인 중…");
                string probe = "false";
                for (int i = 0; i < 16; i++)
                {
                    probe = await cw.ExecuteScriptAsync("(function(){return !!(document.getElementsByName('status')[0] && document.getElementsByName('content')[0]);})()");
                    if (probe == "true") break;
                    await Task.Delay(300);
                }
                if (probe != "true") { NetcusResult(false, "입력 폼을 찾지 못했습니다 — 로그인 또는 페이지 접근을 확인하세요."); return; }
                // 진단: 페이지 자체 함수(Bmodify)가 쓰는 document.form 연결이 Chromium에서 유효한지 로그
                try { Log("netcus form-assoc(document.form.status): " + await cw.ExecuteScriptAsync("(function(){try{return !!(document.form&&document.form.status);}catch(e){return false;}})()")); } catch { }

                NetcusProgress("내용 작성 중…");
                NetcusStatus("submit", "filling");
                // ★ 미기록(Status="")이면 status 대입 문장 자체를 넣지 않는다 — 페이지가 이미 가진 값
                //   (= netcus에 직접 기록해 둔 휴가·병가 등)이 그대로 남는다. 대입하면 그게 정근으로 덮인다.
                bool keepStatus = string.IsNullOrEmpty(req.Status);
                string stFill = keepStatus ? "" : $"if(st){{st.value={J(req.Status)};}}";
                string fill = "(function(){try{var st=document.getElementsByName('status')[0],ot=document.getElementsByName('overtime')[0],ct=document.getElementsByName('content')[0];"
                    + stFill
                    + $"if(ot){{ot.selectedIndex={req.Overtime};}}if(ct){{ct.value={J(req.Content)};}}"
                    + "return (st&&ct)?1:0;}catch(e){return 0;}})()";
                var filled = await cw.ExecuteScriptAsync(fill);
                if (filled != "1") { NetcusResult(false, "입력 폼 채우기 실패 — 페이지 구조가 바뀌었을 수 있습니다."); return; }
                if (keepStatus)
                {
                    // 나중에 "왜 근태가 안 바뀌었지"를 추적할 유일한 근거 — 사이트에서 읽은 값을 그대로 남긴다.
                    string cur = "";
                    try { cur = (await cw.ExecuteScriptAsync("(function(){try{var s=document.getElementsByName('status')[0];return (s&&s.value)||'';}catch(e){return '';}})()") ?? "").Trim('"'); }
                    catch { }
                    Log("netcus 근태: 미기록 → 사이트 기존값 유지(" + cur + ")");
                }

                if (req.DryRun)
                {
                    try { _w2win?.Activate(); } catch { }
                    NetcusResult(true, "미제출(테스트): 내용을 채웠습니다. 열린 창에서 확인 후 직접 ‘수정’으로 제출하세요.");
                    return;
                }

                // 실제 제출 — netcus는 euc-kr 페이지. JS fetch+FormData는 본문이 UTF-8 고정이라 한글이 깨진다.
                // 네이티브 폼 submit은 브라우저가 accept-charset='euc-kr'로 필드값을 인코딩(레거시 폼이 원래 쓰던 경로) → 한글 보존.
                // 페이지 안에서 동적 <form>(action=go=write, multipart, accept-charset=euc-kr)을 만들어 값 싣고 submit → 창이 결과로 이동.
                NetcusProgress("제출 중…");
                NetcusStatus("submit", "submitting");
                var submitNav = NavOnce(cw, 20000);
                // ★ 미기록이면 status를 '페이지의 현재 select 값'으로 되싣는다(dbstatus를 읽어 싣는 바로 아래 줄과 같은 패턴).
                //   폼에서 status를 아예 빼지는 않는다 — netcus가 필드 부재를 어떻게 다루는지 모르고, 빈 값으로 저장될 수 있다.
                string stPost = keepStatus ? "H('status',(st&&st.value)?st.value:'1');" : $"H('status',{J(req.Status)});";
                string post = "(function(){try{"
                    + "var db=document.getElementsByName('dbstatus')[0];"
                    + "var st=document.getElementsByName('status')[0];"
                    + "var f=document.createElement('form');f.method='post';f.enctype='multipart/form-data';f.acceptCharset='euc-kr';"
                    + $"f.action='pjm_work_view.jsp?go=write&table=report_tbl&y={req.Y}&m={req.M}&d={req.D}&id='+encodeURIComponent({J(id)});"
                    + "function H(n,v){var i=document.createElement('input');i.type='hidden';i.name=n;i.value=v;f.appendChild(i);}"
                    + "H('dbstatus',(db&&db.value)?db.value:'0');"
                    + stPost
                    + $"H('overtime',{J(req.Overtime.ToString())});"
                    + $"var ta=document.createElement('textarea');ta.name='content';ta.value={J(req.Content)};f.appendChild(ta);"
                    + "document.body.appendChild(f);f.submit();return 'SUBMITTED';"
                    + "}catch(e){return 'ERR '+((e&&e.message)||e);}})()";
                var pres = await cw.ExecuteScriptAsync(post);
                Log("netcus submit fire: " + pres);
                if (pres == null || !pres.Contains("SUBMITTED")) { NetcusResult(false, "제출 폼 생성 실패: " + (pres ?? "null")); return; }
                bool navOk = await submitNav;   // go=write 결과 페이지로 이동 대기
                Log("netcus submit nav: " + navOk);

                // 저장 검증(거짓 성공 + 한글 깨짐 동시 차단) — 그 날짜 페이지를 다시 열어 content를 되읽어,
                // 우리가 보낸 한글 토큰이 그대로 있는지 대조(mojibake면 불일치).
                // 반환: -1=로그인페이지, -2=폼없음, 0=빈내용, 1=정상(한글 일치 또는 한글없음+내용존재), 2=저장됐으나 한글 불일치
                NetcusProgress("저장 확인 중…");
                NetcusStatus("submit", "verifying");
                // ★ 자동 제출(go=write)이 끝났다 — 여기서 로드되는 되읽기 문서는 창째로 사용자에게 남긴다.
                //   설정은 문서 로드 시점 스냅샷이므로 로드 '전에' 원복해야 사용자의 확인창(수정 confirm 등)이 산다.
                detach?.Invoke(); detach = null;
                await NavTo(cw, url);
                string needle = NetcusHangulNeedle(req.Content);
                string check = "(function(){try{"
                    + "if(document.querySelector('input[type=password]'))return -1;"
                    + "var c=document.getElementsByName('content')[0];if(!c)return -2;"
                    + "var v=(c.value||'').trim();if(!v.length)return 0;"
                    + $"var n={J(needle)};return (!n)?1:(v.indexOf(n)>=0?1:2);"
                    + "}catch(e){return -3;}})()";
                int vr = -9;
                for (int i = 0; i < 14; i++)
                {
                    var v = await cw.ExecuteScriptAsync(check);
                    if (int.TryParse((v ?? "").Trim('"'), out vr) && (vr == 1 || vr == 2 || vr == -1)) break;
                    await Task.Delay(300);
                }
                Log("netcus verify: " + vr + " (needle=" + needle + ")");
                if (vr == -1) NetcusResult(false, "세션 만료/로그인 필요 — 자격증명을 확인하세요.");
                else if (vr == 1)
                {
                    NetcusResult(true, "회사 일간보고 전송 완료 — 한글까지 정상 저장 확인. netcus 사이트에서도 확인하세요.");
                    //  ★ 결과를 먼저 알리고 그 다음 기록한다 — 사용자가 보는 진실은 '전송됐다'이고,
                    //    DB 저장은 그걸 바꾸지 못한다. vr==1(되읽어 검증됨)일 때만 부른다 —
                    //    실패한 전송을 기록하면 표가 거짓말을 하게 된다.
                    _host.SaveDailyReport(req.Y, req.M, req.D, req.Status, req.Overtime, req.Content, req.HoursJson);
                }
                else if (vr == 2) NetcusResult(false, "저장은 됐으나 한글이 깨졌을 수 있습니다(되읽기 대조 불일치) — netcus에서 확인하세요.");
                else NetcusResult(false, "저장 확인 실패(내용이 비어 있음) — 열린 창에서 직접 확인하세요.");
            }
            catch (Exception ex) { Log("netcus 예외: " + ex); NetcusResult(false, "전송 오류: " + ex.Message); }
            finally { detach?.Invoke(); _ncBusy = false; NetcusBusy(false); }
        }

        // 주간보고 — '채우고 열어두기'(자동 제출 안 함). pjm_write.jsp 폼에 기간/제목/과제투입시간/진행사항/차주계획 과제목록을 채운 뒤
        // 창을 띄워 둔다. 차주계획 내용·회의내용 등을 보완 후 사용자가 직접 '제출'(Bwrite) — euc-kr은 네이티브 폼이 처리.
        private async Task NetcusWeekFill(NetcusWeekReq req)
        {
            if (_ncBusy) { NetcusResult(false, "다른 netcus 작업이 진행 중입니다 — 잠시 후 다시 시도하세요."); return; }
            _ncBusy = true; NetcusBusy(true);
            CoreWebView2? cw = null;
            Action? detach = null;
            try
            {
                var (id, pw) = NetcusLoadCreds();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw)) { NetcusResult(false, "자격증명이 없습니다 — 설정 → 회사 일간보고에서 ID/비밀번호를 저장하세요."); return; }
                if (NetcusCredsValid() == false) { NetcusResult(false, "저장된 자격증명이 로그인 실패 상태입니다 — 설정에서 자격증명을 다시 확인하세요."); return; }   // 검증 실패 확정 → 로그인 시도 없이 차단(busy는 finally 해제)

                NetcusProgress("창 준비 중…");
                await EnsureW2();
                cw = _w2!.CoreWebView2;
                detach = AttachDialogAutoAccept(cw, "week");

                NetcusProgress("로그인 중…");
                NetcusStatus("weekfill", "login");
                if (!await NetcusLoginVerify(cw, id, pw))
                {
                    try { Dispatcher.Invoke(() => { try { _w2win?.Close(); } catch { } }); } catch { }   // 실패한 창 남기지 않음
                    NetcusResult(false, "netcus 로그인 실패 — 설정에서 ID/비밀번호를 확인하세요.");
                    return;
                }

                NetcusProgress("주간보고 목록 여는 중…");
                NetcusStatus("weekfill", "listing");
                await NavTo(cw, "https://www.netcus.com/pjm/pjm.jsp?id=" + Uri.EscapeDataString(id));
                if (_w2 == null) { NetcusResult(false, "확인 창이 닫혀 중단되었습니다."); return; }

                // 작성 폼은 직접 GET하면 서버가 '게시판이 옳지 않습니다' 거부 → 목록의 form(table_code=report_tbl)을
                // pjm_write.jsp로 POST해야 함. 폼 연결 의존을 피해 동적 POST 폼으로 table_code/id를 실어 이동.
                NetcusProgress("작성 폼 여는 중…");
                NetcusStatus("weekfill", "opening");
                // ★ 여기서부터 로드되는 문서(pjm_write.jsp)는 사용자에게 넘긴다 — 그 전에 자동수락을 반드시 끈다.
                //   AreDefaultScriptDialogsEnabled 는 문서 로드 시점에 스냅샷되므로, 로드 뒤 원복은 이미 늦다(실측).
                //   또 핸들러를 붙였다 떼면 무음 취소가 아니라 '응답 주체 없는 정지'가 된다 — 제출 confirm 이 죽는다.
                detach?.Invoke(); detach = null;
                var wnav = NavOnce(cw, 15000);
                string goWrite = "(function(){try{var f=document.createElement('form');f.method='post';f.action='pjm_write.jsp';"
                    + "function H(n,v){var i=document.createElement('input');i.type='hidden';i.name=n;i.value=v;f.appendChild(i);}"
                    + "H('table_code','report_tbl');H('word_code','');H('n_code','');H('s_code','');H('c_code','');"
                    + "H('id'," + J(id) + ");document.body.appendChild(f);f.submit();return 'ok';}catch(e){return 'err';}})()";
                await cw.ExecuteScriptAsync(goWrite);
                await wnav;
                if (_w2 == null) { NetcusResult(false, "확인 창이 닫혀 중단되었습니다."); return; }

                NetcusProgress("페이지 확인 중…");
                string probe = "false";
                for (int i = 0; i < 16; i++)
                {
                    if (_w2 == null) { NetcusResult(false, "확인 창이 닫혀 중단되었습니다."); return; }
                    probe = await cw.ExecuteScriptAsync("(function(){return !!(document.getElementsByName('subject')[0] && document.getElementsByName('content')[0]);})()");
                    if (probe == "true") break;
                    await Task.Delay(300);
                }
                if (probe != "true") { NetcusResult(false, "주간보고 작성 폼을 찾지 못했습니다 — 로그인 또는 페이지 접근을 확인하세요."); return; }

                NetcusProgress("내용 작성 중…");
                NetcusStatus("weekfill", "filling");
                // sdate/edate는 readonly지만 JS .value 할당은 가능. content(과제투입시간)/endwork(진행사항)/planwork(차주계획)는 textarea.
                string fill = "(function(){try{function set(n,v){var e=document.getElementsByName(n)[0];if(e){e.value=v;}}"
                    + $"set('sdate',{J(req.Sdate)});set('edate',{J(req.Edate)});set('subject',{J(req.Subject)});"
                    + $"set('content',{J(req.Content)});set('endwork',{J(req.Endwork)});set('planwork',{J(req.Planwork)});"
                    + "return !!document.getElementsByName('content')[0];}catch(e){return false;}})()";
                var filled = await cw.ExecuteScriptAsync(fill);
                if (filled != "true") { NetcusResult(false, "주간보고 폼 채우기 실패 — 페이지 구조가 바뀌었을 수 있습니다."); return; }

                // 안전장치 — 이 폼도 <table> 안 빈 폼이라 페이지의 Bwrite(document.form.submit)가 빈 폼을 보낼 수 있다.
                // Bwrite를 euc-kr 동적 폼 버전으로 오버라이드(원본과 동일 검증·confirm 유지) → 사용자가 보완 후 직접 제출해도 안전.
                string ov = "(function(){try{window.Bwrite=function(iscompletion){"
                    + "var sd=document.getElementsByName('sdate')[0],sj=document.getElementsByName('subject')[0];"
                    + "if(sd&&!sd.value){alert('기간을 선택하세요.');return;}"
                    + "if(sj&&!sj.value){alert('제목을 입력하세요.');return;}"
                    + "if(iscompletion==='y'&&!confirm('제출하시겠습니까?'))return;"
                    + "var f=document.createElement('form');f.method='post';f.enctype='multipart/form-data';f.acceptCharset='euc-kr';"
                    + $"f.action='pjm_write.jsp?go=write&table=report_tbl&id='+encodeURIComponent({J(id)});"
                    + "function H(n,v){var i=document.createElement('input');i.type='hidden';i.name=n;i.value=v;f.appendChild(i);}"
                    + "H('iscompletion',iscompletion);"
                    + "['ndate','sdate','edate','subject','endwork','content','notendwork','planwork','problem','resultwork'].forEach(function(n){var e=document.getElementsByName(n)[0];if(e)H(n,e.value);});"
                    + "var hc=document.getElementsByName('html')[0];if(hc&&hc.checked)H('html',hc.value);"
                    + "document.body.appendChild(f);f.submit();};return 'ok';}catch(e){return 'err';}})()";
                try { Log("netcus(week) Bwrite override: " + await cw.ExecuteScriptAsync(ov)); } catch { }

                try { _w2win?.Activate(); } catch { }
                NetcusResult(true, "주간보고 작성 폼을 채웠습니다 — 차주계획 내용 등을 보완 후 열린 창에서 직접 ‘제출’하세요.");
                //  ★ 여기서 기록하는 것은 '보냈다'가 아니라 **'캘린더가 작성했다'**다.
                //    위젯은 주간을 전송하지 않고 사용자가 직접 제출한다(위 문구 그대로).
                //    그래서 사용자가 폼에서 보완한 내용은 담기지 않고, 컴럼 이름도 composed_at 이다(§5.9.5).
                _host.SaveWeeklyReport(req.Sdate, req.Edate, req.Subject, req.Content, req.Endwork, req.Planwork);
            }
            catch (Exception ex)
            {
                Log("netcus(week) 예외: " + ex);
                bool disposed = (ex.Message ?? "").Contains("disposed");
                NetcusResult(false, disposed ? "확인 창이 닫혀 중단되었습니다 — 다시 시도하세요." : ("주간보고 작성 오류: " + ex.Message));
            }
            finally { detach?.Invoke(); _ncBusy = false; NetcusBusy(false); }
        }

        // 주간보고 병합(Phase2) — from~to 기간의 일간보고 content를 '읽기만' 해서 웹으로 회신(제출/수정 없음).
        // 로그인 → 각 날짜 pjm_work_view.jsp에서 content textarea 값을 읽음 → days 배열로 __hostReply 회신 → 창 닫음.
        // 파싱·과제별 그룹핑·미분류 판정은 전부 웹(JS parseNetcusWeek)이 담당한다. 읽기 창은 최소화하고 끝나면 닫는다.
        // 회신 계약: { ok:bool, error:string, days:[{ date:"YYYY-MM-DD", content:string, ok:bool }] }
        //   error: ""(정상)/"no-creds"/"login"/"read". content 비었거나 요소 없으면 content:""(파서가 '일간 없음' 판정).
        private async Task NetcusWeekMerge(string reqId, string from, string to)
        {
            if (_ncBusy) { GitReply(reqId, new { ok = false, error = "busy", days = Array.Empty<object>() }); return; }   // 이미 진행 중 → 즉시 반환(_w2 안 건드림)
            _ncBusy = true; NetcusBusy(true);
            var days = new List<object>();
            CoreWebView2? cw = null;
            Action? detach = null;
            try
            {
                var (id, pw) = NetcusLoadCreds();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw))
                {
                    GitReply(reqId, new { ok = false, error = "no-creds", days = Array.Empty<object>() });
                    return;
                }
                if (NetcusCredsValid() == false) { GitReply(reqId, new { ok = false, error = "unverified", days = Array.Empty<object>() }); return; }   // 검증 실패 확정 → 로그인 시도 없이 차단(busy는 finally 해제)

                // 기간 파싱(YYYY-MM-DD). 실패/역순은 방어 처리.
                if (!DateTime.TryParse(from, out var dFrom) || !DateTime.TryParse(to, out var dTo))
                {
                    GitReply(reqId, new { ok = false, error = "read", days = Array.Empty<object>() });
                    return;
                }
                if (dTo < dFrom) { var tmp = dFrom; dFrom = dTo; dTo = tmp; }
                if ((dTo - dFrom).TotalDays > 31)   // 과도한 범위 방어 — 커스텀 폭주/DateTime.MaxValue 증가 예외 차단
                {
                    GitReply(reqId, new { ok = false, error = "range", days = Array.Empty<object>() });
                    return;
                }

                Log($"netcus 주간병합 읽기: {from} ~ {to}");
                await EnsureW2(background: true);   // 읽기 전용 — 포커스/포그라운드 안 뺏음(최소화 비활성 창)
                cw = _w2!.CoreWebView2;
                detach = AttachDialogAutoAccept(cw, "merge");   // 레거시 pjm alert()/confirm() 자동 수락(최소화 창 무한대기 방어)

                // 로그인 + 양성 인증 확정(공유 헬퍼 — 목적지 work_view에서 인증 요소 존재로 판정)
                NetcusStatus("merge", "login");
                if (!await NetcusLoginVerify(cw, id, pw))
                {
                    GitReply(reqId, new { ok = false, error = "login", days = Array.Empty<object>() });
                    return;
                }

                // from..to 각 날짜 content 읽기 — 단일 _w2 재사용(하루 1창 금지)
                int mergeTotal = (dTo - dFrom).Days + 1, mergeDone = 0;
                bool sessionLost = false;   // 크롤 도중 login 리다이렉트(세션 만료) 감지 → 빈 날과 구분
                for (var d = dFrom; d <= dTo; d = d.AddDays(1))
                {
                    int Y = d.Year, M = d.Month, D = d.Day;
                    string date = $"{Y:D4}-{M:D2}-{D:D2}";
                    mergeDone++;
                    NetcusStatus("merge", "reading", mergeDone, mergeTotal);
                    string content = ""; bool okDay = false;
                    try
                    {
                        string url = $"https://www.netcus.com/pjm/pjm_work_view.jsp?y={Y}&m={M}&d={D}&id={Uri.EscapeDataString(id)}";
                        await NavTo(cw, url);
                        // content textarea 로드 대기(요소 존재하면 값이 빈 문자열이라도 탈출). ExecuteScriptAsync는 JSON 문자열 반환.
                        string raw = "null";
                        for (int i = 0; i < 12; i++)
                        {
                            raw = await cw.ExecuteScriptAsync("(function(){var c=document.getElementsByName('content')[0];return c?c.value:null;})()");
                            if (raw != null && raw != "null") break;
                            await Task.Delay(250);
                        }
                        if (raw != null && raw != "null")
                        {
                            try { content = JsonSerializer.Deserialize<string>(raw) ?? ""; } catch { content = ""; }
                            okDay = true;   // 요소 존재 = 그 날 페이지 접근 성공(내용은 비었을 수 있음)
                        }
                        else
                        {
                            // 요소 없음 — login 리다이렉트(세션 만료)인지 진짜 접근불가인지 구분
                            var pwp = await cw.ExecuteScriptAsync("(function(){return !!document.querySelector('input[type=password]');})()");
                            if (pwp == "true") { sessionLost = true; break; }   // login 폼 = 세션 만료 → 즉시 중단(빈 날로 위장 금지)
                            content = ""; okDay = false;                          // 비번칸도 없음 = 진짜 접근불가(파서가 빈 날 취급)
                        }
                    }
                    catch (Exception exd) { Log("netcus 주간병합 일자 읽기 실패(" + date + "): " + exd.Message); content = ""; okDay = false; }
                    days.Add(new { date, content, ok = okDay });
                }

                if (sessionLost) { GitReply(reqId, new { ok = false, error = "session", days = Array.Empty<object>() }); return; }   // 세션 만료 → 부분데이터 폐기, 명시 중단(무음 손실 방지)
                Log($"netcus 주간병합 완료: {days.Count}일");
                GitReply(reqId, new { ok = true, error = "", days });
            }
            catch (Exception ex)
            {
                Log("netcus 주간병합 예외: " + ex);
                GitReply(reqId, new { ok = false, error = "read", days = Array.Empty<object>() });
            }
            finally
            {
                detach?.Invoke();
                // 읽기 전용 — 성공/실패 무관 확인창은 닫는다(제출 확인창과 달리 열어두지 않음).
                try { Dispatcher.Invoke(() => { try { _w2win?.Close(); } catch { } }); } catch { }
                _ncBusy = false; NetcusBusy(false);
            }
        }

        // 주간보고 범위 읽기(Phase2) — from~to에 걸치는 netcus '주간보고'들을 읽기만 해서 웹으로 회신(제출/수정 없음).
        // 흐름: 로그인 → 게시판 목록(pjm.jsp) 페이지 순회 → 각 행 {viewNo,regDate,title} 추출 →
        //   작성일 사전필터(from-7 ~ to+7) → 후보 조회(pjm_view.jsp) 열어 라벨셀 innerText 추출 → 기간 겹침 확정 → weeks 누적.
        // 파싱·과제별 병합은 전부 웹(JS parseNetcusWeekly)이 담당. 읽기 창은 최소화하고 끝나면 닫는다.
        // 회신 계약: { ok:bool, error:string, weeks:[{ regdate, title, period, endwork, content, plan }] }
        //   error: ""(정상)/"busy"/"no-creds"/"login"/"read"/"range".
        // 크롤 재현: go_list(loc)=pjm.jsp?list=go&start=loc / go_view(loc)=pjm_view.jsp?start=1&view_no=loc (캡처 그대로).
        //   레거시 <table> 내부 빈 <form>(필드가 형제 노드) 이슈 회피 위해 document.form 대신 동적 POST 폼으로 hidden 필드
        //   (word_code/n_code/s_code/c_code/table_code=report_tbl/id)를 실어 재현한다(기존 NetcusWeekFill와 동일 패턴).
        private async Task NetcusWeeklyRangeRead(string reqId, string from, string to)
        {
            if (_ncBusy) { GitReply(reqId, new { ok = false, error = "busy", weeks = Array.Empty<object>() }); return; }   // 단일 _w2 레이스 방지(모든 op 공유 가드)
            _ncBusy = true; NetcusBusy(true);
            var weeks = new List<object>();
            CoreWebView2? cw = null;
            Action? detach = null;
            try
            {
                var (id, pw) = NetcusLoadCreds();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw)) { GitReply(reqId, new { ok = false, error = "no-creds", weeks = Array.Empty<object>() }); return; }
                if (NetcusCredsValid() == false) { GitReply(reqId, new { ok = false, error = "unverified", weeks = Array.Empty<object>() }); return; }   // 검증 실패 확정 → 로그인 시도 없이 차단(busy는 finally 해제)

                if (!NcDate(from, out var dFrom) || !NcDate(to, out var dTo)) { GitReply(reqId, new { ok = false, error = "read", weeks = Array.Empty<object>() }); return; }
                if (dTo < dFrom) { var tmp = dFrom; dFrom = dTo; dTo = tmp; }
                if ((dTo - dFrom).TotalDays > 400) { GitReply(reqId, new { ok = false, error = "range", weeks = Array.Empty<object>() }); return; }   // 일간(31일)과 달리 넓게, 그러나 폭주 방지
                DateTime winLo = dFrom.AddDays(-7), winHi = dTo.AddDays(7);   // 작성일 사전필터 창(주간보고 등록일은 기간 끝 근처)

                Log($"netcus 주간범위 읽기: {from} ~ {to}");
                await EnsureW2(background: true);   // 읽기 전용 — 포커스 안 뺏음(최소화 비활성 창)
                cw = _w2!.CoreWebView2;
                detach = AttachDialogAutoAccept(cw, "weekly");   // 레거시 pjm alert()/confirm() 자동 수락(최소화 창 무한대기 방어)

                // 로그인 + 양성 인증 확정(공유 헬퍼)
                NetcusStatus("weekly", "login");
                if (!await NetcusLoginVerify(cw, id, pw)) { GitReply(reqId, new { ok = false, error = "login", weeks = Array.Empty<object>() }); return; }

                // 목록 1페이지 이동(GET). 이후 페이지는 go_list 폼 재현.
                await NavTo(cw, "https://www.netcus.com/pjm/pjm.jsp?id=" + Uri.EscapeDataString(id));

                const int MAXPAGES = 40, MAXREAD = 60;
                var seenViews = new HashSet<string>();
                int opened = 0; bool capped = false;

                for (int p = 1; p <= MAXPAGES && !capped; p++)
                {
                    if (p > 1)
                    {
                        var lnav = NavOnce(cw, 15000);
                        await cw.ExecuteScriptAsync(GoListJs(p, id));   // go_list(p) 재현
                        await lnav;
                    }
                    // 목록 행 추출(테이블 로드 대기 재시도)
                    string rowsRaw = "null";
                    for (int i = 0; i < 16; i++)
                    {
                        rowsRaw = await cw.ExecuteScriptAsync(RowExtractJs);
                        if (rowsRaw != null && rowsRaw != "null" && rowsRaw != "\"[]\"") break;
                        await Task.Delay(250);
                    }
                    var rows = NcParseRows(rowsRaw);
                    if (rows.Count == 0)
                    {
                        // 행 0 — login 리다이렉트(세션 만료)인지 진짜 더 없는 페이지인지 구분
                        var pwp = await cw.ExecuteScriptAsync("(function(){return !!document.querySelector('input[type=password]');})()");
                        if (pwp == "true") { GitReply(reqId, new { ok = false, error = "session", weeks = Array.Empty<object>() }); return; }   // login 폼 = 세션 만료 → 부분데이터 폐기, 명시 중단
                        break;   // 비번칸 없음 = 진짜 더 없는 페이지
                    }

                    bool anyNew = false, allOlder = true;
                    var candidates = new List<(string viewNo, string regStr)>();
                    foreach (var r in rows)
                    {
                        if (string.IsNullOrEmpty(r.viewNo)) continue;
                        if (!seenViews.Contains(r.viewNo)) anyNew = true;
                        bool regOk = NcDate(r.regDate, out var dReg);
                        if (regOk && dReg >= winLo) allOlder = false;
                        if (!regOk || (dReg >= winLo && dReg <= winHi)) candidates.Add((r.viewNo, r.regDate));   // 창 안(또는 날짜 불명=관대) → 후보
                    }
                    if (!anyNew) break;   // go_list가 마지막 페이지로 클램프(반복) → 진전 없음 → 중단

                    foreach (var c in candidates)
                    {
                        if (seenViews.Contains(c.viewNo)) continue;
                        seenViews.Add(c.viewNo);
                        if (opened >= MAXREAD) { capped = true; Log("netcus 주간범위: 읽기 상한(60) 도달 — 중단"); break; }

                        var vnav = NavOnce(cw, 15000);
                        await cw.ExecuteScriptAsync(GoViewJs(c.viewNo, id));   // go_view(viewNo) 재현
                        await vnav;
                        opened++;
                        NetcusStatus("weekly", "reading", opened, -1);   // 총 미상 → total=-1

                        string cellRaw = "null";
                        for (int i = 0; i < 16; i++)
                        {
                            cellRaw = await cw.ExecuteScriptAsync(CellExtractJs);
                            if (cellRaw != null && cellRaw != "null" && cellRaw.Length > 4) break;
                            await Task.Delay(250);
                        }
                        var cell = NcParseCell(cellRaw);
                        if (cell == null) continue;

                        bool include;
                        if (NcPeriod(cell.period, out var ps, out var pe)) include = (ps <= dTo && pe >= dFrom);   // 기간 겹침 확정
                        else include = true;   // 기간 파싱 실패 → 작성일 사전필터 통과분은 관대 포함
                        if (!include) continue;

                        string regdate = !string.IsNullOrWhiteSpace(cell.regdate) ? cell.regdate : c.regStr;
                        weeks.Add(new { regdate, title = cell.title ?? "", period = cell.period ?? "", endwork = cell.endwork ?? "", content = cell.content ?? "", plan = cell.plan ?? "" });
                    }

                    if (allOlder) break;   // 페이지 전체가 from-7 이전 → 최신순이므로 이후 페이지 불필요
                }

                Log($"netcus 주간범위 완료: {weeks.Count}건 (열람 {opened})");
                GitReply(reqId, new { ok = true, error = "", weeks });
            }
            catch (Exception ex)
            {
                Log("netcus 주간범위 예외: " + ex);
                GitReply(reqId, new { ok = false, error = "read", weeks = Array.Empty<object>() });
            }
            finally
            {
                detach?.Invoke();
                try { Dispatcher.Invoke(() => { try { _w2win?.Close(); } catch { } }); } catch { }   // 읽기 전용 — 확인창 닫음
                _ncBusy = false; NetcusBusy(false);
            }
        }

        // ExecuteScriptAsync는 JSON 문자열을 다시 JSON 인코딩 → 이중 디코드.
        private List<NcRow> NcParseRows(string? raw)
        {
            var list = new List<NcRow>();
            try
            {
                if (string.IsNullOrEmpty(raw) || raw == "null") return list;
                string inner = JsonSerializer.Deserialize<string>(raw) ?? "[]";
                using var d = JsonDocument.Parse(inner);
                foreach (var el in d.RootElement.EnumerateArray())
                {
                    list.Add(new NcRow
                    {
                        viewNo = el.TryGetProperty("viewNo", out var v) ? (v.GetString() ?? "") : "",
                        regDate = el.TryGetProperty("regDate", out var g) ? (g.GetString() ?? "") : "",
                        title = el.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "",
                    });
                }
            }
            catch (Exception ex) { Log("netcus 주간범위 행파싱 실패: " + ex.Message); }
            return list;
        }

        private NcCell? NcParseCell(string? raw)
        {
            try
            {
                if (string.IsNullOrEmpty(raw) || raw == "null") return null;
                string inner = JsonSerializer.Deserialize<string>(raw) ?? "{}";
                using var d = JsonDocument.Parse(inner);
                var r = d.RootElement;
                string G(string k) => r.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
                return new NcCell { period = G("period"), title = G("title"), regdate = G("regdate"), endwork = G("endwork"), content = G("content"), plan = G("plan") };
            }
            catch (Exception ex) { Log("netcus 주간범위 셀파싱 실패: " + ex.Message); return null; }
        }

        // 주간보고 '구조 캡처' 도구(Phase2 준비) — netcus를 가시 창으로 열고 자동 로그인 → 게시판 홈까지만 이동.
        // 사용자가 주간보고 목록/조회로 직접 이동 후 상단에 주입된 '이 페이지 HTML 저장' 버튼을 누르면
        // 그 페이지 HTML을 로컬(%APPDATA%\TaskCalendar\netcus-probe)에 저장. 읽기 전용 — 제출/수정/삭제 없음.
        // 창은 코드로 닫지 않고 사용자가 닫는다(Closed에서 가드·핸들러 해제).
        private async Task NetcusProbeStart()
        {
            if (_ncBusy)
            {
                // 이미 캡처 창이 열려 있으면 기존 안내, 그 외 다른 op 진행 중이면 busy 거절.
                if (_probeOpen) JsCall("window.__netcusProbeResult && window.__netcusProbeResult(true," + J("이미 캡처 창이 열려 있습니다.") + ")");
                else JsCall("window.__netcusProbeResult && window.__netcusProbeResult(false," + J("다른 netcus 작업이 진행 중입니다 — 잠시 후 다시 시도하세요.") + ")");
                return;
            }
            _ncBusy = true; NetcusBusy(true);
            Action? detach = null;
            try
            {
                var (id, pw) = NetcusLoadCreds();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw))
                {
                    _ncBusy = false; NetcusBusy(false);
                    JsCall("window.__netcusProbeResult && window.__netcusProbeResult(false," + J("netcus 자격증명이 없습니다 — 설정에서 저장하세요.") + ")");
                    return;
                }
                if (NetcusCredsValid() == false)   // 검증 실패 확정 → 로그인 시도 없이 차단(no-creds와 동일 인라인 해제)
                {
                    _ncBusy = false; NetcusBusy(false);
                    JsCall("window.__netcusProbeResult && window.__netcusProbeResult(false," + J("저장된 자격증명이 로그인 실패 상태입니다 — 설정에서 확인하세요.") + ")");
                    return;
                }

                await EnsureW2();   // 가시 창(920x720)
                try { if (_w2win != null) { _w2win.Title = "netcus 주간보고 구조 캡처 (읽기 전용)"; } } catch { }
                var cw = _w2!.CoreWebView2;
                detach = AttachDialogAutoAccept(cw, "probe");   // 로그인 구간에만 유지 — 사용자에게 창을 넘기기 직전에 해제

                // 로그인 — 공유 헬퍼로 통일(목적지 인증요소 존재 기반 양성확인; goLogin 폼주입/비번칸 폴링 제거)
                if (!await NetcusLoginVerify(cw, id, pw))
                {
                    _ncBusy = false; NetcusBusy(false);
                    detach?.Invoke();
                    JsCall("window.__netcusProbeResult && window.__netcusProbeResult(false," + J("netcus 로그인 실패 — 설정에서 로그인 정보를 확인하세요.") + ")");
                    return;
                }

                NetcusStatus("probe", "opening");

                // ★ 여기서부터 로드되는 문서는 전부 사용자에게 넘긴다(캡처 창은 열어둔 채 사용자가 직접 탐색한다).
                //   그 전에 자동수락을 반드시 끈다 — AreDefaultScriptDialogsEnabled 는 문서 로드 시점에 스냅샷되므로
                //   로드 뒤 원복(창 Closed 등)은 이미 늦다(실측). 붙인 채 두면 목록의 삭제 버튼
                //   confirm('정말로 삭제하시겠습니까?') 가 무음 수락되어 주간보고가 확인창 없이 삭제된다.
                detach?.Invoke(); detach = null;

                // 게시판 홈까지만 이동 — 주간보고 목록/조회는 사용자가 직접 이동(URL 추측 자동이동 금지)
                await NavTo(cw, "https://www.netcus.com/pjm/pjm.jsp?id=" + Uri.EscapeDataString(id));

                // 프로브 세션 핸들러 배선 — 창은 계속 열어둠. 창 Closed에서 핸들러 해제 + busy 해제(누수 방지).
                // (dialog detach 는 위 인계 직전에 이미 끝났다 — 여기서 다시 부르지 않는다)
                cw.WebMessageReceived += OnProbeMsg;
                cw.NavigationCompleted += OnProbeNav;
                _w2win!.Closed += (_, __) =>
                {
                    try { cw.WebMessageReceived -= OnProbeMsg; } catch { }
                    try { cw.NavigationCompleted -= OnProbeNav; } catch { }
                    _ncBusy = false; _probeOpen = false; NetcusBusy(false);
                };
                _probeOpen = true;   // 창 열림 확정 — 이후 재요청은 '이미 열림' 안내, 다른 op는 busy 차단

                // 캡처 바 즉시 주입(이후 탐색마다 OnProbeNav가 재주입)
                await InjectProbeBar(cw);

                JsCall("window.__netcusProbeResult && window.__netcusProbeResult(true," + J("netcus 캡처 창을 열었습니다 — 주간보고 목록/조회로 이동 후 상단 '이 페이지 HTML 저장'을 누르세요.") + ")");
                // 성공 경로: _ncBusy·_probeOpen은 창이 살아있는 동안 유지(창 Closed에서 리셋). detach는 인계 직전에 이미 끝났다.
            }
            catch (Exception ex)
            {
                _ncBusy = false; _probeOpen = false; NetcusBusy(false);
                detach?.Invoke();
                JsCall("window.__netcusProbeResult && window.__netcusProbeResult(false," + J("캡처 창 오류: " + ex.Message) + ")");
            }
        }

        // 캡처 바 주입(idempotent) — 이미 있으면 return. body 미존재 대비 documentElement에 append.
        private async Task InjectProbeBar(CoreWebView2 cw)
        {
            string js = @"(function(){
 if(document.getElementById('__tcProbeBar'))return;
 var b=document.createElement('div');b.id='__tcProbeBar';
 b.style.cssText='position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#1d2433;color:#fff;font:13px ""Malgun Gothic"",sans-serif;padding:8px 12px;display:flex;gap:10px;align-items:center;box-shadow:0 2px 8px rgba(0,0,0,.35)';
 var t=document.createElement('span');t.textContent='📸 주간보고 목록/조회 페이지에서 → ';
 var btn=document.createElement('button');btn.textContent='이 페이지 HTML 저장';
 btn.style.cssText='background:#3e5be0;color:#fff;border:0;border-radius:6px;padding:6px 12px;font:inherit;cursor:pointer';
 btn.onclick=function(){try{window.chrome.webview.postMessage(JSON.stringify({t:'probeCapture'}));}catch(e){}};
 var st=document.createElement('span');st.id='__tcProbeStatus';st.style.cssText='margin-left:auto;opacity:.9';
 b.appendChild(t);b.appendChild(btn);b.appendChild(st);
 (document.body||document.documentElement).appendChild(b);
 if(document.body)document.body.style.paddingTop='46px';
})();";
            try { await cw.ExecuteScriptAsync(js); } catch { }
        }

        // 탐색 완료마다 캡처 바 재주입(페이지 이동 후에도 버튼 유지)
        private async void OnProbeNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            try { if (_w2?.CoreWebView2 != null) await InjectProbeBar(_w2.CoreWebView2); } catch { }
        }

        // 캡처 버튼 클릭 수신 → 현재 페이지 outerHTML을 로컬 파일로 저장(읽기 전용)
        private async void OnProbeMsg(object? s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw;
                try { raw = e.TryGetWebMessageAsString(); } catch { raw = e.WebMessageAsJson; }
                if (string.IsNullOrEmpty(raw) || !raw.Contains("probeCapture")) return;
                var cw = _w2?.CoreWebView2;
                if (cw == null) return;
                // ExecuteScriptAsync는 JSON 문자열을 다시 JSON 인코딩해 반환 → 이중 디코드 필요
                string j = await cw.ExecuteScriptAsync("JSON.stringify({html:document.documentElement.outerHTML,url:location.href,title:document.title})");
                if (string.IsNullOrEmpty(j) || j == "null") return;
                var inner = JsonSerializer.Deserialize<string>(j);
                using var docp = JsonDocument.Parse(inner ?? "{}");
                var root = docp.RootElement;
                string html = root.GetProperty("html").GetString() ?? "";
                string url = root.GetProperty("url").GetString() ?? "";
                string title = root.GetProperty("title").GetString() ?? "";

                var dir = Path.Combine(_host.DataDir, "netcus-probe");
                Directory.CreateDirectory(dir);
                string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string fname = "capture-" + ts + ".html";
                string path = Path.Combine(dir, fname);
                string header = "<!-- TaskCalendar netcus probe | URL: " + url + " | TITLE: " + title + " | AT: " + ts + " -->\n";
                File.WriteAllText(path, header + html, new System.Text.UTF8Encoding(false));

                // 페이지 상태 갱신(주입 바에 파일명 표시)
                try { await cw.ExecuteScriptAsync("(function(){var s=document.getElementById('__tcProbeStatus');if(s)s.textContent=" + J("저장됨: " + fname) + ";})()"); } catch { }

                // 최초 저장 시 저장 폴더 1회만 자동 열기
                if (!_ncProbeFolderOpened)
                {
                    _ncProbeFolderOpened = true;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + dir + "\"", UseShellExecute = true }); } catch { }
                }

                JsCall("window.__netcusProbeResult && window.__netcusProbeResult(true," + J("저장됨: " + path) + ")");
                Log("netcus probe 저장: " + path);
            }
            catch (Exception ex) { Log("netcus probe 캡처 오류: " + ex.Message); }
        }
    }

    // Windows DPAPI(CurrentUser) — 추가 NuGet 없이 crypt32 P/Invoke (폐쇄망 오프라인 빌드 안전)
    internal static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static byte[] Protect(byte[] data) => Run(data, true);
        public static byte[] Unprotect(byte[] data) => Run(data, false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            try
            {
                inBlob.cbData = data.Length;
                inBlob.pbData = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, inBlob.pbData, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref inBlob, "tc", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return outBytes;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
    }
}
