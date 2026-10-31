using System.Runtime;

namespace MyFront;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Big result sets live in memory; avoid blocking full GCs while the user scrolls.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        ApplicationConfiguration.Initialize();
        UI.ThemeManager.Init();
        Application.Run(new UI.MainForm());
    }
}
