# Overlay regression tests

This is a manual Windows desktop test. Run it from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/overlay-tests/run-tests.ps1
```

`run-tests.ps1` copies the product source into `product-source/`, builds an isolated test executable, and runs the real WPF overlay methods against a disposable HWND. It is not part of the CI workflow because it needs an interactive desktop.

Do not open `OverlayRegressionTests.csproj` directly before the first test run: it references the generated `product-source/LiveSub/LiveSub.csproj`.
