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

        private void FaceGallery_OnClick(object sender, RoutedEventArgs e)
        {
            var window = new FaceGalleryWindow
            {
                Owner = this
            };
            window.ShowDialog();
        }
    }
}
