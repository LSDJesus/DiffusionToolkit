# Civit Image Grabber

A comprehensive tool for downloading, organizing, and managing images from CivitAI with a modern web interface.

## Features

- 🌐 **Modern Web GUI** - User-friendly browser-based interface
- 📥 **Multi-source Downloads** - Download by username, model ID, tag, or version ID (full resolution)
- 🎬 **Video Support** - Handles .mp4 files with live thumbnail previews on hover
- 🏷️ **Automatic Tag Scraping** - Extracts community tags from CivitAI image pages
- 🔄 **Batch WebP Conversion** - Convert JPG/PNG to WebP with embedded metadata
- 📊 **SQLite Database** - Efficient tracking of downloads and tag status
- ⚡ **Parallel Processing** - Concurrent downloads, conversions, and tag scraping
- 🎯 **Smart Organization** - Automatic folder structure based on NSFW status, model, and user
- 🔍 **Advanced Filtering** - Convert only tagged images, skip duplicates
- 🚀 **Multi-instance Support** - Run multiple operations simultaneously on different ports

---

# Installation

1. **Install Python 3** - Ensure you have Python 3.8 or newer installed.

2. **Install Dependencies**
```bash
pip install -r requirements_gui.txt
```

3. **Install Playwright** (for tag scraping)
```bash
pip install playwright
playwright install chromium
```

4. **Migrate Existing Data** (if upgrading from older versions)
   - If you have `downloaded_images.json` from previous versions:
   ```bash
   python migrate_json_to_sqlite.py
   ```

---

# Quick Start

## Web GUI (Recommended)

Launch the web interface:
```bash
python launch_gui.py
```

The browser will automatically open at `http://127.0.0.1:5000`

### Run Multiple Instances

Run on different ports for parallel operations:
```bash
# Instance 1 - Downloads (default port 5000)
python launch_gui.py

# Instance 2 - Tag scraping (port 5001)
python launch_gui.py --port 5001

# Instance 3 - Conversions (port 5002)
python launch_gui.py --port 5002
```

---

# Web GUI Features

## 1. Browse & Download

- Search and download images and videos by:
  - Username
  - Model ID
  - Tag search
  - Model Version ID
- **Full resolution downloads** - always downloads highest quality available
- **Video support** - .mp4 files with live thumbnail previews on mouseover
- Real-time progress tracking
- Pause/Resume/Stop controls
- NSFW filtering
- Concurrent download workers (1-20)

## 2. Tag Scraper

**Automatically scrapes community tags from CivitAI image pages**

- **Parallel scraping** with Playwright (headless browser)
- Extracts tag names, IDs, and community vote scores
- Automatically expands hidden tags
- 10-20 workers recommended (~4-5 images/second)
- Saves tags to JSON sidecars or WebP EXIF
- Pause/Resume/Stop controls
- Database tracking prevents duplicate scraping

**Workflow:**
1. Download images/videos (creates JPG/PNG/MP4 + JSON)
2. Batch scrape tags → saves to JSON (~1-5ms per image)
3. Tags marked as scraped in database

## 3. WebP Converter

**Convert JPG/PNG to space-efficient WebP format**

- Embeds full metadata + tags in EXIF
- Configurable quality (1-100)
- Parallel conversion workers
- Batch processing (100 images per batch)
- **"Only tagged" option** - converts only images with scraped tags
- Auto-organizes by NSFW/Model/User hierarchy
- In-place or new directory conversion
- Perceptual hash verification
- Database tracking of converted images

**Organized Output Structure:**
```
CivitAI_Browse/
├── Images/
│   ├── SFW/
│   │   └── ModelVersionName/
│   │       └── Username/
│   │           ├── 12345678.webp
│   │           └── 12345678.json
│   └── NSFW/
│       └── ModelVersionName/
│           └── Username/
│               ├── 87654321.webp
│               └── 87654321.json
└── Videos/
    ├── SFW/
    │   └── ModelVersionName/
    │       └── Username/
    │           ├── 98765432.mp4
    │           └── 98765432.json
    └── NSFW/
        └── ModelVersionName/
            └── Username/
                ├── 56781234.mp4
                └── 56781234.json
```

## 4. Database Management

