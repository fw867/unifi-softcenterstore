#!/bin/bash
###############################################################################
# UCG-Max/Fibre Intelligent Fan Controller
# 适配 UniFi SoftCenter：配置来自 config.env，状态写入 fan-controldata
###############################################################################

###[ CONFIGURATION ]###########################################################
# SoftCenter 面板配置（优先）
SC_CONFIG="/data/softcenter/config/fan-control.env"
[ -f "$SC_CONFIG" ] && source "$SC_CONFIG"

# 状态与运行时配置目录（插件名前缀，卸载时可清理）
DATA_DIR="/data/softcenter/bin/fan-controldata"
mkdir -p "$DATA_DIR"

# 脚本自维护的运行时配置（校验修正值会写这里，不回写面板 env）
CONFIG_FILE="${FAN_CONTROL_CONFIG:-$DATA_DIR/config}"
TEMP_STATE_FILE="${TEMP_STATE_FILE:-$DATA_DIR/temp_state}"

# Define default configuration values（面板 env 可覆盖）
DEFAULT_MIN_PWM="${MIN_PWM:-91}"             # Minimum active fan speed (0-255)
DEFAULT_MAX_PWM="${MAX_PWM:-255}"            # Maximum fan speed (0-255)
DEFAULT_MIN_TEMP="${MIN_TEMP:-60}"           # Base threshold (°C)
DEFAULT_MAX_TEMP="${MAX_TEMP:-85}"           # Critical temperature (°C)
DEFAULT_HYSTERESIS="${HYSTERESIS:-5}"        # Temperature buffer (°C)
DEFAULT_CHECK_INTERVAL="${CHECK_INTERVAL:-15}"  # Base check interval (seconds)
DEFAULT_TAPER_MINS="${TAPER_MINS:-90}"       # Cool-down duration (minutes)
DEFAULT_FAN_PWM_DEVICE="${FAN_PWM_DEVICE:-/sys/class/hwmon/hwmon0/pwm1}"
DEFAULT_OPTIMAL_PWM_FILE="${OPTIMAL_PWM_FILE:-$DATA_DIR/optimal_pwm}"
DEFAULT_MAX_PWM_STEP="${MAX_PWM_STEP:-25}"   # Max PWM change per adjustment
DEFAULT_DEADBAND="${DEADBAND:-1}"            # Temp stability threshold (°C)
DEFAULT_ALPHA="${ALPHA:-20}"                 # Smoothing factor (0-100)
DEFAULT_LEARNING_RATE="${LEARNING_RATE:-5}"  # PWM optimization step size

# Create config file if it doesn't exist
if [[ ! -f "$CONFIG_FILE" ]]; then
    logger -t fan-control "CONFIG: Creating new config file"

    # Create directory if it doesn't exist
    config_dir=$(dirname "$CONFIG_FILE")
    if [[ ! -d "$config_dir" ]]; then
        if ! mkdir -p "$config_dir" 2>/dev/null; then
            logger -t fan-control "FATAL: Failed to create config directory: $config_dir"
            exit 1
        fi
    fi

    # Use a temporary file and atomic move to prevent partial writes
    temp_config="${CONFIG_FILE}.tmp"
    if ! cat > "$temp_config" <<-DEFAULTS 2>/dev/null; then
MIN_PWM=$DEFAULT_MIN_PWM             # Minimum active fan speed (0-255)
MAX_PWM=$DEFAULT_MAX_PWM            # Maximum fan speed (0-255)
MIN_TEMP=$DEFAULT_MIN_TEMP            # Base threshold (°C)
MAX_TEMP=$DEFAULT_MAX_TEMP            # Critical temperature (°C)
HYSTERESIS=$DEFAULT_HYSTERESIS           # Temperature buffer (°C)
CHECK_INTERVAL=$DEFAULT_CHECK_INTERVAL      # Base check interval (seconds)
TAPER_MINS=$DEFAULT_TAPER_MINS          # Cool-down duration (minutes)
FAN_PWM_DEVICE="$DEFAULT_FAN_PWM_DEVICE"
OPTIMAL_PWM_FILE="$DEFAULT_OPTIMAL_PWM_FILE"
MAX_PWM_STEP=$DEFAULT_MAX_PWM_STEP        # Max PWM change per adjustment
DEADBAND=$DEFAULT_DEADBAND             # Temp stability threshold (°C)
ALPHA=$DEFAULT_ALPHA               # Smoothing factor (0-100)
LEARNING_RATE=$DEFAULT_LEARNING_RATE        # PWM optimization step size
DEFAULTS
        logger -t fan-control "FATAL: Failed to write to temporary config file"
        exit 1
    elif ! mv "$temp_config" "$CONFIG_FILE" 2>/dev/null; then
        logger -t fan-control "FATAL: Failed to create config file"
        rm -f "$temp_config" 2>/dev/null  # Clean up the temporary file
        exit 1
    else
        logger -t fan-control "CONFIG: New configuration file created successfully"
    fi
