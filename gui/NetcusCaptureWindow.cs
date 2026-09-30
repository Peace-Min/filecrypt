using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using WV = Microsoft.Web.WebView2.Wpf;

namespace FileCrypt
{
    /// <summary>
    /// 근태관리 사이트의 실제 응답을 떠 오는 도구. 목업 사이트(tools\netcus-mock)를 실제와 같게 만들려고 쓴다.
    ///   FileCrypt.exe --netcus-capture
    ///
    /// 읽기만 한다. 기록(go=write)·삭제·수정은 하지 않는다.
    /// 로그인은 사람이 창에서 직접 한다 - 이 도구는 비밀번호를 넣지 않는다.
    ///
    /// 저장하는 것: netcus.com 에서 받은 응답의 원본 바이트(euc-kr 그대로)와 상태·헤더, 요청 주소·메서드.
    /// 저장하지 않는 것: 쿠키 값, 폼으로 보낸 값(로그인 비밀번호 포함) - 이름만 남긴다.
    /// 받은 페이지에는 실제 보고 내용과 이름이 들어 있으므로, 결과 폴더는 이 PC 에만 두고 저장소에 넣지 않는다.
    /// </summary>
    public sealed class NetcusCaptureWindow : Window
    {
        private const string Site = "https://www.netcus.com/pjm/";

        private readonly string _outDir;
        private readonly TextBlock _msg = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        private readonly TextBox _dates = new TextBox { Width = 260, Text = "2024-08-14, 2024-08-15, 2024-08-16" };
        private readonly Button _go = new Button { Content = "로그인했습니다 → 캡처 시작", Padding = new Thickness(12, 5, 12, 5) };
        private readonly WV.WebView2 _web = new WV.WebView2();
        private readonly List<object> _index = new List<object>();
        private int _seq;
        private string _phase = "login";

