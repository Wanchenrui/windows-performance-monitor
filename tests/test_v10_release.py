import json
import xml.etree.ElementTree as ET
from copy import deepcopy
from pathlib import Path

import jsonschema


ROOT = Path(__file__).resolve().parents[1]
WIX_NS = "http://wixtoolset.org/schemas/v4/wxs"
NETFX_NS = "http://wixtoolset.org/schemas/v4/wxs/netfx"


def test_v10_version_and_release_toolchain_are_pinned():
    properties = ET.parse(ROOT / "Directory.Build.props").getroot()
    version = properties.find("./PropertyGroup/Version")
    assert version is not None
    assert version.text == "1.0.0"

    tools = json.loads(
        (ROOT / ".config" / "dotnet-tools.json").read_text(
            encoding="utf-8"
        )
    )
    assert tools["tools"]["cyclonedx"]["version"] == "6.2.0"
    audit_lock = (ROOT / "requirements-audit.txt").read_text(
        encoding="utf-8-sig"
    )
    assert "pip-audit==2.10.1 \\" in audit_lock
    assert (
        "--hash=sha256:"
        "99ef3f600a317c1945f1e89e227ef26e1c2d618429b8bd3fa6f4f7c440c4611a"
    ) in audit_lock
    for lock_name in (
        "requirements.txt",
        "requirements-dev.txt",
        "requirements-audit.txt",
    ):
        lock_text = (ROOT / lock_name).read_text(
            encoding="utf-8-sig"
        )
        assert "--hash=sha256:" in lock_text

    setup_script = (ROOT / "scripts" / "setup.ps1").read_text(
        encoding="utf-8-sig"
    )
    assert "--require-hashes" in setup_script
    assert "--only-binary=:all:" in setup_script
    assert "Windows x64 Python 3.12" in setup_script


def test_installer_freezes_core_optional_broker_and_prerequisites():
    package_path = (
        ROOT / "installer" / "PerfMonitor.Installer" / "Package.wxs"
    )
    root = ET.parse(package_path).getroot()
    package = root.find(f"{{{WIX_NS}}}Package")
    assert package is not None
    assert package.attrib["Scope"] == "perMachine"
    assert package.attrib["UpgradeCode"] == (
        "97E0D462-D2F8-43A5-B56F-06152D4D502D"
    )

    features = {
        item.attrib["Id"]: item
        for item in package.findall(f"{{{WIX_NS}}}Feature")
    }
    assert features["Core"].attrib["Level"] == "1"
    assert features["Core"].attrib["AllowAbsent"] == "no"
    assert features["Broker"].attrib["Level"] == "101"
    assert features["Broker"].attrib["AllowAbsent"] == "yes"

    service = features["Broker"].find(
        f".//{{{WIX_NS}}}ServiceInstall"
    )
    assert service is not None
    assert service.attrib["Name"] == "PerfMonitorBroker"
    assert service.attrib["Account"] == "LocalSystem"
    assert service.attrib["Arguments"] == "--service"
    assert service.attrib["Start"] == "auto"

    permission = features["Broker"].find(
        f".//{{{WIX_NS}}}PermissionEx"
    )
    assert permission is not None
    assert permission.attrib["Sddl"] == (
        "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"
    )

    shortcut_ids = {
        shortcut.attrib["Id"]
        for shortcut in features["Core"].findall(
            f".//{{{WIX_NS}}}Shortcut"
        )
    }
    assert shortcut_ids == {
        "AgentStartupShortcut",
        "DesktopStartMenuShortcut",
    }

    runtime_check = package.find(
        f"{{{NETFX_NS}}}DotNetCompatibilityCheck"
    )
    assert runtime_check is not None
    assert runtime_check.attrib == {
        "Id": "CheckDotNetDesktopRuntime",
        "Property": "DOTNET_DESKTOP_RUNTIME_CHECK",
        "Version": "10.0.0",
        "RuntimeType": "desktop",
        "RollForward": "minor",
        "Platform": "x64",
    }
    launch_conditions = [
        launch.attrib["Condition"]
        for launch in package.findall(f"{{{WIX_NS}}}Launch")
    ]
    assert any(
        "DOTNET_DESKTOP_RUNTIME_CHECK = 0" in condition
        for condition in launch_conditions
    )
    assert any(
        "WINDOWS_CURRENT_BUILD >= 26100" in condition
        for condition in launch_conditions
    )
    assert any(
        'WINDOWS_INSTALLATION_TYPE = "Server"' in condition
        for condition in launch_conditions
    )


