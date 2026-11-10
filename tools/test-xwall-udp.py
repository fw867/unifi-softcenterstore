#!/usr/bin/env python3
"""Exercise real Xray UDP, node UDP failure, NAT, and DNS listener coexistence.

Run only in a disposable Linux network namespace, never on the router:
    unshare -Urn python3 tools/test-xwall-udp.py --xray /path/to/xray
Requires iproute2, iptables, ipset, conntrack, dash, jq, and util-linux. All proxy,
DNS and echo servers are local fixtures; no production credentials or Internet
access are used. Tests the supplied core version, not arbitrary node services.
"""
import argparse
import importlib.util
import threading
import struct
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--xray',required=True)
parser.add_argument('--script',type=Path,default=ROOT/'apps/xwall/xwall')
args=parser.parse_args()
XRAY=str(Path(args.xray).resolve())
if os.geteuid()!=0:
    raise SystemExit('Run as root inside an isolated network namespace.')
try:
    if os.readlink('/proc/self/ns/net')==os.readlink('/proc/1/ns/net'):
        raise SystemExit('Refusing to modify the host network namespace.')
except PermissionError:
    if Path('/proc/self/uid_map').read_text().split()==['0','0','4294967295']:
        raise SystemExit('Cannot verify network isolation.')
def run(*args, **kw):
    r=subprocess.run(args,text=True,capture_output=True,**kw)
    if r.returncode: raise RuntimeError((args,r.stdout,r.stderr))
    return r.stdout

def func(s,n):
    start=s.index(n+'() {');return s[start:s.index('\n}\n',start)+3]
