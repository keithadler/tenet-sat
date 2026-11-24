#!/usr/bin/env python3
"""Writes the two small unsatisfiable test formulas used by the fuzz comparison:
r250.cnf (random 3-SAT, 250 variables, ratio 4.5, seed 1) and php9.cnf (10 pigeons, 9 holes)."""
import random

random.seed(1)
n, m = 250, int(250 * 4.5)
with open("r250.cnf", "w") as f:
    f.write(f"p cnf {n} {m}\n")
    for _ in range(m):
        vs = random.sample(range(1, n + 1), 3)
        f.write(" ".join(str(v if random.random() < 0.5 else -v) for v in vs) + " 0\n")

P, H = 10, 9
var = lambda p, h: p * H + h + 1
clauses = [[var(p, h) for h in range(H)] for p in range(P)]
clauses += [[-var(p, h), -var(q, h)] for h in range(H) for p in range(P) for q in range(p + 1, P)]
with open("php9.cnf", "w") as f:
    f.write(f"p cnf {P * H} {len(clauses)}\n")
    for c in clauses:
        f.write(" ".join(map(str, c)) + " 0\n")
