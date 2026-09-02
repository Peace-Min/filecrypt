using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace FileCrypt
{
    public class Item
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public string FullPath { get; set; }   // 클립보드에서 온 항목이면 null
        public string ClipText { get; set; }   // 클립보드 항목의 본문
        public long Size { get; set; }

        public string SizeText
        {
            get
            {
                if (Size >= 1024 * 1024) return (Size / 1048576.0).ToString("N1") + " MB";
                if (Size >= 1024)        return (Size / 1024.0).ToString("N0") + " KB";
                return Size.ToString("N0") + " B";
            }
        }
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<Item> _items = new ObservableCollection<Item>();
        private bool Encrypting { get { return RbEncrypt.IsChecked == true; } }

        public MainWindow()
        {
            InitializeComponent();
            LvItems.ItemsSource = _items;
            _items.CollectionChanged += (s, e) => RefreshList();
            TxtOutDir.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            RefreshList();
            ApplyMode();
        }

        // ------------------------------------------------------------ 모드
        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _items.Clear();
            ApplyMode();
        }

        private void ApplyMode()
        {
            if (BtnFromClip == null) return;

            if (Encrypting)
            {
                TxtHint.Text = "옮기고 싶은 파일을 아래에 끌어다 놓거나 [파일 추가]를 누르세요. 여러 개를 한 번에 처리합니다.";
                TxtEmpty.Text = "여기로 파일을 끌어다 놓으세요\n(폴더도 됩니다)";
                BtnFromClip.Visibility = Visibility.Collapsed;
                BtnAddFolder.Visibility = Visibility.Visible;
                ChkClipboard.Visibility = Visibility.Visible;
                BtnRun.Content = "텍스트로 만들기";
            }
            else
            {
                TxtHint.Text = "FileCrypt 텍스트 파일을 끌어다 놓거나, 복사해 둔 텍스트가 있으면 [클립보드에서 가져오기]를 누르세요.";
                TxtEmpty.Text = "여기로 FileCrypt 텍스트(.txt)를 끌어다 놓으세요\n또는 [클립보드에서 가져오기]";
                BtnFromClip.Visibility = Visibility.Visible;
                BtnAddFolder.Visibility = Visibility.Collapsed;
                ChkClipboard.Visibility = Visibility.Collapsed;
                BtnRun.Content = "파일로 되돌리기";
            }
            SetStatus("", null);
        }

        // ------------------------------------------------------------ 목록
        private void RefreshList()
        {
            bool any = _items.Count > 0;
            LvItems.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TxtEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            TxtCount.Text = any ? string.Format("{0}개", _items.Count) : "";
            BtnRun.IsEnabled = any;
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
            _items.Add(new Item
            {
                Name = fi.Name,
                Folder = fi.DirectoryName,
                FullPath = fi.FullName,
                Size = fi.Length
            });
        }

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = Encrypting ? "옮길 파일 선택 (여러 개 가능)" : "FileCrypt 텍스트 파일 선택",
                Filter = Encrypting ? "모든 파일 (*.*)|*.*" : "텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*"
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
                Name = string.Format("[클립보드] 블록 {0}개", n),
                Folder = "클립보드",
                FullPath = null,
                ClipText = text,
                Size = Encoding.UTF8.GetByteCount(text)
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
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string p in paths) AddPath(p);
            AutoSuggestOutDir();
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
            if (string.IsNullOrEmpty(outDir))
            {
                SetStatus("저장 폴더를 지정하세요.", false);
                return;
            }
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

            string msg = string.Format("{0}개 파일 → {1}  ({2:N0}자, {3:N0}B → {4:N0}B)",
                                       done, Path.GetFileName(dest), text.Length, srcBytes, text.Length);
            if (copied) msg += "  · 클립보드 복사됨";
            if (failed > 0) msg += string.Format("  · {0}개 실패", failed);
            SetStatus(msg, failed == 0);

            RevealInExplorer(dest);
        }

        private async Task RunDecryptAsync(string outDir)
        {
            // 목록의 모든 항목에서 블록을 모은다.
            var containers = new List<byte[]>();
            foreach (var it in _items)
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
                SetStatus("FileCrypt 블록을 찾지 못했습니다. (-----BEGIN FCRYPT MESSAGE----- 로 시작하는 부분이 있어야 합니다)", false);
                return;
            }

            // 여러 개면 폴더를 하나 만들어 모은다.
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
                    errors.Add(string.Format("{0}번째 블록: 손상되었거나 암호가 걸려 있습니다", i + 1));
                }
                catch (Exception ex)
                {
                    ng++;
                    errors.Add(string.Format("{0}번째 블록: {1}", i + 1, ex.Message));
                }
                Bar.Value = ok + ng;
                SetStatus(string.Format("{0}/{1} 복원 중...", ok + ng, containers.Count), null);
            }

            string msg;
            if (ng == 0)
                msg = string.Format("{0}개 파일 복원 완료  ({1:N0}B)  · 원본과 100% 일치 (SHA-256 검증)", ok, total);
            else
                msg = string.Format("{0}개 복원 / {1}개 실패  ·  {2}", ok, ng, string.Join(" / ", errors));

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
            if (good == null)       TxtStatus.Foreground = (Brush)FindResource("Text2");
            else if (good == true)  TxtStatus.Foreground = (Brush)FindResource("Ok");
            else                    TxtStatus.Foreground = (Brush)FindResource("Bad");
        }

        private void SetBusy(bool busy)
        {
            Bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnRun.IsEnabled = !busy && _items.Count > 0;
            BtnAddFiles.IsEnabled = !busy;
            BtnAddFolder.IsEnabled = !busy;
            BtnFromClip.IsEnabled = !busy;
            BtnRemove.IsEnabled = !busy;
            BtnClear.IsEnabled = !busy;
            RbEncrypt.IsEnabled = !busy;
            RbDecrypt.IsEnabled = !busy;
            if (!busy) Bar.Value = 0;
        }
    }
}
