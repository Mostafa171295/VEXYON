using System.Windows;
namespace VEXYON;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(e.Exception.Message, "VEXYON");
            e.Handled = true;
        };
    }
}
