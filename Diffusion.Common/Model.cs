namespace Diffusion.Common;

public class Model
{
    public required string Path { get; set; }
    public required string Filename { get; set; }
    public required string Hash { get; set; }
    public string? SHA256 { get; set; }
    public bool IsLocal { get; set; }
}