/*
 * ir-client: the `ir` a warm-start broker serves (Util/warm/README.md).
 *
 *   ir-client IR_BINARY ARGS...
 *   ir-client --auto SERVER_SCRIPT N IR_BINARY ARGS...
 *
 * Connects to the broker's socket and hands an already initialized IronRuby process what a
 * freshly exec'ed IR_BINARY ARGS... would have found in its own process - argv, environment,
 * working directory, umask, rlimits, CPU affinity, nice value - plus descriptors 0, 1 and 2
 * (SCM_RIGHTS). It forwards the signals it gets to that process, waits for its wait status and
 * exits the same way. Whenever the warm path is not possible - no broker, no idle worker, a
 * request the pool was not started for - it execs IR_BINARY ARGS... instead, which is exactly
 * what ir.sh would have done without it.
 *
 * --auto (IR_SERVER=auto in ir.sh): when no broker is listening, start one in the background -
 * `sh SERVER_SCRIPT start IR_BINARY` and the first N of ARGS, the pool's own command line - and run
 * this invocation cold; the ones after it find the pool.
 *
 * Environment: IR_SERVER_SOCKET (default: $XDG_RUNTIME_DIR or /tmp, then
 * ironruby-warm-<uid>-<hash of IR_BINARY>.sock), IR_SERVER_VERBOSE=1 says on stderr why a
 * request went cold, IR_SERVER_EXIT=early exits as soon as the program is done instead of once
 * its process is gone.
 *
 * Build: cc -O2 -static -o ir-client ir-client.c
 */
#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <sched.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <sys/types.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;

static char **cold_argv;
static int verbose;
static int early_exit;
static sigset_t original_mask;
static volatile pid_t worker_pid;
static uint64_t ignored_signals;

static const int forwarded[] = {
    SIGINT, SIGTERM, SIGHUP, SIGQUIT, SIGUSR1, SIGUSR2, SIGWINCH, SIGALRM, SIGCONT,
    SIGTSTP, SIGTTIN, SIGTTOU, SIGPWR, SIGVTALRM, SIGPROF, SIGURG, SIGXCPU, SIGXFSZ,
};
#define NFORWARDED (sizeof forwarded / sizeof forwarded[0])

static void cold(const char *why) {
    if (verbose && why) {
        fprintf(stderr, "ir-client: cold start: %s\n", why);
    }
    sigprocmask(SIG_SETMASK, &original_mask, NULL);
    execv(cold_argv[0], cold_argv);
    fprintf(stderr, "ir-client: cannot run %s: %s\n", cold_argv[0], strerror(errno));
    _exit(127);
}

/* ---- request buffer ---- */

static char *buf;
static size_t len, cap;

static void put(const void *data, size_t n) {
    if (len + n > cap) {
        cap = (len + n) * 2 + 4096;
        buf = realloc(buf, cap);
        if (!buf) cold("out of memory");
    }
    memcpy(buf + len, data, n);
    len += n;
}

static void put32(int32_t v) { put(&v, 4); }
static void put64(int64_t v) { put(&v, 8); }
static void putstr(const char *s) {
    size_t n = strlen(s);
    put32((int32_t)n);
    put(s, n);
}

static uint32_t fnv1a(const char *s) {
    uint32_t h = 2166136261u;
    for (; *s; s++) { h ^= (unsigned char)*s; h *= 16777619u; }
    return h;
}

static void default_socket(char *out, size_t size, const char *binary) {
    char resolved[4096];
    const char *dir = getenv("XDG_RUNTIME_DIR");
    if (!dir || !*dir) dir = "/tmp";
    if (!realpath(binary, resolved)) snprintf(resolved, sizeof resolved, "%s", binary);
    snprintf(out, size, "%s/ironruby-warm-%u-%08x.sock", dir, (unsigned)getuid(), fnv1a(resolved));
}

/* The path of our controlling terminal (/dev/pts/3), or "". ttyname() of /dev/tty only says
   "/dev/tty", so the device comes from /proc/self/stat and the name from whichever standard
   descriptor is that terminal, or from the pseudo-terminal numbering. */
