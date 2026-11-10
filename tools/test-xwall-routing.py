#!/usr/bin/env python3
"""Regression with real Linux netfilter and UDP packets; never run on the router.

Requires iproute2, iptables/ip6tables, ipset, util-linux, dash, and kernel support
for ipset/TPROXY. Run in a disposable network namespace:
    unshare -Urn python3 tools/test-xwall-routing.py
This creates a second namespace and a veth pair to exercise PREROUTING. It does
not test printer/Agora connectivity or the UniFi firewall integration.
"""
import argparse
import itertools
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--script', type=Path, default=Path(__file__).resolve().parents[1] / 'apps/xwall/xwall')
args = parser.parse_args()
if os.geteuid() != 0:
    raise SystemExit('Run as root in an isolated network namespace; see usage above.')
try:
    if os.readlink('/proc/self/ns/net') == os.readlink('/proc/1/ns/net'):
        raise SystemExit('Refusing to modify the host network namespace.')
except PermissionError:
    # In an unprivileged user namespace, host /proc/1 is inaccessible. Such a
    # process cannot alter the host network; the veth creation also needs NET_ADMIN.
    if Path('/proc/self/uid_map').read_text().split() == ['0', '0', '4294967295']:
        raise SystemExit('Cannot verify network isolation.')

def run(*args, **kw):
    r = subprocess.run(args, text=True, capture_output=True, **kw)
    if r.returncode: raise RuntimeError((args, r.stdout, r.stderr))
    return r.stdout

def func(source, name):
    start = source.index(name + '() {')
    return source[start:source.index('\n}\n', start) + 3]

