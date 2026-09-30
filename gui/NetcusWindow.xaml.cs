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
    /// 올리기·가져오기 절차 자체는 NetcusJobs 에 있다. 이 창은 값을 받고, 묻고, 결과를 알린다.
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
            if (NetcusHost.MockPort > 0) { TxtHead.Text += "  [목업]"; Title += "  [목업]"; }
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
            // 실제 날짜는 올릴 때 사이트를 읽어 정한다(빈 날은 정근 8h 가 되므로 주 52시간을 넘기는 날은 건너뜀).
            TxtPlan.Text += "  ·  주 52시간을 넘기는 날은 건너뜁니다";
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

        // ------------------------------------------------------------ 올리기 / 받아오기
        // 절차는 NetcusJobs 에 있다(자가 테스트가 같은 코드를 목업 사이트에 돌린다). 창은 묻고 알리는 일만 한다.
        private NetcusJobs Jobs(NetcusGateway gw)
        {
            return new NetcusJobs(gw, Log)
            {
                ConfirmOverwrite = msg => MessageBox.Show(this, msg, "기존 내용을 덮어씁니다", MessageBoxButton.YesNo,
                                                          MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes
            };
        }

        private async Task DoUpload(NetcusGateway gw)
        {
            DateTime start = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            await _building;
            await RebuildSlotsAsync();   // 한도가 방금 바뀌었어도 지금 값으로
            if (_slots == null) throw new InvalidOperationException("올릴 계획을 세우지 못했습니다.");
            NetcusPlan.Redate(_slots, start);

            var r = await Jobs(gw).UploadAsync(_container, _slots);
            switch (r.Outcome)
            {
                case NetcusJobs.UploadOutcome.PageMissing:
                    MessageBox.Show(this, string.Format("{0:yyyy-MM-dd} 페이지를 열지 못했습니다.", r.FailedAt),
                                    "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                case NetcusJobs.UploadOutcome.Cancelled:
                    return;
                case NetcusJobs.UploadOutcome.Aborted:
                    MessageBox.Show(this,
                        string.Format("{0:yyyy-MM-dd} 저장에 실패했습니다.\r\n{1}\r\n\r\n다시 [올리기] 하면 이미 올라간 날짜는 건너뜁니다.",
                                      r.FailedAt, r.FailedWhy),
                        "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
            }

            // 가져오기 창이 다음에 이 범위로 바로 뜨게 한다(주 52시간 때문에 건너뛴 날까지 포함한 일수).
            AppConfig.NetcusLastDate = r.Start;
            AppConfig.NetcusLastDays = Math.Min(60, r.Span);

            string done = string.Format("{0}일치 중 {1}일치 확인{2}{3}", r.Total, r.Verified,
                                        r.Skipped > 0 ? string.Format(" (이미 있던 {0}일치 건너뜀)", r.Skipped) : "",
                                        r.Skipped52.Count > 0 ? string.Format("\r\n주 52시간을 넘기는 {0}일은 건너뛰었습니다.", r.Skipped52.Count) : "");
            MessageBox.Show(this,
                string.Format("{0}\r\n{1}\r\n\r\n가져올 때: 시작 {2:yyyy-MM-dd}, 일수 {3}",
                              done,
                              r.RoundTrip ? "올라간 내용으로 원본이 그대로 복원됨을 확인했습니다."
                                          : "주의: 올라간 내용이 원본과 다릅니다. 기록 창을 확인하고 다시 올리세요.",
                              r.Start, r.Span),
                r.RoundTrip ? "올리기 완료" : "올리기 확인 필요", MessageBoxButton.OK,
                r.RoundTrip ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async Task DoDownload(NetcusGateway gw)
        {
            DateTime start = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            var r = await Jobs(gw).DownloadAsync(start, ParseDays(), _outDir, ChkClear.IsChecked == true);
            if (!r.Ready)
            {
                MessageBox.Show(this, r.Why, "가져오기", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string cleared = "";
            if (r.ClearTargets > 0)
            {
                cleared = string.Format("\r\n\r\n사이트에서 {0}/{1}일치를 지웠습니다.", r.Cleared, r.ClearTargets);
                if (r.ClearAborted)
                    cleared += "\r\n중간에 사이트가 로그인을 막아 멈췄습니다. 잠시 뒤 다시 [가져오기] 하면 남은 날짜만 지웁니다.";
                else if (r.Cleared < r.ClearTargets)
                    cleared += " 남은 날짜는 사이트에서 직접 확인하세요.";
            }
            MessageBox.Show(this,
                string.Format("{0}개 파일을 복원했습니다.\r\n\r\n{1}{2}", r.Unpacked.OkCount, r.Unpacked.TargetDir, cleared),
                "가져오기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
