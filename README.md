# inkwell

A .NET console app that transcribes a directory of images using the Claude API. Point it at a folder of scans, screenshots, or photos and it writes one Markdown file per image with a faithful OCR-style transcription.

Uses the official [Anthropic C# SDK](https://github.com/anthropics/anthropic-sdk-csharp) and [Spectre.Console](https://spectreconsole.net/) for a nice terminal UI.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download) or later
- An Anthropic API key

## First-time setup

### 1. Get a Claude API key

1. Sign in at https://console.anthropic.com/
2. Open **Settings → API Keys** and click **Create Key**
3. Copy the key (it starts with `sk-ant-...`) — you won't be able to view it again
4. Load some credits on **Settings → Billing** if you haven't already

### 2. Provide the key to inkwell

Pick any **one** of these — they're tried in order, first match wins:

**Option A — `appsettings.local.json`** (recommended for local dev, git-ignored)

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

**Option B — .NET user secrets** (stored outside the repo, per-user)

```bash
dotnet user-secrets set "ANTHROPIC_API_KEY" "sk-ant-your-key-here"
```

**Option C — environment variable**

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

## Usage

```bash
dotnet run -- <source-directory> [options]
```

Or after publishing:

```bash
inkwell <source-directory> [options]
```

### Options

| Option | Required | Default | Description |
|---|---|---|---|
| `<source>` | ✅ | — | Directory containing images to transcribe (positional) |
| `-o`, `--output <dir>` | | `<source>/transcriptions` | Where to write the `.md` files |
| `-p`, `--prompt <file>` | | built-in OCR prompt | Path to a custom prompt file |
| `-m`, `--model <id>` | | `DefaultModel` from config, else *interactive picker* | Claude model ID (e.g. `claude-opus-4-7`) |
| `-e`, `--effort <level>` | | `DefaultEffort` from config, else `high` | `low`, `medium`, `high`, or `max` |
| `-r`, `--recurse` | | off | Walk subdirectories. Output mirrors the source tree. |
| `-t`, `--timeout <seconds>` | | SDK default | Per-request HTTP timeout. Increase for large images / high effort. |

### Optional defaults in `appsettings.local.json`

You can skip `--model` and `--effort` on every run by setting defaults in `appsettings.local.json`:

```json
{
  "ANTHROPIC_API_KEY": "sk-ant-your-key-here",
  "DefaultModel": "claude-opus-4-7",
  "DefaultEffort": "high"
}
```

Resolution order for each: CLI flag → config value → hard-coded default (`high` for effort, interactive picker for model).

### Supported image formats

`.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`

### Examples

Transcribe every image in `./scans`, using the default prompt and prompting for a model:

```bash
dotnet run -- ./scans
```

Explicit model and custom output directory:

```bash
dotnet run -- ./scans -o ./out -m claude-opus-4-7
```

Custom prompt, max effort:

```bash
dotnet run -- ./scans -p ./my-prompt.txt -e max -m claude-sonnet-4-6
```

### Output

For each image `foo.png` in the source directory, inkwell writes `foo.txt` into the output directory. With `--recurse`, the source subdirectory structure is mirrored under the output directory (`scans/2024/jan/foo.png` → `<output>/2024/jan/foo.txt`). The built-in prompt asks Claude for plain text that preserves the original layout via whitespace — no Markdown syntax is added. Existing files are overwritten.

## How it works

For each image, inkwell reads the bytes, base64-encodes them, and sends them to the Claude Messages API as an `image` content block alongside the prompt. The model's Markdown response is written verbatim to disk.

## Notes

- **Cost:** each image is one API call. Image size and selected model drive the cost. See [Anthropic pricing](https://www.anthropic.com/pricing).
- **Effort:** `high` (default) is usually the sweet spot. Use `max` for tricky scans or dense handwriting; `low` for clean typed text where you want to save tokens.
- **Prompt file:** the built-in prompt is tuned for typed/printed text. If your images contain handwriting, tables, or other structured content, a custom prompt often helps.
