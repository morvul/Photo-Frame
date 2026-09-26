#!/usr/bin/env bash
#
# Приводит часы рамки в порядок: сервер времени, часовой пояс и само время.
#
#   ./fix-time.sh                     пояс не трогать, выставить время по компьютеру
#   ./fix-time.sh Europe/Minsk        заодно задать часовой пояс
#
# Зачем это вообще нужно. На обеих рамках auto_time включено, но ntp_server пуст —
# спрашивать время не у кого, и часы просто уходят. Пояс тоже попадался чужой:
# Europe/Helsinki вместо Europe/Minsk, а разница видна только зимой, когда в одном
# из них наступает переход на летнее время. Ночные часы висят на стене всю ночь,
# так что расхождение замечаешь именно тогда, когда исправить труднее всего.
#
# Нужен root (su) — на этих рамках он есть.

. "$(dirname "$0")/common.sh"

timezone="${1:-}"

echo "Сервер времени..."
frame shell "settings put global ntp_server pool.ntp.org; settings put global ntp_timeout 20000"

if [ -n "$timezone" ]; then
    echo "Часовой пояс: $timezone"
    frame shell "su -c 'setprop persist.sys.timezone $timezone; am broadcast -a android.intent.action.TIMEZONE_CHANGED'" >/dev/null
fi

echo "Время по компьютеру..."
frame shell "su -c 'date $(date '+%m%d%H%M%Y.%S'); am broadcast -a android.intent.action.TIME_SET'" >/dev/null

sleep 2
echo
echo "компьютер $(date '+%H:%M:%S %z')"
echo "рамка     $(frame shell date '+%H:%M:%S %z' | tr -d '\r')  $(frame shell getprop persist.sys.timezone | tr -d '\r')"
