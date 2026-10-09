# Translation model bench — 2026-10-08

Which small open-weight translator should Chatterbox run locally? The brief
was "fastest, most accurate": a finished caption sentence must come back
translated well before the next one ends, on machines with and without a
GPU, under a license that lets an open-source app download the model for
its users.

## Setup

- 20 VRChat-style English sentences (greetings, world hopping, avatar
  compliments, "I'm deaf, so I read what you say from the captions", mic
  trouble, goodbyes), translated into Japanese, Korean, Spanish, German and
  Chinese.
- Runner: llama.cpp through LLamaSharp 0.27.0 — the binding the app ships —
  greedy decoding, one sentence per request, 1024-token context, each
  model's own chat template and documented translation instruction.
- Machine: Ryzen 7 9800X3D (CPU runs pinned to 8 threads to resemble a
  mid-range desktop), GeForce RTX 5080 (CUDA and Vulkan runs).
- Quality was judged by reading the output, not by an automatic metric:
  meaning preserved, natural register, nothing invented or dropped.

## Candidates

| Model | Size (Q4) | License | Notes |
|---|---|---|---|
| Tencent Hy-MT2 1.8B | 1.1 GB | Apache-2.0 | translation specialist, 33 languages |
| Google TranslateGemma 4B | 2.5 GB | Gemma terms (gated download) | translation specialist, 55 languages |
| Qwen3.5 2B | 1.3 GB | Apache-2.0 | general model, thinking disabled by prefill |
| Google MADLAD-400 3B | 2.0 GB | Apache-2.0 | encoder-decoder (T5) |

## Results (average per sentence over 20, Japanese target)

| Model | CUDA | Vulkan | CPU, 8 threads | Quality |
|---|---:|---:|---:|---|
| Hy-MT2 1.8B Q4 | 79 ms | 144 ms | 531 ms | good: natural ja/ko/es/de/zh, meaning kept; small slips (one odd transliteration of "hangout") |
| Hy-MT2 1.8B Q8 | 103 ms | — | — | same meaning, slightly more polished phrasing; 1.9 GB |
| TranslateGemma 4B Q4 | 1 588 ms* | — | 1 023 ms | very good, formal register; the slowest by far |
| Qwen3.5 2B Q4 | 69 ms | — | 455 ms | fast but unreliable: "I'm deaf" came back as "I'm not blind" (es); meaning dropped in ja |
| MADLAD-400 3B | — | — | — | would not load in LLamaSharp (encoder-decoder architecture) |

\* measured while model downloads were running in the background and not
repeated; the Hy-MT2 CUDA run under the same load gave 370 ms against the
79 ms above, so TranslateGemma's idle figure is likely several hundred
milliseconds. Even so it is the slowest candidate on CPU, where the
difference matters most.

Qwen3.5 must be told not to think: without the empty-thought prefill it
spent 2.6 s per sentence reasoning about the request.

## Decision

**Hy-MT2 1.8B, Q4_K_M**, revision-pinned on Hugging Face
(tencent/Hy-MT2-1.8B-GGUF, commit a0c709d9…). It is the only candidate that
is fast on CPU, accurate, and under a license with no strings for this
app. The runtime is llama.cpp via LLamaSharp (MIT); its natives arrive as
two on-demand packs — CPU (36 MB, four instruction-set variants) and Vulkan
(20 MB, any GPU vendor). The 530 MB CUDA build of the same runtime was
rejected: Vulkan is within 2× of it for a model this small, at 4% of the
download.

Noted for later: Q8 as a quality option for GPU users; a translation row
in the speed check; translating other players' speech (loopback capture),
which is a separate feature.

## Linux note (1.7.x port)

The bench above was run on the Windows build. The Linux port uses the same
runtime version (LLamaSharp 0.27.0's llama.cpp) from the Linux backend
packages — `llamasharp.backend.cpu` (the same package as Windows; its
linux-x64 `.so` files) and `llamasharp.backend.vulkan.linux` — installed
under `~/.local/share/Chatterbox/runtimes/linux-x64/native/<variant>/`.

Checked on 2026-10-08 in WSL2 (Fedora 44, same Ryzen 7 9800X3D, model on
the Linux filesystem, 8 threads) with a throwaway probe against the app's
own classes: the CPU pack downloaded and verified (16 files), the loader
picked the `avx512` variant from the data folder, the model loaded in
1.6 s, and three sentences came back correct — Japanese 4.9 s (a long
sentence, 23 words), Spanish 1.9 s, German 1.6 s. Those times are 3–4×
the Windows CPU figures; WSL2 is not a native Linux measurement, so a real
desktop number is still owed. The Vulkan pack was not run: WSL has no
Vulkan device.
