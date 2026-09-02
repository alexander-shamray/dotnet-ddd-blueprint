# Plan — hardening the sandboxed reviewer (#17), measured on Docker Desktop

**This is a plan in the house sense, and it is also the measurement record
the change was built from.** Two of #17's three items were exercised here
before a line of the harness was edited: the uid collision was reproduced and
its fix built at four id pairs, and the egress proxy was proved from a probe
container on the internal network, including that the grok CLI honours
`HTTPS_PROXY`. The third item, SELinux, is a procedure for an enforcing host
and not a result. The commands and their output are verbatim below, and the
edit blocks in sections 3 and 4 are what landed in `.claude/sandbox/` and
`.claude/scripts/grok-review.sh`. It is not edited to match what followed.

---

Issue #17 defers three items because none could be exercised on the Windows
host that built the sandbox. Two of them can, because Docker Desktop runs
Linux containers: the uid/gid collision is a property of `debian:trixie-slim`
and of `groupadd`, not of the host, and an internal network with a proxy on it
is a property of the daemon. The third — SELinux — is a property of the host
kernel and stays unexercised; what this file carries for it is the procedure a
person runs on a Fedora/RHEL host, with what to expect from each step.

Everything below was run against `docker 29.6.2 linux/amd64` (Docker Desktop,
WSL2 backend) on 2026-09-02. Nothing under `.claude/` was edited; every file
this plan names is in the scratchpad beside it, and every image, container and
network created was tagged `ashamray-probe-*` and removed at the end — the
last command in §2.9 prints the count of what remained, and it is zero.

**One thing was refused and is recorded rather than worked around.** A run
that would have copied this host's `~/.grok/auth.json`, `agent_id` and
`config.toml` into a probe container — exactly the three files `grok-review.sh`
copies for its own preflight — was blocked by the harness's classifier. So an
*authenticated* `grok -p ok` through the proxy was never run here. Every other
claim about grok's behaviour behind the proxy rests on a bogus `XAI_API_KEY`,
which reaches `api.x.ai` and is answered there; §5 says precisely what that
does and does not establish.

---

## 1. Task A — uid/gid collision

### 1.1 What the base image already holds

The ids a host is most likely to arrive with are already taken. From
`docker run --rm debian:trixie-slim sh -c 'cat /etc/passwd; cat /etc/group'`:

```
nobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin
_apt:x:42:65534::/nonexistent:/usr/sbin/nologin
sync:x:4:65534:sync:/bin:/bin/sync
dialout:x:20:
users:x:100:
nogroup:x:65534:
```

`users` (gid 100) is a common primary group on Linux hosts, `dialout` (gid 20)
is the typical macOS primary group the script's own comment names, and
`nobody`/`nogroup` (65534) is what a rootless or squashed-id host reports.
Two other base-image facts the fix relies on, from the same image:

```
$ grep -E "^(HOME_MODE)" /etc/login.defs; useradd -D | grep -E "^(SHELL|HOME)="
HOME_MODE	0700
HOME=/home
SHELL=/bin/sh
```

So a home `useradd --create-home` makes is `0700` with shell `/bin/sh`, and the
rename branch below reproduces both rather than inventing its own.

### 1.2 Reproduction against the unmodified Dockerfile

`Dockerfile.orig` is a byte-for-byte copy of `.claude/sandbox/Dockerfile`.
Three builds, each with `--no-cache --progress=plain`, each failing in the
same `RUN` and each before the 163 MB download is reached:

```
$ docker build --build-arg REVIEWER_UID=1000 --build-arg REVIEWER_GID=100 --file Dockerfile.orig .
#6 0.244 groupadd: GID '100' already exists
#6 ERROR: process "/bin/sh -c groupadd --gid \"${REVIEWER_GID}\" reviewer     && useradd --create-home --uid \"${REVIEWER_UID}\" --gid \"${REVIEWER_GID}\" reviewer" did not complete successfully: exit code: 4
build rc=1

$ docker build --build-arg REVIEWER_UID=65534 --build-arg REVIEWER_GID=65534 --file Dockerfile.orig .
#6 0.238 groupadd: GID '65534' already exists
#6 ERROR: process "/bin/sh -c groupadd --gid ... did not complete successfully: exit code: 4
build rc=1

$ docker build --build-arg REVIEWER_UID=65534 --build-arg REVIEWER_GID=1000 --file Dockerfile.orig .
#6 0.240 useradd: UID 65534 is not unique
#6 ERROR: process "/bin/sh -c groupadd --gid ... did not complete successfully: exit code: 4
build rc=1
```

**Two errors, not one, and the second is only reachable past the first.** A
colliding gid fails `groupadd` and the uid is never examined; a free gid with a
colliding uid fails `useradd`. Both exit 4, which is shadow-utils' "id already
in use", so the exit code alone does not say which half collided — the message
does.

### 1.3 The alternative that was rejected, and why it was measured

`useradd --non-unique` is the one-flag fix, and it builds. What it builds is
wrong, and the reason is visible only by asking the account database rather
than the environment. In `debian:trixie-slim`:

```
$ useradd --non-unique --create-home --uid 65534 --gid 65534 reviewer
useradd warning: reviewer's uid 65534 outside of the UID_MIN 1000 and UID_MAX 60000 range.
useradd rc=0
$ getent passwd 65534
nobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin
$ id reviewer
uid=65534(nobody) gid=65534(nogroup) groups=65534(nogroup)
$ su -s /bin/sh reviewer -c 'id; echo HOME=$HOME; whoami'
uid=65534(nobody) gid=65534(nogroup) groups=65534(nogroup)
HOME=/home/reviewer
nobody
```

**Two entries share the uid, and every lookup by uid returns the first.** `id`,
`whoami` and `getpwuid` all answer `nobody` with a home of `/nonexistent`; only
the `HOME` variable says `/home/reviewer`. Anything that resolves the home
through the account instead of the environment — and a reviewer is a process
nobody here wrote — looks in the wrong place, while `USER reviewer` in the
Dockerfile still resolves by name and happens to work. That is the shape of a
fix that passes its own build and fails somewhere else later. The rename below
leaves exactly one entry for the uid, so the two answers cannot disagree.

