import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_vendor_library_is_confined_to_worker_dependency_graph():
    worker_project = (
        ROOT
        / "src"
        / "PerfMonitor.ProviderWorker"
        / "PerfMonitor.ProviderWorker.csproj"
    ).read_text(encoding="utf-8")
    worker_lock = json.loads(
        (
            ROOT
            / "src"
            / "PerfMonitor.ProviderWorker"
            / "packages.lock.json"
        ).read_text(encoding="utf-8")
    )
    agent_lock = json.loads(
        (
            ROOT / "src" / "PerfMonitor.Agent" / "packages.lock.json"
        ).read_text(encoding="utf-8")
    )

    assert (
        'PackageReference Include="LibreHardwareMonitorLib" '
        'Version="0.9.6"'
    ) in worker_project
    target = next(iter(worker_lock["dependencies"].values()))
    dependency = target["LibreHardwareMonitorLib"]
    assert dependency["type"] == "Direct"
    assert dependency["resolved"] == "0.9.6"
    assert dependency["contentHash"]
    for dependencies in agent_lock["dependencies"].values():
        assert "LibreHardwareMonitorLib" not in dependencies

    forbidden_roots = (
        "PerfMonitor.Agent",
        "PerfMonitor.Core",
        "PerfMonitor.Collectors.Windows",
        "PerfMonitor.Collectors.Worker",
    )
    for project_name in forbidden_roots:
        project_dir = ROOT / "src" / project_name
        source = "\n".join(
            path.read_text(encoding="utf-8")
            for path in project_dir.glob("*")
            if path.suffix in {".cs", ".csproj"}
        )
        assert "LibreHardwareMonitorLib" not in source
        assert "LibreHardwareMonitor.Hardware" not in source


def test_worker_is_as_invoker_and_has_no_open_command_surface():
    manifest = (
        ROOT / "src" / "PerfMonitor.ProviderWorker" / "app.manifest"
    ).read_text(encoding="utf-8")
    protocol = (
        ROOT / "src" / "PerfMonitor.ProviderWorker" / "Program.cs"
    ).read_text(encoding="utf-8")
    client = (
        ROOT
        / "src"
        / "PerfMonitor.Collectors.Worker"
        / "ProcessWorkerSession.cs"
    ).read_text(encoding="utf-8")

    assert 'level="asInvoker"' in manifest
    assert "requireAdministrator" not in manifest
    assert 'startInfo.ArgumentList.Add("--stdio")' in client
    assert "UseShellExecute = false" in client
    assert "RedirectStandardInput = true" in client
    assert "StandardInputEncoding" in client
    assert "CollectOperation" in protocol
    for forbidden in (
        "RunCommand",
        "RunPowerShell",
        "ExecuteScript",
        "WriteArbitraryRegistry",
        "Assembly.Load",
        "LoadFrom",
    ):
        assert forbidden not in protocol
        assert forbidden not in client


def test_hardware_contract_uses_max_not_sum_and_celsius():
    metrics = {
        item["metricId"]: item["unit"]
        for item in json.loads(
            (ROOT / "contracts/v1/metric-catalog.json").read_text(
                encoding="utf-8"
            )
        )["metrics"]
    }
    provider_source = (
        ROOT
        / "src"
        / "PerfMonitor.Collectors.Worker"
        / "HardwareWorkerProviders.cs"
    ).read_text(encoding="utf-8")

    assert metrics["system.gpu.load.max.percent"] == "percent"
    assert (
        metrics["system.gpu.temperature.max.celsius"]
        == "celsius"
    )
    assert (
        metrics["system.hardware.temperature.max.celsius"]
        == "celsius"
    )
    assert ".Max(" in provider_source
    assert "loadValues.Sum" not in provider_source
    assert "temperatureValues.Sum" not in provider_source


def test_release_build_packages_and_smokes_fixed_worker():
    build = (ROOT / "scripts" / "build.ps1").read_text(
        encoding="utf-8-sig"
    )
    workflow = (ROOT / ".github/workflows/ci.yml").read_text(
        encoding="utf-8"
    )

    assert "PerfMonitor.ProviderWorker.csproj" in build
    assert "perf-monitor-provider-worker.exe" in build
    assert "provider-worker" in workflow
    assert "smoke_provider_worker.ps1" in workflow
    assert "Hardware Worker handshake missing" in workflow
    assert "Hardware library leaked into Agent root" in workflow
