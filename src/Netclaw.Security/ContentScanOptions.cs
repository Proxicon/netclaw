// -----------------------------------------------------------------------
// <copyright file="ContentScanOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Narrow, caller-proven scan facts that affect only scanner mechanics. They
/// never replace MIME verification or the content-policy allow list.
/// </summary>
public readonly record struct ContentScanOptions(bool AllowExtensionlessProvisionalImage = false);
