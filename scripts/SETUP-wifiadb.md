# Настройка беспроводного adb на второй рамке

Эта инструкция — для машины, которой **вторая рамка уже доверяет** (ниже —
`<имя доверенной машины>`). Её цель — доделать на второй рамке то же, что уже сделано на
первой: загнать ключ от рабочей машины (`<имя рабочей машины>`) и включить персистентный
adb по Wi-Fi, чтобы дальше сетапить/деплоить рамку вообще без USB и без этой машины.

Конкретные значения — адреса рамок, имена машин, публичный ключ — лежат в
`scripts/local.env`, который в репозиторий не попадает (шаблон — `local.env.template`).
Ниже они обозначены угловыми скобками; подставляйте свои.

Первую рамку (`<адрес первой рамки>`) уже настроили; вторая сейчас `unauthorized` из-под
`<имя рабочей машины>` — она принимает только ключи, которые ей «скормили».

## Как рамка авторизует adb

Рамка не показывает «разрешить отладку?» и принимает только ключи, лежащие в:

```
/data/misc/adb/adb_keys
```

Проверить, какие ключи уже скормлены (с машины, которой рамка доверяет):

```bash
adb shell su -c "cat /data/misc/adb/adb_keys"
```

Там две строки (видны на первой рамке):
```
... <имя доверенной машины>
... <имя рабочей машины>
```

## Шаг 1. Скормить рабочей машине ключ

Ключ, который надо добавить (`<публичный ключ рабочей машины>`; значение — в `local.env`,
поле `DEPLOY_MACHINE_PUBLIC_KEY`):

```
<ПУБЛИЧНЫЙ_КЛЮЧ_РАБОЧЕЙ_МАШИНЫ>
```

Добавить на рамку (рамка подключена по USB и `device` на этой машине):

```bash
adb root                          # userdebug-сборка позволяет
adb shell su -c "echo '<КЛЮЧ_ВЫШЕ>' >> /data/misc/adb/adb_keys"
adb shell su -c "chown root:root /data/misc/adb/adb_keys"
adb shell su -c "stop adbd; start adbd"   # чтобы adbd перечитал ключ
```

После этого рамка станет видна и с рабочей машины (в `adb devices` будет `device` вместо `unauthorized`).

## Шаг 2. Включить adb-tcp (один раз, по USB)

```bash
adb tcpip 5555
adb connect <IP_РАМКИ>:5555
```

IP второй рамки: `adb shell ip addr show wlan0 | grep 'inet '` (пока она в USB).

## Шаг 3. Поставить персистентный init-скрипт (чтобы adb-tcp переживал ребут)

Файл `scripts/wifiadb.rc` (уже в репозитории) кладём в `/system/etc/init/`:

```bash
adb root
adb push scripts/wifiadb.rc /data/local/tmp/wifiadb.rc
adb shell su -c "mount -o rw,remount /system \
  && cp /data/local/tmp/wifiadb.rc /system/etc/init/wifiadb.rc \
  && chown root:root /system/etc/init/wifiadb.rc \
  && chmod 644 /system/etc/init/wifiadb.rc \
  && mount -o ro,remount /system && echo OK"
```

Содержимое `scripts/wifiadb.rc`:

```
on property:sys.boot_completed=1
    setprop service.adb.tcp.port 5555
    stop adbd
    start adbd
```

## Шаг 4. Проверить на ребуте

```bash
adb reboot
# подождать ~40-60с, затем:
adb connect <IP_РАМКИ>:5555
adb devices     # должен быть <IP_РАМКИ>:5555 device
```

Если рамка поднялась по Wi-Fi после ребута — персистентный adb-tcp работает, USB больше не нужен.

## Дальше (с рабочей машины)

```bash
adb connect <IP_РАМКИ>:5555
FRAME=<IP_РАМКИ>:5555 bash scripts/deploy.sh        # одиночный деплой
bash scripts/deploy-lan.sh                           # на все найденные рамки
```

## Важно

- `adb tcpip` и init-запись требуют root (`adb root` или `su`) — у этих рамок `ro.debuggable=1`, root доступен.
- `/system` в read-only: сначала `mount -o rw,remount /system`, в конце вернуть `ro`.
- Если с подключённой второй рамкой видно несколько устройств (например, телефон на USB), одиночный деплой делай только с явным `FRAME=<IP>:5555`.
- Никогда не выбирай в recovery wipe/reset — только сам вход.
