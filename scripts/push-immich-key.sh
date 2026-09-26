#!/usr/bin/env bash
#
# Кладёт файл с ключом доступа Immich на рамку, откуда его подхватит экран настроек.
#
#   ./push-immich-key.sh ~/immich.key
#
# Ключ передаётся файлом, а не аргументом: сорок случайных символов в командной
# строке остались бы в её истории. Набирать их на самой рамке пультом по экранной
# клавиатуре тем более не вариант — для этого файл и придуман.
#
# Файл сильнее сохранённого ключа: положили новый — на экране настроек он подставится
# сам, останется нажать «Сохранить».

. "$(dirname "$0")/common.sh"

key_file="${1:-}"
[ -n "$key_file" ] || die "Укажите файл с ключом: ./push-immich-key.sh <файл>"
[ -f "$key_file" ] || die "Файла нет: $key_file"

size="$(stat -c %s "$key_file")"
[ "$size" -gt 0 ] || die "Файл пуст: $key_file"
[ "$size" -lt 512 ] || die "Слишком велик для ключа ($size байт) — тот файл?"

frame shell "mkdir -p /storage/emulated/0/PhotoFrame"
frame push "$(win_path "$key_file")" "/storage/emulated/0/PhotoFrame/immich.key" >/dev/null 2>&1
echo "Положено на рамку: PhotoFrame/immich.key ($size байт)"
echo
echo "Дальше на самой рамке: ⚙ → ИСТОЧНИК СНИМКОВ → Immich."
echo "Поле ключа заполнится само, нажмите «Сохранить»."
