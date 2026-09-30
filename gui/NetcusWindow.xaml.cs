using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;

namespace FileCrypt
{
    /// <summary>
    /// 사내 보고 시스템(근태관리)에 올리고 받아오는 창.
    ///
    /// 올리기: 고른 파일을 컨테이너 하나로 묶고, 한도에 맞춰 나눠, 시작 날짜부터 하루 1조각씩 기록한다.
    /// 받아오기: 시작 날짜부터 지정한 일수만큼 보고 칸을 읽어 모아 파일로 되돌린다.
    ///
    /// 근태(status)와 초과시간은 절대 건드리지 않는다. 이미 내용이 있는 날짜는 반드시 물어본다.
    ///
    /// 두 방향이 같이 쓰는 절차는 SubmitAllAsync(날짜마다 제출, 막히면 멈춤)와
    /// VerifyAsync(범위 읽기 한 번으로 확인) 두 개로 모았다.
    /// </summary>
    public partial class NetcusWindow : Window
    {
        private readonly bool _upload;
        private readonly byte[] _container;   // 올리기 전용
        private string _outDir;               // 받아오기 전용(화면에서 바꿀 수 있다)
        private bool _busy;

        private NetcusWindow(bool upload, byte[] container, string outDir, int chunkChars)
        {
            InitializeComponent();
            _upload = upload;
            _container = container;
            _outDir = outDir;

            TxtHead.Text = upload ? "근태관리로 올리기" : "근태관리에서 가져오기";
            BtnGo.Content = upload ? "올리기" : "가져오기";

            // 같은 날짜를 계속 쓰는 경우가 많다. 지난번 값이 있으면 그대로,
            // 없으면 기본 날짜(오늘이 아니다 — 실제 근무일을 건드리지 않으려고).
            DpStart.SelectedDate = AppConfig.NetcusStartDate;

            // 올리기는 조각 수가 날짜 수를 정하므로 '일수' 입력이 없다.
            LbDays.Visibility     = upload ? Visibility.Collapsed : Visibility.Visible;
            TxtDays.Visibility    = upload ? Visibility.Collapsed : Visibility.Visible;
            LbDaysHint.Visibility = upload ? Visibility.Collapsed : Visibility.Visible;
            LbDaysHint.Text = "일 (조각이 여러 개면 그 수만큼)";

            RowOut.Visibility   = upload ? Visibility.Collapsed : Visibility.Visible;
            ChkClear.Visibility = upload ? Visibility.Collapsed : Visibility.Visible;
            RowChunk.Visibility = upload ? Visibility.Visible : Visibility.Collapsed;
            if (upload)
            {
                int c = chunkChars > 0 ? chunkChars : AppConfig.NetcusLimit;
                TxtChunk.Text = c.ToString();
                LbChunkHint.Text = "자  (메인 창 [조각내기] 에서 가져옴 — 여기서 바꿔도 됩니다)";
            }
            else
            {
                TxtDays.Text = AppConfig.NetcusLastDays.ToString();
                // 지난번 저장 폴더가 아직 있으면 그걸 쓴다. 없으면 넘겨받은 값.
                string last = AppConfig.NetcusLastOutDir;
                TxtOut.Text = last.Length > 0 ? last : (outDir ?? "");
            }

            _planTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _planTimer.Tick += async (s, e) => { _planTimer.Stop(); await RebuildSlotsAsync(); };

            RefreshAccount();
            UpdatePlan();
        }

        /// <summary>계정은 상단 [계정 정보] 에서 한 번만 저장한다. 여기서는 상태만 보여 준다.</summary>
        private void RefreshAccount()
        {
            if (AppConfig.HasNetcusAccount)
            {
                DateTime? at = AppConfig.NetcusVerifiedAt;
                TxtCredNote.Text = string.Format("저장된 계정 사용: {0}{1}",
                    AppConfig.NetcusId,
                    at.HasValue ? string.Format("  (로그인 확인 {0:yyyy-MM-dd HH:mm})", at.Value.ToLocalTime()) : "");
            }
            else
            {
                TxtCredNote.Text = "저장된 계정이 없습니다. [계정 정보] 에서 먼저 저장하세요.";
            }
            BtnGo.IsEnabled = AppConfig.HasNetcusAccount && !_busy;
        }