### 1.4 The fix, and what it was built with

`Dockerfile.fixed` replaces the two-line `RUN groupadd … && useradd …` with the
block in §3. The rule is: **a gid that exists is reused as-is; a uid that
exists has its account renamed into `reviewer`; both are refused at 0.**
Four builds of it, plus the refusal:

```
$ docker build --build-arg REVIEWER_UID=1000  --build-arg REVIEWER_GID=1000  --file Dockerfile.fixed .   → build rc=0
$ docker build --build-arg REVIEWER_UID=65534 --build-arg REVIEWER_GID=65534 --file Dockerfile.fixed .   → build rc=0
$ docker build --build-arg REVIEWER_UID=1000  --build-arg REVIEWER_GID=100   --file Dockerfile.fixed .   → build rc=0
$ docker build --build-arg REVIEWER_UID=65534 --build-arg REVIEWER_GID=1000  --file Dockerfile.fixed .   → build rc=0
$ docker build --build-arg REVIEWER_UID=0     --build-arg REVIEWER_GID=0     --file Dockerfile.fixed .
#6 0.191 refusing REVIEWER_UID=0 REVIEWER_GID=0: the reviewer is non-root by design, and reusing root's account would make it root
#6 ERROR: ... did not complete successfully: exit code: 1
build rc=1
```

Each image then ran the same check. The command, and its output for all four:

```
$ docker run --rm <image> sh -c 'id; echo "HOME=$HOME"; whoami; getent passwd reviewer;
    getent passwd nobody || echo "(no nobody entry: rc=$?)"; stat -c "%A %U:%G %n" /home/reviewer;
    ls -la /home/reviewer/.grok /home/reviewer/.grok/bin;
    touch /home/reviewer/.grok/probe && echo "home writable"; grok --version;
    git config --global --get-all safe.directory'
```

Normal ids, 1000/1000 — the path every build on this host takes:

```
uid=1000(reviewer) gid=1000(reviewer) groups=1000(reviewer)
HOME=/home/reviewer
reviewer
reviewer:x:1000:1000::/home/reviewer:/bin/sh
nobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin
drwx------ reviewer:reviewer /home/reviewer
/home/reviewer/.grok:
drwxr-xr-x 2 reviewer reviewer 4096 Sep  2 11:08 bin
-rw-r--r-- 1 reviewer reviewer   29 Sep  2 11:08 config.toml
drwxr-xr-x 2 reviewer reviewer 4096 Sep  2 11:08 downloads
/home/reviewer/.grok/bin:
lrwxrwxrwx 1 reviewer reviewer   30 Sep  2 11:08 agent -> ../downloads/grok-linux-x86_64
lrwxrwxrwx 1 reviewer reviewer   30 Sep  2 11:08 grok -> ../downloads/grok-linux-x86_64
home writable
grok 1.0.5 (5115b46bc9)
/review
```

Both collide, 65534/65534 — `nobody` renamed, `nogroup` reused:

```
uid=65534(reviewer) gid=65534(nogroup) groups=65534(nogroup)
HOME=/home/reviewer
reviewer
reviewer:x:65534:65534::/home/reviewer:/bin/sh
(no nobody entry: rc=2)
drwx------ reviewer:nogroup /home/reviewer
/home/reviewer/.grok:
drwxr-xr-x 2 reviewer nogroup 4096 Sep  2 11:15 bin
-rw-r--r-- 1 reviewer nogroup   29 Sep  2 11:15 config.toml
drwxr-xr-x 2 reviewer nogroup 4096 Sep  2 11:15 downloads
/home/reviewer/.grok/bin:
lrwxrwxrwx 1 reviewer nogroup   30 Sep  2 11:15 agent -> ../downloads/grok-linux-x86_64
lrwxrwxrwx 1 reviewer nogroup   30 Sep  2 11:15 grok -> ../downloads/grok-linux-x86_64
home writable
grok 1.0.5 (5115b46bc9)
/review
```

Gid collides only, 1000/100 — `users` reused, account created:

```
uid=1000(reviewer) gid=100(users) groups=100(users)
HOME=/home/reviewer
reviewer
reviewer:x:1000:100::/home/reviewer:/bin/sh
nobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin
drwx------ reviewer:users /home/reviewer
[.grok listing identical in shape, owned reviewer:users]
home writable
grok 1.0.5 (5115b46bc9)
/review
```

Uid collides only, 65534/1000 — `nobody` renamed, group created:

```
uid=65534(reviewer) gid=1000(reviewer) groups=1000(reviewer)
HOME=/home/reviewer
reviewer
reviewer:x:65534:1000::/home/reviewer:/bin/sh
(no nobody entry: rc=2)
drwx------ reviewer:reviewer /home/reviewer
[.grok listing identical in shape, owned reviewer:reviewer]
home writable
grok 1.0.5 (5115b46bc9)
/review
```

**What the four have in common is the whole claim.** `id -un` and `whoami`
answer `reviewer`; `HOME` and the passwd entry agree on `/home/reviewer`; the
home is `0700` and owned by the reviewer's own uid:gid; `.grok/bin/grok` is the
relative symlink the release layer makes and it resolves, so `grok --version`
is the pinned 1.0.5; and `safe.directory` is set — that last one matters
because `git config --global` writes under `HOME`, so a home in the wrong place
would have put the setting where the review's git never reads it. The group
name is whatever the gid already had, and nothing in the Dockerfile or the
script names the group: `USER reviewer` resolves the user, and the mounts are
by path.

**Root stays refused, now in two places.** `grok-review.sh` refuses to pass 0
before the build starts (exit 11) and is left alone; the Dockerfile refuses it
too, because the rename would otherwise turn `root` into `reviewer` and hand a
by-hand build a root reviewer with a clean-looking name. The old form refused
root by accident — root exists, so `groupadd` failed — and the reuse logic
would have removed that accident.

