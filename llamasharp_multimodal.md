# Answer

To integrate multimodal image captioning into your C# project using a GGUF model and GGUF mmproj file, you need to use LLamaSharp's multimodal support with the `InteractiveExecutor` and `LLavaWeights` classes [1](#0-0) .

## Setup Steps

**1. Load the main GGUF model and create a context:**

```csharp
var modelParams = new ModelParams("path/to/model.gguf");
var weights = LLamaWeights.LoadFromFile(modelParams);
var context = weights.CreateContext(contextParams);
``` [2](#0-1) 

**2. Load the mmproj (multimodal projections) file:**

The mmproj file contains the vision encoder weights needed for processing images [3](#0-2) . Load it using:

```csharp
var clipModel = LLavaWeights.LoadFromFile("path/to/mmproj.gguf");
``` [2](#0-1) 

**3. Create an InteractiveExecutor with the clip model:**

You must use `InteractiveExecutor` (not `StatelessExecutor`) for multimodal support [4](#0-3) :

```csharp
var executor = new InteractiveExecutor(context, clipModel);
``` [4](#0-3) 

**4. Add images and prompt with the `<image>` tag:**

Load your image as a byte array and add it to the executor's `Images` list. Then include the `<image>` tag in your prompt where you want the image to be processed [5](#0-4) :

```csharp
byte[] imageBytes = File.ReadAllBytes("path/to/image.jpg");
executor.Images.Add(imageBytes);

string prompt = "Describe this image: <image>";
await foreach (var text in executor.InferAsync(prompt, inferenceParams))
{
    Console.Write(text);
}
``` [6](#0-5) 

The `<image>` tag is parsed during preprocessing, and the image embeddings are created and evaluated at the appropriate position in the token sequence [7](#0-6) [8](#0-7) .

## Supported Image Formats

The image can be provided as either a file path or byte array. Supported formats include JPG, PNG, BMP, and TGA [9](#0-8) [10](#0-9) .

## Example Models

The test suite uses `llava-v1.6-mistral-7b.Q3_K_XS.gguf` as the main model and `mmproj-model-f16.gguf` as the mmproj file [11](#0-10) [12](#0-11) .

# Notes

- `StatelessExecutor` does **not** support multimodal inference<cite />. You must use `InteractiveExecutor` or `InstructExecutor`.
- The mmproj file is separate from the main model file and must be loaded independently using `LLavaWeights.LoadFromFile()`<cite />.
- Images are processed as embeddings that get inserted into the token sequence at the position of the `<image>` tag<cite />.
- For batched multimodal inference with multiple conversations, you can use `BatchedExecutor` with `Conversation.Prompt(SafeLlavaImageEmbedHandle)` instead [13](#0-12) .

Wiki pages you might want to explore:
- [StatelessExecutor (SciSharp/LLamaSharp)](/wiki/SciSharp/LLamaSharp#3.1)
- [State Persistence (SciSharp/LLamaSharp)](/wiki/SciSharp/LLamaSharp#5.6)

### Citations

**File:** LLama/LLavaWeights.cs (L9-13)
```csharp
/// <summary>
/// A set of llava model weights (mmproj), loaded into memory.
/// </summary>
public sealed class LLavaWeights
    : IDisposable
```

**File:** LLama/LLavaWeights.cs (L32-36)
```csharp
    public static LLavaWeights LoadFromFile(string mmProject)
    {
        var weights = SafeLlavaModelHandle.LoadFromFile(mmProject, 1);
        return new LLavaWeights(weights);
    }
```

**File:** LLama/Native/SafeLlavaModelHandle.cs (L32-40)
```csharp
        /// <summary>
        /// Load a model from the given file path into memory
        /// </summary>
        /// <param name="modelPath">MMP File (Multi-Modal Projections)</param>
        /// <param name="verbosity">Verbosity level</param>
        /// <returns>SafeHandle of the Clip Model</returns>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="LoadWeightsFailedException"></exception>
        public static SafeLlavaModelHandle LoadFromFile(string modelPath, int verbosity )
```

**File:** LLama/LLamaInteractExecutor.cs (L47-50)
```csharp
        public InteractiveExecutor(LLamaContext context, LLavaWeights clipModel, ILogger? logger = null)
            : base(context, clipModel, logger)
        {
        }
```

**File:** LLama/LLamaInteractExecutor.cs (L171-189)
```csharp
            // If the prompt contains the tag <image> extract this.
            _imageInPrompt = text.Contains("<image>");
            if (_imageInPrompt && IsMultiModal)
            {
                foreach (var image in Images)
                {
                    _imageEmbedHandles.Add(SafeLlavaImageEmbedHandle.CreateFromMemory(ClipModel!.NativeHandle, Context, image));
                }

                int imageIndex = text.IndexOf("<image>");
                // Tokenize segment 1 (before <image> tag)
                string preImagePrompt = text.Substring(0, imageIndex);
                var segment1 = Context.Tokenize(preImagePrompt, addBos, true);
                // Remember the position to add the image embeddings
                _EmbedImagePosition = segment1.Length;
                string postImagePrompt = text.Substring(imageIndex + 7);
                var segment2 = Context.Tokenize(postImagePrompt, false, true);
                _embed_inps.AddRange(segment1);
                _embed_inps.AddRange(segment2);
```

**File:** LLama/LLamaInteractExecutor.cs (L272-290)
```csharp
                if (IsMultiModal && _EmbedImagePosition > 0)
                {
                    // Tokens previous to the images
                    header = await Context.DecodeAsync(_embeds.GetRange(0, _EmbedImagePosition), LLamaSeqId.Zero, batch, _pastTokensCount);
                    _pastTokensCount = header.Item3;

                    if (header.Item1 != DecodeResult.Ok) throw new LLamaDecodeError(header.Item1);

                    // Images
                    foreach (var image in _imageEmbedHandles)
                        ClipModel!.EvalImageEmbed(Context, image, ref _pastTokensCount);

                    // Post-image Tokens
                    end = await Context.DecodeAsync(_embeds.GetRange(_EmbedImagePosition, _embeds.Count - _EmbedImagePosition), LLamaSeqId.Zero, batch, _pastTokensCount);
                    _pastTokensCount = end.Item3;

                    _EmbedImagePosition = -1;
                    _imageEmbedHandles.Clear();
                    Images.Clear();
```

**File:** LLama/Native/SafeLlavaImageEmbedHandle.cs (L34-41)
```csharp
        /// <param name="image">Path to the image file. Supported formats:
        /// <list type="bullet">
        ///     <item>JPG</item>
        ///     <item>PNG</item>
        ///     <item>BMP</item>
        ///     <item>TGA</item>
        /// </list>
        /// </param>
```

**File:** LLama/Native/SafeLlavaImageEmbedHandle.cs (L90-97)
```csharp
        /// <param name="image">Image bytes. Supported formats:
        /// <list type="bullet">
        ///     <item>JPG</item>
        ///     <item>PNG</item>
        ///     <item>BMP</item>
        ///     <item>TGA</item>
        /// </list>
        /// </param>
```

**File:** LLama.Unittest/Constants.cs (L12-13)
```csharp
        public static readonly string LLavaModelPath = "Models/llava-v1.6-mistral-7b.Q3_K_XS.gguf";
        public static readonly string LLavaMmpPath = "Models/mmproj-model-f16.gguf";
```

**File:** LLama.Unittest/LLama.Unittest.csproj (L55-65)
```text
        <DownloadFileItem Include="llava-v1.6-mistral-7b">
            <SourceUrl>https://huggingface.co/cjpais/llava-1.6-mistral-7b-gguf/resolve/main/llava-v1.6-mistral-7b.Q3_K_XS.gguf</SourceUrl>
            <DestinationFolder>Models</DestinationFolder>
            <LocalFileName>llava-v1.6-mistral-7b.Q3_K_XS.gguf</LocalFileName>
        </DownloadFileItem>

        <DownloadFileItem Include="mmproj-model-f16">
            <SourceUrl>https://huggingface.co/cjpais/llava-1.6-mistral-7b-gguf/resolve/main/mmproj-model-f16.gguf</SourceUrl>
            <DestinationFolder>Models</DestinationFolder>
            <LocalFileName>mmproj-model-f16.gguf</LocalFileName>
        </DownloadFileItem>
```

**File:** LLama/Batched/Conversation.cs (L309-333)
```csharp
    /// <summary>
    /// Prompt this conversation with an image embedding
    /// </summary>
    /// <param name="embedding"></param>
    public void Prompt(SafeLlavaImageEmbedHandle embedding)
    {
        AssertCanBePrompted();

        if (embedding.Model.EmbeddingDimensions != Executor.Model.EmbeddingSize)
            throw new ArgumentException($"Embedding dimension mismatch between image embedding ({embedding.Model.EmbeddingDimensions}) and model ({Executor.Model.EmbeddingSize})");

        for (var i = 0; i < embedding.Model.PatchCount; i++)
        {
            // Get a batch with space
            (var batch, _requiredEpoch) = Executor.GetEmbeddingBatch();
                
            batch.Add(
                (i, embedding),
                static (Span<float> dest, (int index, SafeLlavaImageEmbedHandle embedding) tup) => tup.embedding.GetEmbedding(dest, tup.index),
                _end++,
                ConversationId,
                i == embedding.Model.PatchCount - 1
            );
        }
    }
```
