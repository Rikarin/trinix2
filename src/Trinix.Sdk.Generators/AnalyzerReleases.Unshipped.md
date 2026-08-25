; Roslyn's release-tracking file for the diagnostics in ServiceDiagnostics.cs
; (TRX10xx) and SettingsDiagnostics.cs (TRX20xx).
;
; RS2008 makes this mandatory, and the rule is right: an analyzer that adds or
; removes a diagnostic id without recording it breaks every .editorconfig and
; NoWarn in every consuming project, silently, because a suppression for an id
; that no longer exists is not an error. Nothing here has shipped yet, so it is
; all Unshipped; the first Trinix release moves these lines into
; AnalyzerReleases.Shipped.md under a version heading.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------
TRX1001 | Trinix.Services | Error | A service member must return Task or Task<T>. Doc 01 § The service surface.
TRX1002 | Trinix.Services | Error | A service member must take a CancellationToken last.
TRX1003 | Trinix.Services | Error | A service interface cannot have properties; a getter cannot be a bus call.
TRX1004 | Trinix.Services | Error | No IsPermissionGranted boolean. Do the thing and handle the refusal.
TRX1005 | Trinix.Services | Error | This type has no D-Bus representation.
TRX1006 | Trinix.Services | Error | A [ServiceSignal] subscription has exactly one shape.
TRX1007 | Trinix.Services | Error | A [TrinixService] declaration is missing its bus name or object path.
TRX2001 | Trinix.Settings | Error | A [SettingsSchema] identifier is malformed, or the interface is generic or nested.
TRX2002 | Trinix.Settings | Error | A key with no [Setting] — doc 02's "a key with no schema is a bug", caught.
TRX2003 | Trinix.Settings | Error | This type cannot be a preference. No lists, no blobs, no [Flags], no nullables.
TRX2004 | Trinix.Settings | Error | A setting needs a Default, and it must be a constant of its own type.
TRX2005 | Trinix.Settings | Error | A setting needs a Summary. This is what keeps the store from being a registry.
TRX2006 | Trinix.Settings | Error | A settings schema holds get-only properties and nothing else.
TRX2007 | Trinix.Settings | Error | Two [SettingsSchema] interfaces in one assembly claim the same identifier.
