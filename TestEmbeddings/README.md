# TestEmbeddings

Console application to verify ONNX embedding integration with GPU acceleration.

## Tests

1. **Text Embeddings** - Verifies BGE, CLIP-L, CLIP-G dimensions and normalization
2. **Image Embedding** - Verifies CLIP-H Vision encoder (requires test image)
3. **Full Generation** - Tests parallel execution of all 5 embeddings
4. **Batch Processing** - Tests deduplication and batch efficiency
5. **Text Similarity** - Verifies cosine similarity calculations
6. **GPU Info** - Instructions to monitor GPU utilization

## Usage

```powershell
cd TestEmbeddings
dotnet run
```

## Requirements

- ONNX models in `models/onnx/` directory
- Optional: `test_sample.png` in repository root for image tests
- NVIDIA GPU with CUDA support
- nvidia-smi for GPU monitoring

## Expected Output

- BGE: 1024D embeddings
- CLIP-L: 768D embeddings  
- CLIP-G: 1280D embeddings
- CLIP-H Vision: 1280D embeddings
- Parallel execution < 500ms per batch
- Identical texts: similarity = 1.0000
- Different texts: similarity < 0.95

## GPU Assignment

- **GPU 0 (RTX 5090)**: BGE + CLIP Vision
- **GPU 1 (RTX 3080 Ti)**: CLIP-L + CLIP-G

Monitor with: `nvidia-smi -l 1` in separate terminal
