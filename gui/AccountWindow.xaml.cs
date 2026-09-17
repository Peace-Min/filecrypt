using System;
using System.Windows;

namespace FileCrypt
{
    /// <summary>
    /// 근태관리 계정 정보를 한 곳에서 관리한다.
    /// 여기서 한 번 저장하면 올리기·가져오기 창은 더 이상 로그인 정보를 묻지 않는다.
    /// </summary>
    public partial class AccountWindow : Window
    {
        private bool _busy;

        public AccountWindow()
        {
            InitializeComponent();
            TxtId.Text = AppConfig.NetcusId;
            TxtPath.Text = "저장 위치: " + AppConfig.File_;
            RefreshState();
        }

        public static void Show(Window owner)
        {
            var w = new AccountWindow { Owner = owner };
            w.ShowDialog();
        }

        private void Field_Changed(object sender, EventArgs e)
        {
            if (!IsLoaded) return;
            RefreshState();
        }

        private void RefreshState()
        {
            bool saved = AppConfig.HasNetcusAccount;

            TxtPwNote.Text = saved
                ? "저장된 비밀번호가 있습니다. 바꾸지 않으려면 비워 두세요."
                : "비밀번호가 저장돼 있지 않습니다.";

            if (!saved)
            {
                TxtState.Text = "아직 저장된 계정이 없습니다. 아이디와 비밀번호를 넣고 [저장] 을 누르세요.";
            }
            else
            {
                DateTime? at = AppConfig.NetcusVerifiedAt;
                TxtState.Text = string.Format("저장됨 — 아이디 {0}", AppConfig.NetcusId);
                TxtState.Text += at.HasValue
                    ? string.Format("\r\n마지막 로그인 확인: {0:yyyy-MM-dd HH:mm}", at.Value.ToLocalTime())
                    : "\r\n아직 로그인 확인을 하지 않았습니다. [로그인 확인] 을 눌러 한 번 맞춰 보세요.";
            }

            BtnForget.IsEnabled = saved && !_busy;
            BtnTest.IsEnabled = !_busy && (TxtId.Text.Trim().Length > 0) &&
                                (TxtPw.Password.Length > 0 || saved);
        }

        private void Busy(bool on)
        {
            _busy = on;
            Bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            BtnSave.IsEnabled = !on;
            BtnClose.IsEnabled = !on;
            BtnProbe.IsEnabled = !on;
            TxtId.IsEnabled = !on;
            TxtPw.IsEnabled = !on;
            RefreshState();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string id = TxtId.Text.Trim();
            if (id.Length == 0)
            {
                MessageBox.Show(this, "아이디를 입력하세요.", "계정 정보",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppConfig.NetcusId = id;
            if (TxtPw.Password.Length > 0)
            {
                AppConfig.NetcusPassword = TxtPw.Password;
                AppConfig.NetcusVerifiedAt = null;   // 비밀번호가 바뀌었으니 확인 기록은 무효
                TxtPw.Clear();
            }

            if (!AppConfig.HasNetcusAccount)
            {
                MessageBox.Show(this, "비밀번호도 한 번은 입력해야 저장이 끝납니다.", "계정 정보",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshState();
                return;
            }

            RefreshState();
            MessageBox.Show(this, "저장했습니다. 이제 올리기·가져오기에서 다시 묻지 않습니다.", "계정 정보",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnForget_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "저장된 아이디와 비밀번호를 지울까요?", "계정 정보",
                                MessageBoxButton.YesNo, MessageBoxImage.Question,
                                MessageBoxResult.No) != MessageBoxResult.Yes) return;

            AppConfig.ClearNetcusAccount();
            TxtId.Text = "";
            TxtPw.Clear();
            RefreshState();
        }

        /// <summary>실제로 사이트에 붙어 본다. 성공하면 확인 시각을 남긴다.</summary>
        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            string id = TxtId.Text.Trim();
            string pw = TxtPw.Password.Length > 0 ? TxtPw.Password : AppConfig.NetcusPassword;
            if (id.Length == 0 || pw.Length == 0)
            {
                MessageBox.Show(this, "아이디와 비밀번호를 입력하세요.", "계정 정보",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Busy(true);
            NetcusClient client = null;
            bool keepOpen = false;
            try
            {
                client = new NetcusClient();
                client.Progress += s => Dispatcher.Invoke(new Action(() => { TxtState.Text = s; }));
                await client.InitAsync();
                if (ChkWatch.IsChecked == true) client.ShowWindow();   // 직접 보면서 확인

                var res = await client.LoginAsync(id, pw);

                if (res.Ok)
                {
                    // 확인된 조합만 저장한다 — 틀린 걸 저장해 두면 나중에 조용히 실패한다.
                    AppConfig.NetcusId = id;
                    if (TxtPw.Password.Length > 0) { AppConfig.NetcusPassword = TxtPw.Password; TxtPw.Clear(); }
                    AppConfig.NetcusVerifiedAt = DateTime.UtcNow;
                    RefreshState();
                    MessageBox.Show(this, "로그인에 성공했습니다. 계정 정보를 저장했습니다.", "계정 정보",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    // 실패했으면 창을 띄워 둔다 — 사이트가 실제로 무슨 화면을 보여 주는지가
                    // 원인을 아는 유일한 방법일 때가 많다(비밀번호 변경 안내, 잠금, 공지 등).
                    string msg = res.Reason;
                    if (!string.IsNullOrWhiteSpace(res.PageText))
                        msg += "\r\n\r\n[자세한 정보]\r\n" + res.PageText;

                    TxtState.Text = res.Reason;
                    client.ShowWindow();
                    keepOpen = true;

                    msg += "\r\n\r\n열어 둔 브라우저 창에서 직접 확인해 보세요. 확인이 끝나면 그 창을 닫으면 됩니다.";
                    MessageBox.Show(this, msg, "로그인 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "확인 중 오류: " + ex.Message, "계정 정보",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 실패 진단용으로 띄워 둔 창은 사용자가 닫을 때까지 살려 둔다.
                if (client != null && !keepOpen) client.Dispose();
                Busy(false);
            }
        }

        /// <summary>
        /// 비밀번호 없이 사이트 쪽만 본다. "내 비밀번호가 틀린 건가, 사이트가 문제인가"를
        /// 가르는 용도 — 이게 실패하면 비밀번호를 아무리 고쳐도 소용없다.
        /// </summary>
        private async void BtnProbe_Click(object sender, RoutedEventArgs e)
        {
            Busy(true);
            NetcusClient client = null;
            bool keepOpen = false;
            try
            {
                client = new NetcusClient();
                client.Progress += s => Dispatcher.Invoke(new Action(() => { TxtState.Text = s; }));
                await client.InitAsync();
                if (ChkWatch.IsChecked == true) client.ShowWindow();

                var r = await client.CheckLoginPageAsync();
                TxtState.Text = r.Reason;

                if (r.Ok)
                {
                    MessageBox.Show(this,
                        r.Reason + "\r\n\r\n사이트 쪽은 정상입니다. 로그인이 안 된다면 아이디·비밀번호 문제입니다.",
                        "사이트 연결 확인", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    string msg = r.Reason;
                    if (!string.IsNullOrWhiteSpace(r.PageText))
                        msg += "\r\n\r\n[자세한 정보]\r\n" + r.PageText;
                    client.ShowWindow();
                    keepOpen = true;
                    MessageBox.Show(this, msg, "사이트 연결 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "확인 중 오류: " + ex.Message, "사이트 연결 확인",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (client != null && !keepOpen) client.Dispose();
                Busy(false);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            Close();
        }
    }
}
