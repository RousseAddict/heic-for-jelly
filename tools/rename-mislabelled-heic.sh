#!/bin/sh
#
# Renames .HEIC/.HEIF files whose bytes are actually JPEG so that they end in .jpg.
#
# Jellyfin's PhotoResolver.IsImageFile gates on the extension, so a JPEG named .HEIC
# never becomes a Photo item at all. Renaming it is the whole fix; genuine HEICs are
# left untouched, they are the plugin's job.
#
# Content is read from the magic bytes. The extension is never trusted, in either
# direction. Nothing is modified unless --apply is passed.
#
# Usage:
#   sh rename-mislabelled-heic.sh DIR...              # dry run, prints the plan
#   sh rename-mislabelled-heic.sh --apply DIR...      # renames, writes an undo script
#
# Over ssh, without copying the file to the server:
#   ssh host sh -s -- ~/Downloads/Photos/2025 < tools/rename-mislabelled-heic.sh

set -eu

apply=0

while [ $# -gt 0 ]; do
    case "$1" in
        --apply) apply=1; shift ;;
        -h|--help) sed -n '3,20p' "$0" 2>/dev/null || true; exit 0 ;;
        --) shift; break ;;
        -*) printf 'unknown option: %s\n' "$1" >&2; exit 2 ;;
        *) break ;;
    esac
done

if [ $# -eq 0 ]; then
    printf 'usage: %s [--apply] DIR...\n' "$0" >&2
    exit 2
fi

for dir in "$@"; do
    if [ ! -d "$dir" ]; then
        printf 'not a directory: %s\n' "$dir" >&2
        exit 2
    fi
done

list=$(mktemp)
trap 'rm -f "$list"' EXIT INT TERM

for dir in "$@"; do
    find "$dir" -type f \( -iname '*.heic' -o -iname '*.heif' \) -print >> "$list"
done

undo=''
if [ "$apply" -eq 1 ]; then
    undo="heic-rename-undo-$(date +%Y%m%d-%H%M%S).sh"
    printf '#!/bin/sh\n# Reverses the matching rename pass. Run from the same working directory.\nset -eu\n' > "$undo"
    chmod +x "$undo"
fi

jpeg=0
heif=0
taken=0
skipped=0
unknown=0

while IFS= read -r f; do
    [ -n "$f" ] || continue

    # Single quotes would break the undo script's quoting. Photos do not have them;
    # if one does, leave it alone rather than emit an undo line that cannot be run.
    case "$f" in
        *\'*)
            unknown=$((unknown + 1))
            printf 'SKIP     %s (single quote in name)\n' "$f"
            continue
            ;;
    esac

    magic=$(od -An -tx1 -N12 "$f" | tr -d ' \n')

    case "$magic" in
        ffd8ff*)
            jpeg=$((jpeg + 1))
            target="${f%.*}.jpg"
            if [ -e "$target" ]; then
                taken=$((taken + 1))
                printf 'TAKEN    %s -> %s already exists\n' "$f" "$target"
                continue
            fi
            if [ "$apply" -eq 1 ]; then
                mv -- "$f" "$target"
                printf "mv -- '%s' '%s'\n" "$target" "$f" >> "$undo"
                printf 'RENAMED  %s -> %s\n' "$f" "$target"
            else
                printf 'WOULD    %s -> %s\n' "$f" "$target"
            fi
            ;;
        ????????66747970*)
            heif=$((heif + 1))
            skipped=$((skipped + 1))
            ;;
        *)
            unknown=$((unknown + 1))
            printf 'UNKNOWN  %s (magic %s)\n' "$f" "$magic"
            ;;
    esac
done < "$list"

total=$(wc -l < "$list" | tr -d ' ')

printf '\n'
printf 'scanned            %s\n' "$total"
printf 'JPEG mislabelled   %s\n' "$jpeg"
printf 'genuine HEIF/HEIC  %s  (left alone, the plugin handles these)\n' "$heif"
printf 'target name taken  %s\n' "$taken"
printf 'unrecognised       %s\n' "$unknown"

if [ "$apply" -eq 1 ]; then
    printf '\nrenamed %s file(s). Undo script: %s\n' "$((jpeg - taken))" "$undo"
    printf 'Jellyfin needs a library rescan to pick the renamed files up.\n'
else
    printf '\nDry run, nothing changed. Re-run with --apply to rename.\n'
fi
