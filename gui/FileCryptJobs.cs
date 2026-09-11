using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FileCrypt
{
    /// <summary>
    /// 창(MainWindow)이 하던 파일 처리 절차를 여기로 옮겼다.
    /// UI 안에 있으면 자동 테스트가 닿지 않는다. 창은 이 클래스를 부르는 얇은 껍데기로 둔다.
    /// 이 안에는 WPF 타입이 하나도 없어야 한다.
    /// </summary>
    public static class FileCryptJobs
    {
        // ------------------------------------------------------------ 묶기
        public sealed class PackInput
        {
            public string FullPath { get; set; }
            /// <summary>컨테이너에 기록할 이름. 비우면 파일명만 쓴다.</summary>
            public string RelPath { get; set; }
        }

        public sealed class PackOptions
        {
            /// <summary>전부 하나의 아카이브 블록으로 묶는다.</summary>
            public bool Archive { get; set; }
            /// <summary>0 보다 크면 결과를 항상 이 글자수 이하 조각으로 나눈다.</summary>
            public int SplitChars { get; set; }

            /// <summary>
            /// 0 보다 크면 "필요할 때만" 나눈다. 결과 텍스트가 이 글자수를 넘을 때만 나누고,
            /// 넘지 않으면 통짜 파일 하나로 둔다. SplitChars 가 지정돼 있으면 그쪽이 우선.
            /// 결과 크기는 압축해 보기 전에는 알 수 없으므로, 만들어 놓고 재서 판단한다.
            /// </summary>
            public int AutoSplitOver { get; set; }
            public int LineWidth { get; set; }

            public PackOptions() { LineWidth = FileCryptCore.DefaultWidth; }
        }

        public sealed class PackResult
        {
            /// <summary>만들어진 텍스트 파일들. 조각내기면 여러 개.</summary>
            public List<string> WrittenFiles { get; set; }
            /// <summary>클립보드에 넣을 내용. 조각내기면 1번 조각.</summary>
            public string ClipboardText { get; set; }
            /// <summary>전체 텍스트 (조각내기 전)</summary>
            public string FullText { get; set; }
            public int FileCount { get; set; }
            public int FailedCount { get; set; }
            public List<string> Errors { get; set; }
            public long SourceBytes { get; set; }
            public int PartCount { get; set; }
            public int LongestPartChars { get; set; }
            /// <summary>"필요할 때만" 기준에 걸려서 나눴는지 (사용자가 직접 지정한 경우는 false)</summary>
            public bool SplitWasAutomatic { get; set; }

            public PackResult()
            {
                WrittenFiles = new List<string>();
                Errors = new List<string>();
            }
        }

        /// <summary>파일들을 텍스트로 묶어 outDir 에 쓴다.</summary>
        public static PackResult Pack(IList<PackInput> inputs, string outDir, PackOptions opt)
        {
            if (inputs == null || inputs.Count == 0) throw new ArgumentException("처리할 파일이 없습니다.");
            if (string.IsNullOrWhiteSpace(outDir)) throw new ArgumentException("저장 폴더가 비었습니다.");
            if (opt == null) opt = new PackOptions();
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var result = new PackResult();
            var chunks = new List<string>();

            string NameOf(PackInput i)
            {
                return string.IsNullOrEmpty(i.RelPath) ? Path.GetFileName(i.FullPath) : i.RelPath;
            }

            if (opt.Archive)
            {
                var items = new List<ArchiveItem>();
                foreach (var it in inputs)
                {
                    try
                    {
                        byte[] data = File.ReadAllBytes(it.FullPath);
                        items.Add(new ArchiveItem { Name = NameOf(it), Data = data });
                        result.SourceBytes += data.Length;
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add(Path.GetFileName(it.FullPath) + " : " + ex.Message);
                    }
                }
                if (items.Count == 0) throw new IOException("읽을 수 있는 파일이 없습니다.");

                chunks.Add(FileCryptCore.ToArmor(FileCryptCore.EncryptArchive(items), opt.LineWidth));
                result.FileCount = items.Count;
            }
            else
            {
                foreach (var it in inputs)
                {
                    try
                    {
                        byte[] plain = File.ReadAllBytes(it.FullPath);
                        chunks.Add(FileCryptCore.ToArmor(FileCryptCore.Encrypt(NameOf(it), plain), opt.LineWidth));
                        result.SourceBytes += plain.Length;
                        result.FileCount++;
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add(Path.GetFileName(it.FullPath) + " : " + ex.Message);
                    }
                }
                if (result.FileCount == 0) throw new IOException("전부 실패했습니다.");
            }

            result.FullText = string.Join("\r\n\r\n", chunks.ToArray()) + "\r\n";

            // 파일 하나를 그대로 묶었을 때만 원래 이름을 물려준다.
            string baseName = (result.FileCount == 1 && !opt.Archive)
                ? Path.GetFileName(inputs[0].FullPath) + ".enc.txt"
                : string.Format("FCRYPT 묶음 {0}개 {1:yyyyMMdd-HHmmss}.txt", result.FileCount, DateTime.Now);

            // 나눌지, 얼마로 나눌지 결정한다.
            int splitAt = opt.SplitChars;
            bool auto = false;
            if (splitAt <= 0 && opt.AutoSplitOver > 0 && result.FullText.Length > opt.AutoSplitOver)
            {
                splitAt = opt.AutoSplitOver;
                auto = true;
            }

            if (splitAt > 0)
            {
                result.SplitWasAutomatic = auto;
                var pieces = new List<string>();
                foreach (var c in FileCryptCore.ExtractBlocks(result.FullText))
                    pieces.AddRange(FileCryptCore.ToArmorParts(c, splitAt, opt.LineWidth));
                if (pieces.Count == 0) throw new IOException("조각을 만들지 못했습니다.");

                string stem = Path.GetFileNameWithoutExtension(baseName);
                for (int i = 0; i < pieces.Count; i++)
                {
                    string pn = string.Format("{0} [{1}of{2}].txt", stem, i + 1, pieces.Count);
                    string pp = FileCryptCore.ResolveNonClobbering(outDir, pn);
                    File.WriteAllText(pp, pieces[i] + "\r\n", new UTF8Encoding(false));
                    result.WrittenFiles.Add(pp);
                    if (pieces[i].Length > result.LongestPartChars) result.LongestPartChars = pieces[i].Length;
                }
                result.PartCount = pieces.Count;
                result.ClipboardText = pieces[0];
                return result;
            }

            string dest = FileCryptCore.ResolveNonClobbering(outDir, baseName);
            File.WriteAllText(dest, result.FullText, new UTF8Encoding(false));
            result.WrittenFiles.Add(dest);
            result.ClipboardText = result.FullText;
            return result;
        }

        // ------------------------------------------------------------ 되돌리기
        public sealed class UnpackResult
        {
            public List<string> WrittenFiles { get; set; }
            public int OkCount { get; set; }
            public int FailedCount { get; set; }
            public List<string> Errors { get; set; }
            public long TotalBytes { get; set; }
            /// <summary>실제로 파일이 쓰인 폴더. 파일이 여러 개면 하위 폴더가 새로 만들어진다.</summary>
            public string TargetDir { get; set; }
            public int BlockCount { get; set; }
            /// <summary>블록을 하나도 못 만든 경우, 모자란 조각 상황. 비어 있으면 아예 없는 것.</summary>
            public List<FileCryptCore.PartGroup> PendingParts { get; set; }

            public UnpackResult()
            {
                WrittenFiles = new List<string>();
                Errors = new List<string>();
                PendingParts = new List<FileCryptCore.PartGroup>();
            }
        }

        /// <summary>
        /// 여러 텍스트 조각(파일에서 읽은 것, 붙여넣은 것)을 전부 합쳐 한 번에 해석하고 복원한다.
        /// 흩어진 조각이 모이도록 반드시 합친 뒤에 해석해야 한다.
        /// </summary>
        public static UnpackResult Unpack(IEnumerable<string> texts, string outDir)
        {
            if (string.IsNullOrWhiteSpace(outDir)) throw new ArgumentException("저장 폴더가 비었습니다.");

            var joined = new StringBuilder();
            foreach (var t in texts) { if (t != null) joined.Append(t).Append("\r\n"); }
            string allText = joined.ToString();

            var result = new UnpackResult { TargetDir = outDir };
            var containers = FileCryptCore.ExtractBlocks(allText);
            result.BlockCount = containers.Count;

            if (containers.Count == 0)
            {
                result.PendingParts = FileCryptCore.InspectParts(allText);
                return result;
            }

            // 아카이브 블록 하나에도 파일이 여러 개 들어있다. 폴더를 만들지 미리 판단한다.
            int expected = 0;
            foreach (var c0 in containers)
            {
                try { expected += FileCryptCore.DecryptAll(c0).Count; } catch { expected += 1; }
            }

            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            if (expected > 1)
            {
                result.TargetDir = Path.Combine(outDir, string.Format("FCRYPT 복원 {0:yyyyMMdd-HHmmss}", DateTime.Now));
                Directory.CreateDirectory(result.TargetDir);
            }

            for (int i = 0; i < containers.Count; i++)
            {
                try
                {
                    foreach (var df in FileCryptCore.DecryptAll(containers[i]))
                    {
                        // 파일 하나가 실패해도(경로 길이 등) 나머지는 계속 복원한다.
                        try
                        {
                            string dest = FileCryptCore.ResolveNonClobbering(result.TargetDir, df.FileName);
                            File.WriteAllBytes(dest, df.Data);
                            result.WrittenFiles.Add(dest);
                            result.TotalBytes += df.Data.Length;
                            result.OkCount++;
                        }
                        catch (Exception exf)
                        {
                            result.FailedCount++;
                            result.Errors.Add(df.FileName + " : " + exf.Message);
                        }
                    }
                }
                catch (FileCryptAuthException)
                {
                    result.FailedCount++;
                    result.Errors.Add(string.Format("{0}번째 블록: 손상되었거나 이 도구로 만든 것이 아님", i + 1));
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    result.Errors.Add(string.Format("{0}번째 블록: {1}", i + 1, ex.Message));
                }
            }
            return result;
        }

        /// <summary>모자란 조각 상황을 사람이 읽을 문장으로.</summary>
        public static string DescribePending(IList<FileCryptCore.PartGroup> groups)
        {
            if (groups == null || groups.Count == 0) return "FileCrypt 블록을 찾지 못했습니다.";
            var sb = new StringBuilder("조각이 모자랍니다. ");
            foreach (var g in groups)
            {
                sb.AppendFormat("{0}/{1} 모임", g.Have.Count, g.Total);
                if (g.Missing.Count > 0)
                {
                    sb.Append(" (없는 것: ");
                    for (int i = 0; i < g.Missing.Count && i < 12; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(g.Missing[i]);
                    }
                    if (g.Missing.Count > 12) sb.Append(" …");
                    sb.Append(")");
                }
                sb.Append("  ");
            }
            sb.Append("나머지 조각을 더 넣고 다시 실행하세요.");
            return sb.ToString();
        }
    }
}
