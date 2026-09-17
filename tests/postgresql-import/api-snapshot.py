#!/usr/bin/env python3
"""Records API responses of a running server and compares two recordings.

record <base-url> <directory> <user> <password>
compare <baseline-directory> <candidate-directory>

Fields that change on every login or start (login and activity dates, session state) are removed before comparing;
activity log entries written after the baseline was recorded are ignored.

Two differences are expected after an import and normalised on both sides: PostgreSQL keeps timestamps to the
microsecond, so the seventh decimal of a second is rounded away, and image tags, which are derived from the image
modification time, change once (clients download the images again).
"""
import datetime
import difflib
import json
import pathlib
import re
import sys
import urllib.request

VOLATILE = {"LastLoginDate", "LastActivityDate", "DateLastActivity", "ServerId", "LocalAddress"}


def call(base, path, token=None, body=None):
    auth = 'MediaBrowser Client="parity", Device="parity", DeviceId="parity-1", Version="1.0"'
    if token:
        auth += f', Token="{token}"'
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(base + path, data=data, method="POST" if data else "GET")
    request.add_header("Authorization", auth)
    request.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(request, timeout=60) as response:
        text = response.read().decode()
        return json.loads(text) if text else None


def record(base, directory, user, password):
    out = pathlib.Path(directory)
    out.mkdir(parents=True, exist_ok=True)
    login = call(base, "/Users/AuthenticateByName", body={"Username": user, "Pw": password})
    token, user_id = login["AccessToken"], login["User"]["Id"]
    fields = "Genres,Studios,People,Path,ParentId,Overview,DateCreated,MediaStreams,ProviderIds,SortName,Tags,Chapters"
    item_query = f"userId={user_id}&Recursive=true&Fields={fields}&EnableUserData=true"
    endpoints = {
        "users": "/Users",
        "items-by-name": f"/Items?{item_query}&SortBy=SortName,Id&SortOrder=Ascending",
        "items-by-date": f"/Items?{item_query}&SortBy=DateCreated,SortName&SortOrder=Descending",
        "items-played": f"/Items?{item_query}&Filters=IsPlayed&SortBy=SortName",
        "items-favorite": f"/Items?{item_query}&Filters=IsFavorite&SortBy=SortName",
        "items-movies-page": f"/Items?{item_query}&IncludeItemTypes=Movie&SortBy=ProductionYear,SortName&StartIndex=1&Limit=2",
        "latest": f"/Items/Latest?userId={user_id}&Limit=50",
        "resume": f"/UserItems/Resume?userId={user_id}",
        "next-up": f"/Shows/NextUp?userId={user_id}",
        "persons": f"/Persons?userId={user_id}",
        "genres": f"/Genres?userId={user_id}&SortBy=SortName",
        "studios": f"/Studios?userId={user_id}",
        "artists": f"/Artists?userId={user_id}",
        "activity": "/System/ActivityLog/Entries?limit=500",
        "devices": "/Devices",
        "keys": "/Auth/Keys",
        "display-preferences": f"/DisplayPreferences/usersettings?userId={user_id}&client=emby",
        "virtual-folders": "/Library/VirtualFolders",
    }
    for name, path in endpoints.items():
        (out / f"{name}.json").write_text(json.dumps(call(base, path, token), indent=1, sort_keys=True, ensure_ascii=False))
    print(f"recorded {len(endpoints)} responses into {out}")


TIMESTAMP = re.compile(r"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d+)?Z$")
IMAGE_TAGS = re.compile(r"ImageTags?$")


def microseconds(text):
    match = TIMESTAMP.match(text)
    if not match:
        return text
    # PostgreSQL reads the fraction as a double and rounds it with rint, which Python's round matches.
    fraction = round(float(match.group(2) or "0") * 1_000_000)
    moment = datetime.datetime.fromisoformat(match.group(1)) + datetime.timedelta(microseconds=fraction)
    return moment.strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def strip(value):
    if isinstance(value, dict):
        result = {}
        for key, item in value.items():
            if key in VOLATILE:
                continue
            if IMAGE_TAGS.search(key):
                result[key] = "<tag>" if isinstance(item, str) else ({k: "<tag>" for k in item} if isinstance(item, dict) else ["<tag>" for _ in item])
            elif key == "ImageBlurHashes":
                result[key] = {kind: sorted(hashes.values()) for kind, hashes in item.items()}
            else:
                result[key] = strip(item)
        return result
    if isinstance(value, list):
        return [strip(v) for v in value]
    if isinstance(value, str):
        return microseconds(value)
    return value


def compare(baseline_dir, candidate_dir):
    baseline_path, candidate_path = pathlib.Path(baseline_dir), pathlib.Path(candidate_dir)
    failures = 0
    for file in sorted(baseline_path.glob("*.json")):
        baseline = json.loads(file.read_text())
        candidate = json.loads((candidate_path / file.name).read_text())
        if file.stem == "activity":
            known = {entry["Id"] for entry in baseline["Items"]}
            candidate = {**candidate, "Items": [e for e in candidate["Items"] if e["Id"] in known]}
            candidate["TotalRecordCount"] = len(candidate["Items"])
            baseline["TotalRecordCount"] = len(baseline["Items"])
        baseline, candidate = strip(baseline), strip(candidate)
        if baseline == candidate:
            print(f"same      {file.stem}")
            continue
        failures += 1
        print(f"DIFFERENT {file.stem}")
        base_text = json.dumps(baseline, indent=1, sort_keys=True, ensure_ascii=False).splitlines()
        cand_text = json.dumps(candidate, indent=1, sort_keys=True, ensure_ascii=False).splitlines()
        for line in list(difflib.unified_diff(base_text, cand_text, "sqlite", "postgresql", lineterm="", n=2))[:40]:
            print("    " + line)
    return failures


if __name__ == "__main__":
    if sys.argv[1] == "record":
        record(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5])
    elif sys.argv[1] == "compare":
        sys.exit(1 if compare(sys.argv[2], sys.argv[3]) else 0)
    else:
        sys.exit(__doc__)
