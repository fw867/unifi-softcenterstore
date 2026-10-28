#!/usr/bin/env python3
"""Linux regression: real Xray + local dual-stack DNS fixture (requires port 53).

Usage (as root in an isolated network namespace):
    unshare --net sh -c 'ip link set lo up; python3 tools/test-xwall-dns.py \
        --xray /path/to/xray --jq /path/to/jq'
Geosite/GeoIP entries are replaced by deterministic fixture rules; no public DNS
or proxy node is used. Netfilter installation still needs router verification.
"""
import argparse
import ipaddress
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import threading
import time


def receive(sock, size):
    data = b""
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise EOFError("DNS connection closed")
        data += chunk
    return data


def answer(query, local):
    end = 12
    labels = []
    while query[end]:
        labels.append(query[end + 1:end + 1 + query[end]].decode())
        end += query[end] + 1
    end += 5
    domain = ".".join(labels)
    if local and not domain.endswith((".lan", ".white.test", ".internal.test")):
        # Router DNS cannot resolve public names in this regression fixture.
        return query[:2] + struct.pack("!5H", 0x8183, 1, 0, 0, 0) + query[12:end]
    qtype = struct.unpack("!H", query[end - 4:end - 2])[0]
    ipv4 = "203.0.113.10" if local else "203.0.113.9"
    data = ipaddress.ip_address(ipv4 if qtype == 1 else "2001:db8::9").packed
    return (query[:2] + struct.pack("!5H", 0x8180, 1, 1, 0, 0) + query[12:end]
            + b"\xc0\x0c" + struct.pack("!HHIH", qtype, 1, 60, len(data)) + data)


def serve_udp(sock):
    while True:
        query, peer = sock.recvfrom(4096)
        sock.sendto(answer(query, sock.getsockname()[0] == "127.0.0.1"), peer)


def serve_tcp(sock):
    while True:
        conn, _ = sock.accept()
        with conn:
            conn.settimeout(5)
            try:
                while True:
                    query = receive(conn, struct.unpack("!H", receive(conn, 2))[0])
                    response = answer(query, sock.getsockname()[0] == "127.0.0.1")
                    conn.sendall(struct.pack("!H", len(response)) + response)
            except (EOFError, OSError):
                pass


def query_dns(network, domain, qtype):
    query = (struct.pack("!6H", 1234, 0x100, 1, 0, 0, 0)
             + b"".join(bytes([len(label)]) + label.encode() for label in domain.split("."))
             + b"\0" + struct.pack("!HH", qtype, 1))
    with socket.socket(socket.AF_INET, network) as sock:
        sock.settimeout(4)
        sock.connect(("127.0.0.1", 1280 if network == socket.SOCK_STREAM else 1281))
        if network == socket.SOCK_STREAM:
            sock.sendall(struct.pack("!H", len(query)) + query)
            response = receive(sock, struct.unpack("!H", receive(sock, 2))[0])
        else:
            sock.send(query)
            response = sock.recv(4096)
    ident, flags, _, count, _, _ = struct.unpack("!6H", response[:12])
    assert ident == 1234 and flags & 0x8000 and flags & 15 == 0, response.hex()
    return count, response


