#!/usr/bin/env bash
#
# Общая часть всех скриптов: где лежит adb, какая рамка подключена, где что в репозитории.
# Подключается строкой: . "$(dirname "$0")/common.sh"
#
# Отдельным файлом, а не копией в каждом скрипте: путь к adb и ключ отладки на этой
# машине нестандартные, и держать их в одном месте проще, чем править семь скриптов.

set -euo pipefail

# Пути внутри устройства Git Bash не должен превращать в windows-пути: /sdcard/x
# иначе становится C:/Program Files/Git/sdcard/x, и adb такого файла не находит.
export MSYS_NO_PATHCONV=1

# --- рамка -------------------------------------------------------------------

# Имя пакета и точка входа. Класс активности сгенерирован MAUI, отсюда и crc64 в имени.
PACKAGE="com.morvul.photoframe"
ACTIVITY="$PACKAGE/crc64b2981d3bddd09cc7.MainActivity"

# Куда приложение пишет журнал состояния (виден и с компьютера по USB).
FRAME_LOG="/storage/emulated/0/PhotoFrame/Logs/frame.log"

# --- репозиторий -------------------------------------------------------------

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_PROJECT="$REPO_ROOT/PhotoFrame/PhotoFrame/PhotoFrame.csproj"
TESTS_PROJECT="$REPO_ROOT/PhotoFrame/PhotoFrame.Core.Tests/PhotoFrame.Core.Tests.csproj"
APK="$REPO_ROOT/PhotoFrame/PhotoFrame/bin/Release/net10.0-android/$PACKAGE-Signed.apk"

# --- локальные данные --------------------------------------------------------

# Адреса рамок, имена машин и путь к ключу отладки — не для репозитория: они
# лежат в local.env рядом с этим файлом (шаблон — local.env.template, сам
# local.env в .gitignore). Нет файла — работаем без него: всё, что нужно,
# можно задать снаружи переменными окружения.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "$SCRIPT_DIR/local.env" ]; then
    . "$SCRIPT_DIR/local.env"
fi

# --- adb ---------------------------------------------------------------------

# Ключ отладки: рамка не показывает запрос на разрешение отладки вовсе, и подходит
# только тот ключ, который ей однажды скормили. Путь берётся из local.env, а
# переопределить его можно снаружи через ADB_VENDOR_KEYS.
: "${ADB_VENDOR_KEYS:=${ADB_VENDOR_KEYS_PATH:-$HOME/.android/adbkey}}"
export ADB_VENDOR_KEYS

# adb из PATH, иначе из стандартного места установки SDK.
if command -v adb >/dev/null 2>&1; then
    ADB="$(command -v adb)"
elif [ -x "/c/Program Files (x86)/Android/android-sdk/platform-tools/adb.exe" ]; then
    ADB="/c/Program Files (x86)/Android/android-sdk/platform-tools/adb.exe"
else
    echo "adb не найден. Добавьте platform-tools в PATH либо задайте ADB=путь" >&2
    exit 1
fi

# Путь в виде, понятном программам Windows.
#
# dotnet — программа Windows и путь вида /c/GIT/... принимает за ключ командной
# строки: «Switch: /c/GIT/...». adb с такими путями работает, а вот сборке нужен
# обычный C:/GIT/...
win_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        echo "$1"
    fi
}

die() {
    echo "$*" >&2
    exit 1
}

# Список подключённых рамок, по одному серийному номеру в строке.
list_frames() {
    "$ADB" devices | awk 'NR>1 && $2=="device" {print $1}'
}

# Серийный номер рамки, с которой работаем.
#
# Одна подключена — берём её. Несколько — нужно сказать, какую: FRAME=серийник либо
# первым аргументом -s серийник. Молча выбирать одну из двух нельзя, обновление
# ушло бы не туда.
resolve_frame() {
    if [ -n "${FRAME:-}" ]; then
        echo "$FRAME"
        return
    fi

    local frames
    frames="$(list_frames)"

    case "$(echo "$frames" | grep -c .)" in
        0) die "Рамка не подключена. Проверьте кабель: у этих рамок он часто только для питания." ;;
        1) echo "$frames" ;;
        *) die "Подключено несколько рамок:
$frames
Укажите нужную: FRAME=<серийник> $(basename "$0")" ;;
    esac
}

# adb для выбранной рамки: frame shell ... , frame install ... и так далее.
frame() {
    "$ADB" -s "$(resolve_frame)" "$@"
}
