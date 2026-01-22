using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace Diffusion.Toolkit
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // Catch all unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Diffusion.Common.Logger.Log($"CRITICAL UNHANDLED EXCEPTION: {ex?.Message}\n{ex?.StackTrace}");
            
            if (e.IsTerminating)
            {
                MessageBox.Show($"A critical error occurred:\n\n{ex?.Message}\n\nCheck DiffusionToolkit.log for details.",
                    "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Diffusion.Common.Logger.Log($"UI THREAD EXCEPTION: {e.Exception.Message}\n{e.Exception.StackTrace}");
            
            MessageBox.Show($"An error occurred:\n\n{e.Exception.Message}\n\nCheck DiffusionToolkit.log for details.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            
            e.Handled = true; // Prevent crash
        }

        private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            var ex = e.Exception.Flatten();
            var innerEx = ex.InnerExceptions.FirstOrDefault() ?? ex;
            Diffusion.Common.Logger.Log($"UNOBSERVED TASK EXCEPTION: {innerEx.Message}");
            Diffusion.Common.Logger.Log($"  Type: {innerEx.GetType().FullName}");
            Diffusion.Common.Logger.Log($"  Stack: {innerEx.StackTrace}");
            if (innerEx.InnerException != null)
            {
                Diffusion.Common.Logger.Log($"  Inner: {innerEx.InnerException.Message}");
                Diffusion.Common.Logger.Log($"  Inner Stack: {innerEx.InnerException.StackTrace}");
            }
            e.SetObserved(); // Prevent crash
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Add CUDA paths to environment for ONNX Runtime GPU support
            InitializeCudaPaths();
            
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Clean up background tagging service
            try
            {
                var bgService = Services.ServiceLocator.BackgroundTaggingService;
                if (bgService != null)
                {
                    bgService.StopTagging();
                    bgService.StopCaptioning();
                }
            }
            catch (Exception ex)
            {
                // Log but don't crash on exit
                Diffusion.Common.Logger.Log($"Error stopping background services: {ex.Message}");
            }
            
            base.OnExit(e);
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