fi


# Source the config file
source "$CONFIG_FILE" 2>/dev/null
# SoftCenter env 再次覆盖（保证面板保存的值优先于脚本自动补全）
[ -f "$SC_CONFIG" ] && source "$SC_CONFIG"

# Check if each required parameter is defined, and add missing ones
missing_params=()
missing_values=()
missing_comments=()

check_param() {
    local param=$1
    local default_value=$2
    local comment=$3

    if ! grep -q "^${param}=" "$CONFIG_FILE" 2>/dev/null; then
        logger -t fan-control "CONFIG: Missing parameter detected: $param"
        missing_params+=("$param")
        missing_values+=("$default_value")
        missing_comments+=("$comment")
        # Set the value in the current environment
        eval "${param}=${default_value}"
    fi
}

# Check each parameter
check_param "MIN_PWM" "$DEFAULT_MIN_PWM" "# Minimum active fan speed (0-255)"
check_param "MAX_PWM" "$DEFAULT_MAX_PWM" "# Maximum fan speed (0-255)"
check_param "MIN_TEMP" "$DEFAULT_MIN_TEMP" "# Base threshold (°C)"
check_param "MAX_TEMP" "$DEFAULT_MAX_TEMP" "# Critical temperature (°C)"
check_param "HYSTERESIS" "$DEFAULT_HYSTERESIS" "# Temperature buffer (°C)"
check_param "CHECK_INTERVAL" "$DEFAULT_CHECK_INTERVAL" "# Base check interval (seconds)"
check_param "TAPER_MINS" "$DEFAULT_TAPER_MINS" "# Cool-down duration (minutes)"
check_param "FAN_PWM_DEVICE" "\"$DEFAULT_FAN_PWM_DEVICE\"" "# Fan PWM device path"
check_param "OPTIMAL_PWM_FILE" "\"$DEFAULT_OPTIMAL_PWM_FILE\"" "# Optimal PWM file path"
check_param "MAX_PWM_STEP" "$DEFAULT_MAX_PWM_STEP" "# Max PWM change per adjustment"
check_param "DEADBAND" "$DEFAULT_DEADBAND" "# Temp stability threshold (°C)"
check_param "ALPHA" "$DEFAULT_ALPHA" "# Smoothing factor (0-100)"
check_param "LEARNING_RATE" "$DEFAULT_LEARNING_RATE" "# PWM optimization step size"

