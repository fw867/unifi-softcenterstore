#!/usr/bin/env python3
"""Real Xray regression for wildcard SNI, domain routing and FakeDNS recovery.
Run in an isolated network namespace (needs local DNS port 53):
  unshare -Urn python3 tools/test-xwall-sniffing.py --xray /path/to/xray --jq /path/to/jq
Uses only local TLS/DNS/SOCKS fixtures and disposable certificates.
"""
import argparse
import copy
import ipaddress
import json
import os
from pathlib import Path
import select
import socket
import ssl
import struct
import subprocess
import tempfile
import threading
import time


def receive(sock, size):
    data = b''
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise EOFError()
        data += chunk
    return data


def serve_dns(sock):
    while True:
        data, peer = sock.recvfrom(4096)
        sock.sendto(data[:2] + struct.pack('!5H', 0x8183, 1, 0, 0, 0) + data[12:], peer)


def serve_tls(sock, context, label):
    while True:
        conn, _ = sock.accept()
        try:
            with context.wrap_socket(conn, server_side=True) as tls:
                tls.settimeout(3)
                if tls.recv(32) == b'probe':
                    tls.sendall(label)
        except (OSError, ssl.SSLError):
            conn.close()


def socks_connect(sock, host, port):
    sock.sendall(b'\x05\x01\x00')
    assert receive(sock, 2) == b'\x05\x00'
    sock.sendall(b'\x05\x01\x00\x01' + socket.inet_aton(host) + struct.pack('!H', port))
    assert receive(sock, 4) == b'\x05\x00\x00\x01'
    receive(sock, 6)


def socks_handler(conn, targets):
    with conn:
        conn.settimeout(4)
        assert receive(conn, 1) == b'\x05'
        receive(conn, receive(conn, 1)[0])
        conn.sendall(b'\x05\x00')
        header = receive(conn, 4)
        assert header[1] == 1
        if header[3] == 1:
            host = socket.inet_ntoa(receive(conn, 4))
        elif header[3] == 3:
            host = receive(conn, receive(conn, 1)[0]).decode()
        else:
            raise AssertionError(header)
        port = struct.unpack('!H', receive(conn, 2))[0]
        targets.append((host, port))
        with socket.create_connection(('127.0.0.4', 24441), timeout=3) as upstream:
            conn.sendall(b'\x05\x00\x00\x01' + socket.inet_aton('127.0.0.4') + struct.pack('!H', 24441))
            while True:
                ready, _, _ = select.select([conn, upstream], [], [], 4)
                if not ready:
                    return
                for source in ready:
                    data = source.recv(65536)
                    if not data:
                        return
                    (upstream if source is conn else conn).sendall(data)


def serve_socks(sock, targets):
    while True:
        conn, _ = sock.accept()
        threading.Thread(target=socks_handler, args=(conn, targets), daemon=True).start()


