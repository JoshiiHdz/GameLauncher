# WebP fixtures

Real WebP files (encoded by libwebp through Pillow) embedded into `GameLauncher.Tests` and used by
`ProviderWebpTests`. They are checked in so the tests never depend on a codec being present to *produce* them.

Regenerate with `python generate_fixtures.py` (from this directory; needs Pillow with WebP support). The tests assert
on decoded dimensions and behaviour, never on the bytes, so a regeneration under a different encoder version is safe.

| File | What it is | Why |
|---|---|---|
| `cover-600x900-lossy.webp` / `-lossless.webp` / `-alpha-lossless.webp` | Normal cover-shaped images | WebP is decoded, validated, cached and served (codec present) |
| `tiny-1x1-lossless.webp` | 1x1 lossless | Smallest valid image; cross-checks the codec probe |
| `too-wide-9000x100.webp`, `too-tall-100x9000.webp` | ~38-byte files that *declare* >8000px | The dimension bound applies to WebP |
| `at-limit-8000x50.webp` | Exactly 8000px wide | The bound is not off by one |
| `animated-2-frames-120x180.webp` | Two frames | Only frame 0 is shown |
| `truncated-lossy.webp` | 40% of the lossy cover | The Windows decoder reads the full header, then "decodes" it to **1x1 without error** - rejected by the faithful-decode check |
| `riff-header-only.webp` | A RIFF/WEBP header and nothing else | Rejected whatever the codec |

Tests marked `[FactRequiresWebpCodec]` run only where Windows' WebP Image Extensions are installed and are reported as
**Skipped** (never passed vacuously) elsewhere; `[FactRequiresNoWebpCodec]` tests pin the unsupported-codec behaviour and
are skipped where the codec exists.
