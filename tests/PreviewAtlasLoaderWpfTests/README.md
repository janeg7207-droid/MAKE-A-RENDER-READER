This executable tests the real Windows .NET Framework 4.8 WPF decoder used by Studio. Building it on Linux checks compilation only; it does not execute WPF or verify runtime behavior.

On Windows with .NET Framework 4.8 installed, run the built executable:

```powershell
.\tests\PreviewAtlasLoaderWpfTests\bin\Release\net48\PreviewAtlasLoaderWpfTests.exe
```

The checks create a two-frame PNG, verify decoded pixels and frozen crops, replace the file at the same path, and verify that old and new images retain their own pixels with no file handle held open.

To reproduce the previous stream-only `IgnoreImageCache` null-key failure separately:

```powershell
.\tests\PreviewAtlasLoaderWpfTests\bin\Release\net48\PreviewAtlasLoaderWpfTests.exe --runtime-expected-error
```

The reproduction passes only when the old code throws `ArgumentNullException` with parameter `key`. It intentionally does not call the fixed loader.