def tls_probe(hostname, fake_ip=None):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    with socket.create_connection(('127.0.0.1', 1283 if fake_ip else 1280), timeout=3) as raw:
        raw.settimeout(3)
        if fake_ip:
            socks_connect(raw, fake_ip, 24440)
        with context.wrap_socket(raw, server_hostname=hostname) as tls:
            tls.sendall(b'probe')
            return tls.recv(32)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--xray', required=True)
    parser.add_argument('--jq', required=True)
    args = parser.parse_args()
    if os.geteuid() != 0:
        raise SystemExit('Run in an isolated network namespace as shown above.')
    root = Path(__file__).resolve().parents[1]
    source = (root / 'apps/xwall/xwall').read_text()
    defaults = source[source.index('XWALL_CONFIG="'):source.index('\nlog_date()')]
    decode = source[source.index('decode_b64() {'):source.index('\ndetect_lan_ip()')]
    build = source[source.index('build_xray_config() {'):source.index('\nrun_config_test()')]
    sockets = []
    with tempfile.TemporaryDirectory(prefix='xwall-sniff-') as temp:
        work = Path(temp)
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=fixture', '-keyout', str(work/'key.pem'), '-out', str(work/'cert.pem'), '-days', '1'], check=True, capture_output=True)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(str(work/'cert.pem'), str(work/'key.pem'))
        for address, port, label in [('127.0.0.2', 24440, b'direct'), ('127.0.0.4', 24441, b'proxy')]:
            sock = socket.socket(); sock.bind((address, port)); sock.listen(32); sockets.append(sock)
            threading.Thread(target=serve_tls, args=(sock, context, label), daemon=True).start()
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); sock.bind(('127.0.0.3', 53)); sockets.append(sock)
        threading.Thread(target=serve_dns, args=(sock,), daemon=True).start()
        targets = []
        sock = socket.socket(); sock.bind(('127.0.0.1', 24442)); sock.listen(32); sockets.append(sock)
        threading.Thread(target=serve_socks, args=(sock, targets), daemon=True).start()
        (work/'user.json').write_text(json.dumps({'outbounds':[{'tag':'proxy','protocol':'socks','settings':{'servers':[{'address':'127.0.0.1','port':24442}]}}]}))
        runner = defaults+'\n'+decode+'\n'+build+'''
log_date() { :; }
USER_JSON="$WORK/user.json"
XRAY_JSON="$WORK/config.json"
ASSET_DIR="$WORK"
XRAY_LOG="$WORK/xray.log"
DNS_LOCAL_DOMAINS='["domain:lan"]'
DNS_PORT=15353
DNS_CN=127.0.0.3
DNS_FOREIGN=127.0.0.3
build_xray_config
'''
        (work/'generate.sh').write_text(runner)
        env = dict(os.environ, WORK=temp, PROXY_MODE='1', XWALL_IPV6_ENABLE='0', XWALL_SOCKS_ENABLE='1', XWALL_SOCKS_AUTH='0', XWALL_SOCKS_PORT='1283', PATH=str(Path(args.jq).parent)+':'+os.environ['PATH'])
        subprocess.run(['dash', str(work/'generate.sh')], env=env, check=True)
        generated = json.loads((work/'config.json').read_text())
        def fixtures(value):
            if isinstance(value, dict): return {k:fixtures(v) for k,v in value.items()}
            if isinstance(value, list): return [fixtures(v) for v in value]
            if isinstance(value, str) and value.startswith('geosite:'): return 'domain:ai.test'
            if isinstance(value, str) and value.startswith('geoip:'): return '10.0.0.0/8'
            return value
        for route_only in (False, True):
            config = fixtures(copy.deepcopy(generated))
            for inbound in config['inbounds']:
                inbound['listen'] = '127.0.0.1'
                if inbound['tag'] == 'tproxy-tcp':
                    inbound['settings'].update(address='127.0.0.2', port=24440, followRedirect=False)
                if inbound['tag'] in ('tproxy-tcp', 'tproxy-udp'):
                    inbound['sniffing']['routeOnly'] = route_only
                if inbound['tag'] == 'socks-in':
                    inbound['sniffing'] = copy.deepcopy(config['inbounds'][0]['sniffing'])
            (work/'config.json').write_text(json.dumps(config))
            with (work/'process.log').open('w') as log:
                proc = subprocess.Popen([args.xray,'run','-c',str(work/'config.json')], stdout=log, stderr=log)
                try:
                    for _ in range(100):
                        assert proc.poll() is None, (work/'process.log').read_text()
                        try:
                            with socket.create_connection(('127.0.0.1',1280),timeout=.1): break
                        except OSError: time.sleep(.03)
                    if not route_only:
                        try: tls_probe('*.edge.agora.io')
                        except (OSError, ssl.SSLError): pass
                        else: raise AssertionError('Old destination override unexpectedly worked')
                        print('PASS: old wildcard-SNI destination override reproduces failure')
                        continue
                    assert tls_probe('*.edge.agora.io') == b'direct'
                    assert tls_probe('ai.test') == b'proxy'
                    assert targets[-1] == ('127.0.0.2', 24440), targets
                    query = struct.pack('!6H', 4321, 0x100, 1, 0, 0, 0)+b'\x02ai\x04test\0'+struct.pack('!HH',1,1)
                    with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as dns:
                        dns.settimeout(3); dns.sendto(query,('127.0.0.1',15353)); reply,_=dns.recvfrom(4096)
                    fake_ip = socket.inet_ntoa(reply[-4:])
                    assert ipaddress.ip_address(fake_ip) in ipaddress.ip_network('198.18.0.0/15'), fake_ip
                    assert tls_probe('ai.test',fake_ip) == b'proxy'
                    assert targets[-1] == ('ai.test',24440), targets
                    print('PASS: wildcard SNI keeps real IP; AI domain routes to proxy; FakeIP restores domain')
                finally:
                    proc.terminate(); proc.wait(timeout=5)


if __name__ == '__main__':
    main()
