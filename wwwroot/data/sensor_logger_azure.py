import csv
import json
import os
import re
import socket
import time
from datetime import datetime, timezone

from azure.iot.device import IoTHubDeviceClient, Message

# ==========================================
# Sensor configuration
# ==========================================
SENSORS = [
    "10.0.0.108",  # upstairs
    "10.0.0.38",   # basement
    "10.0.0.175",  # outside
]

PORT = 13
CSV_FILE = "sensor_log.csv"
POLL_INTERVAL = 30 * 60
SOCKET_TIMEOUT = 5

# Store the Azure IoT Hub DEVICE connection string in an environment variable.
# Do not paste the connection string directly into this source file.
#
# Linux/macOS:
#   export IOTHUB_DEVICE_CONNECTION_STRING='HostName=...'
#
# Windows PowerShell:
#   $env:IOTHUB_DEVICE_CONNECTION_STRING='HostName=...'
IOTHUB_CONNECTION_STRING = os.environ.get("IOTHUB_DEVICE_CONNECTION_STRING")

# Matches:
# DEVICE=BASEMENT TEMP_C=20.0 TEMP_F=68.0 HUMIDITY=63.4%
PATTERN = re.compile(
    r"(?:DEVICE=([^\s]+)\s+)?"
    r"TEMP_C=(-?\d+(?:\.\d+)?)\s+"
    r"TEMP_F=(-?\d+(?:\.\d+)?)\s+"
    r"HUMIDITY=(-?\d+(?:\.\d+)?)%"
)


def ensure_csv_exists() -> None:
    """Create the CSV file and header if the file does not already exist."""
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


def create_iot_client() -> IoTHubDeviceClient:
    """Create and connect the Azure IoT Hub device client."""
    if not IOTHUB_CONNECTION_STRING:
        raise RuntimeError(
            "IOTHUB_DEVICE_CONNECTION_STRING environment variable is not set."
        )

    client = IoTHubDeviceClient.create_from_connection_string(
        IOTHUB_CONNECTION_STRING
    )
    client.connect()
    return client


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
    """Keep the existing local CSV backup."""
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


def send_to_azure(
    client: IoTHubDeviceClient,
    timestamp_utc: str,
    device_name: str,
    temp_c: float,
    temp_f: float,
    humidity: float,
    sensor_ip: str,
) -> None:
    """Send one JSON telemetry message to Azure IoT Hub."""
    payload = {
        "recordedUtc": timestamp_utc,
        "deviceId": device_name,
        "temperatureC": temp_c,
        "temperatureF": temp_f,
        "humidity": humidity,
        "sensorIp": sensor_ip,
    }

    message = Message(json.dumps(payload))
    message.content_type = "application/json"
    message.content_encoding = "utf-8"

    # These are optional application properties that can later be used
    # by IoT Hub message routing rules.
    message.custom_properties["deviceId"] = device_name
    message.custom_properties["messageType"] = "sensorReading"

    client.send_message(message)


def main() -> None:
    ensure_csv_exists()

    print("Starting sensor logger...")
    print("Local CSV logging is enabled.")

    try:
        iot_client = create_iot_client()
        print("Connected to Azure IoT Hub.")
    except Exception as exc:
        iot_client = None
        print(f"Azure connection unavailable at startup: {exc}")
        print("Readings will still be written to the local CSV file.")

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

                    # Try Azure without preventing local collection.
                    if iot_client is None:
                        try:
                            iot_client = create_iot_client()
                            print("Reconnected to Azure IoT Hub.")
                        except Exception as exc:
                            print(f"Azure still unavailable: {exc}")
                            continue

                    try:
                        send_to_azure(
                            iot_client,
                            timestamp_utc,
                            device_name,
                            temp_c,
                            temp_f,
                            humidity,
                            sensor_ip,
                        )
                        print(f"Sent Azure telemetry for {device_name}.")
                    except Exception as exc:
                        print(f"Azure send failed for {device_name}: {exc}")

                        try:
                            iot_client.shutdown()
                        except Exception:
                            pass

                        # Force a fresh connection attempt on the next reading.
                        iot_client = None

                except (OSError, UnicodeDecodeError) as exc:
                    print(f"Error polling {sensor_ip}: {exc}")
                except Exception as exc:
                    print(f"Unexpected error processing {sensor_ip}: {exc}")

            print(f"Waiting {POLL_INTERVAL // 60} minutes...")
            time.sleep(POLL_INTERVAL)

    except KeyboardInterrupt:
        print("\nStopping sensor logger.")

    finally:
        if iot_client is not None:
            try:
                iot_client.shutdown()
            except Exception:
                pass


if __name__ == "__main__":
    main()
