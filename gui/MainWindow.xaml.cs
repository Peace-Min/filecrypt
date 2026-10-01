using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace FileCrypt
{
    public class Item
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public string FullPath { get; set; }   // 클립보드 항목이면 null
        public string ClipText { get; set; }   // 클립보드 항목의 본문
        public long Size { get; set; }

        /// <summary>복원 탭에서만 의미 있음. 이 항목 하나만으로 되돌릴 수 있는 블록 수.</summary>
        public int BlockCount { get; set; }

        /// <summary>복원 탭: 통짜 MESSAGE 블록 수 (조각 제외).</summary>
        public int MessageBlocks { get; set; }

        /// <summary>
        /// 복원 탭: 이 항목에 든 조각들. 넣을 때 한 번만 살펴 두고, 목록이 바뀔 때는 이것만 합친다 -
        /// 예전에는 목록이 바뀔 때마다 모든 조각 파일을 다시 읽어 해석했다.
        /// </summary>
        public List<FileCryptCore.PartGroup> Parts { get; set; }

        /// <summary>묶기 탭에서만 의미 있음. FileCrypt 텍스트로 보이면 true (잘못 넣은 것일 수 있음).</summary>
        public bool LooksArmor { get; set; }

        /// <summary>true = 복원 탭 항목, false = 묶기 탭 항목</summary>
        public bool ForDecrypt { get; set; }

        /// <summary>폴더를 통째로 넣어서 들어온 항목인지</summary>
        public bool FromFolder { get; set; }

        /// <summary>분할 조각을 담고 있는 항목인지</summary>
        public bool IsPart { get { return Parts != null && Parts.Count > 0; } }

        /// <summary>
        /// 컨테이너에 기록할 이름. 폴더로 추가한 파일은 폴더 기준 상대 경로가 들어가
        /// 복원할 때 폴더 구조가 그대로 살아난다. 개별 파일은 파일명만.
        /// </summary>
        public string RelPath { get; set; }

        public string SizeText
        {
            get
            {
                if (Size >= 1048576) return (Size / 1048576.0).ToString("N1") + " MB";
                if (Size >= 1024)    return (Size / 1024.0).ToString("N0") + " KB";
                return Size.ToString("N0") + " B";
            }
        }

        /// <summary>목록에 보여줄 이름 (폴더로 넣었으면 상대 경로).</summary>
        public string Display
        {
            get { return string.IsNullOrEmpty(RelPath) ? Name : RelPath; }
        }

        public string KindText
        {
            get
            {
                if (ForDecrypt)
                {
                    if (IsPart) return "조각";
                    return BlockCount > 0 ? string.Format("블록 {0}개", BlockCount) : "블록 없음";
                }
                return LooksArmor ? "FCRYPT?" : "묶기";
            }
        }

        // 배지 색. 항목마다, 그리려 할 때마다 새 브러시를 만들지 않도록 한 번만 만들어 얼린다.
        private static Brush B(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
        private static readonly Brush WarnBg = B(0xFF, 0xF4, 0xE0), WarnFg = B(0x9A, 0x60, 0x00);
        private static readonly Brush OkBg   = B(0xE7, 0xF6, 0xEC), OkFg   = B(0x0F, 0x7B, 0x45);
        private static readonly Brush BadBg  = B(0xFD, 0xEC, 0xEA), BadFg  = B(0xC0, 0x28, 0x1C);
        private static readonly Brush EncBg  = B(0xEC, 0xF1, 0xFE), EncFg  = B(0x1D, 0x4E, 0xD8);

        public Brush BadgeBg
        {
            get
            {
                if (ForDecrypt) return IsPart ? WarnBg : (BlockCount > 0 ? OkBg : BadBg);
                return LooksArmor ? WarnBg : EncBg;
            }
        }

        public Brush BadgeFg
        {
            get
            {
                if (ForDecrypt) return IsPart ? WarnFg : (BlockCount > 0 ? OkFg : BadFg);
                return LooksArmor ? WarnFg : EncFg;
            }
        }
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<Item> _enc = new ObservableCollection<Item>();
        private readonly ObservableCollection<Item> _dec = new ObservableCollection<Item>();

        /// <summary>
        /// 작업 중(묶기·복원·파일 살펴보기·근태관리 준비). 이 동안에는 실행 버튼이 다시 켜지지 않고
        /// 드래그드롭·추가도 받지 않는다 - 예전에는 작업 중에 파일을 끌어 놓으면 목록 갱신이
        /// 실행 버튼을 다시 켜서 두 번 실행될 수 있었다.
        /// </summary>
        private bool _busy;

        /// <summary>여러 항목을 한꺼번에 넣는 동안 목록 갱신을 미룬다(항목마다 전체 갱신 = O(N²)).</summary>
        private bool _bulk;

        private bool Encrypting { get { return RbEnc.IsChecked == true; } }
        private ObservableCollection<Item> Current { get { return Encrypting ? _enc : _dec; } }

        public MainWindow()
        {
            InitializeComponent();
            Title = "FileCrypt " + BuildLabel();
            // 목업 사이트에 붙어 있을 때는 실제 근태관리로 착각하지 않도록 제목에 표시한다.
            if (NetcusHost.MockPort > 0) Title += "  [목업 근태관리 127.0.0.1:" + NetcusHost.MockPort + "]";
            _enc.CollectionChanged += (s, e) => { if (!_bulk) RefreshUi(); };
            _dec.CollectionChanged += (s, e) => { if (!_bulk) RefreshUi(); };
            TxtOutDir.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        /// <summary>
        /// "2.0.1 (f38486b)" 처럼 버전과 빌드 커밋. 설치본과 개발 빌드가 같은 버전 번호라도
        /// 어느 코드로 만든 exe 인지 창 제목만 보고 알 수 있게 한다.
        /// 커밋 뒤 "-dirty" 는 커밋 안 된 수정이 섞인 빌드라는 뜻(build-setup.ps1 이 붙인다).
        /// </summary>
        private static string BuildLabel()
        {
            var asm = typeof(MainWindow).Assembly;
            var info = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            string v = info != null ? info.InformationalVersion : asm.GetName().Version.ToString();
            int plus = v.IndexOf('+');
            if (plus < 0) return v;
            string rev = v.Substring(plus + 1);
            string suffix = "";
            if (rev.EndsWith("-dirty")) { suffix = "-dirty"; rev = rev.Substring(0, rev.Length - suffix.Length); }
            if (rev.Length > 7) rev = rev.Substring(0, 7);
            return v.Substring(0, plus) + " (" + rev + suffix + ")";
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshAccountLabel();
            ApplyMode();
        }

        // ------------------------------------------------------------ 모드
        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ApplyMode();
        }

        /// <summary>탭별로 목록을 따로 유지하므로 모드를 바꿔도 넣어둔 것이 사라지지 않는다.</summary>
        private void ApplyMode()
        {
            LvItems.ItemsSource = Current;

            if (Encrypting)
            {
                TxtHint.Text = "옮길 파일을 넣으세요. 여러 개를 골라도 결과는 텍스트 파일 하나로 묶입니다.";
                TxtEmptyTitle.Text = "여기로 파일을 끌어다 놓으세요";
                TxtEmptySub.Text = "일반 파일 · 폴더 무엇이든\n아래 [파일 추가] · [폴더 추가] 도 됩니다";
                BtnAddFolder.Visibility = Visibility.Visible;
                BtnFromClip.Visibility = Visibility.Collapsed;
                ChkClipboard.Visibility = Visibility.Visible;
                ChkArchive.Visibility = Visibility.Visible;
                CbSplitSize.Visibility = Visibility.Visible;
            }
            else
            {
                TxtHint.Text = "FileCrypt 텍스트를 넣으세요. 안에 들어있는 파일을 전부 원래 이름으로 되돌립니다.";
                TxtEmptyTitle.Text = "여기로 FileCrypt 텍스트를 끌어다 놓으세요";
                TxtEmptySub.Text = "복사해 둔 텍스트가 있으면\n아래 [클립보드에서 가져오기] 를 누르세요";
                BtnAddFolder.Visibility = Visibility.Collapsed;
                BtnFromClip.Visibility = Visibility.Visible;
                ChkClipboard.Visibility = Visibility.Collapsed;
                ChkArchive.Visibility = Visibility.Collapsed;
                CbSplitSize.Visibility = Visibility.Collapsed;
            }

            SetStatus("", null);
            RefreshUi();
        }

        // ------------------------------------------------------------ 판별
        /// <summary>앞부분 64KB 안에 FCRYPT 표식이 있는지.</summary>
        private static bool LooksLikeFCryptFile(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    int want = (int)Math.Min(65536, fs.Length);
                    if (want < 20) return false;
                    var buf = new byte[want];
                    int read = 0;
                    while (read < want)
                    {
                        int k = fs.Read(buf, read, want - read);
                        if (k <= 0) break;
                        read += k;
                    }
                    return Encoding.ASCII.GetString(buf, 0, read)
                                   .IndexOf("-----BEGIN FCRYPT", StringComparison.Ordinal) >= 0;
                }
            }
            catch { return false; }
        }

        /// <summary>복원 탭 항목에 무엇이 들었는지 살펴 적어 둔다. 작업 스레드에서 불러도 된다.</summary>
        private static void Inspect(Item it, string text)
        {
            var scan = FileCryptCore.Scan(text);
            it.MessageBlocks = scan.MessageBlocks;
            it.Parts = scan.PartGroups;
            it.BlockCount = scan.BlockCount;
        }

        /// <summary>
        /// 복원 탭에 넣어 둔 조각을 전부 합친 상황. 항목마다 적어 둔 조각 정보만 합치므로 파일을 읽지 않는다.
        /// 목록 표시와 클립보드 가져오기가 같은 계산을 쓴다(예전에는 두 곳이 따로 세서 결과가 달랐다).
        /// </summary>
        private List<FileCryptCore.PartGroup> PooledParts()
        {
            var have = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
            var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in _dec)
            {
                if (it.Parts == null) continue;
                foreach (var g in it.Parts)
                {
                    SortedSet<int> s;
                    if (!have.TryGetValue(g.Id, out s)) { s = new SortedSet<int>(); have[g.Id] = s; total[g.Id] = 0; }
                    foreach (int i in g.Have) s.Add(i);
                    if (g.Total > total[g.Id]) total[g.Id] = g.Total;
                }
            }
            var list = new List<FileCryptCore.PartGroup>();
            foreach (var kv in have)
            {
                int n = total[kv.Key];
                var missing = new List<int>();
                for (int i = 1; i <= n; i++) if (!kv.Value.Contains(i)) missing.Add(i);
                list.Add(new FileCryptCore.PartGroup { Id = kv.Key, Total = n, Have = kv.Value.ToList(), Missing = missing });
            }
            return list;
        }

        // ------------------------------------------------------------ 목록
        private void RefreshUi()
        {
            var list = Current;
            bool any = list.Count > 0;

            LvItems.Visibility   = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyPane.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

            int other = Encrypting ? _dec.Count : _enc.Count;
            TxtCount.Text = any ? string.Format("{0}개", list.Count) : "";
            if (other > 0)
                TxtCount.Text += string.Format("   (반대쪽 탭에 {0}개 있음)", other);

            if (Encrypting)
            {
                long total = 0;
                foreach (var i in list) total += i.Size;
                bool arch = ChkArchive.IsChecked == true;
                if (!any)
                {
                    TxtPlan.Text = "파일이나 폴더를 넣으면 무엇을 할지 여기에 표시됩니다.";
                }
                else if (arch)
                {
                    TxtPlan.Text = string.Format(
                        "파일 {0}개  →  아카이브 1블록  ({1:N0} B 를 한 번에 압축)", list.Count, total);
                }
                else
                {
                    TxtPlan.Text = string.Format(
                        "파일 {0}개  →  블록 {0}개  ({1:N0} B, 각 파일 독립)", list.Count, total);
                    // 결과 메시지(TxtStatus)를 덮어쓰지 않도록 권고는 계획 줄에 붙인다.
                    if (list.Count >= 20) TxtPlan.Text += "  ·  [하나로 묶기] 를 켜면 크게 작아집니다";
                }
                if (any) TxtPlan.Text += SplitRuleText();

                BtnRun.Content = "텍스트로 만들기";
                BtnRun.IsEnabled = any && !_busy;

                BtnNetcus.Content = "근태관리로 올리기";
                BtnNetcus.IsEnabled = any && !_busy;
                BtnNetcus.ToolTip = "고른 파일을 [하나로 묶기] 설정과 무관하게 항상 하나로 묶어 "
                                  + "사내 일간보고 칸에 기록합니다 (날짜당 조각 1개).";
            }
            else
            {
                if (!any)
                {
                    TxtPlan.Text = "텍스트를 넣으면 무엇을 할지 여기에 표시됩니다.";
                    BtnRun.IsEnabled = false;
                }
                else if (list.Any(i => i.IsPart))
                {
                    // 조각은 전부 합쳐야 의미가 있으므로 합쳐서 판단한다.
                    int haveN = 0, totalN = 0, doneG = 0;
                    foreach (var g in PooledParts()) { haveN += g.Have.Count; totalN += g.Total; if (g.Complete) doneG++; }
                    int msgs = list.Sum(i => i.MessageBlocks);
                    TxtPlan.Text = string.Format("조각 {0}/{1} 모임  ·  완성된 묶음 {2}개", haveN, totalN, doneG)
                                 + (msgs > 0 ? string.Format("  ·  통짜 블록 {0}개", msgs) : "");
                    BtnRun.IsEnabled = (doneG + msgs) > 0 && !_busy;
                }
                else
                {
                    int blocks = list.Sum(i => i.BlockCount);
                    TxtPlan.Text = string.Format("텍스트 {0}개 (블록 {1}개)  →  파일 {1}개로 되돌립니다", list.Count, blocks);
                    BtnRun.IsEnabled = blocks > 0 && !_busy;
                }
                BtnRun.Content = "파일로 되돌리기";

                // 받아오기는 사이트에서 읽어 오므로 넣어 둔 텍스트가 없어도 쓸 수 있다.
                BtnNetcus.Content = "근태관리에서 가져오기";
                BtnNetcus.IsEnabled = !_busy;
                BtnNetcus.ToolTip = "사내 일간보고에 올려 둔 내용을 날짜 범위로 읽어 와 파일로 되돌립니다.";
            }
        }

        /// <summary>상단 계정 정보 관리. 여기서 한 번 저장하면 계속 유지된다.</summary>
        private void BtnAccount_Click(object sender, RoutedEventArgs e)
        {
            AccountWindow.Show(this);
            RefreshAccountLabel();
        }

        /// <summary>상단에 지금 어떤 계정이 저장돼 있는지 보여 준다.</summary>
        private void RefreshAccountLabel()
        {
            if (AppConfig.HasNetcusAccount)
            {
                TxtAccount.Text = "근태관리 계정: " + AppConfig.NetcusId;
                BtnAccount.ToolTip = "저장된 계정을 확인하거나 바꿉니다. " + AppConfig.File_;
            }
            else
            {
                TxtAccount.Text = "근태관리 계정 없음";
                BtnAccount.ToolTip = "근태관리 올리기·가져오기를 쓰려면 먼저 계정을 저장하세요.";
            }
        }

        /// <summary>묶기 탭 목록 -> 처리 절차 입력.</summary>
        private List<FileCryptJobs.PackInput> PackInputs()
        {
            return _enc.Where(i => i.FullPath != null).Select(i => new FileCryptJobs.PackInput
            {
                FullPath = i.FullPath,
                RelPath  = i.RelPath
            }).ToList();
        }

        /// <summary>저장 폴더 칸을 읽고 없으면 만든다. 실패하면 상태줄에 이유를 쓰고 null.</summary>
        private string EnsureOutDir()
        {
            string outDir = TxtOutDir.Text.Trim();
            if (string.IsNullOrEmpty(outDir)) { SetStatus("저장 폴더를 먼저 고르세요.", false); return null; }
            try
            {
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                return outDir;
            }
            catch (Exception ex)
            {
                SetStatus("저장 폴더를 만들 수 없습니다: " + ex.Message, false);
                return null;
            }
        }

        /// <summary>사내 보고 시스템으로 올리기 / 에서 가져오기.</summary>
        private async void BtnNetcus_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (Encrypting)
            {
                var inputs = PackInputs();
                if (inputs.Count == 0) { SetStatus("올릴 파일이 없습니다.", false); return; }

                byte[] container = null;
                int count = 0; long bytes = 0;
                List<string> errors = null;

                // 파일 읽기 + 압축·암호화는 큰 폴더면 수 초 걸린다. 창이 멈추지 않게 작업 스레드에서.
                SetBusy(true);
                Bar.IsIndeterminate = true;
                SetStatus(string.Format("{0}개 파일을 하나로 묶는 중...", inputs.Count), null);
                try
                {
                    container = await Task.Run(() => FileCryptJobs.BuildContainer(inputs, out count, out bytes, out errors));
                }
                catch (Exception ex)
                {
                    SetStatus("묶기 실패: " + ex.Message, false);
                }
                finally
                {
                    Bar.IsIndeterminate = false;
                    SetBusy(false);
                }
                if (container == null) return;

                string msg = string.Format("{0}개 파일({1:N0} B)을 하나로 묶었습니다.", count, bytes);
                if (ChkArchive.IsChecked != true) msg += " (근태관리로는 항상 하나로 묶어 올립니다)";
                if (errors.Count > 0) msg += string.Format("  · {0}개는 못 읽어 뺐습니다: {1}", errors.Count, string.Join(" / ", errors));
                SetStatus(msg, errors.Count == 0);
                NetcusWindow.Upload(this, container, NetcusChunkChars());
            }
            else
            {
                string outDir = EnsureOutDir();
                if (outDir == null) return;
                NetcusWindow.Download(this, outDir);
            }
        }

        private void ChkArchive_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            RefreshUi();
        }

        private void ChkSplit_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            RefreshUi();
        }

        /// <summary>
        /// 조각내기 기본 한도. 붙여넣는 쪽(사내 보고 필드)에서 지연 없이 처리되는
        /// 한도를 실측해 잡은 값이다. 더 키우면 응답이 느려지고, 더 줄이면 조각만 늘어난다.
        /// </summary>
        private const int DefaultAutoSplit = 200000;

        /// <summary>
        /// 콤보에서 고른 조각내기 한도. 0 = 나누지 않음.
        ///   Tag "0"        -> 나누지 않음
        ///   Tag "A200000"  -> 결과가 20만 자를 넘을 때만 20만 자씩 나눔
        /// </summary>
        private int ReadSplitRule()
        {
            var item = CbSplitSize.SelectedItem as System.Windows.Controls.ComboBoxItem;
            if (item == null || item.Tag == null) return DefaultAutoSplit;

            string tag = item.Tag.ToString();
            int v;
            if (tag.StartsWith("A", StringComparison.OrdinalIgnoreCase) && int.TryParse(tag.Substring(1), out v)) return v;
            return 0;
        }

        /// <summary>
        /// 근태관리에 올릴 때 한 날짜에 담을 글자수.
        /// 조각내기 칸에서 고른 값을 그대로 쓴다 — 예전에는 이 값이 연결돼 있지 않아
        /// 콤보를 바꿔도 날짜 수가 그대로였다.
        /// '안 함' 은 근태관리에서는 쓸 수 없다(한 칸에 다 들어가지 않는다) → 저장된 기본값을 쓴다.
        /// </summary>
        private int NetcusChunkChars()
        {
            int autoOver = ReadSplitRule();
            return autoOver > 0 ? autoOver : AppConfig.NetcusLimit;
        }

        /// <summary>실행 버튼 위에 보여 줄 조각내기 규칙 설명.</summary>
        private string SplitRuleText()
        {
            int autoOver = ReadSplitRule();
            if (autoOver > 0) return string.Format("  ·  {0:N0}자 넘으면 나눔", autoOver);
            return "  ·  나누지 않음";
        }

        /// <summary>
        /// 경로들(파일·폴더)을 지금 탭 목록에 넣는다.
        /// 폴더 훑기, 크기 확인, FCRYPT 판별, 조각 살펴보기는 작업 스레드에서 하고,
        /// 목록에는 마지막에 한꺼번에 넣어 갱신을 한 번만 한다.
        /// </summary>
        private async Task AddPathsAsync(IEnumerable<string> paths)
        {
            if (_busy) return;
            bool enc = Encrypting;
            var list = Current;
            var pathList = paths.ToList();
            var seen = new HashSet<string>(list.Where(i => i.FullPath != null).Select(i => i.FullPath),
                                           StringComparer.OrdinalIgnoreCase);

            bool hadFolder = false;
            List<Item> found;
            SetBusy(true);
            Bar.IsIndeterminate = true;
            try
            {
                found = await Task.Run(() =>
                {
                    var items = new List<Item>();
                    foreach (string p in pathList)
                    {
                        if (Directory.Exists(p))
                        {
                            if (!enc)
                            {
                                // 복원 탭에서는 폴더 안의 텍스트 파일만 훑는다.
                                foreach (string f in Directory.GetFiles(p, "*.txt", SearchOption.AllDirectories))
                                    AddFileTo(items, seen, enc, f, null, false);
                                continue;
                            }
                            // 폴더 단위: 넣은 폴더 이름을 최상위로 두고 그 아래 구조를 그대로 보존한다.
                            // 순회 로직은 테스트가 닿을 수 있도록 코어에 둔다.
                            hadFolder = true;
                            foreach (var fe in FileCryptCore.EnumerateFolder(p))
                                AddFileTo(items, seen, enc, fe.FullPath, fe.RelativePath, true);
                            continue;
                        }
                        AddFileTo(items, seen, enc, p, null, false);
                    }
                    return items;
                });
            }
            catch (Exception ex)
            {
                found = new List<Item>();
                SetStatus("추가 실패: " + ex.Message, false);
            }
            finally
            {
                Bar.IsIndeterminate = false;
            }

            _bulk = true;
            try { foreach (var it in found) list.Add(it); }
            finally { _bulk = false; }

            // 폴더를 넣으면 아카이브(하나로 묶기)가 기본이다.
            if (hadFolder && enc) ChkArchive.IsChecked = true;
            SetBusy(false);   // 여기서 RefreshUi 한 번

            AutoSuggestOutDir();
            WarnIfMisplaced();
        }

        /// <summary>파일 하나를 항목으로 만든다. UI 를 건드리지 않으므로 작업 스레드에서 부른다.</summary>
        private static void AddFileTo(List<Item> items, HashSet<string> seen, bool enc,
                                      string path, string relPath, bool fromFolder)
        {
            if (!File.Exists(path)) return;
            var fi = new FileInfo(path);
            if (!seen.Add(fi.FullName)) return;

            var it = new Item
            {
                Name = fi.Name,
                Folder = fi.DirectoryName,
                FullPath = fi.FullName,
                Size = fi.Length,
                ForDecrypt = !enc,
                RelPath = relPath ?? fi.Name,
                FromFolder = fromFolder
            };

            if (enc) it.LooksArmor = LooksLikeFCryptFile(path);
            else
            {
                try { Inspect(it, File.ReadAllText(path)); }
                catch { it.BlockCount = 0; }
            }
            items.Add(it);
        }

        private void WarnIfMisplaced()
        {
            if (Encrypting)
            {
                int n = _enc.Count(i => i.LooksArmor);
                if (n > 0)
                    SetStatus(string.Format("{0}개가 FileCrypt 텍스트로 보입니다. 되돌리려면 위에서 [텍스트 → 파일] 을 누르세요.", n), false);
            }
            else
            {
                // 조각 파일은 혼자서는 블록이 0개인 게 정상이다. 조각도 블록도 없는 것만 짚는다.
                int n = _dec.Count(i => i.BlockCount == 0 && !i.IsPart);
                if (n > 0)
                    SetStatus(string.Format("{0}개에서 FileCrypt 블록을 찾지 못했습니다. 묶으려면 위에서 [파일 → 텍스트] 를 누르세요.", n), false);
            }
        }

        private async void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = Encrypting ? "묶을 파일 선택 (여러 개 가능)" : "FileCrypt 텍스트 선택 (여러 개 가능)",
                Filter = Encrypting ? "모든 파일 (*.*)|*.*" : "텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            await AddPathsAsync(dlg.FileNames);
        }

        private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder;
            using (var dlg = new Forms.FolderBrowserDialog())
            {
                dlg.Description = "폴더 안의 파일을 전부 추가합니다";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
                folder = dlg.SelectedPath;
            }
            await AddPathsAsync(new[] { folder });
        }

        private async void BtnFromClip_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }   // 클립보드는 UI 스레드에서만

            if (!FileCryptCore.LooksLikeArmor(text))
            {
                SetStatus("클립보드에 FileCrypt 텍스트가 없습니다.", false);
                return;
            }

            // 2천만 자까지 온다. 해석은 작업 스레드에서.
            var it = new Item
            {
                Folder = "붙여넣은 텍스트",
                FullPath = null,
                ClipText = text,
                ForDecrypt = true
            };
            SetBusy(true);
            try
            {
                await Task.Run(() => { Inspect(it, text); it.Size = Encoding.UTF8.GetByteCount(text); });
            }
            finally { SetBusy(false); }
            it.Name = it.IsPart ? "[클립보드 조각]" : "[클립보드]";
            _dec.Add(it);

            if (it.IsPart)
            {
                // 지금까지 모은 조각 전체(파일로 넣은 것 포함)를 기준으로 진행 상황을 알려 준다.
                var pooled = PooledParts();
                var g = pooled.FirstOrDefault(x => it.Parts.Any(p => string.Equals(p.Id, x.Id, StringComparison.OrdinalIgnoreCase)));
                if (g != null)
                {
                    SetStatus(string.Format("조각 {0}/{1} 모았습니다.{2}",
                        g.Have.Count, g.Total,
                        g.Complete ? " 이제 [파일로 되돌리기] 를 누르세요." : " 나머지를 복사해 다시 누르세요."),
                        g.Complete);
                    return;
                }
            }
            SetStatus(string.Format("클립보드에서 블록 {0}개를 가져왔습니다.", it.BlockCount), true);
        }

        // ------------------------------------------------------------ 경로로 추가
        private void TxtPaths_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            TxtPathsHint.Visibility = TxtPaths.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Enter = 추가, Shift+Enter = 줄바꿈(여러 경로를 한 줄씩).</summary>
        private async void TxtPaths_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;
            e.Handled = true;
            await AddTypedPathsAsync();
        }

        private async void BtnAddPaths_Click(object sender, RoutedEventArgs e)
        {
            await AddTypedPathsAsync();
        }

        /// <summary>
        /// 경로 칸에 넣은 절대 경로들을 목록에 넣는다(파일·폴더, 지금 탭 규칙 그대로).
        /// 문제가 있는 경로만 칸에 남겨 둔다 - 고쳐서 다시 Enter 하면 된다.
        /// </summary>
        private async Task AddTypedPathsAsync()
        {
            if (_busy) return;
            var p = FileCryptJobs.ParsePaths(TxtPaths.Text);
            if (p.Found.Count + p.Missing.Count + p.NotAbsolute.Count == 0)
            {
                SetStatus("추가할 경로를 넣으세요. 예: C:\\자료\\보고서.xlsx", false);
                return;
            }

            int before = Current.Count;
            string statusBefore = TxtStatus.Text;
            if (p.Found.Count > 0) await AddPathsAsync(p.Found);
            int added = Current.Count - before;

            var bad = p.Missing.Concat(p.NotAbsolute).ToList();
            TxtPaths.Text = string.Join("\r\n", bad);
            TxtPaths.CaretIndex = TxtPaths.Text.Length;

            if (bad.Count > 0)
            {
                var msg = new StringBuilder();
                if (added > 0) msg.AppendFormat("{0}개 추가 · ", added);
                if (p.Missing.Count > 0) msg.AppendFormat("없는 경로 {0}개: {1}", p.Missing.Count, string.Join(", ", p.Missing.Take(3)));
                if (p.Missing.Count > 3) msg.Append(" …");
                if (p.Missing.Count > 0 && p.NotAbsolute.Count > 0) msg.Append(" · ");
                if (p.NotAbsolute.Count > 0) msg.AppendFormat("절대 경로가 아님(C:\\… 로 시작해야 함) {0}개: {1}", p.NotAbsolute.Count, string.Join(", ", p.NotAbsolute.Take(3)));
                SetStatus(msg.ToString(), false);
            }
            else if (TxtStatus.Text == statusBefore)   // 추가 과정이 따로 알린 것(잘못 넣은 탭 등)이 없을 때만
            {
                SetStatus(added > 0 ? string.Format("경로로 {0}개를 추가했습니다.", added) : "이미 목록에 있는 경로입니다.", added > 0 ? (bool?)true : null);
            }
            TxtPaths.Focus();
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            var sel = LvItems.SelectedItems.Cast<Item>().ToList();
            _bulk = true;
            try { foreach (var it in sel) Current.Remove(it); }
            finally { _bulk = false; }
            RefreshUi();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            Current.Clear();
            SetStatus("", null);
        }

        private void AutoSuggestOutDir()
        {
            var first = Current.FirstOrDefault(i => i.FullPath != null);
            if (first != null && Directory.Exists(first.Folder)) TxtOutDir.Text = first.Folder;
        }

        // ------------------------------------------------------------ 드래그 앤 드롭
        private void DropZone_DragEnter(object sender, DragEventArgs e)
        {
            if (!_busy && e.Data.GetDataPresent(DataFormats.FileDrop))
                DropZone.Background = (Brush)FindResource("DropHi");
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            DropZone.Background = (Brush)FindResource("Panel");
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = (!_busy && e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            DropZone.Background = (Brush)FindResource("Panel");
            HandleDrop(e);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = (!_busy && e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            HandleDrop(e);
        }

        private async void HandleDrop(DragEventArgs e)
        {
            e.Handled = true;
            if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            await AddPathsAsync(paths);
        }

        // ------------------------------------------------------------ 저장 폴더
        private void BtnBrowseOut_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new Forms.FolderBrowserDialog())
            {
                dlg.Description = "결과를 저장할 폴더";
                dlg.ShowNewFolderButton = true;
                if (Directory.Exists(TxtOutDir.Text)) dlg.SelectedPath = TxtOutDir.Text;
                if (dlg.ShowDialog() == Forms.DialogResult.OK) TxtOutDir.Text = dlg.SelectedPath;
            }
        }

        // ------------------------------------------------------------ 실행
        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string outDir = EnsureOutDir();
            if (outDir == null) return;

            SetBusy(true);
            try
            {
                if (Encrypting) await RunEncryptAsync(outDir);
                else            await RunDecryptAsync(outDir);
            }
            catch (Exception ex)
            {
                SetStatus("오류: " + ex.Message, false);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task RunEncryptAsync(string outDir)
        {
            var inputs = PackInputs();
            if (inputs.Count == 0) { SetStatus("처리할 파일이 없습니다.", false); return; }

            int splitAutoOver = ReadSplitRule();
            var opt = new FileCryptJobs.PackOptions
            {
                Archive       = (ChkArchive.IsChecked == true),
                AutoSplitOver = splitAutoOver
            };

            Bar.IsIndeterminate = true;
            SetStatus(string.Format("{0}개 처리 중...", inputs.Count), null);

            FileCryptJobs.PackResult r;
            try { r = await Task.Run(() => FileCryptJobs.Pack(inputs, outDir, opt)); }
            catch (Exception ex) { Bar.IsIndeterminate = false; SetStatus(ex.Message, false); return; }
            Bar.IsIndeterminate = false;

            bool copied = false;
            if (ChkClipboard.IsChecked == true && !string.IsNullOrEmpty(r.ClipboardText))
            {
                try { Clipboard.SetText(r.ClipboardText); copied = true; } catch { }
            }

            string msg;
            if (r.PartCount > 0)
            {
                msg = string.Format("{0}개 → 조각 {1}개 (각 최대 {2:N0}자){3}",
                                    r.FileCount, r.PartCount, r.LongestPartChars,
                                    r.SplitWasAutomatic ? " · 한도를 넘어 자동으로 나눔" : "");
                if (copied) msg += "  · 1번 조각을 클립보드에 복사함";
            }
            else
            {
                msg = string.Format("{0}개 → {1}   ({2:N0} B → {3:N0} 자)",
                                    r.FileCount, Path.GetFileName(r.WrittenFiles[0]),
                                    r.SourceBytes, r.TotalChars);
                if (splitAutoOver > 0) msg += " · 한도 안이라 나누지 않음";
                if (copied) msg += "  · 클립보드 복사됨";
            }
            if (r.FailedCount > 0) msg += string.Format("  · {0}개 실패: {1}", r.FailedCount, string.Join(" / ", r.Errors));
            SetStatus(msg, r.FailedCount == 0);

            if (r.WrittenFiles.Count > 0) Explorer.Show(r.WrittenFiles[0]);
        }

        private async Task RunDecryptAsync(string outDir)
        {
            // 조각이 여러 파일 / 여러 번의 붙여넣기에 흩어져 있을 수 있으므로
            // 입력을 전부 넘겨 한 번에 해석하게 한다. 파일 읽기도 작업 스레드에서.
            var sources = _dec.ToList();

            Bar.IsIndeterminate = true;
            SetStatus("복원 중...", null);

            FileCryptJobs.UnpackResult r;
            try
            {
                r = await Task.Run(() =>
                {
                    var texts = new List<string>(sources.Count);
                    foreach (var it in sources)
                    {
                        string text = it.ClipText;
                        if (text == null)
                        {
                            try { text = File.ReadAllText(it.FullPath); }
                            catch (Exception ex) { throw new IOException(it.Name + " 읽기 실패: " + ex.Message, ex); }
                        }
                        texts.Add(text);
                    }
                    return FileCryptJobs.Unpack(texts, outDir);
                });
            }
            catch (Exception ex) { Bar.IsIndeterminate = false; SetStatus(ex.Message, false); return; }
            Bar.IsIndeterminate = false;

            if (r.BlockCount == 0)
            {
                SetStatus(FileCryptJobs.DescribePending(r.PendingParts), false);
                return;
            }

            string msg = r.FailedCount == 0
                ? string.Format("{0}개 파일 복원 완료 ({1:N0} B) · 원본과 100% 일치 (SHA-256 검증)", r.OkCount, r.TotalBytes)
                : string.Format("{0}개 복원 / {1}개 실패 · {2}", r.OkCount, r.FailedCount, string.Join(" / ", r.Errors));
            SetStatus(msg, r.FailedCount == 0);

            // 복원 폴더를 새로 만들었으면 그 폴더를, 파일 하나면 그 파일을 연다(예전에는 마지막 파일을 골라
            // 하위 폴더가 있는 묶음이면 깊은 폴더 하나가 열렸다).
            Explorer.Show(r.ShowPath);
        }

        // ------------------------------------------------------------ 보조
        private void SetStatus(string text, bool? good)
        {
            TxtStatus.Text = text;
            if (good == null)      TxtStatus.Foreground = (Brush)FindResource("Text2");
            else if (good == true) TxtStatus.Foreground = (Brush)FindResource("Ok");
            else                   TxtStatus.Foreground = (Brush)FindResource("Bad");
        }

        /// <summary>작업 중에는 목록·옵션·실행을 전부 막는다. 끝나면 목록을 한 번 갱신한다.</summary>
        private void SetBusy(bool busy)
        {
            _busy = busy;
            Bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnAddFiles.IsEnabled = !busy;
            BtnAddFolder.IsEnabled = !busy;
            BtnFromClip.IsEnabled = !busy;
            BtnRemove.IsEnabled = !busy;
            BtnClear.IsEnabled = !busy;
            BtnBrowseOut.IsEnabled = !busy;
            TxtPaths.IsEnabled = !busy;
            BtnAddPaths.IsEnabled = !busy;
            TxtOutDir.IsEnabled = !busy;
            ChkArchive.IsEnabled = !busy;
            ChkClipboard.IsEnabled = !busy;
            CbSplitSize.IsEnabled = !busy;
            BtnAccount.IsEnabled = !busy;
            RbEnc.IsEnabled = !busy;
            RbDec.IsEnabled = !busy;
            if (busy) { BtnRun.IsEnabled = false; BtnNetcus.IsEnabled = false; }
            else { Bar.Value = 0; RefreshUi(); }
        }
    }
}