source = args.script.read_text()
functions = '\n'.join(func(source, n) for n in ['trim', 'add_direct_rule', 'load_dns_bypass_set', 'create_ipsetlist', 'add_bypass_ip', 'ipts_update', 'ipts_origin'])
constants = '''
CHAIN=XWALL
IPSET_BYPASS=xwall_bypass
FWMARK=0x08
ROUTE_TABLE=320
TCP_PORT=1280
UDP_PORT=1281
DNS_PORT=15353
DNS_CN=223.5.5.5
XWALL_SOCKS_ENABLE=0
XWALL_SOCKS_PORT=1283
log_date() { :; }
detect_lan_ip() { echo 192.168.99.1; }
ipt() { iptables "$@"; }
ipt6() {
    if [ "${FAIL6:-0}" = 1 ] && [ "$*" = '-t nat -I XWALL 2 -p udp --dport 53 -m set ! --match-set xwall_dns_bypass6 src -j RETURN' ]; then return 1; fi
    ip6tables "$@"
}
'''
run('ip', 'link', 'set', 'lo', 'up')
# A second network namespace provides real packets arriving at PREROUTING.
client = subprocess.Popen(['unshare', '-n', 'sleep', '300'])
try:
    for _ in range(100):
        if os.readlink(f'/proc/{client.pid}/ns/net') != os.readlink('/proc/self/ns/net'):
            break
        time.sleep(.01)
    run('ip', 'link', 'add', 'router', 'type', 'veth', 'peer', 'name', 'client')
    run('ip', 'link', 'set', 'client', 'netns', str(client.pid))
    run('ip', 'addr', 'add', '192.168.99.1/24', 'dev', 'router')
    run('ip', 'link', 'set', 'router', 'up')
    run('ip', '-6', 'addr', 'add', 'fd00::1/64', 'dev', 'router', 'nodad')
    ns = ['nsenter', '-t', str(client.pid), '-n']
    run(*ns, 'ip', 'link', 'set', 'lo', 'up')
    run(*ns, 'ip', 'addr', 'add', '192.168.99.2/24', 'dev', 'client')
    run(*ns, 'ip', 'link', 'set', 'client', 'up')
    run(*ns, 'ip', '-6', 'addr', 'add', 'fd00::2/64', 'dev', 'client', 'nodad')
    run(*ns, 'ip', '-6', 'route', 'add', 'default', 'via', 'fd00::1')
    run(*ns, 'ip', 'route', 'add', 'default', 'via', '192.168.99.1')
    checks = 0
    with tempfile.TemporaryDirectory() as work:
        script = Path(work) / 'rules.sh'
        script.write_text(constants + functions + '\nipts_origin\ncreate_ipsetlist\nadd_bypass_ip\nipts_update\n')
        for ipv6, dns in itertools.product('01', repeat=2):
            env = dict(os.environ, XWALL_IPV6_ENABLE=ipv6, DNS_HIJACK=dns, DIRECT_LIST='')
            run('dash', str(script), env=env)
            for binary in ['iptables'] + (['ip6tables'] if ipv6 == '1' else []):
                rules = run(binary, '-t', 'mangle', '-S', 'XWALL')
                assert 'xwall-udp-direct' not in rules, rules
                assert '--dport 53 -j RETURN' in rules and not any('--dport 53' in line and '-j TPROXY' in line for line in rules.splitlines()), rules
                assert '--to-ports 15353' in run(binary, '-t', 'nat', '-S', 'XWALL')
                assert '-j REDIRECT --to-ports 1280' in run(binary, '-t', 'nat', '-S', 'XWALL')
                checks += 4
            # A real UDP packet to an ordinary external IP, including QUIC, must still reach Xray.
            for dest, port, bypass in [('203.0.113.9', 40000, False), ('203.0.113.9', 443, False), ('203.0.113.9', 53, True), ('198.18.0.9', 40000, False)]:
                before = run('iptables', '-t', 'mangle', '-L', 'XWALL', '-n', '-v', '-x')
                previous = sum(int(line.split()[0]) for line in before.splitlines() if 'TPROXY' in line)
                run(*ns, 'python3', '-c', f'import socket; s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.sendto(b"fixture", ({dest!r},{port}))')
                counts = run('iptables', '-t', 'mangle', '-L', 'XWALL', '-n', '-v', '-x')
                hits = sum(int(line.split()[0]) for line in counts.splitlines() if 'TPROXY' in line)
                assert (hits == previous) == bypass, (ipv6, dns, dest, port, counts)
                checks += 1
            # Real TCP connection attempts must still hit REDIRECT, with ordinary UDP intercepted too.
            port = 41000 + checks
            run(*ns, 'python3', '-c', f'import socket; s=socket.socket(); s.settimeout(.2); s.connect_ex(("203.0.113.9",{port}))')
            counts = run('iptables', '-t', 'nat', '-L', 'XWALL', '-n', '-v', '-x')
            assert any(int(line.split()[0]) > 0 for line in counts.splitlines() if 'REDIRECT' in line), counts
            checks += 1
            if ipv6 == '1':
                for port, bypass in [(40000, False), (443, False), (53, True)]:
                    before = run('ip6tables', '-t', 'mangle', '-L', 'XWALL', '-n', '-v', '-x')
                    previous = sum(int(line.split()[0]) for line in before.splitlines() if 'TPROXY' in line)
                    run(*ns, 'python3', '-c', f'import socket; s=socket.socket(socket.AF_INET6,socket.SOCK_DGRAM); s.sendto(b"fixture", ("2001:db8::9",{port}))')
                    counts = run('ip6tables', '-t', 'mangle', '-L', 'XWALL', '-n', '-v', '-x')
                    hits = sum(int(line.split()[0]) for line in counts.splitlines() if 'TPROXY' in line)
                    assert (hits == previous) == bypass, (ipv6, dns, port, counts)
                    checks += 1
        # IPv4 set-match succeeds and IPv6 fails: only IPv6 must get the fallback before DNS.
        env = dict(os.environ, XWALL_IPV6_ENABLE='1', DNS_HIJACK='1', DIRECT_LIST='192.168.99.2\nfd00::2', FAIL6='1')
        run('dash', str(script), env=env)
        for binary, address, fallback in [('iptables', '192.168.99.2/32', False), ('ip6tables', 'fd00::2/128', True)]:
            for table in ['nat']:
                rules = run(binary, '-t', table, '-S', 'XWALL')
                # Normal device bypass also exists later in the chain.
                before_dns = rules[:rules.index('--dport 53')]
                assert (f'-s {address} -j RETURN' in before_dns) == fallback, rules
                checks += 1
        # Removing Xwall must preserve unrelated DNS rules with a firewall mark.
        run('iptables', '-I', 'INPUT', '-p', 'udp', '--dport', '53', '-m', 'mark', '--mark', '0x20', '-j', 'ACCEPT')
        script.write_text(constants + functions + '\nipts_origin || :\n')
        run('dash', str(script), env=env)
        assert '--mark 0x20' in run('iptables', '-S', 'INPUT')
        assert '--dport 53 -j XWALL' not in run('iptables', '-t', 'nat', '-S', 'PREROUTING')
        checks += 2
    print(f'PASS: {checks} real netfilter checks, including arriving UDP packets, IPv6 fallback and cleanup isolation')
finally:
    client.terminate()
    client.wait()