        private void BtnAccount_Click(object sender, RoutedEventArgs e)
        {
            AccountWindow.Show(this);
            RefreshAccount();
        }

        public static void Upload(Window owner, byte[] container, int chunkChars)
        {
            var w = new NetcusWindow(true, container, null, chunkChars) { Owner = owner };
            w.ShowDialog();
        }

        public static void Download(Window owner, string outDir)
        {
            var w = new NetcusWindow(false, null, outDir, 0) { Owner = owner };
            w.ShowDialog();
        }

        // ------------------------------------------------------------ 계획 표시
        private void Dp_Changed(object sender, EventArgs e)
        {
            if (!IsLoaded) return;
            UpdatePlan();
        }

        // 올리기 조각. 조각은 한도(글자수)로만 정해지고 날짜와 무관하다 - 날짜를 바꾸면 Redate 만 한다.
        // 한도 칸은 글자를 칠 때마다 바뀌므로 곧바로 만들지 않고 잠깐 기다렸다(300ms) 작업 스레드에서 만든다.
        // 예전에는 글자 하나마다 컨테이너 전체를 UI 스레드에서 두 번씩 인코딩했다.
        private List<NetcusPlan.Slot> _slots;
        private int _slotsLimit;
        private readonly DispatcherTimer _planTimer;
        private Task _building = Task.FromResult(0);

        private void UpdatePlan()
        {
            DateTime start = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            if (_upload)
            {
                int limit = ParseChunk();
                if (_slots != null && limit == _slotsLimit)
                {
                    NetcusPlan.Redate(_slots, start);
                    ShowUploadPlan();
                }
                else
                {
                    TxtPlan.Text = "계획: 계산 중…";
                    _planTimer.Stop();
                    _planTimer.Start();
                }
            }
            else
            {
                int days = ParseDays();
                var range = NetcusPlan.DateRange(start, days);
                _outDir = TxtOut.Text.Trim();
                TxtPlan.Text = string.Format("계획: {0:yyyy-MM-dd} ~ {1:yyyy-MM-dd} ({2}일) 읽어서 모으기  →  {3}",
                    range[0], range[range.Count - 1], days, _outDir);
                if (ChkClear.IsChecked == true) TxtPlan.Text += "  ·  복원 뒤 사이트에서 지웁니다";
            }
        }

        private void ShowUploadPlan()
        {
            TxtPlan.Text = "계획: " + NetcusPlan.Describe(_slots);
            if (_slots.Count > 1)
                TxtPlan.Text += string.Format("  —  일간보고는 날짜당 칸이 하나라 {0}일치를 씁니다.", _slots.Count);
        }

        /// <summary>지금 한도로 조각을 (다시) 만든다. 이미 그 한도로 만들어져 있으면 날짜만 맞춘다.</summary>
        private async Task RebuildSlotsAsync()
        {
            DateTime start = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            int limit = ParseChunk();
            if (_slots != null && limit == _slotsLimit) { NetcusPlan.Redate(_slots, start); ShowUploadPlan(); return; }
            try
            {
                var build = Task.Run(() => NetcusPlan.Build(_container, start, limit));
                _building = build;
                var slots = await build;
                if (limit != ParseChunk()) return;   // 계산하는 동안 한도가 또 바뀌었다 - 다음 계산이 이어받는다
                _slots = slots; _slotsLimit = limit;
                NetcusPlan.Redate(_slots, DpStart.SelectedDate ?? AppConfig.NetcusStartDate);
                ShowUploadPlan();
            }
            catch (Exception ex) { TxtPlan.Text = "계획을 세우지 못했습니다: " + ex.Message; }
        }

