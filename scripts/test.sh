#!/usr/bin/env bash
#
# Прогоняет тесты PhotoFrame.Core. Приложение собирается только под Android, и обычный
# тестовый проект на него сослаться не может, — поэтому вся проверяемая логика живёт
# в Core, и тесты идут без устройства.
#
#   ./test.sh                    все тесты
#   ./test.sh Immich             только те, в имени которых есть Immich

. "$(dirname "$0")/common.sh"

if [ -n "${1:-}" ]; then
    dotnet test "$(win_path "$TESTS_PROJECT")" --filter "$1"
else
    dotnet test "$(win_path "$TESTS_PROJECT")"
fi
