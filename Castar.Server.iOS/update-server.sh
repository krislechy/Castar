#!/var/jb/usr/bin/bash
set -euo pipefail
umask 077
export PATH=/var/jb/usr/bin:/var/jb/usr/sbin:/usr/bin:/usr/sbin:/bin:/sbin

[[ $(id -u) == 0 ]]
server=/var/jb/usr/libexec/castar
supervisor=/var/jb/usr/libexec/castar-updater
control=/var/jb/var/lib/castar-control
status=/var/jb/var/run/castar/update.json
version_pattern='^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$'
work=''
installed='null'
target='null'
downloaded=0
total=0
reported=0
error='"Не удалось подготовить обновление."'
progress=''

read_installed_version() {
    local value
    installed='null'
    if [[ -f "$server/version" ]]; then
        value=$(<"$server/version")
        if [[ $value =~ $version_pattern ]]; then installed="\"$value\""; fi
    fi
}

write_status() {
    local temporary
    temporary=$(mktemp "$status.XXXXXXXX") || return 1
    if printf '{"state":"%s","installedVersion":%s,"version":%s,"downloadedBytes":%s,"totalBytes":%s,"error":%s}\n' \
        "$1" "$installed" "$target" "$downloaded" "$total" "$2" >"$temporary" && mv -f -- "$temporary" "$status"; then
        return 0
    fi
    rm -f -- "$temporary"
    return 1
}

read_progress() {
    if [[ -f "$status" ]]; then
        progress=$(<"$status")
        if [[ $progress =~ \"downloadedBytes\":[[:space:]]*([0-9]{1,18}) ]]; then downloaded=${BASH_REMATCH[1]}; fi
        if [[ $progress =~ \"totalBytes\":[[:space:]]*([0-9]{1,18}) ]]; then total=${BASH_REMATCH[1]}; fi
    fi
}

if [[ $# == 1 && $1 == --recover ]]; then
    activation=/var/jb/etc/castar/.activate-update
    if [[ -f "$activation" && ! -f "$control/install.lock" ]]; then rm -f -- "$activation"; fi
    result=0
    if [[ -f "$control/install.lock" ]]; then
        /var/jb/usr/bin/bash "$supervisor/install.sh" --recover || result=$?
    fi
    read_installed_version
    read_progress
    case $result in
        0)
            # The installer may have committed and released the lock before the supervisor wrote its final status.
            if [[ ! -f "$control/install.lock" && $progress =~ \"state\":[[:space:]]*\"installing\" ]] &&
                [[ $progress =~ \"version\":[[:space:]]*\"([0-9]+\.[0-9]+\.[0-9]+)\" ]] &&
                [[ $installed == "\"${BASH_REMATCH[1]}\"" ]]; then
                target=$installed
                write_status complete null
            fi ;;
        3) write_status failed '"Обновление прервано. Прежняя версия восстановлена."' ;;
        4) target=$installed; write_status complete null ;;
        *) write_status failed '"Не удалось восстановить прежнюю версию."' ;;
    esac
    exit 0
fi
[[ $# == 0 ]]
read_installed_version

finish() {
    local result=$?
    trap - EXIT HUP INT TERM
    if [[ $result != 0 && $reported == 0 ]]; then write_status failed "$error" || true; fi
    if [[ -n "$work" ]]; then rm -rf -- "$work"; fi
    exit "$result"
}
trap finish EXIT
trap 'exit 1' HUP INT TERM

write_status checking null
work=$(mktemp -d "$control/update.XXXXXXXX")
result=0
"$server/castar-host" --stage-server-update "$work" "$status" || result=$?
read_progress
if [[ $result == 3 ]]; then
    write_status current null
    exit 0
fi
if [[ $result != 0 && ${progress:-} =~ \"state\":[[:space:]]*\"failed\" ]]; then reported=1; fi
[[ $result == 0 ]]
version=$(<"$work/version")
[[ $version =~ $version_pattern ]]
target="\"$version\""
error='"Не удалось установить обновление."'

result=0
/var/jb/usr/bin/bash "$work/payload/install.sh" --update "$version" || result=$?
if [[ $result == 70 ]]; then error='"Не удалось восстановить прежнюю версию."'; fi
[[ $result == 0 ]]
installed=$target
write_status complete null