# If missing parameters were found, update the config file atomically
if [ ${#missing_params[@]} -gt 0 ]; then
    logger -t fan-control "CONFIG: Updating configuration file with ${#missing_params[@]} missing parameters"

    # Create a temporary file
    temp_config="${CONFIG_FILE}.tmp"

    # Copy existing config to temp file
    if ! cp "$CONFIG_FILE" "$temp_config" 2>/dev/null; then
        logger -t fan-control "ERROR: Failed to create temporary config file for update"
        # Continue with current in-memory values, but don't update the file
    else
        # Add each missing parameter
        update_failed=false
        for i in "${!missing_params[@]}"; do
            if ! echo "${missing_params[$i]}=${missing_values[$i]}        ${missing_comments[$i]}" >> "$temp_config" 2>/dev/null; then
                logger -t fan-control "ERROR: Failed to add parameter ${missing_params[$i]} to config file"
                update_failed=true
                break
            fi
        done

        if [ "$update_failed" = true ]; then
            logger -t fan-control "ERROR: Config file update failed"
            rm -f "$temp_config" 2>/dev/null  # Clean up the temporary file
        else
            # Replace the original file with the updated one
            if ! mv "$temp_config" "$CONFIG_FILE" 2>/dev/null; then
                logger -t fan-control "ERROR: Failed to update config file"
                rm -f "$temp_config" 2>/dev/null  # Clean up the temporary file
            else
                logger -t fan-control "CONFIG: Configuration file updated successfully"
            fi
        fi
    fi
fi

# Validate configuration parameters
validate_config() {
    local param=$1
    local value=$2
    local min=$3
    local max=$4
    local default=$5

    if ! [[ "$value" =~ ^[0-9]+$ ]] || (( value < min || value > max )); then
        logger -t fan-control "CONFIG: Invalid $param value: $value (should be between $min and $max), using default: $default"
        eval "${param}=${default}"
        return 1
    fi
    return 0
}

# Validate numeric parameters
config_changed=false

validate_config "MIN_PWM" "$MIN_PWM" 0 255 "$DEFAULT_MIN_PWM" || config_changed=true
validate_config "MAX_PWM" "$MAX_PWM" "${MIN_PWM:-$DEFAULT_MIN_PWM}" 255 "$DEFAULT_MAX_PWM" || config_changed=true
validate_config "MIN_TEMP" "$MIN_TEMP" 30 80 "$DEFAULT_MIN_TEMP" || config_changed=true
validate_config "MAX_TEMP" "$MAX_TEMP" "$MIN_TEMP" 100 "$DEFAULT_MAX_TEMP" || config_changed=true
validate_config "HYSTERESIS" "$HYSTERESIS" 1 15 "$DEFAULT_HYSTERESIS" || config_changed=true
validate_config "CHECK_INTERVAL" "$CHECK_INTERVAL" 5 60 "$DEFAULT_CHECK_INTERVAL" || config_changed=true
validate_config "TAPER_MINS" "$TAPER_MINS" 1 240 "$DEFAULT_TAPER_MINS" || config_changed=true
validate_config "MAX_PWM_STEP" "$MAX_PWM_STEP" 1 50 "$DEFAULT_MAX_PWM_STEP" || config_changed=true
validate_config "DEADBAND" "$DEADBAND" 0 10 "$DEFAULT_DEADBAND" || config_changed=true
validate_config "ALPHA" "$ALPHA" 1 99 "$DEFAULT_ALPHA" || config_changed=true
validate_config "LEARNING_RATE" "$LEARNING_RATE" 1 20 "$DEFAULT_LEARNING_RATE" || config_changed=true

# If any config values were corrected, update the config file
if [ "$config_changed" = true ]; then
    logger -t fan-control "CONFIG: Updating configuration file with corrected values"

    # Create a temporary file
    temp_config="${CONFIG_FILE}.tmp"

    # Write corrected values to temp file
    if ! cat > "$temp_config" <<-CONFIG 2>/dev/null; then
MIN_PWM=$MIN_PWM             # Minimum active fan speed (0-255)
MAX_PWM=$MAX_PWM            # Maximum fan speed (0-255)
MIN_TEMP=$MIN_TEMP            # Base threshold (°C)
MAX_TEMP=$MAX_TEMP            # Critical temperature (°C)
HYSTERESIS=$HYSTERESIS           # Temperature buffer (°C)
CHECK_INTERVAL=$CHECK_INTERVAL      # Base check interval (seconds)
TAPER_MINS=$TAPER_MINS          # Cool-down duration (minutes)
FAN_PWM_DEVICE="$FAN_PWM_DEVICE"
OPTIMAL_PWM_FILE="$OPTIMAL_PWM_FILE"
MAX_PWM_STEP=$MAX_PWM_STEP        # Max PWM change per adjustment
DEADBAND=$DEADBAND             # Temp stability threshold (°C)
ALPHA=$ALPHA               # Smoothing factor (0-100)
LEARNING_RATE=$LEARNING_RATE        # PWM optimization step size
CONFIG
        logger -t fan-control "ERROR: Failed to write to temporary config file"
        # Continue with current in-memory values, but don't update the file
    elif ! mv "$temp_config" "$CONFIG_FILE" 2>/dev/null; then
        logger -t fan-control "ERROR: Failed to update config file with corrected values"
        rm -f "$temp_config" 2>/dev/null  # Clean up the temporary file
    else
        logger -t fan-control "CONFIG: Configuration file updated with corrected values"
    fi
fi

# Derived values
FAN_ACTIVATION_TEMP=$((MIN_TEMP + HYSTERESIS))
TAPER_DURATION=$((TAPER_MINS * 60))

###[ RUNTIME CHECKS ]##########################################################
# Check for ubnt-systool availability
if ! command -v ubnt-systool >/dev/null 2>&1; then
    logger -t fan-control "FATAL: ubnt-systool command not found"
    exit 1
fi

# Validate hardware access
[[ -w "$FAN_PWM_DEVICE" ]] || {
    logger -t fan-control "FATAL: PWM device $FAN_PWM_DEVICE not writable"
    exit 1
}

# Ensure directories for state files exist
mkdir -p "$(dirname "$TEMP_STATE_FILE")" "$(dirname "$OPTIMAL_PWM_FILE")" || {
    logger -t fan-control "FATAL: Failed to create required directories"
    exit 1
}

# Single instance check
PID_FILE="/var/run/fan-control.pid"

# Check for existing process
if [[ -f "$PID_FILE" ]]; then
    existing_pid=$(cat "$PID_FILE")
    if ps -p "$existing_pid" >/dev/null 2>&1; then
        logger -t fan-control "ALERT: Active service found (PID $existing_pid)"
        exit 1
    else
        logger -t fan-control "CLEANUP: Removing stale PID $existing_pid"
        rm -f "$PID_FILE"
    fi
fi

# Create PID file with atomic lock
(
    flock -x 200
    echo $$ > "$PID_FILE"
    trap 'rm -f "$PID_FILE"; exit' EXIT INT TERM
) 200>"$PID_FILE"

###[ CORE FUNCTIONALITY ]######################################################
# State definitions
STATE_OFF=0        # Fan completely off
STATE_TAPER=1      # Cooling down period before turning off
STATE_ACTIVE=2     # Normal operation with temperature-based fan speed
STATE_EMERGENCY=3  # Critical temperature, maximum fan speed

# Runtime variables
CURRENT_STATE=$STATE_OFF
TAPER_START=0      # Timestamp when taper mode started
LAST_PWM=-1        # Last PWM value set
SMOOTHED_TEMP=50   # Current smoothed temperature
LAST_ADJUSTMENT=0  # Timestamp of last PWM optimization
LAST_AVG_TEMP=0    # Previous temperature (for deadband calculations)
TEMP_READ_FAILURES=0  # Track consecutive temperature reading failures

# Function to safely write to a file using atomic operations
atomic_write_file() {
    local target_file="$1"
    local content="$2"
    local temp_file="${target_file}.tmp"

    if ! echo "$content" > "$temp_file" 2>/dev/null; then
        logger -t fan-control "ERROR: Failed to write to temporary file for $target_file"
        return 1
    elif ! mv "$temp_file" "$target_file" 2>/dev/null; then
        logger -t fan-control "ERROR: Failed to update file $target_file"
        rm -f "$temp_file" 2>/dev/null  # Clean up the temporary file
        return 1
    fi
    return 0
}

# Initialize smoothed temp from state file or raw temp
raw_temp=$(ubnt-systool cputemp | awk '{print int($1)}' || echo 50)
if [[ -f "$TEMP_STATE_FILE" ]]; then
    saved_temp=$(cat "$TEMP_STATE_FILE" 2>/dev/null)
    # Validate saved temperature is a number and within reasonable range
    if [[ "$saved_temp" =~ ^[0-9]+$ ]] && (( saved_temp >= 20 && saved_temp <= 100 )); then
        # Don't use saved temp if it's too far from current raw temp (prevents large jumps)
        if (( ${saved_temp#-} - ${raw_temp#-} < 15 )); then
            SMOOTHED_TEMP=$saved_temp
            logger -t fan-control "INIT: Loaded saved temp=${SMOOTHED_TEMP}°C | Raw=${raw_temp}°C"
        else
            SMOOTHED_TEMP=$raw_temp
            logger -t fan-control "INIT: Discarded saved temp=${saved_temp}°C (too far from raw=${raw_temp}°C)"
        fi
    else
        SMOOTHED_TEMP=$raw_temp
        logger -t fan-control "INIT: Invalid saved temp=${saved_temp}°C, using raw=${raw_temp}°C"
    fi
else
    SMOOTHED_TEMP=$raw_temp
    logger -t fan-control "INIT: No saved temp, using raw=${raw_temp}°C"
fi

get_smoothed_temp() {
    local raw_temp_output=$(ubnt-systool cputemp 2>/dev/null)
    local raw_temp

    # Check if we got valid output
    if [[ -z "$raw_temp_output" ]] || ! [[ "$raw_temp_output" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then
        TEMP_READ_FAILURES=$((TEMP_READ_FAILURES + 1))
        logger -t fan-control "ERROR: Failed to read temperature (attempt $TEMP_READ_FAILURES)"

        # After 3 consecutive failures, take safety measures
        if (( TEMP_READ_FAILURES >= 3 )); then
            logger -t fan-control "ALERT: Multiple temperature read failures - using last known temperature"
            # If in doubt, maintain current fan speed or increase it for safety
            if (( CURRENT_STATE != STATE_EMERGENCY && SMOOTHED_TEMP > MIN_TEMP + 10 )); then
                logger -t fan-control "SAFETY: Activating emergency mode due to sensor failure"
                # Don't change SMOOTHED_TEMP, but act as if in emergency
                if (( CURRENT_STATE != STATE_ACTIVE && CURRENT_STATE != STATE_EMERGENCY )); then
                    CURRENT_STATE=$STATE_ACTIVE
                    set_fan_speed $OPTIMAL_PWM
                fi
            fi
        fi

        # Use last known temperature
        raw_temp=$SMOOTHED_TEMP
    else
        # Reset failure counter on successful read
        TEMP_READ_FAILURES=0
        raw_temp=$(echo "$raw_temp_output" | awk '{print int($1)}')
    fi

    # Ensure we have a valid temperature value
    raw_temp=${raw_temp:-50}
    local previous=$SMOOTHED_TEMP

    # Calculate new smoothed temperature using exponential smoothing formula:
    # smoothed_temp = (α × previous_smooth + (100 - α) × raw_temp) / 100
    # where α (ALPHA) controls how much weight to give to previous vs. new readings
    # Use awk for floating-point arithmetic to prevent drift from integer rounding
    SMOOTHED_TEMP=$(awk "BEGIN { printf \"%.0f\", ($ALPHA * $SMOOTHED_TEMP + (100 - $ALPHA) * $raw_temp) / 100 }")

    # Safety check: If raw and smoothed temps differ by more than 20°C, reset smoothed temp
    local temp_diff=$((raw_temp - SMOOTHED_TEMP))
    if (( ${temp_diff#-} > 20 )); then
        logger -t fan-control "ALERT: Large temp difference detected (${temp_diff}°C) - resetting smoothed temp"
        SMOOTHED_TEMP=$raw_temp
    fi

    # Save smoothed temp to state file (only if it changed significantly)
    if (( ${SMOOTHED_TEMP#-} - ${previous#-} != 0 )); then
        atomic_write_file "$TEMP_STATE_FILE" "$SMOOTHED_TEMP"
    fi

    logger -t fan-control "TEMP:  RAW=${raw_temp}°C | SMOOTH=${SMOOTHED_TEMP}°C | DELTA=$((raw_temp - SMOOTHED_TEMP))°C"
    echo $SMOOTHED_TEMP
}

calculate_speed() {
    local avg_temp=$1
    local temp_range=$((MAX_TEMP - FAN_ACTIVATION_TEMP))
    local temp_diff=$((avg_temp - FAN_ACTIVATION_TEMP))

    # Prevent division by zero
    (( temp_range > 0 )) || temp_range=1

    # Quadratic response curve calculation:
    # PWM = MIN_PWM + (temp_diff²/temp_range²) * (MAX_PWM - MIN_PWM)
    # The formula is multiplied by 20 and divided by 10 to improve integer math precision
    local speed=$(( (temp_diff * temp_diff * (MAX_PWM - MIN_PWM) * 20) / (temp_range * temp_range * 10) ))
    speed=$(( speed + MIN_PWM ))

    # Ensure speed doesn't exceed MAX_PWM
    speed=$(( speed > MAX_PWM ? MAX_PWM : speed ))

    logger -t fan-control "CALC: temp_diff=${temp_diff}°C | range=${temp_range}°C | speed=${speed}pwm"
    echo $speed
}

# Speed control with logging
set_fan_speed() {
    local new_speed=$1
    local current_temp=$(get_smoothed_temp)
    local reason="Normal operation"

    # Emergency override
    if (( current_temp >= MAX_TEMP )); then
        new_speed=$MAX_PWM
        reason="EMERGENCY: Temp ${current_temp}°C ≥ ${MAX_TEMP}°C"
    fi

    # Special handling for OFF state
    if (( CURRENT_STATE == STATE_OFF )); then
        new_speed=0  # Force 0 PWM regardless of other logic
        reason="OFF state override"
    else
        # Apply ramp limits only in non-OFF states
        if (( new_speed > LAST_PWM + MAX_PWM_STEP )); then
            reason="Ramp-up limited: ${LAST_PWM}→$((LAST_PWM + MAX_PWM_STEP))pwm"
            new_speed=$(( LAST_PWM + MAX_PWM_STEP ))
        elif (( new_speed < LAST_PWM - MAX_PWM_STEP )); then
            reason="Ramp-down limited: ${LAST_PWM}→$((LAST_PWM - MAX_PWM_STEP))pwm"
            new_speed=$(( LAST_PWM - MAX_PWM_STEP ))
        fi

        # Enforce MIN/MAX only in active states
        new_speed=$(( new_speed > MAX_PWM ? MAX_PWM : new_speed ))
        new_speed=$(( new_speed < MIN_PWM ? MIN_PWM : new_speed ))
    fi

    if [[ "$new_speed" -ne "$LAST_PWM" ]]; then
        # Note: Due to hardware limitations, the actual PWM value applied may differ from the requested value
        # (e.g., setting 50 might result in ~48, or 100 might result in ~92)
        if ! echo "$new_speed" > "$FAN_PWM_DEVICE" 2>/dev/null; then
            logger -t fan-control "ERROR: Failed to write to PWM device $FAN_PWM_DEVICE"
            # Check if device still exists
            if [[ ! -e "$FAN_PWM_DEVICE" ]]; then
                logger -t fan-control "FATAL: PWM device $FAN_PWM_DEVICE no longer exists"
                # We can't continue without the PWM device, but we'll keep running to monitor temperature
            elif [[ ! -w "$FAN_PWM_DEVICE" ]]; then
                logger -t fan-control "FATAL: PWM device $FAN_PWM_DEVICE is no longer writable"
                # We can't continue without write access, but we'll keep running to monitor temperature
            fi
        else
            logger -t fan-control "SET: ${LAST_PWM}→${new_speed}pwm | Reason: ${reason}"
            LAST_PWM=$new_speed
            LAST_AVG_TEMP=$current_temp  # Reset deadband tracking on change
        fi

        if (( CURRENT_STATE == STATE_ACTIVE )); then
            local now=$(date +%s)
            # More frequent learning for better adaptation (30 minutes instead of 1 hour)
            # Check if it's time to adjust the optimal PWM value (every 30 minutes)
            if (( now - LAST_ADJUSTMENT > 1800 )); then
                local optimal=$(cat "$OPTIMAL_PWM_FILE" 2>/dev/null || echo "$MIN_PWM")
                # Validate optimal PWM value
                if ! [[ "$optimal" =~ ^[0-9]+$ ]] || (( optimal < MIN_PWM || optimal > MAX_PWM )); then
                    logger -t fan-control "WARNING: Invalid optimal PWM value: ${optimal}, using MIN_PWM"
                    optimal=$MIN_PWM
                fi
                local original_optimal=$optimal
                local adjustment=""
                local adaptive_rate=$LEARNING_RATE

                # Calculate temperature change and stability over time
                local temp_delta=$(( current_temp - LAST_AVG_TEMP ))
                local temp_stability=${temp_delta#-}  # Use absolute value of temp_delta

                # Adjust learning rate based on temperature stability
                # More stable temperatures allow for more aggressive learning
                if (( temp_stability < DEADBAND )); then
                    # Temperature is stable, can use higher learning rate
                    adaptive_rate=$(( LEARNING_RATE + 2 ))
                elif (( temp_stability > DEADBAND * 3 )); then
                    # Temperature is fluctuating a lot, use lower learning rate
                    adaptive_rate=$(( LEARNING_RATE - 1 ))
                    adaptive_rate=$(( adaptive_rate < 1 ? 1 : adaptive_rate ))
                fi

                # Enhanced learning logic with more responsive adjustments
                # 1. If we're at optimal speed but temp is rising, increase PWM
                # 2. If we're at optimal speed but temp is stable below MIN_TEMP, decrease PWM
                # 3. If we're above optimal speed but temp is stable, try to decrease PWM
                # 4. If temperature is rising rapidly, make larger adjustments
                if (( new_speed == optimal )); then
                    if (( temp_delta > 0 && current_temp > MIN_TEMP )); then
                        # Temperature rising, increase PWM proactively
                        # Scale adjustment based on how quickly temperature is rising
                        local rise_factor=$(( temp_delta > 2 ? 2 : 1 ))
                        local adj_amount=$(( adaptive_rate * rise_factor ))
                        adjustment="+${adj_amount} (rising temp ${temp_delta}°C)"
                        optimal=$(( optimal + adj_amount ))
                    elif (( current_temp < MIN_TEMP && temp_stability < DEADBAND * 2 )); then
                        # Temperature below threshold and stable, can reduce PWM
                        adjustment="-${adaptive_rate} (stable below threshold)"
                        optimal=$(( optimal - adaptive_rate ))
                    fi
                elif (( new_speed > optimal && temp_stability < DEADBAND && current_temp < MIN_TEMP + HYSTERESIS )); then
                    # We're running faster than optimal but temp is stable and not too high
                    # Try to gradually reduce optimal PWM to find the most efficient setting
                    adjustment="-1 (efficiency optimization)"
                    optimal=$(( optimal - 1 ))
                # If we're below optimal speed but temperature is rising quickly
                elif (( new_speed < optimal && temp_delta > DEADBAND * 2 )); then
                    # Temperature rising quickly while below optimal speed - increase optimal
                    adjustment="+${adaptive_rate} (rapid temp increase ${temp_delta}°C)"
                    optimal=$(( optimal + adaptive_rate ))
                fi

                if [[ -n "$adjustment" ]]; then
                    # Ensure optimal PWM stays within valid range
                    optimal=$(( optimal > MAX_PWM ? MAX_PWM : optimal ))
                    optimal=$(( optimal < MIN_PWM ? MIN_PWM : optimal ))

                    # Use atomic write function to update the optimal PWM file
                    if atomic_write_file "$OPTIMAL_PWM_FILE" "$optimal"; then
                        LAST_ADJUSTMENT=$now
                        logger -t fan-control "LEARNING: ${original_optimal}→${optimal}pwm (${adjustment}) [Rate=${adaptive_rate}]"
                    fi
                fi
            fi
        fi
    fi
}

###[ STATE MANAGEMENT ]########################################################
update_fan_state() {
    local avg_temp=$(get_smoothed_temp)
    local now=$(date +%s)
    local state_transition=""

    # Check for emergency condition first
    if (( avg_temp >= MAX_TEMP )); then
        if (( CURRENT_STATE != STATE_EMERGENCY )); then
            state_transition="→EMERGENCY (${avg_temp}°C ≥ ${MAX_TEMP}°C)"
            CURRENT_STATE=$STATE_EMERGENCY
            set_fan_speed $MAX_PWM
        else
            # Already in emergency state, ensure max fan speed
            set_fan_speed $MAX_PWM
        fi
    else
        # Normal state machine when not in emergency
        case $CURRENT_STATE in
            $STATE_EMERGENCY)
                # Exit emergency mode only when temperature drops significantly below MAX_TEMP
                if (( avg_temp <= MAX_TEMP - HYSTERESIS )); then
                    state_transition="EMERGENCY→ACTIVE (${avg_temp}°C ≤ $((MAX_TEMP - HYSTERESIS))°C)"
                    CURRENT_STATE=$STATE_ACTIVE
                    set_fan_speed $(calculate_speed $avg_temp)
                else
                    # Stay in emergency mode
                    set_fan_speed $MAX_PWM
                fi
                ;;

            $STATE_OFF)
                if (( avg_temp >= FAN_ACTIVATION_TEMP )); then
                    state_transition="OFF→ACTIVE (${avg_temp}°C ≥ ${FAN_ACTIVATION_TEMP}°C)"
                    CURRENT_STATE=$STATE_ACTIVE
                    set_fan_speed $OPTIMAL_PWM
                fi
                ;;

            $STATE_TAPER)
                if (( avg_temp >= FAN_ACTIVATION_TEMP + 2 )); then  # Added 2°C buffer to prevent oscillation
                    state_transition="TAPER→ACTIVE (${avg_temp}°C ≥ $((FAN_ACTIVATION_TEMP + 2))°C)"
                    CURRENT_STATE=$STATE_ACTIVE
                    set_fan_speed $OPTIMAL_PWM
                elif (( now - TAPER_START >= TAPER_DURATION )); then
                    state_transition="TAPER→OFF (${TAPER_MINS}min elapsed)"
                    CURRENT_STATE=$STATE_OFF
                    set_fan_speed 0
                else
                    local remaining=$(( TAPER_DURATION - (now - TAPER_START) ))
                    logger -t fan-control "TAPER: Remaining $((remaining / 60))m | Current: ${avg_temp}°C"
                    set_fan_speed $MIN_PWM
                fi
                ;;

            $STATE_ACTIVE)
                if (( avg_temp <= MIN_TEMP )); then
                    state_transition="ACTIVE→TAPER (${avg_temp}°C ≤ ${MIN_TEMP}°C)"
                    CURRENT_STATE=$STATE_TAPER
                    TAPER_START=$now
                    set_fan_speed $MIN_PWM
                else
                    local temp_delta=$(( avg_temp - LAST_AVG_TEMP ))
                    if (( ${temp_delta#-} > DEADBAND )); then
                        logger -t fan-control "DEADBAND:  DELTA=${temp_delta}°C | THRESHOLD=${DEADBAND}°C"
                        local speed=$(calculate_speed $avg_temp)
                        set_fan_speed $speed
                    else
                        # Force adjustment if we're below target PWM
                        local target_speed=$(calculate_speed $avg_temp)
                        if (( LAST_PWM < target_speed )); then
                            logger -t fan-control "DEADBAND:  Forcing adjustment (current ${LAST_PWM}pwm < target ${target_speed}pwm)"
                            set_fan_speed $target_speed
                        else
                            logger -t fan-control "DEADBAND:  No change | DELTA=${temp_delta}°C"
                        fi
                    fi
                fi
                ;;
        esac
    fi

    [[ -n "$state_transition" ]] && logger -t fan-control "STATE: ${state_transition}"
}

###[ MAIN EXECUTION ]##########################################################
# Initialize optimal PWM file if it doesn't exist
[[ -f "$OPTIMAL_PWM_FILE" ]] || {
    if atomic_write_file "$OPTIMAL_PWM_FILE" "$MIN_PWM"; then
        logger -t fan-control "INIT: Created optimal PWM file with ${MIN_PWM}pwm"
    fi
}

# Read and validate optimal PWM value
OPTIMAL_PWM=$(cat "$OPTIMAL_PWM_FILE" 2>/dev/null || echo "$MIN_PWM")
if ! [[ "$OPTIMAL_PWM" =~ ^[0-9]+$ ]] || (( OPTIMAL_PWM < MIN_PWM || OPTIMAL_PWM > MAX_PWM )); then
    logger -t fan-control "WARNING: Invalid optimal PWM value: ${OPTIMAL_PWM}, using MIN_PWM"
    OPTIMAL_PWM=$MIN_PWM

    # Write corrected value back to file
    if atomic_write_file "$OPTIMAL_PWM_FILE" "$OPTIMAL_PWM"; then
        logger -t fan-control "FIXED: Updated optimal PWM file with corrected value ${OPTIMAL_PWM}pwm"
    fi
fi
logger -t fan-control "START: Optimal=${OPTIMAL_PWM}pwm | Config: MIN=${MIN_TEMP}°C, MAX=${MAX_TEMP}°C, HYST=${HYSTERESIS}°C"

initial_temp=$(get_smoothed_temp)
if (( initial_temp >= FAN_ACTIVATION_TEMP )); then
    logger -t fan-control "COLDSTART: Initial temp ${initial_temp}°C ≥ ${FAN_ACTIVATION_TEMP}°C"
    CURRENT_STATE=$STATE_ACTIVE
    set_fan_speed $OPTIMAL_PWM
else
    logger -t fan-control "COLDSTART: Initial temp ${initial_temp}°C - Fans off"
    set_fan_speed 0
fi

# Define state names for more readable logging
get_state_name() {
    case $1 in
        $STATE_OFF) echo "OFF" ;;
        $STATE_TAPER) echo "TAPER" ;;
        $STATE_ACTIVE) echo "ACTIVE" ;;
        $STATE_EMERGENCY) echo "EMERGENCY" ;;
        *) echo "UNKNOWN" ;;
    esac
}

# Main loop
declare -i loop_counter=0
while true; do
    update_fan_state

    # Log status every 10 iterations
    (( loop_counter++ % 10 == 0 )) && {
        state_name=$(get_state_name $CURRENT_STATE)
        current_temp=$(get_smoothed_temp)
        logger -t fan-control "STATUS: State=${state_name} | PWM=${LAST_PWM} | Temp=${current_temp}°C"
    }

    sleep $CHECK_INTERVAL
done
