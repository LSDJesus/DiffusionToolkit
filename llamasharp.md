## Image Embedding Reuse: InteractiveExecutor vs BatchedExecutor in LLamaSharp
This codemap explains why InteractiveExecutor cannot reuse SafeLlavaImageEmbedHandle across multiple InferAsync calls [1b] and how BatchedExecutor with Conversation API is designed for this use case [2c]. It shows the limitation where InteractiveExecutor always re-encodes images from byte arrays [1a] and clears them after each inference [1c], versus the BatchedExecutor pattern that creates embeddings once [2a] and reuses them across independent conversations [2b] with separate KV cache states.
### 1. InteractiveExecutor Image Encoding Limitation
How InteractiveExecutor forces re-encoding on each InferAsync call
### 1a. Images property stores byte arrays (`LLamaExecutorBase.cs:87`)
InteractiveExecutor inherits this property that only accepts raw image bytes
```text
public List<byte[]> Images { get; }
```
### 1b. Re-encodes images on every prompt (`LLamaInteractExecutor.cs:175`)
PreprocessLlava creates new embeddings from byte arrays each time
```text
foreach (var image in Images)
                {
                    _imageEmbedHandles.Add(SafeLlavaImageEmbedHandle.CreateFromMemory(ClipModel!.NativeHandle, Context, image));
                }
```
### 1c. Clears embeddings after inference (`LLamaInteractExecutor.cs:289`)
Embeddings are discarded after each InferAsync call
```text
_imageEmbedHandles.Clear();
                    Images.Clear();
```
### 1d. Temporary storage for embeddings (`LLamaInteractExecutor.cs:28`)
Embeddings only exist for duration of single inference
```text
private List<SafeLlavaImageEmbedHandle> _imageEmbedHandles = new List<SafeLlavaImageEmbedHandle>();
```
### 2. BatchedExecutor Reusable Embedding Pattern
How BatchedExecutor enables embedding reuse across conversations
### 2a. Create reusable embedding once (`LLavaWeights.cs:94`)
LLavaWeights provides methods to create embeddings that can be reused
```text
public SafeLlavaImageEmbedHandle CreateImageEmbeddings(LLamaContext ctxLlama, string image)
```
### 2b. Create independent conversation (`BatchedExecutor.cs:92`)
Each conversation gets separate KV cache state
```text
public Conversation Create()
```
### 2c. Accept pre-created embedding (`Conversation.cs:313`)
Conversation can accept SafeLlavaImageEmbedHandle directly
```text
public void Prompt(SafeLlavaImageEmbedHandle embedding)
```
### 2d. Add embedding patches to batch (`Conversation.cs:325`)
Efficiently adds pre-computed embedding data to processing batch
```text
batch.Add(
                (i, embedding),
                static (Span<float> dest, (int index, SafeLlavaImageEmbedHandle embedding) tup) => tup.embedding.GetEmbedding(dest, tup.index),
                _end++,
                ConversationId,
                i == embedding.Model.PatchCount - 1
            );
```
### 3. SafeLlavaImageEmbedHandle Design for Reuse
How the embedding handle enables efficient reuse patterns
### 3a. Factory creates reusable handle (`SafeLlavaImageEmbedHandle.cs:44`)
Static methods create handles that reference pre-encoded data
```text
public static SafeLlavaImageEmbedHandle CreateFromFileName(SafeLlavaModelHandle clip, LLamaContext ctx, string image)
```
### 3b. Native encoding happens once (`SafeLlavaImageEmbedHandle.cs:80`)
Expensive encoding operation performed only during creation
```text
var embed = NativeApi.llava_image_embed_make_with_filename(clip, threads, image);
```
### 3c. Efficient data access (`SafeLlavaImageEmbedHandle.cs:145`)
Copy pre-computed embedding data without re-encoding
```text
public void GetEmbedding(Span<float> dest, int index)
```
### 3d. Metadata for batching (`SafeLlavaImageEmbedHandle.cs:26`)
Provides patch count for efficient batch processing
```text
public int PatchCount => Model.PatchCount;
```



#######################################



