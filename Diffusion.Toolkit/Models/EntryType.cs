namespace Diffusion.Toolkit.Models;

public enum EntryType
{
    File,
    Folder,
    RootFolder,
    SpecialFolder,
    Separator,
    Model,  // Model resource (LoRA, checkpoint, embedding, etc.)
}