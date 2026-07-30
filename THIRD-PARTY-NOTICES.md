# Third-party notices

PerfMonitor v0.7.2 ships third-party binaries only in the isolated hardware
Worker or optional Broker distributions. The Agent does not load the hardware
library or the Broker service executable.

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
| All applicable outputs | Microsoft .NET `System.*` support assemblies | lock-file versions | MIT |
| All applicable outputs | Microsoft Windows SDK .NET runtime-pack assemblies | 10.0.17763.57 | MIT |

`LibreHardwareMonitorLib` is distributed without modification as NuGet package
version `0.9.6`. Its exact source release and MPL-2.0 license are available at
<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6>.
The Apache-2.0 text for HidSharp is embedded in its NuGet package as
`LICENSE.txt`. Microsoft .NET license information is available at
<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>. The Windows SDK
.NET projection/runtime assemblies are covered by the C#/WinRT MIT license at
<https://github.com/microsoft/CsWinRT/blob/master/LICENSE>.

The complete resolved NuGet graphs, versions, and content hashes are pinned in
`src/PerfMonitor.ProviderWorker/packages.lock.json` and
`src/PerfMonitor.Broker/packages.lock.json`. SQLite declares its code to be
public domain; SQLitePCLRaw declares Apache-2.0. The v1.0 release process will
add a generated SBOM and bundled license texts; this notice does not claim that
the v1.0 release evidence already exists.

PerfMonitor is not affiliated with LibreHardwareMonitor. Hardware support and
sensor availability depend on the device, driver, operating-system policy, and
current-user permissions.
