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

        /// <summary>복원 탭에서만 의미 있음. 이 항목이 담고 있는 블록 수.</summary>
        public int BlockCount { get; set; }

        /// <summary>묶기 탭에서만 의미 있음. FileCrypt 텍스트로 보이면 true (잘못 넣은 것일 수 있음).</summary>
        public bool LooksArmor { get; set; }

        /// <summary>true = 복원 탭 항목, false = 묶기 탭 항목</summary>
        public bool ForDecrypt { get; set; }

        /// <summary>폴더를 통째로 넣어서 들어온 항목인지</summary>
        public bool FromFolder { get; set; }

        /// <summary>분할 조각을 담고 있는 항목인지</summary>
        public bool IsPart { get; set; }

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

        private static Brush B(byte r, byte g, byte b) { return new SolidColorBrush(Color.FromRgb(r, g, b)); }

        public Brush BadgeBg
        {
            get
            {
                if (ForDecrypt)
                {
                    if (IsPart) return B(0xFF, 0xF4, 0xE0);
                    return BlockCount > 0 ? B(0xE7, 0xF6, 0xEC) : B(0xFD, 0xEC, 0xEA);
                }
                return LooksArmor ? B(0xFF, 0xF4, 0xE0) : B(0xEC, 0xF1, 0xFE);
            }
        }

        public Brush BadgeFg
        {
            get
            {
                if (ForDecrypt)
                {
                    if (IsPart) return B(0x9A, 0x60, 0x00);
                    return BlockCount > 0 ? B(0x0F, 0x7B, 0x45) : B(0xC0, 0x28, 0x1C);
                }
                return LooksArmor ? B(0x9A, 0x60, 0x00) : B(0x1D, 0x4E, 0xD8);
            }
        }
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<Item> _enc = new ObservableCollection<Item>();
        private readonly ObservableCollection<Item> _dec = new ObservableCollection<Item>();

        private bool Encrypting { get { return RbEnc.IsChecked == true; } }
        private ObservableCollection<Item> Current { get { return Encrypting ? _enc : _dec; } }

        public MainWindow()
        {
            InitializeComponent();
            _enc.CollectionChanged += (s, e) => RefreshUi();
            _dec.CollectionChanged += (s, e) => RefreshUi();
            TxtOutDir.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
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
                ChkSplit.Visibility = Visibility.Visible;
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
                ChkSplit.Visibility = Visibility.Collapsed;
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
                long total = list.Sum(i => i.Size);
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
                }
                if (any && ChkSplit.IsChecked == true)
                    TxtPlan.Text += string.Format("  ·  {0:N0}자씩 조각내기", SplitChars());

                BtnRun.Content = "텍스트로 만들기";
                BtnRun.IsEnabled = any;

                if (any && !arch && list.Count >= 20)
                    SetStatus(string.Format("파일이 {0}개입니다. [하나로 묶기] 를 켜면 크게 작아집니다.", list.Count), false);
            }
            else
            {
                int blocks = list.Sum(i => i.BlockCount);
                if (!any)
                {
                    TxtPlan.Text = "텍스트를 넣으면 무엇을 할지 여기에 표시됩니다.";
                    BtnRun.IsEnabled = false;
                }
                else if (list.Any(i => i.IsPart))
                {
                    // 조각은 전부 합쳐야 의미가 있으므로 합쳐서 판단한다.
                    var all = new StringBuilder();
                    foreach (var it in list)
                    {
                        string t = it.ClipText;
                        if (t == null) { try { t = File.ReadAllText(it.FullPath); } catch { t = ""; } }
                        all.Append(t).Append("\r\n");
                    }
                    var gs = FileCryptCore.InspectParts(all.ToString());
                    int haveN = 0, totalN = 0, doneG = 0;
                    foreach (var g in gs) { haveN += g.Have.Count; totalN += g.Total; if (g.Complete) doneG++; }
                    TxtPlan.Text = string.Format("조각 {0}/{1} 모임  ·  완성된 묶음 {2}개", haveN, totalN, doneG);
                    BtnRun.IsEnabled = doneG > 0;
                }
                else
                {
                    TxtPlan.Text = string.Format("텍스트 {0}개 (블록 {1}개)  →  파일 {1}개로 되돌립니다", list.Count, blocks);
                    BtnRun.IsEnabled = blocks > 0;
                }
                BtnRun.Content = "파일로 되돌리기";
            }
        }

        private void ChkArchive_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            RefreshUi();
        }

        private void ChkSplit_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            CbSplitSize.IsEnabled = (ChkSplit.IsChecked == true);
            RefreshUi();
        }

        /// <summary>조각 하나에 담을 최대 글자수.</summary>
        private int SplitChars()
        {
            var item = CbSplitSize.SelectedItem as System.Windows.Controls.ComboBoxItem;
            int v;
            if (item != null && item.Tag != null && int.TryParse(item.Tag.ToString(), out v)) return v;
            return 100000;
        }

        private void AddPath(string path)
        {
            if (Directory.Exists(path))
            {
                if (!Encrypting)
                {
                    // 복원 탭에서는 폴더 안의 텍스트 파일만 훑는다.
                    foreach (string f in Directory.GetFiles(path, "*.txt", SearchOption.AllDirectories))
                        AddFile(f);
                    return;
                }

                // 폴더 단위: 넣은 폴더 이름을 최상위로 두고 그 아래 구조를 그대로 보존한다.
                // 순회 로직은 테스트가 닿을 수 있도록 코어에 둔다.
                foreach (var fe in FileCryptCore.EnumerateFolder(path))
                    AddFile(fe.FullPath, fe.RelativePath, true);
                // 폴더를 넣으면 아카이브(하나로 묶기)가 기본이다.
                if (ChkArchive != null) ChkArchive.IsChecked = true;
                return;
            }
            AddFile(path);
        }

        private void AddFile(string path, string relPath = null, bool fromFolder = false)
        {
            if (!File.Exists(path)) return;
            var list = Current;
            if (list.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))) return;

            var fi = new FileInfo(path);
            var it = new Item
            {
                Name = fi.Name,
                Folder = fi.DirectoryName,
                FullPath = fi.FullName,
                Size = fi.Length,
                ForDecrypt = !Encrypting,
                RelPath = relPath ?? fi.Name,
                FromFolder = fromFolder
            };

            if (Encrypting)
            {
                it.LooksArmor = LooksLikeFCryptFile(path);
            }
            else
            {
                try
                {
                    string t = File.ReadAllText(path);
                    it.IsPart = FileCryptCore.HasParts(t);
                    it.BlockCount = FileCryptCore.ExtractBlocks(t).Count;
                }
                catch { it.BlockCount = 0; }
            }

            list.Add(it);
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
                int n = _dec.Count(i => i.BlockCount == 0);
                if (n > 0)
                    SetStatus(string.Format("{0}개에서 FileCrypt 블록을 찾지 못했습니다. 묶으려면 위에서 [파일 → 텍스트] 를 누르세요.", n), false);
            }
        }

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = Encrypting ? "묶을 파일 선택 (여러 개 가능)" : "FileCrypt 텍스트 선택 (여러 개 가능)",
                Filter = Encrypting ? "모든 파일 (*.*)|*.*" : "텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            foreach (string f in dlg.FileNames) AddFile(f);
            AutoSuggestOutDir();
            WarnIfMisplaced();
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new Forms.FolderBrowserDialog())
            {
                dlg.Description = "폴더 안의 파일을 전부 추가합니다";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
                AddPath(dlg.SelectedPath);
            }
            AutoSuggestOutDir();
            WarnIfMisplaced();
        }

        private void BtnFromClip_Click(object sender, RoutedEventArgs e)
        {
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }

            if (!FileCryptCore.LooksLikeArmor(text))
            {
                SetStatus("클립보드에 FileCrypt 텍스트가 없습니다.", false);
                return;
            }

            int n = FileCryptCore.ExtractBlocks(text).Count;
            bool isPart = FileCryptCore.HasParts(text);
            _dec.Add(new Item
            {
                Name = isPart ? "[클립보드 조각]" : "[클립보드]",
                Folder = "붙여넣은 텍스트",
                FullPath = null,
                ClipText = text,
                Size = Encoding.UTF8.GetByteCount(text),
                ForDecrypt = true,
                BlockCount = n,
                IsPart = isPart
            });

            if (isPart)
            {
                // 지금까지 모은 조각 전체를 기준으로 진행 상황을 알려 준다.
                var all = new StringBuilder();
                foreach (var it in _dec) if (it.ClipText != null) all.Append(it.ClipText).Append("\r\n");
                var gs = FileCryptCore.InspectParts(all.ToString());
                if (gs.Count > 0)
                {
                    var g = gs[0];
                    SetStatus(string.Format("조각 {0}/{1} 모았습니다.{2}",
                        g.Have.Count, g.Total,
                        g.Complete ? " 이제 [파일로 되돌리기] 를 누르세요." : " 나머지를 복사해 다시 누르세요."),
                        g.Complete);
                    return;
                }
            }
            SetStatus(string.Format("클립보드에서 블록 {0}개를 가져왔습니다.", n), true);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            foreach (var it in LvItems.SelectedItems.Cast<Item>().ToList()) Current.Remove(it);
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
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                DropZone.Background = (Brush)FindResource("DropHi");
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            DropZone.Background = (Brush)FindResource("Panel");
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            DropZone.Background = (Brush)FindResource("Panel");
            HandleDrop(e);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            HandleDrop(e);
        }

        private void HandleDrop(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string p in paths) AddPath(p);
            AutoSuggestOutDir();
            WarnIfMisplaced();
            e.Handled = true;
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
            string outDir = TxtOutDir.Text.Trim();
            if (string.IsNullOrEmpty(outDir)) { SetStatus("저장 폴더를 지정하세요.", false); return; }
            try
            {
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                SetStatus("저장 폴더를 만들 수 없습니다: " + ex.Message, false);
                return;
            }

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
            var sources = _enc.Where(i => i.FullPath != null).ToList();
            if (sources.Count == 0) { SetStatus("처리할 파일이 없습니다.", false); return; }

            var inputs = sources.Select(i => new FileCryptJobs.PackInput
            {
                FullPath = i.FullPath,
                RelPath  = i.RelPath
            }).ToList();

            var opt = new FileCryptJobs.PackOptions
            {
                Archive    = (ChkArchive.IsChecked == true),
                SplitChars = (ChkSplit.IsChecked == true) ? SplitChars() : 0
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
                msg = string.Format("{0}개 → 조각 {1}개 (각 최대 {2:N0}자)", r.FileCount, r.PartCount, r.LongestPartChars);
                if (copied) msg += "  · 1번 조각을 클립보드에 복사함";
            }
            else
            {
                msg = string.Format("{0}개 → {1}   ({2:N0} B → {3:N0} 자)",
                                    r.FileCount, Path.GetFileName(r.WrittenFiles[0]),
                                    r.SourceBytes, r.FullText.Length);
                if (copied) msg += "  · 클립보드 복사됨";
            }
            if (r.FailedCount > 0) msg += string.Format("  · {0}개 실패", r.FailedCount);
            SetStatus(msg, r.FailedCount == 0);

            if (r.WrittenFiles.Count > 0) RevealInExplorer(r.WrittenFiles[0]);
        }

        private async Task RunDecryptAsync(string outDir)
        {
            // 조각이 여러 파일 / 여러 번의 붙여넣기에 흩어져 있을 수 있으므로
            // 입력을 전부 넘겨 한 번에 해석하게 한다.
            var texts = new List<string>();
            foreach (var it in _dec)
            {
                string text = it.ClipText;
                if (text == null)
                {
                    try { text = File.ReadAllText(it.FullPath); }
                    catch (Exception ex) { SetStatus(it.Name + " 읽기 실패: " + ex.Message, false); return; }
                }
                texts.Add(text);
            }

            Bar.IsIndeterminate = true;
            SetStatus("복원 중...", null);

            FileCryptJobs.UnpackResult r;
            try { r = await Task.Run(() => FileCryptJobs.Unpack(texts, outDir)); }
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

            if (r.WrittenFiles.Count > 0) RevealInExplorer(r.WrittenFiles[r.WrittenFiles.Count - 1]);
        }

        // ------------------------------------------------------------ 보조
        private void RevealInExplorer(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void SetStatus(string text, bool? good)
        {
            TxtStatus.Text = text;
            if (good == null)      TxtStatus.Foreground = (Brush)FindResource("Text2");
            else if (good == true) TxtStatus.Foreground = (Brush)FindResource("Ok");
            else                   TxtStatus.Foreground = (Brush)FindResource("Bad");
        }

        private void SetBusy(bool busy)
        {
            Bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnRun.IsEnabled = !busy && Current.Count > 0;
            BtnAddFiles.IsEnabled = !busy;
            BtnAddFolder.IsEnabled = !busy;
            BtnFromClip.IsEnabled = !busy;
            BtnRemove.IsEnabled = !busy;
            BtnClear.IsEnabled = !busy;
            RbEnc.IsEnabled = !busy;
            RbDec.IsEnabled = !busy;
            if (!busy) { Bar.Value = 0; RefreshUi(); }
        }
    }
}
