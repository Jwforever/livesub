# Installer

`LiveSub.iss` builds the per-user Windows installer with Inno Setup 6.

`ChineseSimplified.isl` is the official Simplified Chinese language file from the Inno Setup source tree:

https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl

It is included so CI does not depend on an optional compiler language pack. Both installer files are UTF-8 with BOM; keep that encoding when editing.
