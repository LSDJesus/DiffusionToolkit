using System.Collections.Generic;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit.Pages;

public class ModelsModel : BaseNotify
{
    private IEnumerable<ModelViewModel> _models = new List<ModelViewModel>();
    private ModelViewModel? _selectedModel = null;
    private string _search = string.Empty;
    private IEnumerable<ModelViewModel> _filteredModels = new List<ModelViewModel>();

    public IEnumerable<ModelViewModel> Models
    {
        get => _models;
        set => SetField(ref _models, value);
    }

    public IEnumerable<ModelViewModel> FilteredModels
    {
        get => _filteredModels;
        set => SetField(ref _filteredModels, value);
    }

    public string Search
    {
        get => _search;
        set => SetField(ref _search, value);
    }

    public ModelViewModel? SelectedModel
    {
        get => _selectedModel;
        set => SetField(ref _selectedModel, value);
    }
}