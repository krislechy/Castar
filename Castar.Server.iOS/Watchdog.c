#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <netinet/in.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <spawn.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define CONTROL_SOCKET "/var/jb/var/run/castar/control.sock"
#define STATE_DIRECTORY "/var/jb/var/lib/castar-control"
#define DISABLED_FILE STATE_DIRECTORY "/stopped"
#define SERVER_LOG "/var/jb/var/log/castar/server.log"
#define WATCHDOG_LOG "/var/jb/var/log/castar/watchdog.log"
#define LAUNCHCTL "/var/jb/usr/bin/launchctl"
#define INSTALL_MARKER STATE_DIRECTORY "/applying"
#define SERVER_JOB "system/org.castar.server"
#define SERVER_PLIST "/var/jb/usr/libexec/castar/org.castar.server.plist"
#define HEALTH_PORT 5080

enum {
    healthy_probe_interval = 25000,
    stopped_probe_interval = 300000,
    retry_probe_interval = 3000,
    recovery_interval = 30000,
    job_check_interval = 300000,
    log_interval = 300000,
    failed_probes_before_recovery = 3
};

struct supervision {
    const char *state;
    unsigned int failures;
    int64_t next_probe;
    int64_t recovery_at;
    int64_t job_check_at;
};

extern char **environ;

