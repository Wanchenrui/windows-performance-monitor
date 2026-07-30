import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_v07_product_and_stable_provider_groups_are_declared():
    build_props = (ROOT / "Directory.Build.props").read_text(
        encoding="utf-8"
    )
    catalog = json.loads(
        (ROOT / "contracts/v1/provider-catalog.json").read_text(
            encoding="utf-8"
        )
    )

    assert "<Version>0.7.1</Version>" in build_props
    providers = {
        (entry["groupId"], entry["providerId"])
        for entry in catalog["providers"]
    }
    assert (
        "network",
        "windows.network.interface-statistics.v1",
    ) in providers
    assert (
        "diskIo",
        "windows.disk-io.pdh-physical-disk-total.v1",
    ) in providers
    assert (
        "power",
        "windows.power.get-system-power-status.v1",
    ) in providers


def test_v07_rate_units_and_metrics_are_language_independent():
    catalog = json.loads(
        (ROOT / "contracts/v1/metric-catalog.json").read_text(
            encoding="utf-8"
        )
    )
    schema = json.loads(
        (ROOT / "contracts/v1/snapshot-v1.schema.json").read_text(
            encoding="utf-8"
        )
    )
    metrics = {
        item["metricId"]: item["unit"]
        for item in catalog["metrics"]
    }

    assert (
        metrics["system.network.receive.bytes_per_second"]
        == "byte_per_second"
    )
    assert (
        metrics["system.disk.read.operations_per_second"]
        == "count_per_second"
    )
    assert (
        metrics["system.power.battery.charge.percent"]
        == "percent"
    )
    units = schema["$defs"]["metric"]["properties"]["unit"][
        "enum"
    ]
    assert "byte_per_second" in units
    assert "count_per_second" in units


def test_v07_collectors_do_not_shell_out_or_use_wmi():
    collector_dir = ROOT / "src/PerfMonitor.Collectors.Windows"
    source = "\n".join(
        path.read_text(encoding="utf-8")
        for path in collector_dir.glob("*.cs")
    ).lower()

    for forbidden in (
        "powershell",
        "cmd.exe",
        "process.start(",
        "managementobject",
        "system.management",
        "win32_perfformatteddata",
    ):
        assert forbidden not in source
    assert "pdhaddenglishcounter" in source
    assert "getsystempowerstatus" in source
    assert "getipstatistics" in source
