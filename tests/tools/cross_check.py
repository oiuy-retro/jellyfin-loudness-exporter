#!/usr/bin/env python3
"""Compare the exporter's jle-audio-v1 results with the Python reference.

Hashes every fixture in tests/fixtures/audio/ plus mutated copies (truncated,
bytes changed near the start or end, bytes inserted or deleted) with both
implementations and reports any disagreement. Offline; writes only to a
temporary directory that is removed afterwards.

Usage: cross_check.py EXPORTER [COPIES_PER_FIXTURE]
  EXPORTER is jellyfin-loudness-exporter.dll (run with `dotnet`) or the executable.
"""
import os
import random
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import jle_audio_hash as ref  # noqa: E402

FIXTURES = os.path.join(HERE, "..", "fixtures", "audio")


def mutate(rnd, data):
    b = bytearray(data)
    op = rnd.randrange(5)
    if op == 0 and len(b) > 1:
        return bytes(b[:rnd.randrange(len(b))])
    if op == 1:
        for _ in range(rnd.randrange(1, 4)):
            i = rnd.randrange(min(len(b), 300)) if rnd.random() < 0.6 else rnd.randrange(len(b))
            b[i] = rnd.randrange(256)
    elif op == 2:
        for _ in range(rnd.randrange(1, 4)):
            b[max(0, len(b) - 1 - rnd.randrange(min(len(b), 300)))] = rnd.randrange(256)
    elif op == 3:
        i = rnd.randrange(len(b))
        b[i:i] = bytes(rnd.randrange(256) for _ in range(rnd.randrange(1, 20)))
    else:
        i = rnd.randrange(len(b))
        del b[i:min(len(b), i + rnd.randrange(1, 40))]
    return bytes(b)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    exe = sys.argv[1]
    copies = int(sys.argv[2]) if len(sys.argv) > 2 else 60
    cmd = ["dotnet", exe] if exe.endswith(".dll") else [exe]
    rnd = random.Random(1234)
    names = sorted(n for n in os.listdir(FIXTURES) if not n.endswith((".json", ".md")))
    tmp = tempfile.mkdtemp(prefix="jle-cross-")
    try:
        paths = [os.path.join(FIXTURES, n) for n in names]
        k = 0
        for n in names:
            data = open(os.path.join(FIXTURES, n), "rb").read()
            for _ in range(copies):
                p = os.path.join(tmp, f"{k:05d}-{n}")
                with open(p, "wb") as f:
                    f.write(mutate(rnd, data))
                paths.append(p)
                k += 1
        got = []
        for i in range(0, len(paths), 500):
            res = subprocess.run(cmd + ["--audio-hash-file", *paths[i:i + 500]],
                                 capture_output=True, text=True, check=True)
            got += res.stdout.split()
        if len(got) != len(paths):
            print(f"output line count {len(got)} != {len(paths)}")
            return 1
        mismatches = hashed = 0
        for p, c in zip(paths, got):
            h, why = ref.audio_hash(p)
            hashed += h is not None
            if (h or "none") != (c if not c.startswith("none") else "none"):
                mismatches += 1
                print(f"MISMATCH {os.path.basename(p)}: python={h or 'none:' + why} exporter={c}")
        print(f"files={len(paths)} hashed={hashed} mismatches={mismatches}")
        return 1 if mismatches else 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
