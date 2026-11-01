/*
 * ir-broker: keeps a pool of pre-initialized IronRuby processes for ir-client (Util/warm/README.md).
 *
 *   ir-broker [-d] [-k K] [-l LOG] [-p PIDFILE] -s SOCKET -- IR_BINARY ARGS...
 *
 * Starts K workers, each `IR_BINARY ARGS...` with IR_WARM_WORKER_FD naming its end of a socketpair:
 * it initializes the runtime, loads the prelude and RubyGems, says 'Y' and waits. A client connecting
 * to SOCKET gets the next idle worker: the broker tells the client the worker's pid ('W') and passes
 * the connection to the worker (SCM_RIGHTS), which reads the client's request itself. The broker keeps
 * its own end of the connection, and when the worker is reaped it sends the client the wait status
 * ('X'), so that a worker killed by a signal - or replaced by exec - still reports how it ended.
 * Every assignment starts a replacement, so that K workers are idle or warming at any time.
 *
 * A client finds no idle worker: it waits if a warming worker is free to take it, since that worker
 * will be ready before a cold start would be; otherwise it is told 'B' and starts cold.
 *
 * -d detaches (fork, setsid: the workers must not have a controlling terminal, or reading a client's
 * terminal would stop them with SIGTTIN). SIGTERM stops the broker and its idle workers; running
 * programs finish.
 *
 * Build: cc -O2 -o ir-broker ir-broker.c
 */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/signalfd.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/types.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

enum { WARMING, IDLE, BUSY };

struct worker {
    pid_t pid;
    int fd;            /* broker end of the socketpair, -1 once the worker said it took a client */
    int state;
    int conn;          /* the client connection, while BUSY */
    double spawned, ready, assigned;
};

#define MAXW 256
static struct worker workers[MAXW];
static int nworkers;
static int pool = 2;
static char **worker_argv;
static FILE *logf;
static sigset_t original_mask;
static int failures;            /* consecutive workers that died before being ready */
static long served, cold_busy;

#define MAXQ 64
static int queue[MAXQ];
static double queued_at[MAXQ];
static int nqueue;

static double now(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}

static void logmsg(const char *fmt, ...) {
    if (!logf) return;
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    struct tm tm;
    localtime_r(&ts.tv_sec, &tm);
    char stamp[32];
    strftime(stamp, sizeof stamp, "%H:%M:%S", &tm);
    fprintf(logf, "%s.%03ld ir-broker: ", stamp, ts.tv_nsec / 1000000);
    va_list ap;
    va_start(ap, fmt);
    vfprintf(logf, fmt, ap);
    va_end(ap);
    fputc('\n', logf);
    fflush(logf);
}

static int count(int state) {
    int n = 0;
    for (int i = 0; i < nworkers; i++) if (workers[i].state == state) n++;
    return n;
}

static void spawn_worker(void) {
    if (nworkers >= MAXW) return;
    int sv[2];
    if (socketpair(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0, sv) < 0) {
        logmsg("socketpair: %s", strerror(errno));
        return;
    }
    pid_t pid = fork();
    if (pid < 0) {
        logmsg("fork: %s", strerror(errno));
        close(sv[0]);
        close(sv[1]);
        return;
    }
    if (pid == 0) {
        sigprocmask(SIG_SETMASK, &original_mask, NULL);
        signal(SIGPIPE, SIG_DFL);
        /* Its own process group: a program that signals its group (kill 0) must not hit the pool. */
        setpgid(0, 0);
        int null = open("/dev/null", O_RDWR);
        int target = 3;
        if (sv[1] == target) {
            fcntl(target, F_SETFD, 0);
        } else {
            dup2(sv[1], target);
        }
        if (null >= 0) {
            dup2(null, 0);
            if (!getenv("IR_WARM_DEBUG")) {
                dup2(null, 1);
            }
        }
        /* stderr stays the broker's log: whatever the warm-up says ends up there */
        setenv("IR_WARM_WORKER_FD", "3", 1);
        execv(worker_argv[0], worker_argv);
        _exit(127);
    }
    close(sv[1]);
    struct worker *w = &workers[nworkers++];
    memset(w, 0, sizeof *w);
    w->pid = pid;
    w->fd = sv[0];
    w->state = WARMING;
    w->conn = -1;
    w->spawned = now();
    logmsg("worker %d starting", pid);
}

static void refill(void) {
    if (failures >= 3) return;          /* a broken build: stop trying, clients go cold */
    while (count(IDLE) + count(WARMING) < pool) spawn_worker();
}

static void send_all(int fd, const void *data, size_t n) {
    const char *p = data;
    while (n > 0) {
        ssize_t r = send(fd, p, n, MSG_NOSIGNAL);
        if (r < 0 && errno == EINTR) continue;
        if (r <= 0) return;
        p += r;
        n -= (size_t)r;
    }
}

