import os
import json
import csv
from glob import glob
from datetime import datetime, timedelta, timezone

# -------------------- CONFIG --------------------

# Personalized steps→km fit (based on Samsung Health samples).
# km = A + B*steps + C*steps^2  (clamped to ≥ 0)
PERSONAL_STEPS_TO_KM_COEFFS = (0.129091492, 0.000589979242, 2.45535812e-08)

def steps_to_km(steps: float) -> float:
    a, b, c = PERSONAL_STEPS_TO_KM_COEFFS
    y = a + b * steps + c * (steps ** 2)
    return max(0.0, y)

# (Optional) keep the legacy constant for reference/fallback
STEP_TO_KM_LEGACY = (0.0007376 + 0.0007614) / 2  # ≈ 0.0007495

BAD_DATE = "1970-01-01"

# Always read paths from environment (with safe defaults)
RAW_DATA_ROOT = os.environ.get(
    "SHEALTH_RAW_DATA",
    r"C:\Users\matic\Desktop\honeybadger_crt\honey_badger_api\Samsung-Data\RAW_DATA"
)

OUTPUT_DIR = os.environ.get(
    "SHEALTH_OUTPUT_DIR",
    RAW_DATA_ROOT  # default: write CSV next to RAW_DATA
)

# Three rock-solid clusters (triplets) — ISO format dates
CLUSTERS = [
    {"start_date": "2021-12-14", "steps_seq": [4702, 6105, 10453]},  # 2021-12-14..16
    {"start_date": "2023-05-16", "steps_seq": [2470, 9953, 5412]},   # 2023-05-16..18
    {"start_date": "2025-09-15", "steps_seq": [4964, 1247, 2865]},   # 2025-09-15..17
]

# Calendar strictly spans the cluster range (inclusive)
CAL_START = datetime.strptime(min(c["start_date"] for c in CLUSTERS), "%Y-%m-%d")
CAL_END = max(
    datetime.strptime(c["start_date"], "%Y-%m-%d") + timedelta(days=2)
    for c in CLUSTERS
)

# -------------------- HELPERS --------------------

def ms_to_date(ms):
    return datetime.fromtimestamp(ms / 1000, tz=timezone.utc).strftime('%Y-%m-%d')

def try_parse_date_like(x):
    """Try to convert a variety of fields (ms epoch or iso string) to yyyy-mm-dd or None."""
    if x is None:
        return None
    try:
        if isinstance(x, (int, float)):
            if x > 10_000_000_000:  # interpret as ms
                return ms_to_date(x)
            return datetime.fromtimestamp(x, tz=timezone.utc).strftime('%Y-%m-%d')
        if isinstance(x, str):
            try:
                return datetime.fromisoformat(x.replace('Z', '').split('T')[0]).strftime('%Y-%m-%d')
            except Exception:
                pass
            if x.isdigit():
                val = int(x)
                return ms_to_date(val if val > 10_000_000_000 else val * 1000)
    except Exception:
        return None
    return None

def extract_date_from_entry(entry):
    for k in ("mBestStepsDate", "mStartTime", "start_time", "day_time", "time", "date", "day_start"):
        if k in entry:
            ds = try_parse_date_like(entry[k])
            if ds:
                return ds
    return None

def find_all_pedometer_dirs(raw_root):
    """Find every .../jsons/com.samsung.shealth.tracker.pedometer_day_summary directory under RAW_DATA."""
    pattern = os.path.join(
        raw_root,
        "**",
        "jsons",
        "com.samsung.shealth.tracker.pedometer_day_summary"
    )
    dirs = [d for d in glob(pattern, recursive=True) if os.path.isdir(d)]
    dirs.sort()
    return dirs

def find_all_binning_files_in_dir(pedo_dir):
    """Return ALL *.binning_data.json found recursively under pedo_dir (covers 0..f)."""
    pattern = os.path.join(pedo_dir, "**", "*.binning_data.json")
    files = glob(pattern, recursive=True)
    files.sort(key=lambda p: (os.path.getmtime(p), p))  # stable ordering
    return files

# -------------------- EXTRACTION --------------------

def _accumulate_from_iterable(items):
    """Accumulate steps/distance from a list of 'bin' entries with various schemas."""
    total_steps = 0
    total_distance = 0.0
    found_distance = False
    found_date = None

    for entry in items:
        if not isinstance(entry, dict):
            continue

        # steps candidates
        steps_val = None
        for sk in ("mStepCount", "mBestSteps", "count", "steps", "value"):
            if sk in entry and isinstance(entry[sk], (int, float)) and entry[sk] > 0:
                steps_val = int(entry[sk])
                break

        # distance candidates (meters)
        dist_val = None
        for dk in ("mDistance", "distance"):
            if dk in entry and isinstance(entry[dk], (int, float)) and entry[dk] > 0:
                dist_val = float(entry[dk])
                break

        if steps_val:
            total_steps += steps_val
        if dist_val:
            total_distance += dist_val
            found_distance = True

        if not found_date:
            found_date = extract_date_from_entry(entry)

    if total_steps == 0 and not found_distance:
        return None

    distance_km = (total_distance / 1000.0) if (found_distance and total_distance > 0) else steps_to_km(total_steps)
    return {
        "steps": int(total_steps),
        "distance_km": round(distance_km, 2),
        "raw_date": found_date,
    }

