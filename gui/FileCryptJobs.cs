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
            /// <summary>통짜 텍스트. 조각내기를 했으면 만들지 않으므로 null - 길이는 TotalChars.</summary>
            public string FullText { get; set; }
            /// <summary>나누지 않았다면 결과가 됐을 글자수. 조각내기 여부와 무관하게 채워진다.</summary>
            public long TotalChars { get; set; }
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

        /// <summary>
        /// 입력 파일을 읽는다. 못 읽은 파일(잠김·없음)은 건너뛰고 errors 에 적는다.
        /// Pack 과 BuildContainer 가 같은 규칙을 쓰도록 한 곳에 둔다 - 예전에는 두 벌이라
        /// 잠긴 파일을 만나면 한쪽은 건너뛰고 한쪽은 예외로 멈췄다.
        /// </summary>
        private static List<ArchiveItem> ReadItems(IList<PackInput> inputs, List<string> errors, ref long sourceBytes)
        {
            var items = new List<ArchiveItem>(inputs.Count);
            foreach (var it in inputs)
            {
                try
                {
                    byte[] data = File.ReadAllBytes(it.FullPath);
                    items.Add(new ArchiveItem { Name = NameOf(it), Data = data });
                    sourceBytes += data.Length;
                }
                catch (Exception ex)
                {
                    errors.Add(Path.GetFileName(it.FullPath) + " : " + ex.Message);
                }
            }
            return items;
        }

        private static string NameOf(PackInput i)
        {
            return string.IsNullOrEmpty(i.RelPath) ? Path.GetFileName(i.FullPath) : i.RelPath;
        }

        /// <summary>파일들을 텍스트로 묶어 outDir 에 쓴다.</summary>
        public static PackResult Pack(IList<PackInput> inputs, string outDir, PackOptions opt)
        {
            if (inputs == null || inputs.Count == 0) throw new ArgumentException("처리할 파일이 없습니다.");
            if (string.IsNullOrWhiteSpace(outDir)) throw new ArgumentException("저장 폴더가 비었습니다.");
            if (opt == null) opt = new PackOptions();
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var result = new PackResult();
            long src = 0;
            var containers = new List<byte[]>();

            if (opt.Archive)
            {
                var items = ReadItems(inputs, result.Errors, ref src);
                if (items.Count == 0) throw new IOException("읽을 수 있는 파일이 없습니다.");
                containers.Add(FileCryptCore.EncryptArchive(items));
                result.FileCount = items.Count;
            }
            else
            {
                // 블록 방식은 파일마다 컨테이너 하나. 한꺼번에 다 읽어 두지 않고 하나씩 암호화한다.
                foreach (var it in inputs)
                {
                    try
                    {
                        byte[] plain = File.ReadAllBytes(it.FullPath);
                        containers.Add(FileCryptCore.Encrypt(NameOf(it), plain));
                        src += plain.Length;
                        result.FileCount++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add(Path.GetFileName(it.FullPath) + " : " + ex.Message);
                    }
                }
                if (result.FileCount == 0) throw new IOException("전부 실패했습니다.");
            }
            result.SourceBytes = src;
            result.FailedCount = result.Errors.Count;

            // 통짜 텍스트의 길이 = 블록들 + 사이 빈 줄(CRLF CRLF) + 끝 줄바꿈. 만들지 않고 계산한다.
            long total = 2;
            for (int i = 0; i < containers.Count; i++)
                total += FileCryptCore.ArmorLength(containers[i].Length, opt.LineWidth) + (i > 0 ? 4 : 0);
            result.TotalChars = total;

            // 파일 하나를 그대로 묶었을 때만 원래 이름을 물려준다.
            string baseName = (result.FileCount == 1 && !opt.Archive)
                ? Path.GetFileName(inputs[0].FullPath) + ".enc.txt"
                : string.Format("FCRYPT 묶음 {0}개 {1:yyyyMMdd-HHmmss}.txt", result.FileCount, DateTime.Now);

            // 나눌지, 얼마로 나눌지 결정한다.
            int splitAt = opt.SplitChars;
            bool auto = false;
            if (splitAt <= 0 && opt.AutoSplitOver > 0 && total > opt.AutoSplitOver)
            {
                splitAt = opt.AutoSplitOver;
                auto = true;
            }

            if (splitAt > 0)
            {
                // 컨테이너에서 바로 조각을 만든다(통짜 텍스트를 만들었다 되푸는 과정 없음).
                result.SplitWasAutomatic = auto;
                var pieces = new List<string>();
                foreach (var c in containers)
                    pieces.AddRange(FileCryptCore.ToArmorParts(c, splitAt, opt.LineWidth));
                if (pieces.Count == 0) throw new IOException("조각을 만들지 못했습니다.");

                string stem = Path.GetFileNameWithoutExtension(baseName);
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < pieces.Count; i++)
                {
                    string pn = string.Format("{0} [{1}of{2}].txt", stem, i + 1, pieces.Count);
                    string pp = FileCryptCore.ResolveNonClobbering(outDir, pn, known);
                    File.WriteAllText(pp, pieces[i] + "\r\n", new UTF8Encoding(false));
                    result.WrittenFiles.Add(pp);
                    if (pieces[i].Length > result.LongestPartChars) result.LongestPartChars = pieces[i].Length;
                }
                result.PartCount = pieces.Count;
                result.ClipboardText = pieces[0];
                return result;
            }

            var sb = new StringBuilder((int)Math.Min(int.MaxValue, total));
            for (int i = 0; i < containers.Count; i++)
            {
                if (i > 0) sb.Append("\r\n\r\n");
                FileCryptCore.AppendArmor(sb, containers[i], opt.LineWidth);
                containers[i] = null;   // 텍스트로 옮긴 컨테이너는 바로 놓아 준다
            }
            sb.Append("\r\n");
            result.FullText = sb.ToString();

            string dest = FileCryptCore.ResolveNonClobbering(outDir, baseName);
            File.WriteAllText(dest, result.FullText, new UTF8Encoding(false));
            result.WrittenFiles.Add(dest);
            result.ClipboardText = result.FullText;
            return result;
        }

        /// <summary>
        /// 고른 파일들을 컨테이너 하나로 묶는다(항상 아카이브).
        /// 보고 시스템에 올릴 때 쓴다 — 컨테이너가 하나여야 "조각 1개 = 날짜 1개" 가 성립하고,
        /// 받아올 때도 한 묶음으로 되돌아온다. 폴더 구조는 상대 경로로 그대로 보존된다.
        /// 못 읽은 파일은 Pack 과 똑같이 건너뛰고 errors 에 적는다.
        /// </summary>
        public static byte[] BuildContainer(IList<PackInput> inputs, out int fileCount, out long sourceBytes,
                                            out List<string> errors)
        {
            fileCount = 0; sourceBytes = 0;
            errors = new List<string>();
            if (inputs == null || inputs.Count == 0) throw new ArgumentException("처리할 파일이 없습니다.");

            var items = ReadItems(inputs, errors, ref sourceBytes);
            if (items.Count == 0) throw new IOException("읽을 수 있는 파일이 없습니다.");
            fileCount = items.Count;
            return FileCryptCore.EncryptArchive(items);
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

        private static string JoinTexts(IEnumerable<string> texts)
        {
            var joined = new StringBuilder();
            foreach (var t in texts) { if (t != null) joined.Append(t).Append("\r\n"); }
            return joined.ToString();
        }

        /// <summary>
        /// 넣어 둔 텍스트들을 합쳐 "블록 몇 개 / 조각 몇 개 모임" 만 센다. 복원(디코딩·복호화)은 하지 않는다.
        /// 창이 목록이 바뀔 때 부른다. Unpack 과 같은 규칙(합친 뒤 해석)이라 두 결과가 어긋나지 않는다.
        /// </summary>
        public static FileCryptCore.TextScan InspectInputs(IEnumerable<string> texts)
        {
            return FileCryptCore.Scan(JoinTexts(texts));
        }

        /// <summary>
        /// 여러 텍스트 조각(파일에서 읽은 것, 붙여넣은 것)을 전부 합쳐 한 번에 해석하고 복원한다.
        /// 흩어진 조각이 모이도록 반드시 합친 뒤에 해석해야 한다.
        /// </summary>
        public static UnpackResult Unpack(IEnumerable<string> texts, string outDir)
        {
            if (string.IsNullOrWhiteSpace(outDir)) throw new ArgumentException("저장 폴더가 비었습니다.");

            string allText = JoinTexts(texts);

            var result = new UnpackResult { TargetDir = outDir };
            var containers = FileCryptCore.ExtractBlocks(allText);
            result.BlockCount = containers.Count;

            if (containers.Count == 0)
            {
                result.PendingParts = FileCryptCore.InspectParts(allText);
                return result;
            }

            // 파일이 여러 개면 하위 폴더를 만든다. 블록이 2개 이상이면 셀 것도 없이 여러 개다.
            // 블록이 1개일 때만 열어 봐야 알 수 있고, 그때 푼 결과는 아래에서 그대로 쓴다
            // (예전에는 개수를 세려고 모든 블록을 한 번씩 더 복호화했다).
            List<DecryptedFile> first = null;
            Exception firstError = null;
            bool many = containers.Count > 1;
            if (!many)
            {
                try { first = FileCryptCore.DecryptAll(containers[0]); many = first.Count > 1; }
                catch (Exception ex) { firstError = ex; }
            }

            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            if (many)
            {
                result.TargetDir = Path.Combine(outDir, string.Format("FCRYPT 복원 {0:yyyyMMdd-HHmmss}", DateTime.Now));
                Directory.CreateDirectory(result.TargetDir);
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < containers.Count; i++)
            {
                try
                {
                    if (i == 0 && firstError != null) throw firstError;
                    var files = (i == 0 && first != null) ? first : FileCryptCore.DecryptAll(containers[i]);
                    containers[i] = null;   // 다 푼 컨테이너는 바로 놓아 준다
                    foreach (var df in files)
                    {
                        // 파일 하나가 실패해도(경로 길이 등) 나머지는 계속 복원한다.
                        try
                        {
                            string dest = FileCryptCore.ResolveNonClobbering(result.TargetDir, df.FileName, known);
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
