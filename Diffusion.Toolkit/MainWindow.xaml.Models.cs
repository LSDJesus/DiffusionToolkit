using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Diffusion.Civitai;
using Diffusion.Civitai.Models;
using Diffusion.Common;
using System.Diagnostics;
using System.Threading;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    public partial class MainWindow
    {
        public void LoadImageModels()
        {
            var existingModels = _model.ImageModels == null ? Enumerable.Empty<ModelViewModel>() : _model.ImageModels.ToList();

            var imageModels = _dataStore.GetImageModels();

            _model.ImageModels = imageModels.Select(m => new ModelViewModel()
            {
                IsTicked = existingModels.FirstOrDefault(d => d.Name == m.Name || d.Hash == m.Hash)?.IsTicked ?? false,
                Name = m.Name ?? ResolveModelName(m.Hash),
                Hash = m.Hash,
                ImageCount = m.ImageCount
            }).Where(m => !string.IsNullOrEmpty(m.Name) && !string.IsNullOrEmpty(m.Hash)).OrderBy(x => x.Name).ToList();

            foreach (var model in _model.ImageModels)
            {
                model.PropertyChanged += ImageModelOnPropertyChanged;
            }

            _model.ImageModelNames = imageModels.Where(m => !string.IsNullOrEmpty(m.Name)).Select(m => m.Name).OrderBy(x => x);

        }

        private void ImageModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelViewModel.IsTicked))
            {
                var selectedModels = _model.ImageModels.Where(d => d.IsTicked).ToList();
                _model.SelectedModelsCount = selectedModels.Count;
                _model.HasSelectedModels = selectedModels.Any();
                _search.SearchImages();
            }
        }

        private string ResolveModelName(string hash)
        {
            var matches = _allModels.Where(m =>
                !string.IsNullOrEmpty(hash) &&
                (String.Equals(m.Hash, hash, StringComparison.CurrentCultureIgnoreCase)
                 ||
                 (m.SHA256 != null && string.Equals(m.SHA256.Substring(0, hash.Length), hash, StringComparison.CurrentCultureIgnoreCase))
                )).ToList();

            if (matches.Any())
            {
                return matches[0].Filename;
            }
            else
            {
                return hash;
            }
        }

        public async void DownloadCivitaiModels()
        {
            if (_model.IsBusy)
            {
                return;
            }

            var message = "This will update Civitai metadata for local models by looking them up by file hash. " +
                          "Metadata (name, description, base model, trigger words) and cover image thumbnails will be stored in the database.\r\n\r\n" +
                          "Are you sure you want to continue?";

            var result = await _messagePopupManager.ShowCustom(message, "Update Local Model Metadata", PopupButtons.YesNo, 500, 300);

            if (result == PopupResult.Yes)
            {
                if (await ServiceLocator.ProgressService.TryStartTask())
                {
                    try
                    {
                        var enrichmentService = new CivitaiEnrichmentService();
                        var progress = new Progress<(int Current, int Total)>(p =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                _model.TotalProgress = p.Total > 0 ? (int)((p.Current * 100.0) / p.Total) : 0;
                                _model.CurrentProgress = p.Current;
                                _model.Status = $"Fetching Civitai metadata... {p.Current}/{p.Total}";
                            });
                        });

                        await enrichmentService.EnrichPendingResourcesAsync(ServiceLocator.ProgressService.CancellationToken, progress);

                        if (ServiceLocator.ProgressService.CancellationToken.IsCancellationRequested)
                        {
                            message = "Update cancelled by user.";
                        }
                        else
                        {
                            message = "Model metadata has been updated. Check the log for details.";
                        }

                        await _messagePopupManager.Show(message, "Update Complete", PopupButtons.OK);
                        
                        enrichmentService.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error updating model metadata: {ex.Message}");
                        await _messagePopupManager.Show($"Error: {ex.Message}", "Update Failed", PopupButtons.OK);
                    }
                    finally
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _model.TotalProgress = 100;
                            _model.CurrentProgress = 0;
                            _model.Status = "Update Complete";
                        });

                        ServiceLocator.ProgressService.CompleteTask();
                    }
                }
            }
        }


        public async Task<LiteModelCollection?> LoadCivitaiModels()
        {
            var path = Path.Combine(AppDir, "models.json");

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);

                var options = new JsonSerializerOptions()
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Converters = { new JsonStringEnumConverter() }
                };

                return JsonSerializer.Deserialize<LiteModelCollection>(json, options);
            }

            return new LiteModelCollection();
        }


        public async Task<LiteModelCollection> FetchCivitaiModels(CancellationToken token)
        {
            using var civitai = new CivitaiClient();

            var collection = new LiteModelCollection();

            Dispatcher.Invoke(() =>
            {
                _model.Status = $"Downloading models from Civitai...";
            });

            var totalModels = 0;

            var results = await GetNextPage(civitai, civitai.BaseUrl + "/models?limit=100&page=1&types=Checkpoint", token);

            if (results != null)
            {
                collection.Models.AddRange(results.Items);

                while (!string.IsNullOrEmpty(results.Metadata.NextPage))
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    //var nextPage = results.Metadata.CurrentPage + 1;
                    //var totalPages = results.Metadata.TotalPages;

                    Dispatcher.Invoke(() =>
                    {
                        _model.Status = $"Downloading models from Civitai... {collection.Models.Count()}";
                    });

                    results = await GetNextPage(civitai, results.Metadata.NextPage, token);

                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (results != null)
                    {
                        collection.Models.AddRange(results.Items);
                    }
                }

                //while (results.Metadata.CurrentPage < results.Metadata.TotalPages)
                //{
                //    if (token.IsCancellationRequested)
                //    {
                //        break;
                //    }

                //    var nextPage = results.Metadata.CurrentPage + 1;
                //    var totalPages = results.Metadata.TotalPages;

                //    Dispatcher.Invoke(() =>
                //    {
                //        _model.TotalProgress = totalPages;
                //        _model.CurrentProgress = nextPage;
                //        _model.Status = $"Downloading set {nextPage} of {totalPages:n0}...";
                //    });

                //    results = await GetPage(civitai, nextPage, token);

                //    if (token.IsCancellationRequested)
                //    {
                //        break;
                //    }

                //    if (results != null)
                //    {
                //        collection.Models.AddRange(results.Items);
                //    }
                //}
            }


            return collection;
        }

        private async Task<Results<LiteModel>?> GetNextPage(CivitaiClient client, string nextPageUrl, CancellationToken token)
        {
            return await client.GetLiteModels(nextPageUrl, token);
        }


        private async Task<Results<LiteModel>?> GetPage(CivitaiClient client, int page, CancellationToken token)
        {
            return await client.GetLiteModelsAsync(new ModelSearchParameters()
            {
                Page = page,
                Limit = 100,
                Types = new List<ModelType>() { ModelType.Checkpoint }
            }, token);
        }

        /// <summary>
        /// Enrich model metadata: check headers first, sanitize, then fill gaps from Civitai API
        /// </summary>
        public async Task EnrichModelMetadataAsync()
        {
            var message = "This will enrich model metadata by:\n\n" +
                          "1. Reading Civitai metadata from model file headers\n" +
                          "2. Sanitizing any HTML or markdown formatting\n" +
                          "3. Copying header metadata to the database\n" +
                          "4. Fetching missing data (cover images, etc.) from Civitai API\n\n" +
                          "Continue?";

            var result = await _messagePopupManager.ShowCustom(message, "Enrich Model Metadata", PopupButtons.YesNo, 600, 350);

            if (result == PopupResult.Yes)
            {
                if (await ServiceLocator.ProgressService.TryStartTask())
                {
                    try
                    {
                        var enrichmentService = new CivitaiEnrichmentService();
                        var progress = new Progress<(int Current, int Total)>(p =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                _model.TotalProgress = p.Total > 0 ? (int)((p.Current * 100.0) / p.Total) : 0;
                                _model.CurrentProgress = p.Current;
                                _model.Status = $"Enriching metadata... {p.Current}/{p.Total}";
                            });
                        });

                        await enrichmentService.EnrichFromHeadersThenAPIAsync(ServiceLocator.ProgressService.CancellationToken, progress);

                        if (ServiceLocator.ProgressService.CancellationToken.IsCancellationRequested)
                        {
                            message = "Enrichment cancelled by user.";
                        }
                        else
                        {
                            message = "Model metadata enrichment complete. Check the log for details.";
                        }

                        await _messagePopupManager.Show(message, "Enrichment Complete", PopupButtons.OK);

                        enrichmentService.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error enriching model metadata: {ex.Message}");
                        await _messagePopupManager.Show($"Error: {ex.Message}", "Enrichment Failed", PopupButtons.OK);
                    }
                    finally
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _model.TotalProgress = 100;
                            _model.CurrentProgress = 0;
                            _model.Status = "Enrichment Complete";
                        });

                        ServiceLocator.ProgressService.CompleteTask();
                    }
                }
            }
        }

        /// <summary>
        /// Write database metadata back to model file headers (safetensors only)
        /// </summary>
        public async Task WriteMetadataToFilesAsync()
        {
            var message = "This will write model metadata from the database back to model file headers (safetensors only).\n\n" +
                          "Backups will be created before modification.\n\n" +
                          "Continue?";

            var result = await _messagePopupManager.ShowCustom(message, "Write Metadata to Files", PopupButtons.YesNo, 600, 300);

            if (result == PopupResult.Yes)
            {
                if (await ServiceLocator.ProgressService.TryStartTask())
                {
                    try
                    {
                        var enrichmentService = new CivitaiEnrichmentService();
                        var resources = await ServiceLocator.DataStore.GetAllModelResourcesAsync();

                        if (!resources.Any())
                        {
                            await _messagePopupManager.Show("No model resources found.", "Write Metadata", PopupButtons.OK);
                            return;
                        }

                        var safetensorsOnly = resources.Where(r => r.FilePath?.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) == true).ToList();

                        if (!safetensorsOnly.Any())
                        {
                            await _messagePopupManager.Show("No .safetensors files found. Only .safetensors format is supported.", "Write Metadata", PopupButtons.OK);
                            return;
                        }

                        var progress = new Progress<(int Current, int Total)>(p =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                _model.TotalProgress = p.Total > 0 ? (int)((p.Current * 100.0) / p.Total) : 0;
                                _model.CurrentProgress = p.Current;
                                _model.Status = $"Writing metadata to files... {p.Current}/{p.Total}";
                            });
                        });

                        var totalProcessed = 0;
                        var totalSuccessful = 0;

                        for (int i = 0; i < safetensorsOnly.Count; i++)
                        {
                            if (ServiceLocator.ProgressService.CancellationToken.IsCancellationRequested) break;

                            var resource = safetensorsOnly[i];
                            totalProcessed++;

                            try
                            {
                                if (await enrichmentService.WriteMetadataToFileAsync(resource, ServiceLocator.ProgressService.CancellationToken))
                                {
                                    totalSuccessful++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Error writing metadata to {resource.FileName}: {ex.Message}");
                            }

                            ((IProgress<(int, int)>)progress).Report((totalProcessed, safetensorsOnly.Count));
                        }

                        var resultMessage = $"Metadata written to {totalSuccessful}/{safetensorsOnly.Count} files successfully.";
                        await _messagePopupManager.Show(resultMessage, "Write Complete", PopupButtons.OK);

                        enrichmentService.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error writing metadata to files: {ex.Message}");
                        await _messagePopupManager.Show($"Error: {ex.Message}", "Write Failed", PopupButtons.OK);
                    }
                    finally
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _model.TotalProgress = 100;
                            _model.CurrentProgress = 0;
                            _model.Status = "Write Complete";
                        });

                        ServiceLocator.ProgressService.CompleteTask();
                    }
                }
            }
        }
    }
}