def process_binning_json(filepath):
    """
    Robustly parse a ∗.binning_data.json that may be:
      - list of bins
      - dict with 'binning_data' / 'items' / 'data' lists
      - single-object pedometer aggregate
    Returns dict with steps, distance_km, raw_date, mtime; or None if empty.
    """
    try:
        with open(filepath, encoding="utf-8") as f:
            data = json.load(f)
    except Exception as e:
        print(f"[READ FAIL] {filepath}: {e}")
        return None

    result = None

    if isinstance(data, list):
        result = _accumulate_from_iterable(data)

    elif isinstance(data, dict):
        # containers with lists
        for container_key in ("binning_data", "items", "data"):
            if container_key in data and isinstance(data[container_key], list):
                result = _accumulate_from_iterable(data[container_key])
                break

        # single-object fallback
        if result is None:
            steps = 0
            for sk in ("mBestSteps", "mStepCount", "count", "steps", "value"):
                if sk in data and isinstance(data[sk], (int, float)) and data[sk] > 0:
                    steps += int(data[sk])
            total_distance = 0.0
            found_distance = False
            for dk in ("mDistance", "distance"):
                if dk in data and isinstance(data[dk], (int, float)) and data[dk] > 0:
                    total_distance += float(data[dk])
                    found_distance = True
            if steps > 0 or found_distance:
                rd = extract_date_from_entry(data)
                distance_km = (total_distance / 1000.0) if (found_distance and total_distance > 0) else steps_to_km(steps)
                result = {
                    "steps": int(steps),
                    "distance_km": round(distance_km, 2),
                    "raw_date": rd,
                }

    if not result:
        return None

    result.update({
        "file": filepath,
        "mtime": os.path.getmtime(filepath),
    })
    return result

def discover_all_records(raw_root):
    """
    Collect ALL records from ALL pedometer_day_summary folders across ALL exports.
    Ordering: primarily by extracted raw_date (if valid & not 1970), otherwise by mtime, then by path.
    """
    pedo_dirs = find_all_pedometer_dirs(raw_root)
    print(f"[INFO] pedometer dirs found: {len(pedo_dirs)}")

    files = []
    for d in pedo_dirs:
        fset = find_all_binning_files_in_dir(d)
        files.extend(fset)
    print(f"[INFO] binning files found: {len(files)}")

    # Parse all files
    records = []
    for fp in files:
        rec = process_binning_json(fp)
        if rec:
            records.append(rec)

    # ---- FIX: make the sort key always NAIVE (no tz) to avoid naive/aware comparisons
    def rec_sort_key(r):
        rd = r.get("raw_date")
        dt = None
        if rd and rd != BAD_DATE:
            try:
                dt = datetime.strptime(rd, "%Y-%m-%d")  # naive
            except Exception:
                dt = None
        # fallback: use UTC epoch as a NAIVE datetime
        if dt is None:
            dt = datetime.utcfromtimestamp(r["mtime"])  # naive
        return (dt, r["file"])

    records.sort(key=rec_sort_key)
    return records

# -------------------- CLUSTER → DATE MAPPING --------------------

def find_sequence_index_after(records, steps_seq, start_at):
    """Sliding-window match for steps sequence after start_at (inclusive)."""
    L = len(steps_seq)
    n = len(records)
    for i in range(start_at, n - L + 1):
        ok = True
        for j in range(L):
            if records[i + j]["steps"] != steps_seq[j]:
                ok = False
                break
        if ok:
            return i
    return None

def create_blank_timeline(cal_start=CAL_START, cal_end=CAL_END):
    """Full blank, leap-safe timeline from cal_start..cal_end (inclusive)."""
    days = (cal_end - cal_start).days + 1
    rows = []
    for k in range(days):
        d = cal_start + timedelta(days=k)
        rows.append({"date": d.strftime("%Y-%m-%d"), "steps": 0, "distance_km": 0.0})
    return rows

