# Third-party notices

RamPilot is licensed under the MIT License (see [LICENSE](LICENSE)). It uses the third-party components below.
This list was generated from the resolved NuGet packages of every project in `RamPilot.sln`
(`artifacts/obj/*/project.assets.json`) and checked against each package's license metadata.

## Redistributed in the release package

### .NET runtime (compiled into `RamPilot.exe`)
`RamPilot.exe` is a Native AOT binary: parts of the .NET runtime and base class libraries are compiled into it by
the ILCompiler (`Microsoft.DotNet.ILCompiler` / `runtime.win-x64.Microsoft.DotNet.ILCompiler` 10.0.x).

- License: MIT — Copyright (c) .NET Foundation and Contributors.
  <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- The .NET runtime carries its own third-party notices:
  <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Microsoft Edge WebView2 SDK (`Microsoft.Web.WebView2` 1.0.4314.35)
Used by the control panel. Redistributed files: `Microsoft.Web.WebView2.Core.dll`,
`Microsoft.Web.WebView2.WinForms.dll`, `runtimes/*/native/WebView2Loader.dll`.
The WebView2 *Runtime* itself is not redistributed; it is part of Windows 11.

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

The control panel is framework-dependent: the .NET Desktop Runtime it needs is installed separately and is not
redistributed.

## Build and test only (not redistributed)

| Package | Version | License |
|---|---|---|
| Microsoft.DotNet.ILCompiler, runtime.win-x64.Microsoft.DotNet.ILCompiler | 10.0.15 | MIT |
| Microsoft.NET.ILLink.Tasks | 10.0.15 | MIT |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.TestPlatform.ObjectModel, Microsoft.TestPlatform.TestHost | 17.14.1 | MIT |
| Microsoft.CodeCoverage | 17.14.1 | MIT |
| Newtonsoft.Json (via TestHost) | 13.0.3 | MIT |
| xunit, xunit.core, xunit.assert, xunit.extensibility.core, xunit.extensibility.execution | 2.9.3 | Apache-2.0 |
| xunit.abstractions | 2.0.3 | Apache-2.0 |
| xunit.analyzers | 1.18.0 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |

License texts: MIT <https://licenses.nuget.org/MIT>, Apache-2.0 <https://licenses.nuget.org/Apache-2.0>.

## Tools
The tests in `tests/live/` and the probes in `experiments/` use only the .NET SDK and Windows PowerShell.
