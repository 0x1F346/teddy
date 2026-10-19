# Test fixtures

Self-generated sine-wave MP3s (no third-party content). NLayer needs real MP3
frames, and nothing in this repository can encode MP3, so these are checked in.

Created with:

```
ffmpeg -f lavfi -i "sine=frequency=440:duration=1" -ac 1 -ar 22050 -b:a 48k sine440-mono-22k.mp3
ffmpeg -f lavfi -i "sine=frequency=440:duration=1" -ac 2 -ar 44100 -b:a 96k sine440-stereo-44k.mp3
```

Ogg/Opus inputs are not checked in; the tests generate them with the vendored
Concentus encoder.