using LLama.Batched;
using LLama.Common;
using LLama.Sampling;
using Spectre.Console;

namespace LLama.Examples.Examples;

/// <summary>
/// This demonstrates generating multiple replies to the same prompt, with a shared cache
/// </summary>
public class BatchedExecutorFork
{
    /// <summary>
    /// Set how many tokens to generate before forking
    /// </summary>
    private const int ForkTokenCount = 16;

    /// <summary>
    /// Set total length of the sequence to generate
    /// </summary>
    private const int TokenCount = 72;

    public static async Task Run()
    {
        // Load model weights
        var parameters = new ModelParams(UserSettings.GetModelPath());
        using var model = await LLamaWeights.LoadFromFileAsync(parameters);

        var prompt = AnsiConsole.Ask("Prompt (or ENTER for default):", "Not many people know that");

        // Create an executor that can evaluate a batch of conversations together
        using var executor = new BatchedExecutor(model, parameters);

        // Print some info
        var name = model.Metadata.GetValueOrDefault("general.name", "unknown model name");
        Console.WriteLine($"Created executor with model: {name}");

        // Evaluate the initial prompt to create one conversation
        using var start = executor.Create();
        start.Prompt(executor.Context.Tokenize(prompt));
        await executor.Infer();

        // Create the root node of the tree
        var root = new Node(start);
        var display = new Tree(prompt);
        await AnsiConsole
            .Live(display)
            .StartAsync(async ctx =>
            {
                
                // Run inference loop
                for (var i = 0; i < TokenCount; i++)
                {
                    if (i != 0)
                        await executor.Infer();

                    // Occasionally fork all the active conversations
                    if (i != 0 && i % ForkTokenCount == 0)
                        root.Fork();

                    // Sample all active conversations
                    root.Sample();

                    display = new Tree(prompt);
                    root.Display(display);
                    ctx.UpdateTarget(display);
                    ctx.Refresh();
                }
            });

        // Print some stats
        var timings = executor.Context.NativeHandle.GetTimings();
        AnsiConsole.MarkupLine($"Total Tokens Evaluated: {timings.TokensEvaluated}");
        AnsiConsole.MarkupLine($"Eval Time: {(timings.Eval + timings.PromptEval).TotalMilliseconds}ms");
    }

    private class Node
    {
        private readonly StreamingTokenDecoder _decoder;
        
        private readonly DefaultSamplingPipeline _sampler = new();
        private Conversation? _conversation;
        private string _message = string.Empty;

        private Node? _left;
        private Node? _right;

        public int ActiveConversationCount => _conversation != null ? 1 : _left!.ActiveConversationCount + _right!.ActiveConversationCount;

        public Node(Conversation conversation)
        {
            _conversation = conversation;
            _decoder = new StreamingTokenDecoder(conversation.Executor.Context);
        }

        public void Sample()
        {
            if (_conversation == null)
            {
                _left?.Sample();
                _right?.Sample();
                return;
            }

            if (_conversation.RequiresInference)
                return;

            // Sample one token
            var ctx = _conversation.Executor.Context.NativeHandle;
            var token = _sampler.Sample(ctx, _conversation.GetSampleIndex());
            _decoder.Add(token);

            // Prompt the conversation with this token, to continue generating from there
            _conversation.Prompt(token);
        }

        public void Fork()
        {
            if (_conversation != null)
            {
                _left = new Node(_conversation.Fork());
                _right = new Node(_conversation.Fork());

                _conversation.Dispose();
                _conversation = null;
            }
            else
            {
                _left?.Fork();
                _right?.Fork();
            }
        }

        
        public void Display<T>(T tree, int depth = 0)
            where T : IHasTreeNodes
        {
            var colors = new[] { "red", "green", "blue", "yellow", "white" };
            var color = colors[depth % colors.Length];

            _message += _decoder.Read().ReplaceLineEndings(". ");

            var n = tree.AddNode($"[{color}]{_message.EscapeMarkup()}[/]");

            _left?.Display(n, depth + 1);
            _right?.Display(n, depth + 1);
        }
    }
}