        private void BtnOut_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "복원한 파일을 저장할 폴더";
                if (Directory.Exists(TxtOut.Text.Trim())) dlg.SelectedPath = TxtOut.Text.Trim();
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    TxtOut.Text = dlg.SelectedPath;
                    UpdatePlan();
                }
            }
        }

        /// <summary>한 날짜에 담을 글자수. 너무 작으면 조각만 늘어나므로 하한을 둔다.</summary>
        private int ParseChunk()
        {
            int v;
            if (!int.TryParse((TxtChunk.Text ?? "").Trim().Replace(",", ""), out v) || v < 1000)
                v = AppConfig.NetcusLimit;
            return v;
        }

        private int ParseDays()
        {
            int d;
            if (!int.TryParse((TxtDays.Text ?? "").Trim(), out d) || d < 1) d = 1;
            if (d > 60) d = 60;   // 31일이 넘으면 게이트웨이가 31일씩 나눠 읽는다
            return d;
        }

        // ------------------------------------------------------------ 로그
        /// <summary>
        /// 기록 한 줄. 줄마다 Text 를 통째로 다시 만들지 않고(길어질수록 느려진다) 줄을 덧붙인다.
        /// 어느 스레드에서 불러도 된다 - 화면 갱신을 기다리지 않는다.
        /// </summary>
        private void Log(string s)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (TxtLog.Inlines.Count > 0) TxtLog.Inlines.Add(new LineBreak());
                TxtLog.Inlines.Add(new Run(s));
                LogScroll.ScrollToEnd();
            }));
        }

        private void Busy(bool on)
        {
            _busy = on;
            Bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            BtnGo.IsEnabled = !on;
            DpStart.IsEnabled = !on;
            TxtDays.IsEnabled = !on;
            BtnAccount.IsEnabled = !on;
            TxtOut.IsEnabled = !on;
            BtnOut.IsEnabled = !on;
            ChkClear.IsEnabled = !on;
            TxtChunk.IsEnabled = !on;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                MessageBox.Show(this, "진행 중입니다. 끝난 뒤에 닫아 주세요.", "근태관리 연동",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Close();
        }

        // ------------------------------------------------------------ 실행
        private async void BtnGo_Click(object sender, RoutedEventArgs e)
        {
            string id = AppConfig.NetcusId;
            string pw = AppConfig.NetcusPassword;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw))
            {
                MessageBox.Show(this, "저장된 계정이 없습니다. [계정 정보] 에서 먼저 저장하세요.",
                                "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 이번에 쓴 값을 기억해 둔다 — 다음에 창을 열면 그대로 뜬다.
            AppConfig.NetcusLastDate = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            if (_upload)
            {
                AppConfig.NetcusLimit = ParseChunk();   // 다음에도 같은 한도로 뜨게
            }
            else
            {
                AppConfig.NetcusLastDays = ParseDays();
                string od = TxtOut.Text.Trim();
                if (od.Length > 0) AppConfig.NetcusLastOutDir = od;
            }

            Busy(true);
            NetcusGateway gw = null;
            try
            {
                gw = new NetcusGateway();
                gw.Progress += s2 => Log("  " + s2);
                gw.Logged   += s2 => Log("  " + s2);

                // 저장된 자격증명이 이미 검증돼 있으면 확인 로그인을 건너뛴다(기록·읽기가 스스로 로그인한다).
                var login = await gw.EnsureLoginAsync(id, pw);
                if (!login.Key)
                {
                    Log("→ 로그인 실패. [계정 정보] 에서 아이디·비밀번호를 확인하세요.");
                    MessageBox.Show(this, "로그인에 실패했습니다.\r\n[계정 정보] 에서 확인해 주세요.",
                                    "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (login.Value) Log("저장된 계정으로 진행합니다 (확인 로그인 생략)");
                else
                {
                    Log("→ 로그인 성공");
                    AppConfig.NetcusVerifiedAt = DateTime.UtcNow;
                }

                if (_upload) await DoUpload(gw);
                else         await DoDownload(gw);
            }
            catch (Exception ex)
            {
                Log("오류: " + ex.Message);
                MessageBox.Show(this, ex.Message, "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (gw != null) gw.Dispose();
                Busy(false);
            }
        }

        // ------------------------------------------------------------ 공통 절차
        /// <summary>SubmitAllAsync 결과.</summary>
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
        /// 날짜 사이에는 AppConfig.NetcusPaceMs 만큼 쉰다.
        /// </summary>
        private async Task<SubmitOutcome> SubmitAllAsync(NetcusGateway gw, IList<KeyValuePair<DateTime, string>> jobs)
        {
            var o = new SubmitOutcome();
            int pace = AppConfig.NetcusPaceMs;
            for (int i = 0; i < jobs.Count; i++)
            {
                DateTime d = jobs[i].Key;
                string text = jobs[i].Value;
                if (i > 0 && pace > 0) await Task.Delay(pace);
                try
                {
                    // 근태는 건드리지 않는다(status="" 규약). 저장 후 되읽기 검증도 NetcusService 가 한다.
                    var r = text != null ? await gw.SubmitDayAsync(d, text, 0) : await gw.ClearDaySubmitAsync(d);
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

        /// <summary>
        /// dates 를 범위 읽기 한 번으로 다시 읽어 good(날짜, 사이트 내용) 인 날짜 수를 센다.
        /// 날짜마다 따로 열면 로그인·페이지 이동이 날짜 수만큼 늘어난다.
        /// </summary>
        private async Task<KeyValuePair<int, Dictionary<DateTime, string>>> VerifyAsync(
            NetcusGateway gw, IList<DateTime> dates, Func<DateTime, string, bool> good, string okWord)
        {
            int n = 0;
            Dictionary<DateTime, string> after = new Dictionary<DateTime, string>();
            if (dates.Count == 0) return new KeyValuePair<int, Dictionary<DateTime, string>>(0, after);
            try
            {
                after = await gw.ReadDaysAsync(dates.Min(), dates.Max());
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
        private async Task DoUpload(NetcusGateway gw)
        {
            DateTime start = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            await _building;
            await RebuildSlotsAsync();   // 한도가 방금 바뀌었어도 지금 값으로
            if (_slots == null) throw new InvalidOperationException("올릴 계획을 세우지 못했습니다.");
            NetcusPlan.Redate(_slots, start);
            Log(string.Format("계획: {0}", NetcusPlan.Describe(_slots)));

            // 먼저 전부 읽어 본다 — 무엇을 덮어쓰게 되는지 알고 시작해야 한다.
            // 날짜를 하나씩 열지 않고 범위로 한 번에 읽는다(NetcusService 의 범위 읽기).
            Log("대상 날짜의 기존 내용을 확인하는 중…");
            var existing = await gw.ReadDaysAsync(_slots[0].Date, _slots[_slots.Count - 1].Date);
            var todo = new List<NetcusPlan.Slot>();
            var occupied = new List<NetcusPlan.Slot>();
            foreach (var s in _slots)
            {
                string cur;
                if (!existing.TryGetValue(s.Date, out cur))
                {
                    Log(string.Format("  {0:yyyy-MM-dd}: 페이지를 열지 못했습니다 — 중단합니다.", s.Date));
                    MessageBox.Show(this, string.Format("{0:yyyy-MM-dd} 페이지를 열지 못했습니다.", s.Date),
                                    "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                s.Existing = cur;
                // 지난번에 중간에 멈췄다면 이미 올라간 조각이 있다. 그 날짜는 덮어쓰기 경고도, 다시 올리기도 하지 않는다.
                if (NetcusPlan.SameContent(cur, s.Text))
                {
                    Log(string.Format("  {0:yyyy-MM-dd}: 이미 같은 조각이 올라가 있음 — 건너뜀", s.Date));
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
                if (MessageBox.Show(this, msg, "기존 내용을 덮어씁니다", MessageBoxButton.YesNo,
                                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    Log("사용자가 취소했습니다. 아무것도 올리지 않았습니다.");
                    return;
                }
                BackupOccupied(occupied);
            }

            var outcome = await SubmitAllAsync(gw, todo.Select(s => new KeyValuePair<DateTime, string>(s.Date, s.Text)).ToList());
            if (outcome.Aborted)
            {
                Log("→ 중단합니다. 이미 올라간 날짜는 그대로 남아 있습니다. 다시 [올리기] 하면 남은 날짜만 올립니다.");
                MessageBox.Show(this,
                    string.Format("{0:yyyy-MM-dd} 저장에 실패했습니다.\r\n{1}\r\n\r\n다시 [올리기] 하면 이미 올라간 날짜는 건너뜁니다.",
                                  outcome.FailedAt, outcome.FailedWhy),
                    "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 확인: 저장 직후 검증(NetcusService)은 내용이 비어 있지 않은지만 본다 - 본문이 ASCII 라 한글 대조가
            // 빠지기 때문이다. 사이트가 글을 잘라 저장해도 성공으로 나올 수 있어, 전체 범위를 다시 읽어
            // 날짜마다 내용이 같은지, 그리고 모아서 원래 컨테이너로 되돌아오는지까지 본다.
            Log("올라간 내용을 다시 읽어 확인하는 중…");
            var check = await VerifyAsync(gw, _slots.Select(s => s.Date).ToList(),
                (d, c) => NetcusPlan.SameContent(c, _slots.First(s => s.Date == d).Text), "일치");
            bool roundTrip = false;
            try
            {
                var back = FileCryptCore.ExtractBlocks(NetcusPlan.Assemble(_slots.Select(s =>
                {
                    string c; return check.Value.TryGetValue(s.Date, out c) ? c : "";
                })));
                roundTrip = back.Count == 1 && back[0].SequenceEqual(_container);
            }
            catch { }
            Log(roundTrip ? "→ 다시 모아 원본과 바이트 단위로 같음을 확인했습니다."
                          : "→ 다시 모았을 때 원본과 같지 않습니다. 위에서 '다름' 인 날짜를 확인하세요.");

            string done = string.Format("{0}일치 중 {1}일치 확인{2}", _slots.Count, check.Key,
                                        _slots.Count - todo.Count > 0 ? string.Format(" (이미 있던 {0}일치 건너뜀)", _slots.Count - todo.Count) : "");
            Log(string.Format("완료: {0}. 받아올 때는 {1:yyyy-MM-dd} 부터 {2}일로 가져오세요.", done, _slots[0].Date, _slots.Count));
            MessageBox.Show(this,
                string.Format("{0}\r\n{1}\r\n\r\n가져올 때: 시작 {2:yyyy-MM-dd}, 일수 {3}",
                              done,
                              roundTrip ? "올라간 내용으로 원본이 그대로 복원됨을 확인했습니다."
                                        : "주의: 올라간 내용이 원본과 다릅니다. 기록 창을 확인하고 다시 올리세요.",
                              _slots[0].Date, _slots.Count),
                roundTrip ? "올리기 완료" : "올리기 확인 필요", MessageBoxButton.OK,
                roundTrip ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private static string BackupDir
        {
            get { return Path.Combine(AppConfig.Dir, "backup"); }
        }

        /// <summary>덮어쓰기 전 내용을 로컬에 남긴다. 사라지면 되돌릴 방법이 없다.</summary>
        private void BackupOccupied(IList<NetcusPlan.Slot> occupied)
        {
            try
            {
                string dir = BackupDir;
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir,
                    string.Format("일간보고 백업 {0:yyyyMMdd-HHmmss}.txt", DateTime.Now));

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
            }
            catch (Exception ex) { Log("백업 실패(계속 진행): " + ex.Message); }
        }

        // ------------------------------------------------------------ 받아오기
        private async Task DoDownload(NetcusGateway gw)
        {
            DateTime start = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            int days = ParseDays();
            var dates = NetcusPlan.DateRange(start, days);
            Log(string.Format("{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd} 읽는 중…", dates[0], dates[dates.Count - 1]));

            var map = await gw.ReadDaysAsync(dates[0], dates[dates.Count - 1]);
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
                string why = g.Pending.Count > 0
                    ? FileCryptJobs.DescribePending(g.Pending)
                    : "FileCrypt 블록을 찾지 못했습니다. 날짜 범위를 확인하세요.";
                Log("→ " + why);
                MessageBox.Show(this, why, "가져오기", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outDir = _outDir;
            var res = await Task.Run(() => FileCryptJobs.Unpack(new List<string> { g.Text }, outDir));
            foreach (var err in res.Errors) Log("  ! " + err);

            Log(string.Format("완료: {0}개 복원 ({1:N0} B) → {2}", res.OkCount, res.TotalBytes, res.TargetDir));

            // 복원에 성공한 뒤에만 지운다. 실패했는데 지우면 되돌릴 방법이 없다.
            string cleared = "";
            if (res.OkCount > 0 && ChkClear.IsChecked == true)
                cleared = await ClearDays(gw, dates, map);

            MessageBox.Show(this,
                string.Format("{0}개 파일을 복원했습니다.\r\n\r\n{1}{2}", res.OkCount, res.TargetDir, cleared),
                "가져오기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 가져온 날짜들의 보고 칸을 비운다.
        /// 복원이 성공한 뒤에만 부른다 — 옮겨 담은 뒤 원본을 치우는 것이라 순서가 중요하다.
        /// 근태·초과시간은 건드리지 않는다(status="" 규약).
        /// </summary>
        private async Task<string> ClearDays(NetcusGateway gw, List<DateTime> dates,
                                             Dictionary<DateTime, string> had)
        {
            var targets = new List<DateTime>();
            foreach (var d in dates)
            {
                string c;
                if (had.TryGetValue(d, out c) && !string.IsNullOrWhiteSpace(c)) targets.Add(d);
            }
            if (targets.Count == 0) return "";

            Log(string.Format("사이트에서 {0}일치 내용을 지우는 중…", targets.Count));

            // 제출만 먼저 몰아서 한다. 날짜마다 확인까지 하면 로그인이 두 배가 되고,
            // 사이트가 짧은 시간에 몰린 로그인을 막아 버린다(15일치에서 실제로 막혔다).
            var outcome = await SubmitAllAsync(gw, targets.Select(d => new KeyValuePair<DateTime, string>(d, null)).ToList());

            // 확인은 범위 읽기 한 번으로 끝낸다 — 실제 내용을 읽어 비었는지 본다.
            int done = 0;
            if (outcome.Submitted.Count > 0)
            {
                Log("지워졌는지 확인하는 중…");
                done = (await VerifyAsync(gw, outcome.Submitted, (d, c) => string.IsNullOrWhiteSpace(c), "비움 확인")).Key;
            }

            string msg = string.Format("\r\n\r\n사이트에서 {0}/{1}일치를 지웠습니다.", done, targets.Count);
            if (outcome.Aborted)
                msg += "\r\n중간에 사이트가 로그인을 막아 멈췄습니다. 잠시 뒤 다시 [가져오기] 하면 남은 날짜만 지웁니다.";
            else if (done < targets.Count)
                msg += " 남은 날짜는 사이트에서 직접 확인하세요.";
            Log("지우기 완료: " + done + "/" + targets.Count + (outcome.Aborted ? " (중단됨)" : ""));
            return msg;
        }
    }
}
