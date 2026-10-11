"""Generate many full seeds with random Escape from Tarkov options (solo and mixed multiworlds)
through Archipelago's real generator, and report any failure. Run from an Archipelago checkout:

    SKIP_REQUIREMENTS_UPDATE=1 python fuzz_apworld.py <count> <out_dir>
"""
import os
import random
import shutil
import subprocess
import sys
import time

import yaml

AP = os.getcwd()
count = int(sys.argv[1]) if len(sys.argv) > 1 else 50
out = sys.argv[2] if len(sys.argv) > 2 else "fuzz-out"
traders = ["Prapor", "Therapist", "Skier", "Peacekeeper", "Mechanic", "Ragman", "Jaeger"]


def random_tarkov(rng):
    return {
        "goal": rng.choice(["quest_count", "collector", "all_extracts"]),
        "goal_quest_count": rng.choice([5, 20, 60, 150, 372]),
        "starting_map": rng.choice(["ground_zero", "factory", "customs", "woods", "interchange", "shoreline",
                                    "reserve", "lighthouse", "streets_of_tarkov"]),
        "starting_traders": rng.choice(["prapor_and_therapist", "random_two", "random_one"]),
        "quest_traders": rng.sample(traders, rng.randint(1, 7)),
        "max_quest_level": rng.choice([1, 5, 10, 15, 20, 30, 45, 79]),
        "hideout_checks": rng.choice([True, False]),
        "extracts_per_map": rng.randint(0, 5),
        "lock_flea_market": rng.choice([True, False]),
        "level_pacing": rng.choice(["relaxed", "normal", "fast"]),
        "trap_percentage": rng.choice([0, 10, 50]),
        "death_link": rng.choice([True, False]),
        "starting_kit": rng.choice(["off", "extra", "replace"]),
        "starting_kit_quality": rng.choice(["scrappy", "standard", "geared"]),
        "accessibility": rng.choice(["full", "minimal"]),
        "progression_balancing": rng.choice([0, 50, 99]),
    }


failures = []
start = time.time()
for i in range(count):
    rng = random.Random(i)
    pdir = os.path.join(out, f"p{i}")
    shutil.rmtree(pdir, ignore_errors=True)
    os.makedirs(pdir)
    n_tarkov = rng.choice([1, 1, 2, 3])
    players = []
    for t in range(n_tarkov):
        players.append({"name": f"Tarkov{t}", "game": "Escape from Tarkov", "Escape from Tarkov": random_tarkov(rng)})
    for t in range(rng.choice([0, 0, 1, 2])):
        players.append({"name": f"Quest{t}", "game": "APQuest", "APQuest": {}})
    for k, p in enumerate(players):
        with open(os.path.join(pdir, f"{k}.yaml"), "w") as f:
            yaml.safe_dump(p, f)
    seed = rng.randint(1, 10**9)
    proc = subprocess.run([sys.executable, "Generate.py", "--player_files_path", pdir, "--outputpath",
                           os.path.join(out, "o"), "--skip_output", "--seed", str(seed)],
                          cwd=AP, capture_output=True, text=True, stdin=subprocess.DEVNULL,
                          env=dict(os.environ, SKIP_REQUIREMENTS_UPDATE="1"), timeout=900)
    ok = proc.returncode == 0 and "Done." in proc.stdout
    if not ok:
        failures.append((i, seed, (proc.stdout + proc.stderr)[-2500:]))
        print(f"[{i}] FAIL seed={seed}", flush=True)
        print((proc.stdout + proc.stderr)[-1500:], flush=True)
    elif i % 10 == 0:
        print(f"[{i}] ok ({time.time() - start:.0f}s)", flush=True)

print(f"\n{count - len(failures)}/{count} generations succeeded in {time.time() - start:.0f}s")
sys.exit(1 if failures else 0)