static int64_t monotonic_milliseconds(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static int launchctl(const char *operation, const char *first, const char *second)
{
    char *arguments[] = {(char *)LAUNCHCTL, (char *)operation, (char *)first,
                         (char *)second, NULL};
    posix_spawn_file_actions_t actions;
    if (posix_spawn_file_actions_init(&actions) != 0)
        return -1;
    int result = posix_spawn_file_actions_addopen(&actions, STDOUT_FILENO, "/dev/null", O_WRONLY, 0);
    if (result == 0)
        result = posix_spawn_file_actions_addopen(&actions, STDERR_FILENO, "/dev/null", O_WRONLY, 0);
    pid_t child = 0;
    if (result == 0)
        result = posix_spawn(&child, LAUNCHCTL, &actions, NULL, arguments, environ);
    posix_spawn_file_actions_destroy(&actions);
    if (result != 0)
        return -1;

    int status;
    for (int attempt = 0; attempt < 30; attempt++) {
        const pid_t completed = waitpid(child, &status, WNOHANG);
        if (completed == child)
            return WIFEXITED(status) ? WEXITSTATUS(status) : -1;
        if (completed < 0 && errno != EINTR)
            return -1;
        usleep(100000);
    }
    kill(child, SIGKILL);
    while (waitpid(child, NULL, 0) < 0 && errno == EINTR) {}
    return -1;
}

enum job_state { job_loaded, job_absent, job_unknown };

static enum job_state service_job(void)
{
    const int result = launchctl("print", SERVER_JOB, NULL);
    // launchctl on the target reports a missing service as 113.
    return result == 0 ? job_loaded : result == 113 ? job_absent : job_unknown;
}

static bool stop_server(void)
{
    return service_job() == job_absent || launchctl("bootout", SERVER_JOB, NULL) == 0 ||
        service_job() == job_absent;
}

static bool start_server(enum job_state job)
{
    switch (job) {
        case job_loaded: return launchctl("kickstart", "-k", SERVER_JOB) == 0;
        case job_absent: return launchctl("bootstrap", "system", SERVER_PLIST) == 0;
        default: return false;
    }
}

static bool save_stopped(bool stopped)
{
    const int directory = open(STATE_DIRECTORY, O_RDONLY | O_DIRECTORY | O_NOFOLLOW);
    if (directory == -1)
        return false;
    bool saved;
    if (stopped) {
        const int file = open(DISABLED_FILE, O_WRONLY | O_CREAT | O_NOFOLLOW, 0600);
        saved = file >= 0 && fsync(file) == 0;
        if (file >= 0) close(file);
    } else {
        saved = unlink(DISABLED_FILE) == 0 || errno == ENOENT;
    }
    if (saved)
        saved = fsync(directory) == 0;
    close(directory);
    return saved;
}

static bool write_all(int descriptor, const char *data, size_t length)
{
    while (length > 0) {
        const ssize_t written = write(descriptor, data, length);
        if (written < 0 && errno == EINTR)
            continue;
        if (written <= 0)
            return false;
        data += written;
        length -= (size_t)written;
    }
    return true;
}

static void send_text(int descriptor, const char *value)
{
    write_all(descriptor, value, strlen(value));
}

static void set_socket_timeout(int descriptor)
{
    const int enabled = 1;
    const struct timeval timeout = {.tv_sec = 1};
    setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &enabled, sizeof(enabled));
    setsockopt(descriptor, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
    setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
}

static bool wait_socket(int descriptor, short events, int64_t deadline)
{
    struct pollfd event = {.fd = descriptor, .events = events};
    for (;;) {
        const int64_t remaining = deadline - monotonic_milliseconds();
        if (remaining <= 0)
            return false;
        const int result = poll(&event, 1, (int)remaining);
        if (result >= 0)
            return result > 0 && (event.revents & (events | POLLHUP));
        if (errno != EINTR)
            return false;
    }
}

static bool health_ok(const char *version)
{
    const int connection = socket(AF_INET, SOCK_STREAM, 0);
    if (connection < 0)
        return false;
    set_socket_timeout(connection);
    const struct sockaddr_in address = {
        .sin_len = sizeof(address), .sin_family = AF_INET, .sin_port = htons(HEALTH_PORT),
        .sin_addr.s_addr = htonl(INADDR_LOOPBACK)
    };
    static const char request[] = "GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
    bool healthy = false;
    const int64_t deadline = monotonic_milliseconds() + 1000;
    fcntl(connection, F_SETFL, O_NONBLOCK);
    bool connected = connect(connection, (const struct sockaddr *)&address, sizeof(address)) == 0;
    if (!connected && errno == EINPROGRESS && wait_socket(connection, POLLOUT, deadline)) {
        int error = 0;
        socklen_t length = sizeof(error);
        connected = getsockopt(connection, SOL_SOCKET, SO_ERROR, &error, &length) == 0 && error == 0;
    }
    if (connected && write_all(connection, request, sizeof(request) - 1)) {
        char response[1024] = {0};
        size_t received = 0;
        while (received < sizeof(response) - 1 && wait_socket(connection, POLLIN, deadline)) {
            const ssize_t count = read(connection, response + received, sizeof(response) - 1 - received);
            if (count <= 0)
                break;
            received += (size_t)count;
        }
        const char *body = strstr(response, "\r\n\r\n");
        healthy = body && strstr(body, "\"status\":\"ready\"") &&
            (strncmp(response, "HTTP/1.1 200 ", 13) == 0 ||
             strncmp(response, "HTTP/1.0 200 ", 13) == 0);
        if (healthy && version) {
            char field[100];
            const int length = snprintf(field, sizeof(field), "\"version\":\"%s\"", version);
            healthy = length > 0 && (size_t)length < sizeof(field) && strstr(body, field);
        }
    }
    close(connection);
    return healthy;
}

static void send_log(int client)
{
    const int log = open(SERVER_LOG, O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    if (log < 0)
        return;
    char tail[16384];
    struct stat info;
    if (fstat(log, &info) == 0 && S_ISREG(info.st_mode)) {
        const off_t offset = info.st_size > (off_t)sizeof(tail) ? info.st_size - (off_t)sizeof(tail) : 0;
        const ssize_t length = pread(log, tail, sizeof(tail), offset);
        if (length > 0) {
            char *start = tail;
            if (offset != 0) {
                char *next_line = memchr(tail, '\n', (size_t)length);
                start = next_line ? next_line + 1 : tail + length;
            }
            write_all(client, start, (size_t)(tail + length - start));
        }
    }
    close(log);
}

static void limit_log(const char *path)
{
    const int log = open(path, O_RDWR | O_NOFOLLOW | O_NONBLOCK);
    if (log < 0)
        return;
    struct stat info;
    char tail[65536];
    if (fstat(log, &info) == 0 && S_ISREG(info.st_mode) && info.st_size > 1048576) {
        const ssize_t length = pread(log, tail, sizeof(tail), info.st_size - (off_t)sizeof(tail));
        char *newline = length > 0 ? memchr(tail, '\n', (size_t)length) : NULL;
        if (length > 0) {
            const char *start = newline ? newline + 1 : tail;
            const size_t retained = (size_t)(tail + length - start);
            if (pwrite(log, start, retained, 0) == (ssize_t)retained)
                ftruncate(log, (off_t)retained);
        }
    }
    close(log);
}

static int record_stop_state(struct supervision *service, bool stopped)
{
    service->state = stopped ? "stopped\n" : "unavailable\n";
    service->failures = 0;
    service->job_check_at = 0;
    return stopped ? stopped_probe_interval : retry_probe_interval;
}

static int check_server(struct supervision *service, int64_t now)
{
    if (access(INSTALL_MARKER, F_OK) == 0) {
        service->state = "starting\n";
        service->failures = 0;
        service->job_check_at = 0;
        return retry_probe_interval;
    }

    struct stat marker;
    if (lstat(DISABLED_FILE, &marker) == 0)
        return record_stop_state(service, stop_server());
    if (errno != ENOENT) {
        service->state = "unavailable\n";
        service->failures = 0;
        service->job_check_at = 0;
        return retry_probe_interval;
    }

    enum job_state job = job_unknown;
    bool job_checked = false;
    if (health_ok(NULL)) {
        if (now >= service->job_check_at) {
            job = service_job();
            job_checked = true;
            if (job == job_loaded)
                service->job_check_at = monotonic_milliseconds() + job_check_interval;
        }
        if (now < service->job_check_at) {
            service->state = "running\n";
            service->failures = 0;
            return healthy_probe_interval;
        }
    }
    service->job_check_at = 0;

    if (service->failures < failed_probes_before_recovery)
        ++service->failures;
    if (strcmp(service->state, "running\n") == 0)
        service->state = "unavailable\n";

    if (now >= service->recovery_at &&
        (service->failures == 1 || service->failures == failed_probes_before_recovery)) {
        if (!job_checked)
            job = service_job();
        if (job == job_absent || service->failures == failed_probes_before_recovery) {
            if (access(INSTALL_MARKER, F_OK) == 0) {
                service->state = "starting\n";
                service->failures = 0;
                return retry_probe_interval;
            }
            service->state = start_server(job) ? "starting\n" : "unavailable\n";
            service->recovery_at = monotonic_milliseconds() + recovery_interval;
            service->failures = 0;
            fprintf(stderr, "Castar health recovery attempted: %s", service->state);
        }
    }
    return retry_probe_interval;
}

static void handle_client(int client, uid_t allowed_user, struct supervision *service)
{
    uid_t uid;
    gid_t gid;
    if (getpeereid(client, &uid, &gid) != 0 || (uid != allowed_user && uid != 0))
        return;

    set_socket_timeout(client);
    char request[32];
    size_t length = 0;
    const int64_t deadline = monotonic_milliseconds() + 1000;
    while (length < sizeof(request) - 1 && wait_socket(client, POLLIN, deadline)) {
        const ssize_t received = read(client, request + length, sizeof(request) - 1 - length);
        if (received <= 0)
            return;
        length += (size_t)received;
        if (memchr(request, '\n', length))
            break;
    }
    request[length] = '\0';
    if (strcmp(request, "status\n") == 0) {
        send_text(client, service->state);
    } else if (strcmp(request, "logs\n") == 0) {
        send_log(client);
    } else if (access(INSTALL_MARKER, F_OK) == 0) {
        send_text(client, "error\n");
    } else if (strcmp(request, "stop\n") == 0) {
        const bool stopped = save_stopped(true) && stop_server();
        const int interval = record_stop_state(service, stopped);
        service->next_probe = monotonic_milliseconds() + interval;
        send_text(client, stopped ? "ok\n" : "error\n");
    } else if (strcmp(request, "restart\n") == 0) {
        const bool started = save_stopped(false) && start_server(service_job());
        const int64_t now = monotonic_milliseconds();
        service->state = started ? "starting\n" : "unavailable\n";
        service->next_probe = now + retry_probe_interval;
        service->recovery_at = now + recovery_interval;
        service->failures = 0;
        service->job_check_at = 0;
        send_text(client, started ? "ok\n" : "error\n");
    } else {
        send_text(client, "error\n");
    }
}

int main(int argc, char **argv)
{
    if (argc == 3 && strcmp(argv[1], "--check-version") == 0)
        return health_ok(argv[2]) ? 0 : 1;
    if (argc == 2 && strcmp(argv[1], "--check-health") == 0)
        return health_ok(NULL) ? 0 : 1;
    if (argc != 1)
        return 1;

    const struct passwd *mobile = getpwnam("mobile");
    const struct group *mobile_group = getgrnam("mobile");
    if (geteuid() != 0 || !mobile || !mobile_group)
        return 1;
    const uid_t allowed_user = mobile->pw_uid;
    const gid_t allowed_group = mobile_group->gr_gid;

    const int listener = socket(AF_UNIX, SOCK_STREAM, 0);
    if (listener < 0)
        return 1;
    const struct sockaddr_un address = {
        .sun_len = sizeof(address), .sun_family = AF_UNIX, .sun_path = CONTROL_SOCKET
    };
    umask(0077);
    if ((unlink(CONTROL_SOCKET) != 0 && errno != ENOENT) ||
        bind(listener, (const struct sockaddr *)&address, sizeof(address)) != 0 ||
        chown(CONTROL_SOCKET, 0, allowed_group) != 0 || chmod(CONTROL_SOCKET, 0660) != 0 ||
        listen(listener, 8) != 0) {
        perror("Castar control socket");
        return 1;
    }
    fcntl(listener, F_SETFD, FD_CLOEXEC);

    struct supervision service = {.state = "starting\n"};
    int64_t next_log_check = 0;
    for (;;) {
        if (monotonic_milliseconds() >= next_log_check) {
            limit_log(SERVER_LOG);
            limit_log(WATCHDOG_LOG);
            next_log_check = monotonic_milliseconds() + log_interval;
        }
        const int64_t now = monotonic_milliseconds();
        if (now >= service.next_probe) {
            const int interval = check_server(&service, now);
            service.next_probe = monotonic_milliseconds() + interval;
        }

        const int64_t next = service.next_probe < next_log_check ? service.next_probe : next_log_check;
        const int64_t remaining = next - monotonic_milliseconds();
        struct pollfd event = {.fd = listener, .events = POLLIN};
        const int ready = poll(&event, 1, remaining > 0 ? (int)remaining : 0);
        if (ready > 0 && (event.revents & POLLIN)) {
            const int client = accept(listener, NULL, NULL);
            if (client >= 0) {
                fcntl(client, F_SETFD, FD_CLOEXEC);
                handle_client(client, allowed_user, &service);
                close(client);
            }
        } else if (ready < 0 && errno != EINTR) {
            perror("Castar control poll");
            return 1;
        } else if (ready > 0) {
            fprintf(stderr, "Castar control socket unavailable\n");
            return 1;
        }
    }
}
