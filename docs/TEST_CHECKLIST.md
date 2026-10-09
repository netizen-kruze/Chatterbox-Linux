# Chatterbox for Linux — live test checklist

Run against a live VRChat client under Steam Proton (OSC enabled: Action
Menu → Options → OSC). The dev build is
`src/Chatterbox/bin/Debug/net9.0/Chatterbox`; the shippable artifact is
`releases/Chatterbox-<version>-linux-x64` (one self-contained file — the
version is also shown in-app under Settings → About). Don't run another chatbox writer (any other STT tool)
at the same time as captions here — one chatbox writer at a time.

## A. First run & models

Simulate a fresh machine: quit Chatterbox, move `~/.local/share/Chatterbox`
and `~/.config/Chatterbox` away (restore after this section).

- [ ] Launch: first-run screen shows (two choice cards), Start is disabled,
      status chip reads "no model installed"-equivalent (idle, grey dot).
- [ ] **Quick start**: downloads VAD then tiny.en-q5 in sequence with one
      progress bar; when done, the captions screen replaces first-run and
      Start enables (engine set to Whisper automatically). Total ≈33 MB.
- [ ] **Best quality is GPU-aware**: on an NVIDIA machine the card offers
      Whisper turbo-q5 + the CUDA pack and sets the Whisper engine;
      captions run (CPU) as soon as the model lands, full speed after a
      restart (the pack brings its own CUDA 13 runtime; ~587 MB in all).
      On a non-NVIDIA machine it offers Parakeet.
- [ ] Models screen: hardware banner shows your tier + recommendation;
      the q5 trio is listed; downloading a model shows live progress +
      Cancel; Cancel actually stops it; Delete removes and the row returns
      to Download.
- [ ] **Verify installed files**: toast reports all files verified. Corrupt
      test (optional): append a byte to a model file → Verify names it.
- [ ] GPU pack (NVIDIA): one progress bar covers two archives (~144 MB
      whisper build + ~443 MB CUDA runtime; the runtime is skipped when a
      system CUDA 13 runtime exists). Afterwards `runtimes/cuda/linux-x64/`
      holds libggml-cuda-whisper.so, libcudart.so.13, libcublas.so.13,
      libcublasLt.so.13 and NVIDIA-CUDA-LICENSE.txt; the toast says restart
      to activate. After restart the Whisper status chip shows `Cuda` and
      `last_boot.log`'s `cuda:` line ends in `bundled CUDA 13 runtime loaded`.
- [ ] Parakeet engine pack: downloads into
      `~/.local/share/Chatterbox/runtimes/linux-x64/`; Start on Parakeet
      works immediately, no restart.
- [ ] Models → **In use** (moved from Settings in 1.7.0): the engine switch
      and the Whisper model picker change the Active badge below and
      persist across a restart.
- [ ] Restore your real folders.

## B. Captions core

- [ ] Start captions → speak: committed words solid, still-changing words
      dim; previous sentence folds to the smaller dim line above.
- [ ] The same text appears in your in-game chatbox; "in-game window N/144"
      counter updates.
- [ ] Meter strip moves while speaking; Listening pulse shows during
      speech, "Waiting for speech" between.
