In GitHub Actions under All workflows.



There must be one workflow for both windows and macOS.



The artifact created under the workflow must be in format of zip.



Naming Format will be:-

\[Software\_Name]\_\[Version\_Number]



After unzipping the downloaded file.

The internal folder structure must be in format.



* portable

&#x09;- \[Software\_Name]\_\[Version\_Number].exe

* windows-x64

&#x09;- \[Software\_Name]\_\[Version\_Number].exe

* macOS

&#x09;- \[Software\_Name]\_\[Version\_Number].dmg

* Android

&#x09;- \[Software\_Name]\_\[Version\_Number].apk