source=args.script.read_text()
functions='\n'.join(func(source,n) for n in ['trim','add_direct_rule','load_dns_bypass_set','create_ipsetlist','add_bypass_ip','ipts_update','ipts_origin'])
constants='''
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
XWALL_IPV6_ENABLE=0
DNS_HIJACK="${DNS_HIJACK:-1}"
DIRECT_LIST="${DIRECT_LIST:-}"
log_date() { :; }
detect_lan_ip() { echo 192.168.99.1; }
ipt() { iptables "$@"; }
ipt6() { ip6tables "$@"; }
'''
run('ip','link','set','lo','up')
children=[]
try:
    for name,subnet in [('client','192.168.99'),('server','203.0.113')]:
        child=subprocess.Popen(['unshare','-n','sleep','300']);children.append(child)
        for _ in range(100):
            if os.readlink(f'/proc/{child.pid}/ns/net')!=os.readlink('/proc/self/ns/net'): break
            time.sleep(.01)
        ns=['nsenter','-t',str(child.pid),'-n']
        run('ip','link','add',name+'r','type','veth','peer','name',name)
        run('ip','link','set',name,'netns',str(child.pid))
        run('ip','addr','add',subnet+'.1/24','dev',name+'r');run('ip','link','set',name+'r','up')
        run(*ns,'ip','link','set','lo','up');run(*ns,'ip','link','set',name,'up')
        run(*ns,'ip','addr','add',subnet+'.2/24','dev',name)
        run(*ns,'ip','route','add','default','via',subnet+'.1')
    client_ns=['nsenter','-t',str(children[0].pid),'-n']
    server_ns=['nsenter','-t',str(children[1].pid),'-n']
    with tempfile.TemporaryDirectory() as temp:
        work=Path(temp)
        (work/'rules.sh').write_text(constants+functions+'\nipts_origin\ncreate_ipsetlist\nadd_bypass_ip\nipts_update\n')
        run('dash',str(work/'rules.sh'))
        run('iptables','-P','INPUT','DROP')
        run('iptables','-I','INPUT','-i','lo','-j','ACCEPT')
        run('iptables','-I','INPUT','-m','conntrack','--ctstate','ESTABLISHED,RELATED','-j','ACCEPT')
        run(*server_ns,'ip','addr','add','203.0.113.3/24','dev','server')
        run(*server_ns,'ip','addr','add','203.0.113.4/24','dev','server')
        echo_code = """import json,socket,threading,time

def serve(ip,port):
    s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.bind((ip,port))
    while True:
        data,peer=s.recvfrom(65535); s.sendto(json.dumps({'peer':peer,'size':len(data)}).encode(),peer)
for ip,port in [('203.0.113.3',40000),('203.0.113.3',40001),('203.0.113.4',40000)]:
    threading.Thread(target=serve,args=(ip,port),daemon=True).start()
print('ready',flush=True);time.sleep(300)
"""
        echo=subprocess.Popen(server_ns+['python3','-u','-c',echo_code],stdout=subprocess.PIPE,text=True)
        assert echo.stdout.readline().strip()=='ready'
        node_cfg={'log':{'loglevel':'warning'},'inbounds':[{'listen':'203.0.113.2','port':2443,'protocol':'shadowsocks','settings':{'method':'aes-128-gcm','password':'local-fixture-only','network':'tcp,udp'}}],'outbounds':[{'protocol':'freedom','tag':'direct','settings':{'finalRules':[{'action':'allow'}]}}]}
        (work/'node.json').write_text(json.dumps(node_cfg))
        node_log=(work/'node.log').open('w')
        node=subprocess.Popen(server_ns+[XRAY,'run','-c',str(work/'node.json')],stdout=node_log,stderr=node_log)
        time.sleep(.2);assert node.poll() is None,(work/'node.log').read_text()
        user={'outbounds':[{'tag':'proxy','protocol':'shadowsocks','settings':{'servers':[{'address':'203.0.113.2','port':2443,'method':'aes-128-gcm','password':'local-fixture-only'}]}}]}
        (work/'user.json').write_text(json.dumps(user))
        defaults=source[source.index('XWALL_CONFIG="'):source.index('\nlog_date()')]
        generator=defaults+'\n'+func(source,'decode_b64')+'\n'+func(source,'build_xray_config')+'\n'+"""
log_date() { :; }
detect_lan_ip() { echo 192.168.99.1; }
USER_JSON="$WORK/user.json"
XRAY_JSON="$WORK/config.json"
ASSET_DIR="$WORK"
XRAY_LOG="$WORK/xray.log"
DNS_LOCAL_DOMAINS='["domain:lan"]'
DNS_PORT=15353
DNS_CN=127.0.0.3
DNS_FOREIGN=127.0.0.4
build_xray_config
"""
        (work/'generate.sh').write_text(generator)
        def fixtures(value):
            if isinstance(value,dict):return {k:fixtures(v) for k,v in value.items()}
            if isinstance(value,list):return [fixtures(v) for v in value]
            if isinstance(value,str) and value.startswith('geosite:'):return 'domain:fixture.invalid'
            if isinstance(value,str) and value.startswith('geoip:'):return '10.0.0.0/8'
            return value
        probe="""import json,socket
s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.settimeout(.5);s.bind(('192.168.99.2',0))
results=[]
for ip,port in [('203.0.113.3',40000),('203.0.113.3',40001),('203.0.113.4',40000)]:
    s.sendto(b'video-fixture',(ip,port))
    try:
        data,peer=s.recvfrom(2048);results.append({'target':[ip,port],'reply_from':peer,'observed':json.loads(data)})
    except TimeoutError:results.append({'target':[ip,port],'timeout':True})
print(json.dumps({'local':s.getsockname(),'results':results}))
"""
        accept=['iptables','-t','filter','-D','INPUT','-p','udp','-m','mark','--mark','0x08','-m','set','--match-set','xwall_src','src','-m','comment','--comment','xwall-udp-tproxy','-j','ACCEPT']
        try:
            for mode in ('1','4'):
                run('dash',str(work/'generate.sh'),env=dict(os.environ,WORK=str(work),PROXY_MODE=mode,XWALL_IPV6_ENABLE='0'))
                cfg=fixtures(json.loads((work/'config.json').read_text()));cfg['log']['loglevel']='debug'
                (work/'config.json').write_text(json.dumps(cfg))
                with (work/'xray.log').open('w') as log:
                    proc=subprocess.Popen([XRAY,'run','-c',str(work/'config.json')],stdout=log,stderr=log)
                    try:
                        time.sleep(.2);assert proc.poll() is None,(work/'xray.log').read_text()
                        if mode=='1':
                            blocked=json.loads(run(*client_ns,'python3','-c',probe));assert all(r.get('timeout') for r in blocked['results']),blocked
                            print('PASS: strict INPUT fixture drops transparent UDP before explicit admission')
                            allow=accept.copy();allow[3]='-I';run(*allow)
                        result=json.loads(run(*client_ns,'python3','-c',probe));assert all('observed' in r and r['target']==r['reply_from'] for r in result['results']),result
                        print('PASS: generated mode',mode,'TPROXY UDP replies and mappings:',json.dumps(result))
                        if mode=='4':
                            run(*server_ns,'iptables','-I','INPUT','-p','udp','--dport','2443','-j','DROP')
                            blocked=json.loads(run(*client_ns,'python3','-c',probe));assert all(r.get('timeout') for r in blocked['results']),blocked
                            run('python3','-c','import socket; s=socket.create_connection(("203.0.113.2",2443),timeout=1);s.close()')
                            print('PASS: blocking node UDP breaks UDP while node TCP remains reachable')
                            run(*server_ns,'iptables','-D','INPUT','-p','udp','--dport','2443','-j','DROP')
                    finally:proc.terminate();proc.wait()
            # Real NAT DNS ingress alternates normal and source-bypassed clients. Repeating
            # this catches FakeUDP sockets stealing the router's native DNS listener.
            spec=importlib.util.spec_from_file_location('dns_fixture',str(ROOT/'tools/test-xwall-dns.py'))
            dns_fixture=importlib.util.module_from_spec(spec);spec.loader.exec_module(dns_fixture)
            for address in ('127.0.0.1','127.0.0.3','127.0.0.4','192.168.99.1'):
                for network,handler in [(socket.SOCK_DGRAM,dns_fixture.serve_udp),(socket.SOCK_STREAM,dns_fixture.serve_tcp)]:
                    sock=socket.socket(socket.AF_INET,network);sock.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);sock.bind((address,53))
                    if network==socket.SOCK_STREAM:sock.listen(32)
                    threading.Thread(target=handler,args=(sock,),daemon=True).start()
            run(*client_ns,'ip','addr','add','192.168.99.3/24','dev','client')
            dns_probe=r"""import json,socket,struct,os,ipaddress
results=[]
for _ in range(20):
    for address in ('192.168.99.2','192.168.99.3'):
        for network in (socket.SOCK_DGRAM,socket.SOCK_STREAM):
            with socket.socket(socket.AF_INET,network) as s:
                s.bind((address,0));s.settimeout(2);s.connect(('192.168.99.1',53))
                ident=os.urandom(2);q=ident+struct.pack('!5H',0x100,1,0,0,0)+b'\x05hello\x07fixture\x07invalid\0'+struct.pack('!HH',1,1)
                if network==socket.SOCK_DGRAM:
                    s.send(q)
                    try:a=s.recv(4096)
                    except OSError:print('DNS timeout',_,address,network,s.getsockname(),flush=True);raise
                else:
                    s.sendall(struct.pack('!H',len(q))+q);size=struct.unpack('!H',s.recv(2))[0];a=b''
                    while len(a)<size:a+=s.recv(size-len(a))
                assert a[:2]==ident and a[3]&15==0,a.hex()
                results.append({'source':address,'fake':ipaddress.ip_address(a[-4:]) in ipaddress.ip_network('198.18.0.0/15')})
print(json.dumps(results))
"""
            for proto in ('udp','tcp'):
                run('iptables','-I','INPUT','-s','192.168.99.0/24','-p',proto,'--dport','53','-j','ACCEPT')
            for mode in ('1','4'):
                for hijack in ('0','1'):
                    run('conntrack','-F')
                    run('dash',str(work/'rules.sh'),env=dict(os.environ,DIRECT_LIST='192.168.99.2',DNS_HIJACK=hijack))
                    run('dash',str(work/'generate.sh'),env=dict(os.environ,WORK=str(work),PROXY_MODE=mode,XWALL_IPV6_ENABLE='0'))
                    cfg=fixtures(json.loads((work/'config.json').read_text()));(work/'config.json').write_text(json.dumps(cfg))
                    with (work/'xray.log').open('w') as log:
                        proc=subprocess.Popen([XRAY,'run','-c',str(work/'config.json')],stdout=log,stderr=log)
                        try:
                            time.sleep(.2);assert proc.poll() is None,(work/'xray.log').read_text()
                            try:results=json.loads(run(*client_ns,'python3','-c',dns_probe))
                            except Exception:
                                print('Core:',(work/'xray.log').read_text()[-2500:],flush=True);print(run('iptables','-t','nat','-nvxL','XWALL'));print(run('iptables','-t','filter','-nvxL','INPUT'));raise
                            expected=all(r['fake']==(mode=='1' and hijack=='1' and r['source']=='192.168.99.3') for r in results)
                            if not expected:
                                print('CFG DNS',cfg['dns'],flush=True);print(run('iptables','-t','nat','-nvxL','XWALL'));print(run('iptables','-t','nat','-S','PREROUTING'));print(run('ipset','list','xwall_dns_bypass'));print('RESULTS',results,flush=True)
                            assert expected,results
                            sockets=run('ss','-uanp')
                            assert not any('pid='+str(proc.pid)+',' in line and '192.168.99.1:53 ' in line for line in sockets.splitlines()),sockets
                            print('PASS: '+str(len(results))+' real NAT DNS replies, mode='+mode+' hijack='+hijack+', bypass stays real, FakeIP preserved, no router-53 socket conflict')
                        finally:proc.terminate();proc.wait()
            run('iptables','-t','mangle','-D','PREROUTING','-p','udp','-j','XWALL')
            Path('/proc/sys/net/ipv4/ip_forward').write_text('1')
            run('iptables','-t','nat','-A','POSTROUTING','-s','192.168.99.0/24','-o','serverr','-j','MASQUERADE')
            native=json.loads(run(*client_ns,'python3','-c',probe));assert all('observed' in r for r in native['results']),native
            print('PASS: native NAT mappings:',json.dumps(native))
        finally:
            node.terminate();node.wait();node_log.close();echo.terminate();echo.wait()

finally:
    for child in children: child.terminate();child.wait()