- View all tracked images
- Search and filter
- Tag scraping status
- Conversion status
- Export to CSV

---

# Optimal Workflow

**For best performance, follow this sequence:**

### Sequential (Same Batch)
```
1. Download images → 2. Scrape tags → 3. Convert to WebP
```

### Parallel (Different Batches)
```
Instance 1 (Port 5000): Download NEW images (today)
Instance 2 (Port 5001): Scrape tags on OLDER images (yesterday)
Instance 3 (Port 5002): Convert OLDEST images (last week)
```

**Why this order?**
- Tag scraping to JSON is ~10-100x faster than modifying WebP EXIF
- Converter reads JSON + tags in one pass
- Database ensures no duplicate work
- "Only tagged" checkbox prevents converting untagged images

---

# Command-Line Interface

## Interactive Mode
python launch_gui.py --port 5001

# Instance 3 - Conversions (port 5002)
python launch_gui.py --port 5002
```

---

## Command-Line Mode

The classic CLI interface is still available:

Run the script without any command-line arguments:
```bash
python civit_image_downloader.py
```
the script will ask you to:

1.  `Enter timeout value (seconds) [default: 60]:`
2.  `Choose image quality (1=SD, 2=HD) [default: 1]:`
3.  `Allow re-downloading tracked items? (1=Yes, 2=No) [default: 2]:` 
4.  `Choose mode (1=user, 2=model ID, 3=tag search, 4=model version ID):` 
5.  `Enter max concurrent downloads [default: 5]:` 
6.  *(Mode-specific prompts):*
    *   Mode 1: `Enter username(s) (, separated):`
    *   Mode 2: `Enter model ID(s) (numeric, , separated):`
    *   Mode 3: `Enter tags (, separated):`
    *   Mode 3: `Disable prompt check? (y/n) [default: n]:` (Check if tag words must be in the image prompt)
    *   Mode 4: `Enter model version ID(s) (numeric, , separated):`

If you just hit enter it will use the Default values of that Option if it has a default value.  <br /> 
 <br /> 

## Command-Line Mode

Provide arguments directly on the command line. Unspecified arguments will use their defaults. `--mode` is required.

**Available Arguments**

*   `--timeout INT` (Default: 60)
*   `--quality {1,2}` (1=SD, 2=HD, Default: SD)
*   `--redownload {1,2}` (1=Yes, 2=No, Default: 2)
*   `--mode {1,2,3,4}` (**Required**)
*   `--tags TAGS` (Comma-separated, required for Mode 3)
*   `--disable_prompt_check {y,n}` (Default: n)
*   `--username USERNAMES` (Comma-separated, required for Mode 1)
*   `--model_id IDS` (Comma-separated, numeric, required for Mode 2)
*   `--model_version_id IDS` (Comma-separated, numeric, required for Mode 4)
*   `--output_dir PATH` (Default: "image_downloads")
*   `--semaphore_limit INT` (Default: 5)
*   `--no_sort` (Disables model subfolder sorting, Default: False/Sorting enabled)
*   `--max_path INT` (Default: 240)
*   `--retries INT` (Default: 2)

## Examples

*   Download images for user "artist1", allowing redownloads, higher concurrency:
    ```bash
    python civit_image_downloader.py --mode 1 --username "artist1" --quality 2 --redownload 1 --semaphore_limit 10
    ```
*   Download SD images for models 123 and 456, using defaults for other options:
    ```bash
    python civit_image_downloader.py --mode 2 --model_id "123, 456"
    ```
*   Download SD images for tag "sci-fi", disabling prompt check, no redownloads:
    ```bash
    python civit_image_downloader.py --mode 3 --tags "sci-fi" --disable_prompt_check y --redownload 2
    ```

## Mixed Mode

If only some arguments are provided (e.g., only `--mode`), the script will use the provided options and prompt the user for any missing inputs.

---

## Folder Structure

The downloaded files will be organized within the specified `--output_dir` (default: `image_downloads`). Sorting (`--no_sort` flag) affects the structure inside the identifier folder.

**With Sorting Enabled (Default)**

```
image_downloads/
├── Username_Search/
│   └── [Username]/
│       ├── [Model Name Subfolder]/  # Based on image metadata 'Model' field
│       │   ├── [ImageID].jpeg       # or .png, .webp, .mp4, .webm
│       │   └── [ImageID]_meta.txt
│       ├── invalid_metadata/        # For images with meta but no parsable 'Model' field
│       │   ├── [ImageID].jpeg 
│       │   └── [ImageID]_meta.txt
│       └── no_metadata/             # For images with no metadata found
│           ├── [ImageID].jpeg 
│           └── [ImageID]_no_meta.txt
├── Model_ID_Search/
│   └── model_[ModelID]/
│       ├── [Model Name Subfolder]/
│       │   └── [ImageID].jpeg
│       │   └── [ImageID]_meta.txt
│       ├── invalid_metadata/
│       │   └── [ImageID].jpeg
│       │   └── [ImageID]_meta.txt
│       └── no_metadata/
│           └── [ImageID].jpeg
│           └── [ImageID]_no_meta.txt
│ 
├── Model_Version_ID_Search/
│   └── modelVersion_[VersionID]/
│       ├── [Model Name Subfolder]/
│       │   └── [ImageID].jpeg
│       │   └── [ImageID]_meta.txt
│       ├── invalid_metadata/
│       │   └── [ImageID].jpeg
│       │   └── [ImageID]_meta.txt
│       └── no_metadata/
│           └── [ImageID].jpeg
│           └── [ImageID]_no_meta.txt
└── Model_Tag_Search/
    └── [Sanitized_Tag_Name]/         # e.g., sci_fi_vehicle
        ├── model_[ModelID]/          # Folder for each model found under the tag
        │   ├── [Model Name Subfolder]/ # Sorting within model folder
        │   └── [ImageID].jpeg
        │   └── [ImageID]_meta.txt
        │   ├── invalid_metadata/
        │   └── [ImageID].jpeg
        │   └── [ImageID]_meta.txt
        │   └── no_metadata/
        │   └── [ImageID].jpeg
        │   └── [ImageID]_no_meta.txt
        └── summary_[Sanitized_Tag_Name]_[YYYYMMDD].csv 
