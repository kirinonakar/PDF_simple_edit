using Microsoft.UI.Xaml;
using PDF_simple_edit.Helpers;
using System;

namespace PDF_simple_edit
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            // 단일 파일 배포 시 WinAppSDK가 리소스를 찾을 수 있도록 경로 설정
            System.Environment.SetEnvironmentVariable(
                "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", 
                System.AppContext.BaseDirectory);
            InitializeComponent();

            // Global exception handling
            UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");
            e.Handled = true;
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
