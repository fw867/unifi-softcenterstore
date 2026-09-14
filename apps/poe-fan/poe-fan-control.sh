#!/bin/bash
# UniFi UCG-Fiber PoE 风扇控制
# 项目: https://github.com/Autonomous120/unifi-ucg-fiber-poe-fan-control
# 配置: /data/softcenter/config/poe-fan.env
# 状态: /data/softcenter/bin/poe-fandata/

CONFIG="/data/softcenter/config/poe-fan.env"
[ -f "$CONFIG" ] && source "$CONFIG"

DEVICE_ID="${DEVICE_ID:-}"
API_KEY="${API_KEY:-}"
PORT_IDX="${PORT_IDX:-4}"
HIGH_TEMP="${HIGH_TEMP:-50}"
LOW_TEMP="${LOW_TEMP:-43}"
PON_IFACE="${PON_IFACE:-eth6}"

DATA_DIR="/data/softcenter/bin/poe-fandata"
mkdir -p "$DATA_DIR"
STATE_FILE="$DATA_DIR/poe_temp_state"
LOG="/var/log/poe-fan.log"

log_date() {
    line="【$(date '+%Y年%m月%d日 %H:%M:%S')】: $1"
    echo "$line"
    echo "$line" >> "$LOG"
}

if [ -z "$DEVICE_ID" ] || [ -z "$API_KEY" ]; then
    log_date "错误：未配置 DEVICE_ID 或 API_KEY，请在软件中心填写"
    exit 1
fi

TEMP=$(ethtool -m "$PON_IFACE" 2>/dev/null | grep "Module temperature" | head -n1 | awk '{print $4}')

if [ -z "$TEMP" ]; then
    log_date "WARN 无法从 $PON_IFACE 获取 PON 温度"
    exit 1
fi

if [ -f "$STATE_FILE" ]; then
    LAST_STATE=$(cat "$STATE_FILE")
else
    LAST_STATE="unknown"
fi

set_poe_mode() {
    MODE=$1
    curl -sk -X PUT \
        "https://127.0.0.1/proxy/network/api/s/default/rest/device/$DEVICE_ID" \
        -H "X-API-KEY: $API_KEY" \
        -H "Accept: application/json" \
        --data "{
          \"port_overrides\": [
            { \"port_idx\": $PORT_IDX, \"poe_mode\": \"$MODE\" }
          ]
        }" >/dev/null
    echo "$MODE" > "$STATE_FILE"
    log_date "温度=${TEMP}°C, 设置 PoE 端口${PORT_IDX}=$MODE"
}

if awk "BEGIN{exit !($TEMP >= $HIGH_TEMP)}"; then
    if [ "$LAST_STATE" != "auto" ]; then
        set_poe_mode "auto"
    fi
elif awk "BEGIN{exit !($TEMP <= $LOW_TEMP)}"; then
    if [ "$LAST_STATE" != "off" ]; then
        set_poe_mode "off"
    fi
else
    log_date "温度=${TEMP}°C, 状态保持=$LAST_STATE"
fi
