from pathlib import Path


def test_powershell_scripts_have_utf8_bom_for_windows_powershell_51():
    scripts_dir = Path(__file__).resolve().parents[1] / "scripts"

    for script_path in scripts_dir.glob("*.ps1"):
        assert script_path.read_bytes().startswith(b"\xef\xbb\xbf"), (
            f"{script_path.name} 缺少 UTF-8 BOM，Windows PowerShell 5.1 "
            "会按系统代码页误解析中文源码"
        )
