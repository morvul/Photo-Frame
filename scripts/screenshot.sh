#!/usr/bin/env bash
#
# Снимок экрана рамки. Оказался единственным надёжным способом разобраться с тем,
# что видно глазом: так нашлись и резервные часы, мелькавшие на смене минуты,
# и чёрный кадр между слоями.
#
#   ./screenshot.sh                  снять в frame-<время>.png
#   ./screenshot.sh clock.png        снять в указанный файл
#   ./screenshot.sh --series 12      снять 12 кадров подряд, как можно быстрее

. "$(dirname "$0")/common.sh"

if [ "${1:-}" = "--series" ]; then
    count="${2:-10}"
    stamp="$(date '+%H%M%S')"

    echo "Снимаю $count кадров подряд..."
    for i in $(seq 1 "$count"); do
        frame shell "screencap -p /sdcard/shot$i.png"
    done

    for i in $(seq 1 "$count"); do
        frame pull "/sdcard/shot$i.png" "$(win_path "frame-$stamp-$i.png")" >/dev/null 2>&1
        frame shell "rm -f /sdcard/shot$i.png"
    done

    # Размер файла — грубый, но рабочий признак: кадр, выпавший из общего ряда
    # (чёрный экран, другая картинка), сразу виден по размеру.
    for file in frame-$stamp-*.png; do
        echo "$(( $(stat -c %s "$file") / 1024 )) КБ  $file"
    done
    exit 0
fi

target="${1:-frame-$(date '+%H%M%S').png}"
frame shell "screencap -p /sdcard/shot.png"
frame pull "/sdcard/shot.png" "$(win_path "$target")" >/dev/null 2>&1
frame shell "rm -f /sdcard/shot.png"
echo "Снято: $target"