static void assign(struct worker *w, int conn) {
    char msg[5];
    msg[0] = 'W';
    int32_t pid = w->pid;
    memcpy(msg + 1, &pid, 4);
    send_all(conn, msg, 5);

    struct iovec iov = { "C", 1 };
    union { char space[CMSG_SPACE(sizeof(int))]; struct cmsghdr align; } control;
    memset(&control, 0, sizeof control);
    struct msghdr m;
    memset(&m, 0, sizeof m);
    m.msg_iov = &iov;
    m.msg_iovlen = 1;
    m.msg_control = control.space;
    m.msg_controllen = sizeof control.space;
    struct cmsghdr *c = CMSG_FIRSTHDR(&m);
    c->cmsg_level = SOL_SOCKET;
    c->cmsg_type = SCM_RIGHTS;
    c->cmsg_len = CMSG_LEN(sizeof(int));
    memcpy(CMSG_DATA(c), &conn, sizeof(int));
    if (sendmsg(w->fd, &m, MSG_NOSIGNAL) < 0) {
        logmsg("worker %d: cannot pass the connection: %s", w->pid, strerror(errno));
    }
    w->state = BUSY;
    w->conn = conn;
    w->assigned = now();
    served++;
    refill();
}

static void close_conn(int conn) {
    if (conn >= 0) close(conn);
}

static void dispatch_queue(void) {
    while (nqueue > 0) {
        struct worker *idle = NULL;
        for (int i = 0; i < nworkers; i++) if (workers[i].state == IDLE) { idle = &workers[i]; break; }
        if (!idle) return;
        int conn = queue[0];
        logmsg("client waited %.0f ms for worker %d", (now() - queued_at[0]) * 1000, idle->pid);
        memmove(queue, queue + 1, (size_t)(nqueue - 1) * sizeof queue[0]);
        memmove(queued_at, queued_at + 1, (size_t)(nqueue - 1) * sizeof queued_at[0]);
        nqueue--;
        assign(idle, conn);
    }
}

static void on_accept(int lfd) {
    int conn = accept4(lfd, NULL, NULL, SOCK_CLOEXEC);
    if (conn < 0) return;
    struct ucred cred;
    socklen_t cl = sizeof cred;
    if (getsockopt(conn, SOL_SOCKET, SO_PEERCRED, &cred, &cl) < 0 || cred.uid != getuid()) {
        logmsg("refused a client of uid %d", (int)cred.uid);
        close(conn);
        return;
    }
    for (int i = 0; i < nworkers; i++) {
        if (workers[i].state == IDLE && nqueue == 0) {
            assign(&workers[i], conn);
            return;
        }
    }
    if (nqueue < count(WARMING) && nqueue < MAXQ) {
        queue[nqueue] = conn;
        queued_at[nqueue] = now();
        nqueue++;
        return;
    }
    cold_busy++;
    logmsg("no worker for a client: it starts cold");
    send_all(conn, "B", 1);
    close(conn);
}

static void on_worker_message(struct worker *w) {
    char c;
    ssize_t r = read(w->fd, &c, 1);
    if (r <= 0) {
        /* it exited or exec'ed; SIGCHLD tells which */
        close(w->fd);
        w->fd = -1;
        return;
    }
    switch (c) {
    case 'Y':
        w->state = IDLE;
        w->ready = now();
        failures = 0;
        logmsg("worker %d ready after %.0f ms", w->pid, (w->ready - w->spawned) * 1000);
        dispatch_queue();
        break;
    case 'I':
        /* It declined the request (the client starts cold) and can take another. */
        close_conn(w->conn);
        w->conn = -1;
        w->state = IDLE;
        dispatch_queue();
        break;
    case 'A':
        logmsg("worker %d running a program (%.1f ms after assignment)", w->pid, (now() - w->assigned) * 1000);
        break;
    }
}

static void reap(void) {
    for (;;) {
        int status;
        struct rusage ru;
        pid_t pid = wait4(-1, &status, WNOHANG, &ru);
        if (pid <= 0) return;
        for (int i = 0; i < nworkers; i++) {
            struct worker *w = &workers[i];
            if (w->pid != pid) continue;
            if (w->conn >= 0) {
                char msg[13];
                msg[0] = 'X';
                int32_t st = status;
                int64_t rss = ru.ru_maxrss;
                memcpy(msg + 1, &st, 4);
                memcpy(msg + 5, &rss, 8);
                send_all(w->conn, msg, sizeof msg);
                close(w->conn);
                logmsg("worker %d done: status 0x%x, %.0f ms after assignment, peak RSS %ld MB",
                       pid, status, (now() - w->assigned) * 1000, (long)(ru.ru_maxrss / 1024));
            } else if (w->state == WARMING || w->state == IDLE) {
                logmsg("worker %d died while %s: status 0x%x", pid, w->state == WARMING ? "warming up" : "idle", status);
                if (w->state == WARMING) failures++;
            }
            if (w->fd >= 0) close(w->fd);
            *w = workers[--nworkers];
            break;
        }
    }
}