def assign_dates_map(records):
    """
    Anchor clusters and map each record index -> date, then return a
    dict[date_str] = (steps, distance_km) using 'keep max steps per date'.
    """
    if not records:
        return {}

    # locate clusters sequentially
    anchors = {}
    search_from = 0
    for c in CLUSTERS:
        start_dt = datetime.strptime(c["start_date"], "%Y-%m-%d")
        idx0 = find_sequence_index_after(records, c["steps_seq"], search_from)
        if idx0 is None:
            continue
        for off in range(len(c["steps_seq"])):
            anchors[idx0 + off] = start_dt + timedelta(days=off)
        search_from = idx0 + len(c["steps_seq"])

    if not anchors:
        return {}

    # index->date across whole records list
    idx_to_date = dict(anchors)
    known = sorted(idx_to_date.items(), key=lambda kv: kv[0])

    # backward
    first_idx, first_dt = known[0]
    for i in range(first_idx - 1, -1, -1):
        idx_to_date[i] = first_dt - timedelta(days=(first_idx - i))

    # between
    for a in range(len(known) - 1):
        ia, da = known[a]
        ib, db = known[a + 1]
        for i in range(ia + 1, ib):
            idx_to_date[i] = da + timedelta(days=(i - ia))

    # forward
    last_idx, last_dt = known[-1]
    for i in range(last_idx + 1, len(records)):
        idx_to_date[i] = last_dt + timedelta(days=(i - last_idx))

    # date -> best record (max steps, tie: max distance)
    by_date = {}
    for i, rec in enumerate(records):
        dt = idx_to_date.get(i)
        if not isinstance(dt, datetime):
            continue
        ds = dt.strftime("%Y-%m-%d")
        cur = by_date.get(ds)
        if (cur is None) or (rec["steps"] > cur["steps"]) or (
            rec["steps"] == cur["steps"] and rec["distance_km"] > cur["distance_km"]
        ):
            by_date[ds] = {"steps": rec["steps"], "distance_km": rec["distance_km"]}
    return by_date

def insert_data_after_timeline(timeline_rows, date_map):
    """
    Overlay genuine data into the pre-built timeline.
    For each date, set to the max(steps) w.r.t. what's already there (zeros).
    """
    if not date_map:
        return timeline_rows
    idx = {r["date"]: i for i, r in enumerate(timeline_rows)}
    for ds, rec in date_map.items():
        if ds in idx:
            i = idx[ds]
            cur = timeline_rows[i]
            if (rec["steps"] > cur["steps"]) or (
                rec["steps"] == cur["steps"] and rec["distance_km"] > cur["distance_km"]
            ):
                timeline_rows[i] = {"date": ds, "steps": int(rec["steps"]), "distance_km": float(rec["distance_km"])}
    return timeline_rows

def dedupe_across_dates_preserve_timeline(timeline_rows):
    """
    Same-steps across different dates: keep the first, set later duplicates to zero.
    (Zeros are never deduped.)
    """
    seen = set()
    out = []
    for r in sorted(timeline_rows, key=lambda x: x["date"]):
        s = int(r["steps"])
        if s == 0:
            out.append(r)
            continue
        if s in seen:
            out.append({"date": r["date"], "steps": 0, "distance_km": 0.0})
        else:
            seen.add(s)
            out.append(r)
    return out

# -------------------- MAIN --------------------

def main():
    # 1) Build the timeline FIRST (so it never gets wiped)
    timeline = create_blank_timeline(CAL_START, CAL_END)

    # 2) Discover & parse ALL records across ALL exports / pedometer folders
    records = discover_all_records(RAW_DATA_ROOT)
    print(f"[INFO] candidate records parsed: {len(records)}")
    nonzero = sum(1 for r in records if r and (r["steps"] > 0 or r["distance_km"] > 0))
    print(f"[INFO] non-zero records: {nonzero}")

    # 3) Anchor clusters and compute a date->record map (may be empty)
    date_map = assign_dates_map(records)
    print(f"[INFO] mapped dates from records: {len(date_map)}")

    # 4) INSERT genuine data AFTER the timeline is built
    timeline = insert_data_after_timeline(timeline, date_map)

    # 5) Dedupe same steps across dates BUT preserve the timeline (later duplicates -> zeros)
    timeline = dedupe_across_dates_preserve_timeline(timeline)

    # 6) Write CSV
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    pedometer_csv = os.path.join(OUTPUT_DIR, "steps_summary_pedometer.csv")
    with open(pedometer_csv, "w", newline="", encoding="utf-8") as csvfile:
        writer = csv.DictWriter(csvfile, fieldnames=["date", "steps", "distance_km"])
        writer.writeheader()
        for r in sorted(timeline, key=lambda x: x["date"]):
            writer.writerow({"date": r["date"], "steps": r["steps"], "distance_km": r["distance_km"]})
    print(f"[OK] Wrote: {pedometer_csv}")

if __name__ == "__main__":
    main()
