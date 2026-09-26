#!/usr/bin/env bash
#
# Перезапускает приложение на рамке, ничего не собирая и не устанавливая.
# Нужно, когда правились настройки на самом устройстве либо приложение надо
# просто поднять после остановки.

. "$(dirname "$0")/common.sh"

frame shell "am force-stop $PACKAGE"
sleep 1
frame shell "am start -n $ACTIVITY" >/dev/null 2>&1
sleep 5
echo "На экране: $(frame shell "dumpsys activity activities | grep -m1 mResumedActivity" 2>/dev/null | tr -d '' | sed 's/.*ActivityRecord{[^ ]* [^ ]* //; s/ .*//')"