```

**With Sorting Disabled (`--no_sort`)**

All images and metadata files for a given identifier (username, model ID, model version ID, or model ID within a tag) are placed directly within that identifier's folder, without the `[Model Name Subfolder]`, `invalid_metadata`, or `no_metadata` subdirectories.

```
image_downloads/
├── Username_Search/
│   └── [Username]/
│       ├── [ImageID].jpeg
│       ├── [ImageID]_meta.txt
│       └── [ImageID]_no_meta.txt
├── Model_ID_Search/
│   └── model_[ModelID]/
│       ├── [ImageID].jpeg
│       └── ...
├── Model_Version_ID_Search/
│   └── modelVersion_[VersionID]/
│       ├── [ImageID].jpeg
│       └── ...
└── Model_Tag_Search/
    └── [Sanitized_Tag_Name]/
        ├── model_[ModelID]/
        │   ├── [ImageID].jpeg
        │   ├── [ImageID]_meta.txt
        │   └── [ImageID]_no_meta.txt
        └── summary_[Sanitized_Tag_Name]_[YYYYMMDD].csv # CSV still in tag folder
```

---

## Tracking Database (`tracking_database.sqlite`)

This file replaces the old JSON file. It stores a record of each downloaded image/video, including its path, quality, download date, associated tags (from Mode 3), original URL, and extracted checkpoint name (from metadata). You can explore this file using tools like "DB Browser for SQLite".

**Migration Tool (`migrate_json_to_sqlite.py`)**

If you are updating from a version using `downloaded_images.json`, run this separate Python script *once* in the same directory as your JSON file *before* using the main downloader. It will read the JSON and populate the new `tracking_database.sqlite` file.

```bash
python migrate_json_to_sqlite.py
```

---



# Update History

## 2.0 Major Update - Web GUI & Tag Scraping (January 2026)

### New Features

1. **Modern Web GUI Interface**
   - Browser-based interface with real-time updates
   - Separate tabs for Browse, Tag Scraper, Converter, and Database
   - Responsive design with dark/light theme support
   - Pause/Resume/Stop controls for all operations
   - Real-time progress tracking with percentage indicators
   - **Live video previews** - MP4 thumbnails play on mouseover in image cards

2. **Full Resolution Downloads**
   - Always downloads highest quality/resolution available
   - Automatic detection of best available format
   - Support for images (JPG/PNG/WebP) and videos (MP4)
   - No quality degradation or compression during download

3. **Automatic Tag Scraping System**
   - Scrapes community-voted tags from CivitAI image pages
   - Uses Playwright for JavaScript rendering (headless Chromium)
   - Automatically clicks "show more" button to reveal hidden tags
   - Extracts tag names, IDs, and community scores
   - Parallel processing with 10-20 concurrent workers
   - Performance: ~4-5 images/second (~270/minute)
   - Saves to JSON sidecars or WebP EXIF metadata
   - Database tracking prevents duplicate scraping

4. **Batch WebP Converter**
   - Convert JPG/PNG to space-efficient WebP format
   - Embeds full metadata + scraped tags in EXIF
   - Configurable quality settings (1-100)
   - Parallel conversion with worker threads
   - **"Only tagged" filter** - converts only images with scraped tags
   - Auto-organizes by NSFW status, model version, and username
   - In-place or new directory conversion options
   - Perceptual hash verification ensures quality
   - Batch processing (configurable batch size)

4. **Multi-Instance Support**
   - Run multiple instances on different ports
   - Parallel operations: download + tag + convert simultaneously
   - Command-line flag: `--port 5001`
   - Each instance shares the same database

5. **Enhanced Database Schema**
   - New `tags_scraped` column tracks tag scraping status
   - Atomic operations prevent race conditions
   - Efficient filtering for "only tagged" conversions
   - CSV export functionality

6. **Organized Folder Structure**
   - 3-level hierarchy: NSFW → ModelVersion → Username
   - Automatic sorting during conversion
   - Preserves JSON sidecars alongside images and videos (WebP/MP4)

### Performance Optimizations

- Full resolution downloads without quality degradation
- Async Playwright for concurrent tag scraping
- ThreadPoolExecutor for parallel conversions
- Semaphore-based download concurrency
- Batch processing to reduce memory footprint
- Database indexing for faster queries

### Technical Improvements

- RESTful API backend (Flask)
- Vue.js frontend with reactive data binding
- SQLite database with proper schema
- Comprehensive error handling and logging
- Automatic retry logic for network failures
- Perceptual hashing for image verification

### Workflow Enhancements

**Optimal Sequential Flow:**
1. Download images → 2. Scrape tags → 3. Convert to WebP

**Parallel Processing:**
- Run downloads, tag scraping, and conversions on separate instances
- Database ensures no duplicate work across instances
- "Only tagged" checkbox prevents converting untagged images

---

## 1.3 New Feature & Update

1.  **Code Structure (Major Refactoring):**  <br />
                                           The entire script has been refactored into an object-oriented structure using the `CivitaiDownloader` class. This encapsulates state (configuration,tracking data, 
                                           statistics) and logic (downloading, sorting, API interaction) within the class, eliminating reliance on global variables. <br />

2.  **Scalable Tracking (SQLite Migration):** <br />
      **Replaced JSON:** The previous `downloaded_images.json` tracking file has been replaced with an **SQLite database** (`tracking_database.sqlite`). <br />
       **Relational Tags:** Image tags (for Mode 3 summaries) are now stored relationally in a separate `image_tags` table, linked to the main `tracked_images` table. This allows for efficient querying. <br />
       **Migration:** A separate `migrate_json_to_sqlite.py` script is provided for users to perform a one-time migration of their existing `downloaded_images.json` data into the new SQLite database format. <br />

3.  **Robust Error Handling & Retries:** <br />
       **Automatic Retries:** Integrated the `tenacity` library to automatically retry failed network operations (image downloads, API page fetches, model searches) caused by common transient issues like timeouts, 
                              connection errors, or specific server-side errors (500, 502, 503, 504). <br />
       **File Operations:** Implemented a `_safe_move` function with retries to handle potential file locking issues during sorting (especially on Windows). Added checks to verify move operations. <br />

4.  **Improved Tag Search (Mode 3) Validation:** <br />
       **Invalid Tag Detection:** When searching by tag, the script now fetches the first page of results and checks if any of the returned models *actually contain* the searched tag in their own metadata tags. <br />

5.  **Detailed Per-Identifier Statistics:** <br />
       **Granular Reporting:** The final statistics summary now provides a detailed breakdown for *each* identifier (username, model ID, tag, version ID) processed during the run. <br />

6.  **Improved User Interface & Experience:** <br />
       **Input Validation:** Added/improved validation loops for interactive inputs (e.g., ensuring numeric IDs, positive numbers). Handles invalid CLI arguments more gracefully (logs errors, exits). <br />
       **Clearer Output:** Refined console and log messages. Added specific warnings for invalid tags or identifiers that yield no results. Reduced console noise by logging successful per-file downloads only at the 
                            DEBUG level. Added a final summary note listing identifiers that resulted in no downloads. <br />



## 1.2 New Feature & Update

### Command-Line Parameter Support <br />

This update introduces support for three different startup modes.<br />

Fully Interactive Mode: If no command-line arguments are provided, the script will prompt the user for all required inputs interactively, as before.<br />

Fully Command-Line Mode: If all necessary arguments are supplied via the command line, the script will execute without any prompts, offering a streamlined experience for advanced users.<br />

Mixed Mode: If only some arguments are provided, the script will use the provided options and prompt the user for any missing inputs. This allows for a flexible combination of both modes.<br />

The new Feature includes a check for mismatched arguments. If you provide arguments that don't match the selected mode, you will receive a warning message, but the script will continue to run,<br /> 
ignoring the mismatched arguments and prompting for the required information if necessary.<br />

```
Warning: --Argument is not used in ... mode. This argument will be ignored.
```
## no_meta_data Folder
All images with no_meta_data are now moved to their own folder named no_meta_data. <br />
They also have a text file containing the URL of the image, rather than any metadata.<br />
```
No metadata available for this image.
URL: https://civitai.com/images/ID?period=AllTime&periodMode=published&sort=Newest&view=feed&username=Username&withTags=false
```


### Update
## BUG FIX
A bug was fixed where the script sometimes did not download all the images provided by the API.<br />
The logging function was also enhanced. You can now see how many image links the API provided and what the script has downloaded. <br />
A short version is displayed in your terminal. <br />
```
Number of downloaded images: 2
Number of skipped images: 0
```
While more detailed information is available in the log file.<br />
```
Date Time - INFO - Running in interactive mode
Date Time - WARNING - Invalid timeout value. Using default value of 60 seconds.
Date Time - WARNING - Invalid quality choice. Using default quality SD.
Date Time - INFO - Received 2 items from API for username Example
Date Time - INFO - Attempting to download: https://image.civitai.com/247f/width=896/b7354672247f.jpeg
Date Time - INFO - Attempting to download: https://image.civitai.com/db84/width=1024/45757467b84.jpeg
Date Time - INFO - Successfully downloaded: image_downloads/Username_Search/Example/2108516.jpeg
Date Time - INFO - Successfully downloaded: image_downloads/Username_Search/Example/2116132.jpeg
Date Time - INFO - Marked as downloaded: 21808516 at image_downloads/Username_Search/Example/2108516.jpeg
Date Time - INFO - Marked as downloaded: 21516132 at image_downloads/Username_Search/Example/2116132.jpeg
Date Time - INFO - Total items from API: 2, Total downloaded: 2
Date Time - INFO - 2 images have no meta data.
Date Time - INFO - Total API items: 2, Total downloaded: 2
Date Time - INFO - Image download completed.

