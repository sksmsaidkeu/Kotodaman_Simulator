KotodamanWordFinder v1.25.0 Publish Fix v2

Cause:
The previous sync_release_data.ps1 file contained Korean UTF-8 text without a BOM.
Windows PowerShell 5.1 read it using the wrong encoding, which broke quotes and braces.

This patch replaces all publish scripts with ASCII-only versions.

Apply:
1. Close the program and the failed publish window.
2. Extract this ZIP into the v1.25.0 project root.
3. Overwrite all three files:
   - publish_release.bat
   - publish_release.ps1
   - sync_release_data.ps1
4. Run publish_release.bat again.

The project root is the folder containing KotodamanWordFinder.csproj.

Output:
Publish\KotodamanWordFinder_v1.25.0_win-x64
Publish\KotodamanWordFinder_v1.25.0_win-x64_portable.zip
