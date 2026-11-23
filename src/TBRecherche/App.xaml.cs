using System.Windows;
using System.Windows.Threading;
using TBRecherche.Core;

namespace TBRecherche;

public partial class App : Application
{
    void OnStartup(object sender, StartupEventArgs e)
    {
        string? profile = null, db = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--profile") profile = e.Args[i + 1];
            else if (e.Args[i] == "--db") db = e.Args[i + 1];
        }

        // Lancé par Thunderbird pour l'extension : pas de fenêtre, dialogue JSON sur stdin/stdout
        if (NativeHost.IsHostLaunch(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { NativeHost.Run(profile); }
            finally { Shutdown(); }
            return;
        }

        // Déclare (ou met à jour) l'hôte pour l'extension Thunderbird.
        // Pas avec --db (index de test ou de démonstration) : l'extension garde l'exe habituel.
        if (db == null)
            try { NativeHost.Register(Environment.ProcessPath!); } catch { }

        new MainWindow(profile, db).Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "TB Recherche - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
