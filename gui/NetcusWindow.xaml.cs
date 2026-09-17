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
        private readonly string _outDir;      // 받아오기 전용
        private bool _busy;

        private NetcusWindow(bool upload, byte[] container, string outDir)
        {
            InitializeComponent();
            _upload = upload;
            _container = container;
            _outDir = outDir;

            TxtHead.Text = upload ? "근태관리로 올리기" : "근태관리에서 가져오기";
            BtnGo.Content = upload ? "올리기" : "가져오기";
            DpStart.SelectedDate = DateTime.Today;

            // 올리기는 조각 수가 날짜 수를 정하므로 '일수' 입력이 없다.
            LbDays.Visibility     = upload ? Visibility.Collapsed : Visibility.Visible;
            TxtDays.Visibility    = upload ? Visibility.Collapsed : Visibility.Visible;
            LbDaysHint.Visibility = upload ? Visibility.Collapsed : Visibility.Visible;
            LbDaysHint.Text = "일 (조각이 여러 개면 그 수만큼)";

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
            DateTime start = DpStart.SelectedDate ?? DateTime.Today;
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
                TxtPlan.Text = string.Format("계획: {0:yyyy-MM-dd} ~ {1:yyyy-MM-dd} ({2}일) 읽어서 모으기  →  {3}",
                    range[0], range[range.Count - 1], days, _outDir);
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

            Busy(true);
            NetcusClient client = null;
            try
            {
                client = new NetcusClient();
                client.Progress += Log;
                await client.InitAsync();

                Log("로그인 시도…");
                var login = await client.LoginAsync(id, pw);
                if (!login.Ok)
                {
                    Log("→ 로그인 실패: " + login.Reason);
                    if (!string.IsNullOrWhiteSpace(login.PageText)) Log("   사이트 화면: " + login.PageText);
                    client.ShowWindow();   // 무슨 화면이 떠 있는지 직접 보게 한다
                    MessageBox.Show(this,
                        login.Reason + "\r\n\r\n열어 둔 브라우저 창에서 직접 확인해 보세요.",
                        "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Log("→ 로그인 성공");
                AppConfig.NetcusVerifiedAt = DateTime.UtcNow;   // 잘 되는 조합임이 확인됐다

                if (_upload) await DoUpload(client);
                else         await DoDownload(client);
            }
            catch (Exception ex)
            {
                Log("오류: " + ex.Message);
                MessageBox.Show(this, ex.Message, "근태관리 연동", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (client != null) client.Dispose();
                Busy(false);
            }
        }

        // ------------------------------------------------------------ 올리기
        private async Task DoUpload(NetcusClient client)
        {
            DateTime start = DpStart.SelectedDate ?? DateTime.Today;
            _slots = NetcusPlan.Build(_container, start, AppConfig.NetcusLimit);
            Log(string.Format("계획: {0}", NetcusPlan.Describe(_slots)));

            // 먼저 전부 읽어 본다 — 무엇을 덮어쓰게 되는지 알고 시작해야 한다.
            Log("대상 날짜의 기존 내용을 확인하는 중…");
            foreach (var s in _slots)
            {
                string cur = await client.ReadDayAsync(s.Date);
                if (cur == null)
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
                var r = await client.WriteDayAsync(s.Date, s.Text);
                Log(string.Format("  {0:yyyy-MM-dd} [{1}/{2}] {3} — {4}",
                    s.Date, s.Index, s.Total, r.Ok ? "성공" : "실패", r.Message));
                if (!r.Ok)
                {
                    Log("→ 중단합니다. 이미 올라간 날짜는 그대로 남아 있습니다.");
                    MessageBox.Show(this,
                        string.Format("{0:yyyy-MM-dd} 저장에 실패했습니다.\r\n{1}", s.Date, r.Message),
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
        private async Task DoDownload(NetcusClient client)
        {
            DateTime start = DpStart.SelectedDate ?? DateTime.Today;
            int days = ParseDays();
            var dates = NetcusPlan.DateRange(start, days);
            Log(string.Format("{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd} 읽는 중…", dates[0], dates[dates.Count - 1]));

            var contents = new List<string>();
            foreach (var d in dates)
            {
                string c = await client.ReadDayAsync(d);
                if (c == null) { Log(string.Format("  {0:yyyy-MM-dd}: 열지 못함 — 건너뜀", d)); continue; }
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
            MessageBox.Show(this,
                string.Format("{0}개 파일을 복원했습니다.\r\n\r\n{1}", res.OkCount, res.TargetDir),
                "가져오기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
