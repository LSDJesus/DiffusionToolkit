# Architecture

## Components

### Diffusion.Toolkit
**Description:** Request handlers and controllers

**Files:**
- AlbumListModel.cs
- AlbumListWindow.xaml.cs
- AlbumSortModel.cs
- AlbumSortWindow.xaml.cs
- App.xaml.cs
- ...

### Diffusion.Database.PostgreSQL
**Description:** Diffusion.Database.PostgreSQL module

**Files:**
- Models/Album.cs
- Models/AlbumListItem.cs
- Models/CountSize.cs
- ...

### Diffusion.Common
**Description:** Analysis and parsing logic

**Files:**
- AppInfo.cs
- Configuration.cs
- DatabaseConfiguration.cs
- ...

### Diffusion.Civitai
**Description:** Diffusion.Civitai module

**Files:**
- CivitaiClient.cs
- CivitaiRequestException.cs
- Models/CommercialUse.cs
- ...

### Diffusion.Embeddings
**Description:** Management and service layer

**Files:**
- BGETextEncoder.cs
- CLIPTextEncoder.cs
- CLIPTokenizer.cs
- ...

### Diffusion.Scanner
**Description:** Diffusion.Scanner module

**Files:**
- ComfyUI.cs
- FileParameters.cs
- HashFunctions.cs
- ...

### Diffusion.Tagging
**Description:** Management and service layer

**Files:**
- Models/JoyTagInputData.cs
- Models/JoyTagOutputData.cs
- Models/TagResult.cs
- ...

### Diffusion.Github
**Description:** Diffusion.Github module

**Files:**
- Asset.cs
- Author.cs
- GithubClient.cs
- ...

### Diffusion.Updater
**Description:** Diffusion.Updater module

**Files:**
- CustomTextBox.cs
- Form1.Designer.cs
- Form1.cs
- ...

### Diffusion.Captioning
**Description:** Management and service layer

**Files:**
- Models/CaptionResult.cs
- Services/HttpCaptionService.cs
- Services/ICaptionService.cs
- ...

### Diffusion.FaceDetection
**Description:** Management and service layer

**Files:**
- Models/FaceDetectionResult.cs
- Services/ArcFaceEncoder.cs
- Services/FaceDetectionService.cs
- ...

### Diffusion.Watcher
**Description:** Management and service layer

**Files:**
- Program.cs
- WatcherApplicationContext.cs
- WatcherService.cs

## Notes
- Suggested logical grouping of files. Use to understand architecture and refactoring opportunities.

## QA Review
- **Verified:** False
- **Confidence:** 85%
- **Issues:**
  - Diffusion.Toolkit is overloaded, containing UI, behaviors, models, services, and utility classes. This violates separation of concerns.
  - Diffusion.Common is described as 'Analysis and parsing logic' but contains general utilities and shared models, not just analysis/parsing.
  - Management and service layer is used as a description for multiple modules (Embeddings, Tagging, Captioning, FaceDetection, Watcher), which is too generic.
  - Some files in Diffusion.Toolkit (e.g., Common, Models, Services, Behaviors, Controls, Converters) could be split into separate assemblies for clarity and maintainability.
  - Build output files (obj/...) are included in the component map; these should be excluded from architectural grouping.

## Corrections
- **Misplaced Files:**
  - Diffusion.Toolkit/Common/AsyncCommand.cs -> Diffusion.Common
  - Diffusion.Toolkit/Common/RelayCommand.cs -> Diffusion.Common
  - Diffusion.Toolkit/Common/ObservableRangeCollection.cs -> Diffusion.Common
  - Diffusion.Toolkit/Models/AlbumModel.cs -> Diffusion.Common
  - Diffusion.Toolkit/Models/EntryType.cs -> Diffusion.Common
  - Diffusion.Toolkit/Services/AlbumService.cs -> Diffusion.Embeddings
  - Diffusion.Toolkit/Services/TaggingService.cs -> Diffusion.Tagging

- **Suggested Renames:**
  - Diffusion.Toolkit -> Diffusion.UI
  - Diffusion.Common -> Diffusion.Shared
  - Diffusion.Scanner -> Diffusion.MetadataScanner