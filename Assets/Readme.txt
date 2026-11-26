Put your ffmpeg/ffprobe binaries here:

  Windows:      Assets/ffmpeg.exe   Assets/ffprobe.exe
  Linux/macOS:  Assets/ffmpeg       Assets/ffprobe   (no extension)

Get them from https://ffmpeg.org/download.html (a static build is easiest —
no separate install needed, just the two executables).

The app looks here automatically before falling back to PATH, so once these
are in place you don't need ffmpeg/ffprobe installed system-wide. This
folder is copied next to the built/published executable automatically
(see the <ItemGroup> in MkvCensor.csproj), so ship it alongside the .exe/
binary when you distribute the app.

On Linux/macOS, the app fixes the executable bit on these files at runtime
if it's missing (e.g. after unzipping), so you shouldn't need to manually
chmod +x them — but it doesn't hurt to do so yourself if you hit a
"permission denied" error.