```





## 1.1 New Feature & Update

### New Download Option Modelversion ID   <br />
The script can now selectively download images that belong to a specific model version ID. Option 4 <br />
This saves disk space and in addition, the Civit AI Server API is used less, which leads to a more efficient use of resources. <br />
The Script will download the Images to this new Folder  --> Model_Version_ID_Search<br />
Updated the **Folder Structure** <br />


### Updated Timeout  <br />
i have noticed that the timeout of 20 seconds is too short for model ID and version ID and that i get more network errors than downloads,  <br />
so i have set it to 60 seconds for now.  <br />
But if you want to be on the safe side, then enter the following: 120  for the option: Enter timeout value (in seconds): <br />
this has always worked up to now <br />


## 1.0  Update

Updated Folder Structure. <br />
The script creates a Folder for each Option you can choose.  <br />
This new structure ensures better organization based on the search type, making image management more efficient. <br />

## 0.9 Feature & Updates

New Feature

Redownload of images.
The new option allows the tracking file to be switched off. So that already downloaded images can be downloaded again. 
```
Allow re-downloading of images already tracked (1 for Yes, 2 for No) [default: 2]: 
```
If you choose 2 or just hit enter the Script will run with Tracking as Default like always. <br />


New Update <br />

When the script is finished, a summary of the usernames or Model IDs that could not be found is displayed. <br />
```
Failed identifiers:
username: 19wer244rew
```
```
Failed identifiers:
ModelID: 493533
```


## 0.8 Helper script tagnames
With this Script you can search locally in txt a file if your TAG is searchable.  <br />
Just launch tagnames.py and it creates a txt File with all the Tags that the API gives out for the Model TAG search Option 3  <br />
But there are some entrys that are cleary not working. I dont kow why they are in the API Answer.  <br />
It has an function to add only new TAGS to he txt File if you run it again. 

## 0.7 Features Updates Performance 

Features: <br /> 

Model Tag Based Image download in SD or HD with Prompt Check Yes or NO <br /> 
Prompt Check YES means when the TAG is also present in the Prompt, then the image will be Downloaded. Otherwise it will be skipped.<br /> 
Prompt Check NO all Images with the searched TAG will be Downloaded. But the chance for unrelated Images is higher.<br /> 

CSV File creation within Option 3 TAG Seach  
The csv file will contain the image data that, according to the JSON file, has already been downloaded under a different TAG in this format: <br />
"Current Tag,Previously Downloaded Tag,Image Path,Download URL"  <br /> 

Litte Statistc how many images have just been downloaded and skipped with a why reasons.

Updates: <br /> 

Use of Multiple Entrys in all 3 Options comma-separated <br /> 

New Folder Structure for Downloaded Images in all Options First Folder is named after what you searched Username, ModelID, TAG. 
Second is the Model that was used to generate the image

![Untitled](https://github.com/Confuzu/CivitAI_Image_grabber/assets/133601702/fe49eb95-f1bc-4d96-80b6-c165d76d29e5)

Performance:

Code optimizations now the script runs smoother and faster. <br /> 
Better Error Handling for some Cases <br /> 


## 0.6 New Function

Rate Limiting set to 20 simultaneous connections. 
Download Date Format changend in the JSON Tracking File 


## 0.5 New Features 

Option for Downloading SD (jpeg) Low Quality or HD (PNG) High Quality Version of Images


Better Tracking of images that already downloaded, with a JSON File called downloaded_images.json in the same Folder as the script. The Scripts writes 
for SD Images with jpeg Ending
```
        "ImageID_SD": 
        "path": "image_downloads/civitAIuser/image.jpeg",
        "quality": "SD",
        "download_date": "YYYY-MM-DD - H:M"       
