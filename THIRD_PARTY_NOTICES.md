# Source provenance

The core retargeting formats, skeleton/mapping/cleanup/export implementation, editor widgets, settings and verification patterns were copied and adapted from the user's local `humanoid-retargeter` at commit `69ccfcd76f6aecca443f055be912ac3831a64003`. Namespaces, registrations, resource paths and package identifiers were changed to avoid collisions. The original repository was not modified.

The editor uses exposed s&box APIs and the native Group, Button, Theme, SegmentedControl and VideoWidget conventions inspected in Facepunch/sbox-public at `95eb0e4c1409d684de8bec636732c0548b7733fe`. It does not package engine-internal assemblies or pretend internal APIs are public.

The C# QR generator in `Editor/HumanoidMocap/PhoneQr` comes from [manuelbl/QrCodeGenerator](https://github.com/manuelbl/QrCodeGenerator), v2.0.7, commit `f414417201fa7c639463730fe41099528f8026cb`. Original copyright headers and the adjacent MIT license are retained; only the namespace is adapted.

The experimental hand model is downloaded separately from Google's official MediaPipe model storage. Its exact URL, SHA-256 and byte count are recorded by `models`. It is not ACE-Ego-Hand, and the managed interpreter is not an official MediaPipe implementation.

ACE-Ego-Hand, GVHMR and other research checkouts, MANO data, footage and checkpoints are local development inputs, not redistributed package assets. Those inputs and development records stay outside this repository.

Windows Media Foundation API signatures and GUIDs follow Windows SDK 10.0.26100.0. The source-reader implementation calls the OS directly; see [Microsoft's Source Reader documentation](https://learn.microsoft.com/en-us/windows/win32/medfound/processing-media-data-with-the-source-reader).
