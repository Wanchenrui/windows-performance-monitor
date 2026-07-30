# PerfMonitor Provider Worker IPC v1

This protocol is private to the Agent/Worker process boundary. It is versioned
independently from the public snapshot contract so a malformed or incompatible
Worker cannot enter the Agent trust boundary.

## Transport

- anonymous redirected standard input/output pipes;
- one request followed by one response, strictly serialized;
- 4-byte unsigned little-endian payload length;
- UTF-8 JSON payload;
- maximum payload length: 1,048,576 bytes;
- EOF, truncated prefixes/payloads, invalid UTF-8/JSON, and oversized messages
  terminate the Worker session.

Standard output MUST contain frames only. Diagnostics use standard error and
are retained by the Agent with bounded line and character counts.

## Request

```json
{
  "protocolVersion": "1.0",
  "requestId": "32-lowercase-hex-characters",
  "operation": "collect"
}
```

No other operation or request property changes Worker behavior. In
particular, the protocol has no command, script, library path, device path,
plugin, registry, or privilege field.

## Response

```json
{
  "protocolVersion": "1.0",
  "requestId": "the-request-id",
  "workerInstanceId": "32-lowercase-hex-characters",
  "sequence": 1,
  "observedAtUtc": "2026-07-30T04:00:00.0000000+00:00",
  "status": "available",
  "coverage": {
    "enumerated": 2,
    "readable": 2,
    "skipped": 0
  },
  "devices": [
    {
      "deviceId": "gpu-nvidia-0",
      "displayName": "Example GPU",
      "hardwareType": "gpu-nvidia",
      "sensors": [
        {
          "sensorId": "load-0",
          "displayName": "GPU Core",
          "sensorType": "load",
          "value": 12.5
        },
        {
          "sensorId": "temperature-0",
          "displayName": "GPU Core",
          "sensorType": "temperature",
          "value": 48.0
        }
      ]
    }
  ],
  "errors": []
}
```

`status` is one of `available`, `partial`, `not_supported`,
`permission_denied`, or `error`. Error codes use the stable public error-code
vocabulary. Device and sensor IDs are opaque within one Worker instance and
are not persisted.

## Bounds validated by the Agent

- no more than 128 devices;
- no more than 128 sensors per device;
- IDs: 1–64 lowercase ASCII letters, digits, `.`, `_`, or `-`;
- display names: 1–128 characters without control characters;
- unique device IDs and unique sensor IDs within each device;
- finite load values in `[0, 100]`;
- finite temperature values in `[-100, 250]`;
- non-negative coverage counts with
  `readable + skipped <= enumerated`;
- matching protocol version and request ID;
- monotonically increasing sequence for one `workerInstanceId`.