---

## 2. Task B — egress restriction

### 2.1 The design

Three pieces, and Docker supplies two of them.

- **A network created with `--internal`.** Docker gives it no gateway: a member
  has a route to the other members and to nothing else, and Docker's embedded
  resolver refuses to forward a name outside it. The reviewer container runs
  here and nowhere else.
- **A proxy container on that network AND on the default bridge.** It is the
  only member with a way out. It runs `egress-proxy.py` — stdlib Python, in the
  file beside this plan — which accepts `CONNECT host:443` for a host on its
  allow-list, tunnels the bytes without reading them, and answers everything
  else with `403` and closes. No plain-HTTP forwarding, no port but 443, no TLS
  interception, and the log carries a sanitised hostname and nothing the
  reviewer wrote.
- **`HTTPS_PROXY=http://proxy:8888` in the reviewer's environment.** `proxy` is
  a network alias on the internal network, resolved by Docker's resolver, so
  the reviewer needs no external DNS at all — the proxy resolves `api.x.ai`,
  the reviewer only ever resolves `proxy`.

The proxy runs the reviewer image itself. That image already carries `python3`
for the licence gate, so the proxy is one script, no second image, no second
pin and no second digest. The proxy container mounts no clone and no credential.

### 2.2 Bringing it up

```
$ docker network create --internal ashamray-probe-internal
2b46947652a2efcbf780195c01ff4edb862b1c1bd5da34d8f193b3ba0f873aaa
$ docker run --detach --name ashamray-probe-proxy --network ashamray-probe-internal \
    --network-alias proxy --volume <sandbox>/egress-proxy.py:/egress-proxy.py:ro \
    ashamray-probe-fixed-1000-1000 python3 /egress-proxy.py
c55d9b7e7ab514210a8ed0bd210a56004aff5a99b9beb4dd70896191f23df6fd
$ docker network connect bridge ashamray-probe-proxy
rc=0
$ docker logs ashamray-probe-proxy
listen 8888
allow-list api.x.ai auth.x.ai
$ docker inspect ashamray-probe-proxy --format '{{range $k,$v := .NetworkSettings.Networks}}{{$k}}={{$v.IPAddress}} {{end}}'
ashamray-probe-internal=172.19.0.2 bridge=172.17.0.2
$ docker network inspect ashamray-probe-internal --format 'Internal={{.Internal}} Subnet={{(index .IPAM.Config 0).Subnet}}'
Internal=true Subnet=172.19.0.0/16
```

### 2.3 The probes, from a container on the internal network

Every command below is `docker run --rm --network ashamray-probe-internal
curlimages/curl:latest <command>`; only the command is shown.

```
$ curl -sS -o /dev/null -w 'http=%{http_code}\n' -x http://proxy:8888 https://api.x.ai/
http=421
rc=0
$ curl -sS -o /dev/null -w 'http=%{http_code}\n' -x http://proxy:8888 https://auth.x.ai/
http=403
rc=0
$ curl -sS -o /dev/null -w 'http=%{http_code}\n' -x http://proxy:8888 https://example.com/
curl: (7) CONNECT tunnel failed, response 403
http=000
rc=7
$ curl -sS -o /dev/null -w 'http=%{http_code}\n' -x http://proxy:8888 http://api.x.ai/
http=403
rc=0
$ curl -sS -o /dev/null -w 'http=%{http_code}\n' -x http://proxy:8888 https://api.x.ai:8443/
curl: (7) CONNECT tunnel failed, response 403
http=000
rc=7
$ curl -sS --connect-timeout 5 https://example.com/
curl: (28) Resolving timed out after 5002 milliseconds
rc=28
$ curl -sS --connect-timeout 5 https://1.1.1.1/
curl: (7) Failed to connect to 1.1.1.1:443 after 0 ms: Could not connect to server
rc=7
```

And what the proxy logged for those seven, in order:

```
allow api.x.ai
allow auth.x.ai
deny example.com
deny GET httpapi.x.ai
deny api.x.ai
```

**Read the two 4xx codes on the allowed hosts as successes.** A `421` from
`api.x.ai` and a `403` from `auth.x.ai` are answers from the origin, which
means the tunnel opened, the TLS handshake completed end to end through it,
and the origin objected to a bare `GET /` with no credential — which is the
correct objection. A refusal by the proxy looks different: curl reports
`CONNECT tunnel failed, response 403` and never gets an HTTP status at all.
Plain `http://api.x.ai/` through the proxy is refused with the proxy's own
`403` (the log line names the method), and `api.x.ai:8443` is refused because
the port is not 443. Direct, with no proxy, a name lookup times out and an IP
has no route.

### 2.4 Why the direct probes fail the way they do

```
$ ip route
172.19.0.0/16 dev eth0 scope link  src 172.19.0.3
$ getent hosts example.com
getent rc=2
$ getent hosts proxy
172.19.0.2        proxy  proxy
$ nslookup example.com
Server:		127.0.0.11
** server can't find example.com: SERVFAIL
$ cat /etc/resolv.conf
nameserver 127.0.0.11
options ndots:0
# ExtServers: [host(192.168.65.7)]
```

**No default route, and the resolver refuses to forward.** The route table has
one entry, the internal subnet, so an IP outside it is unreachable before any
packet is sent. Names inside the network (`proxy`) resolve through Docker's
embedded resolver; names outside it get `SERVFAIL` from that same resolver,
so DNS is not a side channel here — this daemon does not forward queries off
an internal network. That is a property of the daemon version measured
(29.6.2), and the acceptance run on a Linux host should repeat the `nslookup`
line rather than assume it: an older engine that forwarded would leave a
low-bandwidth exfiltration channel open through DNS while every TCP probe
above still passed.

### 2.5 Does grok honour `HTTPS_PROXY`? Measured, three ways

The reviewer image (`grok 1.0.5`) on the internal network, with a bogus API key
so the call is answered by `api.x.ai` and no credential is involved. `tail -6`
of grok's output in each case:

