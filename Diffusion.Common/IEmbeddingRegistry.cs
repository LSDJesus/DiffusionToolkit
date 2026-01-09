using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Diffusion.Common;

/// <summary>
/// Interface for embedding registry lookup
/// Implemented by PostgreSQL data store for use with EmbeddingExtractor
/// </summary>
public interface IEmbeddingRegistry
{
    Task<List<string>> GetPersonalEmbeddingNamesAsync(CancellationToken cancellationToken = default);}