def fixture_rules(value):
    if isinstance(value, dict):
        return {key: fixture_rules(item) for key, item in value.items()}
    if isinstance(value, list):
        return [fixture_rules(item) for item in value]
    if isinstance(value, str):
        if value.startswith("geosite:"):
            return "domain:cn.test" if value == "geosite:cn" else "domain:proxy.test"
        if value.startswith("geoip:"):
            return "10.0.0.0/8"
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--xray", required=True)
    parser.add_argument("--jq", required=True)
    parser.add_argument("--script", type=Path, default=Path(__file__).resolve().parents[1] / "apps/xwall/xwall")
    args = parser.parse_args()
    source = args.script.read_text()
    # Source only config defaults and the generator, never start/stop or firewall code.
    defaults = source[source.index('XWALL_CONFIG="'):source.index('\nlog_date()')]
    build = source[source.index("build_xray_config() {"):source.index("\nrun_config_test()")]
    decode = source[source.index("decode_b64() {"):source.index("\ndetect_lan_ip()")]
    # 127.0.0.2 represents a detected LAN address with no usable DNS service.
    for address in ("127.0.0.1", "127.0.0.3", "127.0.0.4"):
        for network, handler in [(socket.SOCK_DGRAM, serve_udp), (socket.SOCK_STREAM, serve_tcp)]:
            sock = socket.socket(socket.AF_INET, network)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            sock.bind((address, 53))
            if network == socket.SOCK_STREAM:
                sock.listen(32)
            threading.Thread(target=handler, args=(sock,), daemon=True).start()
    with tempfile.TemporaryDirectory(prefix="xwall-dns-") as temp:
        work = Path(temp)
        (work / "user.json").write_text(json.dumps({"outbounds": [{"tag": "proxy", "protocol": "freedom"}]}))
        runner = (defaults + "\n" + decode + "\n" + build + '\n'
                  + 'log_date() { :; }\ndetect_lan_ip() { echo 127.0.0.2; }\n'
                  + 'jq() { "$JQ_BIN" "$@"; }\n'
                  + 'USER_JSON="$WORK/user.json"\nXRAY_JSON="$WORK/config.json"\n'
                  + 'ASSET_DIR="$WORK"\nXRAY_LOG="$WORK/xray.log"\n'
                  + 'DNS_LOCAL_DOMAINS=\'["domain:lan"]\'\n'
                  + 'DNS_PORT=15353\nDNS_CN=127.0.0.3\nDNS_FOREIGN=127.0.0.4\nbuild_xray_config\n')
        (work / "generate.sh").write_text(runner)
        checks = 0
        for ipv6 in ("0", "1"):
            for mode in ("1", "2", "3", "4"):
                env = dict(os.environ, WORK=temp, JQ_BIN=args.jq, XWALL_IPV6_ENABLE=ipv6,
                           PROXY_MODE=mode, DNS_HIJACK="1", XWALL_SOCKS_ENABLE="0",
                           WHITE_DOMAIN_LIST="fixture.white.test", XWALL_LOCAL_DOMAINS="internal.test")
                subprocess.run(["dash", str(work / "generate.sh")], env=env, check=True)
                config = fixture_rules(json.loads((work / "config.json").read_text()))
                # Feed the same destination/tag as intercepted DNS, using ordinary local
                # sockets so this test needs no changes to host routing or firewall rules.
                for inbound in config["inbounds"]:
                    inbound["listen"] = "127.0.0.1"
                    inbound.pop("streamSettings", None)
                    inbound["settings"].update(address="127.0.0.2", port=53, followRedirect=False)
                (work / "config.json").write_text(json.dumps(config))
                with (work / "core.log").open("w") as log:
                    proc = subprocess.Popen([args.xray, "run", "-c", str(work / "config.json")], stdout=log, stderr=log)
                    try:
                        for _ in range(100):
                            assert proc.poll() is None, (work / "core.log").read_text()
                            try:
                                with socket.create_connection(("127.0.0.1", 1280), timeout=.1):
                                    # Xray starts TCP and UDP workers independently.
                                    udp = Path("/proc/net/udp").read_text().splitlines()[1:]
                                    if any(int(row.split()[1].split(":")[1], 16) == 1281 for row in udp):
                                        break
                            except OSError:
                                pass
                            time.sleep(.03)
                        else:
                            raise AssertionError("Xray listener did not start")
                        for network in (socket.SOCK_DGRAM, socket.SOCK_STREAM):
                            for domain in ("fixture.cn.test", "fixture.proxy.test", "fixture.other.test",
                                           "fixture.lan", "fixture.white.test", "fixture.internal.test"):
                                count, response = query_dns(network, domain, 1)
                                assert count > 0, (ipv6, mode, domain, "A empty")
                                expected = "198.18.0.0" if mode == "1" and domain.endswith("proxy.test") else "203.0.113.9"
                                if domain.endswith((".lan", ".white.test", ".internal.test")):
                                    expected = "203.0.113.10"
                                if expected == "198.18.0.0":
                                    assert ipaddress.ip_address(response[-4:]) in ipaddress.ip_network("198.18.0.0/15")
                                else:
                                    assert response[-4:] == ipaddress.ip_address(expected).packed
                                count, _ = query_dns(network, domain, 28)
                                expected_count = 1 if ipv6 == "1" else 0
                                assert count == expected_count, (ipv6, mode, domain, "AAAA", count)
                                checks += 2
                    except Exception:
                        print(f"FAIL IPv6={ipv6} mode={mode} domain={locals().get('domain')} network={locals().get('network')}")
                        print((work / "core.log").read_text())
                        print((work / "xray.log").read_text())
                        raise
                    finally:
                        proc.terminate()
                        proc.wait(timeout=5)
                print(f"PASS IPv6={ipv6} mode={mode}: UDP/TCP A/AAAA, public/local/whitelist/proxy", flush=True)
        print(f"PASS {checks} real DNS responses")


if __name__ == "__main__":
    main()