```
$ docker run --rm --network ashamray-probe-internal --env XAI_API_KEY=xai-bogus-probe-key \
    ashamray-probe-fixed-1000-1000 timeout 60 grok -p ok
(no output)
rc=124

$ docker run --rm --network ashamray-probe-internal --env XAI_API_KEY=xai-bogus-probe-key \
    --env HTTPS_PROXY=http://proxy:8888 ashamray-probe-fixed-1000-1000 timeout 60 grok -p ok
Error: Internal error: {
  "message": "API error (status 400 Bad Request): invalid-argument: Incorrect API key provided. You can obtain an API key from https://console.x.ai.",
  "http_status": 400
}
rc=1

$ ... --env https_proxy=http://proxy:8888 ... timeout 60 grok -p ok
  "message": "API error (status 400 Bad Request): invalid-argument: Incorrect API key provided. ...",
  "http_status": 400
}
rc=1

$ ... --env HTTP_PROXY=http://proxy:8888 ... timeout 60 grok -p ok     (HTTP_PROXY only)
(no output)
rc=124
```

Proxy log across the runs: `allow api.x.ai` four times per run that reached
it, and nothing for the two that hung.

**So: `HTTPS_PROXY` is honoured, so is the lower-case spelling, and
`HTTP_PROXY` alone is not consulted for the HTTPS call.** With the variable
set, grok's request left the internal network through the tunnel and was
answered by `api.x.ai` with the error the bogus key deserves; without it, the
same command produced nothing for sixty seconds and was killed. The four
`allow` lines per run are grok retrying the 400, which is grok's business. The
binary also carries the strings `HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY` and
their lower-case forms (`grep -a -o` over `grok-linux-x86_64`), and
`grok --help` names no proxy flag — consistent with the measurement, and not a
substitute for it.

**What this does not establish** — see §5 — is which hosts an *authenticated*
call touches. The allow-list is `api.x.ai` and `auth.x.ai` because the issue
names them; the bogus-key run proves the first is enough for an API-key
session, and nothing here proves the OAuth refresh goes to the second rather
than to a third host the list lacks.

### 2.6 Readiness

`--detach` returns before the script binds, so the script waits for the
socket rather than for the container:

```
$ docker exec ashamray-probe-proxy python3 -c 'import socket; socket.create_connection(("127.0.0.1", 8888), timeout=1).close()'
rc=0
```

That probe connects and closes without a request line, which the proxy logs as
`deny -`. Harmless, and worth knowing so the first line of every review's proxy
log is not read as an attack.

### 2.7 The invocation form, measured for MSYS

On this host MSYS rewrites a leading-slash argument into a Windows path — the
reason `grok-review.sh` already needs `MSYS2_ARG_CONV_EXCL="/review"` for its
`--workdir`. A `python3 /usr/local/lib/egress-proxy.py` argument would be one
more exclusion to remember, so the script is installed on `PATH` under a bare
name instead, and that form was built and run with no exclusion set:

```
# Dockerfile.proxy-probe (throwaway)
FROM python:3.12-alpine
COPY --chmod=755 egress-proxy.py /usr/local/bin/egress-proxy
USER nobody

$ docker run --detach --name ashamray-probe-proxy --network ashamray-probe-internal \
    --network-alias proxy ashamray-probe-proxy-img egress-proxy
ready after 1 attempt(s): 1
$ curl -sS -o /dev/null -w 'http=%{http_code}\n' -x http://proxy:8888 https://api.x.ai/
http=421
$ curl -sS -o /dev/null -w 'http=%{http_code}\n' -x http://proxy:8888 https://example.com/
curl: (7) CONNECT tunnel failed, response 403
```

The shebang is `#!/usr/bin/env python3`, so the same file runs identically on
the Debian reviewer image, which is where §4.1 puts it.

### 2.8 Concurrency

The network and the proxy are named from the review's own `mktemp` directory,
so two reviews on one daemon get two networks and two proxies, and `proxy` as
an alias is unambiguous because each reviewer sees only its own network. The
image is already used by id for the same reason.

### 2.9 Teardown

```
$ docker rm --force ashamray-probe-proxy
ashamray-probe-proxy
$ docker network rm ashamray-probe-internal
ashamray-probe-internal
rc=0
$ docker images --format '{{.Repository}}:{{.Tag}}' | grep '^ashamray-probe-' | xargs -r docker image rm
rc=0
--- remaining
0 images, 0 containers, 0 networks
```

**Container first, then network.** `docker network rm` refuses a network with
a member still attached, so the order in `cleanup()` is not a nicety.

---

## 3. Task A — the Dockerfile edit

`Dockerfile.fixed` is the whole file; this is the one block that differs from
`.claude/sandbox/Dockerfile`. The repository's copy is CRLF and the scratchpad
copy is LF — paste the block, do not copy the file.

Old:

```dockerfile
ARG REVIEWER_UID=1000
ARG REVIEWER_GID=1000
RUN groupadd --gid "${REVIEWER_GID}" reviewer \
    && useradd --create-home --uid "${REVIEWER_UID}" --gid "${REVIEWER_GID}" reviewer
USER reviewer
ENV HOME=/home/reviewer
```

New — the comment continues the paragraph that already ends "…argued rather
than discovered.":

