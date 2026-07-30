from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_v06_product_version_and_diagnostics_module_are_declared():
    build_props = (ROOT / "Directory.Build.props").read_text(
        encoding="utf-8"
    )
    solution = (ROOT / "PerfMonitor.slnx").read_text(encoding="utf-8")

    assert "<Version>0.6.0</Version>" in build_props
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
