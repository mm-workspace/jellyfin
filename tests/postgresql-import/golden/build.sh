#!/usr/bin/env bash
# Builds a golden Jellyfin data directory with an official image: generated media, wizard, users, libraries,
# user data, playlists, a collection, an API key, devices and display preferences, all through the API.
#
# Usage: build.sh <version> [--tz Area/City]
# Environment: OUT (output directory, default ./out), KEEP_CONTAINER=1
# The container runs without a network; the API is driven with curl inside it. Container names start with jfpg-golden-.
set -euo pipefail

VERSION="$1"; shift
TZ_NAME="UTC"
if [ "${1:-}" = "--tz" ]; then TZ_NAME="$2"; shift 2; fi
HERE="$(cd "$(dirname "$0")" && pwd)"
IMAGE="jellyfin/jellyfin@$(awk -v v="$VERSION" '$1 == v { print $2 }' "$HERE/images.txt")"
[ "$IMAGE" = "jellyfin/jellyfin@" ] && { echo "no pinned digest for $VERSION in images.txt"; exit 1; }
SUFFIX="$VERSION$([ "$TZ_NAME" = UTC ] || echo "-${TZ_NAME//\//-}")"
OUT="${OUT:-$HERE/out}/$SUFFIX"
NAME="jfpg-golden-${SUFFIX//[^A-Za-z0-9]/-}-$(date +%s)"

log() { printf '== %s\n' "$*"; }
rm -rf "$OUT"; mkdir -p "$OUT/config" "$OUT/cache" "$OUT/media"

log "media"
ff() { docker run --rm --network none --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg -v "$OUT/media:/media" "$IMAGE" -hide_banner -loglevel error -y "$@"; }
video() { # path seconds
  mkdir -p "$OUT/media/$(dirname "$1")"
  ff -f lavfi -i "testsrc=duration=$2:size=320x240:rate=24" -f lavfi -i "sine=frequency=440:duration=$2" -c:v libx264 -g 48 -pix_fmt yuv420p -c:a aac -shortest "/media/$1"
}
video "Movies/Golden Hour (2020)/Golden Hour (2020).mkv" 12
video "Movies/Élodie's Journey (2019)/Élodie's Journey (2019).mkv" 8
video "Movies/Twin Cut (2018)/Twin Cut (2018) - 1080p.mkv" 6
video "Movies/Twin Cut (2018)/Twin Cut (2018) - 720p.mkv" 6
printf '1\n00:00:01,000 --> 00:00:03,000\nHello\n' > "$OUT/media/Movies/Golden Hour (2020)/Golden Hour (2020).en.srt"
for e in 1 2 3; do video "Shows/Test Show/Season 01/Test Show S01E0$e.mkv" 6; done
for e in 1 2; do video "Shows/Test Show/Season 02/Test Show S02E0$e.mkv" 6; done
mkdir -p "$OUT/media/Music/Test Artist/Test Album"
for t in 1 2 3; do
  ff -f lavfi -i "sine=frequency=$((300 + t * 100)):duration=5" -metadata title="Song $t" -metadata artist="Test Artist" -metadata album="Test Album" -metadata track="$t" -metadata genre="Test Genre" "/media/Music/Test Artist/Test Album/0$t - Song $t.mp3"
done

log "start $NAME ($IMAGE, TZ=$TZ_NAME)"
docker run -d --name "$NAME" --network none -e TZ="$TZ_NAME" \
  -v "$OUT/config:/config" -v "$OUT/cache:/cache" -v "$OUT/media:/media:ro" "$IMAGE" > /dev/null
cleanup() { [ "${KEEP_CONTAINER:-0}" = 1 ] || docker rm -f "$NAME" > /dev/null 2>&1 || true; }
trap cleanup EXIT

AUTH='MediaBrowser Client="golden", Device="build", DeviceId="golden-build", Version="1.0"'
api() { # method path [json] -> body
  local method="$1" path="$2" body="${3:-}"
  local header="$AUTH${TOKEN:+, Token=\"$TOKEN\"}"
  if [ -n "$body" ]; then
    docker exec "$NAME" curl -sf -X "$method" -H "Authorization: $header" -H 'Content-Type: application/json' -d "$body" "http://localhost:8096$path"
  else
    docker exec "$NAME" curl -sf -X "$method" -H "Authorization: $header" "http://localhost:8096$path"
  fi
}
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

# The setup server answers first (camelCase JSON), then 503 until the real server is up.
for i in $(seq 1 180); do api GET /System/Info/Public 2>/dev/null | grep -q '"StartupWizardCompleted"' && break; sleep 1; done
api GET /System/Info/Public | json 'd["Version"]'

log "wizard"
api POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
api GET /Startup/User > /dev/null
api POST /Startup/User '{"Name":"admin","Password":"golden"}'
api POST /Startup/RemoteAccess '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}'
api POST /Startup/Complete
TOKEN="$(api POST /Users/AuthenticateByName '{"Username":"admin","Pw":"golden"}' | json 'd["AccessToken"]')"
ADMIN="$(api GET /Users/Me | json 'd["Id"]')"

log "libraries"
options='{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableInternetProviders":false,"SaveLocalMetadata":false,"MetadataSavers":[],"TypeOptions":[]}}'
api POST "/Library/VirtualFolders?name=Movies&collectionType=movies&paths=%2Fmedia%2FMovies&refreshLibrary=false" "$options"
api POST "/Library/VirtualFolders?name=Shows&collectionType=tvshows&paths=%2Fmedia%2FShows&refreshLibrary=false" "$options"
api POST "/Library/VirtualFolders?name=Music&collectionType=music&paths=%2Fmedia%2FMusic&refreshLibrary=false" "$options"
api POST /Library/Refresh
sleep 5
for i in $(seq 1 300); do
  state="$(api GET '/ScheduledTasks?isHidden=false' | json '[t["State"] for t in d if t["Key"] == "RefreshLibrary"][0]')"
  [ "$state" = "Idle" ] && break
  sleep 2
done
api GET "/Items/Counts" | json 'd'

log "users"
ELODIE="$(api POST /Users/New '{"Name":"Élodie","Password":"golden"}' | json 'd["Id"]')"
KID="$(api POST /Users/New '{"Name":"kid","Password":"golden"}' | json 'd["Id"]')"
policy="$(api GET "/Users/$KID" | json 'json.dumps({**d["Policy"], "AccessSchedules": [{"DayOfWeek": "Saturday", "StartHour": 8, "EndHour": 20}], "MaxParentalRating": 10})')"
api POST "/Users/$KID/Policy" "$policy"

log "user data"
items() { api GET "/Items?Recursive=true&IncludeItemTypes=$1&userId=$ADMIN&SortBy=SortName" | json '" ".join(i["Id"] for i in d["Items"])'; }
MOVIES=($(items Movie)); EPISODES=($(items Episode)); SONGS=($(items Audio))
for user in "$ADMIN" "$ELODIE"; do
  api POST "/UserPlayedItems/${MOVIES[0]}?userId=$user" > /dev/null
  api POST "/UserFavoriteItems/${MOVIES[1]}?userId=$user" > /dev/null
  api POST "/UserPlayedItems/${EPISODES[0]}?userId=$user" > /dev/null
  api POST "/UserItems/${EPISODES[1]}/UserData?userId=$user" '{"PlaybackPositionTicks":30000000,"Played":false}' > /dev/null
  api POST "/UserFavoriteItems/${SONGS[0]}?userId=$user" > /dev/null
done

log "playlists, collection, key, device, display preferences"
api POST /Playlists "{\"Name\":\"Golden Mix\",\"Ids\":[\"${SONGS[0]}\",\"${SONGS[1]}\"],\"UserId\":\"$ADMIN\",\"MediaType\":\"Audio\"}" > /dev/null
api POST "/Collections?name=Golden%20Collection&ids=${MOVIES[0]},${MOVIES[1]}" > /dev/null
api POST "/Auth/Keys?app=golden" > /dev/null
AUTH='MediaBrowser Client="golden-tv", Device="Living Room", DeviceId="golden-tv-1", Version="2.0"' TOKEN= api POST /Users/AuthenticateByName '{"Username":"Élodie","Pw":"golden"}' > /dev/null
api POST "/DisplayPreferences/usersettings?userId=$ADMIN&client=emby" '{"Id":"usersettings","SortBy":"SortName","RememberIndexing":false,"PrimaryImageHeight":250,"PrimaryImageWidth":250,"CustomPrefs":{"homesection0":"resume","homesection1":"latestmedia"},"ScrollDirection":"Horizontal","ShowBackdrop":true,"RememberSorting":false,"SortOrder":"Ascending","ShowSidebar":false,"Client":"emby"}'

log "stop"
docker stop -t 60 "$NAME" > /dev/null
find "$OUT/config" -name 'jellyfin.db*' -o -name 'library.db*' | sed "s#$OUT/##"
docker image inspect "$IMAGE" --format '{{.Architecture}}' > "$OUT/image-architecture.txt"
echo "$IMAGE" > "$OUT/image.txt"
log "golden $SUFFIX in $OUT"
