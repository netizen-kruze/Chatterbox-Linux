#!/usr/bin/env python3
"""Feeds a WAV file to stdout as raw 16 kHz mono s16le PCM at real-time pace,
over and over — the microphone stand-in for a soak run:

    Chatterbox --capture-command "python3 tools/feed.py src/Chatterbox/Bench/fixture.wav"

The file must already be 16 kHz, mono, 16-bit (the bundled speech clip is).
A gap of silence is inserted between repeats so every repeat ends a
sentence the way a pause would. Exits quietly when the reader goes away.
"""
import sys
import time
import wave

BYTES_PER_SECOND = 16000 * 2
CHUNK_SECONDS = 0.1
GAP_SECONDS = 1.5


def main() -> int:
    if len(sys.argv) < 2:
        sys.stderr.write("usage: feed.py <file.wav> [repeats]\n")
        return 2
    path = sys.argv[1]
    repeats = int(sys.argv[2]) if len(sys.argv) > 2 else 0   # 0 = forever
    try:
        with wave.open(path, "rb") as w:
            if (w.getnchannels(), w.getframerate(), w.getsampwidth()) != (1, 16000, 2):
                sys.stderr.write(f"feed.py: {path} must be 16 kHz mono 16-bit "
                                 f"(it is {w.getnchannels()} ch, {w.getframerate()} Hz, "
                                 f"{w.getsampwidth() * 8}-bit)\n")
                return 2
            pcm = w.readframes(w.getnframes())
    except (OSError, EOFError, wave.Error) as ex:
        sys.stderr.write(f"feed.py: cannot read {path} as a WAV file: {ex}\n")
        return 2
    if not pcm:
        sys.stderr.write(f"feed.py: {path} holds no audio\n")
        return 2
    gap = b"\0" * int(GAP_SECONDS * BYTES_PER_SECOND)
    out = sys.stdout.buffer
    chunk = int(CHUNK_SECONDS * BYTES_PER_SECOND)
    start = time.monotonic()
    sent = 0
    done = 0
    try:
        while repeats == 0 or done < repeats:
            for buf in (pcm, gap):
                for i in range(0, len(buf), chunk):
                    out.write(buf[i:i + chunk])
                    out.flush()
                    sent += len(buf[i:i + chunk])
                    # Pace to real time: sleep until this many bytes are due.
                    due = start + sent / BYTES_PER_SECOND
                    delay = due - time.monotonic()
                    if delay > 0:
                        time.sleep(delay)
            done += 1
    except (BrokenPipeError, KeyboardInterrupt):
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
