import os
import json
import csv
from glob import glob
from datetime import datetime, timedelta, timezone
import sys

# ---- Config ----
STEP_TO_KM = (0.0007376 + 0.0007614) / 2  # 0.0007495
REFERENCE_STEPS = [
    {"date": "2024-02-12", "steps": 6822},
    {"date": "2024-02-13", "steps": 4521},
    {"date": "2024-02-14", "steps": 1010},
]
BAD_DATE = "1970-01-01"

# Resolve BASE_DIR from env or CLI
BASE_DIR = os.environ.get("SHEALTH_DIR")
if not BASE_DIR and len(sys.argv) > 1:
    BASE_DIR = sys.argv[1]
if not BASE_DIR:
    raise SystemExit("Usage: shealth_steps_pipeline.py <BASE_DIR> or set SHEALTH_DIR")

# ---- Helpers (unchanged logic) ----
def ms_to_date(ms):
    return datetime.fromtimestamp(ms / 1000, tz=timezone.utc).strftime('%Y-%m-%d')

def get_all_binning_files(base_dir):
    files = []
    for root, dirs, _ in os.walk(base_dir):
        for d in dirs:
            dir_path = os.path.join(root, d)
            files.extend(glob(os.path.join(dir_path, "*.binning_data.json")))
    return sorted(files)

def extract_date_from_entry(entry):
    if "mBestStepsDate" in entry: return ms_to_date(entry["mBestStepsDate"])
    if "mStartTime" in entry:     return ms_to_date(entry["mStartTime"])
    if "start_time" in entry:     return ms_to_date(entry["start_time"])
    return None

def process_file(filepath):
    with open(filepath, encoding="utf-8") as f:
        try:
            data = json.load(f)
        except Exception as e:
            print(f"Failed to read {filepath}: {e}")
            return None, None, None

    total_steps = 0
    total_distance = 0.0
    found_distance = False
    key_style = None
    found_date = None

    if isinstance(data, list) and data:
        for entry in data:
            if not isinstance(entry, dict): continue
            if not found_date: found_date = extract_date_from_entry(entry)
            if "count" in entry:       key_style = "shealth";   break
            if "mStepCount" in entry:  key_style = "pedometer"; break
        if not key_style: return None, None, None
        for entry in data:
            if not isinstance(entry, dict): continue
            if key_style == "shealth":
                steps = entry.get("count", 0)
                distance = entry.get("distance", 0.0)
            else:  # pedometer
                steps = entry.get("mStepCount", 0)
                distance = entry.get("mDistance", 0.0)

            if steps and steps > 0: total_steps += steps
            if distance and distance > 0:
                total_distance += distance
                found_distance = True
            if not found_date: found_date = extract_date_from_entry(entry)

    elif isinstance(data, dict):
        if "mBestSteps" in data and "mBestStepsDate" in data:
            key_style = "pedometer"
            total_steps = data.get("mBestSteps", 0)
            found_date = extract_date_from_entry(data)
        elif "count" in data and "start_time" in data:
            key_style = "shealth"
            total_steps = data.get("count", 0)
            found_date = extract_date_from_entry(data)
        else:
            return None, None, None

    if total_steps == 0: return None, None, None

    result = {
        "steps": total_steps,
        "distance_km": round((total_distance / 1000.0) if found_distance and total_distance > 0 else total_steps * STEP_TO_KM, 2),
        "file": filepath,
        "mtime": os.path.getmtime(filepath),
        "date": found_date
    }
    return key_style, result, found_distance

