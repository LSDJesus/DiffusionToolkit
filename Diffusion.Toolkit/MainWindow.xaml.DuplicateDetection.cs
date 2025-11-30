using System.Windows;
using Diffusion.Toolkit.Windows;

namespace Diffusion.Toolkit
{
    public partial class MainWindow
    {
        private void DuplicateDetection_OnClick(object sender, RoutedEventArgs e)
        {
            var window = new DuplicateDetectionWindow
            {
                Owner = this
            };
            window.ShowDialog();
        }
    }
}
