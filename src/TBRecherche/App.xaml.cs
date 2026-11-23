using System.Windows;
using System.Windows.Threading;
using TBRecherche.Core;

namespace TBRecherche;

public partial class App : Application
{
    void OnStartup(object sender, StartupEventArgs e)
    {
        string? profile = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--profile") profile = e.Args[i + 1];

        // Lancé par Thunderbird pour l'extension : pas de fenêtre, dialogue JSON sur stdin/stdout
        if (NativeHost.IsHostLaunch(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { NativeHost.Run(profile); }
            finally { Shutdown(); }
            return;
        }

        // Déclare (ou met à jour) l'hôte pour l'extension Thunderbird
        try { NativeHost.Register(Environment.ProcessPath!); } catch { }

        new MainWindow(profile).Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "TB Recherche - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
