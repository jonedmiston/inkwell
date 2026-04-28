# inkwell

A .NET console app that transcribes a directory of images. Point it at a folder of scans, screenshots, or photos and it writes one `.txt` file per image with a faithful OCR-style transcription.

Two OCR engines are supported:

- **claude** (default): the [Anthropic C# SDK](https://github.com/anthropics/anthropic-sdk-csharp). High accuracy on messy/handwritten/structured pages, costs per image, requires an API key and network.
- **tesseract**: the [Tesseract](https://github.com/charlesw/tesseract) .NET wrapper. Free, fast, fully offline, best on clean typed/printed text. Requires language `.traineddata` files.

Terminal UI uses [Spectre.Console](https://spectreconsole.net/).

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download) or later
- For the **claude** engine: an Anthropic API key
- For the **tesseract** engine: at least one `.traineddata` file (see [Tesseract setup](#tesseract-setup))

## First-time setup (Claude engine)

### 1. Get a Claude API key

1. Sign in at https://console.anthropic.com/
2. Open **Settings -> API Keys** and click **Create Key**
3. Copy the key (it starts with `sk-ant-...`); you won't be able to view it again
4. Load some credits on **Settings -> Billing** if you haven't already

### 2. Provide the key to inkwell

Pick any **one** of these; they're tried in order, first match wins:

**Option A: `appsettings.local.json`** (recommended for local dev, git-ignored)

Copy the example and edit it:

```bash
cp appsettings.local.json.example appsettings.local.json
```

Then put your key in:

```json
{
  "ANTHROPIC_API_KEY": "sk-ant-your-key-here"
}
```

**Option B: .NET user secrets** (stored outside the repo, per-user)

```bash
dotnet user-secrets set "ANTHROPIC_API_KEY" "sk-ant-your-key-here"
```

**Option C: environment variable**

```bash
# Windows (PowerShell)
$env:ANTHROPIC_API_KEY = "sk-ant-your-key-here"

# macOS / Linux
export ANTHROPIC_API_KEY="sk-ant-your-key-here"
```

### 3. Build

```bash
dotnet build
```

## Tesseract setup

The `Tesseract` NuGet package ships the native engine binaries automatically (Windows x64/x86), so no system install is required. **Trained data files are auto-downloaded on first use** from [tessdata_best](https://github.com/tesseract-ocr/tessdata_best) (the most accurate variant). Just run `--engine tesseract` and inkwell will fetch any missing `<lang>.traineddata` into the tessdata directory.

The tessdata directory is resolved in this order:

- `--tessdata <path>` flag
- `TESSDATA_PREFIX` environment variable
- `TessDataPath` in `appsettings.local.json`
- `./tessdata` next to the executable (i.e. `bin/Debug/net9.0/tessdata/`) — created automatically if missing

If you'd rather provide files yourself, drop them into that folder before running and they'll be used as-is. Pass `--no-download` to disable auto-fetching entirely (e.g. for offline / locked-down environments).

To pick a different variant for downloads, pass `--tessdata-source <best|fast|main>`:

| Variant | Repo | English file size | Notes |
|---|---|---|---|
| `best` (default) | [tessdata_best](https://github.com/tesseract-ocr/tessdata_best) | ~15 MB | Highest accuracy, slowest |
| `fast` | [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) | ~4 MB | Fastest, lowest accuracy |
| `main` | [tessdata](https://github.com/tesseract-ocr/tessdata) | ~23 MB | Balanced; supports both legacy and LSTM modes |

For multiple languages on one image, pass `--language eng+fra` (etc.); each code is fetched independently.

## Usage

```bash
dotnet run -- <source-directory> [options]
```

Or after publishing:

```bash
inkwell <source-directory> [options]
```

### Interactive mode

If you launch inkwell with **no arguments at all** (and stdin is a terminal), it walks you through the most common settings — engine, source, output, recurse, skip-existing, then engine-specific options (book-mode, prompt file, model, effort, timeout for Claude; language for Tesseract). Useful when you don't want to memorize the flags.

```bash
inkwell
```

Pass any flag to bypass the interactive flow and use the regular CLI.

### Book mode

For multi-column books where you want a single continuous text file (not the original column layout), pass `-b` / `--book-mode`. inkwell swaps in a built-in prompt that tells Claude to read columns in reading order, merge them into a single flow, dehyphenate words split across lines, and skip page numbers / running headers. `--prompt` overrides this if you want a fully custom prompt.

### Options

| Option | Engine | Default | Description |
|---|---|---|---|
| `<source>` | both | required | Directory containing images to transcribe (positional) |
| `--engine <name>` | both | `claude` (or `DefaultEngine`) | `claude` or `tesseract` |
| `-o`, `--output <dir>` | both | `<source>/transcriptions` | Where to write the `.txt` files |
| `-r`, `--recurse` | both | off | Walk subdirectories. Output mirrors the source tree. |
| `-s`, `--skip-existing` | both | off | Skip images whose output `.txt` already exists (resume mode) |
| `--pdf-dpi <n>` | both | 300 | Render resolution for PDF pages |
| `-y`, `--yes` | both | off | Skip the confirmation prompt and start processing immediately |
| `--log <path>` | both | `./inkwell-<timestamp>.log` | Path for the run log file |
| `--no-log` | both | off | Disable the run log entirely |
| `-p`, `--prompt <file>` | claude | built-in OCR prompt | Path to a custom prompt file |
| `-b`, `--book-mode` | claude | off | Use the book-mode prompt: merges multi-column pages into single-flow text. Ignored if `--prompt` is set. |
| `-m`, `--model <id>` | claude | `DefaultModel`, else *interactive picker* | Claude model ID (e.g. `claude-opus-4-7`) |
| `-e`, `--effort <level>` | claude | `DefaultEffort`, else `high` | `low`, `medium`, `high`, or `max` |
| `-t`, `--timeout <seconds>` | claude | SDK default | Per-request HTTP timeout. Increase for large images / high effort. |
| `-l`, `--language <code>` | tesseract | `DefaultLanguage`, else `eng` | Language code(s), e.g. `eng`, `deu`, `eng+fra` |
| `--tessdata <path>` | tesseract | see [Tesseract setup](#tesseract-setup) | Path to tessdata directory |
| `--tessdata-source <variant>` | tesseract | `best` (or `TessDataSource`) | Source for auto-downloads: `best`, `fast`, or `main` |
| `--no-download` | tesseract | off | Disable auto-download of missing trained data files |

### Optional defaults in `appsettings.local.json`

You can skip frequent flags on every run by setting defaults in `appsettings.local.json`:

```json
{
  "ANTHROPIC_API_KEY": "sk-ant-your-key-here",
  "DefaultEngine": "claude",
  "DefaultModel": "claude-opus-4-7",
  "DefaultEffort": "high",
  "DefaultLanguage": "eng",
  "TessDataPath": "C:\\tools\\tessdata",
  "TessDataSource": "best"
}
```

Resolution order for each: CLI flag -> config value -> hard-coded default.

### Supported image formats

- **claude**: `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, `.pdf`
- **tesseract**: above plus `.tif`, `.tiff`, `.bmp` (anything Leptonica can read), `.pdf`

PDFs are rendered to PNG pages at the `--pdf-dpi` resolution (default 300) and OCR'd page-by-page; the resulting text is concatenated into a single `<name>.txt`.

### Confirmation prompt

When run interactively, inkwell prints a settings summary and waits for you to confirm before processing. Pass `-y`/`--yes` to skip the prompt (e.g. for scripted batch jobs). When stdin isn't a TTY (piped input, scheduled tasks, etc.) the prompt is skipped automatically.

### Run log

By default each run writes a log file to the current directory: `inkwell-<yyyyMMdd-HHmmss>.log`. The log records:

- The run settings as a `# key: value` header
- One line per file with status, elapsed time, character count, and (for PDFs) page count: `OK   foo.pdf  elapsed=10264ms  chars=3290 pages=3`
- One line per failure with the error message: `FAIL bar.webp  elapsed=42ms  error=...`
- Native warnings printed to stderr by Tesseract / Leptonica / PDFium (e.g. `Error in boxClipToRectangle: box outside rectangle`) are captured into the same file via an OS-level stderr redirect

Use `--log <path>` to point the log somewhere else, or `--no-log` to disable it entirely.

### Examples

Default Claude transcription, prompts for a model:

```bash
dotnet run -- ./scans
```

Explicit Claude model and custom output directory:

```bash
dotnet run -- ./scans -o ./out -m claude-opus-4-7
```

Custom prompt, max effort:

```bash
dotnet run -- ./scans -p ./my-prompt.txt -e max -m claude-sonnet-4-6
```

Tesseract, English (uses `./tessdata` next to the binary):

```bash
dotnet run -- ./scans --engine tesseract
```

Tesseract, German + French, custom tessdata path:

```bash
dotnet run -- ./scans --engine tesseract -l deu+fra --tessdata C:\tools\tessdata
```

Resume a long batch (skip files already transcribed), recursing into subfolders:

```bash
dotnet run -- ./scans --engine tesseract -r --skip-existing
```

### Output

For each image `foo.png` in the source directory, inkwell writes `foo.txt` into the output directory. With `--recurse`, the source subdirectory structure is mirrored under the output directory (`scans/2024/jan/foo.png` -> `<output>/2024/jan/foo.txt`). The built-in Claude prompt asks for plain text that preserves the original layout via whitespace; no Markdown syntax is added. Tesseract also returns plain text. Existing files are overwritten unless `--skip-existing` is set.

## How it works

- **claude**: each image is base64-encoded and sent to the Messages API as an `image` content block alongside the prompt. The model's text response is written verbatim.
- **tesseract**: each image is loaded into a Leptonica `Pix`, processed by an in-process Tesseract engine initialized once for the run, and the resulting text is written verbatim.

## Notes

- **Cost (claude):** each image is one API call. Image size and selected model drive the cost. See [Anthropic pricing](https://www.anthropic.com/pricing).
- **Cost (tesseract):** zero. Runs locally on CPU.
- **Effort (claude):** `high` (default) is usually the sweet spot. Use `max` for tricky scans or dense handwriting; `low` for clean typed text where you want to save tokens.
- **Prompt file (claude):** the built-in prompt is tuned for typed/printed text. If your images contain handwriting, tables, or other structured content, a custom prompt often helps.
- **Accuracy:** Claude usually wins on noisy scans, handwriting, multi-column layouts, and tables. Tesseract is competitive (and free) on clean typed text and scanned books.
