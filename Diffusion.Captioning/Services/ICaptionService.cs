using System.Threading;
using System.Threading.Tasks;
using Diffusion.Captioning.Models;

namespace Diffusion.Captioning.Services;

public interface ICaptionService
{
    Task<CaptionResult> CaptionImageAsync(string imagePath, string? prompt = null, CancellationToken cancellationToken = default);
    void ReleaseModel();
}