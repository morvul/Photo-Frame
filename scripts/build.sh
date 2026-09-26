#!/usr/bin/env bash
#
# Собирает APK в конфигурации Release — ровно то, что потом ставится на рамку.
# Debug на рамке не используется: она работает месяцами, и вырезанные проверки
# заметно экономят и память, и процессор.
#
#   ./build.sh            собрать приложение
#   ./build.sh --tests    сперва прогнать тесты Core, потом собрать

. "$(dirname "$0")/common.sh"

if [ "${1:-}" = "--tests" ]; then
    "$(dirname "$0")/test.sh"
fi

echo "Сборка Release..."
dotnet build "$(win_path "$APP_PROJECT")" -c Release

[ -f "$APK" ] || die "Сборка прошла, но APK не найден: $APK"

echo
echo "APK: $APK"
echo "Размер: $(( $(stat -c %s "$APK") / 1024 / 1024 )) МБ, собран $(date -r "$APK" '+%H:%M:%S %d.%m.%Y')"
