# Face Recognition & Identity Clustering

The Diffusion Toolkit Face Recognition system provides automated face detection, identity embedding, and clustering for large-scale image collections.

## Architecture Overview

The system operates as a multi-stage pipeline integrated into the background scanning and metadata extraction process.

### 1. Detection (YOLO11-Face)
- **Model**: YOLO11-face (ONNX)
- **Input**: Full image (resized to 640px)
- **Output**: Bounding boxes, 5-point landmarks, and confidence scores.
- **Performance**: ~20-50ms per image on modern GPUs.

### 2. Quality Assessment
Before embedding, each face is analyzed for:
- **Sharpness**: Laplacian variance of the face crop.
- **Pose**: Yaw, Pitch, and Roll estimation.
- **Size**: Minimum resolution check (e.g., 40x40px).
- **Confidence**: Detection thresholding.

### 3. Identity Embedding (ArcFace)
- **Model**: ArcFace (ResNet-50 or ResNet-100 backbone)
- **Input**: Aligned face crop (112x112px).
- **Output**: 512-dimensional feature vector (embedding).
- **Normalization**: L2-normalized for cosine similarity comparison.

### 4. Database Storage (PostgreSQL + pgvector)
- **Table**: `face_detection`
- **Vector Column**: `arcface_embedding vector(512)`
- **Indexing**: HNSW (Hierarchical Navigable Small World) index for sub-millisecond similarity search across millions of faces.

### 5. Clustering & Labeling
- **Algorithm**: Incremental clustering based on cosine similarity threshold (default: 0.65).
- **Clusters**: Faces are grouped into `face_cluster` entities.
- **Manual Labeling**: Users can assign names to clusters, which propagates to all faces in that cluster.
- **Representative Faces**: The system selects the highest-quality face crops as thumbnails for each cluster.

## UI Integration

### Metadata Panel
The "Face Recognition" tab displays:
- **Face Crops**: High-quality crops of all detected faces in the current image.
- **Identity Labels**: The name of the person (if labeled) or the Cluster ID.
- **Similarity Search**: One-click button to find all other images containing this specific person.

### Face Gallery
A dedicated management window for:
- Browsing all identified people/clusters.
- Merging clusters (e.g., when the same person is identified as two different clusters).
- Bulk labeling and pruning.

## Technical Requirements
- **ONNX Runtime**: GPU acceleration via CUDA/DirectML.
- **PostgreSQL 16+**: With the `pgvector` extension enabled.
- **Models**:
  - `yolo12m-face.onnx` (Detection)
  - `w600k_r50.onnx` (Embedding)
