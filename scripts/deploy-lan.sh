#!/usr/bin/env bash
#
# Собирает APK и ставит его сразу на все рамки в локальной сети — без FRAME=
# для каждой по отдельности.
#
#   ./deploy-lan.sh              найти рамки, собрать, поставить на все
#   ./deploy-lan.sh --no-build   поставить то, что уже собрано
#   SUBNET=192.168.2 ./deploy-lan.sh   явно задать подсеть для поиска

. "$(dirname "$0")/common.sh"

"$(dirname "$0")/lan-discover.sh"

if [ "${1:-}" != "--no-build" ]; then
    "$(dirname "$0")/build.sh"
fi

[ -f "$APK" ] || die "APK не найден: $APK. Соберите без --no-build."

frames="$(list_frames)"
[ -n "$frames" ] || die "Рамка не подключена."

for serial in $frames; do
    echo "Рамка $serial: установка..."
    "$ADB" -s "$serial" install -r "$(win_path "$APK")" | tail -1
    echo "Запуск..."
    "$ADB" -s "$serial" shell "am start -n $ACTIVITY" >/dev/null 2>&1
done
