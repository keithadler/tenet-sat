#!/usr/bin/env python3
"""Corrupt a binary LRAT proof in many small ways and require tenet-sat and cake_lpr to agree.

usage: fuzz_binary_vs_cake_lpr.py <tenet-sat checkout> <name> <count> [seed]
Reads name.cnf and name.blrat (binary LRAT, e.g. `cadical --lrat name.cnf name.blrat`).
cake_lpr is taken from <checkout>/tools/cake_lpr/cake_lpr; tenet-sat from $TENETSAT, or the Release
build run through dotnet. Each mutant is checked by tenet-sat with a random thread count (1 or 8) and
batch size (1 MB or the default), so the parallel binary parser's guessed chunk starts are exercised.

Binary LRAT: 'a' id lits 0 hints 0 | 'd' ids 0, every number a varint of 2*|x| + (x < 0).
"""
import collections, os, random, subprocess, sys

W, name, N = sys.argv[1], sys.argv[2], int(sys.argv[3])
seed = int(sys.argv[4]) if len(sys.argv) > 4 else 11
TENETSAT = [os.environ["TENETSAT"]] if os.environ.get("TENETSAT") else ["dotnet", W + "/src/TenetSat/bin/Release/net10.0/tenetsat.dll"]
CAKE = W + "/tools/cake_lpr/cake_lpr"

data = open(name + ".blrat", "rb").read()


def varint(buf, i):
    v = shift = 0
    while True:
        b = buf[i]; i += 1
        v |= (b & 0x7F) << shift
        if b < 0x80: return v, i
        shift += 7


def enc(x):
    u = 2 * abs(x) + (1 if x < 0 else 0)
    out = bytearray()
    while u >= 0x80: out.append((u & 0x7F) | 0x80); u >>= 7
    out.append(u)
    return bytes(out)


def dec(u): return -(u >> 1) if u & 1 else u >> 1


# Split into steps: (start, end, tag, id, lits, hints) for additions, (start, end, 'd', None, ids, None) for deletions.
steps, i = [], 0
while i < len(data):
    start, tag = i, data[i]; i += 1
    if tag == ord('d'):
        ids = []
        while True:
            u, i = varint(data, i)
            if u == 0: break
            ids.append(dec(u))
        steps.append((start, i, 'd', None, ids, None))
    elif tag == ord('a'):
        u, i = varint(data, i); sid = dec(u)
        lits, hints = [], []
        while True:
            u, i = varint(data, i)
            if u == 0: break
            lits.append(dec(u))
        while True:
            u, i = varint(data, i)
            if u == 0: break
            hints.append(dec(u))
        steps.append((start, i, 'a', sid, lits, hints))
    else:
        sys.exit(f"not binary LRAT at byte {start}")
adds = [k for k, s in enumerate(steps) if s[2] == 'a']
print(f"{name}: {len(steps)} steps ({len(adds)} additions), {len(data)} bytes", flush=True)


def add_bytes(sid, lits, hints):
    return b"a" + enc(sid) + b"".join(map(enc, lits)) + b"\x00" + b"".join(map(enc, hints)) + b"\x00"


def splice(k, new): s = steps[k]; return data[:s[0]] + new + data[s[1]:]


rng = random.Random(seed)
KINDS = ["drop hint", "flip literal", "drop literal", "replace hint", "drop step", "delete hint first",
         "swap hints", "flip a bit", "truncate", "duplicate step"]


def mutate():
    kind = rng.randrange(len(KINDS))
    if kind == 7:                                         # flip one bit anywhere
        b = bytearray(data); p = rng.randrange(len(b)); b[p] ^= 1 << rng.randrange(8); return kind, bytes(b)
    if kind == 8:                                         # cut the file short
        return kind, data[:rng.randrange(1, len(data))]
    if kind == 4:                                         # drop a whole step
        return kind, splice(rng.randrange(len(steps)), b"")
    if kind == 9:                                         # repeat a step right after itself
        s = steps[rng.randrange(len(steps))]; return kind, data[:s[1]] + data[s[0]:s[1]] + data[s[1]:]
    k = rng.choice(adds)
    _, _, _, sid, lits, hints = steps[k]
    lits, hints = list(lits), list(hints)
    if kind == 0 and hints: del hints[rng.randrange(len(hints))]
    elif kind == 1 and lits: j = rng.randrange(len(lits)); lits[j] = -lits[j]
    elif kind == 2 and lits: del lits[rng.randrange(len(lits))]
    elif kind == 3 and hints: hints[rng.randrange(len(hints))] = rng.randrange(1, sid)
    elif kind == 5 and hints:                             # delete a hint's clause just before this step
        h = abs(rng.choice(hints))
        return kind, splice(k, b"d" + enc(h) + b"\x00" + data[steps[k][0]:steps[k][1]])
    elif kind == 6 and len(hints) > 1: j = rng.randrange(len(hints) - 1); hints[j], hints[j + 1] = hints[j + 1], hints[j]
    else: return mutate()                                 # this step cannot take that mutation; draw again
    return kind, splice(k, add_bytes(sid, lits, hints))


def verdict(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True, errors="replace")
    return p.stdout + p.stderr


tally, bad, expected = collections.Counter(), [], collections.Counter()
for n in range(N):
    kind, mutant = mutate()
    open("mut.blrat", "wb").write(mutant)
    threads, batch = rng.choice([1, 8]), rng.choice([1, 1024])
    out = verdict(TENETSAT + ["check", name + ".cnf", "mut.blrat", "--threads", str(threads), "--batch-mb", str(batch)])
    ours = "s VERIFIED" in out
    cake = "s VERIFIED UNSAT" in verdict([CAKE, name + ".cnf", "mut.blrat"])
    tally[(KINDS[kind], ours, cake)] += 1
    if ours != cake:
        # Documented difference: tenet-sat requires addition IDs to strictly increase, cake_lpr lets a
        # repeated ID overwrite. It can only make tenet-sat reject, never accept.
        if not ours and "must strictly increase" in out: expected[KINDS[kind]] += 1; continue
        bad.append((KINDS[kind], threads, batch, ours, cake))
        os.replace("mut.blrat", f"mismatch{len(bad)}.blrat")
by_answer = collections.Counter()
for (k, o, c), v in tally.items(): by_answer[(o, c)] += v
for (o, c), v in sorted(by_answer.items()):
    print(f"tenetsat={'accept' if o else 'reject'} cake_lpr={'accept' if c else 'reject'}: {v}")
for k in KINDS:
    row = {(o, c): v for (kk, o, c), v in tally.items() if kk == k}
    if row: print(f"  {k:18} " + "  ".join(f"{'acc' if o else 'rej'}/{'acc' if c else 'rej'} {v}" for (o, c), v in sorted(row.items())))
for k, v in expected.items(): print(f"expected difference ({k}): tenet-sat rejects a repeated clause ID, cake_lpr accepts: {v}")
print(f"unexplained disagreements: {len(bad)}  (tenet-sat accepting what cake_lpr rejects: {sum(1 for b in bad if b[3])})")
for b in bad[:12]: print("  ", b)
