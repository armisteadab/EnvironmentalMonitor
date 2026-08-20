import csv
import json
import os
import re
import socket
import time
from datetime import datetime, timezone

import requests

# ==========================================
# Sensor configuration
# ==========================================
SENSORS = [
    "10.0.0.108",  # upstairs
    "10.0.0.38",   # basement
    "10.0.0.202",  # outside
]

PORT = 13
CSV_FILE = "sensor_log.csv"
POLL_INTERVAL = 30 * 60
SOCKET_TIMEOUT = 5
HTTP_TIMEOUT = 10

# The URL of the deployed EnvironmentalMonitor app's sensor ingest endpoint.
# Defaults to the current Azure App Service deployment, but can be overridden
# with an environment variable so this script works against any environment
# (local dev, staging, a future redeploy, etc.) without editing source.
#
# Linux/macOS:
#   export SENSOR_INGEST_URL='https://<your-app>.azurewebsites.net/api/sensor-ingest'
#
# Windows PowerShell:
#   $env:SENSOR_INGEST_URL='https://<your-app>.azurewebsites.net/api/sensor-ingest'
DEFAULT_INGEST_URL = (
    "https://environmentalmonitor20260715220237-etdsargvdjaueygd"
    ".canadacentral-01.azurewebsites.net/api/sensor-ingest"
)
SENSOR_INGEST_URL = os.environ.get("SENSOR_INGEST_URL", DEFAULT_INGEST_URL)

# Matches:
# DEVICE=BASEMENT TEMP_C=20.0 TEMP_F=68.0 HUMIDITY=63.4%
PATTERN = re.compile(
    r"(?:DEVICE=([^\s]+)\s+)?"
    r"TEMP_C=(-?\d+(?:\.\d+)?)\s+"
    r"TEMP_F=(-?\d+(?:\.\d+)?)\s+"
    r"HUMIDITY=(-?\d+(?:\.\d+)?)%"
)


def ensure_csv_exists() -> None:
    """Create the local CSV backup file and header if it does not already exist."""
    try:
        with open(CSV_FILE, "x", newline="", encoding="utf-8") as file:
            writer = csv.writer(file)
            writer.writerow(
                [
                    "timestamp_utc",
                    "device",
                    "temp_c",
                    "temp_f",
                    "humid",
                    "ip_of_sensor",
                ]
            )
    except FileExistsError:
        pass


def poll_sensor(sensor_ip: str) -> str:
    """Connect to one sensor and return its response string."""
    with socket.create_connection(
        (sensor_ip, PORT), timeout=SOCKET_TIMEOUT
    ) as sock:
        return sock.recv(1024).decode("utf-8").strip()


def parse_sensor_data(data: str) -> tuple[str | None, float, float, float] | None:
    """Parse one sensor response. Return None when it does not match."""
    match = PATTERN.search(data)
    if not match:
        return None

    device_name = match.group(1)
    temp_c = float(match.group(2))
    temp_f = float(match.group(3))
    humidity = float(match.group(4))

    return device_name, temp_c, temp_f, humidity


def append_to_csv(
    timestamp_utc: str,
    device_name: str,
    temp_c: float,
    temp_f: float,
    humidity: float,
    sensor_ip: str,
) -> None:
    """Keep a local CSV backup, in case the app is briefly unreachable."""
    with open(CSV_FILE, "a", newline="", encoding="utf-8") as file:
        writer = csv.writer(file)
        writer.writerow(
            [
                timestamp_utc,
                device_name,
                temp_c,
                temp_f,
                humidity,
                sensor_ip,
            ]
        )


def send_to_app(
    timestamp_utc: str,
    device_name: str,
    temp_c: float,
    temp_f: float,
    humidity: float,
    sensor_ip: str,
) -> None:
    """POST one JSON telemetry reading to the deployed app's ingest endpoint."""
    payload = {
        "recordedUtc": timestamp_utc,
        "deviceId": device_name,
        "temperatureC": temp_c,
        "temperatureF": temp_f,
        "humidity": humidity,
        "sensorIp": sensor_ip,
    }

    response = requests.post(
        SENSOR_INGEST_URL,
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
        timeout=HTTP_TIMEOUT,
    )
    response.raise_for_status()


def main() -> None:
    ensure_csv_exists()

    print("Starting sensor logger...")
    print("Local CSV backup logging is enabled.")
    print(f"Sending readings to: {SENSOR_INGEST_URL}")

    try:
        while True:
            for sensor_ip in SENSORS:
                try:
                    data = poll_sensor(sensor_ip)
                    print(f"{sensor_ip} -> {data}")

                    parsed = parse_sensor_data(data)
                    if parsed is None:
                        print(f"Could not parse response from {sensor_ip}")
                        continue

                    device_name, temp_c, temp_f, humidity = parsed

                    # Fall back to the IP address if DEVICE= is missing.
                    if not device_name:
                        device_name = sensor_ip

                    timestamp_utc = datetime.now(timezone.utc).isoformat()

                    # Always preserve a local copy first.
                    append_to_csv(
                        timestamp_utc,
                        device_name,
                        temp_c,
                        temp_f,
                        humidity,
                        sensor_ip,
                    )
                    print(f"Saved local reading for {device_name}.")

                    # Send to the deployed app without preventing local collection.
                    try:
                        send_to_app(
                            timestamp_utc,
                            device_name,
                            temp_c,
                            temp_f,
                            humidity,
                            sensor_ip,
                        )
                        print(f"Sent reading for {device_name} to the app.")
                    except requests.RequestException as exc:
                        print(f"Failed to send reading for {device_name} to the app: {exc}")

                except (OSError, UnicodeDecodeError) as exc:
                    print(f"Error polling {sensor_ip}: {exc}")
                except Exception as exc:
                    print(f"Unexpected error processing {sensor_ip}: {exc}")

            print(f"Waiting {POLL_INTERVAL // 60} minutes...")
            time.sleep(POLL_INTERVAL)

    except KeyboardInterrupt:
        print("\nStopping sensor logger.")


if __name__ == "__main__":
    main()
