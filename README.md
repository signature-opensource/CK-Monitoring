# CK-Monitoring

[![Build status](https://ci.appveyor.com/api/projects/status/pxo8hsxuhqw3ebqa?svg=true)](https://ci.appveyor.com/project/Signature-OpenSource/ck-monitoring)
[![Licence](https://img.shields.io/github/license/signature-opensource/CK-Monitoring.svg)](LICENSE)

This repository contains the log [sink](https://en.wikipedia.org/wiki/Sink_(computing)) for the [ActivityMonitor](https://github.com/signature-opensource/CK-ActivityMonitor/blob/stable/CK.ActivityMonitor/README.md)s and its .NET Generic Host integration.

| Package | Description | Latest stable |
|---------|-------------|---------------|
| [CK.Monitoring](CK.Monitoring/README.md) | The `GrandOutput` sink itself, its handlers (Console, TextFile, BinaryFile) and the `.ckmon` persistence layer. | [![nuget](https://img.shields.io/nuget/v/CK.Monitoring.svg?label=CK.Monitoring)](https://www.nuget.org/packages/CK.Monitoring/) |
| [CK.Monitoring.Hosting](CK.Monitoring.Hosting/README.md) | Wires the `GrandOutput.Default` into the .NET Generic Host from the "CK-Monitoring" configuration section. | [![nuget](https://img.shields.io/nuget/v/CK.Monitoring.Hosting.svg?label=CK.Monitoring.Hosting)](https://www.nuget.org/packages/CK.Monitoring.Hosting/) |
