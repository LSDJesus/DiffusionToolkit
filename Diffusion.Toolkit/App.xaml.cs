using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace Diffusion.Toolkit
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Add CUDA paths to environment for ONNX Runtime GPU support
            InitializeCudaPaths();
            
            base.OnStartup(e);
        }
        
        private void InitializeCudaPaths()
        {
            var cudaPaths = new[]
            {
                // CUDA Toolkit - try common versions
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9\bin",
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin",
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6\bin",
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.4\bin",
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.2\bin",
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.1\bin",
                // cuDNN - try common versions
                @"C:\Program Files\NVIDIA\CUDNN\v9.14\bin\12.9",
                @"C:\Program Files\NVIDIA\CUDNN\v9.13\bin",
                @"C:\Program Files\NVIDIA\CUDNN\v9.6\bin\12.6",
                @"C:\Program Files\NVIDIA\CUDNN\v9.5\bin\12.6",
                // TensorRT
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\TensorRT-10.13.3.9\lib",
            };

            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var pathsToAdd = new List<string>();
            
            foreach (var cudaPath in cudaPaths)
            {
                if (Directory.Exists(cudaPath) && !existingPath.Contains(cudaPath, StringComparison.OrdinalIgnoreCase))
                {
                    pathsToAdd.Add(cudaPath);
                }
            }
            
            if (pathsToAdd.Count > 0)
            {
                var newPath = string.Join(";", pathsToAdd) + ";" + existingPath;
                Environment.SetEnvironmentVariable("PATH", newPath);
            }
        }
    }
}
