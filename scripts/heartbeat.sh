#!/usr/bin/env bash
#
# Журнал состояния: строка в минуту с памятью, процессорным временем и тем, что
# на экране. Пишется из фонового таймера — если строки идут, а картинка замерла,
# значит встал UI-поток, а не устройство.
#
#   ./heartbeat.sh           последние 20 строк
#   ./heartbeat.sh 100       последние 100 строк
#   ./heartbeat.sh 07:5      строки за нужное время (утренний переход, например)
#   ./heartbeat.sh --pull    скачать журнал целиком в текущий каталог

. "$(dirname "$0")/common.sh"

case "${1:-20}" in
    --pull)
        target="frame-$(resolve_frame).log"
        frame pull "$FRAME_LOG" "$(win_path "$target")" >/dev/null 2>&1
        echo "Скачано: $target ($(wc -l < "$target") строк)"
        ;;
    *[0-9])
        frame shell "tail -${1:-20} $FRAME_LOG" | tr -d '\r'
        ;;
    *)
        # Всё, что не число, считаем образцом времени: 08:0 — восьмой час, и так далее.
        frame shell "grep '$1' $FRAME_LOG | tail -40" | tr -d '\r'
        ;;
esac
