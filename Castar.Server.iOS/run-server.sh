#!/var/jb/usr/bin/bash
set -eu
umask 077
set -a
. /var/jb/etc/castar/server.env
if [ -f /var/jb/etc/castar/https.env ]; then
    . /var/jb/etc/castar/https.env
fi
set +a
export ASPNETCORE_URLS=http://127.0.0.1:5080
export Castar__UpdateActivationGate=/var/jb/etc/castar/.activate-update
if [ -f /var/jb/etc/castar/.verify-update ]; then
    export Castar__UpdateVerification=true
elif [ -n "${Castar__ClientAddress:-}" ]; then
    if [[ "$Castar__ClientAddress" != https://* || "$Castar__ClientAddress" == *';'* ]]; then
        printf '%s\n' 'Castar__ClientAddress must be a single HTTPS address.' >&2
        exit 2
    fi
    ASPNETCORE_URLS="$ASPNETCORE_URLS;$Castar__ClientAddress"
fi
export Database__SqlitePath=/var/jb/var/lib/castar/castar.sqlite
export Castar__LocalAdminDirectory=/var/jb/var/lib/castar
export Logging__LogLevel__Default="${Logging__LogLevel__Default:-Warning}"
exec >>/var/jb/var/log/castar/server.log 2>&1
printf '%s\n' 'Starting Castar Server'
exec /var/jb/usr/libexec/castar/castar-host
