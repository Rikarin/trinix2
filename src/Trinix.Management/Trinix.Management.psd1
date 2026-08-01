@{
    # Trinix.Management — the distribution's administration surface.
    #
    # Cmdlets rather than scripts, and C# rather than PowerShell, because this
    # is the module every later phase extends: Phase 7 adds the update and
    # package cmdlets, Phase 8 the networking ones. Starting it as a binary
    # module means that growth never requires a rewrite.

    RootModule        = 'Trinix.Management.dll'
    ModuleVersion     = '0.3.0'
    GUID              = 'b7f2c4a1-3d58-4e6b-9a07-51c8d2f4e930'
    Author            = 'Trinix'
    CompanyName       = 'Trinix'
    Copyright         = 'Trinix'
    Description       = 'System administration cmdlets for Trinix: system identity, services, and networking.'

    PowerShellVersion = '7.4'
    CompatiblePSEditions = @('Core')

    # Explicit rather than wildcards: an exported surface that changes because
    # someone added a class is how a module acquires accidental public API.
    CmdletsToExport   = @(
        'Get-TrinixSystem'
        'Test-TrinixSystem'
        'Get-TrinixService'
        'Restart-TrinixService'
        'Get-TrinixNetworkInterface'
    )
    FunctionsToExport = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags       = @('Trinix', 'Administration')
            ProjectUri = 'https://github.com/rikarin/trinix'
        }
    }
}
