using System;
using System.Windows.Forms;

namespace Diffusion.Watcher;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        
        // Check if already running
        var mutex = new System.Threading.Mutex(true, "DiffusionWatcher_SingleInstance", out bool isNewInstance);
        
        if (!isNewInstance)
        {
            MessageBox.Show("Diffusion Watcher is already running.", "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new WatcherApplicationContext());
        
        GC.KeepAlive(mutex);
    }
}
