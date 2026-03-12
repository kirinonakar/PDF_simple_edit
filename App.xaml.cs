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
            InitializeComponent();

            // Global exception handling
            UnhandledException += App_UnhandledException;

            // Initialize Korean font resolver
            try
            {
                PdfFontResolver.Initialize();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Font resolver init error: {ex.Message}");
            }
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
