@echo off
REM ============================================================
REM  Create a SELF-SIGNED code-signing certificate for TESTING
REM  the signing pipeline only. It does NOT remove SmartScreen
REM  warnings (self-signed = untrusted). For release, buy a real
REM  Authenticode certificate (see SIGNING.md).
REM ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=PhoneLink Pro TEST - ENG. Mohammed Sattar' -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -KeyExportPolicy Exportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(1); Write-Host ('Test cert created. Thumbprint: ' + $c.Thumbprint); Write-Host 'Now run sign.bat (leave CERT empty to auto-pick it).'"
pause
