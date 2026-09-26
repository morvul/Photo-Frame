#!/usr/bin/env bash
#
# Ставит APK на рамку и запускает его.
#
#   ./deploy.sh              собрать, поставить, запустить
#   ./deploy.sh --no-build   поставить то, что уже собрано
#   FRAME=<серийник> ./deploy.sh    когда подключено несколько рамок
#
# Приложение запускается сразу после установки не для красоты: пока его ни разу
# не открыли, Android считает пакет остановленным и не рассылает ему BOOT_COMPLETED —
# то есть автозапуск после включения рамки не сработает.

. "$(dirname "$0")/common.sh"

if [ "${1:-}" != "--no-build" ]; then
    "$(dirname "$0")/build.sh"
fi

[ -f "$APK" ] || die "APK не найден: $APK. Соберите без --no-build."

serial="$(resolve_frame)"
echo "Рамка $serial: установка..."
frame install -r "$(win_path "$APK")" | tail -1

echo "Запуск..."
frame shell "am start -n $ACTIVITY" >/dev/null 2>&1
sleep 6

echo
echo "На экране: $(frame shell "dumpsys activity activities | grep -m1 mResumedActivity" 2>/dev/null | tr -d '' | sed 's/.*ActivityRecord{[^ ]* [^ ]* //; s/ .*//')"
echo "Версия:    $(frame shell "dumpsys package $PACKAGE | grep -m1 versionName" 2>/dev/null | tr -d ' ' | cut -d= -f2)"
