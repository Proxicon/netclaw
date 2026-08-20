# Microsoft Teams app package

This directory contains the source for the Netclaw Microsoft Teams app package.

The package grants only personal and team bot scopes. It does not request Graph,
group chat, meeting, tab, calling, video, or file capabilities.

## Build the package

Use public HTTPS pages for your privacy policy and terms of use.

```powershell
$BuildPackage = @{
    AppId = '00000000-0000-0000-0000-000000000000'
    DeveloperName = 'Example Operator'
    PrivacyUrl = 'https://example.com/privacy'
    TermsOfUseUrl = 'https://example.com/terms'
    OutputPath = './artifacts/netclaw-teams.zip'
    Version = '1.0.0'
    Verbose = $true
}

./build-package.ps1 @BuildPackage
```

The developer name has a 32-character limit. Increase the semantic version for
each package update.

The ZIP file contains these three files at its root:

- `manifest.json`
- `color.png`
- `outline.png`

Do not commit an operator-specific package. The generated manifest contains
your app registration ID and your policy URLs.

See [the Microsoft Teams runbook](../../docs/integrations/microsoft-teams-channel.md)
for registration, deployment, health, rotation, and rollback procedures.