def assign_dates(files_info, reference_steps):
    with_dates = [f for f in files_info if f.get("date")]
    without_dates = [f for f in files_info if not f.get("date")]
    if not without_dates: return files_info
    files_info.sort(key=lambda x: x["mtime"])
    steps_to_idx = {f["steps"]: idx for idx, f in enumerate(files_info)}
    ref_dates_idx = []
    for ref in reference_steps:
        idx = steps_to_idx.get(ref["steps"])
        if idx is not None: ref_dates_idx.append({"date": ref["date"], "idx": idx})
    if len(ref_dates_idx) < 1:
        base_date = datetime.strptime(reference_steps[0]["date"], "%Y-%m-%d")
        return [{**f, "date": (base_date + timedelta(days=i)).strftime("%Y-%m-%d")} for i, f in enumerate(files_info)]
    date_map = {}
    for i in range(len(ref_dates_idx)):
        ref = ref_dates_idx[i]
        date0 = datetime.strptime(ref["date"], "%Y-%m-%d")
        idx0 = ref["idx"]
        date_map[idx0] = date0
        next_idx = ref_dates_idx[i+1]["idx"] if i+1 < len(ref_dates_idx) else len(files_info)
        for offset, idx in enumerate(range(idx0+1, next_idx), 1):
            date_map[idx] = date0 + timedelta(days=offset)
        if i == 0:
            for offset, idx in enumerate(range(idx0-1, -1, -1), 1):
                date_map[idx] = date0 - timedelta(days=offset)
        else:
            prev_idx = ref_dates_idx[i-1]["idx"]
            prev_date = datetime.strptime(ref_dates_idx[i-1]["date"], "%Y-%m-%d")
            span = idx0 - prev_idx
            if span > 1:
                for j, idx in enumerate(range(prev_idx+1, idx0), 1):
                    date_map[idx] = prev_date + timedelta(days=j)
    output = []
    for i, f in enumerate(files_info):
        date = f.get("date")
        if not date:
            date = date_map.get(i)
            if isinstance(date, datetime):
                date = date.strftime("%Y-%m-%d")
        f2 = f.copy()
        f2["date"] = date
        output.append(f2)
    return output

def fix_bad_dates_in_csv(input_csv, output_csv, bad_date=BAD_DATE):
    rows = []
    with open(input_csv, newline='', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for row in reader:
            rows.append(row)

    idx = len(rows) - 1
    while idx >= 0 and rows[idx]['date'] == bad_date:
        idx -= 1
    if idx < 0:
        print(f"No good dates found in {input_csv}, nothing to fix.")
        # still write header + rows as-is
        with open(output_csv, 'w', newline='', encoding='utf-8') as f:
            writer = csv.DictWriter(f, fieldnames=["date", "steps", "distance_km"])
            writer.writeheader()
            for row in rows: writer.writerow(row)
        return

    last_good_date = datetime.strptime(rows[idx]['date'], "%Y-%m-%d")
    for i in range(idx-1, -1, -1):
        if rows[i]['date'] == bad_date:
            last_good_date -= timedelta(days=1)
            rows[i]['date'] = last_good_date.strftime("%Y-%m-%d")
        else:
            last_good_date = datetime.strptime(rows[i]['date'], "%Y-%m-%d")
    rows.sort(key=lambda x: x["date"])

    with open(output_csv, 'w', newline='', encoding='utf-8') as f:
        writer = csv.DictWriter(f, fieldnames=["date", "steps", "distance_km"])
        writer.writeheader()
        for row in rows: writer.writerow(row)
    print(f"Fixed CSV written to: {output_csv}")

def main():
    files = get_all_binning_files(BASE_DIR)
    shealth_info, pedometer_info = [], []
    for f in files:
        key_style, file_info, _ = process_file(f)
        if not key_style or not file_info: continue
        (pedometer_info if key_style == "pedometer" else shealth_info).append(file_info)

    shealth_with_dates   = assign_dates(shealth_info,   REFERENCE_STEPS)
    pedometer_with_dates = assign_dates(pedometer_info, REFERENCE_STEPS)

    # temp paths (will be deleted)
    tmp_shealth_csv   = os.path.join(BASE_DIR, "_tmp_steps_summary_shealth.csv")
    tmp_pedometer_csv = os.path.join(BASE_DIR, "_tmp_steps_summary_pedometer.csv")
    final_csv         = os.path.join(BASE_DIR, "steps_summary_pedometer_fixed.csv")

    # write temps
    with open(tmp_shealth_csv, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["date", "steps", "distance_km"])
        writer.writeheader()
        for r in shealth_with_dates:
            writer.writerow({"date": r["date"], "steps": r["steps"], "distance_km": r["distance_km"]})
    with open(tmp_pedometer_csv, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["date", "steps", "distance_km"])
        writer.writeheader()
        for r in pedometer_with_dates:
            writer.writerow({"date": r["date"], "steps": r["steps"], "distance_km": r["distance_km"]})

    # fix + write final (only pedometer is kept)
    fix_bad_dates_in_csv(tmp_pedometer_csv, final_csv)

    # delete temps + obsolete files
    for p in [tmp_shealth_csv, tmp_pedometer_csv,
              os.path.join(BASE_DIR, "steps_summary_shealth_fixed.csv"),
              os.path.join(BASE_DIR, "steps_summary_shealth.csv"),
              os.path.join(BASE_DIR, "steps_summary_pedometer.csv")]:
        try:
            if os.path.exists(p): os.remove(p)
        except Exception as e:
            print(f"Could not delete {p}: {e}")

    print(f"FINAL: {final_csv}")

if __name__ == "__main__":
    main()