```dockerfile
#
# **An id the base image already uses is reused, not created.** `groupadd` and
# `useradd` refuse an id that exists — `GID '100' already exists`, `UID 65534
# is not unique` — and debian:trixie-slim already holds gid 20 (dialout, a
# typical macOS primary group), gid 100 (users, a common Linux one) and
# uid/gid 65534 (nobody/nogroup). A host with any of those could not build the
# reviewer at all (#17). So the group is created only when its gid is free, and
# a uid that is taken has its account RENAMED into `reviewer` rather than
# joined by a second entry: `useradd --non-unique` would leave the base's name
# first in /etc/passwd, so `id` and getpwuid would answer `nobody` with a home
# of /nonexistent, and anything that resolves HOME through the account rather
# than the environment would look in the wrong place. The name and the path
# are both load-bearing — grok-review.sh mounts the credentials at the literal
# /home/reviewer/.grok/, and `USER` below is by name — so both are fixed here
# whichever branch ran, and the last line checks that they are.
#
# **0:0 is refused on purpose, where it used to be refused by accident.** The
# old form failed on root because root exists; reuse would instead rename root
# into `reviewer` and hand the review a root shell. grok-review.sh refuses to
# pass 0 before the build starts and keeps doing so; this is the same refusal
# one layer down, for a build invoked by hand.
ARG REVIEWER_UID=1000
ARG REVIEWER_GID=1000
RUN set -eu; \
    [ "${REVIEWER_UID}" -ne 0 ] && [ "${REVIEWER_GID}" -ne 0 ] || \
        { echo "refusing REVIEWER_UID=${REVIEWER_UID} REVIEWER_GID=${REVIEWER_GID}: the reviewer is non-root by design, and reusing root's account would make it root" >&2; exit 1; }; \
    getent group "${REVIEWER_GID}" >/dev/null \
        || groupadd --gid "${REVIEWER_GID}" reviewer; \
    existing="$(getent passwd "${REVIEWER_UID}" | cut -d: -f1)"; \
    if [ -n "${existing}" ]; then \
        usermod --login reviewer --comment "" --gid "${REVIEWER_GID}" \
            --home /home/reviewer --shell /bin/sh "${existing}"; \
        mkdir -p /home/reviewer; \
        chown "${REVIEWER_UID}:${REVIEWER_GID}" /home/reviewer; \
        chmod 700 /home/reviewer; \
    else \
        useradd --create-home --uid "${REVIEWER_UID}" --gid "${REVIEWER_GID}" reviewer; \
    fi; \
    [ "$(id -u reviewer)" = "${REVIEWER_UID}" ] \
        && [ "$(id -g reviewer)" = "${REVIEWER_GID}" ] \
        && [ "$(getent passwd reviewer | cut -d: -f6)" = /home/reviewer ] \
        && [ -d /home/reviewer ]