- [ ] Microphone picker: entry 0 is "System default input"; the other
      entries are PipeWire's capture devices by description; picking one
      restarts a running session on it (`pw-record --target` in the boot
      log's capture line). Unplug a chosen USB mic and start again → the
      default input is used (name re-resolution).
- [ ] Pause ~3 s → next sentence on a new chatbox line. Stay quiet ≥30 s
      (bubble fades) → next speech starts a fresh window, old text gone.
- [ ] Stop captions → in-game chatbox clears, typing indicator off, the
      `pw-record` process is gone (`pgrep pw-record`).
- [ ] Settings: engine Parakeet↔Whisper (takes effect next start), whisper
      model picker honors an explicit choice, update rate change applies
      to a running session, typing indicator toggles live.
- [ ] Unplug/disable the mic mid-session, or `systemctl --user restart
      pipewire` → captions stop with the "microphone capture failed" toast
      (not silent dead air).
- [ ] Start captions with a large Whisper model → the button reads
      "Loading model…" and the window stays responsive (drag it, switch
      sections) until the header flips to the engine name.
- [ ] While talking, the header pace chip reads "keeping up" (green). On a
      slow machine — or Whisper turbo forced onto the CPU — it turns red
      ("falling behind N× · M s late") and after ~10 s a toast offers the
      one-click fix (Switch to Parakeet / Use tiny.en / Open Models /
      Restart Chatterbox); pressing it restarts captions on the new engine.
      `last_boot.log` gets a "recognition falling behind" line and, at
      Stop, a "captions session ended" summary with pass counts and lag.
- [ ] Talk continuously for a minute → captions keep flowing with a steady
      lag (the pace chip stays green on capable hardware), no forced break
      every 28 s, and no word is lost or repeated where the window was
      trimmed (listen for the sentence around 10–15 s in).
- [ ] CPU-only Whisper (no GPU pack): a paragraph of speech transcribes as
      accurately as before — that runtime uses a shortened encoder context
      and no temperature fallback (WhisperNetEngine), and windows are capped
      at 18 s.
- [ ] Whisper on an NVIDIA machine without the GPU pack (or before the
      restart that activates it) → a red toast at Start says the card is
      idle, with a one-click "Open Models" / "Restart Chatterbox" action;
      with the pack's natives but no runtime (an install from before 1.5.1)
      the toast sends you back to the Models screen to fetch it. With the
      NVIDIA kernel module loaded but `libcuda.so.1` missing, the Models
      banner and the toast name `xorg-x11-drv-nvidia-cuda-libs`.

## C. Watched players & auto-start

- [ ] Players screen shows the live "In your instance" list with uids;
      the world name updates when you travel. `last_boot.log`'s
      "vrchat log dir" line names the Proton prefix folder and says
      "found".
- [ ] **Watch** on an instance player adds them (with uid); ✕ removes.
- [ ] Manual add by display name shows "name only — id fills in when
      seen"; when that player next joins, the uid appears on the row.
- [ ] Auto-start ON + watched player joins you → captions start with the
      "— {name} joined" toast. Last watched player leaves → captions stop.
- [ ] You travel to a world with a watched player already there → captions
      start within ~5–15 s. Travel to a world with none → captions stop
      ("no watched players in this world" — not "{name} left").
- [ ] Launch Chatterbox while already in an instance with a watched player →
      the window opens normally and captions start shortly after launch with
      the "— {name} is here" toast (boot reconcile).
- [ ] Manual Stop while a watched player is present → stays stopped until
      they re-join or you change worlds.
- [ ] Quit VRChat during an auto session → captions stop within ~15 s (the
      `VRChat.exe` process is the authority, not the log).

## D. Window, instances, Steam

- [ ] Close (✕) → the app quits: chatbox cleared, mic released, process
      gone. Minimize instead → captions keep running (verify in-game).
- [ ] Launching Chatterbox again while running → no second window, no
      error (single instance).
- [ ] Steam launch options `…/Chatterbox %command%` → VRChat starts with
      its normal command line, the Steam overlay works in-game, Chatterbox
      is up with a rendered page (no blank window from the overlay
      preload), and it exits by itself when VRChat closes.
- [ ] Crash recovery: with Chatterbox closed, create an empty
      `~/.local/share/Chatterbox/boot.inprogress`, then launch → a red
      toast says the last run "never reached the window", captions do NOT
      auto-start (even with a watched player present), `last_boot.log`
      has the "previous run … never reached the window" line and
      `error.log` a "PreviousStart" entry (with the coredumpctl/journal
      lines when a record exists). The launch after that is normal again.
- [ ] Crash DURING captions (1.7.2: the marker lives for the whole run):
      start captions, then `kill -9` the process → the next launch toasts
      "ended while captions were running", does not auto-start, and logs
      the SAFE BOOT line; while running, `boot.inprogress` ends in
      `|window` when idle and `|captions` during a session, and is gone
      after a clean close. `kill -9` of an IDLE window → the next launch
      logs "ended without a clean exit while idle" and collects the crash
      record, but auto-start works as normal (no safe boot).

## E. Coexistence

- [ ] Another VRChat companion app running at the same time: both apps
      stable; Chatterbox captions work alongside it. (Keep any other
      chatbox writer disabled so the two don't fight over the window.)
- [ ] VRChat holds the mic throughout — game voice unaffected (PipeWire
      shares the input).

## H. Translation (1.7.x)

- [ ] The Translate tab shows whether the model and engine are installed
      (an **Open Models** button when they are not) and the last translated
      sentence once captions run.
- [ ] Models → Components lists the Hy-MT2 translation model (1.1 GB), the
      Translation engine (36 MB) and GPU acceleration for translation
      (Vulkan, 21 MB); each downloads with progress, Cancel works, and
      **Verify installed files** covers the pack libraries. Afterwards
      `~/.local/share/Chatterbox/runtimes/linux-x64/native/` holds
      `avx/`, `avx2/`, `avx512/`, `noavx/` (and `vulkan/`) with
      libggml-base.so, libggml-cpu.so (libggml-vulkan.so), libggml.so and
      libllama.so — no libmtmd.so.
- [ ] Translate tab: switch on, "Translate into" Japanese, Start, speak a
      sentence and pause → the chatbox shows the Japanese sentence (check
      CJK renders in-game over OSC), the Captions page shows it under your
      words in amber, and `last_boot.log` has a "translation: … loaded in
      N ms (CPU, T threads | Vulkan GPU) → Japanese" line.
- [ ] Vulkan pack on a machine without a usable Vulkan driver (no
      `libvulkan.so.1`, or `vulkaninfo` fails): the load falls back to the
      CPU build (`WithAutoFallback`) or reports "Translation unavailable"
      in a toast — never a crash, and captions keep running untranslated.
- [ ] "Show the original too" → chatbox shows "translation (original)".
- [ ] Translation on with the model or engine pack missing → Start still
      works, captions run untranslated, a toast with **Open Models** says
      why. Same when the model fails to load.
- [ ] Switching translation off while captions run stops translating at
      the next sentence; on again resumes (the model loads once, 1–3 s).
- [ ] Stop frees the model (`ps -o rss` on the process: drops by about
      1.2 GB).
- [ ] A copy run from a read-only folder (/opt): the packs still install,
      because they go to the data folder, not beside the binary.
- [ ] GPU pack truthfulness (1.7.2): with the Vulkan pack installed but
      `vulkaninfo` absent (`sudo dnf remove vulkan-tools`, or run with
      `PATH` lacking it), the Translate banner says the pack is installed
      but names `vulkan-tools`, the pack's install toast says the same,
      Start toasts "Translation runs on the CPU — …", and `last_boot.log`'s
      `translation:` line ends in "CPU (avx2…)" with "GPU pack not used".
      With `vulkan-tools` installed the line says "Vulkan GPU" and the
      "llama loader:" lines name `…/native/vulkan/libllama.so`. A pack
      installed while a translator had already loaded in this run → the
      toast and banner say "restart Chatterbox to use it".

## G. Other machines (any Linux desktop — no VRChat session needed)

- [ ] `tools/smoke.sh <path to the unpacked Chatterbox binary>` →
      "SMOKE TEST PASSED" (boots against a throwaway data folder with a
      watched player already present).
- [ ] `tools/scenarios.sh <binary>` → "SCENARIOS: ALL PASSED" (about five
      minutes: second launch refused, SIGTERM exit, crash recovery, missing
      and empty VRChat log, Steam wrapper mode, web-process crash reload —
      each in its own temporary home; `xvfb-run -a` works without a
      desktop).
- [ ] Settings → Speed check → Run → a row per installed engine. The
      verdict matches how captions feel live: *fast* ≈ a second behind,
      *usable* two to three, *too slow* = falling behind. `bench.log`
      holds the same rows plus the machine line.
- [ ] `Chatterbox --bench` from a terminal prints the same table and exits 0
      (no window; works over SSH with no display).
- [ ] `last_boot.log` starts with a `machine:` line naming the CPU, GPU,
      distribution ("Fedora Linux 44 …"), kernel and desktop session
      (e.g. "GNOME on wayland") correctly.
- [ ] Fresh Fedora without `webkit2gtk4.1` (or `gtk3`/`libnotify`): the
      launch shows a dialog naming the `dnf install` command and exits
      with code 1 — no crash, no blank window.
- [ ] A machine without `pw-record` but with `parec` → captions still run
      (boot log's capture line says parec).
- [ ] Disconnect the network in the middle of a model download → it fails
      within about a minute with "no data received", never hangs forever.
- [ ] A filesystem with less free space than the model needs → the
      download refuses up front with a "not enough space" message.
- [ ] A CPU without AVX2/FMA (1.7.2) → `last_boot.log` says "whisper
      natives: this CPU lacks AVX2/FMA — the no-AVX build … is used",
      `~/.local/share/Chatterbox/runtimes/noavx/linux-x64/` holds four
      libraries, and Start works on BOTH engines (Whisper slowly, with
      the loader line naming `CpuNoAvx`). Before 1.7.2 not even Parakeet
      started there: the voice detector loads through whisper.cpp.

## F. Release file (ideally on the second machine)

- [ ] Download `Chatterbox-<version>-linux-x64`, `chmod +x` it and run it
      from ~/Downloads → the window opens; Settings → About shows the
      matching version and the Read me / License / Third-party notices
      expand (and their text is selectable/copyable).
- [ ] Settings → **Add to app grid** → the toast names the install path
      and the Steam launch-options line, the row flips to "Installed at
      …", the app grid shows Chatterbox with its icon, and launching from
      there runs `~/.local/share/Chatterbox/app/Chatterbox`. The same from
      a terminal: `./Chatterbox-<version>-linux-x64 --install`.
- [ ] First launch creates `~/.local/share/Chatterbox/runtimes/linux-x64/`
      (the bundled whisper/VAD natives, written at boot — never beside the
      binary); pressing Start then actually produces captions — this is
      the single-file native-loading regression test. Launched from a
      read-only folder (`/opt`) it is exactly the same (boot log's
      "runtimes:" line names the data folder).
- [ ] No VRChat running → Players shows "VRChat is not running"; app is
      otherwise fine.
- [ ] VRChat running with its Settings → Debug → Logging off → about 30 s
      after VRChat starts, Players shows "VRChat's log is empty" with the
      Logging instructions, and `last_boot.log` gets a "vrchat log: still
      empty" line. Turn Logging on and rejoin the world → the players list
      fills in and the boot log adds "vrchat log: being written now".
- [ ] `--vrchat-log-dir /nonexistent` with VRChat running → about 30 s
      later Players shows "VRChat's log folder wasn't found", and the boot
      log's "vrchat log: log folder not found" line names that folder.
- [ ] Update simulation: run a newer download and press Add to app grid
      again while the installed copy is still running → the installed file
      is replaced (rename over, no "text file busy"); settings, watched
      players and models survive, and the Parakeet engine / CUDA packs
      under `runtimes/` keep working.
- [ ] Update from 1.5.3 or older (1.5.4 moved the voice detector to Silero
      VAD v6.2.0): with `ggml-silero-v5.1.2.bin` but no
      `ggml-silero-v6.2.0.bin` in `~/.local/share/Chatterbox/models/stt/`,
      launch → `last_boot.log` gets "voice detector: Silero VAD v6.2.0 not
      installed (found ggml-silero-v5.1.2.bin) — downloading 864 KB in the
      background" and then "downloaded and verified; removed
      ggml-silero-v5.1.2.bin"; a toast says the same, the captions screen
      shows normally (no first-run cards), and Start / the boot auto-start
      work. Offline, Start fails with the "Captions need the voice detector
      … couldn't be downloaded" toast — never a "model not found" error.
- [ ] Settings → Updates (1.6.0+): a build with `<UpdateRepository>` set
      says "You have the latest version (x.y.z)" after Check for updates
      (one request to api.github.com); a build without it shows the greyed
      "no update source configured" line and never goes online. "Check
      when Chatterbox starts" persists (`CheckUpdatesAtStartup` in
      stt_settings.json) and is on by default.
- [ ] Update flow, offline-safe: serve a fake release with
      `python3 -m http.server` — a `latest.json` in GitHub's release shape
      (tag `vX.Y.Z` above the build, an asset named
      `Chatterbox-X.Y.Z-linux-x64`, notes containing
      `SHA-256: <hash of that file>`) — and launch a copy of the app with
      `--update-url http://127.0.0.1:8000/latest.json --data-dir <throwaway>`.
      Check → "Version X.Y.Z is available"; Update now → progress bar,
      "Installing…", Chatterbox exits and comes back by itself (1.7.2: the
      file is swapped by a helper after the old process is gone, never
      underneath it); the new instance toasts "Chatterbox updated from …
      to X.Y.Z", `last_boot.log` carries the "update:" lines, the file
      under the old name is executable (`ls -l`) and no `Chatterbox.old` /
      `Chatterbox.new` is left beside it. Run from the app-grid install
      AND from a copy in ~/Downloads: each replaces its own path. A wrong
      SHA-256 in the notes → "SHA-256 mismatch", nothing replaced. Update
      now while captions run → "Stop captions before updating". From a
      read-only folder (`/opt`) → "Update failed — … is not writable", the
      app keeps running.
- [ ] The same from a terminal (1.7.2): `Chatterbox --update --update-url
      http://127.0.0.1:8000/latest.json --data-dir <throwaway>` prints the
      download progress and "… takes the place of <path> as soon as this
      command exits"; a second later `ls -l` shows the new file under the
      old name and `Chatterbox.old` beside it, and the next start of that
      file logs "update: updated from … to X.Y.Z" and removes `.old`.
      Already current → "You have the latest version (…)".
- [ ] `~/.local/share/Chatterbox/app/Chatterbox --uninstall` — run from
      the installed copy itself, as the README says — prints its message,
      exits 0 with no "Unhandled exception" (1.7.1 aborted here), and
      removes the app folder and the grid entry while keeping the data;
      `--uninstall --purge` removes the data, `~/.config/Chatterbox` and
      `~/.net/Chatterbox*` too. `Chatterbox --help` lists the switches.

---
Record results here with date + commit.

## Stability (tools/soak.sh, or by hand)

- [ ] **Soak**: `tools/soak.sh <binary> 20` (the bundled clip fed in a loop
      through `--capture-command "python3 tools/feed.py …"`, auto-start
      armed, models borrowed from the real folder) runs continuous speech
      for 20+ minutes and ends with "SOAK PASSED"; the `memory after N
      passes:` lines in `last_boot.log` stay flat (RSS within a few tens
      of MB of the first line, gen2 count not climbing by the minute); no
      `error.log` entries.
- [ ] **Recorder dies mid-session** (kill pw-record/parec or the feeder):
      within ~5 s a red toast says captions stopped; Start works again;
      an auto-started session comes back by itself once the device is.
- [ ] **Engine pass throws** (no by-hand trigger: both engines hold the
      model in memory, so deleting its file changes nothing — the unit
      test AWorkerExceptionIsReportedNotSwallowed covers the path; if it
      ever happens live): the session stops with "recognition failed
      (...)" instead of staying "running" in silence.
- [ ] **Stop during a long pass** on a slow CPU (Parakeet): the app never
      crashes; the log may say "engine disposal deferred".
- [ ] **Sustained overload** (large model on a weak CPU): captions lag but
      never more than roughly window + 10 s; the log shows
      `skipped N s of audio to catch up` rather than minutes of lag.
- [ ] **SIGTERM / Ctrl+C / Steam Stop**: the chatbox is cleared and the
      session summary is written (`exit requested by SIGTERM` in the log).
- [ ] **Second launch** shows a "Chatterbox is already running" notification
      and exits.
- [ ] **Download retry continues where it stopped**: cut the network in
      the middle of a model, Parakeet engine or GPU pack download → the
      toast ends in "(a retry continues where it stopped)" and the
      `.partial` / `.download` file stays; download again → it continues
      and verifies. Cancel instead → the file is removed; Delete on the
      Models screen also removes a leftover partial. The GPU pack starts
      even when `/tmp` (RAM on Fedora) is smaller than the pack.
- [ ] **Data folder deleted to reset the app**: quit, delete
      `~/.local/share/Chatterbox`, start → first-run screens at once
      (`last_boot.log`: settings from "first run", no provisional wait),
      even though `~/.config/Chatterbox/last-run-version` says it ran
      before.
