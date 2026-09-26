#!/usr/bin/env bash
#
# Ищет рамки в локальной сети (adb поверх Wi-Fi, порт 5555) и подключает их.
#
# Разовая подготовка на каждой рамке — по USB, один раз, до первого сна рамки:
#   adb tcpip 5555
#
#   ./lan-discover.sh                  угадать подсеть по ipconfig, просканировать её
#   SUBNET=192.168.2 ./lan-discover.sh    явно задать первые три октета подсети
#
# Только /24 — рамки живут в домашней сети, а не за VPN с маской /8: там сканировать
# нечего, и 16 миллионов адресов не просканируешь за разумное время.

. "$(dirname "$0")/common.sh"

guess_subnet() {
    ipconfig 2>/dev/null | awk '
        /IPv4 Address/ { ip = $0; sub(/.*: /, "", ip) }
        /Subnet Mask/ {
            mask = $0; sub(/.*: /, "", mask)
            if (mask == "255.255.255.0" && ip !~ /^169\.254\./) {
                print ip
                exit
            }
        }
    ' | sed -E 's/\.[0-9]{1,3}$//'
}

subnet="${SUBNET:-$(guess_subnet)}"
[ -n "$subnet" ] || die "Не удалось определить подсеть. Укажите явно: SUBNET=192.168.2 $(basename "$0")"

echo "Подсеть: $subnet.0/24, порт 5555..."

# Сканирование через 254 отдельных процесса bash — на Windows это сотни
# запусков MSYS-процесса, и это заняло больше двух минут даже при 64 параллельных
# ветках. PowerShell перебирает адреса в одном процессе, с неблокирующим
# BeginConnect и коротким таймаутом на каждый — на порядок быстрее.
found="$(powershell.exe -NoProfile -Command "
    \$subnet = '$subnet'
    1..254 | ForEach-Object {
        \$ip = \"\$subnet.\$_\"
        \$client = New-Object System.Net.Sockets.TcpClient
        try {
            \$result = \$client.BeginConnect(\$ip, 5555, \$null, \$null)
            if (\$result.AsyncWaitHandle.WaitOne(150)) {
                \$client.EndConnect(\$result)
                Write-Output \$ip
            }
        } catch {
        } finally {
            \$client.Close()
        }
    }
" | tr -d '\r')"

if [ -z "$found" ]; then
    die "Ни одной рамки не найдено на $subnet.0/24:5555. Проверьте adb tcpip 5555 по USB и что рамка не спит."
fi

for ip in $found; do
    echo "Найдена $ip:5555 — подключаюсь..."
    "$ADB" connect "$ip:5555" >/dev/null
done

echo
list_frames
