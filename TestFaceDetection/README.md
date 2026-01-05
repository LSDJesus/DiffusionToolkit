# Face Detection Test Setup

## Required Models

The face detection pipeline requires two ONNX models:

### 1. YOLO Face Detector
- **Model**: `yolo12m-face.onnx` (or `yolo11n-face.onnx` for lighter version)
- **Location**: `models/onnx/yolo/yolo12m-face.onnx`
- **Source**: [YOLO-Face GitHub](https://github.com/akanametov/yolo-face)
- **Alternative**: [Ultralytics YOLO11](https://github.com/ultralytics/ultralytics)

### 2. ArcFace Embedding Model
- **Model**: `w600k_r50.onnx` (ResNet50 trained on WebFace600K)
- **Location**: `models/onnx/arcface/w600k_r50.onnx`
- **Source**: [InsightFace Model Zoo](https://github.com/deepinsight/insightface/tree/master/model_zoo)
- **Alternative**: `arcface_r100.onnx` (ResNet100 - more accurate but slower)

## Directory Structure

```
D:\AI\Github_Desktop\DiffusionToolkit\
├── TestFaceDetection/
│   ├── Program.cs
│   ├── TestFaceDetection.csproj
│   └── models/
│       └── onnx/
│           ├── yolo/
│           │   └── yolo12m-face.onnx
│           └── arcface/
│               └── w600k_r50.onnx
└── test_face.jpg  (optional - for testing)
```

## How to Run

1. **Download Models** (place in the structure above)

2. **Prepare Test Image** (optional)
   - Place a test image with faces at `test_face.jpg`
   - Or the test will just verify model loading

3. **Run Test**
   ```bash
   cd TestFaceDetection
   dotnet run
   ```

## Expected Output

```
=== Face Detection Pipeline Test ===

Testing Integrated Face Detection Service...
  ✓ Service initialized
  ✓ Processed image:
     - Image: test_face.jpg
     - Dimensions: 1920x1080
     - Faces detected: 3
     - Processing time: 245.67ms
     - Face details:
       • Confidence=0.987, Quality=0.823, Crop=15234 bytes, Embedding=512D
       • Confidence=0.952, Quality=0.791, Crop=14891 bytes, Embedding=512D
       • Confidence=0.931, Quality=0.756, Crop=13456 bytes, Embedding=512D

=== All tests completed ===
```

## Model Download Links

### YOLO Face Detection
```bash
# YOLO11n (nano - fastest, less accurate)
wget https://github.com/ultralytics/assets/releases/download/v0.0.0/yolo11n-face.pt

# Convert to ONNX using ultralytics
pip install ultralytics
yolo export model=yolo11n-face.pt format=onnx
```

### ArcFace Embedding
```bash
# Download from InsightFace
# ResNet50 (balanced)
wget https://github.com/deepinsight/insightface/releases/download/v0.7/w600k_r50.onnx

# OR ResNet100 (more accurate, slower)
wget https://github.com/deepinsight/insightface/releases/download/v0.7/glint360k_r100FC_1.0.onnx
```

## Testing Components Individually

The test will automatically verify:
1. ✓ Model file existence
2. ✓ Model loading (ONNX runtime initialization)
3. ✓ Face detection (YOLO inference)
4. ✓ Face embedding generation (ArcFace inference)
5. ✓ Cropped face image creation
6. ✓ Quality and confidence scoring

## Troubleshooting

### "Missing models" Error
- Ensure models are in the correct `models/onnx/{yolo,arcface}/` subdirectories
- Check file names match exactly: `yolo12m-face.onnx` and `w600k_r50.onnx`

### ONNX Runtime Errors
- GPU errors: The models will fall back to CPU automatically
- Install ONNX Runtime: `dotnet add package Microsoft.ML.OnnxRuntime.Gpu`
- CPU-only fallback: Models will work but slower

### No Faces Detected
- Ensure test image actually contains visible faces
- Try lowering confidence threshold in config
- Face must be at least 20x20 pixels to detect

## Performance Expectations

| Hardware | YOLO Inference | ArcFace Encoding | Total |
|----------|---------------|------------------|-------|
| GPU (RTX 3080) | ~20ms | ~5ms per face | ~30ms |
| CPU (i7-10700) | ~150ms | ~30ms per face | ~200ms |

## Next Steps

After successful test:
1. Integrate with DiffusionToolkit database
2. Test batch processing of multiple images
3. Test face clustering and similarity search
4. Verify UI display of detected faces