static int bind_socket(const char *path) {
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    struct sockaddr_un addr;
    memset(&addr, 0, sizeof addr);
    addr.sun_family = AF_UNIX;
    if (strlen(path) >= sizeof addr.sun_path) {
        fprintf(stderr, "ir-broker: socket path too long\n");
        exit(1);
    }
    strcpy(addr.sun_path, path);
    if (connect(fd, (struct sockaddr *)&addr, sizeof addr) == 0) {
        fprintf(stderr, "ir-broker: a broker is already listening on %s\n", path);
        exit(1);
    }
    close(fd);
    unlink(path);
    fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    mode_t old = umask(0077);
    if (bind(fd, (struct sockaddr *)&addr, sizeof addr) < 0 || listen(fd, 64) < 0) {
        fprintf(stderr, "ir-broker: %s: %s\n", path, strerror(errno));
        exit(1);
    }
    umask(old);
    chmod(path, 0600);
    return fd;
}

int main(int argc, char **argv) {
    const char *sock = NULL, *log = NULL, *pidfile = NULL;
    int detach = 0, i;
    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--") == 0) { i++; break; }
        if (strcmp(argv[i], "-d") == 0) detach = 1;
        else if (strcmp(argv[i], "-k") == 0 && i + 1 < argc) pool = atoi(argv[++i]);
        else if (strcmp(argv[i], "-s") == 0 && i + 1 < argc) sock = argv[++i];
        else if (strcmp(argv[i], "-l") == 0 && i + 1 < argc) log = argv[++i];
        else if (strcmp(argv[i], "-p") == 0 && i + 1 < argc) pidfile = argv[++i];
        else break;
    }
    if (!sock || i >= argc || pool < 1 || pool > 64) {
        fprintf(stderr, "usage: ir-broker [-d] [-k K] [-l LOG] [-p PIDFILE] -s SOCKET -- IR_BINARY ARGS...\n");
        return 2;
    }
    worker_argv = argv + i;

    int lfd = bind_socket(sock);
    if (log) {
        logf = fopen(log, "a");
    } else if (!detach) {
        logf = stderr;
    }

    if (detach) {
        pid_t pid = fork();
        if (pid < 0) { perror("fork"); return 1; }
        if (pid > 0) _exit(0);
        setsid();
        int null = open("/dev/null", O_RDWR);
        dup2(null, 0);
        dup2(null, 1);
        if (logf) dup2(fileno(logf), 2); else dup2(null, 2);
        if (null > 2) close(null);
    }
    if (pidfile) {
        FILE *f = fopen(pidfile, "w");
        if (f) { fprintf(f, "%d\n", (int)getpid()); fclose(f); }
    }

    signal(SIGPIPE, SIG_IGN);
    sigset_t mask;
    sigemptyset(&mask);
    sigaddset(&mask, SIGCHLD);
    sigaddset(&mask, SIGTERM);
    sigaddset(&mask, SIGINT);
    sigaddset(&mask, SIGHUP);
    sigprocmask(SIG_BLOCK, &mask, &original_mask);
    int sfd = signalfd(-1, &mask, SFD_CLOEXEC);

    logmsg("listening on %s, pool of %d: %s", sock, pool, worker_argv[0]);
    refill();

    for (;;) {
        struct pollfd pfd[2 + MAXW];
        struct worker *who[2 + MAXW];
        int n = 0;
        pfd[n].fd = sfd; pfd[n].events = POLLIN; who[n++] = NULL;
        pfd[n].fd = lfd; pfd[n].events = POLLIN; who[n++] = NULL;
        for (int k = 0; k < nworkers; k++) {
            if (workers[k].fd >= 0) {
                pfd[n].fd = workers[k].fd;
                pfd[n].events = POLLIN;
                who[n++] = &workers[k];
            }
        }
        if (poll(pfd, (nfds_t)n, -1) < 0) {
            if (errno == EINTR) continue;
            logmsg("poll: %s", strerror(errno));
            return 1;
        }
        int stop = 0, children = 0;
        if (pfd[0].revents & POLLIN) {
            struct signalfd_siginfo si;
            while (read(sfd, &si, sizeof si) == sizeof si) {
                if (si.ssi_signo == SIGCHLD) children = 1;
                else stop = 1;
                break;
            }
        }
        /* worker messages before reaping: a 'Y' or 'I' may precede the exit */
        pid_t pids[2 + MAXW];
        for (int k = 2; k < n; k++) pids[k] = who[k]->pid;
        for (int k = 2; k < n; k++) {
            if (!(pfd[k].revents & (POLLIN | POLLHUP | POLLERR))) continue;
            for (int j = 0; j < nworkers; j++) {
                if (workers[j].pid == pids[k]) { on_worker_message(&workers[j]); break; }
            }
        }
        if (children) {
            reap();
            refill();
            dispatch_queue();
        }
        if (pfd[1].revents & POLLIN) on_accept(lfd);
        if (stop) {
            logmsg("stopping: served %ld, %ld clients found no worker", served, cold_busy);
            for (int k = 0; k < nworkers; k++) {
                if (workers[k].state != BUSY) kill(workers[k].pid, SIGKILL);
            }
            for (int k = 0; k < nqueue; k++) { send_all(queue[k], "B", 1); close(queue[k]); }
            unlink(sock);
            if (pidfile) unlink(pidfile);
            return 0;
        }
    }
}
