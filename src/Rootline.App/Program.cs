// Rootline.App — Program.cs
using System;
using System.Windows;

namespace Rootline.App
{
    public static class Program
    {
        [STAThread]
        public static int Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "Rootline", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            return app.Run(new MainWindow());
        }
    }
}
