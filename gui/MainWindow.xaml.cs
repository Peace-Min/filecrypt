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
    public enum ItemKind
    {
        ToText,   // 일반 파일 -> 텍스트로 묶을 대상
        ToFile    // FileCrypt 텍스트 -> 파일로 되돌릴 대상
    }

    public enum RunMode
    {
        Auto = 0,
        ToText = 1,
        ToFile = 2
    }

    public class Item
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public string FullPath { get; set; }   // 클립보드 항목이면 null
        public string ClipText { get; set; }   // 클립보드 항목의 본문
        public long Size { get; set; }
        public ItemKind Kind { get; set; }
        public int BlockCount { get; set; }

        public string SizeText
        {
            get
            {
                if (Size >= 1048576) return (Size / 1048576.0).ToString("N1") + " MB";
                if (Size >= 1024)    return (Size / 1024.0).ToString("N0") + " KB";
                return Size.ToString("N0") + " B";
            }
        }

        public string KindText
        {
            get { return Kind == ItemKind.ToFile ? string.Format("복원 {0}개", BlockCount) : "묶기"; }
        }

        public Brush BadgeBg
        {
            get
            {
                return Kind == ItemKind.ToFile
                    ? new SolidColorBrush(Color.FromRgb(0xE7, 0xF6, 0xEC))
                    : new SolidColorBrush(Color.FromRgb(0xEC, 0xF1, 0xFE));
            }
        }

        public Brush BadgeFg
        {
            get
            {
                return Kind == ItemKind.ToFile
                    ? new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x45))
                    : new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
            }
        }
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<Item> _items = new ObservableCollection<Item>();

        public MainWindow()
        {
            InitializeComponent();
            LvItems.ItemsSource = _items;
            _items.CollectionChanged += (s, e) => RefreshUi();
            TxtOutDir.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            RefreshUi();
        }

        // ------------------------------------------------------------ 판별
        /// <summary>앞부분 64KB 안에 FCRYPT 표식이 있으면 복원 대상으로 본다.</summary>
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
                    string head = Encoding.ASCII.GetString(buf, 0, read);
                    return head.IndexOf("-----BEGIN FCRYPT", StringComparison.Ordinal) >= 0;
                }
            }
            catch { return false; }
        }

        private RunMode SelectedMode
        {
            get { return (RunMode)Math.Max(0, CbMode.SelectedIndex); }
        }

        /// <summary>실제로 수행할 동작. 섞여 있어 정할 수 없으면 null.</summary>
        private RunMode? EffectiveMode()
        {
            if (SelectedMode != RunMode.Auto) return SelectedMode;
            if (_items.Count == 0) return null;

            bool anyText = _items.Any(i => i.Kind == ItemKind.ToText);
            bool anyFile = _items.Any(i => i.Kind == ItemKind.ToFile);
            if (anyText && anyFile) return null;
            return anyFile ? RunMode.ToFile : RunMode.ToText;
        }

        // ------------------------------------------------------------ 목록
        private void RefreshUi()
        {
            bool any = _items.Count > 0;
            LvItems.Visibility  = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyPane.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            TxtCount.Text = any ? string.Format("{0}개", _items.Count) : "";

            RunMode? m = EffectiveMode();

            if (!any)
            {
                TxtPlan.Text = "파일을 추가하면 무엇을 할지 여기에 표시됩니다.";
                BtnRun.Content = "실행";
                BtnRun.IsEnabled = false;
                ChkClipboard.Visibility = Visibility.Visible;
                return;
            }

            if (m == null)
            {
                int a = _items.Count(i => i.Kind == ItemKind.ToText);
                int b = _items.Count(i => i.Kind == ItemKind.ToFile);
                TxtPlan.Text = string.Format("일반 파일 {0}개와 FileCrypt 텍스트 {1}개가 섞여 있습니다. 아래 [동작]에서 하나를 고르세요.", a, b);
                BtnRun.Content = "동작 선택 필요";
                BtnRun.IsEnabled = false;
                return;
            }

            if (m == RunMode.ToText)
            {
                int n = _items.Count(i => i.FullPath != null);
                long total = _items.Where(i => i.FullPath != null).Sum(i => i.Size);
                TxtPlan.Text = string.Format("파일 {0}개  →  텍스트 파일 1개 ({1:N0} B 를 묶습니다)", n, total);
                BtnRun.Content = "텍스트로 만들기";
                BtnRun.IsEnabled = n > 0;
                ChkClipboard.Visibility = Visibility.Visible;
            }
            else
            {
                int srcN = _items.Count;
                int blocks = _items.Sum(i => i.BlockCount);
                TxtPlan.Text = string.Format("텍스트 {0}개 (블록 {1}개)  →  파일 {1}개로 되돌립니다", srcN, blocks);
                BtnRun.Content = "파일로 되돌리기";
                BtnRun.IsEnabled = blocks > 0;
                ChkClipboard.Visibility = Visibility.Collapsed;
            }
        }

        private void CbMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            RefreshUi();
        }

        private void AddPath(string path)
        {
            if (Directory.Exists(path))
            {
                foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    AddFile(f);
                return;
            }
            AddFile(path);
        }

        private void AddFile(string path)
        {
            if (!File.Exists(path)) return;
            if (_items.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))) return;

            var fi = new FileInfo(path);
            var it = new Item
            {
                Name = fi.Name,
                Folder = fi.DirectoryName,
                FullPath = fi.FullName,
                Size = fi.Length,
                Kind = ItemKind.ToText
            };

            if (LooksLikeFCryptFile(path))
            {
                it.Kind = ItemKind.ToFile;
                try { it.BlockCount = FileCryptCore.ExtractBlocks(File.ReadAllText(path)).Count; }
                catch { it.BlockCount = 0; }
            }

            _items.Add(it);
        }

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "파일 선택 (여러 개 가능)",
                Filter = "모든 파일 (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            foreach (string f in dlg.FileNames) AddFile(f);
            AutoSuggestOutDir();
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
            _items.Add(new Item
            {
                Name = "[클립보드]",
                Folder = "붙여넣은 텍스트",
                FullPath = null,
                ClipText = text,
                Size = Encoding.UTF8.GetByteCount(text),
                Kind = ItemKind.ToFile,
                BlockCount = n
            });
            SetStatus(string.Format("클립보드에서 블록 {0}개를 가져왔습니다.", n), true);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            foreach (var it in LvItems.SelectedItems.Cast<Item>().ToList()) _items.Remove(it);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
            SetStatus("", null);
        }

        private void AutoSuggestOutDir()
        {
            var first = _items.FirstOrDefault(i => i.FullPath != null);
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

        // 창 아무 데나 떨어뜨려도 받는다.
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
            RunMode? mode = EffectiveMode();
            if (mode == null) { SetStatus("동작을 선택하세요.", false); return; }

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
                if (mode == RunMode.ToText) await RunEncryptAsync(outDir);
                else                        await RunDecryptAsync(outDir);
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
            var sources = _items.Where(i => i.FullPath != null).ToList();
            if (sources.Count == 0) { SetStatus("처리할 파일이 없습니다.", false); return; }

            Bar.Maximum = sources.Count;
            Bar.Value = 0;

            var chunks = new List<string>();
            long srcBytes = 0;
            int done = 0, failed = 0;
            var errors = new List<string>();

            foreach (var it in sources)
            {
                string path = it.FullPath;
                try
                {
                    string armor = await Task.Run(() =>
                    {
                        byte[] plain = File.ReadAllBytes(path);
                        byte[] container = FileCryptCore.Encrypt(Path.GetFileName(path), plain, FileCryptCore.DefaultKey);
                        return FileCryptCore.ToArmor(container);
                    });
                    chunks.Add(armor);
                    srcBytes += it.Size;
                    done++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add(it.Name + " : " + ex.Message);
                }
                Bar.Value = done + failed;
                SetStatus(string.Format("{0}/{1} 처리 중...", done + failed, sources.Count), null);
            }

            if (done == 0) { SetStatus("전부 실패했습니다. " + string.Join(" / ", errors), false); return; }

            string text = string.Join("\r\n\r\n", chunks) + "\r\n";

            string fileName = done == 1
                ? Path.GetFileName(sources[0].FullPath) + ".enc.txt"
                : string.Format("FCRYPT 묶음 {0}개 {1:yyyyMMdd-HHmmss}.txt", done, DateTime.Now);

            string dest = FileCryptCore.ResolveNonClobbering(outDir, fileName);
            File.WriteAllText(dest, text, new UTF8Encoding(false));

            bool copied = false;
            if (ChkClipboard.IsChecked == true)
            {
                try { Clipboard.SetText(text); copied = true; } catch { }
            }

            string msg = string.Format("{0}개 → {1}  ({2:N0} B → {3:N0} 자)",
                                       done, Path.GetFileName(dest), srcBytes, text.Length);
            if (copied) msg += "  · 클립보드 복사됨";
            if (failed > 0) msg += string.Format("  · {0}개 실패", failed);
            SetStatus(msg, failed == 0);

            RevealInExplorer(dest);
        }

        private async Task RunDecryptAsync(string outDir)
        {
            var containers = new List<byte[]>();
            foreach (var it in _items.Where(i => i.Kind == ItemKind.ToFile))
            {
                string text = it.ClipText;
                if (text == null)
                {
                    try { text = File.ReadAllText(it.FullPath); }
                    catch (Exception ex) { SetStatus(it.Name + " 읽기 실패: " + ex.Message, false); return; }
                }
                containers.AddRange(FileCryptCore.ExtractBlocks(text));
            }

            if (containers.Count == 0)
            {
                SetStatus("FileCrypt 블록을 찾지 못했습니다.", false);
                return;
            }

            string targetDir = outDir;
            if (containers.Count > 1)
            {
                targetDir = Path.Combine(outDir, string.Format("FCRYPT 복원 {0:yyyyMMdd-HHmmss}", DateTime.Now));
                Directory.CreateDirectory(targetDir);
            }

            Bar.Maximum = containers.Count;
            Bar.Value = 0;

            int ok = 0, ng = 0;
            long total = 0;
            string lastPath = null;
            var errors = new List<string>();

            for (int i = 0; i < containers.Count; i++)
            {
                byte[] c = containers[i];
                try
                {
                    DecryptedFile df = await Task.Run(() => FileCryptCore.Decrypt(c, FileCryptCore.DefaultKey));
                    string dest = FileCryptCore.ResolveNonClobbering(targetDir, FileCryptCore.SanitizeFileName(df.FileName));
                    File.WriteAllBytes(dest, df.Data);
                    total += df.Data.Length;
                    lastPath = dest;
                    ok++;
                }
                catch (FileCryptAuthException)
                {
                    ng++;
                    errors.Add(string.Format("{0}번째 블록: 손상되었거나 암호가 걸려 있음", i + 1));
                }
                catch (Exception ex)
                {
                    ng++;
                    errors.Add(string.Format("{0}번째 블록: {1}", i + 1, ex.Message));
                }
                Bar.Value = ok + ng;
                SetStatus(string.Format("{0}/{1} 복원 중...", ok + ng, containers.Count), null);
            }

            string msg = ng == 0
                ? string.Format("{0}개 파일 복원 완료 ({1:N0} B) · 원본과 100% 일치 (SHA-256 검증)", ok, total)
                : string.Format("{0}개 복원 / {1}개 실패 · {2}", ok, ng, string.Join(" / ", errors));

            SetStatus(msg, ng == 0);
            if (lastPath != null) RevealInExplorer(lastPath);
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
            BtnRun.IsEnabled = !busy && _items.Count > 0 && EffectiveMode() != null;
            BtnAddFiles.IsEnabled = !busy;
            BtnAddFolder.IsEnabled = !busy;
            BtnFromClip.IsEnabled = !busy;
            BtnRemove.IsEnabled = !busy;
            BtnClear.IsEnabled = !busy;
            CbMode.IsEnabled = !busy;
            if (!busy) Bar.Value = 0;
        }
    }
}
