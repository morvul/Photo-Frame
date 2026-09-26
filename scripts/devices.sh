#!/usr/bin/env bash
#
# Показывает подключённые рамки и их серийные номера — их и передают в FRAME=.

. "$(dirname "$0")/common.sh"

frames="$(list_frames)"

if [ -z "$frames" ]; then
    echo "Рамка не подключена."
    echo
    echo "Если кабель воткнут, проверьте по порядку:"
    echo "  * кабель — у этих рамок часто попадается только для питания;"
    echo "  * гнездо на рамке — данные идут лишь через OTG-порт;"
    echo "  * подключение напрямую, без разветвителя."
    exit 1
fi

for serial in $frames; do
    model="$("$ADB" -s "$serial" shell getprop ro.product.model 2>/dev/null | tr -d '\r')"
    version="$("$ADB" -s "$serial" shell "dumpsys package com.morvul.photoframe | grep -m1 versionName" 2>/dev/null | tr -d '\r ')"
    echo "$serial  модель $model  $version"
done