USER reviewer
ENV HOME=/home/reviewer
```

Three choices in it that a reviewer will ask about:

- **`usermod` without `--move-home`.** The colliding account's home is
  `/nonexistent` for `nobody` and `/usr/sbin` for `daemon`; moving either is
  wrong, and the first does not exist to move. The new home is made and owned
  explicitly, at the `0700` `HOME_MODE` the base's `useradd` would have used.
- **`--comment ""`.** Without it the renamed entry keeps the GECOS `nobody`
  and reads `reviewer:x:65534:65534:nobody:…`, which is true and misleading in
  equal measure; `useradd` gives an empty field, so the two branches match.
- **The group is never renamed.** Nothing names it: `USER` is by user, the
  mounts are by path, and `id` printing `nogroup` or `users` is an accurate
  statement about the host that built the image.

**What renaming `nobody` costs.** After a 65534 build there is no `nobody` in
the image. Nothing in it uses that name — apt has already run under `_apt`,
and `sync` and `_apt` reference the gid, which is untouched — but it is a
change to the base image's account table and it is stated here rather than
left to be noticed.

---

## 4. Task B — the edits

### 4.1 Dockerfile: install the proxy

After the `apt-get` layer and before the `ARG REVIEWER_UID` block, as root.
The build context is already `.claude/sandbox/`, so the file lands beside the
Dockerfile.

Old — nothing; this is an insertion after
`    && rm --recursive --force /var/lib/apt/lists/*`.

New:

```dockerfile

# The reviewer's only way out, baked into the image rather than bind-mounted:
# a mount would need `:Z` on an SELinux host, and `:Z` relabels the file it
# names — which would be a file inside this repository's checkout. On PATH
# under a bare name, because grok-review.sh runs under MSYS on one host and
# MSYS rewrites a leading-slash argument into a Windows path; `--workdir
# /review` already costs that script an exclusion, and this would be a second.
# Root-owned and readable by everyone, which is all a script needs. It is run
# by grok-review.sh in a SECOND container of this image — no clone, no
# credential, and a leg on the bridge the reviewer's container does not have.
COPY --chmod=755 egress-proxy.py /usr/local/bin/egress-proxy
```

`egress-proxy.py` itself is the file beside this plan, copied into
`.claude/sandbox/` unchanged.

### 4.2 grok-review.sh: the header (lines 17–29)

Old:

```bash
# Egress is NOT restricted, and that is one of two residuals, recorded rather
# than hidden. Confining it to api.x.ai needs an allow-list proxy on an internal
# network; Docker alone offers "all" or "none", and "none" stops the review too.
#
# **The credential half is NARROWED, not closed, and this header said "closed"
# for months (#58).** No gh token, no SSH keys and no host filesystem beyond the
# clone — all three genuinely absent. But the fallback path below copies
# ~/.grok/auth.json in, and that file carries a REFRESH-TOKEN-BEARING OAuth
# session for the x.ai account: anything inside can read it and, given the
# unrestricted egress above, post it anywhere. **The two residuals are therefore
# not independent** — the open one is what makes the credential that crosses
# exploitable — and writing them as separate bullet points is what let the
# second one read as settled.
```

New:

```bash
# Egress is CONFINED to api.x.ai and auth.x.ai, and it takes two containers.
# The reviewer runs on a network created with --internal, which Docker gives no
# gateway: a member routes to the other members and to nothing else, and the
# embedded resolver answers SERVFAIL for any name outside it. The one member
# with a second leg on the bridge is a proxy — .claude/sandbox/egress-proxy.py,
# a CONNECT-only tunnel with a host allow-list — and the reviewer reaches it
# through HTTPS_PROXY, which grok honours (measured: without it the same call
# hangs on the internal network; with it the call is answered by api.x.ai).
# Docker alone offers "all" or "none"; the proxy is the third option (#17).
#
# **The credential half is NARROWED, not closed, and this header said "closed"
# for months (#58).** No gh token, no SSH keys and no host filesystem beyond the
# clone — all three genuinely absent. But the fallback path below copies
# ~/.grok/auth.json in, and that file carries a REFRESH-TOKEN-BEARING OAuth
# session for the x.ai account: anything inside can read it. What it can no
# longer do is post it anywhere — the only hosts a byte can reach are the two
# the session is for — which is why the egress half was the one to close: it
# was what made the crossing credential exploitable. What remains is the
# session's own blast radius against x.ai, which no boundary here can shrink;
# a scoped, revocable XAI_API_KEY still crosses no file at all.
```

### 4.3 grok-review.sh: cleanup (lines 226–230)

Old:

```bash
cleanup() {
  rm -rf "$work" "$auth" 2>/dev/null || true
  rm -f "$result" 2>/dev/null || true
}
trap cleanup EXIT
```

New:

```bash
# The proxy and its network are torn down here too, in that order: a network
# with a member still attached refuses to be removed, and a proxy left running
# is a container with egress that nothing is watching. Both names are assigned
# after the build, so the guard is on the variable and not on docker's answer.
net=""
proxy=""
cleanup() {
  [ -z "$proxy" ] || docker rm --force "$proxy" >/dev/null 2>&1 || true
  [ -z "$net" ] || docker network rm "$net" >/dev/null 2>&1 || true
  rm -rf "$work" "$auth" 2>/dev/null || true
  rm -f "$result" 2>/dev/null || true
}
trap cleanup EXIT
```

### 4.4 grok-review.sh: bring the network up (after line 316)

**Placed after the build and before the credential block, not beside the
review's own `docker run`** — and the reason is that three `docker run`s in
this script carry the credential, not one. The key probe and the limit probe
are model calls too, and a probe that reaches the network unconfined is the
residual back for one second per round. The network has to exist before the
first of them.

Old — nothing; insertion after
`  { echo "docker build produced no image id" >&2; exit 10; }`.

New:

```bash

# The network the reviewer runs on, and the one container allowed off it.
# `--internal` is the mechanism: Docker gives the network no gateway, so a
# member has a route to the other members and to nothing else, and the
# embedded resolver refuses to forward a name outside it. The proxy is a member
# with a second leg on the default bridge, running THIS image with no clone and
# no credential mounted — it already carries python3 for the licence gate, so
# the proxy is one stdlib script on PATH and no second image to pin.
# `--network-alias proxy` is what the reviewer's HTTPS_PROXY names, and it is
# unambiguous because the network is per review: both names derive from this
# run's own temp directory, so two reviews on one daemon never share either.
#
# Created here, after the build and before the credential probes, because the
# probes are model calls that carry the credential too. Every reviewer-side
# `docker run` from here down takes $net_args.
#
# Waited for, not assumed: `--detach` returns before the script binds, and a
# reviewer started into that gap fails its first call on a refused connection,
# which reads like a dead proxy rather than an early start. Ten seconds, then
# refuse — a proxy that never listened is not one to review behind.
net="grok-review-$(basename "$work")"
proxy="$net-proxy"
docker network create --internal "$net" >/dev/null ||
  { echo "could not create the reviewer's internal network" >&2; exit 15; }
docker run --detach --name "$proxy" --network "$net" --network-alias proxy \
  "$image" egress-proxy >/dev/null ||
  { echo "could not start the egress proxy" >&2; exit 15; }
docker network connect bridge "$proxy" ||
  { echo "could not give the egress proxy its leg on the bridge" >&2; exit 15; }
ready=0
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if docker exec "$proxy" python3 -c \
       'import socket; socket.create_connection(("127.0.0.1", 8888), timeout=1).close()' \
       >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
[ "$ready" -eq 1 ] ||
  { echo "the egress proxy did not start listening; the review did not run" >&2; exit 15; }
# HTTPS_PROXY is the variable grok reads for its API calls — measured: with
# HTTP_PROXY alone the call hangs on the internal network. HTTP_PROXY is set
# beside it so a plain-http attempt is refused by the proxy at once rather than
# timing out on a route that does not exist.
net_args=(--network "$net"
          --env HTTPS_PROXY=http://proxy:8888
          --env HTTP_PROXY=http://proxy:8888)
```

Exit 15 is the next free code; 2–14 are taken. `ship.md` branches on the
helper's exits in prose — 12 skips the round, 13 stops the loop — and would
gain a sentence for 15: a proxy that could not be brought up is a "did not
run", like 4 and 7, and spends no slot because it sits above the reservation.

### 4.5 grok-review.sh: the key probe (lines 335–336)

Old:

```bash
   key_probe=$(docker run --rm --env XAI_API_KEY "$image" \
     grok -p "ok" 2>&1); then
```

New:

```bash
   key_probe=$(docker run --rm "${net_args[@]}" --env XAI_API_KEY "$image" \
     grok -p "ok" 2>&1); then
```

### 4.6 grok-review.sh: the limit probe (line 399)

Old:

```bash
limit_probe=$(docker run --rm "${mounts[@]}" "$image" grok -p "ok" 2>&1) || probe_rc=$?
```

New:

```bash
limit_probe=$(docker run --rm "${net_args[@]}" "${mounts[@]}" "$image" grok -p "ok" 2>&1) || probe_rc=$?
```

### 4.7 grok-review.sh: the review (lines 465–470)

Old:

```bash
docker run --rm \
  --volume "$(host_path "$work/repo"):/review:Z" \
  "${mounts[@]}" \
  --workdir /review \
  "$image" \
  grok -p "/review-branch" --permission-mode bypassPermissions --output-format json >"$result"
```

New:

```bash
docker run --rm \
  "${net_args[@]}" \
  --volume "$(host_path "$work/repo"):/review:Z" \
  "${mounts[@]}" \
  --workdir /review \
  "$image" \
  grok -p "/review-branch" --permission-mode bypassPermissions --output-format json >"$result"
```

`test_grok_helpers.py`'s `ALLOWED_RESULT_USES` matches this command after
joining its continuation lines with `docker run .* grok -p "/review-branch"
…`, so the inserted line does not break that entry. Run `py -3.12 -m unittest`
in `.claude/scripts` after the edit all the same — that suite is where a
second pattern on this command would live, and this plan read one.

### 4.8 What the proxy is not

It is not a `NO_PROXY`-aware, general-purpose HTTP proxy, and it must not
become one. It forwards one verb to one port for two names. The reviewer's
`git` never needs the network — the clone is local and `origin` is a bind
mount — and the licence gate is stdlib. If a future grok version needs a third
host, the change is one entry in `EGRESS_ALLOW` and a line in the header
saying why, which is the reviewed diff an allow-list is for.

---

## 5. Task C — SELinux acceptance on an enforcing host

Not exercised here; this is the procedure. It assumes Fedora or RHEL, Docker
Engine (not Podman — `grok-review.sh` invokes `docker`), a user who is not
root and is in the `docker` group, and a branch with exactly one open pull
request, because the script refuses anything else before it reaches a mount.

**What is under test.** Four bind mounts in `grok-review.sh` carry `:Z`:

| line | mount | what it is |
|---|---|---|
| 466 | `"$(host_path "$work/repo"):/review:Z"` | the clone, under `${TMPDIR:-/tmp}/grok-review-XXXXXX/repo` |
| 364 | `"$(host_path "$auth/auth.json"):/home/reviewer/.grok/auth.json:Z"` | OAuth session copy, under `${TMPDIR:-/tmp}/grok-review-auth-XXXXXX/` |
| 384 | `"$(host_path "$auth/$extra"):/home/reviewer/.grok/$extra:Z"` for `agent_id` | same directory |
| 384 | the same line, for `config.toml` | same directory |

The three credential mounts exist only on the OAuth path, so an acceptance run
with a usable `XAI_API_KEY` tests one mount, not four. **Run it with the key
unset** so all four are exercised. §4's proxy adds no mount — the script is
baked in — which is why it was baked in.

### 5.1 Establish that SELinux is confining containers at all

```
$ getenforce
Enforcing
$ docker info --format '{{.SecurityOptions}}'
[name=seccomp,profile=builtin name=selinux name=cgroupns]
```

**If `name=selinux` is absent, stop: nothing below tests anything.** Docker
Engine does not enable SELinux labelling by default on every distribution;
without it, `:Z` is accepted and ignored, and every mount works for the wrong
reason. Enable it with `"selinux-enabled": true` in `/etc/docker/daemon.json`,
restart the daemon, and re-run `docker info`.

### 5.2 Prove the negative first

A mount *without* `:Z`, of a file in the same place the script makes its
copies, has to be denied — or the positive result in 5.3 is vacuous.

```
$ d=$(mktemp -d /tmp/selinux-probe-XXXXXX); echo hello > "$d/f"; ls -Z "$d/f"
unconfined_u:object_r:user_tmp_t:s0 /tmp/selinux-probe-abc123/f
$ docker run --rm --volume "$d/f:/f" debian:trixie-slim cat /f
cat: /f: Permission denied
$ sudo ausearch -m avc -ts recent | grep -E 'comm="cat".*user_tmp_t'
type=AVC msg=audit(...): avc:  denied  { read } for  pid=... comm="cat" name="f" ... scontext=system_u:system_r:container_t:s0:c123,c456 tcontext=unconfined_u:object_r:user_tmp_t:s0 tclass=file permissive=0
```

Expected: `Permission denied`, and one AVC whose `scontext` is `container_t`
and whose `tcontext` is the host's `user_tmp_t`. That denial is what `:Z` is
there to remove. If `cat` succeeds here, SELinux is not confining this daemon's
containers and 5.1 was misread.

### 5.3 Prove the positive on the same file

```
$ docker run --rm --volume "$d/f:/f:Z" debian:trixie-slim cat /f
hello
$ ls -Z "$d/f"
system_u:object_r:container_file_t:s0:c789,c12 /tmp/selinux-probe-abc123/f
$ sudo ausearch -m avc -ts recent | grep -c 'comm="cat"'
1
```

Expected: the file reads, its label on the host is now `container_file_t` with
a category pair, and the AVC count has not moved since 5.2 — still the one
denial, none new. Remove `$d` afterwards.

### 5.4 The full review, with every mount on the OAuth path

```
$ unset XAI_API_KEY
$ ls ~/.grok/auth.json ~/.grok/agent_id ~/.grok/config.toml    # all three must exist
$ since=$(date '+%H:%M:%S')
$ bash .claude/scripts/grok-review.sh 1 full; echo "rc=$?"
grok finished its turn (stopReason "end_turn") — findings, if any, are in suggestions.md
rc=0
$ sudo ausearch -m avc -ts "$since" -c grok; sudo ausearch -m avc -ts "$since" -c git
<no matches>
```

This spends ledger slot 1 on that pull request; say so in the PR the run is
for. Expected: exit 0 and `end_turn`, and **no AVC with `comm="grok"` or
`comm="git"`**. `suggestions.md` present or absent is the review's verdict and
is not the subject here — but it *must* be importable, which is the `/review`
mount being writable: the reviewer writes it inside, the host copies it out.

### 5.5 What to look for if 5.4 is not clean

- `tcontext=…:user_tmp_t` or `tmp_t` on a path under `/tmp/grok-review-*` —
  a mount that lost its `:Z`. Check the line numbers in the table above
  against the script as merged.
- `tcontext=…:container_file_t:s0:cA,cB` with a **different** category pair
  from the reviewer's `scontext` — two containers relabelled the same path.
  `:Z` gives each container a private pair, so a file shared between the
  reviewer and any other container needs `:z`. §4 has no such file by design;
  if one appears, that is the finding.
- `comm="grok"` denied `{ write }` on `auth.json` — grok refreshing the
  session mid-run, which the copies exist to permit. That is a label problem
  on the copy, not on the original; the original is never mounted.
- Any AVC with `scontext=…:container_t` and a `tcontext` that is not under
  `/tmp` — the container reached for something the boundary says it cannot
  have. Not an SELinux problem; a boundary finding, and the more important
  kind.

`sudo ausearch -m avc -ts "$since" | audit2why` explains each denial in a
sentence and is the fastest read.

### 5.6 The uid half, on the same host

Item 1's acceptance says a review completes on a host whose uid is not 1000
and on one whose ids collide. On the same Fedora host:

```
$ sudo useradd --uid 2001 --gid 100 --create-home probe-collide     # gid 100 = users, which the image holds
$ sudo usermod --append --groups docker probe-collide
$ sudo -iu probe-collide bash -c 'cd <checkout> && bash .claude/scripts/grok-review.sh 2 full; echo rc=$?'
```

Expected: the build passes the `RUN` in §3 with `users` reused, and the review
completes. The clone the script makes is owned by 2001 on the host and
readable inside by the same uid — which is the whole reason the ids are
passed, and the reason Docker Desktop could never test it. `id` inside will
print `gid=100(users)`, which is correct.

---

## 6. What remains unverified, and why

- **An authenticated call through the proxy.** Refused by the harness
  classifier when this session tried to copy the host's OAuth files into a
  probe container; not attempted another way. So whether `auth.x.ai` is the
  host the OAuth refresh actually uses — and whether an authenticated grok
  touches any third host — is not measured. The allow-list is the issue's
  two names. The first review run after applying §4 is that measurement, and
  if it dies with a connection error, `docker logs` on the proxy container
  will name the refused host in a `deny` line, which is the diagnostic the
  proxy logs for. `grok-review.sh`'s `cleanup()` removes the container, so
  read the log before the script exits or add a `docker logs "$proxy" >&2`
  to the exit-4 path while diagnosing.
- **A full `/review-branch` behind the proxy.** Needs a ledger slot and an open
  pull request; the acceptance run in §5.4 is that, on Linux, and the same
  command on this host is the Windows half.
- **The uid fix against a real host-owned mount.** Docker Desktop maps
  ownership, so every `docker run` above would have succeeded at 1000/1000
  too; what was proven is that the image *builds* with colliding ids and that
  the account inside is the one the mounts and `USER` name. That the mounted
  clone is then readable is §5.6's job.
- **SELinux, entirely.** §5 is a procedure, not a result.
- **DNS forwarding off an internal network on other engine versions.** 29.6.2
  answers `SERVFAIL`; §2.4 says to repeat the `nslookup` line rather than
  carry the claim.
- **`docker network connect bridge` on a daemon whose default bridge is
  disabled** (`"bridge": "none"` in `daemon.json`). Not this host's
  configuration; a named non-internal network created beside `$net` would be
  the substitute and was not measured.

---

## 7. Prose sites that change once this is applied

Each of these states the egress residual or the uid deferral, and the one rule
says they move together.

| site | what it says now | what it says after |
|---|---|---|
| `.claude/sandbox/Dockerfile` header, lines 21–24 | "Egress is deliberately NOT restricted, and that is the remaining residual … Docker alone offers 'all' or 'none'" | Egress is confined to two hosts by an internal network and `egress-proxy.py`; the remaining residual is the crossing credential's own blast radius at x.ai |
| `.claude/sandbox/Dockerfile`, the uid paragraph before `ARG REVIEWER_UID` | ends at "argued rather than discovered" | continues with the reuse argument in §3 |
| `.claude/scripts/grok-review.sh` header, lines 17–29 | §4.2's old block | §4.2's new block |
| `docs/harness-boundaries.md`, lines 187–207 | "**Egress is not restricted** — … needs an allow-list proxy Docker cannot supply alone. **And the credential half is narrowed rather than closed** … *The open residual is what makes the crossing one exploitable*" | Egress is confined; the credential still crosses on the OAuth path and can now reach only the two hosts it is for; state the proxy as a grant wider than the operation if the reviewer's own network is judged one — it is not, since the session grants nothing new to run it |
| `.claude/commands/ship.md`, lines 833–835 | "Residual, stated in the script and in `CLAUDE.md`: **egress is not restricted**…" | the residual is gone; the paragraph that follows (#58's credential note) stays and loses "given the unrestricted egress" if it carries it. The step that branches on exits 12 and 13 gains a sentence for 15 |
| `CLAUDE.md` | names neither residual directly; forwards to `docs/harness-boundaries.md` for the boundary. Issue #17 says "recorded in `CLAUDE.md`", which was true before the extraction | no change unless a sentence there says "two residuals" — grep `residual` there returns only the PR rows |
| `docs/superpowers/specs/2026-08-08-review-sandbox-design.md` §5 | "**Egress.** The container reaches the whole network…" | **not edited.** `CLAUDE.md`'s precedence rule makes the specs a frozen record, never edited to match the code that followed. §5 stays as the statement of what that PR did not close |
| `docs/pr-decision-log.md` | gains a `## PR-NN` row: the residual PR #16's row named as owed, now paid, and the rule that moved — the boundary's egress half | the row records §2's measurements by reference to this plan's commands |
| `.claude/scripts/test_grok_helpers.py` | reads `grok-review.sh` for the `docker run` line and `cleanup()` | a case whose subject is *what the reviewer's `docker run`s are attached to*: every `docker run` in the script that names `"$image"` carries `"${net_args[@]}"`, the one gate shape this repository trusts |
| issue #17 | three items open | item 1 closes on §5.6's run, item 2 on §5.4's, item 3 on §4 plus the first authenticated run |

---

## 8. Files

All under this directory.

| file | what |
|---|---|
| `PLAN.md` | this |
| `Dockerfile.orig` | byte-for-byte copy of `.claude/sandbox/Dockerfile`, the reproduction target |
| `Dockerfile.fixed` | the Task A fix, whole file, LF; built and run at 1000/1000, 65534/65534, 1000/100, 65534/1000, refused at 0/0 |
| `egress-proxy.py` | the Task B proxy — stdlib, CONNECT-only, allow-list by `EGRESS_ALLOW`, port by `EGRESS_PORT` |
