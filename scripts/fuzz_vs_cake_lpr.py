import random, subprocess, sys, collections
# usage: fuzz_vs_cake_lpr.py <tenet-sat checkout> <name> <count>   (reads name.cnf and name.lrat)
# Set TENETSAT to a tenetsat binary to use it instead of the Release build run through dotnet.
import os
W, name, N = sys.argv[1], sys.argv[2], int(sys.argv[3])
TENETSAT = [os.environ["TENETSAT"]] if os.environ.get("TENETSAT") else ["dotnet", W + "/src/TenetSat/bin/Release/net10.0/tenetsat.dll"]
lines = open(name + ".lrat").read().splitlines()
random.seed(11)
tally = collections.Counter(); bad = []
def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True)
    return p.stdout + p.stderr
for n in range(N):
    L = list(lines); i = random.randrange(len(L)); t = L[i].split(); kind = random.randrange(7)
    isdel = len(t) > 1 and t[1] == "d"
    z = t.index("0") if not isdel else 1
    if kind == 0 and not isdel and len(t) > z + 2:   # drop one hint
        del t[random.randrange(z + 1, len(t) - 1)]
    elif kind == 1 and not isdel and z > 1:            # flip a literal
        k = random.randrange(1, z); t[k] = str(-int(t[k]))
    elif kind == 2 and not isdel and z > 1:            # drop a literal (strengthen)
        del t[random.randrange(1, z)]
    elif kind == 3 and not isdel and len(t) > z + 2:   # replace a hint with another id
        t[random.randrange(z + 1, len(t) - 1)] = str(random.randrange(1, int(t[0])))
    elif kind == 4:                                    # drop the step
        del L[i]; t = None
    elif kind == 5 and not isdel:                      # delete a hint clause just before use
        if len(t) > z + 2: L.insert(i, f"{t[0]} d {t[z+1]} 0"); t = None
    elif kind == 6 and not isdel and len(t) > z + 3:   # swap two hints
        a = random.randrange(z + 1, len(t) - 2); t[a], t[a+1] = t[a+1], t[a]
    if t is not None: L[i] = " ".join(t)
    open("mut.lrat", "w").write("\n".join(L) + "\n")
    ours = "s VERIFIED" in run(TENETSAT + ["check", name + ".cnf", "mut.lrat"])
    cake = "s VERIFIED UNSAT" in run([W + "/tools/cake_lpr/cake_lpr", name + ".cnf", "mut.lrat"])
    tally[(ours, cake)] += 1
    if ours != cake:
        bad.append((kind, i, lines[i][:120], L[i][:120] if i < len(L) else ""))
        open(f"mismatch{len(bad)}.lrat", "w").write("\n".join(L) + "\n")
for (o, c), v in sorted(tally.items()): print(f"tenetsat={'accept' if o else 'reject'} cake_lpr={'accept' if c else 'reject'}: {v}")
for b in bad[:12]: print(b)
