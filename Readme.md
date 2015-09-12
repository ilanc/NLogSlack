# Purpose
Create logging wrapper in a separate library (Lib.Core) which contains all NLog code / config.

App code can then use the wrapper without any dependency on NLog

### See also
https://github.com/NLog/NLog/issues/904

### How to build and run

You need Visual Studio 2013
* Open NLogSlack.sln
* Build [Release]
* Copy NLog .dlls into build directory manually
	* copy Lib.Core\bin\Release
	* to NLogSlack\bin\Release
* F5 to run
* Look for log files in:
	* NLogSlack\bin\Release\logs\yyyy-mm-dd.log