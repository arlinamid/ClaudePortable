using System.Reflection;
using System.Runtime.Versioning;
using System.Windows;
using ClaudePortable.App.Ui.Views;

namespace ClaudePortable.App.Ui;

[SupportedOSPlatform("windows")]
public static class App
{
    public static int RunGui()
    {
        if (!SingleInstance.TryAcquire(out var mutex))
        {
            return 0;
        }

        using (mutex)
        using (var activateEvent = SingleInstance.CreateActivateEvent())
        {
            var app = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            LoadThemeResources(app);

            var tray = new TrayIcon();
            var mainWindow = new MainWindow();
            tray.OpenRequested += (_, _) => mainWindow.ShowAndActivate();
            tray.QuitRequested += (_, _) =>
            {
                tray.Dispose();
                app.Shutdown();
            };
            mainWindow.Closing += (_, args) =>
            {
                args.Cancel = true;
                mainWindow.Hide();
            };

            using var cts = new CancellationTokenSource();
            var activateLoop = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (!activateEvent.WaitOne(500))
                    {
                        continue;
                    }

                    try
                    {
                        app.Dispatcher.BeginInvoke(() => mainWindow.ShowAndActivate());
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        // Dispatcher already shut down.
                        break;
                    }
                }
            }, cts.Token);

            try
            {
                mainWindow.Show();
                return app.Run();
            }
            finally
            {
                cts.Cancel();
                activateEvent.Set(); // unblock WaitOne so the loop can exit
                try
                {
                    activateLoop.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // ignore shutdown race
                }
            }
        }
    }

    private static void LoadThemeResources(System.Windows.Application app)
    {
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        var uri = new Uri($"pack://application:,,,/{assemblyName};component/Ui/Theme.xaml", UriKind.Absolute);
        var theme = new ResourceDictionary { Source = uri };
        app.Resources.MergedDictionaries.Add(theme);
    }
}
