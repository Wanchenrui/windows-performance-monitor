import json
from pathlib import Path
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]


def _project_source(name):
    project = ROOT / "src" / name
    return "\n".join(
        path.read_text(encoding="utf-8")
        for path in project.glob("*")
        if path.suffix in {".cs", ".csproj", ".manifest"}
    )


def test_v072_version_and_broker_are_packaged_separately():
    properties = ET.parse(
        ROOT / "Directory.Build.props"
    ).getroot()
    version = properties.find("./PropertyGroup/Version")
    assert version is not None
    product_version = tuple(
        int(part) for part in version.text.split(".")
    )
    build = (ROOT / "scripts/build.ps1").read_text(
        encoding="utf-8-sig"
    )
    workflow = (ROOT / ".github/workflows/ci.yml").read_text(
        encoding="utf-8"
    )

    assert product_version >= (0, 7, 2)
    assert "PerfMonitor.Broker.csproj" in build
    assert "perf-monitor-broker.exe" in build
    assert "smoke_broker.ps1" in workflow
    assert "dist/broker/**" in workflow


def test_broker_has_only_closed_typed_action_surface():
    protocol = _project_source("PerfMonitor.Broker.Protocol")
    contracts = _project_source("PerfMonitor.Contracts")
    executor = _project_source("PerfMonitor.Broker")
    action_schema = json.loads(
        (ROOT / "contracts/v1/actions-v1.schema.json").read_text(
            encoding="utf-8"
        )
    )

    for action_type in (
        "set_process_priority",
        "terminate_process",
        "start_approved_diagnostic",
        "apply_approved_power_profile",
    ):
        assert action_type in contracts

    for forbidden in (
        "RunCommand",
        "RunPowerShell",
        "ExecuteScript",
        "WriteArbitraryRegistry",
        "cmd.exe",
        "powershell.exe",
        "Process.Start(",
    ):
        assert forbidden not in protocol
        assert forbidden not in executor

    request = action_schema["$defs"]["userActionRequest"]
    assert request["additionalProperties"] is False
    for definition in (
        "processTarget",
        "setProcessPriority",
        "startApprovedDiagnostic",
        "applyApprovedPowerProfile",
    ):
        assert (
            action_schema["$defs"][definition]["additionalProperties"]
            is False
        )


def test_broker_identity_audit_and_storage_are_separate():
    resolver = _project_source("PerfMonitor.Broker")
    broker_project = (
        ROOT / "src/PerfMonitor.Broker/PerfMonitor.Broker.csproj"
    ).read_text(encoding="utf-8")
    broker_lock = json.loads(
        (
            ROOT / "src/PerfMonitor.Broker/packages.lock.json"
        ).read_text(encoding="utf-8")
    )
    agent_lock = json.loads(
        (
            ROOT / "src/PerfMonitor.Agent/packages.lock.json"
        ).read_text(encoding="utf-8")
    )

    assert "GetNamedPipeClientProcessId" in resolver
    assert "RunAsClient" in resolver
    assert "WindowsIdentity.GetCurrent" in resolver
    assert "broker-v1.db" in resolver
    assert "history-v1.db" not in resolver
    assert "Microsoft.Data.Sqlite.Core" in broker_project
    for dependencies in agent_lock["dependencies"].values():
        assert (
            "System.ServiceProcess.ServiceController"
            not in dependencies
        )
        assert "perfmonitor.broker" not in dependencies
    assert "PerfMonitor.Storage.Sqlite" not in broker_project
    target = next(iter(broker_lock["dependencies"].values()))
    assert target["Microsoft.Data.Sqlite.Core"]["resolved"] == "10.0.10"
    assert (
        target["System.ServiceProcess.ServiceController"]["resolved"]
        == "10.0.10"
    )


def test_broker_is_as_invoker_and_console_is_forced_dry_run():
    manifest = (
        ROOT / "src/PerfMonitor.Broker/app.manifest"
    ).read_text(encoding="utf-8")
    runner = (
        ROOT / "src/PerfMonitor.Broker/BrokerRunner.cs"
    ).read_text(encoding="utf-8")
    options = (
        ROOT / "src/PerfMonitor.Broker/BrokerOptions.cs"
    ).read_text(encoding="utf-8")

    assert 'level="asInvoker"' in manifest
    assert "requireAdministrator" not in manifest
    assert "options.Mode == BrokerRunMode.Console" in runner
    assert "forceDryRunOnly" in runner
    assert "Service mode uses fixed machine paths" in options


def test_optional_broker_probe_never_delays_telemetry_start():
    agent_runner = (
        ROOT / "src/PerfMonitor.Agent/AgentServiceRunner.cs"
    ).read_text(encoding="utf-8")
    core_project = (
        ROOT / "src/PerfMonitor.Core/PerfMonitor.Core.csproj"
    ).read_text(encoding="utf-8")
    collector_project = (
        ROOT
        / "src/PerfMonitor.Collectors.Windows"
        / "PerfMonitor.Collectors.Windows.csproj"
    ).read_text(encoding="utf-8")

    assert "_ = actionGateway.ProbeAsync(cancellationToken);" in (
        agent_runner
    )
    assert "await actionGateway.ProbeAsync" not in agent_runner
    assert "PerfMonitor.Broker" not in core_project
    assert "PerfMonitor.Broker" not in collector_project
