from pathlib import Path


def test_powershell_scripts_have_utf8_bom_for_windows_powershell_51():
    scripts_dir = Path(__file__).resolve().parents[1] / "scripts"

    for script_path in scripts_dir.glob("*.ps1"):
        assert script_path.read_bytes().startswith(b"\xef\xbb\xbf"), (
            f"{script_path.name} 缺少 UTF-8 BOM，Windows PowerShell 5.1 "
            "会按系统代码页误解析中文源码"
        )

def test_v05_product_build_does_not_package_python_agent():
    root = Path(__file__).resolve().parents[1]
    build_script = (root / "scripts" / "build.ps1").read_text(
        encoding="utf-8-sig"
    )

    assert "PerfMonitor.Agent.csproj" in build_script
    assert "PerfMonitor.Desktop.csproj" in build_script
    assert "dotnet publish" in build_script
    # v1.0: encrypted endpoint environments must not fan out reusable
    # MSBuild worker processes during the release-gating test pass.
    assert "--disable-build-servers" in build_script
    assert "-maxcpucount:1" in build_script
    assert "PyInstaller" not in build_script
    assert "perf-monitor.spec" not in build_script


def test_default_launcher_is_dotnet_and_http_requires_explicit_flag():
    root = Path(__file__).resolve().parents[1]
    batch = (root / "启动.bat").read_text(encoding="utf-8")
    launcher = (root / "scripts" / "start.ps1").read_text(
        encoding="utf-8-sig"
    )

    assert "--dev-http" in batch
    assert "scripts\\start.ps1" in batch
    assert "app.py" not in batch
    assert "perf-monitor-agent.exe" in launcher
    assert "perf-monitor-desktop.exe" in launcher
    assert "if ($DevHttp)" in launcher
    assert "& $PythonPath app.py" in launcher
