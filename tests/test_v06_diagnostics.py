from pathlib import Path
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]


def test_diagnostics_module_remains_declared_after_v06():
    properties = ET.parse(
        ROOT / "Directory.Build.props"
    ).getroot()
    version = properties.find("./PropertyGroup/Version")
    assert version is not None
    product_version = tuple(
        int(part) for part in version.text.split(".")
    )
    solution = (ROOT / "PerfMonitor.slnx").read_text(encoding="utf-8")

    assert product_version >= (0, 6, 0)
    assert "PerfMonitor.Diagnostics.csproj" in solution


def test_v06_ipc_exposes_diagnostics_but_no_execution_request():
    ipc_files = [
        ROOT / "src/PerfMonitor.Ipc.NamedPipes/IpcProtocol.cs",
        ROOT / "src/PerfMonitor.Ipc.NamedPipes/NamedPipeAgentClient.cs",
        ROOT / "src/PerfMonitor.Ipc.NamedPipes/NamedPipeAgentServer.cs",
    ]
    source = "\n".join(
        path.read_text(encoding="utf-8") for path in ipc_files
    )

    assert '"queryDiagnostics"' in source
    for forbidden_type in (
        "action",
        "execute",
        "runCommand",
        "runPowerShell",
        "runScript",
        "writeRegistry",
    ):
        assert f'Type = "{forbidden_type}"' not in source
        assert f'case "{forbidden_type}"' not in source


def test_v06_diagnostic_capability_is_read_only():
    contracts = (
        ROOT / "src/PerfMonitor.Contracts/DiagnosticContracts.cs"
    ).read_text(encoding="utf-8")
    policy = (
        ROOT / "src/PerfMonitor.Diagnostics/DiagnosticPolicy.cs"
    ).read_text(encoding="utf-8")

    assert "ActionsSupported" in contracts
    assert "ActionsSupported = false" in policy
    assert "DiagnosticReplayLimits" in contracts
