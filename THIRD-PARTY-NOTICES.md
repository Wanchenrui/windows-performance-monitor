# Third-party notices

PerfMonitor v1.0 ships third-party runtime binaries only where listed below.
The Agent does not load the hardware library or the Broker service executable.
Build and release tools are listed separately and are not installed by the MSI.

| Distribution | Component | Version | License |
| --- | --- | --- | --- |
| Provider Worker | LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 |
| Provider Worker | BlackSharp.Core | 1.0.7 | MPL-2.0 |
| Provider Worker | DiskInfoToolkit | 1.1.2 | MPL-2.0 |
| Provider Worker | RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 |
| Provider Worker | HidSharp | 2.6.4 | Apache-2.0 |
| Broker | Microsoft.Data.Sqlite.Core | 10.0.10 | MIT |
| Broker | SQLitePCLRaw | 3.0.5 | Apache-2.0 |
| Broker | SQLite native library | 3.53.4 | Public Domain |
| Broker | System.ServiceProcess.ServiceController | 10.0.10 | MIT |
| Agent/Support | Microsoft.Data.Sqlite.Core | 10.0.10 | MIT |
| Agent/Support | SQLitePCLRaw | 3.0.5 | Apache-2.0 |
| Agent/Support | SQLite native library | 3.53.4 | Public Domain |
| All applicable outputs | Microsoft .NET `System.*` support assemblies | lock-file versions | MIT |
| All applicable outputs | Microsoft Windows SDK .NET runtime-pack assemblies | 10.0.17763.57 | MIT |

Build-only dependencies:

| Tool | Version | Terms |
| --- | --- | --- |
| WiX Toolset SDK / NetFx extension | 7.0.0 | WiX license plus OSMF EULA v1.1; explicit acceptance required |
| CycloneDX for .NET | 6.2.0 | Apache-2.0 |
| pip-audit | 2.10.1 | Apache-2.0 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2270 | Microsoft package license terms |

`LibreHardwareMonitorLib` is distributed without modification as NuGet package
version `0.9.6`. Its exact source release and MPL-2.0 license are available at
<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6>.
The Apache-2.0 text for HidSharp is embedded in its NuGet package as
`LICENSE.txt`. Microsoft .NET license information is available at
<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>. The Windows SDK
.NET projection/runtime assemblies are covered by the C#/WinRT MIT license at
<https://github.com/microsoft/CsWinRT/blob/master/LICENSE>.

The complete resolved NuGet graphs, versions, and content hashes are pinned in
the repository's `packages.lock.json` files. SQLite declares its code to be
public domain; SQLitePCLRaw declares Apache-2.0. A release candidate additionally
contains a generated CycloneDX 1.6 SBOM with the exact resolved graph and final
artifact hashes.

WiX 7 is not a silent transitive build choice. Its OSMF EULA must be reviewed
independently, and the `wix7` EULA ID must be supplied explicitly to each MSI
build. See <https://docs.firegiant.com/wix/osmf/>. CycloneDX for .NET licensing
is documented at <https://github.com/CycloneDX/cyclonedx-dotnet>; pip-audit
licensing is documented at <https://github.com/pypa/pip-audit>.

PerfMonitor is not affiliated with LibreHardwareMonitor. Hardware support and
sensor availability depend on the device, driver, operating-system policy, and
current-user permissions.
