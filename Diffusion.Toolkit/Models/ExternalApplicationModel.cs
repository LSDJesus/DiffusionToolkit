namespace Diffusion.Toolkit.Models;

public class ExternalApplicationModel : BaseNotify
{
    private string? _name;
    private string? _path;
    private string? _commandLineArgs;
    private string? _shortcut;

    public string Name
    {
        get => _name ?? string.Empty;
        set => SetField(ref _name, value);
    }

    public string Path
    {
        get => _path ?? string.Empty;
        set => SetField(ref _path, value);
    }

    public string CommandLineArgs
    {
        get => _commandLineArgs ?? string.Empty;
        set => SetField(ref _commandLineArgs, value);
    }

    public string Shortcut
    {
        get => _shortcut ?? string.Empty;
        set => SetField(ref _shortcut, value);
    }
}