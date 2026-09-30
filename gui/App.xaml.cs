using System;
using System.Linq;
using System.Windows;

namespace FileCrypt
{
    public partial class App : Application
    {
        /// <summary>
        /// 평소에는 메인 창. 개발용 숨은 옵션:
        ///   --netcus-capture   근태관리 사이트의 실제 응답을 떠 온다(읽기 전용, 목업 제작용)
        /// </summary>
        private void App_Startup(object sender, StartupEventArgs e)
        {
            Window w;
            if (e.Args.Any(a => string.Equals(a, "--netcus-capture", StringComparison.OrdinalIgnoreCase)))
                w = new NetcusCaptureWindow();
            else
                w = new MainWindow();
            MainWindow = w;
            w.Show();
        }
    }
}