        public NetcusCaptureWindow()
        {
            Title = "근태관리 구조 캡처 (읽기 전용)";
            Width = 1100; Height = 800;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _outDir = Path.Combine(AppConfig.Dir, "netcus-capture", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_outDir);

            var bar = new DockPanel { Margin = new Thickness(10), LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(new TextBlock { Text = "읽을 날짜", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            right.Children.Add(_dates);
            right.Children.Add(new Border { Width = 8 });
            right.Children.Add(_go);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            bar.Children.Add(_msg);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(_web);
            Content = root;

            _go.IsEnabled = false;
            _go.Click += async (s, e) => await CaptureAsync();
            Loaded += async (s, e) => await StartAsync();
        }

        private void Say(string s) { _msg.Text = s; }

        private async Task StartAsync()
        {
            try
            {
                var host = new NetcusHost(Dispatcher);
                await host.InitAsync();          // 앱과 같은 WebView2 프로필(쿠키 공유)
                await _web.EnsureCoreWebView2Async(host.Env);
                Hook(_web.CoreWebView2, "main");
                Say("사이트에 직접 로그인하세요. 로그인이 끝나면 오른쪽 [캡처 시작] 을 누르세요. "
                  + "(이 도구는 비밀번호를 넣지 않고, 보낸 값은 저장하지 않습니다)\r\n저장 위치: " + _outDir);
                _go.IsEnabled = true;
                _web.CoreWebView2.Navigate(Site + "login.htm");
            }
            catch (Exception ex) { Say("시작 실패: " + ex.Message); }
        }

        /// <summary>netcus.com 응답을 전부 기록한다. 이미지·스타일·스크립트도 목업에 필요하므로 거르지 않는다.</summary>
        private void Hook(CoreWebView2 cw, string profile)
        {
            cw.WebResourceResponseReceived += async (s, e) =>
            {
                try
                {
                    var uri = new Uri(e.Request.Uri);
                    if (!uri.Host.EndsWith("netcus.com", StringComparison.OrdinalIgnoreCase)) return;
                    int n = System.Threading.Interlocked.Increment(ref _seq);

                    string fname = string.Format("{0:D3}_{1}_{2}", n, e.Request.Method, Slug(uri.AbsolutePath));
                    byte[] body = null;
                    try
                    {
                        using (var st = await e.Response.GetContentAsync())
                        {
                            if (st != null)
                                using (var ms = new MemoryStream()) { await st.CopyToAsync(ms); body = ms.ToArray(); }
                        }
                    }
                    catch { /* 3xx 등 본문 없음 */ }
                    if (body != null && body.Length > 0) File.WriteAllBytes(Path.Combine(_outDir, fname + ".bin"), body);

                    var reqHeaders = new Dictionary<string, string>();
                    foreach (var h in e.Request.Headers) reqHeaders[h.Key] = Redact(h.Key, h.Value);
                    var resHeaders = new Dictionary<string, string>();
                    foreach (var h in e.Response.Headers) resHeaders[h.Key] = Redact(h.Key, h.Value);

                    var entry = new
                    {
                        seq = n,
                        profile,
                        phase = _phase,
                        method = e.Request.Method,
                        url = e.Request.Uri,
                        status = e.Response.StatusCode,
                        reason = e.Response.ReasonPhrase,
                        requestHeaders = reqHeaders,
                        postFieldNames = PostFieldNames(e.Request),
                        responseHeaders = resHeaders,
                        bodyFile = body != null && body.Length > 0 ? fname + ".bin" : null,
                        bodyBytes = body != null ? body.Length : 0
                    };
                    lock (_index) { _index.Add(entry); SaveIndex(); }
                }
                catch (Exception ex) { DebugLog.Write("캡처", "응답 기록 실패: " + ex.Message); }
            };
        }

        /// <summary>쿠키 값은 남기지 않는다(이름만). 세션을 훔칠 수 있는 값이다.</summary>
        private static string Redact(string name, string value)
        {
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                return string.Join("; ", CookieNames(value, ';'));
            if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                // 이름과 속성(Path, HttpOnly 등)만. 값 자리는 <redacted>.
                var parts = value.Split(';');
                int eq = parts[0].IndexOf('=');
                parts[0] = (eq > 0 ? parts[0].Substring(0, eq) : parts[0]) + "=<redacted>";
                return string.Join(";", parts);
            }
            return value;
        }

        private static IEnumerable<string> CookieNames(string v, char sep)
        {
            foreach (var p in v.Split(sep))
            {
                string t = p.Trim(); int eq = t.IndexOf('=');
                if (t.Length > 0) yield return (eq > 0 ? t.Substring(0, eq) : t) + "=<redacted>";
            }
        }

        /// <summary>폼으로 보낸 필드의 이름만. 값(비밀번호 등)은 절대 남기지 않는다.</summary>
        private static List<string> PostFieldNames(CoreWebView2WebResourceRequest req)
        {
            var names = new List<string>();
            if (!string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase)) return names;
            try
            {
                var st = req.Content;
                if (st == null) return names;
                string text;
                using (var sr = new StreamReader(st, Encoding.GetEncoding(28591))) text = sr.ReadToEnd();   // 바이트 그대로 문자로
                string ct = "";
                foreach (var h in req.Headers) if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) ct = h.Value;
                if (ct.IndexOf("multipart", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (Match m in Regex.Matches(text, "name=\"([^\"]*)\"")) names.Add(m.Groups[1].Value);
                }
                else
                {
                    foreach (var kv in text.Split('&'))
                    {
                        int eq = kv.IndexOf('=');
                        if (eq > 0) names.Add(Uri.UnescapeDataString(kv.Substring(0, eq)));
                    }
                }
            }
            catch { }
            return names;
        }

        private static string Slug(string path)
        {
            string s = Regex.Replace(path.Trim('/'), "[^A-Za-z0-9._-]+", "_");
            if (s.Length == 0) s = "root";
            return s.Length > 60 ? s.Substring(s.Length - 60) : s;
        }

        private void SaveIndex()
        {
            File.WriteAllText(Path.Combine(_outDir, "index.json"),
                JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                new UTF8Encoding(false));
        }

        private static Task<bool> Nav(CoreWebView2 cw, string url)
        {
            var tcs = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> h = null;
            h = (s, e) => { cw.NavigationCompleted -= h; tcs.TrySetResult(e.IsSuccess); };
            cw.NavigationCompleted += h;
            cw.Navigate(url);
            return Task.WhenAny(tcs.Task, Task.Delay(20000)).ContinueWith(t => tcs.Task.IsCompleted && tcs.Task.Result);
        }

        /// <summary>각 날짜 페이지의 폼 요소 이름·종류를 적어 둔다(원본 HTML 과 함께 목업의 기준이 된다).</summary>
        private const string FormScan = @"(function(){try{
 var out={url:location.href,title:document.title,charset:document.characterSet,forms:[],fields:[],scripts:[],hasPassword:!!document.querySelector('input[type=password]')};
 for(var i=0;i<document.forms.length;i++){var f=document.forms[i];out.forms.push({name:f.name,action:f.getAttribute('action'),method:f.method,enctype:f.enctype,acceptCharset:f.acceptCharset});}
 var els=document.querySelectorAll('input,select,textarea,button');
 for(var j=0;j<els.length;j++){var e=els[j];var o={tag:e.tagName.toLowerCase(),type:e.type||'',name:e.name||'',id:e.id||''};
  if(e.tagName==='SELECT'){o.options=[];for(var k=0;k<e.options.length;k++)o.options.push({value:e.options[k].value,text:e.options[k].text});o.selectedIndex=e.selectedIndex;}
  if(e.tagName==='TEXTAREA'){o.valueLength=(e.value||'').length;}
  out.fields.push(o);}
 var ss=document.scripts;for(var m=0;m<ss.length;m++){out.scripts.push(ss[m].src||('inline '+(ss[m].text||'').length+'자'));}
 var fns=['goLogin','Encrypt','Bmodify','Bwrite','go_list','go_view'];out.functions={};for(var q=0;q<fns.length;q++){out.functions[fns[q]]=typeof window[fns[q]];}
 return JSON.stringify(out);}catch(e){return JSON.stringify({error:String(e)});}})()";

        private async Task SaveScan(CoreWebView2 cw, string label)
        {
            try
            {
                string raw = await cw.ExecuteScriptAsync(FormScan);
                string json = JsonSerializer.Deserialize<string>(raw) ?? "{}";
                File.WriteAllText(Path.Combine(_outDir, "scan-" + label + ".json"), json, new UTF8Encoding(false));
            }
            catch (Exception ex) { DebugLog.Write("캡처", "구조 기록 실패: " + ex.Message); }
        }

        private async Task CaptureAsync()
        {
            _go.IsEnabled = false;
            var cw = _web.CoreWebView2;
            string id = AppConfig.NetcusId;
            try
            {
                // 로그인이 끝난 지금 화면(로그인 뒤 도착한 페이지)도 남긴다.
                await SaveScan(cw, "after-login");

                _phase = "read";
                var dates = new List<DateTime>();
                foreach (var t in _dates.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    DateTime d;
                    if (DateTime.TryParse(t, out d)) dates.Add(d.Date);
                }
                if (id.Length == 0)
                {
                    // 저장된 아이디가 없으면 URL 을 만들 수 없다. 로그인 폼에 넣은 값을 읽는 일은 하지 않는다.
                    Say("저장된 근태관리 아이디가 없습니다. 메인 창 [계정 정보 관리] 에서 아이디를 저장한 뒤 다시 실행하세요.");
                    return;
                }

                foreach (var d in dates)
                {
                    Say(string.Format("{0:yyyy-MM-dd} 읽는 중… (읽기만 합니다)", d));
                    await Nav(cw, string.Format("{0}pjm_work_view.jsp?y={1}&m={2}&d={3}&id={4}", Site, d.Year, d.Month, d.Day, Uri.EscapeDataString(id)));
                    await Task.Delay(800);   // 늦게 붙는 스크립트·이미지까지
                    await SaveScan(cw, "work_view-" + d.ToString("yyyyMMdd"));
                }

                // 로그인 안 된 상태: 쿠키가 없는 새 프로필로 같은 페이지를 연다(앱의 세션은 건드리지 않는다).
                _phase = "logged-out";
                Say("로그인 안 된 상태의 응답을 기록하는 중…");
                string tmpProfile = Path.Combine(Path.GetTempPath(), "fc_capture_wv2_" + Guid.NewGuid().ToString("N"));
                var env2 = await CoreWebView2Environment.CreateAsync(null, tmpProfile);
                var web2 = new WV.WebView2 { Visibility = Visibility.Hidden, Width = 10, Height = 10 };
                ((DockPanel)Content).Children.Add(web2);
                await web2.EnsureCoreWebView2Async(env2);
                Hook(web2.CoreWebView2, "fresh");
                var d0 = dates.Count > 0 ? dates[0] : AppConfig.NetcusDefaultDate;
                await Nav(web2.CoreWebView2, string.Format("{0}pjm_work_view.jsp?y={1}&m={2}&d={3}&id={4}", Site, d0.Year, d0.Month, d0.Day, Uri.EscapeDataString(id)));
                await Task.Delay(800);
                await SaveScan(web2.CoreWebView2, "logged-out-work_view");
                await Nav(web2.CoreWebView2, Site + "login.htm");
                await Task.Delay(800);
                await SaveScan(web2.CoreWebView2, "login");
                ((DockPanel)Content).Children.Remove(web2);
                web2.Dispose();
                try { Directory.Delete(tmpProfile, true); } catch { }

                File.WriteAllText(Path.Combine(_outDir, "README.txt"),
                    "근태관리 구조 캡처 (FileCrypt.exe --netcus-capture)\r\n"
                  + "읽기만 했음. 쿠키 값과 폼으로 보낸 값은 저장하지 않음(이름만).\r\n"
                  + "*.bin 은 사이트가 보낸 원본 바이트(euc-kr 페이지는 euc-kr 그대로), index.json 은 요청/응답 목록,\r\n"
                  + "scan-*.json 은 각 페이지의 폼·필드·스크립트 요약이다.\r\n"
                  + "받은 페이지에 실제 보고 내용과 이름이 들어 있으므로 이 폴더는 저장소에 넣지 말 것.\r\n",
                    new UTF8Encoding(true));

                _phase = "done";
                Say("완료. 응답 " + _seq + "건을 저장했습니다. 이 창을 닫아도 됩니다.\r\n" + _outDir);
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + _outDir + "\""); } catch { }
            }
            catch (Exception ex) { Say("캡처 실패: " + ex.Message); _go.IsEnabled = true; }
        }
    }
}
