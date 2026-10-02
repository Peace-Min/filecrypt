using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace FileCrypt
{
    /// <summary>
    /// 묶기 목록에 넣은 파일들의 폴더 트리. 왼쪽 패널에서 폴더를 체크하면 그 폴더의 파일만 처리한다.
    ///
    /// 체크는 폴더마다 "자기 파일을 넣을지"(SelfIncluded)로 기억하고, 화면의 체크 상태(State)는
    /// 자기 파일과 하위 폴더들로 계산한다: 전부 켜짐 = 체크, 전부 꺼짐 = 빈칸, 섞임 = 일부(null).
    /// 체크박스를 누르면 그 폴더와 하위 폴더 전체가 한꺼번에 켜지거나 꺼진다.
    ///
    /// WPF 타입을 쓰지 않는다(테스트가 닿도록). 화면은 INotifyPropertyChanged 로만 따라온다.
    /// </summary>
    public sealed class FolderNode : INotifyPropertyChanged
    {
        internal FolderTree Owner;
        public FolderNode Parent { get; internal set; }
        /// <summary>이 노드가 뜻하는 폴더의 전체 경로.</summary>
        public string Path { get; internal set; }
        /// <summary>화면에 보일 이름 = 폴더 이름(드라이브 루트면 그 경로). 전체 경로는 Path(툴팁).</summary>
        public string Name { get; internal set; }
        public List<FolderNode> Children { get; private set; }
        /// <summary>이 폴더에 바로 들어 있는 파일 수.</summary>
        public int OwnFiles { get; internal set; }
        /// <summary>하위 폴더까지 합친 파일 수.</summary>
        public int TotalFiles { get; internal set; }
        /// <summary>이 폴더에 바로 들어 있는 파일을 처리에 넣는가.</summary>
        public bool SelfIncluded { get; internal set; }
        public bool IsExpanded { get; set; }

        public FolderNode() { Children = new List<FolderNode>(); SelfIncluded = true; IsExpanded = true; }

        public string Label { get { return string.Format("{0}  ({1})", Name, TotalFiles); } }

        /// <summary>true = 전부 처리, false = 전부 제외, null = 일부만.</summary>
        public bool? State
        {
            get
            {
                bool any = false, all = true;
                if (OwnFiles > 0) { if (SelfIncluded) any = true; else all = false; }
                foreach (var c in Children)
                {
                    bool? s = c.State;
                    if (s == null) return null;
                    if (s == true) any = true; else all = false;
                }
                if (all) return true;
                return any ? (bool?)null : false;
            }
            set
            {
                if (Owner == null) return;
                // 섞임(■)에서 누르면 전부 켠다. WPF 체크박스는 섞임 -> 꺼짐으로 넘겨 주므로(두 상태 체크박스의
                // 기본 동작) 여기서 바로잡는다 - 키보드 Space 와 같게. (UI 시험으로 드러남)
                bool include = State == null ? true : value != false;
                Owner.Set(this, include);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        internal void NotifyState()
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs("State"));
        }
    }

    public sealed class FolderTree
    {
        private readonly Dictionary<string, FolderNode> _byPath =
            new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);

        public List<FolderNode> Roots { get; private set; }

        /// <summary>체크가 바뀌었을 때(누가 체크박스를 눌렀을 때).</summary>
        public event Action Changed;

        private FolderTree() { Roots = new List<FolderNode>(); }

        /// <summary>
        /// 파일 경로들로 트리를 만든다. excludedDirs 에 있는 폴더는 꺼진 채로 시작한다
        /// (목록을 다시 만들 때 사람이 꺼 둔 것을 잃지 않으려고).
        /// 맨 위에서 파일 없이 하위 폴더 하나로만 이어지는 단계(C:\ → Users → CEO …)는 건너뛴다.
        /// </summary>
        public static FolderTree Build(IEnumerable<string> filePaths, ICollection<string> excludedDirs)
        {
            var t = new FolderTree();
            var top = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in filePaths)
            {
                if (string.IsNullOrEmpty(f)) continue;
                string dir;
                try { dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(f)); }
                catch { continue; }
                if (string.IsNullOrEmpty(dir)) continue;

                FolderNode node = t.Ensure(top, dir);
                node.OwnFiles++;
                for (var n = node; n != null; n = n.Parent) n.TotalFiles++;
            }

            foreach (var root in top.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                // 파일이나 갈래가 처음 나오는 폴더부터 보인다(좁은 패널에서 긴 앞부분이 잘리지 않게).
                // 이름은 그 폴더 이름만, 전체 경로는 Path(툴팁).
                var r = root;
                while (r.OwnFiles == 0 && r.Children.Count == 1) r = r.Children[0];
                r.Parent = null;
                t.Roots.Add(r);
            }

            foreach (var n in t._byPath.Values)
            {
                n.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                if (excludedDirs != null && excludedDirs.Contains(n.Path)) n.SelfIncluded = false;
            }
            return t;
        }

        /// <summary>dir 까지의 노드를 (없으면 만들어) 돌려준다. "C:\a\b" -> C:\ / a / b.</summary>
        private FolderNode Ensure(Dictionary<string, FolderNode> top, string dir)
        {
            FolderNode n;
            if (_byPath.TryGetValue(dir, out n)) return n;

            string parent = System.IO.Path.GetDirectoryName(dir);
            n = new FolderNode { Owner = this, Path = dir };
            if (string.IsNullOrEmpty(parent))
            {
                n.Name = dir;                 // 드라이브 루트(C:\) 또는 \\서버\공유
                top[dir] = n;
            }
            else
            {
                var p = Ensure(top, parent);
                n.Name = System.IO.Path.GetFileName(dir);
                n.Parent = p;
                p.Children.Add(n);
            }
            _byPath[dir] = n;
            return n;
        }

        /// <summary>그 폴더와 하위 폴더 전체를 켜거나 끈다.</summary>
        public void Set(FolderNode node, bool include)
        {
            SetSubtree(node, include);
            foreach (var n in _byPath.Values) n.NotifyState();
            var h = Changed;
            if (h != null) h();
        }

        private static void SetSubtree(FolderNode n, bool include)
        {
            n.SelfIncluded = include;
            foreach (var c in n.Children) SetSubtree(c, include);
        }

        public void SetAll(bool include)
        {
            foreach (var r in Roots) SetSubtree(r, include);
            foreach (var n in _byPath.Values) n.NotifyState();
            var h = Changed;
            if (h != null) h();
        }

        /// <summary>이 파일을 처리에 넣는가 = 그 파일이 든 폴더가 켜져 있는가.</summary>
        public bool IsIncluded(string filePath)
        {
            try
            {
                FolderNode n;
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath));
                return dir == null || !_byPath.TryGetValue(dir, out n) || n.SelfIncluded;
            }
            catch { return true; }
        }

        /// <summary>꺼 둔 폴더들(파일이 있는 폴더만). 트리를 다시 만들 때 넘겨 상태를 이어 간다.</summary>
        public HashSet<string> ExcludedDirs()
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in _byPath.Values) if (!n.SelfIncluded && n.OwnFiles > 0) s.Add(n.Path);
            return s;
        }

        public FolderNode Find(string dir)
        {
            FolderNode n;
            return _byPath.TryGetValue(dir.TrimEnd('\\'), out n) ? n : null;
        }
    }
}