static void controlling_tty(char *out, size_t size) {
    out[0] = 0;
    char stat[1024];
    int fd = open("/proc/self/stat", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return;
    ssize_t n = read(fd, stat, sizeof stat - 1);
    close(fd);
    if (n <= 0) return;
    stat[n] = 0;
    char *p = strrchr(stat, ')');           /* after "pid (comm)": state ppid pgrp session tty_nr */
    if (!p) return;
    char state;
    int ppid, pgrp, session;
    unsigned tty_nr;
    if (sscanf(p + 1, " %c %d %d %d %u", &state, &ppid, &pgrp, &session, &tty_nr) != 5 || tty_nr == 0) return;
    unsigned major = (tty_nr >> 8) & 0xfff, minor = (tty_nr & 0xff) | ((tty_nr >> 12) & 0xfff00);
    for (int i = 0; i < 3; i++) {
        struct stat st;
        if (isatty(i) && fstat(i, &st) == 0 && st.st_rdev == makedev(major, minor) && ttyname_r(i, out, size) == 0) return;
    }
    if (major >= 136 && major <= 143) snprintf(out, size, "/dev/pts/%u", (major - 136) * 256 + minor);
    else out[0] = 0;
}

/* The first descriptor above 2 that an exec would pass on, or -1. */
static int inherited_descriptor(void) {
    DIR *d = opendir("/proc/self/fd");
    if (!d) return -1;
    int self = dirfd(d), found = -1;
    struct dirent *e;
    while ((e = readdir(d)) != NULL) {
        if (e->d_name[0] < '0' || e->d_name[0] > '9') continue;
        int fd = atoi(e->d_name);
        if (fd <= 2 || fd == self) continue;
        int flags = fcntl(fd, F_GETFD);
        if (flags >= 0 && !(flags & FD_CLOEXEC)) { found = fd; break; }
    }
    closedir(d);
    return found;
}

/* Starts `sh SCRIPT start BINARY POOL_ARGS...` detached (its own session, stdio on /dev/null)
   and returns at once; the broker it starts binds the socket, and a second one that loses the
   race exits. */
static void start_server(const char *script, char **pool_argv, int pool_args) {
    pid_t pid = fork();
    if (pid < 0) return;
    if (pid == 0) {
        if (fork() != 0) _exit(0);              /* the grandchild is nobody's to wait for */
        setsid();
        int null = open("/dev/null", O_RDWR);
        if (null >= 0) { dup2(null, 0); dup2(null, 1); dup2(null, 2); if (null > 2) close(null); }
        sigprocmask(SIG_SETMASK, &original_mask, NULL);
        char *args[64];
        int n = 0;
        args[n++] = "sh";
        args[n++] = (char *)script;
        args[n++] = "start";
        for (int i = 0; i <= pool_args && n < 63; i++) args[n++] = pool_argv[i];
        args[n] = NULL;
        execv("/bin/sh", args);
        _exit(127);
    }
    waitpid(pid, NULL, 0);
    if (verbose) fprintf(stderr, "ir-client: starting a pool in the background\n");
}

/* ---- signals ---- */

static void on_signal(int sig) {
    pid_t pid = worker_pid;
    if (pid <= 0) return;
    switch (sig) {
    case SIGTSTP: case SIGTTIN: case SIGTTOU:
        /* The terminal stopped our process group; the worker is not in it. Stop both. */
        kill(pid, SIGSTOP);
        kill(getpid(), SIGSTOP);
        break;
    case SIGCONT:
        kill(pid, SIGCONT);
        break;
    default:
        kill(pid, sig);
    }
}

static void install_forwarding(void) {
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_signal;
    sa.sa_flags = SA_RESTART;
    sigemptyset(&sa.sa_mask);
    for (size_t i = 0; i < NFORWARDED; i++) {
        if (!(ignored_signals & (1ull << forwarded[i]))) sigaction(forwarded[i], &sa, NULL);
    }
    sigprocmask(SIG_SETMASK, &original_mask, NULL);
}

static void exit_like(int status) {
    if (WIFEXITED(status)) _exit(WEXITSTATUS(status));
    if (WIFSIGNALED(status)) {
        int sig = WTERMSIG(status);
        struct rlimit none = {0, 0};
        setrlimit(RLIMIT_CORE, &none);     /* the worker dumped (or not); this process should not */
        signal(sig, SIG_DFL);
        sigset_t set;
        sigemptyset(&set);
        sigaddset(&set, sig);
        sigprocmask(SIG_UNBLOCK, &set, NULL);
        raise(sig);
        _exit(128 + sig);
    }
    _exit(1);
}

/* ---- reading the reply stream ---- */

static int sock = -1;
static char in[4096];
static size_t in_len, in_pos;

/* Returns 0 at EOF. */
static int read_exact(void *out, size_t n) {
    char *p = out;
    while (n > 0) {
        if (in_pos == in_len) {
            ssize_t r = read(sock, in, sizeof in);
            if (r < 0 && errno == EINTR) continue;
            if (r <= 0) return 0;
            in_len = (size_t)r;
            in_pos = 0;
        }
        size_t k = in_len - in_pos < n ? in_len - in_pos : n;
        memcpy(p, in + in_pos, k);
        in_pos += k;
        p += k;
        n -= k;
    }
    return 1;
}

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: ir-client IR_BINARY [ARGS...]\n       ir-client --socket-path IR_BINARY\n");
        return 2;
    }
    char path[4096];
    if (strcmp(argv[1], "--socket-path") == 0 && argc == 3) {
        default_socket(path, sizeof path, argv[2]);
        puts(path);
        return 0;
    }
    const char *auto_script = NULL;
    int auto_pool_args = 0;
    if (strcmp(argv[1], "--auto") == 0 && argc >= 5) {
        auto_script = argv[2];
        auto_pool_args = atoi(argv[3]);
        argv += 3;
        argc -= 3;
        if (auto_pool_args < 0 || auto_pool_args > argc - 2) auto_pool_args = 0;
    }
    cold_argv = argv + 1;
    const char *v = getenv("IR_SERVER_VERBOSE");
    verbose = v && *v && strcmp(v, "0") != 0;
    /* IR_SERVER_EXIT=early: exit on the worker's own word ('E', as its Main returns) rather than
       on the broker's wait status ('X', once the process is gone): saves the CLR's teardown. */
    const char *x = getenv("IR_SERVER_EXIT");
    early_exit = x && strcmp(x, "early") == 0;

    /* Hold the signals we forward until there is somewhere to forward them to. */
    sigset_t block;
    sigemptyset(&block);
    for (size_t i = 0; i < NFORWARDED; i++) sigaddset(&block, forwarded[i]);
    sigprocmask(SIG_BLOCK, &block, &original_mask);

    const char *socket_path = getenv("IR_SERVER_SOCKET");
    if (!socket_path || !*socket_path) {
        default_socket(path, sizeof path, argv[1]);
        socket_path = path;
    }

    for (int fd = 0; fd < 3; fd++) {
        if (fcntl(fd, F_GETFD) < 0) cold("a standard descriptor is closed");
    }
    /* A cold `ir` inherits every descriptor not marked close-on-exec (`3<file`, a make jobserver,
       systemd's LISTEN_FDS); the worker gets 0, 1 and 2, and its own runtime holds the low numbers. */
    int extra = inherited_descriptor();
    if (extra >= 0) {
        char why[64];
        snprintf(why, sizeof why, "descriptor %d is inherited", extra);
        cold(why);
    }

    sock = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (sock < 0) cold("socket");
    struct sockaddr_un addr;
    memset(&addr, 0, sizeof addr);
    addr.sun_family = AF_UNIX;
    if (strlen(socket_path) >= sizeof addr.sun_path) cold("socket path too long");
    strcpy(addr.sun_path, socket_path);
    if (connect(sock, (struct sockaddr *)&addr, sizeof addr) < 0) {
        if (auto_script) start_server(auto_script, cold_argv, auto_pool_args);
        cold("no broker");
    }

    /* Only a broker of our own user gets our environment and descriptors. */
    struct ucred cred;
    socklen_t cred_len = sizeof cred;
    if (getsockopt(sock, SOL_SOCKET, SO_PEERCRED, &cred, &cred_len) < 0 || cred.uid != getuid()) {
        cold("broker belongs to another user");
    }

    /* ---- the request ---- */
    put("IRW1", 4);
    put32(0);                                   /* length, patched below */
    put32((int32_t)getpid());
    put32((int32_t)getppid());
    mode_t mask = umask(0);
    umask(mask);
    put32((int32_t)mask);
    errno = 0;
    int nice_value = getpriority(PRIO_PROCESS, 0);
    put32(errno ? 0 : nice_value);

    /* Signals ignored at exec stay ignored in a cold `ir` (nohup, `cmd &` in a script): the worker
       ignores them too, and they are not forwarded. */
    uint64_t ignored = 0;
    for (int sig = 1; sig < 64; sig++) {
        struct sigaction old;
        if (sigaction(sig, NULL, &old) == 0 && old.sa_handler == SIG_IGN) ignored |= 1ull << sig;
    }
    ignored_signals = ignored;
    put64((int64_t)ignored);

    static const int resources[] = {
        RLIMIT_CPU, RLIMIT_FSIZE, RLIMIT_DATA, RLIMIT_STACK, RLIMIT_CORE, RLIMIT_NOFILE,
        RLIMIT_AS, RLIMIT_NPROC, RLIMIT_MEMLOCK, RLIMIT_LOCKS, RLIMIT_SIGPENDING,
        RLIMIT_MSGQUEUE, RLIMIT_NICE, RLIMIT_RTPRIO,
    };
    int nres = sizeof resources / sizeof resources[0];
    put32(nres);
    for (int i = 0; i < nres; i++) {
        struct rlimit rl;
        if (getrlimit(resources[i], &rl) < 0) { rl.rlim_cur = rl.rlim_max = RLIM_INFINITY; }
        put32(resources[i]);
        put64((int64_t)rl.rlim_cur);
        put64((int64_t)rl.rlim_max);
    }

    cpu_set_t cpus;
    if (sched_getaffinity(0, sizeof cpus, &cpus) == 0) {
        put32((int32_t)sizeof cpus);
        put(&cpus, sizeof cpus);
    } else {
        put32(0);
    }

    char cwd[4096];
    if (!getcwd(cwd, sizeof cwd)) cold("no working directory");
    putstr(cwd);

    put32(argc - 1);
    for (int i = 1; i < argc; i++) putstr(argv[i]);

    int envc = 0;
    for (char **e = environ; *e; e++) envc++;
    put32(envc);
    for (char **e = environ; *e; e++) putstr(*e);

    /* The worker has no controlling terminal; IO.console opens ours by name. */
    char tty_path[256];
    controlling_tty(tty_path, sizeof tty_path);
    putstr(tty_path);

    int32_t body = (int32_t)(len - 8);
    memcpy(buf + 4, &body, 4);

    struct iovec iov = { buf, len };
    union { char space[CMSG_SPACE(3 * sizeof(int))]; struct cmsghdr align; } control;
    memset(&control, 0, sizeof control);
    struct msghdr msg;
    memset(&msg, 0, sizeof msg);
    msg.msg_iov = &iov;
    msg.msg_iovlen = 1;
    msg.msg_control = control.space;
    msg.msg_controllen = sizeof control.space;
    struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
    cmsg->cmsg_level = SOL_SOCKET;
    cmsg->cmsg_type = SCM_RIGHTS;
    cmsg->cmsg_len = CMSG_LEN(3 * sizeof(int));
    int fds[3] = { 0, 1, 2 };
    memcpy(CMSG_DATA(cmsg), fds, sizeof fds);

    /* The descriptors go with the first part of the message; the rest follows on the stream. */
    size_t sent = 0;
    while (sent < len) {
        ssize_t n;
        if (sent == 0) {
            n = sendmsg(sock, &msg, MSG_NOSIGNAL);
        } else {
            n = send(sock, buf + sent, len - sent, MSG_NOSIGNAL);
        }
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) cold("broker went away");
        sent += (size_t)n;
    }

    /* ---- replies ---- */
    int accepted = 0, have_exit = 0;
    int32_t exit_code = 0;
    for (;;) {
        unsigned char type;
        if (!read_exact(&type, 1)) {
            if (!accepted) cold("worker went away before starting");
            if (have_exit) _exit(exit_code & 0xff);
            fprintf(stderr, "ir-client: the warm IronRuby process went away without an exit status\n");
            _exit(1);
        }
        switch (type) {
        case 'W': {
            int32_t pid;
            if (!read_exact(&pid, 4)) cold("broker went away");
            worker_pid = pid;
            break;
        }
        case 'B':
            cold("no idle worker");
            break;
        case 'F': {
            int32_t n;
            char reason[1024];
            if (!read_exact(&n, 4)) cold("worker went away");
            size_t k = n < (int32_t)sizeof reason - 1 ? (size_t)n : sizeof reason - 1;
            if (!read_exact(reason, k)) cold("worker went away");
            for (int32_t rest = n - (int32_t)k; rest > 0; rest--) { char c; read_exact(&c, 1); }
            reason[k] = 0;
            cold(reason);
            break;
        }
        case 'R':
            accepted = 1;
            install_forwarding();
            /* The worker holds them now; a reader waiting for EOF on our stdout should not have to
               wait for us. Standard error stays for our own diagnostics. */
            close(0);
            close(1);
            break;
        case 'E':
            if (!read_exact(&exit_code, 4)) break;
            have_exit = 1;
            if (early_exit) _exit(exit_code & 0xff);
            break;
        case 'X': {
            int32_t status;
            int64_t maxrss;
            if (!read_exact(&status, 4) || !read_exact(&maxrss, 8)) {
                if (have_exit) _exit(exit_code & 0xff);
                _exit(1);
            }
            if (verbose) fprintf(stderr, "ir-client: warm worker %d, peak RSS %lld MB\n", (int)worker_pid, (long long)(maxrss / 1024));
            exit_like(status);
            break;
        }
        default:
            fprintf(stderr, "ir-client: protocol error (%d)\n", type);
            if (!accepted) cold("protocol error");
            _exit(1);
        }
    }
}
