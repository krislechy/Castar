#!/var/jb/usr/bin/bash
set -euo pipefail
umask 077
export PATH=/var/jb/usr/bin:/var/jb/usr/sbin:/usr/bin:/usr/sbin:/bin:/sbin

if [[ $(id -u) != 0 || ! -d /var/jb/Library/LaunchDaemons ]]; then
    printf '%s\n' 'Run as root on a rootless-jailbroken iPhone.' >&2
    exit 1
fi

payload=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
server=/var/jb/usr/libexec/castar
app=/var/jb/Applications/CastarControl.app
watch=/var/jb/Library/LaunchDaemons/org.castar.watchdog.plist
config=/var/jb/etc/castar/server.env
ldid=${LDID:-ldid}
for tool in "$ldid" uicache launchctl; do
    command -v "$tool" >/dev/null || { printf 'Missing tool: %s\n' "$tool" >&2; exit 1; }
done
for file in Castar.Server.dll castar-host castar-watchdog run-server.sh org.castar.server.plist \
            org.castar.watchdog.plist CastarControl.entitlements.plist CastarControl.app/CastarControl \
            CastarControl.app/Info.plist CastarControl.app/AppIcon60@2x.png CastarControl.app/AppIcon60@3x.png \
            lib/libcoreclr.dylib lib/libe_sqlite3.dylib lib/libSkiaSharp.dylib lib/libZXing.dylib; do
    [[ -f "$payload/$file" ]] || { printf 'Missing archive member: %s\n' "$file" >&2; exit 1; }
done

install -d -o root -g wheel -m 0755 /var/jb/usr/libexec /var/jb/Applications
install -d -o root -g mobile -m 0750 /var/jb/etc/castar /var/jb/var/run/castar /var/jb/var/log/castar
install -d -o root -g wheel -m 0700 /var/jb/var/lib/castar-control
install -d -o mobile -g mobile -m 0700 /var/jb/var/lib/castar
if [[ ! -e "$config" ]]; then
    key=$(od -An -v -N32 -tx1 /dev/urandom | tr -d '[:space:]')
    [[ ${#key} == 64 ]]
    printf 'Castar__ApiKey=%s\nCastar__Telegram__Enabled=false\nCastar__Receipts__Provider=serbia\n' "$key" >"$config"
    chown root:mobile "$config"
    chmod 0640 "$config"
fi
for log in server watchdog; do
    file="/var/jb/var/log/castar/$log.log"
    if [[ ! -e "$file" ]]; then
        install -o mobile -g mobile -m 0600 /dev/null "$file"
    fi
done

unload() {
    if launchctl print "system/$1" >/dev/null 2>&1; then
        launchctl bootout "system/$1"
    fi
}

stage=$(mktemp -d /var/jb/usr/libexec/.castar-install.XXXXXXXX)
activated=0
unloaded=0
restore() {
    status=$?
    trap - EXIT
    if [[ $status != 0 && $unloaded == 1 ]]; then
        printf '%s\n' 'Installation failed. Restoring the previous binaries.' >&2
        set +e
        unload org.castar.watchdog
        unload org.castar.server
        if [[ $activated == 1 ]]; then mv "$watch" "$stage/failed-watchdog.plist"; fi
        if [[ -e "$stage/previous-watchdog.plist" ]]; then mv "$stage/previous-watchdog.plist" "$watch"; fi
        if [[ -e "$stage/previous-server" || ! -e "$stage/server" ]]; then
            if [[ -e "$server" ]]; then mv "$server" "$stage/failed-server"; fi
            if [[ -e "$stage/previous-server" ]]; then mv "$stage/previous-server" "$server"; fi
        fi
        if [[ -e "$stage/previous-app" || ! -e "$stage/app" ]]; then
            if [[ -e "$app" ]]; then
                uicache -u "$app"
                mv "$app" "$stage/failed-app"
            fi
            if [[ -e "$stage/previous-app" ]]; then
                mv "$stage/previous-app" "$app"
                uicache -p "$app"
            fi
        fi
        if [[ -f "$watch" ]]; then launchctl bootstrap system "$watch"; fi
        printf 'Installation files: %s\n' "$stage" >&2
    else
        rm -rf -- "$stage"
    fi
    exit "$status"
}
trap restore EXIT

install -d -m 0755 "$stage/server"
cp -R "$payload/lib" "$stage/server/lib"
cp -R "$payload/CastarControl.app" "$stage/app"
chown -R root:wheel "$stage/server" "$stage/app"
find "$stage/server" "$stage/app" -type d -exec chmod 0755 {} +
find "$stage/server" "$stage/app" -type f -exec chmod 0644 {} +
for file in Castar.Server.dll run-server.sh org.castar.server.plist; do
    install -o root -g wheel -m 0644 "$payload/$file" "$stage/server/$file"
done
for file in castar-host castar-watchdog; do
    install -o root -g wheel -m 0755 "$payload/$file" "$stage/server/$file"
    "$ldid" -S "$stage/server/$file"
done
for file in "$stage/server"/lib/*.dylib; do "$ldid" -S "$file"; done
chmod 0755 "$stage/app/CastarControl"
"$ldid" "-S$payload/CastarControl.entitlements.plist" "$stage/app/CastarControl"
install -o root -g wheel -m 0644 "$payload/org.castar.watchdog.plist" "$stage/watchdog.plist"

unloaded=1
unload org.castar.watchdog
unload org.castar.server
if [[ -e "$server" ]]; then mv "$server" "$stage/previous-server"; fi
if [[ -e "$app" ]]; then mv "$app" "$stage/previous-app"; fi
if [[ -e "$watch" ]]; then mv "$watch" "$stage/previous-watchdog.plist"; fi
mv "$stage/server" "$server"
mv "$stage/app" "$app"
mv "$stage/watchdog.plist" "$watch"
activated=1
uicache -p "$app"
launchctl bootstrap system "$watch"
printf '%s\n' 'Castar installed. Open Castar for service status.'