```
For HD Images with PNG Ending
```
        "ImageID_HD": {
        "path": "image_downloads/civitAIuser/Image.png",
        "quality": "HD",
        "download_date": "YYYY-MM-DD- H:M"
```
into it and checks before Downloading a Image. For Both Option, Model ID or Username


## 0.4 Added new Functions

Image Download with Model ID. Idea for it came from bbdbby 
The outcome looks sometimes chaotic a lot of folders with Modelnames you cant find on CivitAI. 
Because of renaming or Deleting the Models. But older Images have the old Model Names in the META data. 


Sort function to put the images and meta txt files into the right Model Folder. 
The sort Function relies on the Meta Data from the API for the images. Sometimes Chaos. 
Especially for models that have a lot of images.


Tracking of images that already downloaded with a text file called downloaded_images.txt in the same Folder as the script.
The Scripts writes the Image ID into  it and checks before Downloading a Image. 
For Both Option, Model ID or Username

Increased the timeout to 20

## 0.3 Added a new Function

It is writing the Meta Data for every image into a separate text file with  the ID of the image: ID_meta.txt.
If no Meta Data is available, the text file will have the URL to the image to check on the website.

Increased the timeout to 10

Added a delay between requests  
    
## 0.2 Updated with better error handling, some json validation and an option to set a timeout
