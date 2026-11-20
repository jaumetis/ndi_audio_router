using System.Windows;

namespace NdiAudioRouter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Ndi.RegisterResolver();   // localitza Processing.NDI.Lib.x64.dll (NDI Runtime)
        base.OnStartup(e);
    }
}
