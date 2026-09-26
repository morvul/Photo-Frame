#!/usr/bin/env bash
#
# Журнал приложения на рамке. Пишет его FrameLog, а не Debug.WriteLine: в Release
# тот вырезается, и без своих записей о происходящем внутри судить нечем.
#
#   ./logs.sh              следить за новыми записями (Ctrl+C — выйти)
#   ./logs.sh --dump       показать накопленное и выйти
#   ./logs.sh --warn       только предупреждения
#   ./logs.sh --all        вместе с системными сообщениями об этом процессе

. "$(dirname "$0")/common.sh"

case "${1:-}" in
    --dump) frame logcat -d -s PhotoFrame:V ;;
    --warn) frame logcat -d -s PhotoFrame:W ;;
    --all)
        # Заодно ошибки среды выполнения и вендорного декодера: именно они объясняли
        # оба падения — ObjectDisposedException и «stream list full wait».
        frame logcat -d -v time -s PhotoFrame:V AndroidRuntime:E ROCKCHIP_VIDEO_DEC:E DEBUG:F libc:F
        ;;
    "")
        echo "Слежу за журналом. Ctrl+C — выйти."
        frame logcat -s PhotoFrame:V
        ;;
    *) die "Не знаю ключа $1. Есть --dump, --warn, --all." ;;
esac
