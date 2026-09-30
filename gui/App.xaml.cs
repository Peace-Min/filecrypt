using System;
using System.Linq;
using System.Windows;

namespace FileCrypt
{
    public partial class App : Application
    {
        /// <summary>
        /// 평소에는 메인 창. 개발용 숨은 옵션:
        ///   --netcus-capture          근태관리 사이트의 실제 응답을 떠 온다(읽기 전용, 목업 제작용)
        ///   --netcus-selftest 파일    목업 사이트로 올리기·가져오기 시나리오를 돌린다(test-netcus-mock.ps1)
        /// </summary>
        private async void App_Startup(object sender, StartupEventArgs e)
        {
            var args = e.Args;
            int st = Array.FindIndex(args, a => string.Equals(a, "--netcus-selftest", StringComparison.OrdinalIgnoreCase));
            if (st >= 0)
            {
                // 창 없이 돈다. WebView2 창은 NetcusService 가 화면 밖에 만든다.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                int code = st + 1 < args.Length ? await NetcusSelfTest.RunAsync(args[st + 1]) : 2;
                Shutdown(code);
                return;
            }

            Window w;
            if (args.Any(a => string.Equals(a, "--netcus-capture", StringComparison.OrdinalIgnoreCase)))
                w = new NetcusCaptureWindow();
            else
                w = new MainWindow();
            MainWindow = w;
            w.Show();
        }
    }
}
