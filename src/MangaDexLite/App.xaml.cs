using Microsoft.UI.Xaml;

namespace MangaDexLite;

public partial class App : Application
{
    private Window? window;
    public App()
    {
        UnhandledException += App_UnhandledException;
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            window = new MainWindow();
            window.Activate();
        }
        catch (Exception ex) { LogFailure(ex); throw; }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) => LogFailure(args.Exception);

    private static void LogFailure(Exception exception)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaDexLite");
            Directory.CreateDirectory(directory);
            string detail = exception.Data["RestrictedDescription"] as string ?? "";
            File.WriteAllText(Path.Combine(directory, "startup-error.log"), DateTimeOffset.Now + " " + exception + Environment.NewLine + detail);
        }
        catch (Exception) { /* Preserve the original failure if the log cannot be written. */ }
    }
}
