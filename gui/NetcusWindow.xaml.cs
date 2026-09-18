using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace FileCrypt
{
    /// <summary>
    /// 사내 보고 시스템(근태관리)에 올리고 받아오는 창.
    ///
    /// 올리기: 고른 파일을 컨테이너 하나로 묶고, 한도에 맞춰 나눠, 시작 날짜부터 하루 1조각씩 기록한다.
    /// 받아오기: 시작 날짜부터 지정한 일수만큼 보고 칸을 읽어 모아 파일로 되돌린다.
    ///
    /// 근태(status)와 초과시간은 절대 건드리지 않는다. 이미 내용이 있는 날짜는 반드시 물어본다.
    /// </summary>
    public partial class NetcusWindow : Window
    {
        private readonly bool _upload;
        private readonly byte[] _container;   // 올리기 전용
        private string _outDir;               // 받아오기 전용(화면에서 바꿀 수 있다)
        private bool _busy;

        private NetcusWindow(bool upload, byte[] container, string outDir)
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
            if (!upload)
            {
                TxtDays.Text = AppConfig.NetcusLastDays.ToString();
                // 지난번 저장 폴더가 아직 있으면 그걸 쓴다. 없으면 넘겨받은 값.
                string last = AppConfig.NetcusLastOutDir;
                TxtOut.Text = last.Length > 0 ? last : (outDir ?? "");
            }

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

        public static void Upload(Window owner, byte[] container)
        {
            var w = new NetcusWindow(true, container, null) { Owner = owner };
            w.ShowDialog();
        }

        public static void Download(Window owner, string outDir)
        {
            var w = new NetcusWindow(false, null, outDir) { Owner = owner };
            w.ShowDialog();
        }

        // ------------------------------------------------------------ 계획 표시
        private void Dp_Changed(object sender, EventArgs e)
        {
            if (!IsLoaded) return;
            UpdatePlan();
        }

        private List<NetcusPlan.Slot> _slots;

        private void UpdatePlan()
        {
            DateTime start = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            if (_upload)
            {
                try
                {
                    _slots = NetcusPlan.Build(_container, start, AppConfig.NetcusLimit);
                    TxtPlan.Text = "계획: " + NetcusPlan.Describe(_slots);
                    if (_slots.Count > 1)
                        TxtPlan.Text += string.Format("  —  일간보고는 날짜당 칸이 하나라 {0}일치를 씁니다.", _slots.Count);
                }
                catch (Exception ex) { TxtPlan.Text = "계획을 세우지 못했습니다: " + ex.Message; }
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

        private int ParseDays()
        {
            int d;
            if (!int.TryParse((TxtDays.Text ?? "").Trim(), out d) || d < 1) d = 1;
            if (d > 60) d = 60;
            return d;
        }

        // ------------------------------------------------------------ 로그
        private void Log(string s)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                TxtLog.Text += (TxtLog.Text.Length > 0 ? "\r\n" : "") + s;
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
            if (!_upload)
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

                Log("로그인 시도…");
                if (!await gw.LoginVerifyAsync(id, pw))
                {
                    Log("→ 로그인 실패. [계정 정보] 에서 아이디·비밀번호를 확인하세요.");
                    MessageBox.Show(this, "로그인에 실패했습니다.\r\n[계정 정보] 에서 확인해 주세요.",
                                    "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Log("→ 로그인 성공");
                AppConfig.NetcusVerifiedAt = DateTime.UtcNow;

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

        // ------------------------------------------------------------ 올리기
        private async Task DoUpload(NetcusGateway gw)
        {
            DateTime start = DpStart.SelectedDate ?? AppConfig.NetcusStartDate;
            _slots = NetcusPlan.Build(_container, start, AppConfig.NetcusLimit);
            Log(string.Format("계획: {0}", NetcusPlan.Describe(_slots)));

            // 먼저 전부 읽어 본다 — 무엇을 덮어쓰게 되는지 알고 시작해야 한다.
            // 날짜를 하나씩 열지 않고 범위로 한 번에 읽는다(NetcusService 의 범위 읽기).
            Log("대상 날짜의 기존 내용을 확인하는 중…");
            var existing = await gw.ReadDaysAsync(_slots[0].Date, _slots[_slots.Count - 1].Date);
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
                Log(string.Format("  {0:yyyy-MM-dd}: {1}", s.Date,
                    s.ExistingHasContent ? "내용 있음 (" + NetcusPlan.Preview(cur, 30) + ")" : "빈 칸"));
            }

            var occupied = NetcusPlan.Occupied(_slots);
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

            int ok = 0;
            foreach (var s in _slots)
            {
                // 근태는 건드리지 않는다(status="" 규약). 저장 후 되읽기 검증도 NetcusService 가 한다.
                var r = await gw.SubmitDayAsync(s.Date, s.Text, 0);
                Log(string.Format("  {0:yyyy-MM-dd} [{1}/{2}] {3} — {4}",
                    s.Date, s.Index, s.Total, r.Key ? "성공" : "실패", r.Value));
                if (!r.Key)
                {
                    Log("→ 중단합니다. 이미 올라간 날짜는 그대로 남아 있습니다.");
                    MessageBox.Show(this,
                        string.Format("{0:yyyy-MM-dd} 저장에 실패했습니다.\r\n{1}", s.Date, r.Value),
                        "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ok++;
            }

            Log(string.Format("완료: {0}일치 기록. 받아올 때는 {1:yyyy-MM-dd} 부터 {0}일로 가져오세요.",
                              ok, _slots[0].Date));
            MessageBox.Show(this,
                string.Format("{0}일치를 올렸습니다.\r\n\r\n가져올 때: 시작 {1:yyyy-MM-dd}, 일수 {0}",
                              ok, _slots[0].Date),
                "올리기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string BackupDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileCrypt", "backup");
            }
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

            var texts = new List<string> { g.Text };
            var res = FileCryptJobs.Unpack(texts, _outDir);
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
            int done = 0;
            foreach (var d in targets)
            {
                try
                {
                    var r = await gw.ClearDayAsync(d);
                    Log(string.Format("  {0:yyyy-MM-dd} {1} — {2}", d, r.Key ? "지움" : "실패", r.Value));
                    if (r.Key) done++;
                }
                catch (Exception ex) { Log(string.Format("  {0:yyyy-MM-dd} 지우기 오류: {1}", d, ex.Message)); }
            }

            string msg = string.Format("\r\n\r\n사이트에서 {0}/{1}일치를 지웠습니다.", done, targets.Count);
            if (done < targets.Count) msg += " 남은 날짜는 사이트에서 직접 확인하세요.";
            Log("지우기 완료: " + done + "/" + targets.Count);
            return msg;
        }
    }
}