def test_wix_eula_is_never_accepted_implicitly():
    project_path = (
        ROOT
        / "installer"
        / "PerfMonitor.Installer"
        / "PerfMonitor.Installer.wixproj"
    )
    project = ET.parse(project_path).getroot()
    assert project.find(".//AcceptEula") is None

    script = (ROOT / "scripts" / "build_installer.ps1").read_text(
        encoding="utf-8-sig"
    )
    assert '$WixEulaId -cne "wix7"' in script
    assert "-p:AcceptEula=$WixEulaId" in script
    assert "production_publisher_not_configured" in script
    assert project.find(".//SuppressIces") is None

    workflow = (
        ROOT / ".github" / "workflows" / "release.yml"
    ).read_text(encoding="utf-8")
    assert "workflow_dispatch:" in workflow
    assert "wix_eula_id:" in workflow
    assert 'WIX_EULA_ID: ${{ inputs.wix_eula_id }}' in workflow
    assert "actions/attest@v4" in workflow
    assert "PERFMONITOR_SIGNING_PFX_BASE64" in workflow
    assert "production_publisher_not_configured" in (
        ROOT / "scripts" / "build.ps1"
    ).read_text(encoding="utf-8-sig")
    build_script = (
        ROOT / "scripts" / "build.ps1"
    ).read_text(encoding="utf-8-sig")
    assert '$Publisher.Length -gt 255' in build_script
    assert '$Publisher -match "[;`r`n]"' in build_script
    test_certificate = (
        ROOT / "scripts" / "new_test_certificate.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "$CertificateClock = Get-Date" in test_certificate
    assert "[DateTime]::UtcNow" not in test_certificate


def test_initial_vulnerability_waiver_file_is_empty_and_valid():
    schema = json.loads(
        (
            ROOT
            / "contracts"
            / "v1"
            / "vulnerability-waivers-v1.schema.json"
        ).read_text(encoding="utf-8")
    )
    document = json.loads(
        (
            ROOT
            / "release"
            / "vulnerability-waivers-v1.json"
        ).read_text(encoding="utf-8")
    )
    jsonschema.Draft202012Validator(
        schema,
        format_checker=jsonschema.FormatChecker(),
    ).validate(document)
    assert document["waivers"] == []


def test_release_scripts_are_fail_closed_and_include_final_files():
    vulnerability_script = (
        ROOT / "scripts" / "test_vulnerabilities.ps1"
    ).read_text(encoding="utf-8-sig")
    sbom_script = (ROOT / "scripts" / "new_sbom.ps1").read_text(
        encoding="utf-8-sig"
    )
    crash_script = (
        ROOT / "scripts" / "test_crash_recovery.ps1"
    ).read_text(encoding="utf-8-sig")

    assert "nuget_vulnerability_report_incomplete" in (
        vulnerability_script
    )
    assert "python_vulnerability_exit_report_mismatch" in (
        vulnerability_script
    )
    assert "vulnerability_gate_failed" in vulnerability_script
    assert "[switch]$ReleaseScope" in vulnerability_script
    assert "Get-PerfMonitorDependencyInputHashes" in (
        vulnerability_script
    )
    assert 'type = "file"' in sbom_script
    assert "perfmonitor:path" in sbom_script
    assert "Get-LogicalRequirementLines" in sbom_script
    assert "WheelSha256" in sbom_script
    assert '"--hash=sha256:' in sbom_script
    assert "walObservedBeforeTermination" in crash_script
    assert "integrityPassedAfterRestart" in crash_script

    budgets = json.loads(
        (
            ROOT / "release" / "resource-budgets-v1.json"
        ).read_text(encoding="utf-8")
    )
    assert budgets["agent"]["handlePeak"] == 768
    assert budgets["agent"]["retainedHandleGrowth"] == 64
    soak_script = (
        ROOT / "scripts" / "run_agent_soak.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "RetainedHandleGrowth" in soak_script
    release_manifest_script = (
        ROOT / "scripts" / "new_release_manifest.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "InstallerContractEvidencePath" in release_manifest_script
    assert "InstallerLifecycleEvidencePath" in release_manifest_script
    assert "CrashRecoveryEvidencePath" in release_manifest_script
    assert "ResourceEvidencePath" in release_manifest_script
    assert (
        "production_release_requires_baseline_72h_evidence"
        in release_manifest_script
    )
    assert (
        "production_release_requires_support_matrix_evidence"
        in release_manifest_script
    )
    assert "test_support_matrix_evidence.ps1" in (
        release_manifest_script
    )
    assert "release_vulnerability_input_mismatch" in (
        release_manifest_script
    )
    release_common = (
        ROOT / "scripts" / "release_common.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "Assert-PerfMonitorMsiFile" in release_common
    assert "installer_file_header_invalid" in release_common
    update_manifest_script = (
        ROOT / "scripts" / "new_update_manifest.ps1"
    ).read_text(encoding="utf-8-sig")
    update_validation_script = (
        ROOT / "scripts" / "test_update_manifest.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "2.16.840.1.101.3.4.2.1" in update_manifest_script
    assert (
        "update_manifest_digest_algorithm_invalid"
        in update_validation_script
    )


def test_support_matrix_is_attested_and_production_bound():
    matrix_workflow = (
        ROOT / ".github" / "workflows" / "support-matrix.yml"
    ).read_text(encoding="utf-8")
    release_workflow = (
        ROOT / ".github" / "workflows" / "release.yml"
    ).read_text(encoding="utf-8")

    for target in (
        "windows-11-24h2",
        "windows-11-25h2",
        "windows-server-2022-desktop",
        "windows-server-2025-desktop",
    ):
        assert f"targetId: {target}" in matrix_workflow
    assert "Fail before mutation on the wrong host" in matrix_workflow
    assert "actions/attest@v4" in matrix_workflow
    assert "support-matrix-evidence-v1.json" in matrix_workflow
    assert "support_matrix_run_id:" in release_workflow
    assert "gh attestation verify" in release_workflow
    assert "--signer-workflow" in release_workflow
    assert "--source-digest $env:GITHUB_SHA" in release_workflow

    lifecycle_script = (
        ROOT / "scripts" / "test_installer_lifecycle.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "installer_lifecycle_preexisting_product" in lifecycle_script
    assert '"smoke.ps1"' in lifecycle_script
    assert '"/fa"' in lifecycle_script
    assert "TimeStamperCertificate" in lifecycle_script


def test_clean_ci_runner_creates_crash_evidence_directory():
    workflow = (
        ROOT / ".github" / "workflows" / "ci.yml"
    ).read_text(encoding="utf-8")
    crash_step = workflow.split(
        "- name: Force WAL crash and verify recovery",
        maxsplit=1,
    )[1].split("- name:", maxsplit=1)[0]
    assert "New-Item `" in crash_step
    assert "-Path .\\artifacts `" in crash_step
    assert "crash-recovery.json" in crash_step


def test_support_matrix_schema_accepts_only_the_frozen_four_targets():
    schema = json.loads(
        (
            ROOT
            / "contracts"
            / "v1"
            / "support-matrix-evidence-v1.schema.json"
        ).read_text(encoding="utf-8")
    )

    def target(
        target_id: str,
        display_version: str,
        build: str,
        installation_type: str,
    ) -> dict:
        return {
            "targetId": target_id,
            "testedAtUtc": "2026-07-30T02:00:00Z",
            "host": {
                "productName": "Windows",
                "displayVersion": display_version,
                "currentBuildNumber": build,
                "ubr": 1,
                "installationType": installation_type,
                "editionId": "Professional",
                "osArchitecture": "X64",
            },
            "entryEvidence": {
                "sha256": "A" * 64,
                "sizeBytes": 100,
            },
            "lifecycleEvidence": {
                "sha256": "B" * 64,
                "sizeBytes": 100,
                "startedAtUtc": "2026-07-30T01:00:00Z",
                "completedAtUtc": "2026-07-30T01:30:00Z",
                "currentMsiSha256": "C" * 64,
                "previousMsiSha256": "D" * 64,
                "passed": True,
            },
            "passed": True,
        }

    evidence = {
        "schemaVersion": "1.0",
        "productVersion": "1.0.0",
        "gitCommit": "a" * 40,
        "runtimeIdentifier": "win-x64",
        "generatedAtUtc": "2026-07-30T02:01:00Z",
        "sourceMsiSha256": "C" * 64,
        "previousMsiSha256": "D" * 64,
        "signatureMode": "test",
        "signerSubject": "CN=PerfMonitor CI Test",
        "signerCertificateSha256": "E" * 64,
        "sourceWorkflow": {
            "repository": "owner/repository",
            "runId": "123",
            "runAttempt": 1,
            "workflowFile": ".github/workflows/support-matrix.yml",
        },
        "targets": [
            target("windows-11-24h2", "24H2", "26100", "Client"),
            target("windows-11-25h2", "25H2", "26200", "Client"),
            target(
                "windows-server-2022-desktop",
                "21H2",
                "20348",
                "Server",
            ),
            target(
                "windows-server-2025-desktop",
                "24H2",
                "26100",
                "Server",
            ),
        ],
        "passed": True,
    }
    validator = jsonschema.Draft202012Validator(
        schema,
        format_checker=jsonschema.FormatChecker(),
    )
    validator.validate(evidence)

    wrong_host = deepcopy(evidence)
    wrong_host["targets"][0]["host"]["currentBuildNumber"] = "19045"
    assert list(validator.iter_errors(wrong_host))

    missing_target = deepcopy(evidence)
    missing_target["targets"].pop()
    assert list(validator.iter_errors(missing_target))
