KotodamanWordFinder v1.25.0 -> v1.25.1 update foundation patch

Apply:
1. Close the program.
2. Extract this ZIP into the v1.25.0 project root.
3. Overwrite all files.
4. Run clean_run.bat and verify the program starts normally.

This patch does not change the main UI layout.
It adds:
- Separate program/data version tracking
- Character/image data update ZIP builder
- Automatic backup and 3-way merge when applying data updates
- Approved data baseline workflow
- Automatic bundled data patch chain for future full releases
- Publish validation that blocks unapproved Data changes
- Publish cleanup that keeps only the portable ZIP and removes bin/obj

Developer commands:
- create_data_update.bat
- accept_data_baseline.bat
- clean_workspace.bat
- publish_release.bat

End-user data update command in published builds:
- Drag a data update ZIP onto apply_data_update.bat

Read the Korean file: 배포_및_업데이트_방법.txt
