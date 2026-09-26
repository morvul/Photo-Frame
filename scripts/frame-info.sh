#!/usr/bin/env bash
#
# Состояние рамки одним экраном: время, память, что показывается, как настроены
# источники. Первое, что стоит посмотреть, когда «что-то не так».

. "$(dirname "$0")/common.sh"

serial="$(resolve_frame)"
prefs="/data/data/$PACKAGE/shared_prefs/${PACKAGE}_preferences.xml"

echo "=== Рамка $serial ===================================================="
echo "модель      $(frame shell getprop ro.product.model | tr -d '\r')"
echo "Android     $(frame shell getprop ro.build.version.release | tr -d '\r')"
echo "приложение  $(frame shell "dumpsys package $PACKAGE | grep -m1 versionName" | tr -d '\r ' | cut -d= -f2)"
echo "работает    $(frame shell uptime | tr -d '\r' | sed 's/^ *//')"

echo
echo "=== Часы ============================================================="
echo "компьютер   $(date '+%H:%M:%S %z')"
echo "рамка       $(frame shell date "+%H:%M:%S" | tr -d '\r')"
echo "часовой пояс  $(frame shell getprop persist.sys.timezone | tr -d '\r')"
# Пустой ntp_server встречался на обеих рамках: автоматическое время включено,
# а спрашивать его не у кого, поэтому часы только уходят.
echo "сервер времени $(frame shell settings get global ntp_server | tr -d '\r')"

echo
echo "=== Память ==========================================================="
frame shell "cat /proc/meminfo | grep -E 'MemTotal|MemAvailable'" | tr -d '\r'
# Первое число строки TOTAL — это PSS в килобайтах, остальное здесь не нужно.
app_pss="$(frame shell "dumpsys meminfo $PACKAGE | grep -m1 TOTAL" | tr -d '\r' | awk '{print $2}')"
echo "занято приложением: $(( ${app_pss:-0} / 1024 )) МБ"

echo
echo "=== Что на экране ===================================================="
echo "приложение: $(frame shell "dumpsys activity activities | grep -m1 mResumedActivity" 2>/dev/null | tr -d '' | sed 's/.*ActivityRecord{[^ ]* [^ ]* //; s/ .*//')"
echo "домашний экран: $(frame shell "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME | tail -1" | tr -d '\r')"

echo
echo "=== Источники ========================================================"
frame shell "su -c 'grep -oE \"(use_shared_album|use_immich|use_local_folders|night_start_hour|night_end_hour|slideshow_interval_seconds)\\\" value=\\\"[a-z0-9]+\" $prefs'" 2>/dev/null | tr -d '\r' \
    || echo "настройки прочитать не удалось (нужен root)"

echo
echo "кадров в кэше Immich: $(frame shell "su -c 'ls /data/user/0/$PACKAGE/files/immich/*.jpg 2>/dev/null | wc -l'" | tr -d '\r')"
echo "кадров в кэше альбома: $(frame shell "su -c 'ls /data/user/0/$PACKAGE/files/photos/*.jpg 2>/dev/null | wc -l'" | tr -d '\r')"
echo "клипов скачано: $(frame shell "ls /storage/emulated/0/PhotoFrame/Cache/*.mp4 2>/dev/null | wc -l" | tr -d '\r')"
echo "в корзине рамки: $(frame shell "find /storage/emulated/0/PhotoFrame/Trash -type f 2>/dev/null | wc -l" | tr -d '\r')"

echo
echo "=== Пульс (последние строки) ========================================="
frame shell "tail -3 $FRAME_LOG" 2>/dev/null | tr -d '\r' || echo "журнал недоступен"
