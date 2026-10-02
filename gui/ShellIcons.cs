using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileCrypt
{
    /// <summary>
    /// 탐색기와 같은 아이콘(16px). Windows 셸(SHGetFileInfo)에서 받아 온다 - 설치된 프로그램에 따라 PC 마다 다르다.
    ///
    /// 확장자마다 한 번만 받아 캐시한다(파일 수천 개여도 확장자 수만큼만 호출, 디스크도 읽지 않음).
    /// exe·ico·lnk 처럼 파일마다 아이콘이 다른 종류만 그 파일을 직접 본다.
    /// UI 스레드에서 부른다(목록 바인딩이 부른다).
    /// </summary>
    internal static class ShellIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

        // 파일마다 아이콘이 따로 있는 종류. 이것만 실제 파일을 본다.
        private static readonly HashSet<string> PerFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".ico", ".lnk", ".url", ".cur", ".ani", ".scr", ".appref-ms" };

        private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static ImageSource _folder;

        /// <summary>닫힌 폴더 아이콘.</summary>
        public static ImageSource Folder
        {
            get { return _folder ?? (_folder = Get("folder", FILE_ATTRIBUTE_DIRECTORY, true)); }
        }

        /// <summary>그 파일 종류의 아이콘. 경로가 없거나(클립보드 항목) 못 받으면 일반 파일 아이콘.</summary>
        public static ImageSource ForFile(string pathOrName)
        {
            string ext = "";
            try { ext = Path.GetExtension(pathOrName ?? "") ?? ""; } catch { }

            if (PerFile.Contains(ext) && !string.IsNullOrEmpty(pathOrName) && File.Exists(pathOrName))
            {
                ImageSource own;
                if (Cache.TryGetValue(pathOrName, out own)) return own;
                own = Get(pathOrName, FILE_ATTRIBUTE_NORMAL, false) ?? ByExtension(ext);
                Cache[pathOrName] = own;
                return own;
            }
            return ByExtension(ext);
        }

        private static ImageSource ByExtension(string ext)
        {
            string key = "*" + ext;
            ImageSource img;
            if (Cache.TryGetValue(key, out img)) return img;
            // 파일이 있을 필요 없다: 확장자만으로 셸이 등록된 아이콘을 준다.
            img = Get("file" + ext, FILE_ATTRIBUTE_NORMAL, true);
            Cache[key] = img;
            return img;
        }

        private static ImageSource Get(string path, uint attr, bool byAttributesOnly)
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | (byAttributesOnly ? SHGFI_USEFILEATTRIBUTES : 0);
            try
            {
                IntPtr r = SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DestroyIcon(info.hIcon); }
            }
            catch { return null; }   // 아이콘을 못 받아도 목록은 그대로 보여야 한다
        }
    }
}
