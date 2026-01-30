using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Diffusion.Common;
using Diffusion.Toolkit.Localization;
using Diffusion.Toolkit.Models;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace Diffusion.Toolkit.Services;

public class ProgressService
{
    private CancellationTokenSource _progressCancellationTokenSource;
    private Dispatcher _dispatcher => ServiceLocator.Dispatcher;

    private SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);
    public CancellationToken CancellationToken => _progressCancellationTokenSource.Token;

    private string GetLocalizedText(string key)
    {
        return (string)JsonLocalizationProvider.Instance.GetLocalizedObject(key, null, CultureInfo.InvariantCulture);
    }

    public ProgressService()
    {
        _progressCancellationTokenSource = new CancellationTokenSource();
    }

    public async Task StartTask()
    {
        await _syncLock.WaitAsync();
        try
        {
            ServiceLocator.MainModel.HasQueued = true;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<bool> TryStartTask()
    {
        await _syncLock.WaitAsync();
        try
        {
            if (ServiceLocator.MainModel.IsBusy)
            {
                await ServiceLocator.MessageService.Show(GetLocalizedText("Common.MessageBox.OperationInProgress"), GetLocalizedText("Common.MessageBox.Title"), PopupButtons.OK);
                return false;
            }

            _progressCancellationTokenSource = new CancellationTokenSource();

            _dispatcher.Invoke(() => { ServiceLocator.MainModel.IsBusy = true; });

            return true;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void CompleteTask()
    {
        _dispatcher.Invoke(() =>
        {
            ServiceLocator.MainModel.IsBusy = false;
        });
    }

    public async Task WaitForCompletion()
    {
        while (ServiceLocator.MainModel.IsBusy)
        {
            await Task.Delay(500);
        }
    }

    public void Cancel()
    {
        _progressCancellationTokenSource.Cancel();
    }

    public async Task<bool> CancelTask()
    {
        try
        {
            if (ServiceLocator.MessageService == null)
            {
                // No message service available - just cancel immediately
                _progressCancellationTokenSource.Cancel();
                return true;
            }

            var dialogResult = await ServiceLocator.MessageService.Show(
                GetLocalizedText("Common.MessageBox.ConfirmCancelOperation"), 
                GetLocalizedText("Common.MessageBox.Cancel"), 
                PopupButtons.YesNo);

            if (dialogResult == PopupResult.Yes)
            {
                _progressCancellationTokenSource.Cancel();
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"Error in CancelTask: {ex.Message}");
            // Cancel anyway if there's an error
            _progressCancellationTokenSource.Cancel();
            return true;
        }
    }

    public void InitializeProgress(int count)
    {
        _dispatcher.Invoke(() =>
        {
            ServiceLocator.MainModel.TotalProgress = count;
            ServiceLocator.MainModel.CurrentProgress = 0;
        });
    }

    public void ResetTotal()
    {
        _dispatcher.Invoke(() =>
        {
            ServiceLocator.MainModel.TotalProgress = 0;
        });
    }



    public void AddTotal(int count)
    {
        _dispatcher.Invoke(() =>
        {
            ServiceLocator.MainModel.TotalProgress += count;
        });
    }

    public void AddProgress(int count)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ServiceLocator.MainModel.CurrentProgress += count;
            //if (ServiceLocator.MainModel.CurrentProgress == ServiceLocator.MainModel.ProgressTarget)
            //{
            //    ClearProgress();
            //    CompleteTask();
            //}
        });
    }

    /// <summary>
    /// Updates the progress
    /// </summary>
    /// <param name="value">The CurrentProgress value</param>
    /// <param name="statusFormat">A string that with tokens {current} and {total}</param>
    public void SetProgress(int value, string? statusFormat = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ServiceLocator.MainModel.CurrentProgress = value;
            if (statusFormat != null)
            {
                ServiceLocator.MainModel.Status = statusFormat.Replace("{current}", value.ToString()).Replace("{total}", ServiceLocator.MainModel.TotalProgress.ToString());
            }
        });
    }

    public void ClearProgress()
    {
        _dispatcher.Invoke(() =>
        {
            ServiceLocator.MainModel.TotalProgress = 100;
            ServiceLocator.MainModel.CurrentProgress = 0;
        });
    }

    public void SetStatus(string status)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ServiceLocator.MainModel.Status = status;
        });
    }

    public void ClearStatus()
    {
        _dispatcher.BeginInvoke(() =>
        {
            ServiceLocator.MainModel.Status = "";
        });
    }
}