// J-Rate : JPG / PNG 画像・MP4 動画選別ツール
// .NET Framework 4.x 標準の csc.exe (C# 5) でビルドできるように書いている。build.bat を参照。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JRate
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MainWindow(args));
        }    }

    // ------------------------------------------------------------------
    // 画像 1 枚分の情報
    // ------------------------------------------------------------------
    public class ImageItem : INotifyPropertyChanged
    {
        private int rating;
        private ImageSource thumbnail;

        public ImageItem(string path, DateTime modified)
        {
            Path = path;
            FileName = System.IO.Path.GetFileName(path);
            IsVideo = IsVideoFile(path);
            Modified = modified;
        }

        public string Path { get; private set; }
        public string FileName { get; private set; }
        public DateTime Modified { get; private set; }   // フォルダを開いた時点の更新日時 (並べ替え用)
        public bool IsVideo { get; private set; }

        // 解像度 (ピクセル)。メイン窓に初めて表示したときに調べる。0 = まだ不明
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }

        // サムネールに重ねる「▶」マーク
        public Visibility VideoMark { get { return IsVideo ? Visibility.Visible : Visibility.Collapsed; } }

        public static bool IsVideoFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".mp4" || ext == ".m4v";
        }

        public int Rating
        {
            get { return rating; }
            set
            {
                if (rating == value) return;
                rating = value;
                OnPropertyChanged("Rating");
                OnPropertyChanged("Stars");
            }
        }

        public string Stars { get { return StarText(rating); } }

        public ImageSource Thumbnail
        {
            get { return thumbnail; }
            set { thumbnail = value; OnPropertyChanged("Thumbnail"); OnPropertyChanged("GridImage"); }
        }

        // グリッド一覧 (中・大) 用の大きめのサムネール。できるまでは普通のサムネールを出す。
        private ImageSource gridThumb;
        public int GridThumbSize { get; private set; }
        public ImageSource GridImage { get { return gridThumb ?? thumbnail; } }

        public void SetGridThumb(ImageSource src, int size)
        {
            gridThumb = src;
            GridThumbSize = src == null ? 0 : size;
            OnPropertyChanged("GridImage");
        }

        public static string StarText(int r)
        {
            return new string('★', r) + new string('☆', 5 - r);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    // ------------------------------------------------------------------
    // エクスプローラーと同じ Windows プロパティシステム (System.Rating) で評価を読み書きする
    // ------------------------------------------------------------------
    public static class ShellRating
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public uint uintVal;
            [FieldOffset(8)] public IntPtr p1;
            [FieldOffset(16)] public IntPtr p2;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHGetPropertyStoreFromParsingName(
            string pszPath, IntPtr pbc, int flags, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        private const int GPS_DEFAULT = 0;
        private const int GPS_READWRITE = 2;
        private const ushort VT_EMPTY = 0;
        private const ushort VT_UI4 = 19;

        private static readonly Guid IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

        // PKEY_Rating {64440492-4C8B-11D1-8B70-080036B11A03}, 9
        private static PROPERTYKEY RatingKey()
        {
            var k = new PROPERTYKEY();
            k.fmtid = new Guid("64440492-4C8B-11D1-8B70-080036B11A03");
            k.pid = 9;
            return k;
        }

        // エクスプローラーが書き込む値 (★1=1, ★2=25, ★3=50, ★4=75, ★5=99)
        public static uint StarsToPercent(int stars)
        {
            switch (stars)
            {
                case 1: return 1;
                case 2: return 25;
                case 3: return 50;
                case 4: return 75;
                case 5: return 99;
                default: return 0;
            }
        }

        // エクスプローラーの表示と同じ区切りで ★ 数に変換
        public static int PercentToStars(int p)
        {
            if (p <= 0) return 0;
            if (p <= 12) return 1;
            if (p <= 37) return 2;
            if (p <= 62) return 3;
            if (p <= 87) return 4;
            return 5;
        }

        public static int Read(string path)
        {
            IPropertyStore store = null;
            try
            {
                Guid iid = IID_IPropertyStore;
                SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_DEFAULT, ref iid, out store);
                PROPERTYKEY key = RatingKey();
                PROPVARIANT pv;
                if (store.GetValue(ref key, out pv) != 0) return 0;
                int stars = pv.vt == VT_UI4 ? PercentToStars((int)pv.uintVal) : 0;
                PropVariantClear(ref pv);
                return stars;
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }

        // stars = 0 で評価を削除
        public static void Write(string path, int stars)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    WriteOnce(path, stars);
                    return;
                }
                catch (Exception ex)
                {
                    // 読み込みスレッドが一瞬ファイルを開いている場合などに備えてリトライ
                    last = ex;
                    Thread.Sleep(60);
                }
            }
            throw last;
        }

        private static void WriteOnce(string path, int stars)
        {
            IPropertyStore store = null;
            try
            {
                Guid iid = IID_IPropertyStore;
                SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_READWRITE, ref iid, out store);
                PROPERTYKEY key = RatingKey();
                var pv = new PROPVARIANT();
                if (stars > 0)
                {
                    pv.vt = VT_UI4;
                    pv.uintVal = StarsToPercent(stars);
                }
                else
                {
                    pv.vt = VT_EMPTY;
                }
                int hr = store.SetValue(ref key, ref pv);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                hr = store.Commit();
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }
    }

    // ------------------------------------------------------------------
    // ごみ箱への削除
    // ------------------------------------------------------------------
    public static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public IntPtr pFrom;
            public IntPtr pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public IntPtr lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_WANTNUKEWARNING = 0x4000;   // ごみ箱に入らず完全削除になる場合は確認を出す

        // 戻り値 0 で成功。ユーザーが確認ダイアログで取り消した場合は aborted = true。
        public static int Send(IntPtr owner, string path, out bool aborted)
        {
            // pFrom は二重の NUL 終端が必要
            IntPtr from = Marshal.StringToHGlobalUni(path + "\0");
            try
            {
                var op = new SHFILEOPSTRUCT();
                op.hwnd = owner;
                op.wFunc = FO_DELETE;
                op.pFrom = from;
                op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING);
                int rc = SHFileOperationW(ref op);
                aborted = op.fAnyOperationsAborted;
                return rc;
            }
            finally
            {
                Marshal.FreeHGlobal(from);
            }
        }
    }

    // ------------------------------------------------------------------
    // 画像読み込み (ファイルをロックしないよう、読み終えたら即座に閉じる)
    // ------------------------------------------------------------------
    public static class ImageLoader
    {
        private const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

        // メタデータだけを読んで評価を取得 (画素はデコードしないので高速)
        public static int ReadRating(string path)
        {
            if (ImageItem.IsVideoFile(path)) return ShellRating.Read(path);   // 動画はシェルのプロパティから
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share))
                {
                    var dec = BitmapDecoder.Create(fs,
                        BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.None);
                    var md = dec.Frames[0].Metadata as BitmapMetadata;
                    if (md != null)
                    {
                        object raw = SafeQuery(md, "System.Rating");
                        if (raw != null)
                        {
                            int stars = ShellRating.PercentToStars(Convert.ToInt32(raw));
                            if (stars > 0) return stars;
                        }
                        try
                        {
                            int simple = md.Rating;
                            if (simple >= 1 && simple <= 5) return simple;
                        }
                        catch { }
                    }
                }
                return 0;
            }
            catch
            {
                // WIC で読めない場合はシェル経由
                return ShellRating.Read(path);
            }
        }

        // 長辺が maxSize 以下になるよう縮小してデコード。EXIF の回転情報も反映する。
        public static BitmapSource Load(string path, int maxSize)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share))
            {
                int w, h;
                int orientation = 1;
                var dec = BitmapDecoder.Create(fs,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.None);
                var frame = dec.Frames[0];
                w = frame.PixelWidth;
                h = frame.PixelHeight;
                var md = frame.Metadata as BitmapMetadata;
                if (md != null)
                {
                    object o = SafeQuery(md, "System.Photo.Orientation");
                    if (o != null)
                    {
                        try { orientation = Convert.ToInt32(o); } catch { }
                    }
                }

                fs.Seek(0, SeekOrigin.Begin);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = fs;
                if (w >= h)
                {
                    if (w > maxSize) bi.DecodePixelWidth = maxSize;
                }
                else
                {
                    if (h > maxSize) bi.DecodePixelHeight = maxSize;
                }
                bi.EndInit();
                bi.Freeze();

                double angle = 0;
                switch (orientation)
                {
                    case 3: case 4: angle = 180; break;
                    case 5: case 6: angle = 90; break;
                    case 7: case 8: angle = 270; break;
                }
                if (angle == 0) return bi;

                var tb = new TransformedBitmap(bi, new RotateTransform(angle));
                tb.Freeze();
                return tb;
            }
        }

        // 画像の解像度 { 幅, 高さ } をヘッダーだけ読んで取得 (画素はデコードしない)。EXIF の回転を反映した見た目の向き。
        public static int[] ReadSize(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share))
            {
                var dec = BitmapDecoder.Create(fs,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);
                var frame = dec.Frames[0];
                int w = frame.PixelWidth;
                int h = frame.PixelHeight;
                int orientation = 1;
                var md = frame.Metadata as BitmapMetadata;
                if (md != null)
                {
                    object o = SafeQuery(md, "System.Photo.Orientation");
                    if (o != null)
                    {
                        try { orientation = Convert.ToInt32(o); } catch { }
                    }
                }
                if (orientation >= 5 && orientation <= 8) return new[] { h, w };   // 90° / 270° 回転
                return new[] { w, h };
            }
        }

        private static object SafeQuery(BitmapMetadata md, string query)
        {
            try { return md.GetQuery(query); }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------
    // エクスプローラーと同じサムネール (動画用。IShellItemImageFactory)
    // ------------------------------------------------------------------
    public static class ShellThumbnail
    {
        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx, cy;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1;

        public static BitmapSource Load(string path, int size)
        {
            IShellItemImageFactory factory = null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
                var sz = new SIZE();
                sz.cx = size;
                sz.cy = size;
                int hr = factory.GetImage(sz, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out hbmp);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                                                                BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }
    }

    // ------------------------------------------------------------------
    // Windows 標準のフォルダー選択ダイアログ (IFileOpenDialog)
    // ------------------------------------------------------------------
    public static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        private const uint FOS_PICKFOLDERS = 0x20;
        private const uint FOS_FORCEFILESYSTEM = 0x40;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        public static string Pick(Window owner, string initialFolder, string title)
        {
            var dlg = (IFileDialog)new FileOpenDialogRCW();
            try
            {
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                {
                    try
                    {
                        Guid iid = typeof(IShellItem).GUID;
                        IShellItem folder;
                        SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iid, out folder);
                        dlg.SetFolder(folder);
                    }
                    catch { }
                }
                IntPtr hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
                if (dlg.Show(hwnd) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dlg);
            }
        }
    }

    // ------------------------------------------------------------------
    // メインウィンドウ
    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    // シェルのアイコン・表示名 (フォルダツリー用)
    // ------------------------------------------------------------------
    public static class ShellIcons
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
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll")]
        private static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr ppidl);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_DISPLAYNAME = 0x200;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_PIDL = 0x8;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const int CSIDL_DRIVES = 0x11, CSIDL_NETWORK = 0x12;

        private static readonly object sync = new object();
        private static ImageSource folderIcon;

        // 普通のフォルダのアイコン (全フォルダで共用)
        public static ImageSource Folder
        {
            get
            {
                lock (sync)
                {
                    if (folderIcon == null)
                    {
                        var info = new SHFILEINFO();
                        SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf(info),
                                      SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
                        folderIcon = ToSource(info.hIcon);
                    }
                    return folderIcon;
                }
            }
        }

        // 実在するパス (ドライブ、特殊フォルダ) のアイコンと表示名
        public static ImageSource ForPath(string path, out string displayName)
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_DISPLAYNAME);
            displayName = string.IsNullOrEmpty(info.szDisplayName) ? path : info.szDisplayName;
            return ToSource(info.hIcon);
        }

        // 「PC」「ネットワーク」などの仮想フォルダ
        public static ImageSource ForSpecial(int csidl, out string displayName)
        {
            displayName = null;
            IntPtr pidl;
            if (SHGetSpecialFolderLocation(IntPtr.Zero, csidl, out pidl) != 0) return null;
            try
            {
                var info = new SHFILEINFO();
                SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf(info),
                              SHGFI_PIDL | SHGFI_ICON | SHGFI_SMALLICON | SHGFI_DISPLAYNAME);
                displayName = info.szDisplayName;
                return ToSource(info.hIcon);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }

        private static ImageSource ToSource(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DestroyIcon(hIcon); }
        }
    }

    // ------------------------------------------------------------------
    // フォルダツリーの 1 ノード。子は展開されたときに初めて読み込む。
    // ------------------------------------------------------------------
    public enum NodeKind { Computer, Folder, NetworkRoot, Server, Placeholder }

    public class FolderNode : INotifyPropertyChanged
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        private bool isExpanded, isSelected;
        private Task loading;

        public FolderNode(NodeKind kind, string name, string path, ImageSource icon)
        {
            Kind = kind;
            Name = name;
            Path = path;
            Icon = icon;
            Opacity = 1.0;
            Children = new ObservableCollection<FolderNode>();
            if (kind != NodeKind.Placeholder)
                Children.Add(new FolderNode(NodeKind.Placeholder, "読み込み中...", null, null));
        }

        public NodeKind Kind { get; private set; }
        private string name;
        private ImageSource icon;

        public string Name
        {
            get { return name; }
            private set { name = value; OnPropertyChanged("Name"); }
        }
        public string Path { get; private set; }          // 開けるフォルダのパス。PC / ネットワークなどは null
        public ImageSource Icon
        {
            get { return icon; }
            private set { icon = value; OnPropertyChanged("Icon"); }
        }
        public double Opacity { get; set; }               // 隠しフォルダは薄く表示
        public ObservableCollection<FolderNode> Children { get; private set; }

        // このノードをクリックしたときに画像フォルダとして開けるか
        public bool CanOpen { get { return Kind == NodeKind.Folder && Path != null; } }

        public bool IsExpanded
        {
            get { return isExpanded; }
            set
            {
                if (isExpanded == value) return;
                isExpanded = value;
                OnPropertyChanged("IsExpanded");
                if (value) EnsureChildrenAsync();
            }
        }

        public bool IsSelected
        {
            get { return isSelected; }
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                OnPropertyChanged("IsSelected");
            }
        }

        // 子ノードを (まだなら) 読み込む。UI スレッドから呼ぶこと。
        public Task EnsureChildrenAsync()
        {
            if (loading == null) loading = LoadAsync();
            return loading;
        }

        // 次に展開したときに読み直す
        public void Invalidate()
        {
            loading = null;
        }

        private async Task LoadAsync()
        {
            List<FolderNode> list;
            try
            {
                switch (Kind)
                {
                    case NodeKind.Computer:
                        list = await Task.Run(() => ListDrives());
                        break;
                    case NodeKind.NetworkRoot:
                        list = await RunSta(() => ListShell(ShellIcons.CSIDL_NETWORK, NodeKind.Server));
                        break;
                    case NodeKind.Server:
                        {
                            string server = Path;
                            list = await RunSta(() => ListShell(server, NodeKind.Folder));
                        }
                        break;
                    case NodeKind.Folder:
                        {
                            string dir = Path;
                            list = await Task.Run(() => ListFolders(dir));
                        }
                        break;
                    default:
                        list = new List<FolderNode>();
                        break;
                }
            }
            catch
            {
                list = new List<FolderNode>();
            }
            Children.Clear();
            foreach (var n in list) Children.Add(n);
            if (Kind == NodeKind.Computer)
            {
                foreach (var n in list) n.ResolveDriveInfo();
            }
        }

        // ドライブ文字だけで即座に一覧を作る。ディスクには触れないので、休止中の HDD などを待たない。
        private static List<FolderNode> ListDrives()
        {
            var list = new List<FolderNode>();
            foreach (string root in Environment.GetLogicalDrives())
            {
                list.Add(new FolderNode(NodeKind.Folder, "(" + root.TrimEnd('\\') + ")", root, null));
            }
            return list;
        }

        // ドライブの表示名とアイコンを後から取得する (ドライブごとに独立。遅いドライブが他を待たせない)
        private async void ResolveDriveInfo()
        {
            try
            {
                string path = Path;
                var info = await Task.Run(() =>
                {
                    string display;
                    var ic = ShellIcons.ForPath(path, out display);
                    return Tuple.Create(display, ic);
                });
                if (!string.IsNullOrEmpty(info.Item1)) Name = info.Item1;
                if (info.Item2 != null) Icon = info.Item2;
            }
            catch { }
        }

        private static List<FolderNode> ListFolders(string dir)
        {
            var infos = new List<DirectoryInfo>();
            foreach (var di in new DirectoryInfo(dir).EnumerateDirectories())
            {
                try
                {
                    var a = di.Attributes;
                    // 保護されたシステムフォルダ ($Recycle.Bin など) は出さない
                    if ((a & FileAttributes.Hidden) != 0 && (a & FileAttributes.System) != 0) continue;
                    infos.Add(di);
                }
                catch { }
            }
            infos.Sort((x, y) => StrCmpLogicalW(x.Name, y.Name));

            var list = new List<FolderNode>();
            foreach (var di in infos)
            {
                var n = new FolderNode(NodeKind.Folder, di.Name, di.FullName, ShellIcons.Folder);
                if ((di.Attributes & FileAttributes.Hidden) != 0) n.Opacity = 0.5;
                list.Add(n);
            }
            return list;
        }

        // シェルの名前空間を列挙する (ネットワーク上の PC と、その共有フォルダ)
        private static List<FolderNode> ListShell(object ns, NodeKind childKind)
        {
            var list = new List<FolderNode>();
            Type t = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(t);
            dynamic folder = shell.NameSpace(ns);
            if (folder == null) return list;

            string dummy;
            ImageSource serverIcon = childKind == NodeKind.Server ? ShellIcons.ForSpecial(ShellIcons.CSIDL_DRIVES, out dummy) : null;
            foreach (dynamic it in folder.Items())
            {
                string path = (string)it.Path;
                if (!(bool)it.IsFolder || path == null || !path.StartsWith("\\\\")) continue;
                list.Add(new FolderNode(childKind, (string)it.Name, path,
                                        childKind == NodeKind.Server ? serverIcon : ShellIcons.Folder));
            }
            list.Sort((x, y) => StrCmpLogicalW(x.Name, y.Name));
            return list;
        }

        // Shell.Application は STA スレッドが必要
        private static Task<List<FolderNode>> RunSta(Func<List<FolderNode>> work)
        {
            var tcs = new TaskCompletionSource<List<FolderNode>>();
            var th = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            return tcs.Task;
        }

        public static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    public enum FilterMode { Only, AtLeast, AtMost }

    // 並び順 (ツールバーの選択肢と同じ順番)
    public enum SortMode { NameAsc, NameDesc, DateAsc, DateDesc }

    public class ItemComparer : System.Collections.IComparer, IComparer<ImageItem>
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        private readonly SortMode mode;

        public ItemComparer(SortMode mode) { this.mode = mode; }

        public int Compare(object x, object y) { return Compare((ImageItem)x, (ImageItem)y); }

        public int Compare(ImageItem a, ImageItem b)
        {
            int c = 0;
            if (mode == SortMode.DateAsc || mode == SortMode.DateDesc) c = a.Modified.CompareTo(b.Modified);
            if (c == 0) c = StrCmpLogicalW(a.FileName, b.FileName);   // 名前は数字の大小を考慮 (エクスプローラーと同じ)
            return mode == SortMode.NameDesc || mode == SortMode.DateDesc ? -c : c;
        }
    }

    public class MainWindow : Window
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        private const int ThumbSize = 192;
        private const int CacheLimit = 8;
        private const string HelpText = "1〜5: 評価 / 0: 評価解除 / Del: ごみ箱へ削除 / ← →・ホイール: 移動 / Ctrl+ホイール: 拡大縮小 / Space: 動画の再生・一時停止 / Home End: 先頭・末尾 / Ctrl+O: フォルダを開く / Ctrl+G: グリッド一覧";

        private readonly ObservableCollection<ImageItem> items = new ObservableCollection<ImageItem>();
        private readonly ICollectionView view;
        private readonly Dictionary<string, BitmapSource> cache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> cacheOrder = new LinkedList<string>();
        private readonly int mainMaxSize;

        private Image mainImage;
        private ListBox thumbList;
        private TextBlock infoText, countText, statusText, emptyText;
        private Panel starPanel;
        private readonly TextBlock[] starBlocks = new TextBlock[5];
        private static readonly Brush StarBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        private static readonly Brush StarHoverBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xB0));
        private Border ratingBadge, infoBadge;
        private ToggleButton[] filterButtons;
        private ToggleButton modeAtLeast, modeAtMost, modeOnly;
        private CheckBox autoNext;
        private Button copyButton, moveButton;
        private FrameworkElement toolbar, loadPanel;
        private TextBlock loadText;
        private ProgressBar loadBar;
        private bool busy;

        private TreeView folderTree;
        private ColumnDefinition treeColumn;
        private FolderNode computerNode, networkNode;

        // メイン画像の拡大表示
        private const double ZoomStep = 1.25, MaxZoom = 16;
        private readonly ScaleTransform zoomScale = new ScaleTransform(1, 1);
        private readonly TranslateTransform zoomMove = new TranslateTransform();
        private FrameworkElement mainArea;
        private double zoom = 1;
        private bool panning;
        private Point panStart, panOrigin;
        private string fullResPath;    // 元の解像度で読み込み済み (または読み込み中) の画像

        // 動画の再生
        private MediaElement mainVideo;
        private FrameworkElement videoBar;
        private Button playButton, muteButton;
        private Slider seekSlider, volumeSlider;
        private TextBlock timeText;
        private DispatcherTimer videoTimer;
        private string videoPath;      // 再生中 (または開いている途中) の動画。動画を表示していなければ null
        private bool videoOpened, videoPlaying, seekDragging, seekUpdating;
        private bool videoShown;           // 動画の映像を表示済み (それまでは前の絵を仮表示)
        private DateTime revealDeadline;   // 映像が出なくてもこの時刻には仮表示を消す
        private TimeSpan resumePosition;   // 開き直したときに戻る位置
        private bool resumePlaying;
        private bool syncingTree;      // コードからツリーの選択を変えている間は true
        private bool loadingFolder;    // フォルダのファイル一覧・評価を読み込んでいる間は true
        private string folderError;    // フォルダを開けなかったときのメッセージ
        private int treeSyncVersion;

        // グリッド一覧 (別ウインドウ)
        private Window gridWindow;
        private FrameworkElement gridRoot;
        private ListBox gridList;
        private TextBlock gridCountText;
        private TextBlock gridLoadText;
        private ToggleButton[] gridSizeButtons;
        private int gridSize;                                        // 0 = 小, 1 = 中, 2 = 大
        private static readonly int[] GridTiles = { 160, 240, 320 };  // タイルの幅 (小 = ×1, 中 = ×1.5, 大 = ×2)
        private volatile int gridThumbVersion;
        private ContextMenu itemMenu;
        private bool draggingOut;      // 一覧から他のアプリへドラッグ中
        private ContextMenuEventHandler itemMenuGuard;

        private string currentFolder;
        private int filterValue;               // 0 = フィルターなし
        private FilterMode filterMode = FilterMode.Only;
        private SortMode sortMode = SortMode.NameAsc;
        private ComboBox sortBox;
        private bool resorting;                // 並べ替え中は選択の変化を無視する
        private int loadVersion;
        private volatile int folderGeneration;

        public MainWindow(string[] args)
        {
            Title = "J-Rate";
            Width = 1280;
            Height = 900;
            MinWidth = 640;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            AllowDrop = true;
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("J-Rate.ico"))
                {
                    Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
            }
            catch { }

            var root = LoadXaml("MainWindow.xaml");
            Content = root;
            mainImage = (Image)root.FindName("MainImage");
            thumbList = (ListBox)root.FindName("ThumbList");
            starPanel = (Panel)root.FindName("StarPanel");
            for (int i = 0; i < 5; i++)
            {
                int n = i + 1;
                var star = new TextBlock();
                star.FontSize = 32;
                star.Foreground = StarBrush;
                star.Cursor = Cursors.Hand;
                star.Padding = new Thickness(1, 0, 1, 0);
                star.MouseEnter += delegate { DrawStars(n, true); };
                star.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    OnStarClick(n);
                };
                starBlocks[i] = star;
                starPanel.Children.Add(star);
            }
            starPanel.MouseLeave += delegate { DrawStars(Current != null ? Current.Rating : 0, false); };
            infoText = (TextBlock)root.FindName("InfoText");
            countText = (TextBlock)root.FindName("CountText");
            statusText = (TextBlock)root.FindName("StatusText");
            emptyText = (TextBlock)root.FindName("EmptyText");
            ratingBadge = (Border)root.FindName("RatingBadge");
            infoBadge = (Border)root.FindName("InfoBadge");
            autoNext = (CheckBox)root.FindName("AutoNext");
            modeAtLeast = (ToggleButton)root.FindName("ModeAtLeast");
            modeAtMost = (ToggleButton)root.FindName("ModeAtMost");
            modeOnly = (ToggleButton)root.FindName("ModeOnly");
            filterButtons = new ToggleButton[5];
            for (int i = 0; i < 5; i++)
            {
                int n = i + 1;
                filterButtons[i] = (ToggleButton)root.FindName("F" + n);
                filterButtons[i].Click += delegate { OnFilterNumber(n); };
            }
            modeAtLeast.Click += delegate { SetMode(FilterMode.AtLeast); };
            modeAtMost.Click += delegate { SetMode(FilterMode.AtMost); };
            modeOnly.Click += delegate { SetMode(FilterMode.Only); };
            ((Button)root.FindName("FilterAll")).Click += delegate { filterValue = 0; ApplyFilter(); };
            ((Button)root.FindName("OpenButton")).Click += delegate { BrowseFolder(); };
            ((Button)root.FindName("GridButton")).Click += delegate { ShowGrid(); };
            copyButton = (Button)root.FindName("CopyButton");
            moveButton = (Button)root.FindName("MoveButton");
            copyButton.Click += delegate { Transfer(false); };
            moveButton.Click += delegate { Transfer(true); };
            toolbar = (FrameworkElement)root.FindName("Toolbar");
            loadPanel = (FrameworkElement)root.FindName("LoadPanel");
            loadText = (TextBlock)root.FindName("LoadText");
            loadBar = (ProgressBar)root.FindName("LoadBar");

            view = CollectionViewSource.GetDefaultView(items);
            view.Filter = o => Matches((ImageItem)o);
            InitSort((ComboBox)root.FindName("SortBox"));
            thumbList.ItemsSource = view;
            thumbList.SelectionChanged += delegate { OnSelectionChanged(); };
            thumbList.PreviewMouseWheel += OnThumbWheel;
            EnableFileDrag(thumbList);
            mainArea = (FrameworkElement)root.FindName("MainArea");
            mainArea.MouseWheel += OnMainWheel;
            InitVideo(root);
            InitZoom();

            PreviewKeyDown += OnKey;
            Drop += OnDrop;

            folderTree = (TreeView)root.FindName("FolderTree");
            treeColumn = (ColumnDefinition)root.FindName("TreeColumn");
            InitFolderTree();
            InitContextMenu((FrameworkElement)root.FindName("MainArea"));

            int screenMax = 0;
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
                screenMax = Math.Max(screenMax, Math.Max(s.Bounds.Width, s.Bounds.Height));
            mainMaxSize = Math.Max(1920, screenMax);

            statusText.Text = HelpText;
            UpdateFilterButtons();
            UpdateInfo();

            // GPU で描画すると、4K モニター (150%) 側にウィンドウがあるときだけ動画の 1 周目の映像が更新されない
            // (時間は進む。原因は特定できず、DPI スケーリングを切っても再現)。ソフトウェア描画なら起きない。
            SourceInitialized += delegate
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src != null) src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            };

            Loaded += delegate { OpenInitial(args); };
            Closed += delegate { if (gridWindow != null) gridWindow.Close(); };
        }

        public static FrameworkElement LoadXaml(string resourceName)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(resourceName))
            {
                return (FrameworkElement)XamlReader.Load(s);
            }
        }

        // ---------------- フォルダツリー ----------------

        private void InitFolderTree()
        {
            var roots = new ObservableCollection<FolderNode>();

            // よく使う場所
            foreach (var sf in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyPictures })
            {
                try
                {
                    string path = Environment.GetFolderPath(sf);
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                    string name;
                    var icon = ShellIcons.ForPath(path, out name);
                    roots.Add(new FolderNode(NodeKind.Folder, name, path, icon));
                }
                catch { }
            }

            string pcName, netName;
            var pcIcon = ShellIcons.ForSpecial(ShellIcons.CSIDL_DRIVES, out pcName);
            var netIcon = ShellIcons.ForSpecial(ShellIcons.CSIDL_NETWORK, out netName);
            computerNode = new FolderNode(NodeKind.Computer, pcName ?? "PC", null, pcIcon);
            roots.Add(computerNode);
            networkNode = new FolderNode(NodeKind.NetworkRoot, netName ?? "ネットワーク", null, netIcon);
            roots.Add(networkNode);

            folderTree.ItemsSource = roots;
            computerNode.IsExpanded = true;
            InitTreeMenu();

            folderTree.SelectedItemChanged += delegate(object s, RoutedPropertyChangedEventArgs<object> e)
            {
                var node = e.NewValue as FolderNode;
                if (syncingTree || busy || node == null || !node.CanOpen) return;
                if (FolderNode.SamePath(node.Path, currentFolder)) return;
                OpenFolder(node.Path, null);
            };
            // 選択されたノードが見える位置までスクロール
            folderTree.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler(delegate(object s, RoutedEventArgs e)
            {
                var tvi = e.OriginalSource as TreeViewItem;
                if (tvi != null)
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => tvi.BringIntoView()));
            }));

            double w;
            string saved = LoadSetting("treewidth.txt");
            if (saved != null && double.TryParse(saved, System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out w) && w >= 0 && w < 2000)
                treeColumn.Width = new GridLength(w);
            Closing += delegate
            {
                SaveSetting("treewidth.txt", treeColumn.ActualWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            };
        }

        // 指定フォルダまでツリーを展開して選択する。
        // reveal = false : フォルダを開いたときの自動追従 (ネットワークパスは時間がかかるので追わない)
        // reveal = true  : 右クリックの「フォルダを表示」。ネットワークも探し、見える位置までスクロールする
        private async void SyncTreeTo(string folder, bool reveal)
        {
            int ver = ++treeSyncVersion;
            try
            {
                string full = System.IO.Path.GetFullPath(folder);
                bool unc = full.StartsWith("\\\\");
                if (!reveal)
                {
                    var selected = folderTree.SelectedItem as FolderNode;
                    if (selected != null && FolderNode.SamePath(selected.Path, full)) return;
                    if (unc) return;
                }

                var chain = new List<FolderNode>();   // ルートから目的のフォルダまでのノード
                string rootPath = System.IO.Path.GetPathRoot(full);
                FolderNode node;
                if (unc)
                {
                    statusText.Text = "ネットワークを検索中...";
                    node = networkNode;
                    chain.Add(node);
                    await node.EnsureChildrenAsync();
                    if (ver != treeSyncVersion) return;
                    node.IsExpanded = true;
                    int slash = rootPath.IndexOf('\\', 2);
                    string server = slash > 0 ? rootPath.Substring(0, slash) : rootPath;
                    node = node.Children.FirstOrDefault(c => FolderNode.SamePath(c.Path, server));
                    if (node != null)
                    {
                        chain.Add(node);
                        await node.EnsureChildrenAsync();
                        if (ver != treeSyncVersion) return;
                        node.IsExpanded = true;
                        node = node.Children.FirstOrDefault(c => FolderNode.SamePath(c.Path, rootPath));
                    }
                }
                else
                {
                    node = computerNode;
                    chain.Add(node);
                    await node.EnsureChildrenAsync();
                    if (ver != treeSyncVersion) return;
                    node.IsExpanded = true;
                    node = node.Children.FirstOrDefault(c => FolderNode.SamePath(c.Path, rootPath));
                }

                bool found = node != null;
                if (found)
                {
                    chain.Add(node);
                    string rest = full.Substring(rootPath.Length);
                    foreach (string seg in rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        await node.EnsureChildrenAsync();
                        if (ver != treeSyncVersion) return;
                        string name = seg;
                        var next = node.Children.FirstOrDefault(c => c.Path != null &&
                            string.Equals(System.IO.Path.GetFileName(c.Path.TrimEnd('\\')), name, StringComparison.OrdinalIgnoreCase));
                        if (next == null) { found = false; break; }   // ツリーに出していないフォルダ (システムフォルダなど)
                        node.IsExpanded = true;
                        node = next;
                        chain.Add(node);
                    }
                }

                if (found)
                {
                    syncingTree = true;
                    try { node.IsSelected = true; }
                    finally { syncingTree = false; }
                }
                if (reveal)
                {
                    statusText.Text = found ? "フォルダを表示しました: " + full
                                            : "ツリーの中にこのフォルダが見つかりません: " + full;
                    if (treeColumn.ActualWidth < 120) treeColumn.Width = new GridLength(250);   // ツリーを隠していたら出す
                    var path = chain;
                    var op = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ScrollTreeTo(path)));
                }
            }
            catch { }
        }

        // ノードの並び (ルート→目的) をたどって、最後のノードが見える位置までスクロールする
        private void ScrollTreeTo(List<FolderNode> chain)
        {
            folderTree.UpdateLayout();
            ItemsControl parent = folderTree;
            TreeViewItem last = null;
            foreach (var n in chain)
            {
                var c = parent.ItemContainerGenerator.ContainerFromItem(n) as TreeViewItem;
                if (c == null) break;
                c.UpdateLayout();
                last = c;
                parent = c;
            }
            if (last == null) return;
            // 子が展開されていると項目全体が対象になってしまうので、見出し行だけを対象にする
            var header = last.Template != null ? last.Template.FindName("Bd", last) as FrameworkElement : null;
            if (header != null) header.BringIntoView(); else last.BringIntoView();
        }

        // ---------------- 右クリックメニュー ----------------

        private void InitContextMenu(FrameworkElement mainArea)
        {
            var menu = new ContextMenu();

            var show = new MenuItem { Header = "フォルダを表示" };
            show.Click += delegate
            {
                var it = Current;
                if (it != null) SyncTreeTo(System.IO.Path.GetDirectoryName(it.Path), true);
            };
            menu.Items.Add(show);
            var explorer = new MenuItem { Header = "エクスプローラーで開く" };
            explorer.Click += delegate
            {
                var it = Current;
                if (it != null) OpenInExplorer(it.Path, true);
            };
            menu.Items.Add(explorer);
            menu.Items.Add(new Separator());

            // ComfyUI で動画を生成 (画像のときだけ)
            var makeVideo = new MenuItem { Header = "動画にする (ComfyUI)" };
            makeVideo.Click += delegate
            {
                var it = Current;
                if (it != null && !it.IsVideo) MakeVideo(it);
            };
            menu.Items.Add(makeVideo);
            var comfySettings = new MenuItem { Header = "動画生成の設定..." };
            comfySettings.Click += delegate { ShowComfySettings(); };
            menu.Items.Add(comfySettings);
            menu.Items.Add(new Separator());

            var ratingItems = new MenuItem[6];
            for (int i = 1; i <= 5; i++)
            {
                int n = i;
                var mi = new MenuItem { Header = ImageItem.StarText(n), InputGestureText = n.ToString() };
                mi.Click += delegate { if (!busy) SetRating(n); };
                ratingItems[n] = mi;
                menu.Items.Add(mi);
            }
            var clear = new MenuItem { Header = "評価を解除", InputGestureText = "0" };
            clear.Click += delegate { if (!busy) SetRating(0); };
            menu.Items.Add(clear);
            menu.Items.Add(new Separator());

            var del = new MenuItem { Header = "ファイルを削除 (ごみ箱へ)", InputGestureText = "Del" };
            del.Click += delegate { if (!busy) DeleteCurrent(); };
            menu.Items.Add(del);

            // 今の評価にチェックを付ける
            menu.Opened += delegate
            {
                var it = Current;
                for (int i = 1; i <= 5; i++) ratingItems[i].IsChecked = it != null && it.Rating == i;
                clear.IsEnabled = it != null && it.Rating > 0;
                makeVideo.IsEnabled = it != null && !it.IsVideo;
            };

            // 画像がないとき・処理中はメニューを出さない
            ContextMenuEventHandler guard = delegate(object s, ContextMenuEventArgs e)
            {
                if (busy || Current == null) e.Handled = true;
            };
            itemMenu = menu;   // グリッド一覧でも同じメニューを使う
            itemMenuGuard = guard;
            mainArea.ContextMenu = menu;
            mainArea.ContextMenuOpening += guard;
            thumbList.ContextMenu = menu;
            thumbList.ContextMenuOpening += guard;
        }

        // ---------------- 動画にする (ComfyUI) ----------------

        private int videoJobs;   // 生成中の件数

        private async void MakeVideo(ImageItem item)
        {
            string workflow = ComfySettings.WorkflowPath;
            if (string.IsNullOrEmpty(workflow) || !File.Exists(workflow))
            {
                // 初回はワークフローを指定してもらう
                if (!ShowComfySettings()) return;
                workflow = ComfySettings.WorkflowPath;
                if (string.IsNullOrEmpty(workflow) || !File.Exists(workflow)) return;
            }
            string url = ComfySettings.Url;
            string src = item.Path;
            string name = item.FileName;

            // プロンプトと秒数を聞く
            string prompt = null;
            double? seconds = null;
            try
            {
                var wf = ComfyVideo.LoadWorkflow(ComfyVideo.NewSerializer(), workflow);
                var dlg = new ComfyPromptDialog(name, ComfyVideo.HasPrompt(wf), ComfyVideo.GetDuration(wf),
                                                ComfyVideo.DurationIsInteger(wf));
                dlg.Owner = gridWindow != null && gridWindow.IsActive ? (Window)gridWindow : this;
                if (dlg.ShowDialog() != true) return;
                prompt = dlg.Prompt;
                seconds = dlg.Seconds;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "動画にする - J-Rate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            videoJobs++;
            bool done = false;
            var dispatcher = Dispatcher;
            Action<string> report = msg => dispatcher.BeginInvoke(new Action(() =>
            {
                if (done) return;   // 完了後に届いた古い進捗は無視
                statusText.Text = "動画生成 " + name + " : " + msg +
                                  (videoJobs > 1 ? "  (処理中 " + videoJobs + " 件)" : "");
            }));
            report("準備中...");

            string result = null;
            Exception error = null;
            try
            {
                result = await Task.Run(() => ComfyVideo.Generate(src, url, workflow, prompt, seconds, report));
            }
            catch (Exception ex) { error = ex; }
            done = true;
            videoJobs--;
            string rest = videoJobs > 0 ? "  (処理中 " + videoJobs + " 件)" : "";

            if (error != null)
            {
                statusText.Text = "動画を作れませんでした: " + name + rest;
                string text = error is System.Net.WebException
                    ? "ComfyUI に接続できません (" + url + ")。\nComfyUI が起動しているか確認してください。\n\n" + error.Message
                    : error.Message;
                MessageBox.Show(this, name + "\n\n" + text, "動画にする - J-Rate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            statusText.Text = "動画を作成しました: " + System.IO.Path.GetFileName(result) + rest;
            AddGeneratedFile(result);
        }

        // 出来た動画が今開いているフォルダのものなら一覧に加える
        private void AddGeneratedFile(string path)
        {
            if (currentFolder == null || !FolderNode.SamePath(System.IO.Path.GetDirectoryName(path), currentFolder)) return;
            if (!IsSupportedImage(path) || !File.Exists(path)) return;
            if (items.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return;

            var it = new ImageItem(path, File.GetLastWriteTimeUtc(path));
            items.Add(it);   // 並べ替え (CustomSort) とフィルターは view が反映する
            Task.Run(() =>
            {
                try { return ShellThumbnail.Load(path, ThumbSize); }
                catch { return null; }
            }).ContinueWith(t =>
            {
                if (t.Result != null) it.Thumbnail = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private bool ShowComfySettings()
        {
            var dlg = new ComfySettingsDialog();
            dlg.Owner = this;
            return dlg.ShowDialog() == true;
        }

        // ---------------- フォルダ読み込み ----------------
        private void OpenInitial(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                OpenPath(args[0]);
                return;
            }
            string last = LoadLastFolder();
            if (last != null && Directory.Exists(last)) OpenFolder(last, null);
        }

        private void OpenPath(string path)
        {
            if (Directory.Exists(path))
                OpenFolder(path, null);
            else if (File.Exists(path))
                OpenFolder(System.IO.Path.GetDirectoryName(path), path);
        }

        private void BrowseFolder()
        {
            string folder = FolderPicker.Pick(this, currentFolder, "画像のあるフォルダを選択");
            if (folder != null) OpenFolder(folder, null);
        }

        private static bool IsSupportedImage(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".jpe" || ext == ".png" || ImageItem.IsVideoFile(path);
        }

        private async void OpenFolder(string folder, string selectFile)
        {
            int gen = ++folderGeneration;

            // まず画面を「読み込み中」に切り替える
            currentFolder = folder;
            loadingFolder = true;
            folderError = null;
            SyncTreeTo(folder, false);
            Title = "J-Rate - " + folder;
            ClearCache();
            loadVersion++;
            StopVideo();
            mainImage.Source = null;
            gridThumbVersion++;   // グリッド用サムネールの作成を止める
            items.Clear();
            loadPanel.Visibility = Visibility.Collapsed;
            UpdateInfo();

            // ファイル一覧の取得 (遅いドライブやネットワークでも画面が固まらないよう別スレッドで)
            List<ImageItem> found;
            var comparer = new ItemComparer(sortMode);
            try
            {
                found = await Task.Run(() =>
                {
                    var list = new DirectoryInfo(folder).EnumerateFiles()
                        .Where(fi => IsSupportedImage(fi.Name))
                        .Select(fi => new ImageItem(fi.FullName, fi.LastWriteTimeUtc))
                        .ToList();
                    list.Sort(comparer);
                    return list;
                });
            }
            catch (Exception ex)
            {
                if (gen != folderGeneration) return;
                loadingFolder = false;
                folderError = "フォルダを開けません\n" + ex.Message;
                UpdateInfo();
                return;
            }
            if (gen != folderGeneration) return;
            SaveLastFolder(folder);

            foreach (var it in found) items.Add(it);
            var snapshot = found;   // 表示順。評価・サムネールもこの順に読み込む
            // 評価の読み込みを待たずに 1 枚目を表示 (フィルター中は評価が分かるまで「読み込み中」のまま)
            if (thumbList.Items.Count > 0 && selectFile == null) thumbList.SelectedIndex = 0;

            // 評価の読み込み (メタデータのみなので速い)
            var dispatcher = Dispatcher;
            var paths = found.Select(it => it.Path).ToArray();
            int total = paths.Length;
            ShowLoadProgress(gen, "評価を読み込み中", 0, total, false);
            int[] ratings = await Task.Run(() =>
            {
                var result = new int[paths.Length];
                for (int i = 0; i < paths.Length; i++)
                {
                    if (gen != folderGeneration) break;
                    result[i] = ImageLoader.ReadRating(paths[i]);
                    if ((i + 1) % 20 == 0)
                    {
                        int n = i + 1;
                        dispatcher.BeginInvoke(new Action(() => ShowLoadProgress(gen, "評価を読み込み中", n, total, false)));
                    }
                }
                return result;
            });
            if (gen != folderGeneration) return;
            for (int i = 0; i < snapshot.Count; i++) snapshot[i].Rating = ratings[i];

            loadingFolder = false;
            ApplyFilter();
            if (selectFile != null)
            {
                var target = snapshot.FirstOrDefault(it => string.Equals(it.Path, selectFile, StringComparison.OrdinalIgnoreCase));
                if (target != null && Matches(target)) thumbList.SelectedItem = target;
            }
            if (thumbList.SelectedIndex < 0 && thumbList.Items.Count > 0) thumbList.SelectedIndex = 0;
            UpdateInfo();
            LoadGridThumbs();   // グリッド一覧が中・大で開いていれば、その大きさのサムネールも作る

            // サムネールを順次生成
            ShowLoadProgress(gen, "サムネール作成中", 0, total, true);
            await Task.Run(() =>
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (gen != folderGeneration) return;
                    BitmapSource thumb = null;
                    try
                    {
                        thumb = snapshot[i].IsVideo ? ShellThumbnail.Load(snapshot[i].Path, ThumbSize)
                                                    : ImageLoader.Load(snapshot[i].Path, ThumbSize);
                    }
                    catch { }
                    var target = snapshot[i];
                    int n = i + 1;
                    dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (thumb != null) target.Thumbnail = thumb;
                        ShowLoadProgress(gen, "サムネール作成中", n, total, true);
                    }));
                }
            });
        }

        // ステータスバー右側の読み込みインジケータ。終わったら自動で消える。
        private void ShowLoadProgress(int gen, string label, int done, int total, bool lastPhase)
        {
            if (gen != folderGeneration) return;   // 別のフォルダを開き直した後の古い通知は無視
            bool finished = lastPhase && done >= total;
            loadPanel.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
            loadBar.Maximum = total;
            loadBar.Value = done;
            loadText.Text = label + " " + done + " / " + total;
        }

        // ---------------- 表示 ----------------

        private ImageItem Current
        {
            get { return thumbList.SelectedItem as ImageItem; }
        }

        private void OnSelectionChanged()
        {
            if (resorting) return;
            var item = Current;
            if (item != null) thumbList.ScrollIntoView(item);
            SyncGridSelection();
            ShowCurrent();
        }

        private async void ShowCurrent()
        {
            var item = Current;
            // 次の動画が動き出すまでは今の表示 (前の動画の最後のフレーム / 前の画像) を残す。
            // 低解像度のサムネールを挟むと、切り替え時に一瞬ぼやけた別の絵が出て瞬きのように見える。
            ImageSource keep = null;
            if (item != null && item.IsVideo && zoom <= 1)
                keep = videoPath != null && videoShown ? SnapshotVideo() : mainImage.Source;
            ResetZoom();
            UpdateInfo();
            int ver = ++loadVersion;
            StopVideo();
            if (item == null)
            {
                mainImage.Source = null;
                return;
            }
            if (item.IsVideo)
            {
                mainImage.Source = keep ?? item.Thumbnail;   // 何も表示していなかったときだけサムネール
                OpenVideo(item);
                Prefetch(ver);
                return;
            }

            LoadSize(item);
            BitmapSource bmp;
            if (!cache.TryGetValue(item.Path, out bmp))
            {
                mainImage.Source = item.Thumbnail;   // 読み込み中は仮にサムネールを表示
                try
                {
                    string p = item.Path;
                    int max = mainMaxSize;
                    bmp = await Task.Run(() => ImageLoader.Load(p, max));
                }
                catch (Exception ex)
                {
                    if (ver == loadVersion) statusText.Text = "読み込み失敗: " + item.FileName + " - " + ex.Message;
                    return;
                }
                CachePut(item.Path, bmp);
                if (ver != loadVersion) return;
            }
            // 読み込みを待つ間に拡大されて、元解像度の画像に差し替え済みならそのままにする
            if (fullResPath != item.Path) mainImage.Source = bmp;
            Prefetch(ver);
        }

        // 画像の解像度を調べて情報表示に出す (動画は開いたときに MediaOpened で分かる)
        private async void LoadSize(ImageItem item)
        {
            if (item.PixelWidth > 0) return;
            string p = item.Path;
            int[] size;
            try
            {
                size = await Task.Run(() => ImageLoader.ReadSize(p));
            }
            catch
            {
                return;
            }
            item.PixelWidth = size[0];
            item.PixelHeight = size[1];
            if (Current == item) UpdateInfo();
        }

        // 前後の画像を先読み
        private async void Prefetch(int ver)
        {
            int idx = thumbList.SelectedIndex;
            foreach (int offset in new[] { 1, -1, 2 })
            {
                if (ver != loadVersion) return;
                int i = idx + offset;
                if (i < 0 || i >= thumbList.Items.Count) continue;
                var it = (ImageItem)thumbList.Items[i];
                if (it.IsVideo || cache.ContainsKey(it.Path)) continue;
                string p = it.Path;
                int max = mainMaxSize;
                try
                {
                    var bmp = await Task.Run(() => ImageLoader.Load(p, max));
                    CachePut(p, bmp);
                }
                catch { }
            }
        }

        private void CachePut(string path, BitmapSource bmp)
        {
            if (cache.ContainsKey(path))
            {
                cacheOrder.Remove(path);
            }
            cache[path] = bmp;
            cacheOrder.AddLast(path);
            while (cacheOrder.Count > CacheLimit)
            {
                cache.Remove(cacheOrder.First.Value);
                cacheOrder.RemoveFirst();
            }
        }

        private void ClearCache()
        {
            cache.Clear();
            cacheOrder.Clear();
        }

        private void UpdateInfo()
        {
            var item = Current;
            // 画像が 1 枚も表示されていないときの案内。「ありません」は読み込みが終わってから出す。
            emptyText.Visibility = thumbList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (thumbList.Items.Count == 0)
            {
                if (loadingFolder) emptyText.Text = "読み込み中...";
                else if (folderError != null) emptyText.Text = folderError;
                else if (items.Count > 0) emptyText.Text = "フィルター条件に一致する画像がありません";
                else if (currentFolder == null) emptyText.Text = "「フォルダを開く」(Ctrl+O) か、ここにフォルダ / 画像をドロップしてください";
                else emptyText.Text = "このフォルダには JPG / PNG 画像・MP4 動画がありません";
            }

            if (item == null)
            {
                ratingBadge.Visibility = Visibility.Collapsed;
                infoBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                ratingBadge.Visibility = Visibility.Visible;
                infoBadge.Visibility = Visibility.Visible;
                DrawStars(item.Rating, false);
                infoText.Text = (thumbList.SelectedIndex + 1) + " / " + thumbList.Items.Count + "    " + item.FileName
                    + (item.PixelWidth > 0 ? "    " + item.PixelWidth + " × " + item.PixelHeight : "")
                    + (zoom > 1 ? "    拡大 ×" + zoom.ToString("0.0") : "");
            }

            string filter = "フィルター: なし";
            if (filterValue > 0)
            {
                string m = filterMode == FilterMode.Only ? "のみ" : filterMode == FilterMode.AtLeast ? "以上" : "以下";
                filter = "フィルター: ★" + filterValue + " " + m;
            }
            countText.Text = filter + "    表示 " + thumbList.Items.Count + " / 全 " + items.Count + " 枚";

            // コピー / 移動は ★ フィルターが有効で、表示中の画像があるときだけ
            bool canTransfer = filterValue > 0 && thumbList.Items.Count > 0;
            copyButton.IsEnabled = canTransfer;
            moveButton.IsEnabled = canTransfer;
            UpdateGridInfo();
        }

        // ---------------- フィルター ----------------

        private bool Matches(ImageItem it)
        {
            if (filterValue == 0) return true;
            switch (filterMode)
            {
                case FilterMode.AtLeast: return it.Rating >= filterValue;
                case FilterMode.AtMost: return it.Rating <= filterValue;   // 未評価 (0) も含む
                default: return it.Rating == filterValue;
            }
        }

        private void OnFilterNumber(int n)
        {
            // 選択中の数字をもう一度押すと解除
            filterValue = filterValue == n ? 0 : n;
            ApplyFilter();
        }

        private void SetMode(FilterMode mode)
        {
            filterMode = mode;
            ApplyFilter();
        }

        private void UpdateFilterButtons()
        {
            for (int i = 0; i < 5; i++) filterButtons[i].IsChecked = filterValue == i + 1;
            modeOnly.IsChecked = filterMode == FilterMode.Only;
            modeAtLeast.IsChecked = filterMode == FilterMode.AtLeast;
            modeAtMost.IsChecked = filterMode == FilterMode.AtMost;
        }

        // フィルターを再適用。表示中の画像が条件から外れたら、同じ位置 (=次) の画像を選ぶ。
        private void ApplyFilter()
        {
            UpdateFilterButtons();
            var cur = Current;
            int idx = thumbList.SelectedIndex;
            view.Refresh();

            if (cur != null && Matches(cur))
            {
                thumbList.SelectedItem = cur;
            }
            else if (thumbList.Items.Count > 0)
            {
                thumbList.SelectedIndex = Math.Max(0, Math.Min(idx, thumbList.Items.Count - 1));
            }
            else
            {
                thumbList.SelectedIndex = -1;
            }
            if (Current != cur) return;   // 選択が変われば SelectionChanged 側で更新される
            UpdateInfo();
        }

        // ---------------- 並び順 ----------------

        private void InitSort(ComboBox box)
        {
            sortBox = box;
            int saved;
            if (int.TryParse(LoadSetting("sort.txt"), out saved) && saved >= 0 && saved <= (int)SortMode.DateDesc)
                sortMode = (SortMode)saved;
            ((ListCollectionView)view).CustomSort = new ItemComparer(sortMode);
            sortBox.SelectedIndex = (int)sortMode;
            sortBox.SelectionChanged += delegate
            {
                if (sortBox.SelectedIndex < 0 || (SortMode)sortBox.SelectedIndex == sortMode) return;
                ChangeSort((SortMode)sortBox.SelectedIndex);
            };
        }

        // 並び順を変える。表示中の画像・動画はそのまま (動画も再生し直さない)。
        private void ChangeSort(SortMode mode)
        {
            sortMode = mode;
            SaveSetting("sort.txt", ((int)mode).ToString());
            var cur = Current;
            resorting = true;
            try
            {
                ((ListCollectionView)view).CustomSort = new ItemComparer(mode);
                if (cur != null) thumbList.SelectedItem = cur;
            }
            finally { resorting = false; }
            if (Current != cur) ShowCurrent();   // 念のため (通常は同じ項目が選ばれたまま)
            if (Current != null) thumbList.ScrollIntoView(Current);
            SyncGridSelection();
            UpdateInfo();
            Prefetch(loadVersion);   // 前後の項目が変わったので先読みし直す
        }

        // ---------------- 評価 ----------------

        // 右上の星を描く。preview = true はマウスを乗せたときの仮表示。
        private void DrawStars(int count, bool preview)
        {
            for (int i = 0; i < 5; i++)
            {
                starBlocks[i].Text = i < count ? "★" : "☆";
                starBlocks[i].Foreground = preview && i < count ? StarHoverBrush : StarBrush;
            }
        }

        // 星をクリックして評価。今の評価と同じ星をクリックすると解除。
        private void OnStarClick(int n)
        {
            var item = Current;
            if (busy || item == null) return;
            SetRating(item.Rating == n ? 0 : n);
            // フィルターから外れて別の画像に切り替わった場合も含め、表示を実際の評価に合わせる
            DrawStars(Current != null ? Current.Rating : 0, false);
        }

        private void SetRating(int stars)
        {
            var item = Current;
            if (item == null) return;

            // 再生中の動画はファイルがロックされて書き込めないので、いったん閉じて書き込み後に開き直す
            bool reopen = item.IsVideo && videoPath == item.Path;
            TimeSpan resumeAt = TimeSpan.Zero;
            bool resumePlay = false;
            if (reopen)
            {
                resumeAt = videoOpened ? mainVideo.Position : TimeSpan.Zero;
                resumePlay = videoPlaying;
                mainImage.Source = videoOpened && zoom <= 1 ? SnapshotVideo() ?? item.Thumbnail : item.Thumbnail;
                StopVideo();
            }

            try
            {
                // 評価を書き込むとファイルの更新日時が今の時刻になるので、元に戻す (更新日時順の並びが崩れないように)
                DateTime modified = File.GetLastWriteTimeUtc(item.Path);
                ShellRating.Write(item.Path, stars);
                try { File.SetLastWriteTimeUtc(item.Path, modified); }
                catch { }
            }
            catch (Exception ex)
            {
                statusText.Text = "評価を書き込めません: " + item.FileName + " - " + ex.Message;
                if (reopen) OpenVideo(item, resumeAt, resumePlay);
                return;
            }
            item.Rating = stars;
            statusText.Text = item.FileName + " を " + (stars == 0 ? "未評価" : "★" + stars) + " にしました";

            if (!Matches(item))
            {
                ApplyFilter();   // 条件から外れた → 一覧から消えて次の画像へ
            }
            else
            {
                UpdateInfo();
                if (autoNext.IsChecked == true) Move(1);
            }
            // 別の画像・動画に移らなかったら続きから再生
            if (reopen && Current == item && videoPath == null) OpenVideo(item, resumeAt, resumePlay);
        }

        // ---------------- 操作 ----------------

        // 表示中のファイルをごみ箱へ送る (元に戻せる)
        private void DeleteCurrent()
        {
            var item = Current;
            if (item == null) return;
            int idx = thumbList.SelectedIndex;
            if (item.IsVideo) StopVideo();   // 再生中のファイルを手放す

            bool aborted;
            int rc = RecycleBin.Send(new WindowInteropHelper(this).Handle, item.Path, out aborted);
            if (aborted || File.Exists(item.Path))
            {
                if (item.IsVideo) ShowCurrent();
                statusText.Text = rc == 0 || aborted
                    ? "削除を取り消しました: " + item.FileName
                    : "削除できません: " + item.FileName + " (エラー " + rc + ")";
                return;
            }

            if (cache.Remove(item.Path)) cacheOrder.Remove(item.Path);
            items.Remove(item);
            if (thumbList.Items.Count > 0)
                thumbList.SelectedIndex = Math.Max(0, Math.Min(idx, thumbList.Items.Count - 1));
            ShowCurrent();
            statusText.Text = item.FileName + " をごみ箱に移動しました";
        }

        private void Move(int delta)
        {
            int count = thumbList.Items.Count;
            if (count == 0) return;
            int idx = thumbList.SelectedIndex < 0 ? 0 : thumbList.SelectedIndex + delta;
            thumbList.SelectedIndex = Math.Max(0, Math.Min(idx, count - 1));
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (busy)
            {
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
            {
                BrowseFolder();
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.G)
            {
                ShowGrid();
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers != ModifierKeys.None) return;

            int stars = -1;
            if (e.Key >= Key.D0 && e.Key <= Key.D5) stars = e.Key - Key.D0;
            else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad5) stars = e.Key - Key.NumPad0;
            if (stars >= 0)
            {
                SetRating(stars);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Delete)
            {
                // 押しっぱなしで連続削除されないよう、キーリピートは無視
                if (!e.IsRepeat) DeleteCurrent();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    // 動画は再生 / 一時停止、画像は次へ
                    if (videoPath != null) TogglePlay(); else Move(1);
                    e.Handled = true; break;
                case Key.Right:
                case Key.Down:
                case Key.PageDown:
                    Move(1); e.Handled = true; break;
                case Key.Left:
                case Key.Up:
                case Key.Back:
                case Key.PageUp:
                    Move(-1); e.Handled = true; break;
                case Key.Home:
                    if (thumbList.Items.Count > 0) thumbList.SelectedIndex = 0;
                    e.Handled = true; break;
                case Key.End:
                    if (thumbList.Items.Count > 0) thumbList.SelectedIndex = thumbList.Items.Count - 1;
                    e.Handled = true; break;
            }
        }

        private void OnMainWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+ホイール: マウスの位置を中心に拡大 (上) / 縮小 (下)
                bool shown = videoPath != null ? videoOpened : mainImage.Source != null;
                if (Current != null && shown)
                    ZoomAt(e.GetPosition(ZoomTarget), e.Delta > 0 ? ZoomStep : 1 / ZoomStep);
                return;
            }
            Move(e.Delta > 0 ? -1 : 1);
        }

        // ---------------- 拡大・縮小 ----------------

        private void InitZoom()
        {
            var tg = new TransformGroup();
            tg.Children.Add(zoomScale);
            tg.Children.Add(zoomMove);
            mainImage.RenderTransform = tg;
            mainVideo.RenderTransform = tg;

            mainArea.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (zoom <= 1)
                {
                    // 動画はクリックで再生 / 一時停止
                    if (videoPath != null && e.ClickCount == 1) TogglePlay();
                    return;
                }
                if (e.ClickCount == 2)
                {
                    // ダブルクリックで全体表示に戻す
                    panning = false;
                    mainArea.ReleaseMouseCapture();
                    SetZoom(1, new Point(0, 0));
                    return;
                }
                panning = true;
                panStart = e.GetPosition(mainArea);
                panOrigin = new Point(zoomMove.X, zoomMove.Y);
                mainArea.CaptureMouse();
            };
            mainArea.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!panning) return;
                Point p = e.GetPosition(mainArea);
                zoomMove.X = panOrigin.X + p.X - panStart.X;
                zoomMove.Y = panOrigin.Y + p.Y - panStart.Y;
                ClampPan();
            };
            MouseButtonEventHandler endPan = delegate
            {
                if (!panning) return;
                panning = false;
                mainArea.ReleaseMouseCapture();
            };
            mainArea.MouseLeftButtonUp += endPan;
            mainArea.LostMouseCapture += delegate { panning = false; };
            mainArea.SizeChanged += delegate { Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ClampPan)); };
        }

        // point = 画像上の位置 (拡大前の座標)。その位置が画面上で動かないように倍率を変える
        private void ZoomAt(Point point, double factor)
        {
            double z = Math.Max(1, Math.Min(MaxZoom, zoom * factor));
            if (z < 1.02) z = 1;
            SetZoom(z, point);
        }

        private void SetZoom(double z, Point point)
        {
            zoomMove.X += (zoom - z) * point.X;
            zoomMove.Y += (zoom - z) * point.Y;
            zoom = z;
            zoomScale.ScaleX = z;
            zoomScale.ScaleY = z;
            ClampPan();
            mainArea.Cursor = zoom > 1 ? Cursors.SizeAll : null;
            UpdateInfo();
            if (zoom > 1) LoadFullResolution();
        }

        // 画像を切り替えたときなどに全体表示へ戻す
        private void ResetZoom()
        {
            panning = false;
            zoom = 1;
            zoomScale.ScaleX = 1;
            zoomScale.ScaleY = 1;
            zoomMove.X = 0;
            zoomMove.Y = 0;
            fullResPath = null;
            mainArea.Cursor = null;
        }

        // 拡大した画像が表示エリアからはみ出しすぎないように位置を制限する
        private void ClampPan()
        {
            if (zoom <= 1)
            {
                zoomMove.X = 0;
                zoomMove.Y = 0;
                return;
            }
            // 拡大前の画像の左上位置 (表示エリア基準)
            var target = ZoomTarget;
            Point o = target.TranslatePoint(new Point(0, 0), mainArea);
            o.X -= zoomMove.X;
            o.Y -= zoomMove.Y;
            zoomMove.X = ClampAxis(zoomMove.X, o.X, target.ActualWidth * zoom, mainArea.ActualWidth);
            zoomMove.Y = ClampAxis(zoomMove.Y, o.Y, target.ActualHeight * zoom, mainArea.ActualHeight);
        }

        private static double ClampAxis(double t, double origin, double size, double view)
        {
            if (size <= view) return (view - size) / 2 - origin;            // 収まるなら中央に
            return Math.Min(-origin, Math.Max(view - size - origin, t));    // はみ出すなら端で止める
        }

        // 通常は画面サイズに縮小した画像を表示しているので、拡大時は元の解像度で読み直す
        private async void LoadFullResolution()
        {
            var item = Current;
            if (item == null || item.IsVideo || fullResPath == item.Path) return;
            string p = item.Path;
            fullResPath = p;
            int ver = loadVersion;
            try
            {
                var bmp = await Task.Run(() => ImageLoader.Load(p, int.MaxValue));
                if (ver != loadVersion || fullResPath != p) return;
                mainImage.Source = bmp;
            }
            catch
            {
                if (fullResPath == p) fullResPath = null;
            }
        }

        // ---------------- 動画 ----------------

        // 拡大・移動の対象 (表示中の画像か動画)
        private FrameworkElement ZoomTarget
        {
            get { return videoPath != null ? (FrameworkElement)mainVideo : mainImage; }
        }

        private void InitVideo(FrameworkElement root)
        {
            mainVideo = (MediaElement)root.FindName("MainVideo");
            videoBar = (FrameworkElement)root.FindName("VideoBar");
            playButton = (Button)root.FindName("PlayButton");
            muteButton = (Button)root.FindName("MuteButton");
            seekSlider = (Slider)root.FindName("SeekSlider");
            volumeSlider = (Slider)root.FindName("VolumeSlider");
            timeText = (TextBlock)root.FindName("TimeText");

            mainVideo.MediaOpened += delegate
            {
                if (videoPath == null) return;
                videoOpened = true;
                var opened = Current;
                if (opened != null && opened.Path == videoPath && mainVideo.NaturalVideoWidth > 0)
                {
                    opened.PixelWidth = mainVideo.NaturalVideoWidth;
                    opened.PixelHeight = mainVideo.NaturalVideoHeight;
                    UpdateInfo();
                }
                // 開いた直後に一度シークする。これをしないと 1 周目の映像が止まったままになることがある
                mainVideo.Position = resumePosition;
                if (!resumePlaying)
                {
                    mainVideo.Pause();
                    mainVideo.ScrubbingEnabled = true;
                    mainVideo.Opacity = 1;   // 仮表示の画像の下なので、まだ見えない
                }
                // 再生位置が進み始めて最初のフレームが確実に描かれてから仮表示と入れ替える
                // (一時停止で開いた場合は位置が進まないので、少し待ってから入れ替える)
                revealDeadline = DateTime.Now.AddMilliseconds(resumePlaying ? 1000 : 300);
                CompositionTarget.Rendering += WaitForFirstFrame;
                seekSlider.Maximum = mainVideo.NaturalDuration.HasTimeSpan
                    ? Math.Max(0.1, mainVideo.NaturalDuration.TimeSpan.TotalSeconds) : 1;
                UpdateVideoTime();
            };
            mainVideo.MediaEnded += delegate
            {
                // 最後まで再生したら先頭に戻して繰り返す (短いクリップがすぐ終わって止まって見えないように)
                mainVideo.Position = TimeSpan.Zero;
                mainVideo.Play();
                UpdateVideoTime();
            };
            mainVideo.MediaFailed += delegate(object s, ExceptionRoutedEventArgs e)
            {
                var it = Current;
                statusText.Text = "動画を再生できません: " + (it != null ? it.FileName : "")
                                  + (e.ErrorException != null ? " - " + e.ErrorException.Message : "");
                SetPlaying(false);
                if (it != null && it.IsVideo) mainImage.Source = it.Thumbnail;   // 前の動画の絵を残さない
            };

            playButton.Click += delegate { TogglePlay(); };
            muteButton.Click += delegate
            {
                mainVideo.IsMuted = !mainVideo.IsMuted;
                UpdateMuteButton();
            };
            volumeSlider.ValueChanged += delegate
            {
                mainVideo.Volume = volumeSlider.Value;
                if (mainVideo.IsMuted && volumeSlider.Value > 0) { mainVideo.IsMuted = false; UpdateMuteButton(); }
            };

            // シークバー: クリック・ドラッグした位置へ移動
            seekSlider.ValueChanged += delegate
            {
                if (seekUpdating || !videoOpened) return;
                mainVideo.Position = TimeSpan.FromSeconds(seekSlider.Value);
                UpdateVideoTime();
            };
            seekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(delegate { seekDragging = true; }));
            seekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(delegate { seekDragging = false; }));

            videoTimer = new DispatcherTimer(DispatcherPriority.Background);
            videoTimer.Interval = TimeSpan.FromMilliseconds(200);
            videoTimer.Tick += delegate { UpdateVideoTime(); };

            // 音量・消音は前回の状態に戻す
            string saved = LoadSetting("volume.txt");
            double vol = 0.5;
            bool muted = false;
            if (saved != null)
            {
                string[] parts = saved.Split(',');
                double v;
                if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 1)
                    vol = v;
                muted = parts.Length > 1 && parts[1] == "1";
            }
            volumeSlider.Value = vol;
            mainVideo.Volume = vol;
            mainVideo.IsMuted = muted;
            UpdateMuteButton();
            Closing += delegate
            {
                SaveSetting("volume.txt", volumeSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                          + "," + (mainVideo.IsMuted ? "1" : "0"));
            };
        }

        private void OpenVideo(ImageItem item)
        {
            OpenVideo(item, TimeSpan.Zero, true);
        }

        // start = 開いたら移動する位置、play = false なら一時停止のまま
        private void OpenVideo(ImageItem item, TimeSpan start, bool play)
        {
            resumePosition = start;
            resumePlaying = play;
            videoPath = item.Path;
            videoOpened = false;
            videoShown = false;
            mainVideo.Opacity = 0;
            seekUpdating = true;
            seekSlider.Value = 0;
            seekUpdating = false;
            timeText.Text = "0:00 / 0:00";
            videoBar.Visibility = Visibility.Visible;
            SetPlaying(play);
            mainVideo.ScrubbingEnabled = false;   // 開くときは常にスクラブなし (一時停止で開く場合は MediaOpened 後に Pause)
            mainVideo.Source = new Uri(item.Path);
            mainVideo.Play();   // 一時停止で開く場合も、最初のフレームを出すために一度再生を始める
            videoTimer.Start();
        }

        private void WaitForFirstFrame(object sender, EventArgs e)
        {
            if (videoPath == null || videoShown)
            {
                CompositionTarget.Rendering -= WaitForFirstFrame;
                return;
            }
            TimeSpan advanced = mainVideo.Position - resumePosition;
            if (advanced > TimeSpan.FromMilliseconds(30)) mainVideo.Opacity = 1;   // 仮表示の画像の下で描画を始める
            if (advanced > TimeSpan.FromMilliseconds(180) || DateTime.Now > revealDeadline) RevealVideo();
        }

        // 仮表示の画像を消して動画を見せる
        private void RevealVideo()
        {
            CompositionTarget.Rendering -= WaitForFirstFrame;
            videoShown = true;
            mainVideo.Opacity = 1;
            mainImage.Source = null;
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        // 表示中の動画の今のフレームを画像として取り出す (切り替え・開き直しの間の仮表示用)。
        // ソフトウェア描画では RenderTargetBitmap に動画が写らないので、画面に出ている絵をそのまま読み取る。
        private BitmapSource SnapshotVideo()
        {
            IntPtr oldContext = IntPtr.Zero;
            try
            {
                var root = Content as FrameworkElement;
                if (!IsActive || root == null || root.ActualWidth <= 0 || mainVideo.ActualWidth <= 0) return null;   // 他のウィンドウが重なっているかもしれない
                Point tl = mainVideo.TranslatePoint(new Point(0, 0), root);

                // 拡大表示 (150% など) のモニターでは WPF の座標と画面の画素がずれるので、
                // 実際の画素単位でウィンドウの位置を取り、その比率で動画の範囲を求める
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                oldContext = SetThreadDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2
                RECT rc;
                GetClientRect(hwnd, out rc);
                var origin = new POINT();
                ClientToScreen(hwnd, ref origin);
                double sx = rc.Right / root.ActualWidth, sy = rc.Bottom / root.ActualHeight;
                int x = origin.X + (int)Math.Round(tl.X * sx), y = origin.Y + (int)Math.Round(tl.Y * sy);
                int w = (int)Math.Round(mainVideo.ActualWidth * sx), h = (int)Math.Round(mainVideo.ActualHeight * sy);
                if (w <= 0 || h <= 0) return null;
                using (var bmp = new System.Drawing.Bitmap(w, h))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(x, y, 0, 0, bmp.Size);
                    IntPtr hbmp = bmp.GetHbitmap();
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                                                                        BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();                        return src;
                    }
                    finally { DeleteObject(hbmp); }
                }
            }
            catch { return null; }
            finally
            {
                if (oldContext != IntPtr.Zero) SetThreadDpiAwarenessContext(oldContext);
            }
        }

        // 再生を止めてファイルを手放す
        private void StopVideo()
        {
            if (videoPath == null) return;
            CompositionTarget.Rendering -= WaitForFirstFrame;
            videoShown = false;
            videoTimer.Stop();
            mainVideo.Stop();
            mainVideo.Close();
            mainVideo.Source = null;
            mainVideo.Opacity = 0;
            videoPath = null;
            videoOpened = false;
            seekDragging = false;
            SetPlaying(false);
            videoBar.Visibility = Visibility.Collapsed;
        }

        private void TogglePlay()
        {
            if (videoPath == null) return;
            bool play = !videoPlaying;
            SetPlaying(play);   // 再生前にスクラブを切っておく
            if (play) mainVideo.Play(); else mainVideo.Pause();
        }

        private void SetPlaying(bool playing)
        {
            videoPlaying = playing;
            // スクラブ (シーク時のフレーム更新) を有効にしたまま再生すると映像が止まることがあるので、一時停止中だけ有効にする
            mainVideo.ScrubbingEnabled = !playing;
            playButton.Content = playing ? "❚❚" : "▶";
        }

        private void UpdateMuteButton()
        {
            muteButton.Content = mainVideo.IsMuted ? "\U0001F507" : "\U0001F50A";
            muteButton.ToolTip = mainVideo.IsMuted ? "消音を解除" : "消音";
        }

        // 再生位置の表示とシークバーを更新
        private void UpdateVideoTime()
        {
            if (!videoOpened) return;
            TimeSpan pos = mainVideo.Position;
            TimeSpan len = mainVideo.NaturalDuration.HasTimeSpan ? mainVideo.NaturalDuration.TimeSpan : TimeSpan.Zero;
            timeText.Text = FormatTime(pos) + " / " + FormatTime(len);
            if (!seekDragging)
            {
                seekUpdating = true;
                seekSlider.Value = Math.Min(seekSlider.Maximum, pos.TotalSeconds);
                seekUpdating = false;
            }
        }

        private static string FormatTime(TimeSpan t)
        {
            int total = (int)t.TotalSeconds;
            if (total >= 3600) return (total / 3600) + ":" + (total / 60 % 60).ToString("00") + ":" + (total % 60).ToString("00");
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        // 縦ホイールでサムネール一覧を横スクロール
        private void OnThumbWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = FindChild<ScrollViewer>(thumbList);
            if (sv == null) return;
            for (int i = 0; i < 3; i++)
            {
                if (e.Delta > 0) sv.LineLeft(); else sv.LineRight();
            }
            e.Handled = true;
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var t = child as T;
                if (t != null) return t;
                var found = FindChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (busy || draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0) OpenPath(paths[0]);
        }

        // ---------------- グリッド一覧 (別ウインドウ) ----------------
        // メインウインドウと同じ一覧 (フィルター・並び順も同じ) をタイル状に並べる。
        // クリックした画像・動画はメインウインドウで表示・再生する。

        private void ShowGrid()
        {
            if (gridWindow != null)
            {
                if (gridWindow.WindowState == WindowState.Minimized) gridWindow.WindowState = WindowState.Normal;
                gridWindow.Activate();
                return;
            }

            gridRoot = LoadXaml("GridWindow.xaml");
            gridList = (ListBox)gridRoot.FindName("GridList");
            gridCountText = (TextBlock)gridRoot.FindName("GridCountText");
            gridLoadText = (TextBlock)gridRoot.FindName("GridLoadText");
            gridSizeButtons = new[]
            {
                (ToggleButton)gridRoot.FindName("SizeSmall"),
                (ToggleButton)gridRoot.FindName("SizeMedium"),
                (ToggleButton)gridRoot.FindName("SizeLarge")
            };
            for (int i = 0; i < 3; i++)
            {
                int n = i;
                gridSizeButtons[i].Click += delegate { SetGridSize(n); };
            }
            int saved;
            if (!int.TryParse(LoadSetting("gridsize.txt"), out saved) || saved < 0 || saved > 2) saved = 0;
            gridSize = saved;
            ApplyGridSize();

            gridList.ItemsSource = view;
            gridList.SelectedItem = Current;
            gridList.SelectionChanged += delegate { OnGridSelectionChanged(); };
            gridList.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton == MouseButton.Left && IsOnGridItem(e.OriginalSource as DependencyObject))
                    ActivateMain();
            };
            gridList.PreviewMouseWheel += OnGridWheel;
            EnableFileDrag(gridList);
            gridList.IsEnabled = !busy;
            gridList.ContextMenu = itemMenu;
            gridList.ContextMenuOpening += itemMenuGuard;

            var w = new Window();
            w.Content = gridRoot;
            w.Icon = Icon;
            w.Background = Background;
            w.MinWidth = 320;
            w.MinHeight = 240;
            w.Width = 1000;
            w.Height = 750;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            RestoreGridBounds(w);
            w.PreviewKeyDown += OnGridKey;
            w.Loaded += delegate { if (Current != null) gridList.ScrollIntoView(Current); };
            w.Closing += delegate { SaveGridBounds(w); };
            w.Closed += delegate
            {
                gridList.ItemsSource = null;   // 共有している一覧から切り離す
                gridWindow = null;
                gridRoot = null;
                gridList = null;
                gridCountText = null;
                gridLoadText = null;
                gridSizeButtons = null;
                ClearGridThumbs();             // 大きめのサムネールはメモリを使うので捨てる
            };
            gridWindow = w;
            UpdateGridInfo();
            w.Show();
            LoadGridThumbs();
        }

        private void UpdateGridInfo()
        {
            if (gridWindow == null) return;
            gridWindow.Title = "グリッド一覧" + (currentFolder != null ? " - " + currentFolder : "");
            gridCountText.Text = countText.Text;
        }

        // グリッドでクリック (右クリック・キー操作も) された項目をメインウインドウで表示する
        private void OnGridSelectionChanged()
        {
            if (resorting || gridList == null) return;
            var it = gridList.SelectedItem as ImageItem;
            if (it != null && it != Current) thumbList.SelectedItem = it;
        }

        // メインウインドウ側で選択が変わったらグリッドの選択も合わせる
        private void SyncGridSelection()
        {
            if (gridList == null) return;
            var it = Current;
            if (gridList.SelectedItem != it) gridList.SelectedItem = it;
            if (it != null) gridList.ScrollIntoView(it);
        }

        private void ActivateMain()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private static bool IsOnGridItem(DependencyObject d)
        {
            return FindAncestor<ListBoxItem>(d) != null;
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T))
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return (T)d;
        }

        private void OnGridKey(object sender, KeyEventArgs e)
        {
            if (!busy && Keyboard.Modifiers == ModifierKeys.None)
            {
                switch (e.Key)
                {
                    case Key.Up:
                    case Key.Down:
                        // 上下は 1 行分移動
                        Move((e.Key == Key.Down ? 1 : -1) * GridColumns());
                        e.Handled = true;
                        return;
                    case Key.Enter:
                        ActivateMain();
                        e.Handled = true;
                        return;
                    case Key.Escape:
                        gridWindow.Close();
                        e.Handled = true;
                        return;
                }
            }
            OnKey(sender, e);   // 評価・削除・左右移動などはメインウインドウと同じ
        }

        // 1 行に並んでいるタイルの数
        private int GridColumns()
        {
            var first = gridList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            var panel = FindChild<WrapPanel>(gridList);
            if (first == null || panel == null || first.ActualWidth <= 0) return 1;
            return Math.Max(1, (int)(panel.ActualWidth / first.ActualWidth));
        }

        // Ctrl+ホイールでタイルの大きさを 1 段階ずつ変える (普通のホイールはスクロール)
        private void OnGridWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            SetGridSize(Math.Max(0, Math.Min(2, gridSize + (e.Delta > 0 ? 1 : -1))));
        }

        private void SetGridSize(int size)
        {
            bool changed = size != gridSize;
            gridSize = size;
            ApplyGridSize();   // 同じボタンを押したときもボタンの表示は戻す
            if (!changed) return;
            SaveSetting("gridsize.txt", size.ToString());
            if (Current != null) gridList.ScrollIntoView(Current);
            if (size == 0) ClearGridThumbs(); else LoadGridThumbs();
        }

        private void ApplyGridSize()
        {
            int w = GridTiles[gridSize];
            gridRoot.Resources["TileW"] = (double)w;
            gridRoot.Resources["TileH"] = (double)(w * 3 / 4);
            for (int i = 0; i < 3; i++) gridSizeButtons[i].IsChecked = i == gridSize;
        }

        // 中・大のときは、タイルの大きさに合わせたサムネールを作り直す (小は普通のサムネールのまま)。
        // グリッドに表示中の順に作り、そのあとフィルターで隠れている分も作る。
        private void LoadGridThumbs()
        {
            int ver = ++gridThumbVersion;
            if (gridWindow == null || gridSize == 0)
            {
                if (gridLoadText != null) gridLoadText.Visibility = Visibility.Collapsed;
                return;
            }
            int px = GridTiles[gridSize];
            var order = new List<ImageItem>();
            foreach (ImageItem it in gridList.Items) order.Add(it);
            var shown = new HashSet<ImageItem>(order);
            foreach (var it in items) if (!shown.Contains(it)) order.Add(it);
            // 大 → 中 に戻したときは、作ってある大きい方をそのまま使う
            var todo = order.Where(it => it.GridThumbSize < px).ToList();
            int total = todo.Count;
            ShowGridLoad(ver, 0, total);
            if (total == 0) return;

            var dispatcher = Dispatcher;
            Task.Run(() =>
            {
                for (int i = 0; i < total; i++)
                {
                    if (ver != gridThumbVersion) return;
                    var target = todo[i];
                    BitmapSource thumb = null;
                    try
                    {
                        thumb = target.IsVideo ? ShellThumbnail.Load(target.Path, px) : ImageLoader.Load(target.Path, px);
                    }
                    catch { }
                    int n = i + 1;
                    dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (ver != gridThumbVersion) return;
                        if (thumb != null) target.SetGridThumb(thumb, px);
                        ShowGridLoad(ver, n, total);
                    }));
                }
            });
        }

        private void ShowGridLoad(int ver, int done, int total)
        {
            if (ver != gridThumbVersion || gridLoadText == null) return;
            gridLoadText.Visibility = done >= total ? Visibility.Collapsed : Visibility.Visible;
            gridLoadText.Text = "サムネール作成中 " + done + " / " + total;
        }

        private void ClearGridThumbs()
        {
            gridThumbVersion++;   // 作成中なら止める
            foreach (var it in items) it.SetGridThumb(null, 0);
            if (gridLoadText != null) gridLoadText.Visibility = Visibility.Collapsed;
        }

        // ウインドウの位置・大きさを覚えておく (2 画面で別のモニターに置く使い方のため)
        private static void SaveGridBounds(Window w)
        {
            Rect r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
            if (r.IsEmpty) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            SaveSetting("gridwindow.txt", string.Join(",", new[]
            {
                r.Left.ToString(ci), r.Top.ToString(ci), r.Width.ToString(ci), r.Height.ToString(ci),
                w.WindowState == WindowState.Maximized ? "1" : "0"
            }));
        }

        private static void RestoreGridBounds(Window w)
        {
            string s = LoadSetting("gridwindow.txt");
            if (s == null) return;
            var parts = s.Split(',');
            if (parts.Length < 5) return;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out v[i])) return;
            }
            var r = new Rect(v[0], v[1], Math.Max(320, v[2]), Math.Max(240, v[3]));
            // モニター構成が変わって画面外になる場合は既定の位置に出す
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var visible = Rect.Intersect(r, screen);
            if (visible.IsEmpty || visible.Width < 100 || visible.Height < 100) return;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = r.Left;
            w.Top = r.Top;
            w.Width = r.Width;
            w.Height = r.Height;
            if (parts[4] == "1") w.WindowState = WindowState.Maximized;
        }

        // ---------------- 他のアプリへのドラッグ ----------------

        // サムネールをドラッグすると、そのファイルを他のアプリ (エクスプローラー、ブラウザなど) に渡せる。
        // 渡すのはコピーだけ (移動されると一覧と食い違うため)。
        private void EnableFileDrag(ListBox list)
        {
            ImageItem pressed = null;
            Point start = new Point();
            list.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
                pressed = lbi != null ? lbi.DataContext as ImageItem : null;
                start = e.GetPosition(list);
            };
            list.PreviewMouseLeftButtonUp += delegate { pressed = null; };
            list.PreviewMouseMove += delegate(object s, MouseEventArgs e)
            {
                if (pressed == null) return;
                if (e.LeftButton != MouseButtonState.Pressed) { pressed = null; return; }
                Vector d = e.GetPosition(list) - start;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                var item = pressed;
                pressed = null;
                if (busy || !File.Exists(item.Path)) return;

                var files = new System.Collections.Specialized.StringCollection();
                files.Add(item.Path);
                var data = new DataObject();
                data.SetFileDropList(files);
                draggingOut = true;
                try
                {
                    DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
                }
                catch (Exception ex)
                {
                    statusText.Text = "ドラッグできません: " + item.FileName + " - " + ex.Message;
                }
                finally
                {
                    draggingOut = false;
                }
                e.Handled = true;
            };
        }

        // ---------------- エクスプローラーで開く ----------------

        // select = true : ファイルを選択した状態で、そのフォルダを開く
        private void OpenInExplorer(string path, bool select)
        {
            try
            {
                string arg = select ? "/select,\"" + path + "\"" : "\"" + path + "\"";
                System.Diagnostics.Process.Start("explorer.exe", arg);
            }
            catch (Exception ex)
            {
                statusText.Text = "エクスプローラーを開けません: " + path + " - " + ex.Message;
            }
        }

        // フォルダツリーの右クリックメニュー
        private void InitTreeMenu()
        {
            FolderNode target = null;
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "エクスプローラーで開く" };
            open.Click += delegate
            {
                if (target == null) return;
                if (target.Path != null) OpenInExplorer(target.Path, false);
                else if (target.Kind == NodeKind.Computer) OpenInExplorer("shell:MyComputerFolder", false);
                else if (target.Kind == NodeKind.NetworkRoot) OpenInExplorer("shell:NetworkPlacesFolder", false);
            };
            menu.Items.Add(open);
            folderTree.ContextMenu = menu;
            // 右クリックしたノードを対象にする (ツリーの選択は変えない = フォルダは開かない)
            folderTree.ContextMenuOpening += delegate(object s, ContextMenuEventArgs e)
            {
                var tvi = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
                target = tvi != null ? tvi.DataContext as FolderNode : null;
                if (target == null || target.Kind == NodeKind.Placeholder) e.Handled = true;
            };
        }

        // ---------------- 設定 ----------------

        private static string SettingsFile(string name)
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "J-Rate");
            return System.IO.Path.Combine(dir, name);
        }

        private static string LoadSetting(string name)
        {
            try
            {
                string f = SettingsFile(name);
                return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch { return null; }
        }

        private static void SaveSetting(string name, string value)
        {
            try
            {
                string f = SettingsFile(name);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
                File.WriteAllText(f, value);
            }
            catch { }
        }

        private static string LoadLastFolder() { return LoadSetting("lastfolder.txt"); }
        private static void SaveLastFolder(string folder) { SaveSetting("lastfolder.txt", folder); }

        // ---------------- コピー / 移動 ----------------

        private string FilterDescription()
        {
            if (filterValue == 0) return "";
            if (filterMode == FilterMode.Only) return "★" + filterValue;
            return "★" + filterValue + (filterMode == FilterMode.AtLeast ? "以上" : "以下");
        }

        private async void Transfer(bool move)
        {
            if (busy || filterValue == 0) return;
            var targets = thumbList.Items.Cast<ImageItem>().ToList();
            if (targets.Count == 0) return;

            string desc = FilterDescription();
            var dlg = new ExportDialog(this, move, desc, targets.Count, currentFolder,
                                       LoadSetting("exportfolder.txt"), desc);
            if (dlg.ShowDialog() != true) return;
            string dest = dlg.Destination;
            ConflictMode conflict = dlg.Conflict;
            bool identicalOverwrite = dlg.IdenticalOverwrite;
            SaveSetting("exportfolder.txt", dest);

            string verb = move ? "移動" : "コピー";
            busy = true;
            toolbar.IsEnabled = false;
            folderTree.IsEnabled = false;
            if (gridList != null) gridList.IsEnabled = false;
            int selectedIndex = thumbList.SelectedIndex;
            if (move)
            {
                // 表示中・先読み中の画像を手放す
                loadVersion++;
                ClearCache();
                StopVideo();
                mainImage.Source = null;
            }

            int done = 0;
            var skippedSame = new List<string>();       // 中身が同じ → スキップ
            var skippedDiff = new List<string>();       // 中身が違う → スキップ
            var overwrittenSame = new List<string>();   // 中身が同じ → 上書き
            var overwrittenDiff = new List<string>();   // 中身が違う → 上書き
            var failed = new List<string>();
            var moved = new List<ImageItem>();
            int total = targets.Count;
            var progress = new Progress<int>(n => statusText.Text = verb + "中... " + n + " / " + total);

            await Task.Run(() =>
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var item = targets[i];
                    try
                    {
                        string dst = System.IO.Path.Combine(dest, item.FileName);
                        bool overwrite = false;
                        List<string> overwriteLog = null;
                        if (File.Exists(dst))
                        {
                            if (SameContent(item.Path, dst))
                            {
                                // サイズもチェックサムも一致 → 同じファイルとみなす
                                if (!identicalOverwrite) { skippedSame.Add(item.FileName); continue; }
                                overwrite = true;
                                overwriteLog = overwrittenSame;
                            }
                            else
                            {
                                if (conflict == ConflictMode.Skip) { skippedDiff.Add(item.FileName); continue; }
                                if (conflict == ConflictMode.Rename) dst = UniquePath(dst);
                                else { overwrite = true; overwriteLog = overwrittenDiff; }
                            }
                        }
                        if (move)
                        {
                            string src = item.Path, to = dst;
                            RetryIO(() =>
                            {
                                if (overwrite && File.Exists(to)) File.Delete(to);
                                File.Move(src, to);
                            });
                            moved.Add(item);
                        }
                        else
                        {
                            File.Copy(item.Path, dst, overwrite);
                        }
                        done++;
                        if (overwriteLog != null) overwriteLog.Add(item.FileName);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(item.FileName + " : " + ex.Message);
                    }
                    finally
                    {
                        ((IProgress<int>)progress).Report(i + 1);
                    }
                }
            });

            if (move && moved.Count > 0)
            {
                foreach (var it in moved) items.Remove(it);
                if (thumbList.Items.Count > 0)
                    thumbList.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, thumbList.Items.Count - 1));
            }
            busy = false;
            toolbar.IsEnabled = true;
            folderTree.IsEnabled = true;
            if (gridList != null) gridList.IsEnabled = true;
            ShowCurrent();
            string summary = done + " 枚を" + verb + "しました";
            int skippedCount = skippedSame.Count + skippedDiff.Count;
            int overwrittenCount = overwrittenSame.Count + overwrittenDiff.Count;
            if (overwrittenCount > 0) summary += " (うち上書き " + overwrittenCount + " 枚)";
            if (skippedCount > 0) summary += " / スキップ " + skippedCount + " 枚";
            if (failed.Count > 0) summary += " / 失敗 " + failed.Count + " 枚";
            statusText.Text = summary + " → " + dest;

            // スキップ・上書き・失敗があったときはダイアログで知らせる
            if (skippedCount + overwrittenCount + failed.Count > 0)
            {
                var msg = new System.Text.StringBuilder();
                msg.AppendLine(summary);
                AppendFileList(msg, "スキップしました (中身が同じファイルが既にあります)", skippedSame);
                AppendFileList(msg, "スキップしました (同じ名前で中身が違うファイルが既にあります)", skippedDiff);
                AppendFileList(msg, "上書きしました (中身が同じファイル)", overwrittenSame);
                AppendFileList(msg, "上書きしました (同じ名前で中身が違うファイル)", overwrittenDiff);
                AppendFileList(msg, "失敗しました", failed);
                var icon = failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
                MessageBox.Show(this, msg.ToString().TrimEnd(), "J-Rate", MessageBoxButton.OK, icon);
            }
        }

        private static void AppendFileList(System.Text.StringBuilder msg, string heading, List<string> names)
        {
            if (names.Count == 0) return;
            msg.AppendLine();
            msg.AppendLine(heading + ": " + names.Count + " 枚");
            foreach (var n in names.Take(8)) msg.AppendLine("    " + n);
            if (names.Count > 8) msg.AppendLine("    ... ほか " + (names.Count - 8) + " 枚");
        }

        // サイズが同じで、かつ SHA-256 チェックサムも一致すれば同じファイルとみなす
        private static bool SameContent(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            byte[] ha = null, hb = null;
            RetryIO(() => ha = Checksum(a));
            RetryIO(() => hb = Checksum(b));
            return ha.SequenceEqual(hb);
        }

        private static byte[] Checksum(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return sha.ComputeHash(fs);
            }
        }

        // "name.jpg" → "name (2).jpg", "name (3).jpg" ...
        private static string UniquePath(string path)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string ext = System.IO.Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string p = System.IO.Path.Combine(dir, name + " (" + n + ")" + ext);
                if (!File.Exists(p)) return p;
            }
        }

        // サムネール読み込みスレッドが一瞬ファイルを開いている場合に備えてリトライ
        private static void RetryIO(Action action)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { action(); return; }
                catch (IOException)
                {
                    if (attempt >= 5) throw;
                    Thread.Sleep(80);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // コピー / 移動先の選択ダイアログ
    // ------------------------------------------------------------------
    public enum ConflictMode { Rename, Skip, Overwrite }

    public class ExportDialog : Window
    {
        private readonly string sourceFolder;
        private readonly RadioButton existingRadio;
        private readonly TextBox existingPath, parentPath, newName;
        private readonly ComboBox conflictBox, identicalBox;

        public string Destination { get; private set; }
        public ConflictMode Conflict { get; private set; }
        public bool IdenticalOverwrite { get; private set; }   // 中身が同じ同名ファイル: true=上書き / false=スキップ

        public ExportDialog(Window owner, bool move, string filterDesc, int count,
                            string sourceFolder, string lastDest, string defaultName)
        {
            this.sourceFolder = sourceFolder;
            string verb = move ? "移動" : "コピー";
            Owner = owner;
            Title = verb + "先の選択";
            Width = 600;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));

            var root = MainWindow.LoadXaml("ExportDialog.xaml");
            Content = root;
            existingRadio = (RadioButton)root.FindName("ExistingRadio");
            var newRadio = (RadioButton)root.FindName("NewRadio");
            existingPath = (TextBox)root.FindName("ExistingPath");
            parentPath = (TextBox)root.FindName("ParentPath");
            newName = (TextBox)root.FindName("NewName");
            conflictBox = (ComboBox)root.FindName("ConflictBox");
            var ok = (Button)root.FindName("OkButton");
            var cancel = (Button)root.FindName("CancelButton");

            ((TextBlock)root.FindName("HeaderText")).Text =
                "サムネール一覧に表示中の " + filterDesc + " の画像 " + count + " 枚を" + verb + "します。";
            existingPath.Text = lastDest ?? "";
            parentPath.Text = sourceFolder ?? "";
            newName.Text = defaultName;
            bool hasLast = !string.IsNullOrEmpty(lastDest) && Directory.Exists(lastDest);
            existingRadio.IsChecked = hasLast;
            newRadio.IsChecked = !hasLast;
            conflictBox.SelectedIndex = 0;
            identicalBox = (ComboBox)root.FindName("IdenticalBox");
            identicalBox.SelectedIndex = 0;
            ok.Content = verb;

            ((Button)root.FindName("BrowseExisting")).Click += delegate
            {
                string p = FolderPicker.Pick(this, existingPath.Text, verb + "先のフォルダを選択");
                if (p != null) { existingPath.Text = p; existingRadio.IsChecked = true; }
            };
            ((Button)root.FindName("BrowseParent")).Click += delegate
            {
                string p = FolderPicker.Pick(this, parentPath.Text, "新しいフォルダを作る場所を選択");
                if (p != null) parentPath.Text = p;
            };
            ok.Click += delegate { OnOk(); };
            cancel.Click += delegate { DialogResult = false; };
            Loaded += delegate
            {
                var box = existingRadio.IsChecked == true ? existingPath : newName;
                box.Focus();
                box.SelectAll();
            };
        }

        private void OnOk()
        {
            string dest;
            bool create = false;
            try
            {
                if (existingRadio.IsChecked == true)
                {
                    dest = existingPath.Text.Trim();
                    if (dest == "" || !Directory.Exists(dest))
                    {
                        Error("フォルダが見つかりません。\n" + dest);
                        return;
                    }
                }
                else
                {
                    string parent = parentPath.Text.Trim();
                    string name = newName.Text.Trim();
                    if (parent == "" || !Directory.Exists(parent))
                    {
                        Error("作成場所のフォルダが見つかりません。\n" + parent);
                        return;
                    }
                    if (name == "" || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                    {
                        Error("フォルダ名が正しくありません。\n次の文字は使えません:  \\ / : * ? \" < > |");
                        return;
                    }
                    dest = System.IO.Path.Combine(parent, name);
                    if (Directory.Exists(dest))
                    {
                        var r = MessageBox.Show(this, "このフォルダは既に存在します。このフォルダを使いますか？\n" + dest,
                                                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (r != MessageBoxResult.Yes) return;
                    }
                    else
                    {
                        create = true;
                    }
                }
                dest = System.IO.Path.GetFullPath(dest);
            }
            catch (Exception ex)
            {
                Error(ex.Message);
                return;
            }

            if (sourceFolder != null && SamePath(dest, sourceFolder))
            {
                Error("元のフォルダと同じフォルダは指定できません。");
                return;
            }
            if (create)
            {
                try { Directory.CreateDirectory(dest); }
                catch (Exception ex)
                {
                    Error("フォルダを作成できません。\n" + ex.Message);
                    return;
                }
            }

            Destination = dest;
            Conflict = (ConflictMode)Math.Max(0, conflictBox.SelectedIndex);
            IdenticalOverwrite = identicalBox.SelectedIndex == 1;
            DialogResult = true;
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\'),
                                 System.IO.Path.GetFullPath(b).TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase);
        }

        private void Error(string message)
        {
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    // ------------------------------------------------------------------
    // 動画生成 (ComfyUI) の設定ダイアログ
    // ------------------------------------------------------------------
    public class ComfySettingsDialog : Window
    {
        private readonly TextBox urlBox, workflowBox;
        private readonly TextBlock resultText;

        public ComfySettingsDialog()
        {
            Title = "動画生成の設定 (ComfyUI)";
            Width = 600;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock { Text = "ComfyUI の URL" });
            var urlRow = new DockPanel { Margin = new Thickness(0, 4, 0, 12) };
            var test = new Button { Content = "接続テスト", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(test, Dock.Right);
            urlRow.Children.Add(test);
            urlBox = new TextBox { Text = ComfySettings.Url, Padding = new Thickness(2, 3, 2, 3) };
            urlRow.Children.Add(urlBox);
            panel.Children.Add(urlRow);

            panel.Children.Add(new TextBlock { Text = "ワークフロー (ComfyUI のメニュー「Export (API)」で保存した JSON)" });
            var wfRow = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
            var browse = new Button { Content = "参照...", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(browse, Dock.Right);
            wfRow.Children.Add(browse);
            workflowBox = new TextBox { Text = ComfySettings.WorkflowPath ?? "", Padding = new Thickness(2, 3, 2, 3) };
            wfRow.Children.Add(workflowBox);
            panel.Children.Add(wfRow);

            panel.Children.Add(new TextBlock
            {
                Text = "ワークフローの「Load Image」ノードの画像が、右クリックした画像に差し替わります。" +
                       "seed は毎回ランダムになります。出来た動画は元の画像と同じフォルダに「(元の名前)_video.mp4」で保存します。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
            });
            resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(resultText);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
            var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;

            test.Click += async delegate
            {
                string url = urlBox.Text.Trim().TrimEnd('/');
                resultText.Foreground = Brushes.DimGray;
                resultText.Text = "接続中...";
                test.IsEnabled = false;
                string err = await Task.Run(() => ComfyVideo.Test(url));
                test.IsEnabled = true;
                resultText.Foreground = err == null ? Brushes.ForestGreen : Brushes.Firebrick;
                resultText.Text = err == null ? "ComfyUI に接続できました: " + url : "接続できません: " + err;
            };

            browse.Click += delegate
            {
                var ofd = new Microsoft.Win32.OpenFileDialog();
                ofd.Title = "API 形式のワークフロー JSON を選択";
                ofd.Filter = "ワークフロー JSON (*.json)|*.json|すべてのファイル (*.*)|*.*";
                try
                {
                    string cur = workflowBox.Text.Trim();
                    if (cur.Length > 0 && File.Exists(cur)) ofd.InitialDirectory = System.IO.Path.GetDirectoryName(cur);
                }
                catch { }
                if (ofd.ShowDialog(this) == true)
                {
                    workflowBox.Text = ofd.FileName;
                    Check(ofd.FileName);
                }
            };

            ok.Click += delegate
            {
                string wf = workflowBox.Text.Trim().Trim('"');
                if (wf.Length == 0)
                {
                    MessageBox.Show(this, "ワークフローの JSON を指定してください。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!Check(wf)) return;
                ComfySettings.Url = urlBox.Text;
                ComfySettings.WorkflowPath = wf;
                DialogResult = true;
            };
        }

        // ワークフローを確かめて結果を表示する
        private bool Check(string path)
        {
            try
            {
                var wf = ComfyVideo.LoadWorkflow(ComfyVideo.NewSerializer(), path);
                int n = ComfyVideo.FindNodes(wf, "LoadImage").Count;
                if (n == 0) throw new Exception("ワークフローに「Load Image」(LoadImage) ノードがありません。");
                resultText.Foreground = Brushes.ForestGreen;
                resultText.Text = "ワークフローを確認しました (ノード " + wf.Count + " 個、Load Image " + n + " 個" +
                                  (n > 1 ? " ※すべて同じ画像に差し替わります" : "") + ")";
                return true;
            }
            catch (Exception ex)
            {
                resultText.Foreground = Brushes.Firebrick;
                resultText.Text = ex.Message;
                return false;
            }
        }
    }

    // ------------------------------------------------------------------
    // 「動画にする」のたびに出す、プロンプトと秒数の入力ダイアログ
    // ------------------------------------------------------------------
    public class ComfyPromptDialog : Window
    {
        private const double MaxSeconds = 60;

        private readonly TextBox promptBox, secondsBox;
        private readonly bool integerSeconds;   // 秒数を整数でしか受け付けないワークフロー

        // ワークフローに入れる場所がない項目は null (ワークフローの値のまま)
        public string Prompt { get; private set; }
        public double? Seconds { get; private set; }

        public ComfyPromptDialog(string fileName, bool hasPrompt, double? workflowSeconds, bool integerSeconds)
        {
            this.integerSeconds = integerSeconds;
            Title = "動画にする - " + fileName;
            Width = 600;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = hasPrompt ? "プロンプト (Enter で開始、Shift+Enter で改行)"
                                 : "プロンプト (このワークフローには入れる場所がありません)",
                Foreground = hasPrompt ? Brushes.Black : Brushes.DimGray
            });
            promptBox = new TextBox
            {
                Text = hasPrompt ? ComfySettings.Prompt : "",
                IsEnabled = hasPrompt,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 120,
                Padding = new Thickness(2, 3, 2, 3),
                Margin = new Thickness(0, 4, 0, 12)
            };
            panel.Children.Add(promptBox);

            bool hasSeconds = workflowSeconds != null;
            var secRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            secRow.Children.Add(new TextBlock { Text = "長さ", VerticalAlignment = VerticalAlignment.Center });
            string sec = "";
            if (hasSeconds)
            {
                // 前回の秒数、なければワークフローに書かれている秒数
                double last;
                sec = TryParseSeconds(ComfySettings.Seconds, out last)
                    ? ComfySettings.Seconds
                    : workflowSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            secondsBox = new TextBox
            {
                Text = sec,
                IsEnabled = hasSeconds,
                Width = 60,
                Padding = new Thickness(2, 3, 2, 3),
                Margin = new Thickness(8, 0, 6, 0),
                HorizontalContentAlignment = HorizontalAlignment.Right
            };
            secRow.Children.Add(secondsBox);
            secRow.Children.Add(new TextBlock
            {
                Text = hasSeconds ? "秒" : "秒 (このワークフローでは変えられません)",
                Foreground = hasSeconds ? Brushes.Black : Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(secRow);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "動画にする", Width = 100, IsDefault = true };
            var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;

            ok.Click += delegate { Accept(); };

            // 複数行の入力欄でも Enter だけで開始できるようにする (改行は Shift+Enter)。
            // 日本語変換の確定の Enter は Key.ImeProcessed で届くので、ここには来ない
            promptBox.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    Accept();
                }
            };

            Loaded += delegate
            {
                var first = hasPrompt ? promptBox : hasSeconds ? secondsBox : null;
                if (first == null) { ok.Focus(); return; }
                first.Focus();
                first.SelectAll();
            };
        }

        private void Accept()
        {
            if (secondsBox.IsEnabled)
            {
                double sec;
                if (!TryParseSeconds(secondsBox.Text, out sec) || (integerSeconds && sec != Math.Floor(sec)))
                {
                    MessageBox.Show(this, "長さは 1 ～ " + MaxSeconds + " 秒の" + (integerSeconds ? "整数" : "数字") +
                                          "で指定してください。", Title,
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    secondsBox.Focus();
                    secondsBox.SelectAll();
                    return;
                }
                Seconds = sec;
                ComfySettings.Seconds = secondsBox.Text;
            }
            if (promptBox.IsEnabled)
            {
                Prompt = promptBox.Text.Trim();
                ComfySettings.Prompt = Prompt;
            }
            DialogResult = true;
        }

        private static bool TryParseSeconds(string text, out double seconds)
        {
            return double.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out seconds)
                   && seconds >= 1 && seconds <= MaxSeconds;
        }
    }
}
