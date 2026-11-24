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
legacy_supervisor=/var/jb/usr/libexec/castar-updater
app=/var/jb/Applications/CastarControl.app
watch=/var/jb/Library/LaunchDaemons/org.castar.watchdog.plist
legacy_jobs=(org.castar.updater org.castar.updater-recovery)
control=/var/jb/var/lib/castar-control
data=/var/jb/var/lib/castar
config=/var/jb/etc/castar/server.env
verify=/var/jb/etc/castar/.verify-update
activation=/var/jb/etc/castar/.activate-update
applying="$control/applying"
version_pattern='^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$'
data_files=(castar.sqlite castar.sqlite-wal castar.sqlite-shm castar.sqlite-journal
            castar.sqlite.settings.json castar.sqlite.keys.json)
version=''
recovering=0
if [[ $# == 1 && $1 == --recover ]]; then
    recovering=1
elif [[ $# == 0 ]]; then
    ldid=$(command -v "${LDID:-ldid}")
else
    printf '%s\n' 'Usage: install.sh [--recover]' >&2
    exit 1
fi
if [[ $recovering == 0 ]]; then
for tool in "$ldid" uicache launchctl sudo; do
    command -v "$tool" >/dev/null || { printf 'Missing tool: %s\n' "$tool" >&2; exit 1; }
done
for file in Castar.Server.dll castar-host castar-watchdog run-server.sh org.castar.server.plist \
            org.castar.watchdog.plist version \
            CastarControl.entitlements.plist CastarControl.app/CastarControl CastarControl.app/Info.plist \
            CastarControl.app/AppIcon60@2x.png CastarControl.app/AppIcon60@3x.png \
            lib/libcoreclr.dylib lib/libe_sqlite3.dylib lib/libSkiaSharp.dylib lib/libZXing.dylib; do
    [[ -f "$payload/$file" ]] || { printf 'Missing archive member: %s\n' "$file" >&2; exit 1; }
done
version=$(<"$payload/version")
if [[ ! $version =~ $version_pattern ]]; then
    printf '%s\n' 'Invalid Castar package version.' >&2
    exit 1
fi
fi

as_mobile() (
    cd /
    sudo -n -u mobile -- "$@"
)

if [[ $recovering == 0 ]]; then
install -d -o root -g wheel -m 0755 /var/jb/usr/libexec /var/jb/Applications
install -d -o root -g mobile -m 0750 /var/jb/etc/castar /var/jb/var/run/castar /var/jb/var/log/castar
install -d -o root -g wheel -m 0700 "$control"
install -d -o mobile -g mobile -m 0700 "$data"
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
fi

unload() {
    local description result=0 pid attempt
    description=$(launchctl print "system/$1" 2>/dev/null) || result=$?
    if [[ $result == 113 ]]; then return 0; fi
    if [[ $result != 0 ]]; then return 1; fi
    launchctl bootout "system/$1" || return 1
    if [[ $description =~ $'\n'[[:blank:]]+pid[[:blank:]]+=[[:blank:]]+([0-9]+) ]]; then
        pid=${BASH_REMATCH[1]}
        for ((attempt=0; attempt<30; attempt++)); do
            if ! kill -0 "$pid" 2>/dev/null; then return 0; fi
            sleep 1
        done
        return 1
    fi
}

disable_legacy_jobs() {
    local job description result
    for job in "${legacy_jobs[@]}"; do
        result=0
        description=$(launchctl print "system/$job" 2>/dev/null) || result=$?
        if [[ $result == 113 ]]; then continue; fi
        if [[ $result != 0 ]]; then return 1; fi
        if [[ $job == org.castar.updater && $description =~ $'\n'[[:blank:]]+pid[[:blank:]]+=[[:blank:]]+([0-9]+) ]]; then
            printf '%s\n' 'The previous Castar updater is still running. Retry after it exits.' >&2
            return 1
        fi
        touch "$stage/$job.loaded"
        unload "$job" || return 1
    done
}

restore_legacy_jobs() {
    local job
    for job in "${legacy_jobs[@]}"; do
        if [[ -f "$stage/$job.loaded" ]] && ! launchctl print "system/$job" >/dev/null 2>&1; then
            launchctl bootstrap system "/var/jb/Library/LaunchDaemons/$job.plist" || return 1
        fi
    done
}

finish_installation() {
    local job
    if [[ -f "$stage/manual-install" ]]; then
        for job in "${legacy_jobs[@]}"; do
            rm -f -- "/var/jb/Library/LaunchDaemons/$job.plist" || return 1
        done
        rm -f -- "$legacy_supervisor/install.sh" "$legacy_supervisor/update-server.sh" \
            "$legacy_supervisor/ldid" /var/jb/var/run/castar/update.json || return 1
    fi
    rm -f -- "$verify" "$activation" "$applying"
}

snapshot_data() {
    local file result
    mkdir "$stage/data"
    for file in "${data_files[@]}"; do
        result=0
        # Open mobile-controlled data only with mobile privileges. Root owns the snapshot.
        as_mobile /var/jb/usr/bin/bash -c '
            [[ ! -L "$1" ]] || exit 1
            [[ -e "$1" ]] || exit 3
            [[ -f "$1" ]] && cat -- "$1"
        ' bash "$data/$file" >"$stage/data/$file" || result=$?
        case $result in
            0) ;;
            3) rm -f -- "$stage/data/$file" ;;
            *) return 1 ;;
        esac
    done
}

restore_data() {
    local file
    for file in "${data_files[@]}"; do
        if [[ -f "$stage/data/$file" ]]; then
            as_mobile /var/jb/usr/bin/bash -c '
                set -eu
                umask 077
                temporary=$(mktemp "$1.rollback.XXXXXXXX")
                trap '\''rm -f -- "$temporary"'\'' EXIT
                cat >"$temporary"
                rm -f -- "$1"
                mv -- "$temporary" "$1"
            ' bash "$data/$file" <"$stage/data/$file" || return 1
        else
            as_mobile rm -f -- "$data/$file" || return 1
        fi
    done
}

wait_for_version() {
    local attempt
    for ((attempt=0; attempt<30; attempt++)); do
        if "$server/castar-watchdog" --check-version "$version"; then return 0; fi
        sleep 1
    done
    return 1
}

lock="$control/install.lock"
stage=''
boot_id() {
    local boot
    boot=$(sysctl -n kern.boottime 2>/dev/null) || boot=''
    if [[ $boot =~ sec[[:space:]]*=[[:space:]]*([0-9]+) ]]; then
        printf '%s\n' "${BASH_REMATCH[1]}"
    else
        printf '%s\n' unknown
    fi
}

unloaded=0
snapshot=0
swapped=0
verify_created=0
applying_created=0
activation_created=0
restart_watchdog=0
restore() {
    local status=$? restored=1
    trap - EXIT HUP INT TERM
    set +e
    if [[ $status != 0 && -f "$stage/finished" ]]; then
        # Preserve the committed installation for completion with --recover.
        exit 70
    fi
    if [[ $status != 0 && $unloaded == 1 ]]; then
        printf '%s\n' 'Installation failed. Restoring the previous installation.' >&2
        if [[ $swapped == 1 && ! -f "$stage/rollback-restored" ]]; then
            unload org.castar.watchdog || restored=0
            unload org.castar.server || restored=0
        fi
        if [[ $verify_created == 1 ]]; then rm -f -- "$verify" || restored=0; fi
        if [[ $swapped == 1 && $restored == 1 && ! -f "$stage/rollback-restored" ]]; then
            if [[ ! -e "$stage/server" && ! -e "$stage/failed-server" && -e "$server" ]]; then
                mv "$server" "$stage/failed-server" || restored=0
            fi
            if [[ -e "$stage/previous-server" ]]; then mv "$stage/previous-server" "$server" || restored=0; fi
            if [[ ! -e "$stage/app" && ! -e "$stage/failed-app" && -e "$app" ]]; then
                uicache -u "$app"
                mv "$app" "$stage/failed-app" || restored=0
            fi
            if [[ -e "$stage/previous-app" ]]; then mv "$stage/previous-app" "$app" || restored=0; fi
            if [[ -e "$app" ]]; then uicache -p "$app" || restored=0; fi
            if [[ ! -e "$stage/watchdog.plist" && ! -e "$stage/failed-watchdog.plist" && -e "$watch" ]]; then
                mv "$watch" "$stage/failed-watchdog.plist" || restored=0
            fi
            if [[ -e "$stage/previous-watchdog.plist" ]]; then mv "$stage/previous-watchdog.plist" "$watch" || restored=0; fi
            if [[ $snapshot == 1 ]]; then restore_data || restored=0; fi
        fi
        if [[ $restored == 1 ]]; then
            touch "$stage/rollback-restored" && sync || restored=0
        fi
        if [[ $restored == 1 && $activation_created == 1 ]]; then rm -f -- "$activation" || restored=0; fi
        if [[ $restored == 1 && $applying_created == 1 ]]; then rm -f -- "$applying" || restored=0; fi
        if [[ $restart_watchdog == 1 && $restored == 1 && -f "$watch" ]] &&
            ! launchctl print system/org.castar.watchdog >/dev/null 2>&1; then
            launchctl bootstrap system "$watch" || restored=0
        fi
        if [[ $restored == 1 ]]; then restore_legacy_jobs || restored=0; fi
        if [[ $restored == 0 ]]; then
            printf 'Automatic rollback failed. Backup files: %s\n' "$stage" >&2
        fi
    fi
    if [[ $restored == 0 ]]; then exit 70; fi
    # Clean up the lock only after all displaced files have been restored or the new version committed.
    rm -rf -- "$stage"
    rm -f -- "$lock"
    if [[ $recovering == 1 ]]; then exit 0; fi
    exit "$status"
}

if [[ $recovering == 1 ]]; then
    [[ -f "$lock" ]] || exit 0
    read -r name owner owner_boot <"$lock"
    [[ $name =~ ^\.castar-install\.[[:alnum:]]{8}$ && $owner =~ ^[1-9][0-9]{0,8}$ ]]
    current_boot=$(boot_id)
    if [[ $current_boot == unknown || $owner_boot == unknown || $current_boot == "$owner_boot" ]] &&
        kill -0 "$owner" 2>/dev/null; then
        exit 0
    fi
    stage="/var/jb/usr/libexec/$name"
    if [[ -f "$stage/finished" ]]; then
        finish_installation
        rm -rf -- "$stage"
        rm -f -- "$lock"
        exit 0
    fi
    if [[ ! -d "$stage" ]]; then
        rm -f -- "$verify" "$activation" "$applying" "$lock"
        exit 0
    fi
    if [[ -f "$stage/armed" ]]; then
        unloaded=1 restart_watchdog=1 verify_created=1 applying_created=1 activation_created=1
        if [[ -f "$stage/swapping" ]]; then swapped=1; fi
        if [[ -f "$stage/data-ready" ]]; then snapshot=1; fi
    fi
    trap restore EXIT
    exit 1
fi

stage=$(mktemp -d /var/jb/usr/libexec/.castar-install.XXXXXXXX)
lock_record=$(mktemp "$control/.install-owner.XXXXXXXX")
printf '%s %s %s\n' "${stage##*/}" "$$" "$(boot_id)" >"$lock_record"
if ! ln "$lock_record" "$lock"; then
    rm -f -- "$lock_record"
    rm -rf -- "$stage"
    printf '%s\n' 'Castar installation is already in progress.' >&2
    exit 1
fi
rm -f -- "$lock_record"
trap restore EXIT
trap 'exit 1' HUP INT TERM

touch "$stage/manual-install"
install -d -m 0755 "$stage/server"
cp -R "$payload/lib" "$stage/server/lib"
cp -R "$payload/CastarControl.app" "$stage/app"
chown -R root:wheel "$stage/server" "$stage/app"
find "$stage/server" "$stage/app" -type d -exec chmod 0755 {} +
find "$stage/server" "$stage/app" -type f -exec chmod 0644 {} +
for file in Castar.Server.dll run-server.sh org.castar.server.plist version; do
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

touch "$stage/armed"
sync
unloaded=1
restart_watchdog=1
unload org.castar.watchdog
unload org.castar.server
disable_legacy_jobs
as_mobile true
snapshot_data
snapshot=1
touch "$stage/data-ready"
install -o root -g wheel -m 0600 /dev/null "$applying"
applying_created=1
install -o root -g wheel -m 0644 /dev/null "$activation"
activation_created=1
install -o root -g wheel -m 0644 /dev/null "$verify"
verify_created=1
touch "$stage/swapping"
sync
swapped=1
if [[ -e "$server" ]]; then mv "$server" "$stage/previous-server"; fi
if [[ -e "$app" ]]; then mv "$app" "$stage/previous-app"; fi
if [[ -e "$watch" ]]; then mv "$watch" "$stage/previous-watchdog.plist"; fi
mv "$stage/server" "$server"
mv "$stage/app" "$app"
mv "$stage/watchdog.plist" "$watch"
uicache -p "$app"
launchctl bootstrap system "$watch"
launchctl bootstrap system "$server/org.castar.server.plist"
wait_for_version
unload org.castar.server
rm -f -- "$verify"
verify_created=0
if [[ ! -e "$control/stopped" ]]; then
    launchctl bootstrap system "$server/org.castar.server.plist"
    wait_for_version
fi
touch "$stage/finished"
sync
finish_installation
printf '%s\n' 'Castar installed. Open Castar for service status.'
