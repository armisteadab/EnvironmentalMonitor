import socket
import csv
import time
import re
from datetime import datetime

# ==========================================
# Sensor list
# Add all your sensor IPs here
# ==========================================


SENSORS = [
    "10.0.0.108",   # upstairs
    "10.0.0.38",   # basement
    "10.0.0.175",   # outside
]


PORT = 13
CSV_FILE = "sensor_log.csv"

# Poll sensors once every 30 minutes
POLL_INTERVAL = 30 * 60

# ==========================================
# Create CSV with header if it doesn't exist
# ==========================================
try:
    with open(CSV_FILE, "x", newline="") as f:
        writer = csv.writer(f)
        writer.writerow([
            "timestamp",
            "device",
            "temp_c",
            "temp_f",
            "humid",
            "ip_of_sensor"
        ])
except FileExistsError:
    pass

# ==========================================
# Regex parser
# Matches:
# DEVICE=BASEMENT TEMP_C=20.0 TEMP_F=68.0 HUMIDITY=63.4%
# TEMP_C=21.5 TEMP_F=70.7 HUMIDITY=58.6%
# ==========================================
#pattern = re.compile(
#    r"(?:DEVICE=[^\s]+\s+)?TEMP_C=([\-\d.]+)\s+TEMP_F=([\-\d.]+)\s+HUMIDITY=([\-\d.]+)%"
#)

pattern = re.compile(
    r"(?:DEVICE=([^\s]+)\s+)?TEMP_C=(-?\d+(?:\.\d+)?)\s+TEMP_F=(-?\d+(?:\.\d+)?)\s+HUMIDITY=(-?\d+(?:\.\d+)?)%"
)

print("Starting sensor logger...")
print()

while True:

    for sensor_ip in SENSORS:

        try:
            # ----------------------------------
            # Connect to sensor
            # ----------------------------------
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sock.settimeout(5)

            sock.connect((sensor_ip, PORT))

            # ----------------------------------
            # Receive response
            # ----------------------------------
            data = sock.recv(1024).decode("utf-8").strip()

            sock.close()

            print(f"{sensor_ip} -> {data}")

            # ----------------------------------
            # Parse sensor data
            # ----------------------------------
            match = pattern.search(data)

            match = pattern.search(data)

            if match:
                devicename = match.group(1)
                temp_c = match.group(2)
                temp_f = match.group(3)
                humidity = match.group(4)


                # ----------------------------------
                # Append to CSV
                # ----------------------------------
                with open(CSV_FILE, "a", newline="") as f:

                    writer = csv.writer(f)

                    writer.writerow([
                        datetime.now().isoformat(),
                        devicename,
                        temp_c,
                        temp_f,
                        humidity,
                        sensor_ip
                    ])

            else:
                print(f"Could not parse response from {sensor_ip}")

        except Exception as e:
            print(f"Error polling {sensor_ip}: {e}")

    # ======================================
    # Wait before polling again
    # ======================================
    time.sleep(POLL_INTERVAL)
